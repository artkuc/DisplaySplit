using DisplaySplit.Core;
using DisplaySplit.Native;

namespace DisplaySplit.UI;

/// <summary>
/// Zone hints shown while Shift-dragging, and the short "snapped" flash. One small LWA_ALPHA window per zone
/// (no full-screen surface), all click-through and never activated so the OS move loop keeps capture.
/// </summary>
public sealed class DragOverlay : IDisposable
{
    sealed class ZoneWindow : NativeWindow
    {
        const string Cls = "DisplaySplit.Drag";
        public bool Highlighted;
        public string Label = "";
        public RectI Rect;
        static nint _font;
        static uint _fontDpi;

        public ZoneWindow()
        {
            EnsureClass(Cls);
            CreateHandle(Cls, null, C.WS_POPUP,
                C.WS_EX_LAYERED | C.WS_EX_TRANSPARENT | C.WS_EX_TOOLWINDOW | C.WS_EX_NOACTIVATE | C.WS_EX_TOPMOST,
                0, 0, 1, 1);
        }

        bool _visible;

        public void Show(RectI r, string label, bool highlighted)
        {
            bool changed = r != Rect || label != Label || highlighted != Highlighted;
            if (!changed && _visible) return; // nothing to do this frame
            _visible = true;
            Rect = r; Label = label; Highlighted = highlighted;
            User32.SetLayeredWindowAttributes(Handle, 0, (byte)(highlighted ? 150 : 70), C.LWA_ALPHA);
            User32.SetWindowPos(Handle, C.HWND_TOPMOST, r.Left, r.Top, r.Width, r.Height,
                C.SWP_NOACTIVATE | C.SWP_SHOWWINDOW | C.SWP_NOOWNERZORDER);
            if (changed) unsafe { User32.InvalidateRect(Handle, null, false); }
        }

        public void Hide()
        {
            if (!_visible) return;
            _visible = false;
            User32.ShowWindow(Handle, C.SW_HIDE);
        }

        protected override unsafe nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
        {
            switch (msg)
            {
                case C.WM_MOUSEACTIVATE: return C.MA_NOACTIVATE;
                case C.WM_NCHITTEST: return C.HTTRANSPARENT;
                case C.WM_ERASEBKGND: return 1;
                case C.WM_PAINT:
                {
                    nint dc = User32.BeginPaint(hwnd, out var ps);
                    User32.GetClientRect(hwnd, out var rc);
                    uint bg = Highlighted ? C.Rgb(0x2D, 0x5F, 0xC4) : C.Rgb(0x1E, 0x23, 0x2C);
                    nint b = Gdi32.CreateSolidBrush(bg);
                    User32.FillRect(dc, rc, b);
                    Gdi32.DeleteObject(b);
                    nint border = Gdi32.CreateSolidBrush(C.Rgb(0x4C, 0x8D, 0xFF));
                    int t = Math.Max(2, (int)(User32.GetDpiForWindow(hwnd) / 48));
                    for (int i = 0; i < t; i++)
                    {
                        var r = new RECT(rc.Left + i, rc.Top + i, rc.Right - i, rc.Bottom - i);
                        User32.FrameRect(dc, r, border);
                    }
                    Gdi32.DeleteObject(border);
                    uint dpi = User32.GetDpiForWindow(hwnd);
                    if (_font == 0 || _fontDpi != dpi)
                    {
                        if (_font != 0) Gdi32.DeleteObject(_font);
                        _font = Gdi32.CreateFontW(-(int)(56 * dpi / 96), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
                        _fontDpi = dpi;
                    }
                    nint old = Gdi32.SelectObject(dc, _font);
                    Gdi32.SetBkMode(dc, C.TRANSPARENT);
                    Gdi32.SetTextColor(dc, C.Rgb(0xF2, 0xF4, 0xF8));
                    var tr = rc;
                    User32.DrawTextW(dc, Label, Label.Length, ref tr, 0x1 | 0x4 | 0x20); // center|vcenter|singleline
                    Gdi32.SelectObject(dc, old);
                    User32.EndPaint(hwnd, ps);
                    return 0;
                }
            }
            return User32.DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }

    readonly List<ZoneWindow> _pool = [];
    int _used;
    nuint _flashTimer;

    /// <summary>Show all zones, highlighting <paramref name="highlightId"/> (0 = none).</summary>
    public void ShowZones(IReadOnlyList<Zone> zones, int highlightId)
    {
        App.CancelTimeout(ref _flashTimer);
        for (int i = 0; i < zones.Count; i++)
        {
            if (i >= _pool.Count) _pool.Add(new ZoneWindow());
            _pool[i].Show(Shrink(zones[i].Rect), (zones[i].Index + 1).ToString(), zones[i].Id == highlightId);
        }
        for (int i = zones.Count; i < _used; i++) _pool[i].Hide();
        _used = zones.Count;
    }

    static RectI Shrink(RectI r) => r.Width > 24 && r.Height > 24 ? r.Inflate(-4, -4) : r;

    public void Hide()
    {
        for (int i = 0; i < _used; i++) _pool[i].Hide();
        _used = 0;
    }

    /// <summary>Brief confirmation flash on a zone (~250 ms).</summary>
    public void Flash(RectI rect)
    {
        App.CancelTimeout(ref _flashTimer);
        if (_pool.Count == 0) _pool.Add(new ZoneWindow());
        for (int i = 1; i < _used; i++) _pool[i].Hide();
        _pool[0].Show(Shrink(rect), "", true);
        _used = 1;
        _flashTimer = App.SetTimeout(260, () => { _flashTimer = 0; Hide(); });
    }

    public void Dispose()
    {
        foreach (var w in _pool) w.Dispose();
        _pool.Clear();
    }
}
