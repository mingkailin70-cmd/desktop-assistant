using XiaoK.Core;
using XiaoK.Host;
using XiaoK.Storage;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using XiaoK.Adapters.Windows;

if (args.Length == 2 && args[0] == "--uia-readonly-worker" && args[1] == "--test-block")
{
    await Task.Delay(Timeout.Infinite);
    return;
}

if (args.Length == 1 && args[0] == "--uia-worker-job-owner-test")
{
    var executablePath = Environment.ProcessPath
        ?? throw new InvalidOperationException("无法定位UIA工作进程测试宿主。");
    var workerStartInfo = new ProcessStartInfo(executablePath)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        WindowStyle = ProcessWindowStyle.Hidden
    };
    workerStartInfo.ArgumentList.Add("--uia-readonly-worker");
    workerStartInfo.ArgumentList.Add("--test-block");
    using var worker = new SystemUiAutomationWorkerProcessStarter().Start(workerStartInfo)
        ?? throw new InvalidOperationException("无法启动用于Job Object回收探针的辅助进程。");
    Console.WriteLine(worker.Process.Id);
    Console.Out.Flush();
    await Task.Delay(Timeout.Infinite);
    return;
}

if (args.Length == 1 && args[0] == "--uia-readonly-worker")
{
    Environment.ExitCode = await WindowsUiAutomationWorkerEntryPoint.RunAsync();
    return;
}

if (args.Length == 1 && args[0] == "--only-uia-readonly-probe")
{
    await UiAutomationReadOnlyProbe.RunAsync();
    Console.WriteLine("通过：离屏合成窗口的UIA只读探针未触发控件或前台；取消、5秒超时及所有者进程退出均回收工作进程。");
    return;
}

if (args.Length == 1 && args[0] == "--only-runtime-task-history-recovery")
{
    await XiaoK.DesktopExperience.Checks.RuntimeTaskHistoryRecoveryProbe.RunAsync();
    Console.WriteLine("通过：生产AssistantRuntime从隔离SQLite恢复任务历史；旧动作与审批只显示核对状态，不入队或重放。");
    return;
}

var checks = 0;
void Require(bool passed, string message)
{
    if (!passed)
    {
        var annotationMessage = message.Replace("%", "%25", StringComparison.Ordinal)
            .Replace("\r", "%0D", StringComparison.Ordinal)
            .Replace("\n", "%0A", StringComparison.Ordinal);
        Console.WriteLine($"::error title=Desktop experience check::{annotationMessage}");
        throw new InvalidOperationException(message);
    }
    checks++;
}
static bool Near(double left, double right) => Math.Abs(left - right) < 0.00001;

foreach (var percent in new[] { 40d, 70d, 100d, 160d })
foreach (var dpi in new uint[] { 96, 144, 192 })
{
    var size = PetSizingPolicy.FitToWorkArea(percent, 1920, 1080, dpi);
    Require(Near(size.WidthDip / size.HeightDip, PetSizingPolicy.BaseWidthDip / PetSizingPolicy.BaseHeightDip), "桌宠宽高比改变。");
    Require(size.WidthDip * dpi / 96 <= 1920 && size.HeightDip * dpi / 96 <= 1080,
        "桌宠超出工作区。");
}
var tiny = PetSizingPolicy.FitToWorkArea(160, 100, 100, 192);
Require(Near(tiny.HeightDip, 50)
    && Near(tiny.WidthDip / tiny.HeightDip, PetSizingPolicy.BaseWidthDip / PetSizingPolicy.BaseHeightDip),
    "小屏幕裁剪错误。");
Require(PetSizingPolicy.ClampPercent(12) == 40 && PetSizingPolicy.ClampPercent(300) == 160, "缩放限值错误。");
Require(!PetSizingPolicy.IsValidPercent(double.NaN) && !PetSizingPolicy.IsValidPercent(double.PositiveInfinity), "接受非有限缩放。");
var petNudgeCases = new[]
{
    (Direction: PetWindowNudgeDirection.Left, Expected: (-80, 0)),
    (Direction: PetWindowNudgeDirection.Right, Expected: (80, 0)),
    (Direction: PetWindowNudgeDirection.Up, Expected: (0, -80)),
    (Direction: PetWindowNudgeDirection.Down, Expected: (0, 80))
};
foreach (var testCase in petNudgeCases)
    Require(PetWindowInteractionPolicy.GetKeyboardNudgeOffsetDip(testCase.Direction) == testCase.Expected,
        "桌宠方向微移未提供对称的80-DIP位置调整。");
