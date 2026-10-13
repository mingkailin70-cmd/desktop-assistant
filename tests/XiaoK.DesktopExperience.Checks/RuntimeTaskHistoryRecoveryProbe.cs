using System.Text.Json;
using XiaoK.Core;
using XiaoK.Host;
using XiaoK.Storage;

namespace XiaoK.DesktopExperience.Checks;

internal static class RuntimeTaskHistoryRecoveryProbe
{
    public static async Task RunAsync()
    {
        var profileRoot = Path.Combine(Path.GetTempPath(), "XiaoK-Diagnostics-" + Guid.NewGuid().ToString("N"));
        try
        {
            XiaoKSettings.EnableDiagnosticsProfile(profileRoot);
            var settings = XiaoKSettings.Load();
            Require(IsWithin(profileRoot, settings.DataRoot)
                && IsWithin(profileRoot, settings.ModelRoot)
                && IsWithin(profileRoot, settings.CodeWorkspaceRoot)
                && !settings.MonitorWeChatNotifications && !settings.MonitorQQNotifications
                && !settings.WakeWordEnabled,
                "Runtime历史恢复探针没有限制在隔离诊断目录，或意外启用了通知/唤醒监听。");

            var databasePath = Path.Combine(settings.DataRoot, "tasks.sqlite3");
            var store = new SqliteTaskStore(databasePath);
            var oldHostSessionId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var actionId = Guid.NewGuid();
            var acceptedId = Guid.NewGuid();
            var approvalId = Guid.NewGuid();
            var completedId = Guid.NewGuid();
            var privateSentinel = "RUNTIME_HISTORY_PRIVATE_SENTINEL_47d2";
            var records = new[]
            {
                new TaskRecord(actionId, "file-copy", "复制文件", TaskLifecycleState.Running,
                    now.AddSeconds(-4), now.AddSeconds(-3), Result: privateSentinel,
                    HostSessionId: oldHostSessionId, CheckpointStage: TaskCheckpointStage.ActionStarted),
                new TaskRecord(acceptedId, "app", "打开应用", TaskLifecycleState.Queued,
                    now.AddSeconds(-8), now.AddSeconds(-7), Result: privateSentinel,
                    HostSessionId: oldHostSessionId, CheckpointStage: TaskCheckpointStage.Accepted),
                new TaskRecord(approvalId, "send", "消息待确认", TaskLifecycleState.AwaitingApproval,
                    now.AddSeconds(-6), now.AddSeconds(-5), Result: privateSentinel,
                    HostSessionId: oldHostSessionId, CheckpointStage: TaskCheckpointStage.ApprovalPending),
                new TaskRecord(completedId, "app", "已完成应用任务", TaskLifecycleState.Completed,
                    now.AddSeconds(-10), now.AddSeconds(-9), Result: privateSentinel,
                    HostSessionId: oldHostSessionId, CheckpointStage: TaskCheckpointStage.Terminal)
            };
            foreach (var record in records) await store.SaveAsync(record, CancellationToken.None);

            var codeTaskId = $"{now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
            var codeTaskRoot = Path.Combine(settings.CodeWorkspaceRoot, codeTaskId);
            var codeWorkspace = Path.Combine(codeTaskRoot, "workspace");
            Directory.CreateDirectory(codeWorkspace);
            await File.WriteAllTextAsync(Path.Combine(codeTaskRoot, "task-state.json"),
                JsonSerializer.Serialize(new
                {
                    taskId = codeTaskId,
                    createdAtUtc = now.AddMinutes(-1),
                    updatedAtUtc = now,
                    state = "running",
                    hostSessionId = oldHostSessionId,
                    workspacePath = codeWorkspace,
                    privateBody = privateSentinel
                }));

            await using (var runtime = new AssistantRuntime(new RejectAllApprovals()))
            {
                Require(runtime.ModelStatus.StartsWith("诊断模式", StringComparison.Ordinal)
                    && runtime.VoiceStatus.StartsWith("诊断模式", StringComparison.Ordinal)
                    && !runtime.IsMicrophoneActive,
                    "诊断Runtime意外启用了模型或麦克风工作路径。");

                using var historyTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var first = await runtime.GetRecentTaskHistoryAsync(historyTimeout.Token);
                var second = await runtime.GetRecentTaskHistoryAsync(historyTimeout.Token);
                Require(first.Count == 5 && runtime.PendingUserTaskCount == 0 && !runtime.HasActiveUserTask,
                    "Runtime恢复历史时排入或启动了任务，或没有返回所有合成历史。");

                var entries = first.Where(entry => entry.TaskId.HasValue)
                    .ToDictionary(entry => entry.TaskId!.Value);
                Require(entries.Count == records.Length
                    && entries[acceptedId].State == "失败"
                    && entries[acceptedId].Detail.Contains("操作开始前退出", StringComparison.Ordinal)
                    && entries[actionId].State == "结果待核对"
                    && entries[actionId].Detail.Contains("不会自动重试", StringComparison.Ordinal)
                    && entries[approvalId].State == "结果待核对"
                    && entries[approvalId].Detail.Contains("不会自动继续或重放动作", StringComparison.Ordinal)
                    && entries[completedId].State == "已完成",
                    "生产Runtime没有按检查点安全区分未开始、可能已开始、旧审批和终态任务。");

                var codeEntry = first.SingleOrDefault(entry => entry.EntryKey == "code:" + codeTaskId);
                Require(codeEntry is not null
                    && codeEntry.State == "上次中断，需核对"
                    && codeEntry.CurrentStep.Contains("不会自动恢复或重试", StringComparison.Ordinal)
                    && codeEntry.NextAction.Contains("不会自动重试", StringComparison.Ordinal),
                    "生产Runtime没有将跨Host会话的隔离编程任务显示为待核对且不重放。");

                var visibleText = JsonSerializer.Serialize(first);
                Require(!visibleText.Contains(privateSentinel, StringComparison.Ordinal)
                    && first.Select(entry => (entry.EntryKey, entry.State, entry.Detail, entry.CurrentStep, entry.NextAction))
                        .SequenceEqual(second.Select(entry => (entry.EntryKey, entry.State, entry.Detail, entry.CurrentStep, entry.NextAction))),
                    "生产Runtime泄漏了历史哨兵内容，或3秒轮询重复读取改变了恢复投影。");
            }

            var persisted = await store.GetRecentAsync(20, CancellationToken.None);
            var persistedById = persisted.ToDictionary(record => record.Id);
            Require(records.All(record => persistedById[record.Id].Status == record.Status
                && persistedById[record.Id].CheckpointStage == record.CheckpointStage
                && persistedById[record.Id].Result is null),
                "任务中心只读恢复改变了持久化状态，或任务结果正文进入SQLite。");
        }
        finally
        {
            if (Directory.Exists(profileRoot)) Directory.Delete(profileRoot, recursive: true);
        }
    }

    private static bool IsWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return !Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class RejectAllApprovals : IApprovalPresenter
    {
        public Task<bool> ConfirmAsync(string actionId, string title, string details,
            CancellationToken cancellationToken, Guid? taskId = null) => Task.FromResult(false);
    }
}
