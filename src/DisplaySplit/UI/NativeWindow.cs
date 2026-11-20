using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DisplaySplit.Native;
using DisplaySplit.Platform;

namespace DisplaySplit.UI;

/// <summary>Thin base class over a Win32 window. Instances are bound to the HWND via GWLP_USERDATA (GCHandle).</summary>
public abstract unsafe class NativeWindow : IDisposable
{
    const uint WM_NCCREATE = 0x0081, WM_NCDESTROY = 0x0082;
    static readonly HashSet<string> Registered = [];

    GCHandle _self;
    public nint Handle { get; private set; }

    protected static void EnsureClass(string className, uint style = 0, nint cursor = 0)
    {
        if (!Registered.Add(className)) return;
        fixed (char* name = className)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                style = style,
                lpfnWndProc = &StaticWndProc,
                hInstance = Kernel32.GetModuleHandleW(null),
                hCursor = cursor != 0 ? cursor : User32.LoadCursorW(0, C.IDC_ARROW),
                lpszClassName = name,
            };
            if (User32.RegisterClassExW(wc) == 0)
                Log.Warn($"RegisterClassEx({className}) failed: {Marshal.GetLastPInvokeError()}");
        }
    }

    protected void CreateHandle(string className, string? title, uint style, uint exStyle, int x, int y, int w, int h, nint parent = 0)
    {
        _self = GCHandle.Alloc(this);
        Handle = User32.CreateWindowExW(exStyle, className, title, style, x, y, w, h, parent, 0,
            Kernel32.GetModuleHandleW(null), GCHandle.ToIntPtr(_self));
        if (Handle == 0)
        {
            _self.Free();
            throw new InvalidOperationException($"CreateWindowEx({className}) failed: {Marshal.GetLastPInvokeError()}");
        }
    }

    protected abstract nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);

    protected virtual void OnDestroyed() { }

    [UnmanagedCallersOnly]
    static nint StaticWndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            if (msg == WM_NCCREATE)
            {
                // CREATESTRUCTW.lpCreateParams is the first field.
                nint param = *(nint*)lParam;
                User32.SetWindowLongPtr(hwnd, C.GWLP_USERDATA, param);
                if (GCHandle.FromIntPtr(param).Target is NativeWindow nw) nw.Handle = hwnd;
            }
            nint ud = User32.GetWindowLongPtr(hwnd, C.GWLP_USERDATA);
            if (ud == 0) return User32.DefWindowProcW(hwnd, msg, wParam, lParam);
            var target = GCHandle.FromIntPtr(ud).Target as NativeWindow;
            if (target is null) return User32.DefWindowProcW(hwnd, msg, wParam, lParam);
            if (msg == WM_NCDESTROY)
            {
                nint r = User32.DefWindowProcW(hwnd, msg, wParam, lParam);
                User32.SetWindowLongPtr(hwnd, C.GWLP_USERDATA, 0);
                target.Handle = 0;
                if (target._self.IsAllocated) target._self.Free();
                target.OnDestroyed();
                return r;
            }
            return target.WndProc(hwnd, msg, wParam, lParam);
        }
        catch (Exception ex)
        {
            Log.Error($"WndProc msg=0x{msg:X}", ex);
            return User32.DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }

    public virtual void Dispose()
    {
        if (Handle != 0) User32.DestroyWindow(Handle);
        GC.SuppressFinalize(this);
    }
}