var petNudgePixelSteps = new Dictionary<uint, int>
{
    [96] = 80,
    [120] = 100,
    [144] = 120,
    [168] = 140,
    [192] = 160
};
foreach (var (dpi, expectedStep) in petNudgePixelSteps)
foreach (var testCase in petNudgeCases)
{
    var horizontalSign = Math.Sign(testCase.Expected.Item1);
    var verticalSign = Math.Sign(testCase.Expected.Item2);
    var expectedPixels = (horizontalSign * expectedStep, verticalSign * expectedStep);
    Require(PetWindowInteractionPolicy.GetKeyboardNudgeOffsetPixels(testCase.Direction, dpi) == expectedPixels,
        $"桌宠方向微移在{dpi} DPI下没有保持80 DIP的实际位移。");
}
var rejectedZeroDpi = false;
try { _ = PetWindowInteractionPolicy.GetKeyboardNudgeOffsetPixels(PetWindowNudgeDirection.Right, 0); }
catch (ArgumentOutOfRangeException) { rejectedZeroDpi = true; }
Require(rejectedZeroDpi, "桌宠方向微移接受了无效的0 DPI。");

Require(PetPosePresentationPolicy.FromStatus("正在采集麦克风") == XiaoKPetPose.Listening
    && PetPosePresentationPolicy.FromStatus("唤醒监听已开启") == XiaoKPetPose.Listening
    && PetPosePresentationPolicy.FromStatus("执行中") == XiaoKPetPose.Executing
    && PetPosePresentationPolicy.FromStatus("正在分析请求") == XiaoKPetPose.Thinking
    && PetPosePresentationPolicy.FromStatus("待命") == XiaoKPetPose.Idle,
    "桌宠状态没有映射到对应姿态。");
foreach (var pose in Enum.GetValues<XiaoKPetPose>())
{
    var motion = PetPosePresentationPolicy.GetMotionProfile(pose);
    Require(double.IsFinite(motion.ScaleX) && double.IsFinite(motion.ScaleY) && double.IsFinite(motion.OffsetY)
        && motion.ScaleX is >= 0.98 and <= 1.03 && motion.ScaleY is >= 0.98 and <= 1.03
        && Math.Abs(motion.OffsetY) <= 2,
        "桌宠姿态过渡超出轻量动效范围。");
}
Require(!PetPosePresentationPolicy.ShouldAnimate(null, XiaoKPetPose.Idle, true, true)
    && !PetPosePresentationPolicy.ShouldAnimate(XiaoKPetPose.Idle, XiaoKPetPose.Idle, true, true)
    && !PetPosePresentationPolicy.ShouldAnimate(XiaoKPetPose.Idle, XiaoKPetPose.Listening, false, true)
    && !PetPosePresentationPolicy.ShouldAnimate(XiaoKPetPose.Idle, XiaoKPetPose.Listening, true, false)
    && PetPosePresentationPolicy.ShouldAnimate(XiaoKPetPose.Idle, XiaoKPetPose.Listening, true, true),
    "桌宠动画没有遵循状态变化、可见性或系统动画设置。");

