using System.Runtime.InteropServices;

namespace XiaoK.Inference;

public sealed record SystemMemorySnapshot(long AvailableMiB, long TotalMiB);

public sealed record SystemMemoryAdmission(bool Allowed, string Reason);

public static class SystemMemoryAdmissionPolicy
{
    public const long MinimumRuntimeReserveMiB = 4_096;

    public static SystemMemoryAdmission Evaluate(SystemMemorySnapshot? snapshot, long minimumAvailableMiB)
    {
        if (minimumAvailableMiB <= 0)
            return new(false, "系统可用内存准入阈值无效。");
        if (snapshot is null || snapshot.AvailableMiB <= 0 || snapshot.TotalMiB <= 0
            || snapshot.AvailableMiB > snapshot.TotalMiB)
            return new(false, "无法取得有效的系统可用内存读数。");
        if (minimumAvailableMiB > snapshot.TotalMiB)
            return new(false, "系统可用内存准入阈值超过物理内存总量。");

        return snapshot.AvailableMiB >= minimumAvailableMiB
            ? new(true, "系统可用内存达到模型启动保护线。")
            : new(false, $"系统可用内存不足：需要至少 {minimumAvailableMiB} MiB。未启动模型。");
    }

    public static bool HasRuntimeReserve(SystemMemorySnapshot? snapshot) =>
        snapshot is not null
        && snapshot.AvailableMiB >= MinimumRuntimeReserveMiB
        && snapshot.TotalMiB >= MinimumRuntimeReserveMiB
        && snapshot.AvailableMiB <= snapshot.TotalMiB;
}

public static class WindowsSystemMemoryProbe
{
    public static SystemMemorySnapshot? Read()
    {
        try
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (!GlobalMemoryStatusEx(ref status) || status.TotalPhysical == 0
                || status.AvailablePhysical > status.TotalPhysical)
                return null;

            return new(
                checked((long)(status.AvailablePhysical / 1_048_576)),
                checked((long)(status.TotalPhysical / 1_048_576)));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or OverflowException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}

public sealed class LowSystemMemoryException(string message) : InvalidOperationException(message)
{
    public const string ErrorCode = "LOW_RAM";
}
