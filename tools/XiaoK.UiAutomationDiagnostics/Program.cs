using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

internal static class Program
{
    private const string AutomationId = "XiaoK.Synthetic.Compose";
    private const string SyntheticText = "XiaoK synthetic cross-process background UIA value";

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    private sealed record WindowEnumerationState(HashSet<int> ProcessIds, List<IntPtr> Windows);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static Windows.Win32.Foundation.BOOL EnumerateTargetWindow(
        Windows.Win32.Foundation.HWND hwnd,
        Windows.Win32.Foundation.LPARAM lParam)
    {
        try
        {
            var stateHandle = GCHandle.FromIntPtr((IntPtr)lParam);
            if (stateHandle.Target is not WindowEnumerationState state) return false;
            _ = PInvoke.GetWindowThreadProcessId(hwnd, out var windowProcessId);
            if (windowProcessId <= int.MaxValue && state.ProcessIds.Contains((int)windowProcessId) && PInvoke.IsWindowVisible(hwnd))
                state.Windows.Add((IntPtr)hwnd);
        }
        catch
        {
            // Never allow a managed exception to cross the unmanaged callback boundary.
            return false;
        }
        return true;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--target")
            return RunSyntheticTarget(args[1]);
        if (args.Length == 2 && args[0] == "--inspect-live-app")
            return RunLiveAppProbe(args[1]);
        if (args.Length == 2 && args[0] == "--inspect-live-app-worker")
            return RunLiveAppProbeWorker(args[1]);
        if (args.Length != 0)
        {
            Console.Error.WriteLine("FAIL: unexpected probe arguments; refusing to start another client.");
            return 9;
        }
        return RunProbe();
    }

