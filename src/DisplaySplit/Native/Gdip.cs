using System.Runtime.InteropServices;

namespace DisplaySplit.Native;

/// <summary>Minimal GDI+ flat API surface (gdiplus.dll ships with every Windows install).</summary>
public static unsafe partial class Gdip
{
    const string Dll = "gdiplus.dll";

    [StructLayout(LayoutKind.Sequential)]
    public struct StartupInput
    {
        public uint GdiplusVersion;
        public nint DebugEventCallback;
        public int SuppressBackgroundThread;
        public int SuppressExternalCodecs;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RectF
    {
        public float X, Y, Width, Height;
        public RectF(float x, float y, float w, float h) { X = x; Y = y; Width = w; Height = h; }
    }

    public const int PixelFormat32bppPARGB = 0x000E200B;
    public const int SmoothingModeAntiAlias = 4;
    public const int TextRenderingHintAntiAliasGridFit = 3;
    public const int CompositingModeSourceCopy = 1;
    public const int CompositingModeSourceOver = 0;
    public const int PixelOffsetModeHalf = 4;
    public const int UnitPixel = 2;
    public const int FontStyleRegular = 0, FontStyleBold = 1;
    public const int StringAlignmentNear = 0, StringAlignmentCenter = 1, StringAlignmentFar = 2;
    public const int StringFormatFlagsNoWrap = 0x1000;
    public const int StringTrimmingEllipsisCharacter = 3;
    public const int DashStyleSolid = 0, DashStyleDash = 1, DashStyleDot = 2;
    public const int CombineModeReplace = 0;

    [LibraryImport(Dll)] public static partial int GdiplusStartup(out nint token, in StartupInput input, nint output);
    [LibraryImport(Dll)] public static partial void GdiplusShutdown(nint token);

    [LibraryImport(Dll)] public static partial int GdipCreateBitmapFromScan0(int w, int h, int stride, int format, void* scan0, out nint bitmap);
    [LibraryImport(Dll)] public static partial int GdipDisposeImage(nint image);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GdipSaveImageToFile(nint image, string file, in Guid encoder, nint parameters);
    [LibraryImport(Dll)] public static partial int GdipGetImageGraphicsContext(nint image, out nint graphics);
    [LibraryImport(Dll)] public static partial int GdipDeleteGraphics(nint graphics);
    [LibraryImport(Dll)] public static partial int GdipFlush(nint graphics, int intention);
    [LibraryImport(Dll)] public static partial int GdipSetSmoothingMode(nint graphics, int mode);
    [LibraryImport(Dll)] public static partial int GdipSetPixelOffsetMode(nint graphics, int mode);
    [LibraryImport(Dll)] public static partial int GdipSetTextRenderingHint(nint graphics, int hint);
    [LibraryImport(Dll)] public static partial int GdipSetCompositingMode(nint graphics, int mode);
    [LibraryImport(Dll)] public static partial int GdipGraphicsClear(nint graphics, uint argb);
    [LibraryImport(Dll)] public static partial int GdipSetClipRectI(nint graphics, int x, int y, int w, int h, int combine);
    [LibraryImport(Dll)] public static partial int GdipResetClip(nint graphics);

    [LibraryImport(Dll)] public static partial int GdipCreateSolidFill(uint argb, out nint brush);
    [LibraryImport(Dll)] public static partial int GdipSetSolidFillColor(nint brush, uint argb);
    [LibraryImport(Dll)] public static partial int GdipDeleteBrush(nint brush);
    [LibraryImport(Dll)] public static partial int GdipCreatePen1(uint argb, float width, int unit, out nint pen);
    [LibraryImport(Dll)] public static partial int GdipSetPenColor(nint pen, uint argb);
    [LibraryImport(Dll)] public static partial int GdipSetPenWidth(nint pen, float width);
    [LibraryImport(Dll)] public static partial int GdipSetPenDashStyle(nint pen, int style);
    [LibraryImport(Dll)] public static partial int GdipDeletePen(nint pen);

    [LibraryImport(Dll)] public static partial int GdipCreatePath(int fillMode, out nint path);
    [LibraryImport(Dll)] public static partial int GdipResetPath(nint path);
    [LibraryImport(Dll)] public static partial int GdipAddPathArc(nint path, float x, float y, float w, float h, float start, float sweep);
    [LibraryImport(Dll)] public static partial int GdipAddPathLine(nint path, float x1, float y1, float x2, float y2);
    [LibraryImport(Dll)] public static partial int GdipClosePathFigure(nint path);
    [LibraryImport(Dll)] public static partial int GdipDeletePath(nint path);
    [LibraryImport(Dll)] public static partial int GdipFillPath(nint graphics, nint brush, nint path);
    [LibraryImport(Dll)] public static partial int GdipDrawPath(nint graphics, nint pen, nint path);
    [LibraryImport(Dll)] public static partial int GdipFillRectangleI(nint graphics, nint brush, int x, int y, int w, int h);
    [LibraryImport(Dll)] public static partial int GdipFillRectangle(nint graphics, nint brush, float x, float y, float w, float h);
    [LibraryImport(Dll)] public static partial int GdipDrawLine(nint graphics, nint pen, float x1, float y1, float x2, float y2);
    [LibraryImport(Dll)] public static partial int GdipFillPie(nint graphics, nint brush, float x, float y, float w, float h, float start, float sweep);
    [LibraryImport(Dll)] public static partial int GdipFillEllipse(nint graphics, nint brush, float x, float y, float w, float h);
    [LibraryImport(Dll)] public static partial int GdipDrawEllipse(nint graphics, nint pen, float x, float y, float w, float h);

    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GdipCreateFontFamilyFromName(string name, nint collection, out nint family);
    [LibraryImport(Dll)] public static partial int GdipDeleteFontFamily(nint family);
    [LibraryImport(Dll)] public static partial int GdipCreateFont(nint family, float size, int style, int unit, out nint font);
    [LibraryImport(Dll)] public static partial int GdipDeleteFont(nint font);
    [LibraryImport(Dll)] public static partial int GdipCreateStringFormat(int flags, ushort lang, out nint format);
    [LibraryImport(Dll)] public static partial int GdipDeleteStringFormat(nint format);
    [LibraryImport(Dll)] public static partial int GdipSetStringFormatAlign(nint format, int align);
    [LibraryImport(Dll)] public static partial int GdipSetStringFormatLineAlign(nint format, int align);
    [LibraryImport(Dll)] public static partial int GdipSetStringFormatTrimming(nint format, int trimming);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GdipDrawString(nint graphics, string text, int length, nint font, in RectF layout, nint format, nint brush);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GdipMeasureString(nint graphics, string text, int length, nint font, in RectF layout, nint format,
        out RectF bounds, out int codepointsFitted, out int linesFilled);
}
