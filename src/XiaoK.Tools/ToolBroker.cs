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
    private readonly CodeTaskAgent _codeAgent;
    private readonly string _codeProjectRoot;
    private readonly string _codeWorkspaceRoot;

    public ToolBroker(XiaoK.Adapters.Windows.WindowsDesktopTools desktop, IInferenceClient inference, ModelBroker models,
        IApprovalPresenter approval, CodeTaskAgent codeAgent, string codeProjectRoot, string codeWorkspaceRoot)
    {
        _desktop = desktop;
        _inference = inference;
        _models = models;
        _approval = approval;
        _codeAgent = codeAgent;
        _codeProjectRoot = codeProjectRoot;
        _codeWorkspaceRoot = codeWorkspaceRoot;
    }

    public async Task<ToolResult> ExecuteAsync(ToolProposal proposal, CancellationToken cancellationToken)
    {
        var invalidProposal = ValidateProposal(proposal);
        if (invalidProposal is not null) return invalidProposal;

        // Fixed registry: model text never becomes a command, script, arbitrary path, or click target.
        return proposal.ToolId switch
        {
            "app.launch.v1" => await _desktop.LaunchAsync(proposal, cancellationToken),
            "window.activate.v1" => await _desktop.ActivateWindowAsync(proposal, cancellationToken),
            "file.search.v1" => await _desktop.SearchFilesAsync(proposal, cancellationToken),
            "message.analyze.v1" => await AnalyzeAsync(proposal, cancellationToken),
            "message.notice.analyze.v1" => await AnalyzeNoticeAsync(proposal, cancellationToken),
            "message.draft.v1" => await DraftAsync(proposal, cancellationToken),
            "message.send.v1" => await SendAsync(proposal, cancellationToken),
            "code.inspect.v1" => await _codeAgent.InspectAsync(_codeProjectRoot, _codeWorkspaceRoot,
                proposal.Arguments["instruction"], cancellationToken),
            "code.task.create.v1" => await _codeAgent.ExecuteAsync(_codeProjectRoot, _codeWorkspaceRoot,
                proposal.Arguments["instruction"], cancellationToken, _approval as ICodeTaskReviewPresenter),
            _ => new ToolResult(false, "未知工具已拒绝。", "UNKNOWN_TOOL")
        };
    }

    private static ToolResult? ValidateProposal(ToolProposal proposal)
    {
        if (proposal is null || proposal.Arguments is null
            || string.IsNullOrWhiteSpace(proposal.ToolId) || proposal.ToolId.Length > 80
            || string.IsNullOrWhiteSpace(proposal.Target) || proposal.Target.Length > 512
            || proposal.ExpectedOutcome == ToolExpectedOutcome.None
            || proposal.Arguments.Count > 8
            || proposal.Arguments.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 80
                || pair.Value is null || pair.Value.Length > 20_000))
            return InvalidProposal();

        var requiredPreconditions = GetRequiredPreconditions(proposal.ToolId);
        if (requiredPreconditions != ToolPrecondition.None
            && (proposal.Preconditions != requiredPreconditions
                || proposal.ExpectedOutcome != GetRequiredExpectedOutcome(proposal.ToolId)))
            return InvalidProposal("动作提案的固定前置条件或可观察结果缺失，或与工具不匹配；未执行。");

        return proposal.ToolId switch
        {
            "app.launch.v1" => ValidateAppLaunch(proposal),
            "window.activate.v1" => ValidateWindowActivation(proposal),
            "file.search.v1" => ValidateFileSearch(proposal),
            "message.analyze.v1" => ValidateMessage(proposal, "message"),
            "message.notice.analyze.v1" => ValidateVerifiedNotice(proposal),
            "message.draft.v1" => ValidateDraft(proposal),
            "message.send.v1" => ValidateSend(proposal),
            "code.inspect.v1" or "code.task.create.v1" => proposal.Arguments.Count == 1
                && proposal.Arguments.TryGetValue("instruction", out var codeInstruction)
                && !string.IsNullOrWhiteSpace(codeInstruction) && codeInstruction.Length <= 4_000
                && proposal.Target == "configured-project"
                    ? null
                    : InvalidProposal("编程任务只接受用户输入的说明，并绑定到设置中明确选择的项目。"),
            _ => null // The fixed registry below rejects unknown tool IDs.
        };
    }

    private static ToolResult? ValidateAppLaunch(ToolProposal proposal)
    {
        var args = proposal.Arguments;
        var appId = args.GetValueOrDefault("app_id");
        if (args.Keys.Any(key => key is not ("app_id" or "workspace_id"))
            || string.IsNullOrWhiteSpace(appId)
            || !string.Equals(proposal.Target, appId, StringComparison.OrdinalIgnoreCase))
            return InvalidProposal("启动目标必须与允许列表中的 app_id 一致。");

        if (args.TryGetValue("workspace_id", out var workspaceId)
            && (!appId.Equals("vscode", StringComparison.OrdinalIgnoreCase) || workspaceId != "xiaok"))
            return InvalidProposal("工作区只能使用已配置的 VS Code 项目别名。");

        return null;
    }

    private static ToolResult? ValidateWindowActivation(ToolProposal proposal)
    {
        var appId = proposal.Arguments.GetValueOrDefault("app_id");
        if (proposal.Arguments.Count != 1 || string.IsNullOrWhiteSpace(appId)
            || !string.Equals(proposal.Target, appId, StringComparison.OrdinalIgnoreCase))
            return InvalidProposal("窗口切换只接受与 app_id 完全绑定的白名单应用目标。未执行任意句柄、坐标或命令。");
        return null;
    }

    private static ToolResult? ValidateFileSearch(ToolProposal proposal)
    {
        var args = proposal.Arguments;
        var query = args.GetValueOrDefault("query");
        var rootId = args.GetValueOrDefault("root_id");
        if (args.Count != 2 || string.IsNullOrWhiteSpace(query) || query.Length > 120
            || string.IsNullOrWhiteSpace(rootId) || rootId != "user-files"
            || proposal.Target != rootId)
            return InvalidProposal("文件搜索的目标和范围必须是已配置的 user-files。");
        return null;
    }

    private static ToolResult? ValidateMessage(ToolProposal proposal, string key)
    {
        if (proposal.Arguments.Count != 1 || !proposal.Arguments.TryGetValue(key, out var body)
            || string.IsNullOrWhiteSpace(body) || body.Length > 20_000
            || proposal.Target != "用户本次提供的单条消息")
            return InvalidProposal("消息分析和草稿只能使用用户本次提供的单条消息。");
        return null;
    }

    private static ToolResult? ValidateDraft(ToolProposal proposal)
    {
        var styleId = proposal.Arguments.GetValueOrDefault("style_id", ContactReplyStyleCatalog.DefaultStyleId);
        if (proposal.Arguments.Count is < 1 or > 2
            || proposal.Arguments.Keys.Any(key => key is not ("draft" or "style_id"))
            || !proposal.Arguments.TryGetValue("draft", out var body)
            || string.IsNullOrWhiteSpace(body) || body.Length > 20_000
            || proposal.Target != "用户本次提供的单条消息"
            || !ContactReplyStyleCatalog.IsSupportedStyle(styleId))
            return InvalidProposal("回复草稿必须基于本次提供的单条消息，并使用固定风格选项。文案不会自动发送。");
        return null;
    }

    private static ToolResult? ValidateVerifiedNotice(ToolProposal proposal)
    {
        var args = proposal.Arguments;
        var applicationId = args.GetValueOrDefault("application_id") ?? string.Empty;
        var sourceAppId = args.GetValueOrDefault("source_app_id") ?? string.Empty;
        var isPrivateConversation = args.GetValueOrDefault("is_private_conversation") ?? string.Empty;
        var conversationId = args.GetValueOrDefault("conversation_id") ?? string.Empty;
        var sender = args.GetValueOrDefault("sender") ?? string.Empty;
        var body = args.GetValueOrDefault("body") ?? string.Empty;
        var receivedAt = args.GetValueOrDefault("received_at_utc") ?? string.Empty;
        var deduplicationKey = args.GetValueOrDefault("deduplication_key") ?? string.Empty;
        if (args.Count != 8 || applicationId is not ("wechat" or "qq") || isPrivateConversation != "true"
            || string.IsNullOrWhiteSpace(sourceAppId) || sourceAppId.Length > 256 || !sourceAppId.Contains('!')
            || string.IsNullOrWhiteSpace(conversationId) && string.IsNullOrWhiteSpace(sender)
            || conversationId.Length > 256 || sender.Length > 256
            || string.IsNullOrWhiteSpace(body) || body.Length > 20_000
            || !DateTimeOffset.TryParse(receivedAt, out var timestamp)
            || timestamp > DateTimeOffset.UtcNow || timestamp < DateTimeOffset.UtcNow - TimeSpan.FromHours(24)
            || deduplicationKey is not { Length: 64 } || !deduplicationKey.All(Uri.IsHexDigit)
            || proposal.Target != "verified-private-notice")
            return InvalidProposal("自动分析只接受来源已核验、近期、正文可见且会话归属明确的私聊通知。");
        return null;
    }

    private static ToolResult? ValidateSend(ToolProposal proposal)
    {
        var args = proposal.Arguments;
        var recipient = args.GetValueOrDefault("recipient");
        var text = args.GetValueOrDefault("text");
        if (args.Keys.Any(key => key is not ("recipient" or "text" or "attachments"))
            || args.Count is < 2 or > 3 || string.IsNullOrWhiteSpace(recipient)
            || string.IsNullOrWhiteSpace(text) || text.Length > 20_000
            || proposal.Target != recipient)
            return InvalidProposal("发送目标必须与最终收件人一致，且正文和附件字段必须明确。");
        return null;
    }

    private static ToolResult InvalidProposal(string message = "动作提案未通过 ToolBroker 参数和目标校验；未执行。") =>
        new(false, message, "INVALID_TOOL_PROPOSAL");

    private Task<ToolResult> AnalyzeAsync(ToolProposal proposal, CancellationToken token) =>
        CompleteAsync(proposal, "请用中文分析用户提供的单条聊天通知。只区分明确内容、可能意图和建议；不要推断未给出的上下文。", "message", token);

    private Task<ToolResult> AnalyzeNoticeAsync(ToolProposal proposal, CancellationToken token) =>
        CompleteAsync(proposal, "请用中文分析这条已核验私聊通知中可见的单条消息。只区分明确内容、可能意图和建议；不要推断未给出的上下文。", "body", token, background: true);

    private Task<ToolResult> DraftAsync(ToolProposal proposal, CancellationToken token) =>
        CompleteAsync(proposal,
            ContactReplyStyleCatalog.CreateSystemPrompt(proposal.Arguments.GetValueOrDefault("style_id", ContactReplyStyleCatalog.DefaultStyleId)),
            "draft", token);

    private async Task<ToolResult> CompleteAsync(ToolProposal proposal, string systemPrompt, string key,
        CancellationToken token, bool background = false)
    {
        if (!proposal.Arguments.TryGetValue(key, out var body) || string.IsNullOrWhiteSpace(body))
            return new(false, "没有可供分析的正文。", "EMPTY_MESSAGE");
        try
        {
            var answer = background
                ? await _models.RunBackgroundStepAsync(inner => _inference.CompleteAsync(systemPrompt, body, inner), token)
                : await _models.RunInteractiveAsync(inner => _inference.CompleteAsync(systemPrompt, body, inner), token);
            return new(true, answer);
        }
        catch (ModelQueueFullException) { return new(false, "本地模型请求过多；当前请求未排队。", "RESOURCE_BUSY"); }
        catch (LowGpuMemoryException) { return new(false, "可用独显显存不足或读数不可用；已拒绝启动模型，保留系统显存余量。", "LOW_VRAM"); }
        catch (ModelRuntimeUnavailableException) { return new(false, "本地模型清单、程序或权重校验失败；没有向模型发送请求。", "MODEL_RUNTIME_UNAVAILABLE"); }
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
        var confirmed = await _approval.ConfirmAsync(ApprovalAuditCatalog.MessageSendAction, "确认发送",
            $"收件人：{recipient}{Environment.NewLine}{Environment.NewLine}正文：{text}{Environment.NewLine}{Environment.NewLine}附件：{attachments}", token);
        if (!confirmed) return new(false, "用户取消发送。", "USER_DECLINED");
        // No WeChat/QQ sender is implemented; approval alone must never imply an external side effect.
        return new(false, "预览已确认，但微信/QQ发送适配器尚未接入；未发送任何内容。", "SEND_ADAPTER_UNAVAILABLE");
    }

    private static ToolPrecondition GetRequiredPreconditions(string toolId) => toolId switch
    {
        "app.launch.v1" => ToolPrecondition.ApplicationAllowlisted,
        "window.activate.v1" => ToolPrecondition.ApplicationAllowlisted | ToolPrecondition.ExistingWindow,
        "file.search.v1" => ToolPrecondition.ConfiguredSearchRoot,
        "message.analyze.v1" or "message.draft.v1" => ToolPrecondition.UserProvidedSingleMessage,
        "message.notice.analyze.v1" => ToolPrecondition.VerifiedPrivateNotice,
        "message.send.v1" => ToolPrecondition.CompleteMessagePreview,
        "code.inspect.v1" or "code.task.create.v1" => ToolPrecondition.ConfiguredProjectAndIsolatedWorkspace,
        _ => ToolPrecondition.None
    };

    private static ToolExpectedOutcome GetRequiredExpectedOutcome(string toolId) => toolId switch
    {
        "app.launch.v1" => ToolExpectedOutcome.ApplicationWindowVisible,
        "window.activate.v1" => ToolExpectedOutcome.TargetWindowInForeground,
        "file.search.v1" => ToolExpectedOutcome.MatchingFilesListed,
        "message.analyze.v1" => ToolExpectedOutcome.LocalMessageAnalysis,
        "message.notice.analyze.v1" => ToolExpectedOutcome.LocalMessageAnalysis,
        "message.draft.v1" => ToolExpectedOutcome.ReplyDraftOnly,
        "message.send.v1" => ToolExpectedOutcome.PreviewConfirmedBeforeSend,
        "code.inspect.v1" => ToolExpectedOutcome.CodeExplanationReturned,
        "code.task.create.v1" => ToolExpectedOutcome.ReviewablePatchCreated,
        _ => ToolExpectedOutcome.None
    };

    public static ToolProposal Proposal(string toolId, IEnumerable<KeyValuePair<string, string>> args, string target,
        ToolExpectedOutcome expectedOutcome) =>
        new(toolId, args.ToImmutableDictionary(StringComparer.Ordinal), target, GetRequiredPreconditions(toolId),
            expectedOutcome);
}