    private static int RunLiveAppProbe(string app)
    {
        if (!TryGetTargetApp(app, out var processName, out _))
        {
            Console.WriteLine("status=invalid_app; allowed=wechat,qq");
            return 10;
        }

        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("current process path unavailable");
        var start = new ProcessStartInfo(processPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--inspect-live-app-worker");
        start.ArgumentList.Add(processName);

        using var child = Process.Start(start) ?? throw new InvalidOperationException("could not start isolated UIA probe");
        var stdoutTask = child.StandardOutput.ReadToEndAsync();
        var stderrTask = child.StandardError.ReadToEndAsync();
        if (!child.WaitForExit(20000))
        {
            child.Kill(entireProcessTree: true);
            child.WaitForExit(5000);
            Console.WriteLine("status=timeout; target_app_process_left_untouched=true");
            return 11;
        }

        var output = stdoutTask.GetAwaiter().GetResult();
        _ = stderrTask.GetAwaiter().GetResult();
        Console.Write(output);
        return child.ExitCode;
    }

    private static int RunLiveAppProbeWorker(string processName)
    {
        if (!TryGetTargetApp(processName, out var expectedProcessName, out var expectedPath))
        {
            Console.WriteLine("status=invalid_app");
            return 10;
        }

        try
        {
            var matchingProcessIds = new HashSet<int>();
            var windows = new List<IntPtr>();
            foreach (var process in Process.GetProcessesByName(expectedProcessName))
            {
                using (process)
                {
                    try
                    {
                        if (string.Equals(process.MainModule?.FileName, expectedPath, StringComparison.OrdinalIgnoreCase))
                        {
                            matchingProcessIds.Add(process.Id);
                        }
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                    {
                        // Process metadata is optional; inaccessible processes are ignored.
                    }
                }
            }

            if (matchingProcessIds.Count == 0)
            {
                Console.WriteLine("status=target_process_not_found; message_content_read=false");
                return 12;
            }

            var enumerationStateHandle = GCHandle.Alloc(new WindowEnumerationState(matchingProcessIds, windows));
            bool enumerationSucceeded;
            try
            {
                unsafe
                {
                    enumerationSucceeded = PInvoke.EnumWindows(
                        &EnumerateTargetWindow,
                        GCHandle.ToIntPtr(enumerationStateHandle));
                }
            }
            finally
            {
                enumerationStateHandle.Free();
            }
            if (!enumerationSucceeded)
            {
                Console.WriteLine("status=window_enumeration_failed; window_titles_read=false; message_content_read=false");
                return 17;
            }

            if (windows.Count == 0)
            {
                Console.WriteLine($"status=no_visible_window; matching_process_count={matchingProcessIds.Count}; message_content_read=false");
                return 13;
            }

            var foregroundBefore = GetForegroundWindow();
            var automation = CUIAutomation8.CreateInstance<IUIAutomation2>();
            automation.put_AutoSetFocus(new BOOL(false));
            if (automation.get_AutoSetFocus().Value != 0)
            {
                Console.WriteLine("status=autosetfocus_disable_failed; message_content_read=false");
                return 14;
            }

            var uiAutomation = (IUIAutomation)automation;
            var focusedBefore = uiAutomation.GetFocusedElement();
            var walker = uiAutomation.get_ControlViewWalker();
            var controlCounts = new Dictionary<UIA_CONTROLTYPE_ID, int>();
            var automationIdCount = 0;
            var valuePatternCount = 0;
            var invokePatternCount = 0;
            var scannedNodes = 0;
            var windowsScanned = 0;
            foreach (var hwnd in windows.Take(4))
            {
                var root = automation.ElementFromHandle(new HWND(hwnd));
                CountStructureOnly(walker, root, 0, ref scannedNodes, controlCounts, ref automationIdCount, ref valuePatternCount, ref invokePatternCount);
                windowsScanned++;
                if (scannedNodes >= 1000) break;
            }

            var foregroundAfter = GetForegroundWindow();
            var focusedAfter = uiAutomation.GetFocusedElement();
            var foregroundUnchanged = foregroundBefore == foregroundAfter;
            var focusUnchanged = uiAutomation.CompareElements(focusedBefore, focusedAfter).Value != 0;
            Console.WriteLine($"status={(foregroundUnchanged && focusUnchanged ? "READ_ONLY_SCAN_COMPLETE" : "FOCUS_INVARIANT_FAILED")}");
            Console.WriteLine($"target_process_count={matchingProcessIds.Count}");
            Console.WriteLine($"visible_windows_scanned={windowsScanned}");
            Console.WriteLine($"uia_nodes_scanned={scannedNodes}");
            Console.WriteLine($"control_types={FormatControlCounts(controlCounts)}");
            Console.WriteLine($"nodes_with_automation_id={automationIdCount}");
            Console.WriteLine($"edit_value_patterns_available={valuePatternCount}");
            Console.WriteLine($"button_invoke_patterns_available={invokePatternCount}");
            Console.WriteLine($"foreground_unchanged={foregroundUnchanged}");
            Console.WriteLine($"uia_focus_unchanged={focusUnchanged}");
            Console.WriteLine("window_titles_read=false; control_names_read=false; text_values_read=false; screenshots_taken=false; input_sent=false; message_content_read=false");
            return foregroundUnchanged && focusUnchanged ? 0 : 15;
        }
        catch (COMException)
        {
            Console.WriteLine("status=uia_provider_error; message_content_read=false");
            return 16;
        }
    }

    private static void CountStructureOnly(
        IUIAutomationTreeWalker walker,
        IUIAutomationElement node,
        int depth,
        ref int scannedNodes,
        Dictionary<UIA_CONTROLTYPE_ID, int> controlCounts,
        ref int automationIdCount,
        ref int valuePatternCount,
        ref int invokePatternCount)
    {
        if (depth > 10 || scannedNodes >= 1000) return;
        scannedNodes++;

        var controlType = node.get_CurrentControlType();
        controlCounts[controlType] = controlCounts.GetValueOrDefault(controlType) + 1;
        if (!string.IsNullOrEmpty(node.get_CurrentAutomationId().ToString())) automationIdCount++;

        if (controlType == UIA_CONTROLTYPE_ID.UIA_EditControlTypeId)
            TryCountPattern(node, UIA_PATTERN_ID.UIA_ValuePatternId, ref valuePatternCount);
        if (controlType == UIA_CONTROLTYPE_ID.UIA_ButtonControlTypeId)
            TryCountPattern(node, UIA_PATTERN_ID.UIA_InvokePatternId, ref invokePatternCount);

        var child = walker.GetFirstChildElement(node);
        while (child is not null && scannedNodes < 1000)
        {
            CountStructureOnly(walker, child, depth + 1, ref scannedNodes, controlCounts, ref automationIdCount, ref valuePatternCount, ref invokePatternCount);
            child = walker.GetNextSiblingElement(child);
        }
    }

    private static void TryCountPattern(IUIAutomationElement element, UIA_PATTERN_ID patternId, ref int count)
    {
        try
        {
            if (element.GetCurrentPattern(patternId) is not null) count++;
        }
        catch (COMException)
        {
            // Pattern support is only queried, never invoked.
        }
    }

    private static string FormatControlCounts(Dictionary<UIA_CONTROLTYPE_ID, int> counts)
    {
        var names = new Dictionary<UIA_CONTROLTYPE_ID, string>
        {
            [UIA_CONTROLTYPE_ID.UIA_ButtonControlTypeId] = "Button",
            [UIA_CONTROLTYPE_ID.UIA_CustomControlTypeId] = "Custom",
            [UIA_CONTROLTYPE_ID.UIA_DocumentControlTypeId] = "Document",
            [UIA_CONTROLTYPE_ID.UIA_EditControlTypeId] = "Edit",
            [UIA_CONTROLTYPE_ID.UIA_GroupControlTypeId] = "Group",
            [UIA_CONTROLTYPE_ID.UIA_ListControlTypeId] = "List",
            [UIA_CONTROLTYPE_ID.UIA_ListItemControlTypeId] = "ListItem",
            [UIA_CONTROLTYPE_ID.UIA_PaneControlTypeId] = "Pane",
            [UIA_CONTROLTYPE_ID.UIA_TextControlTypeId] = "Text",
            [UIA_CONTROLTYPE_ID.UIA_WindowControlTypeId] = "Window"
        };
        return string.Join(',', counts.OrderBy(pair => (int)pair.Key).Select(pair => $"{names.GetValueOrDefault(pair.Key, "Other")}-{pair.Value}"));
    }

    private static bool TryGetTargetApp(string value, out string processName, out string executablePath)
    {
        if (value.Equals("wechat", StringComparison.OrdinalIgnoreCase) || value.Equals("weixin", StringComparison.OrdinalIgnoreCase))
        {
            processName = "Weixin";
            executablePath = @"D:\微信\Weixin\Weixin.exe";
            return true;
        }
        if (value.Equals("qq", StringComparison.OrdinalIgnoreCase))
        {
            processName = "QQ";
            executablePath = @"D:\QQ\QQ.exe";
            return true;
        }
        processName = "";
        executablePath = "";
        return false;
    }

    private static int RunSyntheticTarget(string pipeName)
    {
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var form = new SyntheticWindow();
        var input = new TextBox { Name = AutomationId, AccessibleName = "Synthetic compose field", Bounds = new System.Drawing.Rectangle(0, 0, 200, 24) };
        form.Controls.Add(input);
        form.CreateControl();
        input.CreateControl();
        var context = new ApplicationContext();
        form.FormClosed += (_, _) => context.ExitThread();
        var foregroundBeforeShow = GetForegroundWindow();
        var hwnd = form.Handle;
        ShowWindow(hwnd, 4); // SW_SHOWNOACTIVATE
        var foregroundAfterShow = GetForegroundWindow();
        var pipeTask = Task.Run(async () =>
        {
            await pipe.WaitForConnectionAsync();
            using var reader = new StreamReader(pipe, leaveOpen: true);
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync($"{hwnd.ToInt64():X}|{foregroundBeforeShow.ToInt64():X}|{foregroundAfterShow.ToInt64():X}");
            await reader.ReadLineAsync();
            if (!form.IsDisposed)
                form.BeginInvoke(new Action(form.Close));
        });
        Application.Run(context);
        pipeTask.Wait(TimeSpan.FromSeconds(10));
        return 0;
    }
    private static unsafe int RunProbe()
    {
        var priorForeground = GetForegroundWindow();
        var pipeName = "XiaoK-UiaFocusProbe-" + Guid.NewGuid().ToString("N");
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("current process path unavailable");
        var start = new ProcessStartInfo(processPath) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--target");
        start.ArgumentList.Add(pipeName);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("could not start synthetic target");
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(10000);
            using var reader = new StreamReader(pipe, leaveOpen: true);
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            var targetInfo = reader.ReadLine() ?? throw new InvalidOperationException("synthetic target did not provide its window handle");
            var parts = targetInfo.Split('|');
            var targetHwnd = new HWND(new IntPtr(Convert.ToInt64(parts[0], 16)));
            var foregroundBeforeShow = new IntPtr(Convert.ToInt64(parts[1], 16));
            var foregroundAfterShow = new IntPtr(Convert.ToInt64(parts[2], 16));
            var foregroundAfterChildLaunch = GetForegroundWindow();
            Console.WriteLine($"synthetic_target_pid={child.Id}");
            Console.WriteLine($"synthetic_hwnd={targetHwnd}");
            Console.WriteLine($"foreground_before=0x{priorForeground.ToInt64():X}");
            Console.WriteLine($"foreground_before_show=0x{foregroundBeforeShow.ToInt64():X}");
            Console.WriteLine($"foreground_after_show=0x{foregroundAfterShow.ToInt64():X}");
            Console.WriteLine($"foreground_after_child_launch=0x{foregroundAfterChildLaunch.ToInt64():X}");
            if (foregroundBeforeShow != priorForeground || foregroundAfterShow != priorForeground || foregroundAfterChildLaunch != priorForeground)
            {
                Console.WriteLine("FAIL: synthetic target changed foreground; no UIA mutation attempted.");
                writer.WriteLine("exit");
                return 2;
            }

            var automation = CUIAutomation8.CreateInstance<IUIAutomation2>();
            automation.put_AutoSetFocus(new BOOL(false));
            if (automation.get_AutoSetFocus().Value != 0)
            {
                Console.WriteLine("FAIL: AutoSetFocus could not be disabled.");
                writer.WriteLine("exit");
                return 3;
            }
            var uiAutomation = (IUIAutomation)automation;
            var focusedBefore = uiAutomation.GetFocusedElement();
            var windowElement = automation.ElementFromHandle(targetHwnd);
            var walker = uiAutomation.get_ControlViewWalker();
            var count = 0;
            var target = FindByAutomationId(walker, windowElement, AutomationId, 0, ref count);
            Console.WriteLine($"uia_nodes_scanned={count}");
            if (target is null)
            {
                Console.WriteLine("FAIL: UIA did not expose the synthetic edit field by AutomationId.");
                writer.WriteLine("exit");
                return 4;
            }

            var controlType = target.get_CurrentControlType();
            var processId = target.get_CurrentProcessId();
            var isEnabled = target.get_CurrentIsEnabled().Value != 0;
            var isPassword = target.get_CurrentIsPassword().Value != 0;
            var isOffscreen = target.get_CurrentIsOffscreen().Value != 0;
            Console.WriteLine($"target_type={controlType}; pid={processId}; enabled={isEnabled}; password={isPassword}; offscreen={isOffscreen}");
            if (controlType != UIA_CONTROLTYPE_ID.UIA_EditControlTypeId || processId != child.Id || !isEnabled || isPassword)
            {
                Console.WriteLine("FAIL: synthetic target did not meet the expected safe editable-control properties.");
                writer.WriteLine("exit");
                return 5;
            }

            var valuePattern = (IUIAutomationValuePattern)target.GetCurrentPattern(UIA_PATTERN_ID.UIA_ValuePatternId);
            var beforeValue = valuePattern.get_CurrentValue().ToString();
            if (!string.IsNullOrEmpty(beforeValue))
            {
                Console.WriteLine("FAIL: synthetic field was unexpectedly non-empty; no mutation attempted.");
                writer.WriteLine("exit");
                return 5;
            }
            fixed (char* syntheticTextPointer = SyntheticText)
                valuePattern.SetValue(new BSTR(syntheticTextPointer));
            var afterValue = valuePattern.get_CurrentValue().ToString();
            var foregroundAfterWrite = GetForegroundWindow();
            var focusedAfter = uiAutomation.GetFocusedElement();
            var focusUnchanged = uiAutomation.CompareElements(focusedBefore, focusedAfter).Value != 0;
            Console.WriteLine($"value_before='{beforeValue}'");
            Console.WriteLine($"value_after='{afterValue}'");
            Console.WriteLine($"foreground_after_write=0x{foregroundAfterWrite.ToInt64():X}");
            Console.WriteLine($"uia_focus_unchanged={focusUnchanged}");
            Console.WriteLine($"automation_autosetfocus={automation.get_AutoSetFocus().Value}");
            writer.WriteLine("exit");
            var passed = afterValue == SyntheticText && foregroundAfterWrite == priorForeground && focusUnchanged;
            Console.WriteLine(passed ? "PASS: cross-process synthetic UIA value write preserved foreground and focused element." : "FAIL: value or foreground/focus invariant did not hold.");
            return passed ? 0 : 6;
        }
        finally
        {
            if (!child.WaitForExit(5000))
            {
                child.Kill(entireProcessTree: true);
                child.WaitForExit(5000);
            }
        }
    }

    private static IUIAutomationElement? FindByAutomationId(IUIAutomationTreeWalker walker, IUIAutomationElement node, string wanted, int depth, ref int count)
    {
        if (depth > 12 || count >= 500) return null;
        count++;
        if (node.get_CurrentAutomationId().ToString() == wanted) return node;
        var child = walker.GetFirstChildElement(node);
        while (child is not null && count < 500)
        {
            var found = FindByAutomationId(walker, child, wanted, depth + 1, ref count);
            if (found is not null) return found;
            child = walker.GetNextSiblingElement(child);
        }
        return null;
    }
}

internal sealed class SyntheticWindow : Form
{
    public SyntheticWindow()
    {
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Location = new System.Drawing.Point(-32000, -32000);
        Size = new System.Drawing.Size(320, 100);
        FormBorderStyle = FormBorderStyle.None;
        Opacity = 0;
    }
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= 0x08000000 | 0x00000080; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
            return parameters;
        }
    }
}