var root = Path.Combine(Path.GetTempPath(), "XiaoK-pet-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var store = new PetWindowPositionStore(root);
    var path = Path.Combine(root, PetWindowPositionStore.FileName);
    var settings = Path.Combine(root, "settings.json");
    File.WriteAllText(settings, "不改通用设置");
    Require(!store.TryLoad(out _), "未保存时加载了位置。");
    store.Save(-100, 240, 55);
    Require(store.TryLoad(out var saved) && saved == new PetWindowPosition(-100, 240, 55), "缩放保存不正确。");
    store.Save(200, -50, 160);
    Require(store.TryLoad(out saved) && saved.ScalePercent == 160 && saved.LeftPixels == 200, "原子替换失败。");
    Require(File.ReadAllText(settings) == "不改通用设置", "位置修改影响通用设置。");
    File.WriteAllText(path, "{\"leftPixels\":1,\"topPixels\":2}");
    Require(store.TryLoad(out saved) && saved.ScalePercent == 100, "老版位置没有恢复原尺寸。");
    foreach (var invalid in new[]
    {
        "{\"leftPixels\":1,\"topPixels\":2,\"scalePercent\":39}",
        "{\"leftPixels\":1,\"topPixels\":2,\"scalePercent\":161}",
        "{\"leftPixels\":1,\"topPixels\":2,\"scalePercent\":\"70\"}",
        "{\"leftPixels\":1,\"topPixels\":2,\"scalePercent\":70,\"scalePercent\":100}",
        "{\"leftPixels\":1,\"topPixels\":2,\"extra\":70}",
        "{\"leftPixels\":1,\"topPixels\":2,\"scalePercent\":null}"
    })
    {
        File.WriteAllText(path, invalid);
        Require(!store.TryLoad(out _), "接受了损坏或越界的缩放数据。");
    }
    store.Save(1, 2, 70);
    var rejected = false;
    try { store.Save(1, 2, double.NaN); }
    catch (ArgumentOutOfRangeException) { rejected = true; }
    Require(rejected && store.TryLoad(out saved) && saved.ScalePercent == 70, "无效缩放覆盖有效值。");
    Require(!Directory.EnumerateFiles(root, "*.tmp").Any(), "残留临时文件。");
}
finally
{
    // 仅清理本次创建的随机测试目录。
    if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("测试清理目录越界。");
    Directory.Delete(root, recursive: true);
}

foreach (var tool in new[] { "app.launch.v1", "window.activate.v1" })
{
    Require(ToolInteractionPolicy.Check(tool, ToolExecutionAccess.BackgroundOnly)?.ErrorCode == "FOREGROUND_REQUIRED",
        "后台调用放行需要前台的工具。");
    Require(ToolInteractionPolicy.Check(tool, ToolExecutionAccess.ExplicitUserInteraction) is null,
        "阻止明确的用户交互。");
}
foreach (var tool in new[] { "file.search.v1", "file.copy.v1", "file.archive.single.v1", "file.archive.directory.v1", "file.rename.v1", "browser.read.public.v1", "browser.read.dynamic.public.v1", "browser.download.public.v1", "browser.session.open.v1", "browser.session.navigate.v1", "browser.session.snapshot.v1", "browser.session.click-button.v1", "browser.session.fill-text.v1", "browser.session.close.v1", "code.inspect.v1", "code.task.create.v1", "message.analyze.v1", "message.notice.analyze.v1", "message.draft.v1", "message.send.v1", ApplicationUiAutomationPolicy.WaitForEnabledToolId, ApplicationUiAutomationPolicy.ScopedSnapshotToolId })
    Require(ToolInteractionPolicy.Check(tool, ToolExecutionAccess.BackgroundOnly) is null, "阻止后台工具。");
Require(ToolInteractionPolicy.GetOperation(ApplicationUiAutomationPolicy.ToolId) == ToolInteractionOperation.ReadOnlyTarget
    && ToolInteractionPolicy.GetMode(ApplicationUiAutomationPolicy.ToolId) == ToolInteractionMode.Background
    && ToolInteractionPolicy.Check(ApplicationUiAutomationPolicy.ToolId, ToolExecutionAccess.BackgroundOnly) is null,
    "UIA只读快照没有进入同目标只读仲裁路径。");
Require(ToolInteractionPolicy.GetOperation(ApplicationUiAutomationPolicy.ScopedSnapshotToolId) == ToolInteractionOperation.ReadOnlyTarget
    && ToolInteractionPolicy.GetMode(ApplicationUiAutomationPolicy.ScopedSnapshotToolId) == ToolInteractionMode.Background
    && ToolInteractionPolicy.Check(ApplicationUiAutomationPolicy.ScopedSnapshotToolId, ToolExecutionAccess.BackgroundOnly) is null,
    "窄范围UIA元数据检查没有进入同目标只读仲裁路径。");
Require(ApplicationUiAutomationPolicy.CreateUserToolProposal("读取界面控件：微信", []) is null,
    "消息客户端仍能通过通用整窗快照读取可能包含的会话内容。");
var scopedSnapshotProposal = ApplicationUiAutomationPolicy.CreateScopedSnapshotToolProposal(
    "检查界面区域：Edge|Editor.Pane|Pane", []);
