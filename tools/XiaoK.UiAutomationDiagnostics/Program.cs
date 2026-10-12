using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;
using XiaoK.UiAutomation;

internal static class Program
{
    private const string AutomationId = "XiaoK.Synthetic.Compose";
    private const string SyntheticText = "XiaoK synthetic cross-process background UIA value";

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern unsafe int EnumChildWindows(IntPtr parent, delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int> callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern unsafe int GetClassName(IntPtr hwnd, char* className, int maximumCount);

    private sealed class NativeChildWindowSummary
    {
        public const int MaximumWindows = 5_000;
        public int Total { get; set; }
        public int StandardEdit { get; set; }
        public int RichEdit { get; set; }
        public int Button { get; set; }
        public int Other { get; set; }
        public int ClassQueryFailures { get; set; }
        public int CallbackFailures { get; set; }
        public bool CallbackFailed { get; set; }
        public bool Truncated { get; set; }
    }

    private sealed record WindowCandidate(IntPtr Handle, bool IsVisible);

    private sealed class WindowEnumerationState(HashSet<int> processIds, List<WindowCandidate> windows, bool includeHidden)
    {
        public const int MaximumTopLevelWindows = 32;
        public HashSet<int> ProcessIds { get; } = processIds;
        public List<WindowCandidate> Windows { get; } = windows;
        public bool IncludeHidden { get; } = includeHidden;
        public bool Truncated { get; set; }
    }

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
            if (windowProcessId <= int.MaxValue && state.ProcessIds.Contains((int)windowProcessId))
            {
                var isVisible = PInvoke.IsWindowVisible(hwnd);
                if (state.IncludeHidden || isVisible)
                {
                    if (state.Windows.Count < WindowEnumerationState.MaximumTopLevelWindows)
                        state.Windows.Add(new WindowCandidate((IntPtr)hwnd, isVisible));
                    else
                        state.Truncated = true;
                }
            }
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
            return RunLiveAppProbe(args[1], includeHidden: false);
        if (args.Length == 2 && args[0] == "--inspect-live-app-including-hidden")
            return RunLiveAppProbe(args[1], includeHidden: true);
        if (args.Length == 2 && args[0] == "--inspect-live-app-worker")
            return RunLiveAppProbeWorker(args[1], includeHidden: false);
        if (args.Length == 3 && args[0] == "--inspect-live-app-worker-including-hidden")
            return RunLiveAppProbeWorker(args[1], bool.TryParse(args[2], out var includeHidden) && includeHidden);
        if (args.Length != 0)
        {
            Console.Error.WriteLine("FAIL: unexpected probe arguments; refusing to start another client.");
            return 9;
        }
        return RunProbe();
    }

    private static int RunLiveAppProbe(string app, bool includeHidden)
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
        start.ArgumentList.Add(includeHidden ? "--inspect-live-app-worker-including-hidden" : "--inspect-live-app-worker");
        start.ArgumentList.Add(processName);
        if (includeHidden) start.ArgumentList.Add(bool.TrueString);

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

