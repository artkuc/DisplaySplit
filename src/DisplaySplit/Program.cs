using System.Runtime.InteropServices;
using DisplaySplit.Core;
using DisplaySplit.Native;
using DisplaySplit.Platform;
using DisplaySplit.UI;

// All native libraries we use are Windows system DLLs: never resolve them from the app folder or PATH.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace DisplaySplit;

internal static class Program
{
    internal const string MutexName = @"Local\DisplaySplit.SingleInstance.7F3A2C1E";
    const int ERROR_ALREADY_EXISTS = 183, ERROR_ACCESS_DENIED = 5;

    [STAThread]
    private static int Main(string[] args)
    {
        // NativeAOT: every DLL we load is a system DLL, so search System32 only (no app dir, current dir or PATH).
        // (The framework-dependent build loads runtime libraries from its own folder, so it keeps the default.)
        if (AdminMode.IsNativeAot) Kernel32.SetDefaultDllDirectories(SecC.LOAD_LIBRARY_SEARCH_SYSTEM32);

        // Elevated installer for admin mode (started via UAC from the tray). Must run before anything touches storage.
        if (args.Contains(AdminMode.ArgInstall, StringComparer.OrdinalIgnoreCase)) return AdminMode.RunInstaller();
        // Non-elevated helper the admin-mode instance uses to change HKCU autostart (it never writes HKCU itself).
        int sa = Array.FindIndex(args, a => a.Equals(AdminMode.ArgSetAutostart, StringComparison.OrdinalIgnoreCase));
        if (sa >= 0)
        {
            if (WindowInfo.SelfElevated || sa + 1 >= args.Length) return 1;
            Autostart.Set(args[sa + 1] == "on");
            return 0;
        }
        // The elevated admin-mode instance (started by the scheduled task): protected storage, hardened process.
        bool adminMode = AdminMode.TryEnterAdminMode(args);

        // Started "as administrator" some other way: an elevated process must not use the user-writable data folder
        // (links there could redirect its writes). Restart without elevation; admin windows need "Manage admin windows".
        // (With UAC off or the built-in Administrator there is no split token and no boundary to protect.)
        if (!adminMode && AdminMode.IsSplitTokenElevated)
        {
            Log.DisableFile();
            string self = Environment.ProcessPath ?? "";
            bool auto = args.Any(a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));
            if (!ShellLauncher.RunUnelevated(self, auto ? "--autostart" : "", null))
                User32.MessageBoxW(0, "DisplaySplit doesn't run as administrator. Start it normally; to manage windows of apps that run as administrator, use \"Manage admin windows\" in its tray menu.",
                    "DisplaySplit", 0x40);
            return 0;
        }

        bool autostart = adminMode
            ? AdminMode.ConsumeAutostartSignal()
            : args.Any(a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));
        int snap = Array.FindIndex(args, a => a.Equals("--snapshot-editor", StringComparison.OrdinalIgnoreCase));
        if (snap >= 0 && snap + 1 < args.Length && !adminMode)
        {
            GdiPlus.Start();
            var h = new HostWindow();
            App.Init(h);
            try { return EditorSnapshot.Run(args[snap + 1]); }
            finally { h.Dispose(); GdiPlus.Stop(); }
        }

        nint mutex = AcquireMutex(out bool alreadyRunning);
        if (alreadyRunning)
        {
            // Already running: ask the running instance to open the editor (unless this is a duplicate autostart).
            if (!autostart) SignalRunningInstance();
            return 0;
        }

        // Admin mode is set up: hand over to the elevated instance (started through the task).
        if (!adminMode && AdminMode.ShouldRedirect())
        {
            if (AdminMode.Redirect(ref mutex, autostart, () => AcquireMutex(out bool running) is var m && !running ? m : 0))
                return 0;
        }
        if (!adminMode) AdminMode.ImportHandoffIfPending();

        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("unhandled", e.ExceptionObject as Exception);
        Ole32.CoInitializeEx(0, 2 /* COINIT_APARTMENTTHREADED */);
        GdiPlus.Start();

        var host = new HostWindow();
        App.Init(host);
        // Allow a non-elevated second instance to signal an elevated one, and medium-integrity Explorer to deliver
        // tray icon clicks. Neither message can do more than open the editor or show the tray menu.
        User32.ChangeWindowMessageFilterEx(host.Handle, App.WM_APP_OPEN_EDITOR, 1 /* MSGFLT_ALLOW */, 0);
        if (WindowInfo.SelfElevated) User32.ChangeWindowMessageFilterEx(host.Handle, App.WM_APP_TRAY, 1 /* MSGFLT_ALLOW */, 0);

        var settings = Storage.Load("settings.json", JsonCtx.Default.Settings, () => new Settings(), out _);
        settings.ExcludedApps ??= [];
        MonitorService.Refresh();

        Controller? controller = null;
        int exitCode = 0;
        try
        {
            controller = new Controller(settings, autostart);
            host.Handler = controller;
            App.RunMessageLoop();
        }
        catch (Exception ex)
        {
            Log.Error("fatal", ex);
            exitCode = 1;
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
        // e.g. switching admin mode on/off: runs after all data is flushed and the single-instance mutex is released.
        if (exitCode == 0 && App.AfterExit is { } after)
        {
            try { after(); } catch (Exception ex) { Log.Error("after exit", ex); }
        }
        return exitCode;
    }

    static nint AcquireMutex(out bool alreadyRunning)
    {
        nint mutex = Kernel32.CreateMutexW(0, true, MutexName);
        int err = Marshal.GetLastPInvokeError();
        // ERROR_ACCESS_DENIED: the mutex exists and belongs to the elevated (admin mode) instance.
        alreadyRunning = mutex == 0 || err is ERROR_ALREADY_EXISTS or ERROR_ACCESS_DENIED;
        if (alreadyRunning && mutex != 0) { Kernel32.CloseHandle(mutex); mutex = 0; }
        return mutex;
    }

    static void SignalRunningInstance()
    {
        nint other = User32.FindWindowExW(0, 0, App.HostClass, null);
        if (other != 0)
        {
            User32.AllowSetForegroundWindow(C.ASFW_ANY);
            User32.PostMessageW(other, App.WM_APP_OPEN_EDITOR, 0, 0);
        }
    }
}
