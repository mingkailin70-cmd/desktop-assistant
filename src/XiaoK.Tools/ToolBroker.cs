using System.Collections.Immutable;
using XiaoK.Core;
using XiaoK.Inference;

namespace XiaoK.Tools;

public sealed class ToolBroker
{
    private readonly XiaoK.Adapters.Windows.WindowsDesktopTools _desktop;
    private readonly IInferenceClient _inference;
    private readonly ModelBroker _models;
    private readonly IApprovalPresenter _approval;

    public ToolBroker(XiaoK.Adapters.Windows.WindowsDesktopTools desktop, IInferenceClient inference, ModelBroker models, IApprovalPresenter approval)
    { _desktop = desktop; _inference = inference; _models = models; _approval = approval; }

    public async Task<ToolResult> ExecuteAsync(ToolProposal proposal, CancellationToken cancellationToken)
    {
        // Fixed registry: model text never becomes a command, script, arbitrary path, or click target.
        return proposal.ToolId switch
        {
            "app.launch.v1" => await _desktop.LaunchAsync(proposal, cancellationToken),
            "file.search.v1" => await _desktop.SearchFilesAsync(proposal, cancellationToken),
            "message.analyze.v1" => await AnalyzeAsync(proposal, cancellationToken),
            "message.draft.v1" => await DraftAsync(proposal, cancellationToken),
            "message.send.v1" => await SendAsync(proposal, cancellationToken),
            "code.task.create.v1" => new ToolResult(false, "隔离编程代理尚未配置；没有修改项目文件。", "LOCAL_AGENT_NOT_CONFIGURED"),
            _ => new ToolResult(false, "未知工具已拒绝。", "UNKNOWN_TOOL")
        };
    }

    private Task<ToolResult> AnalyzeAsync(ToolProposal proposal, CancellationToken token) =>
        CompleteAsync(proposal, "请用中文分析用户提供的单条聊天通知。只区分明确内容、可能意图和建议；不要推断未给出的上下文。", "message", token);

    private Task<ToolResult> DraftAsync(ToolProposal proposal, CancellationToken token) =>
        CompleteAsync(proposal, "根据用户提供的单条消息起草简洁中文回复。不得声称已发送。只输出草稿正文。", "draft", token);

    private async Task<ToolResult> CompleteAsync(ToolProposal proposal, string systemPrompt, string key, CancellationToken token)
    {
        if (!proposal.Arguments.TryGetValue(key, out var body) || string.IsNullOrWhiteSpace(body))
            return new(false, "没有可供分析的正文。", "EMPTY_MESSAGE");
        try
        {
            var answer = await _models.RunInteractiveAsync(
                inner => _inference.CompleteAsync(systemPrompt, body, inner), token);
            return new(true, answer);
        }
        catch (HttpRequestException) { return new(false, "本地模型服务不可用；小K不会回退到云端。", "LOCAL_MODEL_OFFLINE"); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new(false, "本地模型响应超时；没有调用云端服务。", "LOCAL_MODEL_TIMEOUT"); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return new(false, "本地模型返回内容无法读取。", "LOCAL_MODEL_INVALID_RESPONSE"); }
    }

    private async Task<ToolResult> SendAsync(ToolProposal proposal, CancellationToken token)
    {
        var recipient = proposal.Arguments.GetValueOrDefault("recipient");
        var text = proposal.Arguments.GetValueOrDefault("text");
        var attachments = proposal.Arguments.GetValueOrDefault("attachments", "无");
        if (string.IsNullOrWhiteSpace(recipient) || string.IsNullOrWhiteSpace(text))
            return new(false, "发送预览缺少最终收件人或正文。", "INVALID_SEND_PREVIEW");
        var confirmed = await _approval.ConfirmAsync("确认发送", $"收件人：{recipient}{Environment.NewLine}{Environment.NewLine}正文：{text}{Environment.NewLine}{Environment.NewLine}附件：{attachments}", token);
        if (!confirmed) return new(false, "用户取消发送。", "USER_DECLINED");
        // No WeChat/QQ sender is implemented; approval alone must never imply an external side effect.
        return new(false, "预览已确认，但微信/QQ发送适配器尚未接入；未发送任何内容。", "SEND_ADAPTER_UNAVAILABLE");
    }

    public static ToolProposal Proposal(string toolId, IEnumerable<KeyValuePair<string, string>> args, string target, string expected) =>
        new(toolId, args.ToImmutableDictionary(StringComparer.Ordinal), target, expected);
}
