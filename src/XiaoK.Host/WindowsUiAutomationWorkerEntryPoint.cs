using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using System.Windows.Automation;
using XiaoK.Adapters.Windows;
using XiaoK.Core;
using XiaoK.UiAutomation;

namespace XiaoK.Host;

/// <summary>Single-request UIA worker. The parent enforces a hard timeout and kills this process on cancellation.</summary>
public static class WindowsUiAutomationWorkerEntryPoint
{
    private const int MaximumRequestBytes = 16 * 1024;
    private const int MaximumElements = 64;
    private const int MaximumVisitedNodes = 256;
    private const int MaximumDepth = 4;
    private const int MaximumScopedElements = 32;
    private const int MaximumScopedVisitedNodes = 128;
    private const int MaximumScopedDepth = 5;
    private static readonly HashSet<ControlType> ScopedRootControlTypes =
    [
        ControlType.Pane,
        ControlType.Group,
        ControlType.Tab,
        ControlType.List,
        ControlType.Tree,
        ControlType.DataGrid,
        ControlType.Table
    ];
    private static readonly HashSet<ControlType> SafeNamedControlTypes =
    [
        ControlType.Button,
        ControlType.CheckBox,
        ControlType.ComboBox,
        ControlType.Edit,
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
                    "snapshot" when request.AutomationId is null && request.WaitMilliseconds == 0 && request.Value is null => ReadSnapshot(request),
                    "scoped_snapshot" when request.AutomationId is not null && request.RootControlType is not null
                        && request.WaitMilliseconds == 0 && request.Value is null => ReadScopedSnapshot(request),
                    "wait_enabled" when request.Value is null => WaitForEnabledControl(request),
                    "set_text" => SetTextValue(request),
                    "invoke_button" when request.Value is null && request.RootControlType is null
                        && request.WaitMilliseconds == 0 => InvokeButton(request),
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
        if (ApplicationUiAutomationPolicy.IsMessagingClientTarget(request.ApplicationId,
            request.DisplayName, request.ExecutablePath))
            return new(false, "UIA_MESSAGING_APP_BLOCKED", []);
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
                var isEditable = IsEditableControl(current.ControlType);
                items.Add(new UiAutomationSnapshotItem(
                    Clean(current.AutomationId, 96),
                    Clean(current.ControlType.ProgrammaticName, 48),
                    isEditable ? string.Empty : Clean(current.Name, 160),
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

    private static WindowsUiAutomationSnapshotAdapter.WorkerResponse ReadScopedSnapshot(
        WindowsUiAutomationSnapshotAdapter.WorkerRequest request)
    {
        if (ApplicationUiAutomationPolicy.IsMessagingClientTarget(request.ApplicationId,
            request.DisplayName, request.ExecutablePath))
            return new(false, "UIA_MESSAGING_APP_BLOCKED", []);
        if (request.AutomationId is null || request.AutomationId.Length is 0 or > 96
            || request.AutomationId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))
            || request.RootControlType is null || !TryGetScopedRootControlType(request.RootControlType, out var expectedRootType))
            return new(false, "UIA_INVALID_SCOPED_SELECTOR", []);
        if (!TryResolveRoot(request, out var windowRoot, out var error)) return new(false, error, []);

        var searchResult = FindUniqueScopedRoot(windowRoot, request.AutomationId, out var scopedRoot);
        if (searchResult == ControlSearchResult.TreeLimit)
            return new(false, "UIA_SCOPED_SEARCH_LIMIT", []);
        if (searchResult == ControlSearchResult.Ambiguous)
            return new(false, "UIA_SCOPED_ROOT_AMBIGUOUS", []);
        if (searchResult != ControlSearchResult.Unique || scopedRoot is null)
            return new(false, "UIA_SCOPED_ROOT_NOT_FOUND", []);
        if (scopedRoot.Current.ControlType != expectedRootType)
            return new(false, "UIA_SCOPED_ROOT_TYPE_MISMATCH", []);
        var scopedRootRuntimeId = scopedRoot.GetRuntimeId();
        if (scopedRootRuntimeId.Length == 0) return new(false, "UIA_TARGET_CHANGED", []);

        var handle = new IntPtr(request.WindowHandle);
        var walker = TreeWalker.ControlViewWalker;
        var pending = new Stack<(AutomationElement Element, int Depth)>();
        var items = new List<UiAutomationSnapshotItem>(MaximumScopedElements);
        pending.Push((scopedRoot, 0));
        var visitedNodes = 0;
        while (pending.Count > 0)
        {
            if (items.Count >= MaximumScopedElements || visitedNodes >= MaximumScopedVisitedNodes)
                return new(false, "UIA_SCOPED_TREE_LIMIT", []);
            var (element, depth) = pending.Pop();
            if (depth > MaximumScopedDepth) return new(false, "UIA_SCOPED_TREE_LIMIT", []);
            visitedNodes++;

            var current = element.Current;
            // Scoped mode intentionally never reads Name, Value, HelpText, or descendant AutomationIds.
            items.Add(new UiAutomationSnapshotItem(string.Empty, current.ControlType.ProgrammaticName,
                string.Empty, current.IsEnabled, current.IsOffscreen, depth));

            var child = walker.GetFirstChild(element);
            if (depth >= MaximumScopedDepth)
            {
                if (child is not null) return new(false, "UIA_SCOPED_TREE_LIMIT", []);
                continue;
            }

            var children = new List<AutomationElement>(Math.Min(MaximumScopedElements, 16));
            while (child is not null && children.Count <= MaximumScopedElements)
            {
                children.Add(child);
                child = walker.GetNextSibling(child);
            }
            if (child is not null || items.Count + pending.Count + children.Count > MaximumScopedElements)
                return new(false, "UIA_SCOPED_TREE_LIMIT", []);
            for (var index = children.Count - 1; index >= 0; index--)
                pending.Push((children[index], depth + 1));
        }

        if (!TryResolveRoot(request, out var currentWindowRoot, out _)
            || FindUniqueScopedRoot(currentWindowRoot, request.AutomationId, out var currentScopedRoot) != ControlSearchResult.Unique
            || currentScopedRoot is null || currentScopedRoot.Current.ControlType != expectedRootType
            || !scopedRootRuntimeId.SequenceEqual(currentScopedRoot.GetRuntimeId())
            || !IsWindow(handle) || !IsWindowVisible(handle))
            return new(false, "UIA_TARGET_CHANGED", []);
        return new(true, null, items.ToArray());
    }

    private static bool TryGetScopedRootControlType(string controlTypeName, out ControlType controlType)
    {
        controlType = controlTypeName switch
        {
            "Pane" => ControlType.Pane,
            "Group" => ControlType.Group,
            "Tab" => ControlType.Tab,
            "List" => ControlType.List,
            "Tree" => ControlType.Tree,
            "DataGrid" => ControlType.DataGrid,
            "Table" => ControlType.Table,
            _ => null!
        };
        return controlType is not null && ScopedRootControlTypes.Contains(controlType);
    }

    private static bool IsEditableControl(ControlType controlType) =>
        controlType == ControlType.Edit || controlType == ControlType.ComboBox;

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

    private static WindowsUiAutomationSnapshotAdapter.WorkerResponse SetTextValue(
        WindowsUiAutomationSnapshotAdapter.WorkerRequest request)
    {
        var automationId = request.AutomationId;
        var value = request.Value;
        if (automationId is null || automationId.Length is 0 or > 96
            || automationId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))
            || !ApplicationUiAutomationPolicy.IsAllowedText(value)
            || request.WaitMilliseconds != 0)
            return new(false, "UIA_INVALID_TEXT_REQUEST", []);
        if (ApplicationUiAutomationPolicy.IsTextEntryTargetBlocked(request.ApplicationId, request.DisplayName,
            request.ExecutablePath))
            return new(false, "UIA_PROTECTED_APP_BLOCKED", []);