Require(scopedSnapshotProposal?.ToolId == ApplicationUiAutomationPolicy.ScopedSnapshotToolId
    && scopedSnapshotProposal.Target == "edge"
    && scopedSnapshotProposal.Arguments.GetValueOrDefault("automation_id") == "Editor.Pane"
    && scopedSnapshotProposal.Arguments.GetValueOrDefault("root_control_type") == "Pane"
    && scopedSnapshotProposal.Preconditions == (ToolPrecondition.ApplicationAllowlisted | ToolPrecondition.ExistingWindow)
    && scopedSnapshotProposal.ExpectedOutcome == ToolExpectedOutcome.ApplicationUiSnapshotReturned,
    "明确容器控件的窄范围UIA提案没有绑定固定应用、ID和类型。");
Require(ToolInteractionPolicy.GetOperation(ApplicationUiAutomationPolicy.WaitForEnabledToolId) == ToolInteractionOperation.ReadOnlyTarget
    && ToolInteractionPolicy.GetMode(ApplicationUiAutomationPolicy.WaitForEnabledToolId) == ToolInteractionMode.Background,
    "控件状态等待没有进入只读后台仲裁路径。");
var uiWaitProposal = ApplicationUiAutomationPolicy.CreateWaitForEnabledToolProposal(
    "等待界面控件可用：微信|SendButton_1", []);
Require(uiWaitProposal?.ToolId == ApplicationUiAutomationPolicy.WaitForEnabledToolId
    && uiWaitProposal.Target == "wechat"
    && uiWaitProposal.Arguments.GetValueOrDefault("automation_id") == "SendButton_1"
    && uiWaitProposal.ExpectedOutcome == ToolExpectedOutcome.ApplicationUiConditionObserved,
    "显式UIA状态等待没有绑定白名单应用和AutomationId。");
Require(ApplicationUiAutomationPolicy.CreateWaitForEnabledToolProposal(
        "等待界面控件可用：未知应用|SendButton", []) is null
    && ApplicationUiAutomationPolicy.CreateWaitForEnabledToolProposal(
        "等待界面控件可用：微信|SendButton|Other", []) is null
    && ApplicationUiAutomationPolicy.CreateWaitForEnabledToolProposal(
        "等待界面控件可用：微信|Send Button", []) is null,
    "UIA状态等待接受了未知目标、歧义分隔符或不安全AutomationId。");
Require(ApplicationUiAutomationPolicy.CreateUserToolProposal("读取界面控件：未知应用", []) is null
    && ApplicationUiAutomationPolicy.CreateUserToolProposal("读取界面控件：微信\n并打开网页", []) is null,
    "UIA快照接受了未知目标或多行扩展指令。");
Require(ApplicationUiAutomationPolicy.CreateScopedSnapshotToolProposal("检查界面区域：未知应用|Root|Pane", []) is null
    && ApplicationUiAutomationPolicy.CreateScopedSnapshotToolProposal("检查界面区域：Edge|bad id|Pane", []) is null
    && ApplicationUiAutomationPolicy.CreateScopedSnapshotToolProposal("检查界面区域：Edge|Root|Window", []) is null
    && ApplicationUiAutomationPolicy.CreateScopedSnapshotToolProposal("检查界面区域：Edge|Root|Pane|Other", []) is null
    && ApplicationUiAutomationPolicy.CreateScopedSnapshotToolProposal("检查界面区域：微信|Root|Pane", []) is null,
    "窄范围UIA接受了未知应用、无效ID、不允许的根类型、歧义分隔符或消息客户端。");
Require(TaskCategoryCatalog.TryGetLabel("app-ui-snapshot", out var uiSnapshotLabel)
    && uiSnapshotLabel == "只读界面检查", "任务中心没有显示只读界面检查类别。");
Require(TaskCategoryCatalog.TryGetLabel("app-ui-scoped-snapshot", out var scopedSnapshotLabel)
    && scopedSnapshotLabel == "指定控件区域检查", "任务中心没有显示窄范围控件区域检查类别。");
Require(TaskCategoryCatalog.TryGetLabel("app-ui-wait-enabled", out var uiWaitLabel)
    && uiWaitLabel == "等待界面控件状态", "任务中心没有显示UIA状态等待类别。");
Require(TaskCategoryCatalog.TryGetLabel("app-ui-set-text", out var uiSetTextLabel)
    && uiSetTextLabel == "应用界面文本填写", "任务中心没有显示应用界面文本填写类别。");
