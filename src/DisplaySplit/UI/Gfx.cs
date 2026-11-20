using DisplaySplit.Native;

namespace DisplaySplit.UI;

/// <summary>Colour palette (ARGB) for overlays.</summary>
public static class Theme
{
    public const uint Backdrop = 0xB4101318;      // editor background (alpha ≥ 10 everywhere → never click-through)
    public const uint ZoneFill = 0x66252A33;
    public const uint ZoneFillHover = 0x8A2F3542;
    public const uint ZoneBorder = 0xFF4A5363;
    public const uint Accent = 0xFF4C8DFF;
    public const uint AccentSoft = 0x664C8DFF;
    public const uint AccentFill = 0x3D4C8DFF;
    public const uint Danger = 0xFFFF5C5C;
    public const uint Text = 0xFFF2F4F8;
    public const uint TextDim = 0xB3C9CED8;
    public const uint TextFaint = 0x80AEB4BF;
    public const uint Panel = 0xF01B1F27;
    public const uint PanelBorder = 0xFF353C49;
    public const uint Button = 0xFF262C37;
    public const uint ButtonHover = 0xFF323A48;
    public const uint ButtonActive = 0xFF4C8DFF;
}

/// <summary>
/// A 32-bpp premultiplied top-down DIB wrapped as a GDI+ bitmap, plus small drawing helpers.
/// Used with UpdateLayeredWindowIndirect for per-pixel-alpha overlays.
/// </summary>
public sealed unsafe class Surface : IDisposable
{
    public int Width { get; }
    public int Height { get; }
    public nint Hdc { get; }
    public uint* Bits { get; }
    public nint Dib => _dib;
    readonly nint _dib, _oldBmp, _gpBitmap;
    public nint G { get; }
    readonly nint _brush, _pen, _path, _format, _family;
    readonly Dictionary<(float, bool), nint> _fonts = [];

