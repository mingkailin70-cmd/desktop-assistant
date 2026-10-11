using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using System.Windows.Automation;
using XiaoK.Adapters.Windows;

namespace XiaoK.Host;

/// <summary>Single-request UIA worker. The parent enforces a hard timeout and kills this process on cancellation.</summary>
public static class WindowsUiAutomationWorkerEntryPoint
{
    private const int MaximumRequestBytes = 8 * 1024;
    private const int MaximumElements = 64;
    private const int MaximumVisitedNodes = 256;
    private const int MaximumDepth = 4;
    private static readonly HashSet<ControlType> SafeNamedControlTypes =
    [
        ControlType.Button,
        ControlType.CheckBox,
        ControlType.Hyperlink,
        ControlType.MenuItem,
        ControlType.RadioButton,
        ControlType.TabItem
    ];

    public static async Task<int> RunAsync()
    {
        var response = new WindowsUiAutomationSnapshotAdapter.WorkerResponse(false, "UIA_INVALID_REQUEST", []);
        try
        {
            var requestBytes = await ReadBoundedAsync(Console.OpenStandardInput(), MaximumRequestBytes).ConfigureAwait(false);
            var request = JsonSerializer.Deserialize<WindowsUiAutomationSnapshotAdapter.WorkerRequest>(requestBytes);
            if (request is not null)
                response = request.Mode switch
                {
                    "snapshot" when request.AutomationId is null && request.WaitMilliseconds == 0 => ReadSnapshot(request),
                    "wait_enabled" => WaitForEnabledControl(request),
                    _ => new(false, "UIA_INVALID_REQUEST", [])
                };
        }
        catch (Exception exception) when (exception is IOException or JsonException or ArgumentException
            or InvalidOperationException or UnauthorizedAccessException or SecurityException or Win32Exception
            or COMException or NotSupportedException)
        {
            response = new(false, "UIA_READ_FAILED", []);
        }

        try
        {
            var output = JsonSerializer.SerializeToUtf8Bytes(response);
            var outputStream = Console.OpenStandardOutput();
            await outputStream.WriteAsync(output).ConfigureAwait(false);
            await outputStream.FlushAsync().ConfigureAwait(false);
            return response.Success ? 0 : 2;
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            return 3;
        }
    }

    private static WindowsUiAutomationSnapshotAdapter.WorkerResponse ReadSnapshot(
        WindowsUiAutomationSnapshotAdapter.WorkerRequest request)
    {
        if (!TryResolveRoot(request, out var root, out var error)) return new(false, error, []);
        var handle = new IntPtr(request.WindowHandle);
        var walker = TreeWalker.ControlViewWalker;
        var pending = new Stack<(AutomationElement Element, int Depth)>();
        var items = new List<UiAutomationSnapshotItem>(MaximumElements);
        pending.Push((root, 0));
        var visitedNodes = 0;
        while (pending.Count > 0 && items.Count < MaximumElements && visitedNodes < MaximumVisitedNodes)
        {
            var (element, depth) = pending.Pop();
            visitedNodes++;
            var current = element.Current;
            if (depth > 0 && SafeNamedControlTypes.Contains(current.ControlType) && !current.IsPassword)
            {
                items.Add(new UiAutomationSnapshotItem(
                    Clean(current.AutomationId, 96),
                    Clean(current.ControlType.ProgrammaticName, 48),
                    Clean(current.Name, 160),
                    current.IsEnabled,
                    current.IsOffscreen));
            }

            if (depth >= MaximumDepth) continue;
            var children = new List<AutomationElement>(32);
            var child = walker.GetFirstChild(element);
            while (child is not null && children.Count < MaximumElements)
            {
                children.Add(child);
                child = walker.GetNextSibling(child);
            }
            for (var index = children.Count - 1; index >= 0; index--)
                pending.Push((children[index], depth + 1));
        }

        if (!IsWindow(handle) || !IsWindowVisible(handle)) return new(false, "UIA_TARGET_CHANGED", []);
        return new(true, null, items.ToArray(), pending.Count > 0 || visitedNodes >= MaximumVisitedNodes);
    }