Require(TaskCategoryCatalog.LabelOrConversation(null) == "本地对话"
    && TaskCategoryCatalog.LabelOrConversation("") == "本地对话"
    && TaskCategoryCatalog.LabelOrConversation("UNKNOWN_PRIVATE_CATEGORY") == "未分类任务",
    "未知任务类别被误标为本地对话，或原始类别字段可能直接显示到任务中心。");
Require(ToolInteractionPolicy.Check("arbitrary.shell", ToolExecutionAccess.ExplicitUserInteraction)?.ErrorCode == "UNKNOWN_TOOL",
    "未知工具被放行。");
Require(ToolInteractionPolicy.Check("file.search.v1", (ToolExecutionAccess)999)?.ErrorCode == "INVALID_EXECUTION_CONTEXT",
    "无效执行上下文被放行。");

var repositoryDirectory = new DirectoryInfo(AppContext.BaseDirectory);
while (repositoryDirectory is not null && !File.Exists(Path.Combine(repositoryDirectory.FullName, "XiaoK.sln")))
    repositoryDirectory = repositoryDirectory.Parent;
Require(repositoryDirectory is not null, "无法从桌面交互检查目录定位仓库根目录。");
foreach (var pose in Enum.GetValues<XiaoKPetPose>())
{
    var asset = PetPosePresentationPolicy.GetFallbackAssetFileName(pose);
    Require(File.Exists(Path.Combine(repositoryDirectory!.FullName, "src", "XiaoK.Host", "Assets", "Pets", asset)),
        "桌宠静态回退姿态资源缺失。");
}
var mainWindowXaml = File.ReadAllText(Path.Combine(repositoryDirectory!.FullName,
    "src", "XiaoK.Host", "MainWindow.xaml"));
var mainWindowCode = File.ReadAllText(Path.Combine(repositoryDirectory.FullName,
    "src", "XiaoK.Host", "MainWindow.xaml.cs"));
const string corgiIdleUri = "pack://siteoforigin:,,,/Assets/Pets/keke-corgi-v1/idle/00_420.png";
Require(!mainWindowXaml.Contains("xiaok-silver-shaded-3d-v15.png", StringComparison.OrdinalIgnoreCase)
    && mainWindowXaml.Split(corgiIdleUri, StringSplitOptions.None).Length - 1 == 3
    && mainWindowXaml.Contains("红白柯基伙伴 · 本地运行", StringComparison.Ordinal)
    && mainWindowXaml.Contains("小K红白柯基角色", StringComparison.Ordinal),
    "展开面板仍混用旧猫图或桌宠角色文案与柯基不一致。");
Require(Regex.IsMatch(mainWindowXaml,
        "<Border x:Name=\"PetStatusBadge\"[^>]*Visibility=\"Collapsed\"", RegexOptions.Singleline)
    && PetSizingPolicy.BaseHeightDip == 324
    && mainWindowXaml.Contains("<Grid Width=\"300\" Height=\"324\" Margin=\"0\">", StringComparison.Ordinal)
    && mainWindowXaml.Contains("x:Name=\"PetResizeHandle\" Width=\"24\" Height=\"24\"", StringComparison.Ordinal)
    && !mainWindowXaml.Contains("拖动移动 · 单击展开", StringComparison.Ordinal)
    && !mainWindowXaml.Contains("小K爪印标记", StringComparison.Ordinal)
    && !mainWindowXaml.Contains("#2EC48E58", StringComparison.Ordinal)
    && !mainWindowXaml.Contains("Color=\"#7A5940\"", StringComparison.Ordinal)
    && mainWindowCode.Contains("PetStatusBadge.Visibility = compactStatus == \"待命\" ? Visibility.Collapsed : Visibility.Visible;", StringComparison.Ordinal)
    && mainWindowCode.Contains("AutomationProperties.SetItemStatus(PetView, status);", StringComparison.Ordinal),
    "桌宠闲置态未收敛为透明柯基主体，或状态提示、缩放命中区域接线不完整。");
