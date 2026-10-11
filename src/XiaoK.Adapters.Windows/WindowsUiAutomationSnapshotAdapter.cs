using System.Diagnostics;
using System.ComponentModel;
using System.Reflection;
using System.Security;
using System.Text;
using System.Text.Json;
using XiaoK.Core;

namespace XiaoK.Adapters.Windows;

/// <summary>Runs bounded, read-only UIA inspection in a disposable helper process.</summary>
public sealed class WindowsUiAutomationSnapshotAdapter : IReadOnlyUiAutomationSnapshotReader
{
    private const int MaximumRequestBytes = 8 * 1024;
    private const int MaximumResponseBytes = 32 * 1024;
    private static readonly TimeSpan WorkerTimeout = TimeSpan.FromSeconds(5);
    private readonly WindowsDesktopTools _desktop;
    private readonly IUiAutomationWorkerProcessStarter _workerProcessStarter;

    public WindowsUiAutomationSnapshotAdapter(WindowsDesktopTools desktop)
        : this(desktop, new SystemUiAutomationWorkerProcessStarter()) { }

    internal WindowsUiAutomationSnapshotAdapter(WindowsDesktopTools desktop,
        IUiAutomationWorkerProcessStarter workerProcessStarter)
    {
        _desktop = desktop;
        _workerProcessStarter = workerProcessStarter;
    }

