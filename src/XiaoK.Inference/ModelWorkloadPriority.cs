using System.Diagnostics;

namespace XiaoK.Inference;

/// <summary>区分交互推理与可延后的后台推理。</summary>
public enum ModelWorkloadPriority
{
    Interactive,
    Background
}

/// <summary>标记模型租约的前台优先级；运行时还需给后台进程施加 CPU 硬上限。</summary>
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