Require(Regex.IsMatch(mainWindowXaml, "<Trigger Property=\"IsKeyboardFocused\" Value=\"True\">", RegexOptions.Singleline)
    && mainWindowXaml.Contains("Header=\"微调桌宠位置\"", StringComparison.Ordinal)
    && new[] { "MovePetLeft_Click", "MovePetRight_Click", "MovePetUp_Click", "MovePetDown_Click" }
        .All(handler => mainWindowXaml.Contains($"Click=\"{handler}\"", StringComparison.Ordinal)
            && mainWindowCode.Contains($"private void {handler}", StringComparison.Ordinal))
    && mainWindowCode.Contains("CompactPetButton.ContextMenu = PetView.ContextMenu;", StringComparison.Ordinal)
    && Regex.IsMatch(mainWindowCode,
        @"GetKeyboardNudgeOffsetPixels\(\s*direction,\s*GetEffectiveDpi\(handle\)\)",
        RegexOptions.CultureInvariant)
    && mainWindowCode.Contains("SwpNoSize | SwpNoZOrder | SwpNoActivate", StringComparison.Ordinal),
    "桌宠键盘焦点提示、DPI感知的上下左右微移菜单或不激活位置保存接线不完整。");
var assistantRuntimeSource = File.ReadAllText(Path.Combine(repositoryDirectory!.FullName,
    "src", "XiaoK.Host", "AssistantRuntime.cs"));
var directToolRoutes = Regex.Matches(assistantRuntimeSource,
        @"_broker\.ExecuteAsync\(new ToolProposal\(""([^""]+)""")
    .Select(match => match.Groups[1].Value)
    .ToArray();
Require(directToolRoutes.SequenceEqual(new[] { "window.activate.v1", "app.launch.v1" }),
    "后台任务路由出现前台兼容执行入口；只有明确窗口切换和应用启动可走该入口。");
var requiredBackgroundRoutes = new[]
{
    "ExecuteBackgroundAsync(proposal, token)",
    "CreateWaitForEnabledToolProposal(request, GetConfiguredAppAliases())",
    "ExecuteBackgroundAsync(new ToolProposal(\"file.search.v1\"",
    "var tool = category == \"draft\" ? \"message.draft.v1\" : \"message.analyze.v1\";",
    "ExecuteBackgroundAsync(new ToolProposal(tool, arguments,",
    "ExecuteBackgroundAsync(new ToolProposal(\"message.send.v1\"",
    "ExecuteBackgroundAsync(new ToolProposal(\"browser.read.dynamic.public.v1\"",
    "ExecuteBackgroundAsync(new ToolProposal(\"browser.session.open.v1\"",
    "ExecuteBackgroundAsync(new ToolProposal(\"browser.session.navigate.v1\"",
    "ExecuteBackgroundAsync(new ToolProposal(\"browser.session.click-button.v1\"",
    "ExecuteBackgroundAsync(new ToolProposal(\"browser.session.fill-text.v1\"",
    "var toolId = isClose ? \"browser.session.close.v1\" : \"browser.session.snapshot.v1\";",
    "ExecuteBackgroundAsync(new ToolProposal(\"code.inspect.v1\"",
    "ExecuteBackgroundAsync(new ToolProposal(\"code.task.create.v1\""
};
Require(requiredBackgroundRoutes.All(route => assistantRuntimeSource.Contains(route, StringComparison.Ordinal)),
    "文件、消息或代码路由没有固定使用后台执行入口。");
Require(assistantRuntimeSource.IndexOf("lower.StartsWith(\"读取动态网页\")", StringComparison.Ordinal)
        < assistantRuntimeSource.IndexOf("lower.StartsWith(\"读取网页\")", StringComparison.Ordinal)
    && assistantRuntimeSource.Contains("if (category == \"web-read-dynamic\")", StringComparison.Ordinal),
    "只有显式“读取动态网页”命令才能进入启用内联脚本的独立浏览器路径。");
var taskCenterSource = File.ReadAllText(Path.Combine(repositoryDirectory.FullName,
    "src", "XiaoK.Host", "TaskHistoryWindow.xaml.cs"));
var taskCenterXaml = File.ReadAllText(Path.Combine(repositoryDirectory.FullName,
    "src", "XiaoK.Host", "TaskHistoryWindow.xaml"));
var taskCenterOpenStart = File.ReadAllText(Path.Combine(repositoryDirectory.FullName,
    "src", "XiaoK.Host", "MainWindow.xaml.cs"));
