using System.Runtime.InteropServices;

namespace XiaoK.Host;

/// <summary>
/// Reads local-segment video-memory usage attributed by DXGI to the current process.
/// This intentionally does not claim to include separately launched inference processes.
/// </summary>
internal static class DxgiProcessMemoryReader
{
    private const uint NvidiaVendorId = 0x10DE;
    private const uint AdapterFlagSoftware = 0x2;
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private const int DxgiMemorySegmentGroupLocal = 0;
    private const int EnumAdapters1VtableIndex = 12; // IDXGIFactory + IDXGIFactory1 method order.
    private const int GetDesc1VtableIndex = 10; // IUnknown + IDXGIObject + IDXGIAdapter + IDXGIAdapter1.
    private const int QueryVideoMemoryInfoVtableIndex = 14; // IDXGIAdapter3 inherits Adapter2 and adds two methods first.
    private const int MaximumAdapters = 16;

    public static string ReadNvidiaHostMemoryUsage()
    {
        var factoryId = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
        var createResult = CreateDXGIFactory1(ref factoryId, out var factory);
        if (createResult < 0 || factory == IntPtr.Zero)
            return "小K Host 独显显存：DXGI 工厂不可用。";

        var foundNvidiaAdapter = false;
        var queriedAdapters = 0;
        ulong currentUsageBytes = 0;

        try
        {
            var enumerate = GetMethod<EnumAdapters1Delegate>(factory, EnumAdapters1VtableIndex);
            for (uint index = 0; index < MaximumAdapters; index++)
            {
                var result = enumerate(factory, index, out var adapter);
                if (result == DxgiErrorNotFound) break;
                if (result < 0 || adapter == IntPtr.Zero) continue;

                try
                {
                    var getDescription = GetMethod<GetDesc1Delegate>(adapter, GetDesc1VtableIndex);
                    if (getDescription(adapter, out var description) < 0
                        || description.VendorId != NvidiaVendorId
                        || (description.Flags & AdapterFlagSoftware) != 0)
                        continue;

                    foundNvidiaAdapter = true;
                    var adapter3Id = new Guid("645967A4-1392-4310-A798-8053CE3E93FD"); // IDXGIAdapter3
                    if (Marshal.QueryInterface(adapter, in adapter3Id, out var adapter3) < 0 || adapter3 == IntPtr.Zero)
                        continue;

                    try
                    {
                        var query = GetMethod<QueryVideoMemoryInfoDelegate>(adapter3, QueryVideoMemoryInfoVtableIndex);
                        if (query(adapter3, nodeIndex: 0, DxgiMemorySegmentGroupLocal, out var memory) >= 0)
                        {
                            currentUsageBytes += memory.CurrentUsage;
                            queriedAdapters++;
                        }
                    }
                    finally
                    {
                        Marshal.Release(adapter3);
                    }
                }
                finally
                {
                    Marshal.Release(adapter);
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or MarshalDirectiveException or SEHException or InvalidOperationException)
        {
            return "小K Host 独显显存：DXGI 查询不可用。";
        }
        finally
        {
            Marshal.Release(factory);
        }

        if (queriedAdapters > 0)
        {
            var usageMiB = currentUsageBytes / (1024d * 1024d);
            return $"小K Host 进程 NVIDIA 本地显存：{usageMiB:N0} MiB（DXGI 当前进程读数；不含独立模型/语音服务进程）";
        }

        return foundNvidiaAdapter
            ? "小K Host 独显显存：驱动未提供当前进程读数。"
            : "小K Host 独显显存：未枚举到 NVIDIA 显卡。";
    }

    private static T GetMethod<T>(IntPtr interfacePointer, int index) where T : Delegate
    {
        var vtable = Marshal.ReadIntPtr(interfacePointer);
        var address = Marshal.ReadIntPtr(vtable, index * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters1Delegate(IntPtr factory, uint index, out IntPtr adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDesc1Delegate(IntPtr adapter, out AdapterDescription1 description);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryVideoMemoryInfoDelegate(IntPtr adapter, uint nodeIndex, int segmentGroup,
        out QueryVideoMemoryInfo memoryInfo);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDescription1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubsystemId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryVideoMemoryInfo
    {
        public ulong Budget;
        public ulong CurrentUsage;
        public ulong AvailableForReservation;
        public ulong CurrentReservation;
    }
}
