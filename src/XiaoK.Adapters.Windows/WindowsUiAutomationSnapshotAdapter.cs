using System.Diagnostics;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using XiaoK.Core;

namespace XiaoK.Adapters.Windows;

/// <summary>Runs bounded UIA operations in a disposable helper process.</summary>
public sealed class WindowsUiAutomationSnapshotAdapter : IReadOnlyUiAutomationSnapshotReader, IBackgroundUiAutomationTextSetter
{
    private const int MaximumRequestBytes = 16 * 1024;
    private const int MaximumResponseBytes = 32 * 1024;
    private static readonly TimeSpan WorkerTimeout = TimeSpan.FromSeconds(5);
    private const int ControlWaitMilliseconds = 2_500;
    private const int MaximumScopedDepth = 5;
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
        if (_desktop.IsApplicationRegistered(applicationId) && !_desktop.IsUiAutomationSnapshotTargetAllowed(applicationId))
            return new(false, "消息客户端不允许使用会返回控件名称的通用 UIA 快照；没有访问窗口。", "UIA_MESSAGING_APP_BLOCKED");
        var (response, failure) = await RunWorkerAsync(applicationId, new WorkerCommand("snapshot", null, 0), cancellationToken)
            .ConfigureAwait(false);
        if (failure is not null) return failure;
        if (response is null || !response.Success)
            return new(false, "无法读取已选应用的界面快照；没有执行控件动作。", response?.ErrorCode ?? "UIA_READ_FAILED");

