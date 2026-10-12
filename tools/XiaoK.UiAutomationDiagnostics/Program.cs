using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
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

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--target")
            return RunSyntheticTarget(args[1]);
        if (args.Length != 0)
        {
            Console.Error.WriteLine("FAIL: unexpected probe arguments; refusing to start another client.");
            return 9;
        }
        return RunProbe();
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