    private static WindowsUiAutomationSnapshotAdapter.WorkerResponse WaitForEnabledControl(
        WindowsUiAutomationSnapshotAdapter.WorkerRequest request)
    {
        if (request.AutomationId is null || request.AutomationId.Length is 0 or > 96
            || request.AutomationId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))
            || request.WaitMilliseconds is < 100 or > 3_000)
            return new(false, "UIA_INVALID_SELECTOR", []);
        if (!TryResolveRoot(request, out _, out var error)) return new(false, error, []);

        var handle = new IntPtr(request.WindowHandle);
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds <= request.WaitMilliseconds)
        {
            if (!IsWindow(handle) || !IsWindowVisible(handle)) return new(false, "UIA_TARGET_CHANGED", []);
            var root = AutomationElement.FromHandle(handle);
            if (root is null) return new(false, "UIA_TARGET_CHANGED", []);
            var matchResult = FindUniqueSafeControl(root, request.AutomationId, out var element);
            if (matchResult == ControlSearchResult.Ambiguous)
                return new(false, "UIA_AUTOMATION_ID_AMBIGUOUS", []);
            if (matchResult == ControlSearchResult.TreeLimit)
                return new(false, "UIA_TREE_LIMIT", []);
            if (matchResult == ControlSearchResult.Unique && element is not null)
            {
                var current = element.Current;
                if (current.IsEnabled)
                {
                    if (!IsWindow(handle) || !IsWindowVisible(handle)) return new(false, "UIA_TARGET_CHANGED", []);
                    return new(true, null, [], ConditionMet: true);
                }
            }
            Thread.Sleep(100);
        }

        if (!IsWindow(handle) || !IsWindowVisible(handle)) return new(false, "UIA_TARGET_CHANGED", []);
        return new(true, null, [], ConditionMet: false);
    }

    private static bool TryResolveRoot(WindowsUiAutomationSnapshotAdapter.WorkerRequest request,
        out AutomationElement root, out string error)
    {
        root = null!;
        error = "UIA_INVALID_TARGET";
        if (!OperatingSystem.IsWindows() || request.WindowHandle == 0 || request.ProcessId <= 0
            || string.IsNullOrWhiteSpace(request.ExecutablePath) || request.ExecutablePath.Length > 32_768
            || !Path.IsPathFullyQualified(request.ExecutablePath) || !File.Exists(request.ExecutablePath))
            return false;

        var handle = new IntPtr(request.WindowHandle);
        if (!IsWindow(handle) || !IsWindowVisible(handle))
        {
            error = "UIA_TARGET_CHANGED";
            return false;
        }
        var threadId = GetWindowThreadProcessId(handle, out var processId);
        if (threadId == 0 || processId != (uint)request.ProcessId)
        {
            error = "UIA_TARGET_CHANGED";
            return false;
        }

        using (var targetProcess = Process.GetProcessById(request.ProcessId))
        using (var currentProcess = Process.GetCurrentProcess())
        {
            if (targetProcess.SessionId != currentProcess.SessionId)
            {
                error = "UIA_TARGET_CHANGED";
                return false;
            }
            var imagePath = targetProcess.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(imagePath)
                || !string.Equals(Path.GetFullPath(imagePath), Path.GetFullPath(request.ExecutablePath), StringComparison.OrdinalIgnoreCase))
            {
                error = "UIA_TARGET_CHANGED";
                return false;
            }
        }

        root = AutomationElement.FromHandle(handle)!;
        if (root is null)
        {
            error = "UIA_TARGET_CHANGED";
            return false;
        }
        error = string.Empty;
        return true;
    }

    private static ControlSearchResult FindUniqueSafeControl(AutomationElement root, string automationId,
        out AutomationElement? match)
    {
        var walker = TreeWalker.ControlViewWalker;
        var pending = new Stack<(AutomationElement Element, int Depth)>();
        pending.Push((root, 0));
        var visitedNodes = 0;
        match = null;
        while (pending.Count > 0 && visitedNodes < MaximumVisitedNodes)
        {
            var (element, depth) = pending.Pop();
            visitedNodes++;
            var current = element.Current;
            if (depth > 0 && SafeNamedControlTypes.Contains(current.ControlType) && !current.IsPassword
                && string.Equals(current.AutomationId, automationId, StringComparison.Ordinal))
            {
                if (match is not null) return ControlSearchResult.Ambiguous;
                match = element;
            }

            if (depth >= MaximumDepth) continue;
            var children = new List<AutomationElement>(32);
            var child = walker.GetFirstChild(element);
            while (child is not null && children.Count < MaximumElements)
            {
                children.Add(child);
                child = walker.GetNextSibling(child);
            }
            for (var index = children.Count - 1; index >= 0; index--)
                pending.Push((children[index], depth + 1));
        }

        if (pending.Count > 0 || visitedNodes >= MaximumVisitedNodes) return ControlSearchResult.TreeLimit;
        return match is null ? ControlSearchResult.NotFound : ControlSearchResult.Unique;
    }

    private enum ControlSearchResult { NotFound, Unique, Ambiguous, TreeLimit }

    private static string Clean(string? value, int maximumLength)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var builder = new System.Text.StringBuilder(Math.Min(value.Length, maximumLength));
        foreach (var character in value)
        {
            if (builder.Length >= maximumLength) break;
            builder.Append(char.IsControl(character) ? ' ' : character);
        }
        return builder.ToString().Trim();
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximumBytes)
    {
        using var output = new MemoryStream();
        var buffer = new byte[2048];
        while (true)
        {
            var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0) return output.ToArray();
            if (output.Length + read > maximumBytes) throw new InvalidDataException("UIA input exceeded its fixed limit.");
            output.Write(buffer, 0, read);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);
}
