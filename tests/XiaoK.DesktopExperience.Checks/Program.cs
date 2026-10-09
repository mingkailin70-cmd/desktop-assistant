using XiaoK.Core;
using XiaoK.Storage;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;

var checks = 0;
void Require(bool passed, string message)
{
    if (!passed) throw new InvalidOperationException(message);
    checks++;
}
static bool Near(double left, double right) => Math.Abs(left - right) < 0.00001;

foreach (var percent in new[] { 40d, 70d, 100d, 160d })
foreach (var dpi in new uint[] { 96, 144, 192 })
{
    var size = PetSizingPolicy.FitToWorkArea(percent, 1920, 1080, dpi);
    Require(Near(size.WidthDip / size.HeightDip, 300d / 372), "桌宠宽高比改变。");
    Require(size.WidthDip * dpi / 96 <= 1920 && size.HeightDip * dpi / 96 <= 1080,
        "桌宠超出工作区。");
}
var tiny = PetSizingPolicy.FitToWorkArea(160, 100, 100, 192);
Require(Near(tiny.HeightDip, 50) && Near(tiny.WidthDip / tiny.HeightDip, 300d / 372), "小屏幕裁剪错误。");
Require(PetSizingPolicy.ClampPercent(12) == 40 && PetSizingPolicy.ClampPercent(300) == 160, "缩放限值错误。");
Require(!PetSizingPolicy.IsValidPercent(double.NaN) && !PetSizingPolicy.IsValidPercent(double.PositiveInfinity), "接受非有限缩放。");

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
foreach (var tool in new[] { "file.search.v1", "file.copy.v1", "file.archive.single.v1", "file.archive.directory.v1", "file.rename.v1", "browser.read.public.v1", "browser.read.dynamic.public.v1", "browser.download.public.v1", "code.inspect.v1", "code.task.create.v1", "message.analyze.v1", "message.notice.analyze.v1", "message.draft.v1", "message.send.v1" })
    Require(ToolInteractionPolicy.Check(tool, ToolExecutionAccess.BackgroundOnly) is null, "阻止后台工具。");
Require(ToolInteractionPolicy.Check("arbitrary.shell", ToolExecutionAccess.ExplicitUserInteraction)?.ErrorCode == "UNKNOWN_TOOL",
    "未知工具被放行。");
Require(ToolInteractionPolicy.Check("file.search.v1", (ToolExecutionAccess)999)?.ErrorCode == "INVALID_EXECUTION_CONTEXT",
    "无效执行上下文被放行。");

var repositoryDirectory = new DirectoryInfo(AppContext.BaseDirectory);
while (repositoryDirectory is not null && !File.Exists(Path.Combine(repositoryDirectory.FullName, "XiaoK.sln")))
    repositoryDirectory = repositoryDirectory.Parent;
Require(repositoryDirectory is not null, "无法从桌面交互检查目录定位仓库根目录。");
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
    "ExecuteBackgroundAsync(new ToolProposal(\"file.search.v1\"",
    "var tool = category == \"draft\" ? \"message.draft.v1\" : \"message.analyze.v1\";",
    "ExecuteBackgroundAsync(new ToolProposal(tool, arguments,",
    "ExecuteBackgroundAsync(new ToolProposal(\"message.send.v1\"",
    "ExecuteBackgroundAsync(new ToolProposal(\"browser.read.dynamic.public.v1\"",
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
    && taskCenterOpenStart.Contains("!IsVisible || Opacity <= 0 || !PetEngineViewbox.IsLoaded", StringComparison.Ordinal)
    && taskCenterOpenStart.Contains("IsVisibleChanged -= MainWindow_IsVisibleChanged;", StringComparison.Ordinal),
    "启动隐藏或托盘驻留时不能初始化VPet；主窗体之后可见时才启动，并在关闭时解除监听。 ");
var petLoadedHandlerStart = taskCenterOpenStart.IndexOf("private void PetEngineViewbox_Loaded", StringComparison.Ordinal);
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

Console.WriteLine($"桌宠缩放/保存/后台交互策略/任务中心独立显示/WPF取消按钮绑定：{checks} 项通过。");

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