    private static int RunLiveAppProbeWorker(string processName, bool includeHidden)
    {
        if (!TryGetTargetApp(processName, out var expectedProcessName, out var expectedPath))
        {
            Console.WriteLine("status=invalid_app");
            return 10;
        }

        try
        {
            var matchingProcessIds = new HashSet<int>();
            var windows = new List<WindowCandidate>();
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

            var enumerationState = new WindowEnumerationState(matchingProcessIds, windows, includeHidden);
            var enumerationStateHandle = GCHandle.Alloc(enumerationState);
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

            var visibleWindowCount = windows.Count(window => window.IsVisible);
            if (windows.Count == 0)
            {
                Console.WriteLine($"status={(includeHidden ? "no_top_level_window" : "no_visible_window")}; matching_process_count={matchingProcessIds.Count}; message_content_read=false");
                return 13;
            }

            var windowsToScan = includeHidden
                ? windows.Take(WindowEnumerationState.MaximumTopLevelWindows).ToArray()
                : windows.Where(window => window.IsVisible).Take(4).ToArray();

            var foregroundBefore = GetForegroundWindow();
            var nativeChildWindows = new NativeChildWindowSummary();
            foreach (var window in windowsToScan)
            {
                CountNativeChildWindowClasses(window.Handle, nativeChildWindows);
                if (nativeChildWindows.Truncated) break;
            }

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
            var patternCounts = new Dictionary<UIA_PATTERN_ID, int>();
            var valuePatternMetadata = new Dictionary<string, int>(StringComparer.Ordinal);
            var invokeButtonMetadata = new Dictionary<string, int>(StringComparer.Ordinal);
            var automationIdSemanticCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var legacyRoleCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var automationIdCount = 0;
            var valuePatternCount = 0;
            var invokePatternCount = 0;
            var scannedNodes = 0;
            var windowsScanned = 0;
            foreach (var window in windowsToScan)
            {
                var root = automation.ElementFromHandle(new HWND(window.Handle));
                CountStructureOnly(walker, root, 0, ref scannedNodes, controlCounts, patternCounts, valuePatternMetadata,
                    invokeButtonMetadata, automationIdSemanticCounts, legacyRoleCounts,
                    ref automationIdCount, ref valuePatternCount, ref invokePatternCount);
                windowsScanned++;
                if (scannedNodes >= 1000) break;
            }

            var foregroundAfter = GetForegroundWindow();
            var focusedAfter = uiAutomation.GetFocusedElement();
            var foregroundUnchanged = foregroundBefore == foregroundAfter;
            var focusUnchanged = uiAutomation.CompareElements(focusedBefore, focusedAfter).Value != 0;
            Console.WriteLine($"status={(foregroundUnchanged && focusUnchanged ? "READ_ONLY_SCAN_COMPLETE" : "FOCUS_INVARIANT_FAILED")}");
            Console.WriteLine($"target_process_count={matchingProcessIds.Count}");
            Console.WriteLine($"top_level_windows_detected={windows.Count}");
            Console.WriteLine($"visible_top_level_windows_detected={visibleWindowCount}");
            Console.WriteLine($"hidden_top_level_windows_detected={(includeHidden ? (windows.Count - visibleWindowCount).ToString() : "not_scanned")}");
            Console.WriteLine($"top_level_window_enumeration_truncated={enumerationState.Truncated}");
            Console.WriteLine($"windows_scanned={windowsScanned}");
            Console.WriteLine($"scan_included_hidden_windows={includeHidden}");
            Console.WriteLine($"uia_nodes_scanned={scannedNodes}");
            Console.WriteLine($"control_types={FormatControlCounts(controlCounts)}");
            Console.WriteLine($"supported_uia_patterns={FormatPatternCounts(patternCounts)}");
            Console.WriteLine($"value_pattern_metadata={FormatMetadataCounts(valuePatternMetadata)}");
            Console.WriteLine($"invoke_button_metadata={FormatMetadataCounts(invokeButtonMetadata)}");
            Console.WriteLine($"automation_id_semantic_matches={FormatMetadataCounts(automationIdSemanticCounts)}");
            Console.WriteLine($"legacy_accessible_role_ids={FormatMetadataCounts(legacyRoleCounts)}");
            Console.WriteLine($"nodes_with_automation_id={automationIdCount}");
            Console.WriteLine($"edit_value_patterns_available={valuePatternCount}");
            Console.WriteLine($"button_invoke_patterns_available={invokePatternCount}");
            Console.WriteLine($"native_child_windows_scanned={nativeChildWindows.Total}");
            Console.WriteLine($"native_standard_edit_class_count={nativeChildWindows.StandardEdit}");
            Console.WriteLine($"native_rich_edit_class_count={nativeChildWindows.RichEdit}");
            Console.WriteLine($"native_button_class_count={nativeChildWindows.Button}");
            Console.WriteLine($"native_other_child_class_count={nativeChildWindows.Other}");
            Console.WriteLine($"native_child_class_query_failures={nativeChildWindows.ClassQueryFailures}");
            Console.WriteLine($"native_child_callback_failures={nativeChildWindows.CallbackFailures}");
            Console.WriteLine($"native_child_callback_exception_occurred={nativeChildWindows.CallbackFailed}");
            Console.WriteLine($"native_child_enumeration_truncated={nativeChildWindows.Truncated}");
            Console.WriteLine($"foreground_unchanged={foregroundUnchanged}");
            Console.WriteLine($"uia_focus_unchanged={focusUnchanged}");
            Console.WriteLine("window_titles_read=false; control_names_read=false; text_values_read=false; native_child_class_names_read_in_memory=true; exact_native_child_class_names_output=false; native_child_class_names_persisted=false; screenshots_taken=false; input_sent=false; message_content_read=false");
            return foregroundUnchanged && focusUnchanged ? 0 : 15;
        }
        catch (COMException)
        {
            Console.WriteLine("status=uia_provider_error; message_content_read=false");
            return 16;
        }
    }

