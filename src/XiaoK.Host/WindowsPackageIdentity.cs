using System.Runtime.InteropServices;
using System.Text;

namespace XiaoK.Host;

internal static class WindowsPackageIdentity
{
    private const int ErrorInsufficientBuffer = 122;
    private const int AppModelErrorNoPackage = 15700;

    public static bool IsPresent
    {
        get
        {
            uint bufferLength = 0;
            var result = GetCurrentPackageFullName(ref bufferLength, null);
            if (result == AppModelErrorNoPackage) return false;
            if (result is ErrorInsufficientBuffer or 0) return true;
            throw new InvalidOperationException($"无法确认小K的打包状态（Windows 错误 {result}）。");
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetCurrentPackageFullName")]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, StringBuilder? packageFullName);
}
