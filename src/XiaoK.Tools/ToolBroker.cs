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
    private readonly IMessageSendPreviewPresenter? _messageSendPreview;
    private readonly CodeTaskAgent _codeAgent;
    private readonly string _codeProjectRoot;
    private readonly string _codeWorkspaceRoot;
    private readonly IPublicWebPageReader? _publicWebPageReader;

    public ToolBroker(XiaoK.Adapters.Windows.WindowsDesktopTools desktop, IInferenceClient inference, ModelBroker models,
        IApprovalPresenter approval, CodeTaskAgent codeAgent, string codeProjectRoot, string codeWorkspaceRoot,
        IMessageSendPreviewPresenter? messageSendPreview = null, IPublicWebPageReader? publicWebPageReader = null)
    {
        _desktop = desktop;
        _inference = inference;
        _models = models;
        _approval = approval;
        _messageSendPreview = messageSendPreview;
        _codeAgent = codeAgent;
        _codeProjectRoot = codeProjectRoot;
        _codeWorkspaceRoot = codeWorkspaceRoot;
        _publicWebPageReader = publicWebPageReader;
    }

    // 兼容直接由用户发起的交互入口；后台调用必须使用 ExecuteBackgroundAsync。
    public Task<ToolResult> ExecuteAsync(ToolProposal proposal, CancellationToken cancellationToken) =>
        ExecuteAsync(proposal, ToolExecutionAccess.ExplicitUserInteraction, cancellationToken);

    public Task<ToolResult> ExecuteBackgroundAsync(ToolProposal proposal, CancellationToken cancellationToken) =>
        ExecuteAsync(proposal, ToolExecutionAccess.BackgroundOnly, cancellationToken);

    public async Task<ToolResult> ExecuteAsync(ToolProposal proposal, ToolExecutionAccess access,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var invalidProposal = ValidateProposal(proposal);
        if (invalidProposal is not null) return invalidProposal;
        var interactionDenied = ToolInteractionPolicy.Check(proposal.ToolId, access);
        if (interactionDenied is not null) return interactionDenied;

        // Fixed registry: model text never becomes a command, script, arbitrary path, or click target.
        return proposal.ToolId switch
        {
            "app.launch.v1" => await _desktop.LaunchAsync(proposal, cancellationToken),
            "window.activate.v1" => await _desktop.ActivateWindowAsync(proposal, cancellationToken),
            "file.search.v1" => await _desktop.SearchFilesAsync(proposal, cancellationToken),
            "file.copy.v1" => await _desktop.CopyFileToExportAsync(proposal, cancellationToken),
            "file.rename.v1" => await _desktop.RenameFileAsync(proposal, cancellationToken),
            "browser.read.public.v1" => _publicWebPageReader is null
                ? new(false, "独立网页读取器未配置；没有启动浏览器。", "BROWSER_READER_UNAVAILABLE")
                : await _publicWebPageReader.ReadPageAsync(proposal.Arguments["url"], cancellationToken),
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
            "file.copy.v1" => ValidateFileCopy(proposal),
            "file.rename.v1" => ValidateFileRename(proposal),
            "browser.read.public.v1" => ValidatePublicWebPageRead(proposal),
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
            || !AppUserModelIdPolicy.IsValid(sourceAppId)
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
        var applicationId = args.GetValueOrDefault("application_id");
        var recipient = args.GetValueOrDefault("recipient");
        var text = args.GetValueOrDefault("text");
        var attachments = args.GetValueOrDefault("attachments");
        if (args.Keys.Any(key => key is not ("application_id" or "recipient" or "text" or "attachments"))
            || args.Count != 4 || applicationId is not ("wechat" or "qq")
            || string.IsNullOrWhiteSpace(recipient) || recipient.Length > 256 || recipient.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(text) || text.Length > 20_000 || text.Contains('\0')
            || attachments is null
            || proposal.Target != $"{applicationId}:{recipient}")
            return InvalidProposal("发送预览必须绑定明确的微信或 QQ、最终收件人、正文和附件清单。");
        if (!MessageSendRecipientPolicy.IsPreviewAllowed(applicationId, recipient))
            return new(false, "当前只允许 QQ 联系人 K 和微信联系人 L；其他目标均已拒绝，未显示或发送内容。",
                "SEND_RECIPIENT_NOT_ALLOWED");
        if (attachments != "none")
            return new(false, "当前版本不支持附件；没有显示或发送任何文件。", "SEND_ATTACHMENTS_UNSUPPORTED");
        return null;
    }

    private static ToolResult InvalidProposal(string message = "动作提案未通过 ToolBroker 参数和目标校验；未执行。") =>
        new(false, message, "INVALID_TOOL_PROPOSAL");

    private static ToolResult? ValidateFileCopy(ToolProposal proposal) =>
        proposal.Arguments.Count == 1
        && proposal.Arguments.TryGetValue("source_path", out var sourcePath)
        && LocalFileCopyPolicy.IsValidSourcePath(sourcePath)
        && proposal.Target == "configured-export"
            ? null
            : InvalidProposal("文件复制只接受搜索范围内的本机完整文件路径，并写入固定的小K导出目录。未执行任意目标路径。");

    private static ToolResult? ValidateFileRename(ToolProposal proposal) =>
        proposal.Arguments.Count == 2
        && proposal.Arguments.TryGetValue("source_path", out var sourcePath)
        && LocalFileRenamePolicy.IsValidSourcePath(sourcePath)
        && proposal.Arguments.TryGetValue("new_name", out var newName)
        && LocalFileRenamePolicy.IsValidFileName(newName)
        && proposal.Target == "configured-search-root"
            ? null
            : InvalidProposal("文件重命名只接受搜索范围内的本机完整源路径和单个新文件名；不会接受任意目标目录或覆盖。");

    private static ToolResult? ValidatePublicWebPageRead(ToolProposal proposal) =>
        proposal.Arguments.Count == 1
        && proposal.Arguments.TryGetValue("url", out var url)
        && PublicWebUrlPolicy.IsAllowedUrlShape(url)
        && proposal.Target == "public-web-page"
            ? null
            : InvalidProposal("网页读取只接受用户明确提供的 HTTPS 公网地址；文件、内网和其他协议不会交给浏览器。");

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
        if (_messageSendPreview is null)
            return new(false, "发送预览界面不可用；没有显示或发送任何内容。", "SEND_PREVIEW_UNAVAILABLE");

        var applicationId = proposal.Arguments["application_id"];
        var recipient = proposal.Arguments.GetValueOrDefault("recipient");
        var text = proposal.Arguments.GetValueOrDefault("text");
        if (string.IsNullOrWhiteSpace(recipient) || string.IsNullOrWhiteSpace(text))
            return new(false, "发送预览缺少最终收件人或正文。", "INVALID_SEND_PREVIEW");
        var preview = new MessageSendPreview(applicationId, recipient, text, []);
        await _messageSendPreview.ShowMessageSendPreviewAsync(preview, token);

        // Preview-only mode deliberately does not request approval: no sender is available to carry out the action.
        return new(false, "已显示最终发送预览。本版本尚未接入微信/QQ发送适配器；没有发送任何内容。", "SEND_ADAPTER_UNAVAILABLE");
    }

    private static ToolPrecondition GetRequiredPreconditions(string toolId) => toolId switch
    {
        "app.launch.v1" => ToolPrecondition.ApplicationAllowlisted,
        "window.activate.v1" => ToolPrecondition.ApplicationAllowlisted | ToolPrecondition.ExistingWindow,
        "file.search.v1" => ToolPrecondition.ConfiguredSearchRoot,
        "file.copy.v1" => ToolPrecondition.ConfiguredSearchRoot | ToolPrecondition.ConfiguredFileExportRoot,
        "file.rename.v1" => ToolPrecondition.ConfiguredSearchRoot,
        "browser.read.public.v1" => ToolPrecondition.UserProvidedPublicWebPageUrl,
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
        "file.copy.v1" => ToolExpectedOutcome.FileCopiedToConfiguredExport,
        "file.rename.v1" => ToolExpectedOutcome.FileRenamedInConfiguredSearchRoot,
        "browser.read.public.v1" => ToolExpectedOutcome.PublicWebPageSnapshotReturned,
        "message.analyze.v1" => ToolExpectedOutcome.LocalMessageAnalysis,
        "message.notice.analyze.v1" => ToolExpectedOutcome.LocalMessageAnalysis,
        "message.draft.v1" => ToolExpectedOutcome.ReplyDraftOnly,
        "message.send.v1" => ToolExpectedOutcome.MessageSendPreviewShown,
        "code.inspect.v1" => ToolExpectedOutcome.CodeExplanationReturned,
        "code.task.create.v1" => ToolExpectedOutcome.ReviewablePatchCreated,
        _ => ToolExpectedOutcome.None
    };

    public static ToolProposal Proposal(string toolId, IEnumerable<KeyValuePair<string, string>> args, string target,
        ToolExpectedOutcome expectedOutcome) =>
        new(toolId, args.ToImmutableDictionary(StringComparer.Ordinal), target, GetRequiredPreconditions(toolId),
            expectedOutcome);
}