    private static void CountNativeChildWindowClasses(IntPtr parent, NativeChildWindowSummary summary)
    {
        var summaryHandle = GCHandle.Alloc(summary);
        try
        {
            unsafe
            {
                // Win32 documents EnumChildWindows' return value as unused; track only explicit callback failures and truncation.
                _ = EnumChildWindows(parent, &EnumerateNativeChildWindow, GCHandle.ToIntPtr(summaryHandle));
            }
        }
        finally
        {
            summaryHandle.Free();
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe int EnumerateNativeChildWindow(IntPtr hwnd, IntPtr lParam)
    {
        try
        {
            var handle = GCHandle.FromIntPtr(lParam);
            if (handle.Target is not NativeChildWindowSummary summary) return 0;
            if (summary.Total >= NativeChildWindowSummary.MaximumWindows)
            {
                summary.Truncated = true;
                return 0;
            }

            summary.Total++;
            var classBuffer = stackalloc char[256];
            var length = GetClassName(hwnd, classBuffer, 256);
            if (length <= 0)
            {
                summary.ClassQueryFailures++;
                return 1;
            }

            var className = new string(classBuffer, 0, length);
            if (className.Equals("Edit", StringComparison.OrdinalIgnoreCase))
                summary.StandardEdit++;
            else if (className.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase))
                summary.RichEdit++;
            else if (className.Equals("Button", StringComparison.OrdinalIgnoreCase))
                summary.Button++;
            else
                summary.Other++;

            return 1;
        }
        catch
        {
            // Never allow a managed exception to cross the unmanaged callback boundary.
            try
            {
                if (GCHandle.FromIntPtr(lParam).Target is NativeChildWindowSummary summary)
                {
                    summary.CallbackFailures++;
                    summary.CallbackFailed = true;
                }
            }
            catch
            {
                // The callback must still fail closed if the state handle is invalid.
            }
            return 0;
        }
    }

    private static void CountStructureOnly(
        IUIAutomationTreeWalker walker,
        IUIAutomationElement node,
        int depth,
        ref int scannedNodes,
        Dictionary<UIA_CONTROLTYPE_ID, int> controlCounts,
        Dictionary<UIA_PATTERN_ID, int> patternCounts,
        Dictionary<string, int> valuePatternMetadata,
        Dictionary<string, int> invokeButtonMetadata,
        Dictionary<string, int> automationIdSemanticCounts,
        Dictionary<string, int> legacyRoleCounts,
        ref int automationIdCount,
        ref int valuePatternCount,
        ref int invokePatternCount)
    {
        if (depth > 10 || scannedNodes >= 1000) return;
        scannedNodes++;

        var controlType = node.get_CurrentControlType();
        controlCounts[controlType] = controlCounts.GetValueOrDefault(controlType) + 1;
        var automationId = node.get_CurrentAutomationId().ToString();
        var hasAutomationId = !string.IsNullOrEmpty(automationId);
        if (hasAutomationId)
        {
            automationIdCount++;
            var category = ClassifyAutomationId(automationId!);
            if (category is not null)
                automationIdSemanticCounts[category] = automationIdSemanticCounts.GetValueOrDefault(category) + 1;
        }

        foreach (var patternId in Enum.GetValues<UIA_PATTERN_ID>())
        {
            try
            {
                var pattern = node.GetCurrentPattern(patternId);
                if (pattern is not null)
                {
                    patternCounts[patternId] = patternCounts.GetValueOrDefault(patternId) + 1;
                    if (patternId == UIA_PATTERN_ID.UIA_ValuePatternId && controlType == UIA_CONTROLTYPE_ID.UIA_EditControlTypeId)
                        valuePatternCount++;
                    if (patternId == UIA_PATTERN_ID.UIA_InvokePatternId && controlType == UIA_CONTROLTYPE_ID.UIA_ButtonControlTypeId)
                    {
                        invokePatternCount++;
                        var enabled = node.get_CurrentIsEnabled().Value != 0;
                        var offscreen = node.get_CurrentIsOffscreen().Value != 0;
                        var key = $"Button:enabled={enabled}:offscreen={offscreen}:automation_id={hasAutomationId}";
                        invokeButtonMetadata[key] = invokeButtonMetadata.GetValueOrDefault(key) + 1;
                    }
                    if (patternId == UIA_PATTERN_ID.UIA_ValuePatternId && pattern is IUIAutomationValuePattern valuePattern)
                    {
                        var readOnly = valuePattern.get_CurrentIsReadOnly().Value != 0;
                        var enabled = node.get_CurrentIsEnabled().Value != 0;
                        var offscreen = node.get_CurrentIsOffscreen().Value != 0;
                        var key = $"{controlType}:read_only={readOnly}:enabled={enabled}:offscreen={offscreen}:automation_id={hasAutomationId}";
                        valuePatternMetadata[key] = valuePatternMetadata.GetValueOrDefault(key) + 1;
                    }
                    if (patternId == UIA_PATTERN_ID.UIA_LegacyIAccessiblePatternId
                        && pattern is IUIAutomationLegacyIAccessiblePattern legacyPattern)
                    {
                        var roleId = legacyPattern.get_CurrentRole();
                        var key = FormatLegacyRoleId(roleId);
                        legacyRoleCounts[key] = legacyRoleCounts.GetValueOrDefault(key) + 1;
                    }
                }
            }
            catch (COMException)
            {
                // Pattern operations are never invoked; only read-only availability/state metadata is queried.
            }
        }

        var child = walker.GetFirstChildElement(node);
        while (child is not null && scannedNodes < 1000)
        {
            CountStructureOnly(walker, child, depth + 1, ref scannedNodes, controlCounts, patternCounts, valuePatternMetadata,
                invokeButtonMetadata, automationIdSemanticCounts, legacyRoleCounts,
                ref automationIdCount, ref valuePatternCount, ref invokePatternCount);
            child = walker.GetNextSiblingElement(child);
        }
    }

    private static string? ClassifyAutomationId(string automationId)
    {
        var value = automationId.ToLowerInvariant();
        if (value.Contains("send", StringComparison.Ordinal) || value.Contains("submit", StringComparison.Ordinal)
            || value.Contains("发送", StringComparison.Ordinal) || value.Contains("提交", StringComparison.Ordinal))
            return "send_action";
        if (value.Contains("compose", StringComparison.Ordinal) || value.Contains("input", StringComparison.Ordinal)
            || value.Contains("editor", StringComparison.Ordinal) || value.Contains("textinput", StringComparison.Ordinal)
            || value.Contains("输入", StringComparison.Ordinal) || value.Contains("编辑", StringComparison.Ordinal))
            return "compose_surface";
        if (value.Contains("message", StringComparison.Ordinal) || value.Contains("msg", StringComparison.Ordinal)
            || value.Contains("消息", StringComparison.Ordinal) || value.Contains("会话", StringComparison.Ordinal))
            return "message_related";
        return null;
    }

    private static string FormatLegacyRoleId(uint roleId) => roleId switch
    {
        0x0001 => "TITLEBAR",
        0x0002 => "MENUBAR",
        0x000A => "CLIENT",
        0x000C => "MENUITEM",
        0x0010 => "PANE",
        0x0021 => "LIST",
        0x0022 => "LISTITEM",
        0x002A => "TEXT",
        0x002B => "PUSHBUTTON",
        _ => $"ROLE_0x{roleId:X4}"
    };

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
        return string.Join(',', counts.OrderBy(pair => (int)pair.Key).Select(pair =>
        {
            var name = names.TryGetValue(pair.Key, out var knownName) ? knownName : $"UIAControlType{(int)pair.Key}";
            return $"{name}-{pair.Value}";
        }));
    }

    private static string FormatPatternCounts(Dictionary<UIA_PATTERN_ID, int> counts) =>
        counts.Count == 0
            ? "none"
            : string.Join(',', counts.OrderBy(pair => (int)pair.Key).Select(pair => $"{pair.Key}={pair.Value}"));

    private static string FormatMetadataCounts(Dictionary<string, int> counts) =>
        counts.Count == 0
            ? "none"
            : string.Join(',', counts.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"));

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
            var writeResult = SafeUiAutomationTextWriter.SetValue(
                (IntPtr)targetHwnd, child.Id, AutomationId, SyntheticText, priorForeground);
            if (!writeResult.Success)
            {
                Console.WriteLine($"FAIL: production UIA writer returned {writeResult.ErrorCode}; uncertain={writeResult.OutcomeUncertain}.");
                writer.WriteLine("exit");
                return 6;
            }
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
