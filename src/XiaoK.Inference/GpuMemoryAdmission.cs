namespace XiaoK.Inference;

public sealed record GpuMemorySnapshot(long FreeMiB, long TotalMiB);

public sealed record GpuMemoryAdmission(bool Allowed, string Reason);

public static class GpuMemoryAdmissionPolicy
{
    public const long MinimumReserveMiB = 1024;

    public static GpuMemoryAdmission Evaluate(GpuMemorySnapshot? snapshot, long expectedModelMiB)
    {
        if (expectedModelMiB < 0)
            return new(false, "模型显存预算无效。");
        if (snapshot is null || snapshot.FreeMiB <= 0 || snapshot.TotalMiB <= 0 || snapshot.FreeMiB > snapshot.TotalMiB)
            return new(false, "无法取得有效的独显可用显存读数。");
        if (expectedModelMiB > snapshot.TotalMiB)
            return new(false, "模型显存预算超过显卡总显存。");

        long requiredMiB;
        try { requiredMiB = checked(expectedModelMiB + MinimumReserveMiB); }
        catch (OverflowException) { return new(false, "模型显存预算溢出。"); }

        return snapshot.FreeMiB >= requiredMiB
            ? new(true, "可用显存满足模型预算和 1 GiB 保留量。")
            : new(false, $"可用显存不足：需要模型预算 {expectedModelMiB} MiB 并额外保留 {MinimumReserveMiB} MiB。");
    }

    public static bool HasMinimumReserve(GpuMemorySnapshot? snapshot) =>
        snapshot is not null && snapshot.FreeMiB >= MinimumReserveMiB && snapshot.FreeMiB <= snapshot.TotalMiB;
}

public sealed class LowGpuMemoryException(string message) : Exception(message);

public interface IGpuMemoryProbe
{
    Task<GpuMemorySnapshot?> ReadAsync(CancellationToken cancellationToken);
}