        var display = FormatSnapshot(response.Items ?? [], response.Truncated);
        return new(true, "已读取目标应用的有限 UIA 快照；没有点击、输入、读取剪贴板或切换窗口。", Data: display);
    }

    public async Task<ToolResult> ReadScopedSnapshotAsync(string applicationId, string automationId,
        string rootControlType, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_desktop.IsApplicationRegistered(applicationId) && !_desktop.IsUiAutomationSnapshotTargetAllowed(applicationId))
            return new(false, "消息客户端不允许使用通用 UIA 快照；没有访问窗口。", "UIA_MESSAGING_APP_BLOCKED");
        if (string.IsNullOrWhiteSpace(automationId) || automationId.Length > 96
            || automationId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))
            || !ApplicationUiAutomationPolicy.IsAllowedScopedRootControlType(rootControlType))
            return new(false, "根控件标识或容器类型无效；没有访问应用界面。", "UIA_INVALID_SCOPED_SELECTOR");

        var (response, failure) = await RunWorkerAsync(applicationId,
            new WorkerCommand("scoped_snapshot", automationId, 0, RootControlType: rootControlType), cancellationToken)
            .ConfigureAwait(false);
        if (failure is not null) return failure;
        if (response is null || !response.Success)
            return new(false, "无法读取指定控件区域；没有读取控件名称、文本或整窗快照。",
                response?.ErrorCode ?? "UIA_READ_FAILED");
        if (response.Truncated || response.Items.Any(item => item.AutomationId.Length != 0 || item.Name.Length != 0
            || item.Depth is < 0 or > MaximumScopedDepth))
            return new(false, "控件区域超出固定范围或返回了非元数据字段；没有显示部分结果。", "UIA_SCOPED_RESPONSE_INVALID");

        return new(true, "已读取指定容器的有界控件类型和状态；未读取控件名称、文本或子控件标识，也没有点击、输入或切换窗口。",
            Data: FormatScopedSnapshot(response.Items));
    }

    public async Task<ToolResult> WaitForEnabledControlAsync(string applicationId, string automationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(automationId) || automationId.Length > 96
            || automationId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-')))
            return new(false, "AutomationId 格式无效；没有访问应用界面。", "UIA_INVALID_SELECTOR");

        var (response, failure) = await RunWorkerAsync(applicationId,
            new WorkerCommand("wait_enabled", automationId, ControlWaitMilliseconds), cancellationToken)
            .ConfigureAwait(false);
        if (failure is not null) return failure;
        if (response is null || !response.Success)
            return new(false, "无法核验指定控件状态；没有执行控件动作。", response?.ErrorCode ?? "UIA_READ_FAILED");
        if (response.ConditionMet != true)
            return new(false, "等待结束时指定控件仍未唯一出现并启用；没有执行控件动作。", "UIA_CONDITION_NOT_MET");
        return new(true, "已核验指定 AutomationId 对应的唯一控件当前已启用；没有点击、输入、读取剪贴板或切换窗口。",
            Data: "控件可用");
    }

    public async Task<ToolResult> SetTextAsync(string applicationId, string automationId, string value,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_desktop.IsUiAutomationTextTargetAllowed(applicationId))
            return new(false, "消息客户端、浏览器、系统文件管理器及金融/凭证敏感应用不允许使用通用界面填写工具；没有修改控件。", "UIA_PROTECTED_APP_BLOCKED");
        if (string.IsNullOrWhiteSpace(automationId) || automationId.Length > 96
            || automationId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))
            || ApplicationUiAutomationPolicy.IsSensitiveTextEntryField(automationId)
            || !ApplicationUiAutomationPolicy.IsAllowedText(value))
            return new(false, "控件标识或文本不符合固定限制；没有访问应用界面。", "UIA_INVALID_TEXT_REQUEST");

        var (response, failure) = await RunWorkerAsync(applicationId,
            new WorkerCommand("set_text", automationId, 0, value), cancellationToken).ConfigureAwait(false);
        if (failure is not null) return failure;
        if (response is null || !response.Success)
        {
            var error = response?.ErrorCode ?? "UIA_TEXT_SET_FAILED";
            var uncertain = response?.OutcomeUncertain == true
                || error is "UIA_VALUE_VERIFY_FAILED" or "UIA_FOREGROUND_CHANGED_AFTER_SET";
            return new(false, uncertain
                ? "文本框写入后无法确认最终状态；请先人工检查目标应用，不要自动重试。"
                : error == "UIA_TARGET_IN_USE"
                    ? "目标应用正处于前台；为避免覆盖你正在编辑的内容，没有修改文本框。"
                    : error == "UIA_FOREGROUND_CHANGED_BEFORE_SET"
                        ? "写入前检测到前台窗口发生变化；没有修改文本框。"
                        : "没有找到唯一、启用且可写的普通文本框；没有修改应用界面。",
                error, FinalState: uncertain ? TaskLifecycleState.OutcomeUncertain : null);
        }
        return new(true, "已向唯一目标普通文本框写入并核对文本；没有点击、模拟按键、读取剪贴板或切换窗口。",
            Data: "目标文本框已更新");
    }

    private async Task<(WorkerResponse? Response, ToolResult? Failure)> RunWorkerAsync(string applicationId,
        WorkerCommand command, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            return (null, new(false, "当前平台不支持 Windows UI Automation。", "UIA_UNAVAILABLE"));
        if (command.Mode == "set_text" && !_desktop.IsUiAutomationTextTargetAllowed(applicationId))
            return (null, new(false, "目标应用不允许使用通用后台文本填写工具；没有修改控件。", "UIA_PROTECTED_APP_BLOCKED"));
        if (!_desktop.TryResolveUiAutomationTarget(applicationId, out var target, out var targetError))
            return (null, targetError switch
            {
                "APP_NOT_ALLOWLISTED" => new(false, "目标应用未登记在本机允许列表；没有访问窗口。", targetError),
                "APP_WINDOW_NOT_FOUND" => new(false, "没有找到该应用的可见窗口；没有切换窗口。", targetError),
                "APP_WINDOW_AMBIGUOUS" => new(false, "该应用有多个可见窗口，无法安全确定目标；没有访问窗口。", targetError),
                _ => new(false, "无法核验该应用窗口的进程身份；没有访问窗口。", targetError)
            });

        if (command.Mode == "set_text" && ApplicationUiAutomationPolicy.IsTextEntryTargetBlocked(
            applicationId, target!.DisplayName, target.ExecutablePath))
            return (null, new(false, "消息客户端、浏览器、系统文件管理器及金融/凭证敏感应用不允许使用通用界面填写工具；没有修改控件。", "UIA_PROTECTED_APP_BLOCKED"));

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            return (null, new(false, "只读 UIA 工作进程不可用；没有访问窗口。", "UIA_WORKER_UNAVAILABLE"));

        var entryAssemblyPath = Assembly.GetEntryAssembly()?.Location;
        var needsEntryAssemblyArgument = string.Equals(Path.GetFileNameWithoutExtension(executablePath),
            "dotnet", StringComparison.OrdinalIgnoreCase);
        if (needsEntryAssemblyArgument && (string.IsNullOrWhiteSpace(entryAssemblyPath) || !File.Exists(entryAssemblyPath)))
            return (null, new(false, "只读 UIA 工作进程不可用；没有访问窗口。", "UIA_WORKER_UNAVAILABLE"));

        var resolvedTarget = target!;
        var request = JsonSerializer.SerializeToUtf8Bytes(new WorkerRequest(
            resolvedTarget.WindowHandle, resolvedTarget.ProcessId, resolvedTarget.ExecutablePath,
            command.Mode, command.AutomationId, command.WaitMilliseconds, command.Value,
            ApplicationId: applicationId, DisplayName: resolvedTarget.DisplayName,
            RootControlType: command.RootControlType));
        if (request.Length > MaximumRequestBytes)
            return (null, new(false, "目标信息超出固定上限；没有访问窗口。", "UIA_REQUEST_TOO_LARGE"));

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
        startInfo.ArgumentList.Add("--uia-worker");

        UiAutomationWorkerProcess? worker = null;
        Process? process = null;
        Task<byte[]>? outputTask = null;
        Task? errorTask = null;
        var mutationMayHaveStarted = false;
        try
        {
            worker = _workerProcessStarter.Start(startInfo);
            if (worker is null)
                return (null, new(false, "只读 UIA 工作进程启动失败；没有访问窗口。", "UIA_WORKER_UNAVAILABLE"));
            process = worker.Process;
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception) { }

            using var timeout = new CancellationTokenSource(WorkerTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            outputTask = ReadBoundedOutputAsync(process.StandardOutput.BaseStream, MaximumResponseBytes, linked.Token);
            errorTask = DrainErrorAsync(process.StandardError.BaseStream);
            mutationMayHaveStarted = command.Mode == "set_text";
            await process.StandardInput.BaseStream.WriteAsync(request, linked.Token).ConfigureAwait(false);
            await process.StandardInput.BaseStream.FlushAsync(linked.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            await errorTask.ConfigureAwait(false);
            var responseBytes = await outputTask.WaitAsync(linked.Token).ConfigureAwait(false);
            if (!TryParseResponse(responseBytes, out var response)
                || process.ExitCode != (response.Success ? 0 : 2))
                return (null, new(false, "UIA 查询未能可靠完成；没有执行控件动作。", "UIA_WORKER_FAILED"));
            return (response, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (process is not null) TryKill(process);
            await ObserveWorkerAsync(outputTask, errorTask).ConfigureAwait(false);
            if (mutationMayHaveStarted)
                return (new WorkerResponse(false, "UIA_VALUE_VERIFY_FAILED", [], OutcomeUncertain: true), null);
            throw;
        }
        catch (OperationCanceledException)
        {
            if (process is not null) TryKill(process);
            await ObserveWorkerAsync(outputTask, errorTask).ConfigureAwait(false);
            if (mutationMayHaveStarted)
                return (new WorkerResponse(false, "UIA_VALUE_VERIFY_FAILED", [], OutcomeUncertain: true), null);
            return (null, new(false, "UIA 查询超过 5 秒，已终止隔离工作进程；没有执行控件动作。", "UIA_WORKER_TIMEOUT"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException
            or ArgumentException or InvalidOperationException or InvalidDataException or Win32Exception
            or ObjectDisposedException or JsonException or OverflowException)
        {
            if (process is not null) TryKill(process);
            await ObserveWorkerAsync(outputTask, errorTask).ConfigureAwait(false);
            if (mutationMayHaveStarted)
                return (new WorkerResponse(false, "UIA_VALUE_VERIFY_FAILED", [], OutcomeUncertain: true), null);
            return (null, new(false, "UIA 查询失败或返回内容超出限制；没有执行控件动作。", "UIA_WORKER_FAILED"));
        }
        finally { worker?.Dispose(); }
    }

    private sealed record WorkerCommand(string Mode, string? AutomationId, int WaitMilliseconds, string? Value = null,
        string? RootControlType = null);

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
                    || item.Name is null || item.Name.Length > 160 || item.Depth is < 0 or > MaximumScopedDepth)) return false;
            response = parsed;
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static string FormatSnapshot(IReadOnlyList<UiAutomationSnapshotItem> items, bool truncated)
    {
        if (items.Count == 0) return truncated ? "已到达只读遍历上限，未发现受支持的语义控件。" : "未发现受支持的语义控件。";
        var builder = new StringBuilder(truncated
            ? "只读控件清单（已到遍历上限，结果可能不完整；不读取输入框当前内容，输入框只列控件类型和AutomationId）："
            : "只读控件清单（不读取输入框当前内容，输入框只列控件类型和AutomationId）：");
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

    private static string FormatScopedSnapshot(IReadOnlyList<UiAutomationSnapshotItem> items)
    {
        if (items.Count == 0) return "指定容器中没有可报告的控件状态。";
        var builder = new StringBuilder("指定容器的有限控件结构（仅控件类型、深度和状态；不含名称、文本或子控件标识）：");
        foreach (var item in items)
        {
            builder.AppendLine();
            builder.Append(' ', item.Depth * 2).Append("- ").Append(item.ControlType);
            builder.Append(" | ").Append(item.IsEnabled ? "可用" : "禁用");
            if (item.IsOffscreen) builder.Append(" | 当前不可见");
        }
        return builder.ToString();
    }

    public sealed record WorkerRequest(long WindowHandle, int ProcessId, string ExecutablePath,
        string Mode = "snapshot", string? AutomationId = null, int WaitMilliseconds = 0, string? Value = null,
        string? ApplicationId = null, string? DisplayName = null, string? RootControlType = null);
    public sealed record WorkerResponse(bool Success, string? ErrorCode, UiAutomationSnapshotItem[] Items,
        bool Truncated = false, bool? ConditionMet = null, bool? OutcomeUncertain = null);
}

internal interface IUiAutomationWorkerProcessStarter
{
    UiAutomationWorkerProcess? Start(ProcessStartInfo startInfo);
}

internal sealed class SystemUiAutomationWorkerProcessStarter : IUiAutomationWorkerProcessStarter
{
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitProcessMemory = 0x00000100;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const long MaximumWorkerMemoryBytes = 256L * 1024 * 1024;

    public UiAutomationWorkerProcess? Start(ProcessStartInfo startInfo)
    {
        var job = CreateWorkerJob();
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
            {
                process.Dispose();
                job.Dispose();
                return null;
            }
            if (!AssignProcessToJobObject(job, process.Handle))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return new UiAutomationWorkerProcess(process, job);
        }
        catch
        {
            TryKill(process);
            process.Dispose();
            job.Dispose();
            throw;
        }
    }

    private static SafeUiAutomationWorkerJobHandle CreateWorkerJob()
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var limits = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose | JobObjectLimitProcessMemory
            },
            ProcessMemoryLimit = new UIntPtr(MaximumWorkerMemoryBytes)
        };
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, ref limits,
                (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            var error = Marshal.GetLastWin32Error();
            job.Dispose();
            throw new Win32Exception(error);
        }
        return job;
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception
            or NotSupportedException or ObjectDisposedException) { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    private sealed class SafeUiAutomationWorkerJobHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateJobObjectW")]
    private static extern SafeUiAutomationWorkerJobHandle CreateJobObjectW(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeUiAutomationWorkerJobHandle job, int informationClass,
        ref JobObjectExtendedLimitInformation information, uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeUiAutomationWorkerJobHandle job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

internal sealed class UiAutomationWorkerProcess(Process process, IDisposable job) : IDisposable
{
    public Process Process { get; } = process;

    public void Dispose()
    {
        try { Process.Dispose(); }
        finally { job.Dispose(); }
    }
}

public sealed record UiAutomationSnapshotItem(string AutomationId, string ControlType, string Name,
    bool IsEnabled, bool IsOffscreen, int Depth = 0);
