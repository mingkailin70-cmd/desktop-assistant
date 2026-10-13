using System.Runtime.InteropServices;

namespace XiaoK.Host;

internal enum PetGlobalMouseEventKind
{
    PointerMoved,
    LeftButtonReleased
}

internal readonly record struct PetGlobalMouseEvent(PetGlobalMouseEventKind Kind, int X, int Y);

/// <summary>
/// Tracks pointer movement only while the user is actively dragging the non-activating pet window.
/// A background window cannot rely on SetCapture to receive movement outside its visible bounds.
/// </summary>
internal sealed class PetGlobalMouseTracker : IDisposable
{
    private const int WhMouseLl = 14;
    private const int HcAction = 0;
    private const int WmMouseMove = 0x0200;
    private const int WmLeftButtonUp = 0x0202;

    private readonly LowLevelMouseProc _hookProcedure;
    private IntPtr _hookHandle;
    private bool _insideHookProcedure;
    private bool _stopRequested;

    public event Action<int, int>? PointerMoved;
    public event Action<int, int>? LeftButtonReleased;

    public PetGlobalMouseTracker() => _hookProcedure = HookProcedure;

    public bool TryStart()
    {
        if (_hookHandle != IntPtr.Zero) return true;
        try
        {
            _hookHandle = SetWindowsHookEx(WhMouseLl, _hookProcedure, GetModuleHandle(null), 0);
            _stopRequested = false;
            return _hookHandle != IntPtr.Zero;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
            or MarshalDirectiveException)
        {
            _hookHandle = IntPtr.Zero;
            return false;
        }
    }

    public void Stop()
    {
        if (_hookHandle == IntPtr.Zero) return;
        _stopRequested = true;
        if (!_insideHookProcedure) ReleaseHook();
    }

    public void Dispose() => Stop();

    internal static bool TryDecodeMouseMessage(int message, int x, int y,
        out PetGlobalMouseEvent pointerEvent)
    {
        if (message == WmMouseMove)
        {
            pointerEvent = new PetGlobalMouseEvent(PetGlobalMouseEventKind.PointerMoved, x, y);
            return true;
        }
        if (message == WmLeftButtonUp)
        {
            pointerEvent = new PetGlobalMouseEvent(PetGlobalMouseEventKind.LeftButtonReleased, x, y);
            return true;
        }

        pointerEvent = default;
        return false;
    }

    private IntPtr HookProcedure(int code, IntPtr wParam, IntPtr lParam)
    {
        var hookHandle = _hookHandle;
        if (code == HcAction && hookHandle != IntPtr.Zero && !_stopRequested)
        {
            _insideHookProcedure = true;
            try
            {
                var message = wParam.ToInt32();
                if (message is WmMouseMove or WmLeftButtonUp)
                {
                    var hookData = Marshal.PtrToStructure<MouseHookData>(lParam);
                    if (TryDecodeMouseMessage(message, hookData.Point.X, hookData.Point.Y, out var pointerEvent))
                    {
                        if (pointerEvent.Kind == PetGlobalMouseEventKind.PointerMoved)
                            PointerMoved?.Invoke(pointerEvent.X, pointerEvent.Y);
                        else
                        {
                            _stopRequested = true;
                            LeftButtonReleased?.Invoke(pointerEvent.X, pointerEvent.Y);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Never let a managed exception cross the native hook callback boundary.
            }
            finally
            {
                _insideHookProcedure = false;
            }
        }

        var next = CallNextHookEx(hookHandle, code, wParam, lParam);
        if (_stopRequested) ReleaseHook();
        return next;
    }

    private void ReleaseHook()
    {
        var hookHandle = _hookHandle;
        _hookHandle = IntPtr.Zero;
        _stopRequested = false;
        if (hookHandle != IntPtr.Zero) _ = UnhookWindowsHookEx(hookHandle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookData
    {
        public NativePoint Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    private delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc callback,
        IntPtr moduleHandle, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hookHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hookHandle, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
