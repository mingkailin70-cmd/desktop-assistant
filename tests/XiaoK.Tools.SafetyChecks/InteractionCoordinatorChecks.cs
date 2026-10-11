using XiaoK.Core;

internal static class InteractionCoordinatorChecks
{
    public static async Task RunAsync()
    {
        var probe = new FixedTargetUsageProbe(InteractionTargetUsage.NotInUse);
        var coordinator = new InteractionCoordinator(probe);
        var readOne = await coordinator.AcquireAsync(ToolInteractionOperation.ReadOnlyTarget,
            ToolExecutionAccess.BackgroundOnly, "APP:editor", CancellationToken.None);
        RequireAllowed(readOne, "首个只读查询应获得租约。");
        var readTwoTask = coordinator.AcquireAsync(ToolInteractionOperation.ReadOnlyTarget,
            ToolExecutionAccess.BackgroundOnly, "app:EDITOR", CancellationToken.None).AsTask();
        var readTwo = await readTwoTask.WaitAsync(TimeSpan.FromSeconds(2));
        RequireAllowed(readTwo, "同一目标的多个只读查询应可并发。");

        var mutationTask = coordinator.AcquireAsync(ToolInteractionOperation.TargetStateChange,
            ToolExecutionAccess.BackgroundOnly, "app:editor", CancellationToken.None).AsTask();
        await Task.Delay(50);
        Assert(!mutationTask.IsCompleted, "目标状态变更应等待该目标现有只读租约释放。");
        await readOne.Lease.DisposeAsync();
        await readTwo.Lease.DisposeAsync();
        var mutation = await mutationTask.WaitAsync(TimeSpan.FromSeconds(2));
        RequireAllowed(mutation, "目标读锁释放后应允许状态变更租约。");
        await mutation.Lease.DisposeAsync();
        Assert(probe.LastTarget == "app:editor", "目标标识没有规范化后交给占用探针。");

        var busyCoordinator = new InteractionCoordinator(new FixedTargetUsageProbe(InteractionTargetUsage.InUse));
        var busy = await busyCoordinator.AcquireAsync(ToolInteractionOperation.TargetStateChange,
            ToolExecutionAccess.BackgroundOnly, "app:chat", CancellationToken.None);
        Assert(busy.Denial?.ErrorCode == "TARGET_IN_USE", "用户正在使用目标应用时没有失败关闭。");

        var unknownCoordinator = new InteractionCoordinator();
        var unknown = await unknownCoordinator.AcquireAsync(ToolInteractionOperation.TargetStateChange,
            ToolExecutionAccess.BackgroundOnly, "app:chat", CancellationToken.None);
        Assert(unknown.Denial?.ErrorCode == "TARGET_USAGE_UNKNOWN", "缺少用户活动探针时没有失败关闭。");

        var invalid = await coordinator.AcquireAsync(ToolInteractionOperation.ReadOnlyTarget,
            ToolExecutionAccess.BackgroundOnly, "app/chat", CancellationToken.None);
        Assert(invalid.Denial?.ErrorCode == "INVALID_INTERACTION_TARGET", "不安全目标标识未被拒绝。");

        var backgroundDesktop = await coordinator.AcquireAsync(ToolInteractionOperation.DesktopExclusive,
            ToolExecutionAccess.BackgroundOnly, "", CancellationToken.None);
        Assert(backgroundDesktop.Denial?.ErrorCode == "FOREGROUND_REQUIRED", "后台任务获得了桌面独占租约。");

        var firstDesktop = await coordinator.AcquireAsync(ToolInteractionOperation.DesktopExclusive,
            ToolExecutionAccess.ExplicitUserInteraction, "", CancellationToken.None);
        RequireAllowed(firstDesktop, "明确用户交互未获得桌面租约。");
        var secondDesktopTask = coordinator.AcquireAsync(ToolInteractionOperation.DesktopExclusive,
            ToolExecutionAccess.ExplicitUserInteraction, "", CancellationToken.None).AsTask();
        await Task.Delay(50);
        Assert(!secondDesktopTask.IsCompleted, "桌面独占租约没有串行化。");
        await firstDesktop.Lease.DisposeAsync();
        var secondDesktop = await secondDesktopTask.WaitAsync(TimeSpan.FromSeconds(2));
        RequireAllowed(secondDesktop, "桌面租约释放后没有允许后续用户交互。");
        await secondDesktop.Lease.DisposeAsync();
    }

    private static void RequireAllowed(InteractionAdmission admission, string message)
    {
        if (admission.Denial is not null) throw new InvalidOperationException(message + " " + admission.Denial.ErrorCode);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FixedTargetUsageProbe(InteractionTargetUsage usage) : IInteractionTargetUsageProbe
    {
        public string? LastTarget { get; private set; }

        public ValueTask<InteractionTargetUsage> GetUsageAsync(string targetId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastTarget = targetId;
            return ValueTask.FromResult(usage);
        }
    }
}
