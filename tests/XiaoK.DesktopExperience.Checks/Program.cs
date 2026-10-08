using XiaoK.Core;
using XiaoK.Storage;

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

foreach (var tool in new[] { "app.launch.v1", "window.activate.v1", "message.send.v1", "code.task.create.v1" })
{
    Require(ToolInteractionPolicy.Check(tool, ToolExecutionAccess.BackgroundOnly)?.ErrorCode == "FOREGROUND_REQUIRED",
        "后台调用放行需要前台的工具。");
    Require(ToolInteractionPolicy.Check(tool, ToolExecutionAccess.ExplicitUserInteraction) is null,
        "阻止明确的用户交互。");
}
foreach (var tool in new[] { "file.search.v1", "code.inspect.v1", "message.analyze.v1", "message.notice.analyze.v1", "message.draft.v1" })
    Require(ToolInteractionPolicy.Check(tool, ToolExecutionAccess.BackgroundOnly) is null, "阻止后台只读工具。");
Require(ToolInteractionPolicy.Check("arbitrary.shell", ToolExecutionAccess.ExplicitUserInteraction)?.ErrorCode == "UNKNOWN_TOOL",
    "未知工具被放行。");
Require(ToolInteractionPolicy.Check("file.search.v1", (ToolExecutionAccess)999)?.ErrorCode == "INVALID_EXECUTION_CONTEXT",
    "无效执行上下文被放行。");
Console.WriteLine($"桌宠缩放/保存/后台交互策略：{checks} 项通过。");