        var handle = new IntPtr(request.WindowHandle);
        var foregroundBefore = GetForegroundWindow();
        if (foregroundBefore == handle || GetWindowThreadProcessId(foregroundBefore, out var foregroundPid) != 0
            && foregroundPid == (uint)request.ProcessId)
            return new(false, "UIA_TARGET_IN_USE", []);
        if (!TryResolveRoot(request, out _, out var error)) return new(false, error, []);

        var writeResult = SafeUiAutomationTextWriter.SetValue(
            handle, request.ProcessId, automationId, value!, foregroundBefore);
        return writeResult.Success
            ? new(true, null, [])
            : new(false, writeResult.ErrorCode, [], OutcomeUncertain: writeResult.OutcomeUncertain);
    }

    private static WindowsUiAutomationSnapshotAdapter.WorkerResponse InvokeButton(
        WindowsUiAutomationSnapshotAdapter.WorkerRequest request)
    {
        var automationId = request.AutomationId;
        if (automationId is null || automationId.Length is 0 or > 96
            || automationId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-')))
            return new(false, "UIA_INVALID_SELECTOR", []);
        if (ApplicationUiAutomationPolicy.IsGenericButtonInvocationSelectorBlocked(automationId))
            return new(false, "UIA_BUTTON_ACTION_BLOCKED", []);
        if (ApplicationUiAutomationPolicy.IsGenericButtonInvocationTargetBlocked(request.ApplicationId, request.DisplayName,
            request.ExecutablePath))
            return new(false, "UIA_BUTTON_TARGET_BLOCKED", []);
        if (!TryResolveRoot(request, out var root, out var error)) return new(false, error, []);

        var handle = new IntPtr(request.WindowHandle);
        var foregroundBefore = GetForegroundWindow();
        if (foregroundBefore == IntPtr.Zero)
            return new(false, "UIA_FOREGROUND_UNKNOWN", []);
        if (foregroundBefore == handle || GetWindowThreadProcessId(foregroundBefore, out var foregroundPid) != 0
            && foregroundPid == (uint)request.ProcessId)
            return new(false, "UIA_TARGET_IN_USE", []);

        var searchResult = FindUniqueSafeControl(root, automationId, out var element);
        if (searchResult == ControlSearchResult.Ambiguous)
            return new(false, "UIA_AUTOMATION_ID_AMBIGUOUS", []);
        if (searchResult == ControlSearchResult.TreeLimit)
            return new(false, "UIA_TREE_LIMIT", []);
        if (searchResult != ControlSearchResult.Unique || element is null)
            return new(false, "UIA_BUTTON_NOT_FOUND", []);

        var invokeMayHaveStarted = false;
        try
        {
            if (!IsWindow(handle) || !IsWindowVisible(handle)
                || GetWindowThreadProcessId(handle, out var processId) == 0 || processId != (uint)request.ProcessId)
                return new(false, "UIA_TARGET_CHANGED", []);
            if (GetForegroundWindow() != foregroundBefore)
                return new(false, "UIA_FOREGROUND_CHANGED_BEFORE_ACTION", []);

            var current = element.Current;
            if (!string.Equals(current.AutomationId, automationId, StringComparison.Ordinal)
                || current.ProcessId != request.ProcessId)
                return new(false, "UIA_TARGET_CHANGED", []);
            if (current.ControlType != ControlType.Button || !current.IsEnabled || current.IsOffscreen
                || !element.TryGetCurrentPattern(InvokePattern.Pattern, out var patternObject)
                || patternObject is not InvokePattern invokePattern)
                return new(false, "UIA_BUTTON_NOT_INVOKABLE", []);
            if (GetForegroundWindow() != foregroundBefore)
                return new(false, "UIA_FOREGROUND_CHANGED_BEFORE_ACTION", []);

            invokeMayHaveStarted = true;
            invokePattern.Invoke();
            if (GetForegroundWindow() != foregroundBefore)
                return new(false, "UIA_ACTION_OUTCOME_UNCERTAIN", [], OutcomeUncertain: true);
            return new(true, null, []);
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException
            or ArgumentException or UnauthorizedAccessException or SecurityException or Win32Exception or NotSupportedException)
        {
            return invokeMayHaveStarted
                ? new(false, "UIA_ACTION_OUTCOME_UNCERTAIN", [], OutcomeUncertain: true)
                : new(false, "UIA_READ_FAILED", []);
        }
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
            if (child is not null) return ControlSearchResult.TreeLimit;
            for (var index = children.Count - 1; index >= 0; index--)
                pending.Push((children[index], depth + 1));
        }

        if (pending.Count > 0 || visitedNodes >= MaximumVisitedNodes) return ControlSearchResult.TreeLimit;
        return match is null ? ControlSearchResult.NotFound : ControlSearchResult.Unique;
    }

    private static ControlSearchResult FindUniqueScopedRoot(AutomationElement root, string automationId,
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
            if (string.Equals(current.AutomationId, automationId, StringComparison.Ordinal))
            {
                if (match is not null) return ControlSearchResult.Ambiguous;
                match = element;
            }

            if (depth >= MaximumDepth)
            {
                if (walker.GetFirstChild(element) is not null) return ControlSearchResult.TreeLimit;
                continue;
            }
            var children = new List<AutomationElement>(32);
            var child = walker.GetFirstChild(element);
            while (child is not null && children.Count < MaximumElements)
            {
                children.Add(child);
                child = walker.GetNextSibling(child);
            }
            if (child is not null) return ControlSearchResult.TreeLimit;
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
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);
}
