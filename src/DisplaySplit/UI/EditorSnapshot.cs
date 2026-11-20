using DisplaySplit.Core;
using DisplaySplit.Native;
using DisplaySplit.Platform;

namespace DisplaySplit.UI;

/// <summary>
/// Developer diagnostic: <c>DisplaySplit.exe --snapshot-editor &lt;dir&gt;</c> renders the editor in a few interaction
/// states to PNG files (works on a locked/headless desktop) and measures frame times. Use with DISPLAYSPLIT_HOME.
/// </summary>
internal static unsafe class EditorSnapshot
{
    static readonly Guid PngEncoder = new("557CF406-1A04-11D3-9A73-0000F81EF32E");

    public static int Run(string dir)
    {
        Directory.CreateDirectory(dir);
        MonitorService.Refresh();
        var store = new LayoutStore(() => 0);
        store.Bind(MonitorService.Monitors);
        EditorSession? session = null;
        session = new EditorSession(store, 0, coach: true, (_, _) => { });
        var overlay = session.Overlays.First();
        var area = overlay.State.Monitor.Work;
        int w = area.Width, h = area.Height;

        void Move(int x, int y, bool shift = false)
        {
            overlay.TestSetShift(shift);
            User32.SendMessageW(overlay.Handle, C.WM_MOUSEMOVE, 0, C.MakeLParam(x, y));
            overlay.TestRenderNow();
        }

        overlay.TestRenderNow();
        Save(overlay, Path.Combine(dir, "1-first-run-coach.png"));
        session.DismissCoach();
        overlay.TestRenderNow();

        Move(w / 2 + 300, h / 2);
        Save(overlay, Path.Combine(dir, "2-hover-split-preview.png"));

        Move(w / 2 + 300, h / 2 + 200, shift: true);
        Save(overlay, Path.Combine(dir, "3-hover-horizontal-preview.png"));

        int divX = w / 4; // focus layout: divider at 25 %
        Move(divX, h / 2);
        Save(overlay, Path.Combine(dir, "4-hover-divider.png"));

        // Toolbar hover
        var tb = overlay.TestToolbar;
        Move(tb.Right - 40, tb.Top + tb.Height / 2);
        Save(overlay, Path.Combine(dir, "5-hover-save.png"));

        // Frame-time measurement: simulate a divider drag across 40 frames.
        User32.SendMessageW(overlay.Handle, C.WM_LBUTTONDOWN, 1, C.MakeLParam(divX, h / 2));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        double worst = 0;
        for (int i = 0; i < 40; i++)
        {
            long t0 = sw.ElapsedTicks;
            User32.SendMessageW(overlay.Handle, C.WM_MOUSEMOVE, 1, C.MakeLParam(divX + i * 12, h / 2));
            overlay.TestRenderNow();
            worst = Math.Max(worst, (sw.ElapsedTicks - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        }
        double avg = sw.Elapsed.TotalMilliseconds / 40;
        Save(overlay, Path.Combine(dir, "6-dragging-divider.png"));
        User32.SendMessageW(overlay.Handle, C.WM_LBUTTONUP, 0, C.MakeLParam(divX + 480, h / 2));
        overlay.TestRenderNow();

        // Hover-move frame time inside a zone (preview line only).
        sw.Restart();
        for (int i = 0; i < 40; i++)
        {
            User32.SendMessageW(overlay.Handle, C.WM_MOUSEMOVE, 0, C.MakeLParam(w / 2 + i * 10, h / 2));
            overlay.TestRenderNow();
        }
        double hoverAvg = sw.Elapsed.TotalMilliseconds / 40;

        // Split via click, then show the result.
        Move(w / 2 + 200, h / 2);
        User32.SendMessageW(overlay.Handle, C.WM_LBUTTONDOWN, 1, C.MakeLParam(w / 2 + 200, h / 2));
        User32.SendMessageW(overlay.Handle, C.WM_LBUTTONUP, 0, C.MakeLParam(w / 2 + 200, h / 2));
        overlay.TestRenderNow();
        Save(overlay, Path.Combine(dir, "7-after-split.png"));

        File.WriteAllText(Path.Combine(dir, "timings.txt"),
            $"divider drag: avg {avg:F2} ms/frame, worst {worst:F2} ms\nhover preview: avg {hoverAvg:F2} ms/frame\n");
        Console.Out.Flush();
        session.Dispose();
        return 0;
    }

    static void Save(EditorOverlay overlay, string path)
    {
        var s = overlay.TestSurface!;
        Gdip.GdipFlush(s.G, 1);
        // Composite over a mid-grey "desktop" so translucency is visible in the PNG.
        using var outS = new Surface(s.Width, s.Height);
        outS.FillRaw(0, 0, s.Width, s.Height, 0xFF5A6B7D);
        var bf = new BLENDFUNCTION { BlendOp = C.AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = C.AC_SRC_ALPHA };
        Msimg32.AlphaBlend(outS.Hdc, 0, 0, s.Width, s.Height, s.Hdc, 0, 0, s.Width, s.Height, bf);
        Gdip.GdipCreateBitmapFromScan0(s.Width, s.Height, s.Width * 4, Gdip.PixelFormat32bppPARGB, outS.Bits, out nint bmp);
        var clsid = PngEncoder;
        Gdip.GdipSaveImageToFile(bmp, path, clsid, 0);
        Gdip.GdipDisposeImage(bmp);
    }
}
