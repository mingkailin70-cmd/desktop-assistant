using System.Diagnostics;

namespace XiaoK.Inference;

/// <summary>区分交互推理与可延后的后台推理。</summary>
public enum ModelWorkloadPriority
{
    Interactive,
    Background
}

/// <summary>将后台模型进程降为低于普通优先级，交互请求恢复普通优先级。</summary>
public static class ModelWorkloadPriorityPolicy
{
    public static ProcessPriorityClass ToProcessPriorityClass(ModelWorkloadPriority priority) => priority switch
    {
        ModelWorkloadPriority.Interactive => ProcessPriorityClass.Normal,
        ModelWorkloadPriority.Background => ProcessPriorityClass.BelowNormal,
        _ => throw new ArgumentOutOfRangeException(nameof(priority))
    };
}

/// <summary>在取得模型租约前按调用类型设置本地推理进程优先级。</summary>
public interface IWorkloadPriorityModelRuntime : IManagedModelRuntime
{
    ValueTask<IAsyncDisposable> AcquireAsync(ModelWorkloadPriority priority, CancellationToken cancellationToken);
}
