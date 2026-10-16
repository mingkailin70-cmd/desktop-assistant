using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;
using XiaoK.Core;

namespace XiaoK.UiAutomation;

public sealed record UiAutomationTextWriteResult(bool Success, string? ErrorCode, bool OutcomeUncertain);

/// <summary>Writes a confirmed value through one UIA client with automatic focus changes disabled.</summary>
public static class SafeUiAutomationTextWriter
{
    private const int MaximumElements = 64;
    private const int MaximumVisitedNodes = 256;
    private const int MaximumDepth = 4;

    public static UiAutomationTextWriteResult SetValue(
        IntPtr windowHandle,
        int processId,
        string automationId,
        string value,
        IntPtr expectedForegroundWindow)
    {
        var mutationMayHaveStarted = false;
        try
        {
            if (windowHandle == IntPtr.Zero || processId <= 0 || string.IsNullOrWhiteSpace(automationId)
                || automationId.Length > 96 || string.IsNullOrEmpty(value) || value.Length > 2_000
                || expectedForegroundWindow == IntPtr.Zero
                || ApplicationUiAutomationPolicy.IsSensitiveTextEntryField(automationId))
                return Fail("UIA_INVALID_TEXT_REQUEST");

            var handle = new HWND(windowHandle);
            if (!IsTargetWindow(handle, processId)) return Fail("UIA_TARGET_CHANGED");
            var foregroundBefore = PInvoke.GetForegroundWindow();
            if ((IntPtr)foregroundBefore != expectedForegroundWindow)
                return Fail("UIA_FOREGROUND_CHANGED_BEFORE_SET");
            if (foregroundBefore == handle || NativeMethods.GetWindowThreadProcessId(foregroundBefore, out var foregroundPid) != 0
                && foregroundPid == (uint)processId)
                return Fail("UIA_TARGET_IN_USE");

            var automation = CUIAutomation8.CreateInstance<IUIAutomation2>();
            automation.put_AutoSetFocus(new BOOL(false));
            if (automation.get_AutoSetFocus().Value != 0)
                return Fail("UIA_AUTO_SET_FOCUS_DISABLE_FAILED");

            var client = (IUIAutomation)automation;
            var focusedBefore = client.GetFocusedElement();
            var root = automation.ElementFromHandle(handle);
            var searchResult = FindUniqueByAutomationId(client, root, automationId, out var target);
            if (searchResult == SearchResult.Ambiguous) return Fail("UIA_AUTOMATION_ID_AMBIGUOUS");
            if (searchResult == SearchResult.TreeLimit) return Fail("UIA_TREE_LIMIT");
            if (searchResult != SearchResult.Unique || target is null) return Fail("UIA_CONTROL_NOT_FOUND");

            if (target.get_CurrentControlType() != UIA_CONTROLTYPE_ID.UIA_EditControlTypeId
                || target.get_CurrentProcessId() != processId
                || target.get_CurrentIsPassword().Value != 0
                || target.get_CurrentIsEnabled().Value == 0
                || target.get_CurrentIsOffscreen().Value != 0
                || target.GetCurrentPattern(UIA_PATTERN_ID.UIA_ValuePatternId) is not IUIAutomationValuePattern valuePattern
                || valuePattern.get_CurrentIsReadOnly().Value != 0)
                return Fail("UIA_CONTROL_NOT_WRITABLE");

            if (!IsTargetWindow(handle, processId)) return Fail("UIA_TARGET_CHANGED");
            if (automation.get_AutoSetFocus().Value != 0) return Fail("UIA_AUTO_SET_FOCUS_DISABLE_FAILED");
            if ((IntPtr)PInvoke.GetForegroundWindow() != expectedForegroundWindow)
                return Fail("UIA_FOREGROUND_CHANGED_BEFORE_SET");
            var focusedImmediatelyBefore = client.GetFocusedElement();
            if (client.CompareElements(focusedBefore, focusedImmediatelyBefore).Value == 0)
                return Fail("UIA_FOCUS_CHANGED_BEFORE_SET");

            mutationMayHaveStarted = true;
            unsafe
            {
                fixed (char* valuePointer = value)
                    valuePattern.SetValue(new BSTR(valuePointer));
            }

            var foregroundAfter = PInvoke.GetForegroundWindow();
            var focusedAfter = client.GetFocusedElement();
            if ((IntPtr)foregroundAfter != expectedForegroundWindow
                || client.CompareElements(focusedBefore, focusedAfter).Value == 0
                || automation.get_AutoSetFocus().Value != 0)
                return Fail("UIA_FOREGROUND_OR_FOCUS_CHANGED_AFTER_SET", outcomeUncertain: true);

            if (!IsTargetWindow(handle, processId)) return Fail("UIA_VALUE_VERIFY_FAILED", outcomeUncertain: true);
            return string.Equals(valuePattern.get_CurrentValue().ToString(), value, StringComparison.Ordinal)
                ? new(true, null, false)
                : Fail("UIA_VALUE_VERIFY_FAILED", outcomeUncertain: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException
            or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        {
            return Fail(mutationMayHaveStarted ? "UIA_VALUE_VERIFY_FAILED" : "UIA_READ_FAILED", mutationMayHaveStarted);
        }
    }

    /// <summary>Writes only when the user has already focused the exact foreground edit control.</summary>
    public static UiAutomationTextWriteResult SetValueInForeground(
        IntPtr windowHandle,
        int processId,
        string automationId,
        string value)
    {
        var mutationMayHaveStarted = false;
        try
        {
            if (windowHandle == IntPtr.Zero || processId <= 0 || string.IsNullOrWhiteSpace(automationId)
                || automationId.Length > 96 || string.IsNullOrEmpty(value) || value.Length > 2_000
                || ApplicationUiAutomationPolicy.IsSensitiveTextEntryField(automationId))
                return Fail("UIA_INVALID_TEXT_REQUEST");

            var handle = new HWND(windowHandle);
            if (!IsTargetWindow(handle, processId)) return Fail("UIA_TARGET_CHANGED");
            var foregroundBefore = PInvoke.GetForegroundWindow();
            if ((IntPtr)foregroundBefore == IntPtr.Zero) return Fail("UIA_FOREGROUND_UNKNOWN");
            if ((IntPtr)foregroundBefore != windowHandle) return Fail("UIA_TARGET_NOT_FOREGROUND");

            var automation = CUIAutomation8.CreateInstance<IUIAutomation2>();
            automation.put_AutoSetFocus(new BOOL(false));
            if (automation.get_AutoSetFocus().Value != 0)
                return Fail("UIA_AUTO_SET_FOCUS_DISABLE_FAILED");

            var client = (IUIAutomation)automation;
            var focusedBefore = client.GetFocusedElement();
            var root = automation.ElementFromHandle(handle);
            var searchResult = FindUniqueByAutomationId(client, root, automationId, out var target);
            if (searchResult == SearchResult.Ambiguous) return Fail("UIA_AUTOMATION_ID_AMBIGUOUS");
            if (searchResult == SearchResult.TreeLimit) return Fail("UIA_TREE_LIMIT");
            if (searchResult != SearchResult.Unique || target is null) return Fail("UIA_CONTROL_NOT_FOUND");

            if (target.get_CurrentControlType() != UIA_CONTROLTYPE_ID.UIA_EditControlTypeId
                || target.get_CurrentProcessId() != processId
                || target.get_CurrentIsPassword().Value != 0
                || target.get_CurrentIsEnabled().Value == 0
                || target.get_CurrentIsOffscreen().Value != 0
                || target.GetCurrentPattern(UIA_PATTERN_ID.UIA_ValuePatternId) is not IUIAutomationValuePattern valuePattern
                || valuePattern.get_CurrentIsReadOnly().Value != 0)
                return Fail("UIA_CONTROL_NOT_WRITABLE");

            if (!IsTargetWindow(handle, processId)) return Fail("UIA_TARGET_CHANGED");
            if (automation.get_AutoSetFocus().Value != 0) return Fail("UIA_AUTO_SET_FOCUS_DISABLE_FAILED");
            if ((IntPtr)PInvoke.GetForegroundWindow() != windowHandle)
                return Fail("UIA_FOREGROUND_CHANGED_BEFORE_SET");
            if (client.CompareElements(focusedBefore, target).Value == 0)
                return Fail("UIA_FOCUS_NOT_TARGET");
            var focusedImmediatelyBefore = client.GetFocusedElement();
            if (client.CompareElements(focusedBefore, focusedImmediatelyBefore).Value == 0)
                return Fail("UIA_FOCUS_CHANGED_BEFORE_SET");

            mutationMayHaveStarted = true;
            unsafe
            {
                fixed (char* valuePointer = value)
                    valuePattern.SetValue(new BSTR(valuePointer));
            }

            var foregroundAfter = PInvoke.GetForegroundWindow();
            var focusedAfter = client.GetFocusedElement();
            if ((IntPtr)foregroundAfter != windowHandle
                || client.CompareElements(focusedBefore, focusedAfter).Value == 0
                || automation.get_AutoSetFocus().Value != 0)
                return Fail("UIA_FOREGROUND_OR_FOCUS_CHANGED_AFTER_SET", outcomeUncertain: true);

            if (!IsTargetWindow(handle, processId)) return Fail("UIA_VALUE_VERIFY_FAILED", outcomeUncertain: true);
            return string.Equals(valuePattern.get_CurrentValue().ToString(), value, StringComparison.Ordinal)
                ? new(true, null, false)
                : Fail("UIA_VALUE_VERIFY_FAILED", outcomeUncertain: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException
            or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        {
            return Fail(mutationMayHaveStarted ? "UIA_VALUE_VERIFY_FAILED" : "UIA_READ_FAILED", mutationMayHaveStarted);
        }
    }

    private static bool IsTargetWindow(HWND handle, int processId) =>
        PInvoke.IsWindow(handle).Value != 0 && PInvoke.IsWindowVisible(handle).Value != 0
        && NativeMethods.GetWindowThreadProcessId(handle, out var targetPid) != 0 && targetPid == (uint)processId;

    private static SearchResult FindUniqueByAutomationId(IUIAutomation client, IUIAutomationElement root,
        string automationId, out IUIAutomationElement? match)
    {
        var walker = client.get_ControlViewWalker();
        var pending = new Stack<(IUIAutomationElement Element, int Depth)>();
        pending.Push((root, 0));
        var visitedNodes = 0;
        var truncated = false;
        match = null;
        while (pending.Count > 0 && visitedNodes < MaximumVisitedNodes)
        {
            var (element, depth) = pending.Pop();
            visitedNodes++;
            if (depth > 0 && string.Equals(element.get_CurrentAutomationId().ToString(), automationId, StringComparison.Ordinal))
            {
                if (match is not null) return SearchResult.Ambiguous;
                match = element;
            }

            if (depth >= MaximumDepth) continue;
            var children = new List<IUIAutomationElement>(MaximumElements);
            var child = walker.GetFirstChildElement(element);
            while (child is not null && children.Count < MaximumElements)
            {
                children.Add(child);
                child = walker.GetNextSiblingElement(child);
            }
            if (child is not null) truncated = true;
            for (var index = children.Count - 1; index >= 0; index--)
                pending.Push((children[index], depth + 1));
        }

        if (truncated || pending.Count > 0 || visitedNodes >= MaximumVisitedNodes) return SearchResult.TreeLimit;
        return match is null ? SearchResult.NotFound : SearchResult.Unique;
    }

    private static UiAutomationTextWriteResult Fail(string errorCode, bool outcomeUncertain = false) =>
        new(false, errorCode, outcomeUncertain);

    private enum SearchResult { NotFound, Unique, Ambiguous, TreeLimit }
}

internal static partial class NativeMethods
{
    [LibraryImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    internal static partial uint GetWindowThreadProcessId(HWND windowHandle, out uint processId);
}