Require(taskCenterOpenStart.Contains("IsVisibleChanged += MainWindow_IsVisibleChanged;", StringComparison.Ordinal)
    && taskCenterOpenStart.Contains("if (e.NewValue is true) EnsurePetRendererInitialization();", StringComparison.Ordinal)
    && taskCenterOpenStart.Contains("!IsVisible || Opacity <= 0 || !PetAnimationViewbox.IsLoaded", StringComparison.Ordinal)
    && taskCenterOpenStart.Contains("IsVisibleChanged -= MainWindow_IsVisibleChanged;", StringComparison.Ordinal),
    "启动隐藏或托盘驻留时不能初始化桌宠渲染器；主窗体之后可见时才启动，并在关闭时解除监听。 ");
Require(taskCenterOpenStart.Contains("new PetSpriteAnimationRuntime", StringComparison.Ordinal)
    && !taskCenterOpenStart.Contains("ThreeDPetRenderer.CreateAsync", StringComparison.Ordinal)
    && taskCenterOpenStart.Contains("PetAnimationHost.Content = renderer.View", StringComparison.Ordinal)
    && taskCenterOpenStart.Contains("renderer?.Dispose();", StringComparison.Ordinal),
    "正式Host应使用有界WPF柯基帧动画作为实际桌宠，并保留静态形象回退及释放路径。 ");
Require(!taskCenterOpenStart.Contains("VPetPortraitRuntime", StringComparison.Ordinal)
    && !taskCenterOpenStart.Contains("PetEngine", StringComparison.Ordinal),
    "Host不得将未裁剪的第三方宠物布局接入紧凑桌宠窗口。 ");
var petLoadedHandlerStart = taskCenterOpenStart.IndexOf("private void PetAnimationViewbox_Loaded", StringComparison.Ordinal);
var petLoadedHandlerEnd = taskCenterOpenStart.IndexOf("private void MainWindow_IsVisibleChanged", petLoadedHandlerStart, StringComparison.Ordinal);
Require(petLoadedHandlerStart >= 0 && petLoadedHandlerEnd > petLoadedHandlerStart
    && taskCenterOpenStart[petLoadedHandlerStart..petLoadedHandlerEnd]
        .Contains("EnsurePetRendererInitialization();", StringComparison.Ordinal)
    && !taskCenterOpenStart[petLoadedHandlerStart..petLoadedHandlerEnd]
        .Contains("InitializePetRendererAsync()", StringComparison.Ordinal),
    "桌宠控件的Loaded事件绕过了窗口可见性准入。 ");
var openTaskCenterStart = taskCenterOpenStart.IndexOf("private Task ShowTaskHistoryAsync()", StringComparison.Ordinal);
var openTaskCenterEnd = taskCenterOpenStart.IndexOf("private void ShowSettings()", openTaskCenterStart, StringComparison.Ordinal);
Require(openTaskCenterStart >= 0 && openTaskCenterEnd > openTaskCenterStart,
    "无法定位托盘任务中心入口。 ");
var openTaskCenter = taskCenterOpenStart[openTaskCenterStart..openTaskCenterEnd];
Require(!openTaskCenter.Contains("RestoreFromTray()", StringComparison.Ordinal)
    && !openTaskCenter.Contains("Owner = this", StringComparison.Ordinal)
    && taskCenterXaml.Contains("WindowStartupLocation=\"CenterScreen\"", StringComparison.Ordinal),
    "从托盘打开任务中心时不应先展开/聚焦桌宠主面板；任务中心应独立显示。 ");
Require(taskCenterSource.Contains("var selectedEntryKey = (HistoryList.SelectedItem as TaskHistoryEntry)?.EntryKey;", StringComparison.Ordinal)
    && taskCenterSource.Contains("item.EntryKey == selectedEntryKey", StringComparison.Ordinal)
    && !taskCenterSource.Contains("selectedTitle", StringComparison.Ordinal)
    && assistantRuntimeSource.Contains("$\"task:{record.Id:N}\"", StringComparison.Ordinal)
    && assistantRuntimeSource.Contains("$\"code:{task.TaskId}\"", StringComparison.Ordinal),
    "任务中心刷新必须按稳定任务键保留所选任务，不能用显示标题关联记录。");
Require(taskCenterSource.Contains("SetCurrentValue(UIElement.IsEnabledProperty, false)", StringComparison.Ordinal)
    && taskCenterSource.Contains("button.SetCurrentValue(UIElement.IsEnabledProperty,", StringComparison.Ordinal)
    && !taskCenterSource.Contains("button.IsEnabled =", StringComparison.Ordinal),
    "任务中心取消按钮应临时更新有效状态并保留CanCancel绑定，避免任务完成后按钮仍可点击。");

