using System.Runtime.InteropServices;
using DisplaySplit.Core;
using DisplaySplit.Native;
using DisplaySplit.Platform;
using DisplaySplit.UI;

namespace DisplaySplit;

internal static class Program
{
    const string MutexName = @"Local\DisplaySplit.SingleInstance.7F3A2C1E";
    const int ERROR_ALREADY_EXISTS = 183, ERROR_ACCESS_DENIED = 5;

    [STAThread]
    private static int Main(string[] args)
    {
        bool autostart = args.Any(a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));
        int snap = Array.FindIndex(args, a => a.Equals("--snapshot-editor", StringComparison.OrdinalIgnoreCase));
        if (snap >= 0 && snap + 1 < args.Length)
        {
            GdiPlus.Start();
            var h = new HostWindow();
            App.Init(h);
            try { return EditorSnapshot.Run(args[snap + 1]); }
            finally { h.Dispose(); GdiPlus.Stop(); }
        }

        nint mutex = Kernel32.CreateMutexW(0, true, MutexName);
        int err = Marshal.GetLastPInvokeError();
        if (mutex == 0 || err is ERROR_ALREADY_EXISTS or ERROR_ACCESS_DENIED)
        {
            // Already running: ask the running instance to open the editor (unless this is a duplicate autostart).
            if (!autostart)
            {
                nint other = User32.FindWindowExW(0, 0, App.HostClass, null);
                if (other != 0)
                {
                    User32.AllowSetForegroundWindow(C.ASFW_ANY);
                    User32.PostMessageW(other, App.WM_APP_OPEN_EDITOR, 0, 0);
                }
            }
            return 0;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("unhandled", e.ExceptionObject as Exception);
        Ole32.CoInitializeEx(0, 2 /* COINIT_APARTMENTTHREADED */);
        GdiPlus.Start();

        var host = new HostWindow();
        App.Init(host);
        // Allow a non-elevated second instance to signal an elevated one.
        User32.ChangeWindowMessageFilterEx(host.Handle, App.WM_APP_OPEN_EDITOR, 1 /* MSGFLT_ALLOW */, 0);

        var settings = Storage.Load("settings.json", JsonCtx.Default.Settings, () => new Settings(), out _);
        settings.ExcludedApps ??= [];
        MonitorService.Refresh();

        Controller? controller = null;
        try
        {
            controller = new Controller(settings, autostart);
            host.Handler = controller;
            App.RunMessageLoop();
        }
        catch (Exception ex)
        {
            Log.Error("fatal", ex);
            return 1;
        }
        finally
        {
            host.Handler = null;
            controller?.Dispose();
            host.Dispose();
            GdiPlus.Stop();
            Kernel32.CloseHandle(mutex);
            Log.Info("exited");
        }
        return 0;
    }
}
