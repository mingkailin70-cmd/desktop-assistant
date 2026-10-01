using System.IO;
using System.Windows;
using XiaoK.Core;

namespace XiaoK.Host;

public partial class CodeTaskReviewWindow : Window
{
    public CodeTaskReviewWindow(string projectPath, string workspacePath, string diff,
        string? targetRelativePath, string? commandPreview)
    {
        InitializeComponent();
        ProjectPathText.Text = "原项目（只读）：" + projectPath;
        WorkspacePathText.Text = "隔离工作区：" + workspacePath;
        DiffText.Text = diff;

        if (string.IsNullOrWhiteSpace(targetRelativePath) || string.IsNullOrWhiteSpace(commandPreview))
        {
            RunTestsButton.Visibility = Visibility.Collapsed;
            TestCommandText.Text = "未找到唯一的根目录 .sln、.slnx 或 .csproj，或本机没有 dotnet.exe；不会运行命令。";
            TestWarningText.Text = "关闭此窗口会保留差异供你手动检查。";
            return;
        }

        TestCommandText.Text = commandPreview;
        TestWarningText.Text = "批准后会运行这两条固定命令，不会执行模型提供的命令。依赖还原仅配置 nuget.org，但可能联网并下载包；构建目标和测试代码仍可能读写其他文件、启动子进程或联网。只对你信任的项目批准。";
    }

    public CodeTaskReviewDecision Decision { get; private set; } = CodeTaskReviewDecision.KeepPatch;

    private void KeepPatch_Click(object sender, RoutedEventArgs e)
    {
        Decision = CodeTaskReviewDecision.KeepPatch;
        DialogResult = true;
    }

    private void RunTests_Click(object sender, RoutedEventArgs e)
    {
        Decision = CodeTaskReviewDecision.RunDotNetTests;
        DialogResult = true;
    }

    private void ApplyPatch_Click(object sender, RoutedEventArgs e)
    {
        Decision = CodeTaskReviewDecision.ApplyPatchToProject;
        DialogResult = true;
    }
}
