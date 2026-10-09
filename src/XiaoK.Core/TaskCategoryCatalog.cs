namespace XiaoK.Core;

/// <summary>Canonical labels for task kinds persisted in the local task history.</summary>
public static class TaskCategoryCatalog
{
    private static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["app"] = "应用操作",
        ["window"] = "窗口切换",
        ["file"] = "文件查找",
        ["file-content-search"] = "文件内容查找",
        ["file-summary"] = "本机文件摘要",
        ["file-copy"] = "文件复制",
        ["file-archive"] = "文件压缩",
        ["file-move"] = "文件移动",
        ["file-rename"] = "文件重命名",
        ["file-delete"] = "移入回收站",
        ["file-classify"] = "文件分类预览",
        ["web-read"] = "静态网页读取",
        ["web-read-dynamic"] = "动态网页读取",
        ["web-download"] = "公网文件下载",
        ["analyze"] = "消息分析",
        ["draft"] = "回复草稿",
        ["send"] = "发送请求",
        ["code-inspect"] = "只读代码检索",
        ["code"] = "本地编程任务",
        ["chat"] = "本地对话"
    };

    public static bool TryGetLabel(string? category, out string label)
    {
        if (category is not null && Labels.TryGetValue(category, out label!)) return true;
        label = string.Empty;
        return false;
    }

    public static string LabelOrConversation(string? category) =>
        TryGetLabel(category, out var label) ? label : "本地对话";
}