    public Surface(int width, int height)
    {
        Width = width;
        Height = height;
        var bmi = new BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32,
        };
        nint screen = User32.GetDC(0);
        Hdc = Gdi32.CreateCompatibleDC(screen);
        User32.ReleaseDC(0, screen);
        _dib = Gdi32.CreateDIBSection(Hdc, bmi, C.DIB_RGB_COLORS, out void* bits, 0, 0);
        if (_dib == 0) throw new OutOfMemoryException("CreateDIBSection failed");
        Bits = (uint*)bits;
        _oldBmp = Gdi32.SelectObject(Hdc, _dib);
        Gdip.GdipCreateBitmapFromScan0(width, height, width * 4, Gdip.PixelFormat32bppPARGB, bits, out _gpBitmap);
        Gdip.GdipGetImageGraphicsContext(_gpBitmap, out nint g);
        G = g;
        Gdip.GdipSetSmoothingMode(G, Gdip.SmoothingModeAntiAlias);
        Gdip.GdipSetPixelOffsetMode(G, Gdip.PixelOffsetModeHalf);
        Gdip.GdipSetTextRenderingHint(G, Gdip.TextRenderingHintAntiAliasGridFit);
        Gdip.GdipCreateSolidFill(0, out _brush);
        Gdip.GdipCreatePen1(0, 1, Gdip.UnitPixel, out _pen);
        Gdip.GdipCreatePath(0, out _path);
        Gdip.GdipCreateStringFormat(0, 0, out _format);
        if (Gdip.GdipCreateFontFamilyFromName("Segoe UI Variable Text", 0, out _family) != 0)
            Gdip.GdipCreateFontFamilyFromName("Segoe UI", 0, out _family);
    }

    // ---- clipping / clearing

    public void Clip(int x, int y, int w, int h) => Gdip.GdipSetClipRectI(G, x, y, w, h, Gdip.CombineModeReplace);
    public void ResetClip() => Gdip.GdipResetClip(G);

    /// <summary>Fast fill of a rect with a premultiplied colour (no blending), clipped to the surface.</summary>
    public void FillRaw(int x, int y, int w, int h, uint argb)
    {
        int x0 = Math.Max(0, x), y0 = Math.Max(0, y), x1 = Math.Min(Width, x + w), y1 = Math.Min(Height, y + h);
        if (x1 <= x0 || y1 <= y0) return;
        uint pm = Premultiply(argb);
        for (int yy = y0; yy < y1; yy++)
            new Span<uint>(Bits + (long)yy * Width + x0, x1 - x0).Fill(pm);
    }

    /// <summary>Fast fill with an already-premultiplied colour.</summary>
    public void FillRawPremul(int x, int y, int w, int h, uint pm)
    {
        int x0 = Math.Max(0, x), y0 = Math.Max(0, y), x1 = Math.Min(Width, x + w), y1 = Math.Min(Height, y + h);
        if (x1 <= x0 || y1 <= y0) return;
        for (int yy = y0; yy < y1; yy++)
            new Span<uint>(Bits + (long)yy * Width + x0, x1 - x0).Fill(pm);
    }

    public nint Brush => _brush;

    public static uint Premultiply(uint argb)
    {
        uint a = argb >> 24;
        uint r = ((argb >> 16) & 0xFF) * a / 255, g = ((argb >> 8) & 0xFF) * a / 255, b = (argb & 0xFF) * a / 255;
        return (a << 24) | (r << 16) | (g << 8) | b;
    }

    // ---- shapes

    public void FillRect(float x, float y, float w, float h, uint argb)
    {
        Gdip.GdipSetSolidFillColor(_brush, argb);
        Gdip.GdipFillRectangle(G, _brush, x, y, w, h);
    }

    void RoundPath(float x, float y, float w, float h, float r)
    {
        Gdip.GdipResetPath(_path);
        r = Math.Max(0.5f, Math.Min(r, Math.Min(w, h) / 2));
        float d = r * 2;
        Gdip.GdipAddPathArc(_path, x, y, d, d, 180, 90);
        Gdip.GdipAddPathArc(_path, x + w - d, y, d, d, 270, 90);
        Gdip.GdipAddPathArc(_path, x + w - d, y + h - d, d, d, 0, 90);
        Gdip.GdipAddPathArc(_path, x, y + h - d, d, d, 90, 90);
        Gdip.GdipClosePathFigure(_path);
    }

    public void FillRound(float x, float y, float w, float h, float r, uint argb)
    {
        RoundPath(x, y, w, h, r);
        Gdip.GdipSetSolidFillColor(_brush, argb);
        Gdip.GdipFillPath(G, _brush, _path);
    }

    public void StrokeRound(float x, float y, float w, float h, float r, uint argb, float width, int dash = Gdip.DashStyleSolid)
    {
        RoundPath(x, y, w, h, r);
        Gdip.GdipSetPenColor(_pen, argb);
        Gdip.GdipSetPenWidth(_pen, width);
        Gdip.GdipSetPenDashStyle(_pen, dash);
        Gdip.GdipDrawPath(G, _pen, _path);
    }

    public void Line(float x1, float y1, float x2, float y2, uint argb, float width, int dash = Gdip.DashStyleSolid)
    {
        Gdip.GdipSetPenColor(_pen, argb);
        Gdip.GdipSetPenWidth(_pen, width);
        Gdip.GdipSetPenDashStyle(_pen, dash);
        Gdip.GdipDrawLine(G, _pen, x1, y1, x2, y2);
    }

    public void FillCircle(float cx, float cy, float r, uint argb)
    {
        Gdip.GdipSetSolidFillColor(_brush, argb);
        Gdip.GdipFillEllipse(G, _brush, cx - r, cy - r, r * 2, r * 2);
    }

    // ---- text

    nint Font(float px, bool bold)
    {
        if (!_fonts.TryGetValue((px, bold), out var f))
        {
            Gdip.GdipCreateFont(_family, px, bold ? Gdip.FontStyleBold : Gdip.FontStyleRegular, Gdip.UnitPixel, out f);
            _fonts[(px, bold)] = f;
        }
        return f;
    }

    public void Text(string s, float x, float y, float w, float h, float px, uint argb, bool bold = false,
        int hAlign = Gdip.StringAlignmentCenter, int vAlign = Gdip.StringAlignmentCenter)
    {
        if (string.IsNullOrEmpty(s) || w <= 0 || h <= 0) return;
        Gdip.GdipSetStringFormatAlign(_format, hAlign);
        Gdip.GdipSetStringFormatLineAlign(_format, vAlign);
        Gdip.GdipSetStringFormatTrimming(_format, Gdip.StringTrimmingEllipsisCharacter);
        Gdip.GdipSetSolidFillColor(_brush, argb);
        var rc = new Gdip.RectF(x, y, w, h);
        Gdip.GdipDrawString(G, s, s.Length, Font(px, bold), rc, _format, _brush);
    }

    public float MeasureText(string s, float px, bool bold = false)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        Gdip.GdipSetStringFormatAlign(_format, Gdip.StringAlignmentNear);
        var rc = new Gdip.RectF(0, 0, 10000, 1000);
        Gdip.GdipMeasureString(G, s, s.Length, Font(px, bold), rc, _format, out var bounds, out _, out _);
        return bounds.Width;
    }

    /// <summary>Push the (dirty part of the) surface to a layered window.</summary>
    public void Present(nint hwnd, int screenX, int screenY, RECT? dirty = null)
    {
        var dst = new POINT(screenX, screenY);
        var size = new SIZE(Width, Height);
        var src = new POINT(0, 0);
        var blend = new BLENDFUNCTION { BlendOp = C.AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = C.AC_SRC_ALPHA };
        RECT d = dirty ?? new RECT(0, 0, Width, Height);
        var info = new UPDATELAYEREDWINDOWINFO
        {
            cbSize = (uint)sizeof(UPDATELAYEREDWINDOWINFO), pptDst = &dst, psize = &size, hdcSrc = Hdc, pptSrc = &src,
            pblend = &blend, dwFlags = C.ULW_ALPHA, prcDirty = dirty.HasValue ? &d : null,
        };
        User32.UpdateLayeredWindowIndirect(hwnd, info);
    }

    public void Dispose()
    {
        foreach (var f in _fonts.Values) Gdip.GdipDeleteFont(f);
        _fonts.Clear();
        if (_family != 0) Gdip.GdipDeleteFontFamily(_family);
        Gdip.GdipDeleteStringFormat(_format);
        Gdip.GdipDeletePath(_path);
        Gdip.GdipDeletePen(_pen);
        Gdip.GdipDeleteBrush(_brush);
        Gdip.GdipDeleteGraphics(G);
        Gdip.GdipDisposeImage(_gpBitmap);
        Gdi32.SelectObject(Hdc, _oldBmp);
        Gdi32.DeleteObject(_dib);
        Gdi32.DeleteDC(Hdc);
    }
}

public static class GdiPlus
{
    static nint _token;
    public static void Start()
    {
        var input = new Gdip.StartupInput { GdiplusVersion = 1 };
        Gdip.GdiplusStartup(out _token, input, 0);
    }
    public static void Stop()
    {
        if (_token != 0) Gdip.GdiplusShutdown(_token);
        _token = 0;
    }
}