    public async Task<ToolResult> ReadSnapshotAsync(string applicationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return new(false, "当前平台不支持 Windows UI Automation。", "UIA_UNAVAILABLE");
        if (!_desktop.TryResolveUiAutomationTarget(applicationId, out var target, out var targetError))
            return targetError switch
            {
                "APP_NOT_ALLOWLISTED" => new(false, "目标应用未登记在本机允许列表；没有访问窗口。", targetError),
                "APP_WINDOW_NOT_FOUND" => new(false, "没有找到该应用的可见窗口；没有切换窗口。", targetError),
                "APP_WINDOW_AMBIGUOUS" => new(false, "该应用有多个可见窗口，无法安全确定目标；没有访问窗口。", targetError),
                _ => new(false, "无法核验该应用窗口的进程身份；没有访问窗口。", targetError)
            };

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            return new(false, "只读 UIA 工作进程不可用；没有访问窗口。", "UIA_WORKER_UNAVAILABLE");

        var entryAssemblyPath = Assembly.GetEntryAssembly()?.Location;
        var needsEntryAssemblyArgument = string.Equals(Path.GetFileNameWithoutExtension(executablePath),
            "dotnet", StringComparison.OrdinalIgnoreCase);
        if (needsEntryAssemblyArgument && (string.IsNullOrWhiteSpace(entryAssemblyPath) || !File.Exists(entryAssemblyPath)))
            return new(false, "只读 UIA 工作进程不可用；没有访问窗口。", "UIA_WORKER_UNAVAILABLE");

        var request = JsonSerializer.SerializeToUtf8Bytes(new WorkerRequest(
            target!.WindowHandle, target.ProcessId, target.ExecutablePath));
        if (request.Length > MaximumRequestBytes)
            return new(false, "目标信息超出固定上限；没有访问窗口。", "UIA_REQUEST_TOO_LARGE");

        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (needsEntryAssemblyArgument) startInfo.ArgumentList.Add(entryAssemblyPath!);
        startInfo.ArgumentList.Add("--uia-readonly-worker");

        Process? process = null;
        Task<byte[]>? outputTask = null;
        Task? errorTask = null;
        try
        {
            process = _workerProcessStarter.Start(startInfo);
            if (process is null) return new(false, "只读 UIA 工作进程启动失败；没有访问窗口。", "UIA_WORKER_UNAVAILABLE");
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception) { }

            using var timeout = new CancellationTokenSource(WorkerTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            outputTask = ReadBoundedOutputAsync(process.StandardOutput.BaseStream, MaximumResponseBytes, linked.Token);
            errorTask = DrainErrorAsync(process.StandardError.BaseStream);
            await process.StandardInput.BaseStream.WriteAsync(request, linked.Token).ConfigureAwait(false);
            await process.StandardInput.BaseStream.FlushAsync(linked.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            await errorTask.ConfigureAwait(false);
            var responseBytes = await outputTask.WaitAsync(linked.Token).ConfigureAwait(false);
            if (process.ExitCode != 0 || !TryParseResponse(responseBytes, out var response))
                return new(false, "UIA 查询未能可靠完成；没有执行控件动作。", "UIA_WORKER_FAILED");
            if (!response.Success)
                return new(false, "无法读取已选应用的界面快照；没有执行控件动作。", response.ErrorCode ?? "UIA_READ_FAILED");

            var display = FormatSnapshot(response.Items ?? [], response.Truncated);
            return new(true, "已读取目标应用的有限 UIA 快照；没有点击、输入、读取剪贴板或切换窗口。", Data: display);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (process is not null) TryKill(process);
            await ObserveWorkerAsync(outputTask, errorTask).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            if (process is not null) TryKill(process);
            await ObserveWorkerAsync(outputTask, errorTask).ConfigureAwait(false);
            return new(false, "UIA 查询超过 5 秒，已终止隔离工作进程；没有执行控件动作。", "UIA_WORKER_TIMEOUT");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException
            or ArgumentException or InvalidOperationException or InvalidDataException or Win32Exception
            or ObjectDisposedException or JsonException or OverflowException)
        {
            if (process is not null) TryKill(process);
            await ObserveWorkerAsync(outputTask, errorTask).ConfigureAwait(false);
            return new(false, "UIA 查询失败或返回内容超出限制；没有执行控件动作。", "UIA_WORKER_FAILED");
        }
        finally { process?.Dispose(); }
    }

    private static async Task<byte[]> ReadBoundedOutputAsync(Stream stream, int maximumBytes, CancellationToken token)
    {
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) return output.ToArray();
            if (output.Length + read > maximumBytes) throw new InvalidDataException("UIA output exceeded its fixed limit.");
            output.Write(buffer, 0, read);
        }
    }

    private static async Task DrainErrorAsync(Stream stream)
    {
        var buffer = new byte[1024];
        while (await stream.ReadAsync(buffer).ConfigureAwait(false) is > 0) { }
    }

    private static async Task ObserveWorkerAsync(Task<byte[]>? outputTask, Task? errorTask)
    {
        try { if (outputTask is not null) await outputTask.ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException or InvalidDataException) { }
        try { if (errorTask is not null) await errorTask.ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException or ObjectDisposedException) { }
    }

    private static bool TryParseResponse(byte[] bytes, out WorkerResponse response)
    {
        response = new(false, "UIA_INVALID_RESPONSE", []);
        try
        {
            var parsed = JsonSerializer.Deserialize<WorkerResponse>(bytes);
            if (parsed is null || parsed.Items is null || parsed.Items.Length > 64
                || parsed.Items.Any(item => item is null || item.AutomationId is null || item.AutomationId.Length > 96
                    || item.ControlType is null || item.ControlType.Length > 48
                    || item.Name is null || item.Name.Length > 160)) return false;
            response = parsed;
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static string FormatSnapshot(IReadOnlyList<UiAutomationSnapshotItem> items, bool truncated)
    {
        if (items.Count == 0) return truncated ? "已到达只读遍历上限，未发现受支持的语义控件。" : "未发现受支持的语义控件。";
        var builder = new StringBuilder(truncated
            ? "只读控件清单（已到遍历上限，结果可能不完整；不含文本框、编辑器、文档或列表内容）："
            : "只读控件清单（不含文本框、编辑器、文档或列表内容）：");
        foreach (var item in items)
        {
            builder.AppendLine();
            builder.Append("- ").Append(item.ControlType);
            if (item.Name.Length > 0) builder.Append(" | ").Append(item.Name);
            if (item.AutomationId.Length > 0) builder.Append(" | AutomationId=").Append(item.AutomationId);
            builder.Append(" | ").Append(item.IsEnabled ? "可用" : "禁用");
            if (item.IsOffscreen) builder.Append(" | 当前不可见");
        }
        return builder.ToString();
    }

    public sealed record WorkerRequest(long WindowHandle, int ProcessId, string ExecutablePath);
    public sealed record WorkerResponse(bool Success, string? ErrorCode, UiAutomationSnapshotItem[] Items,
        bool Truncated = false);
}

internal interface IUiAutomationWorkerProcessStarter
{
    Process? Start(ProcessStartInfo startInfo);
}

internal sealed class SystemUiAutomationWorkerProcessStarter : IUiAutomationWorkerProcessStarter
{
    public Process? Start(ProcessStartInfo startInfo)
    {
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (process.Start()) return process;
        process.Dispose();
        return null;
    }
}

public sealed record UiAutomationSnapshotItem(string AutomationId, string ControlType, string Name,
    bool IsEnabled, bool IsOffscreen);