var bindingResults = new List<(bool Passed, string Message)>();
Exception? bindingFailure = null;
var bindingThread = new Thread(() =>
{
    try
    {
        var state = new CancelButtonState { CanCancel = true };
        var button = new Button();
        BindingOperations.SetBinding(button, UIElement.IsEnabledProperty, new Binding(nameof(CancelButtonState.CanCancel))
        {
            Source = state,
            Mode = BindingMode.OneWay
        });
        bindingResults.Add((button.IsEnabled, "取消按钮初始值没有来自CanCancel绑定。"));
        bindingResults.Add((BindingOperations.IsDataBound(button, UIElement.IsEnabledProperty), "取消按钮没有建立IsEnabled绑定。"));

        button.SetCurrentValue(UIElement.IsEnabledProperty, false);
        bindingResults.Add((!button.IsEnabled, "SetCurrentValue没有暂时禁用取消按钮。"));
        bindingResults.Add((BindingOperations.IsDataBound(button, UIElement.IsEnabledProperty), "暂时禁用取消按钮移除了IsEnabled绑定。"));

        state.CanCancel = false;
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));
        bindingResults.Add((!button.IsEnabled, "CanCancel变为false后按钮未同步禁用。"));
        state.CanCancel = true;
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));
        bindingResults.Add((button.IsEnabled, "任务恢复可取消时，原绑定没有重新启用按钮。"));
    }
    catch (Exception ex)
    {
        bindingFailure = ex;
    }
});
bindingThread.SetApartmentState(ApartmentState.STA);
bindingThread.Start();
bindingThread.Join();
if (bindingFailure is not null)
    throw new InvalidOperationException("WPF取消按钮绑定行为检查失败。", bindingFailure);
foreach (var result in bindingResults)
    Require(result.Passed, result.Message);

foreach (var failure in XiaoK.DesktopExperience.Checks.TaskHistoryWindowUiProbe.Run())
    Require(false, failure);
Require(true, "任务中心离屏WPF检查通过。");
foreach (var failure in XiaoK.DesktopExperience.Checks.SettingsWindowResilienceProbe.Run())
{
    var annotationMessage = failure.Replace("%", "%25", StringComparison.Ordinal)
        .Replace("\r", "%0D", StringComparison.Ordinal)
        .Replace("\n", "%0A", StringComparison.Ordinal);
    Console.WriteLine($"::error title=Settings async UI probe::{annotationMessage}");
    Require(false, failure);
}
Require(true, "设置页初始化、保存、通知诊断与本地维护失败恢复离屏WPF检查通过。");
foreach (var failure in XiaoK.DesktopExperience.Checks.PetPoseTransitionProbe.Run())
    Require(false, failure);
Require(true, "桌宠姿态过渡离屏WPF检查通过。");
foreach (var failure in XiaoK.DesktopExperience.Checks.PetSpriteAnimationProbe.Run(repositoryDirectory!.FullName))
    Require(false, failure);
Require(true, "WPF柯基动画四态渲染、帧变化、透明边缘、不激活前台与有界视图检查通过。");
foreach (var failure in XiaoK.DesktopExperience.Checks.PetWindowDragProbe.Run(repositoryDirectory!.FullName))
    Require(false, failure);
Require(true, "桌宠移动手势阈值、轻点展开和预览事件接线检查通过。");
foreach (var failure in XiaoK.DesktopExperience.Checks.SherpaKwsWorkerIsolationProbe.Run(repositoryDirectory!.FullName))
    Require(false, failure);
Require(true, "Sherpa KWS隔离worker帧边界、关键词白名单、作业回收与先停麦后停进程检查通过。");

Console.WriteLine($"桌宠柯基动画渲染/姿态切换/移动与缩放/键盘位置微调/后台交互策略/任务中心独立显示/WPF任务历史绑定与不激活刷新/取消按钮绑定/设置页异常恢复/Sherpa KWS worker隔离：{checks} 项通过。");

sealed class CancelButtonState : INotifyPropertyChanged
{
    private bool _canCancel;

    public bool CanCancel
    {
        get => _canCancel;
        set
        {
            if (_canCancel == value) return;
            _canCancel = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanCancel)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
