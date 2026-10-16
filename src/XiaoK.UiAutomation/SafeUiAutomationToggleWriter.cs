using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;
using XiaoK.Core;

namespace XiaoK.UiAutomation;

public sealed record UiAutomationToggleSetResult(bool Success, string? ErrorCode, bool OutcomeUncertain);

/// <summary>Sets one focused ordinary checkbox to an explicit state without moving foreground or focus.</summary>
public static class SafeUiAutomationToggleWriter
{
    public static UiAutomationToggleSetResult SetStateInForeground(IntPtr windowHandle, int processId,
        string automationId, string state)
    {
        var mutationMayHaveStarted = false;
        try
        {
            if (windowHandle == IntPtr.Zero || processId <= 0 || string.IsNullOrWhiteSpace(automationId)
                || automationId.Length > 96
                || automationId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))
                || ApplicationUiAutomationPolicy.IsSensitiveTextEntryField(automationId)
                || ApplicationUiAutomationPolicy.IsGenericButtonInvocationSelectorBlocked(automationId)
                || state is not ("On" or "Off"))
                return Fail("UIA_INVALID_TOGGLE_REQUEST");

            var handle = new HWND(windowHandle);
            if (!IsTargetWindow(handle, processId)) return Fail("UIA_TARGET_CHANGED");
            var foreground = PInvoke.GetForegroundWindow();
            if ((IntPtr)foreground == IntPtr.Zero) return Fail("UIA_FOREGROUND_UNKNOWN");
            if ((IntPtr)foreground != windowHandle) return Fail("UIA_TARGET_NOT_FOREGROUND");

            var automation = CUIAutomation8.CreateInstance<IUIAutomation2>();
            automation.put_AutoSetFocus(new BOOL(false));
            if (automation.get_AutoSetFocus().Value != 0)
                return Fail("UIA_AUTO_SET_FOCUS_DISABLE_FAILED");

            var client = (IUIAutomation)automation;
            var focusedBefore = client.GetFocusedElement();
            var root = automation.ElementFromHandle(handle);
            var searchResult = SafeUiAutomationTextWriter.FindUniqueByAutomationId(client, root, automationId, out var target);
            if (searchResult == SafeUiAutomationTextWriter.SearchResult.Ambiguous)
                return Fail("UIA_AUTOMATION_ID_AMBIGUOUS");
            if (searchResult == SafeUiAutomationTextWriter.SearchResult.TreeLimit)
                return Fail("UIA_TREE_LIMIT");
            if (searchResult != SafeUiAutomationTextWriter.SearchResult.Unique || target is null)
                return Fail("UIA_CONTROL_NOT_FOUND");

            if (target.get_CurrentControlType() != UIA_CONTROLTYPE_ID.UIA_CheckBoxControlTypeId
                || target.get_CurrentProcessId() != processId
                || target.get_CurrentIsEnabled().Value == 0
                || target.get_CurrentIsOffscreen().Value != 0
                || target.GetCurrentPattern(UIA_PATTERN_ID.UIA_TogglePatternId) is not IUIAutomationTogglePattern togglePattern)
                return Fail("UIA_TOGGLE_STATE_UNSUPPORTED");

            if (!IsTargetWindow(handle, processId)) return Fail("UIA_TARGET_CHANGED");
            if (automation.get_AutoSetFocus().Value != 0) return Fail("UIA_AUTO_SET_FOCUS_DISABLE_FAILED");
            if ((IntPtr)PInvoke.GetForegroundWindow() != windowHandle)
                return Fail("UIA_FOREGROUND_CHANGED_BEFORE_TOGGLE");
            if (client.CompareElements(focusedBefore, target).Value == 0)
                return Fail("UIA_FOCUS_NOT_TOGGLE_TARGET");
            if (client.CompareElements(focusedBefore, client.GetFocusedElement()).Value == 0)
                return Fail("UIA_FOCUS_CHANGED_BEFORE_TOGGLE");

            var desiredState = state == "On" ? 1 : 0;
            var currentState = (int)togglePattern.get_CurrentToggleState();
            if (currentState is not (0 or 1)) return Fail("UIA_TOGGLE_STATE_UNSUPPORTED");
            if (currentState != desiredState)
            {
                if (!IsTargetWindow(handle, processId)
                    || (IntPtr)PInvoke.GetForegroundWindow() != windowHandle
                    || client.CompareElements(focusedBefore, client.GetFocusedElement()).Value == 0)
                    return Fail("UIA_FOREGROUND_CHANGED_BEFORE_TOGGLE");
                var stateImmediatelyBefore = (int)togglePattern.get_CurrentToggleState();
                if (stateImmediatelyBefore != currentState)
                    return Fail("UIA_TOGGLE_STATE_CHANGED_BEFORE_ACTION");

                mutationMayHaveStarted = true;
                togglePattern.Toggle();

                if ((IntPtr)PInvoke.GetForegroundWindow() != windowHandle
                    || client.CompareElements(focusedBefore, client.GetFocusedElement()).Value == 0
                    || automation.get_AutoSetFocus().Value != 0)
                    return Fail("UIA_FOREGROUND_OR_FOCUS_CHANGED_AFTER_TOGGLE", outcomeUncertain: true);
            }

            if (!IsTargetWindow(handle, processId)) return Fail("UIA_TOGGLE_VERIFY_FAILED", outcomeUncertain: mutationMayHaveStarted);
            return (int)togglePattern.get_CurrentToggleState() == desiredState
                ? new(true, null, false)
                : Fail("UIA_TOGGLE_VERIFY_FAILED", outcomeUncertain: mutationMayHaveStarted);
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException
            or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        {
            return Fail(mutationMayHaveStarted ? "UIA_TOGGLE_VERIFY_FAILED" : "UIA_READ_FAILED", mutationMayHaveStarted);
        }
    }

    private static bool IsTargetWindow(HWND handle, int processId) =>
        PInvoke.IsWindow(handle).Value != 0 && PInvoke.IsWindowVisible(handle).Value != 0
        && NativeMethods.GetWindowThreadProcessId(handle, out var targetPid) != 0 && targetPid == (uint)processId;

    private static UiAutomationToggleSetResult Fail(string errorCode, bool outcomeUncertain = false) =>
        new(false, errorCode, outcomeUncertain);
}
