using DisplaySplit.Native;

namespace DisplaySplit.UI;

public sealed record MenuItem(int Id, string Text, bool Checked = false, bool Enabled = true, List<MenuItem>? Children = null,
    bool Separator = false, bool Default = false, bool Radio = false)
{
    public static MenuItem Sep() => new(0, "", Separator: true);
}

/// <summary>Notification-area icon with a runtime-drawn glyph and a popup menu.</summary>
public sealed unsafe class TrayIcon : IDisposable
{
    readonly nint _host;
    nint _icon;
    bool _added;
    string _tip = "DisplaySplit";
    bool _paused;

    public TrayIcon(nint host)
    {
        _host = host;
        _icon = IconFactory.Create(_paused);
        Add();
    }

    NOTIFYICONDATAW Data(uint flags)
    {
        var d = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW), hWnd = _host, uID = 1, uFlags = flags,
            uCallbackMessage = App.WM_APP_TRAY, hIcon = _icon,
        };
        Copy(_tip, d.szTip, 128);
        return d;
    }

    static void Copy(string s, char* dst, int max)
    {
        int n = Math.Min(s.Length, max - 1);
        for (int i = 0; i < n; i++) dst[i] = s[i];
        dst[n] = '\0';
    }

    public void Add()
    {
        var d = Data(C.NIF_MESSAGE | C.NIF_ICON | C.NIF_TIP | C.NIF_SHOWTIP);
        Shell32.Shell_NotifyIconW(C.NIM_DELETE, d);
        _added = Shell32.Shell_NotifyIconW(C.NIM_ADD, d);
        d.uVersion = C.NOTIFYICON_VERSION_4;
        Shell32.Shell_NotifyIconW(C.NIM_SETVERSION, d);
    }

    public void SetState(string tip, bool paused)
    {
        _tip = tip;
        if (paused != _paused)
        {
            _paused = paused;
            var old = _icon;
            _icon = IconFactory.Create(paused);
            if (old != 0) User32.DestroyIcon(old);
        }
        if (_added) Shell32.Shell_NotifyIconW(C.NIM_MODIFY, Data(C.NIF_ICON | C.NIF_TIP | C.NIF_SHOWTIP));
    }

    /// <summary>
    /// For the elevated instance: is a tray callback plausibly a real user interaction? True if the cursor is on our
    /// icon (mouse), or the taskbar / its overflow — which belong to the shell process — has the keyboard (keyboard).
    /// </summary>
    public bool IsGenuineInteraction()
    {
        var id = new NOTIFYICONIDENTIFIER { cbSize = (uint)sizeof(NOTIFYICONIDENTIFIER), hWnd = _host, uID = 1 };
        if (User32.GetCursorPos(out var p) && Shell32.Shell_NotifyIconGetRect(id, out RECT r) == 0 &&
            p.X >= r.Left - 2 && p.X < r.Right + 2 && p.Y >= r.Top - 2 && p.Y < r.Bottom + 2)
            return true;
        nint fg = User32.GetForegroundWindow(), shell = User32.GetShellWindow();
        if (fg == 0 || shell == 0) return false;
        User32.GetWindowThreadProcessId(fg, out uint fgPid);
        User32.GetWindowThreadProcessId(shell, out uint shellPid);
        return fgPid == shellPid;
    }

    public void Balloon(string title, string text)
    {
        var d = Data(C.NIF_INFO);
        Copy(text, d.szInfo, 256);
        Copy(title, d.szInfoTitle, 64);
        d.dwInfoFlags = C.NIIF_INFO | C.NIIF_NOSOUND;
        Shell32.Shell_NotifyIconW(C.NIM_MODIFY, d);
    }

    /// <summary>Show a popup menu at (x, y); returns the chosen id or 0.</summary>
    public int ShowMenu(IReadOnlyList<MenuItem> items, int x, int y)
    {
        var menus = new List<nint>();
        nint root = Build(items, menus);
        User32.SetForegroundWindow(_host); // required so the menu closes when clicking elsewhere
        int cmd = User32.TrackPopupMenuEx(root, C.TPM_RIGHTBUTTON | C.TPM_RETURNCMD | C.TPM_NONOTIFY | C.TPM_BOTTOMALIGN | C.TPM_RIGHTALIGN,
            x, y, _host, 0);
        User32.PostMessageW(_host, C.WM_NULL, 0, 0);
        User32.DestroyMenu(root); // destroys submenus too
        return cmd;
    }

    static nint Build(IReadOnlyList<MenuItem> items, List<nint> menus)
    {
        nint m = User32.CreatePopupMenu();
        menus.Add(m);
        foreach (var it in items)
        {
            if (it.Separator) { User32.AppendMenuW(m, C.MF_SEPARATOR, 0, null); continue; }
            uint flags = C.MF_STRING;
            if (it.Checked) flags |= C.MF_CHECKED;
            if (it.Radio) flags |= C.MFT_RADIOCHECK;
            if (!it.Enabled) flags |= C.MF_GRAYED;
            if (it.Default) flags |= C.MF_DEFAULT;
            if (it.Children is { Count: > 0 })
            {
                nint sub = Build(it.Children, menus);
                User32.AppendMenuW(m, flags | C.MF_POPUP, (nuint)sub, it.Text);
            }
            else User32.AppendMenuW(m, flags, (nuint)it.Id, it.Text);
        }
        return m;
    }

    public void Dispose()
    {
        if (_added) Shell32.Shell_NotifyIconW(C.NIM_DELETE, Data(0));
        _added = false;
        if (_icon != 0) User32.DestroyIcon(_icon);
        _icon = 0;
    }
}

/// <summary>Draws the tray glyph (three columns, centre one accented) at the current small-icon size.</summary>
public static unsafe class IconFactory
{
    public static nint Create(bool paused)
    {
        int size = User32.GetSystemMetrics(49 /* SM_CXSMICON */);
        if (size <= 0) size = 16;
        // Render at the system DPI small-icon size.
        uint dpi = User32.GetDpiForWindow(User32.GetDesktopWindow());
        if (dpi == 0) dpi = 96;
        size = User32.GetSystemMetricsForDpi(49, dpi);
        using var s = new Surface(size, size);
        float pad = Math.Max(1, size * 0.06f), w = size - 2 * pad, h = w * 0.74f, y = (size - h) / 2;
        uint frame = paused ? 0xFF6B7280 : 0xFFE6E9EF;
        uint accent = paused ? 0xFF9CA3AF : Theme.Accent;
        s.StrokeRound(pad + 0.5f, y + 0.5f, w - 1, h - 1, size * 0.14f, frame, Math.Max(1f, size / 16f));
        float gap = Math.Max(1f, size / 16f), inner = w - 4 * gap - 2, col = (inner - 2 * gap) / 4f;
        float x0 = pad + 2 * gap + 1, top = y + 2 * gap + 1, ih = h - 4 * gap - 2;
        s.FillRound(x0, top, col, ih, 1, frame & 0x99FFFFFF);
        s.FillRound(x0 + col + gap, top, col * 2, ih, 1, accent);
        s.FillRound(x0 + col * 3 + gap * 2, top, col, ih, 1, frame & 0x99FFFFFF);

        // Colour bitmap = our DIB; mask = all zero (alpha channel drives transparency).
        Gdip.GdipFlush(s.G, 1);
        nint mask = Gdi32.CreateBitmap(size, size, 1, 1, null);
        nint color = s.Dib;
        var info = new ICONINFO { fIcon = 1, hbmMask = mask, hbmColor = color };
        nint icon = User32.CreateIconIndirect(info);
        Gdi32.DeleteObject(mask);
        return icon; // the colour DIB is owned (and freed) by the surface
    }
}
