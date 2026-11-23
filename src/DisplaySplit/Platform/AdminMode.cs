using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using DisplaySplit.Core;
using DisplaySplit.Native;

namespace DisplaySplit.Platform;

/// <summary>
/// Optional "Manage admin windows" mode: DisplaySplit runs elevated so it can also arrange windows of apps that run as
/// administrator (UIPI blocks a normal process from moving them).
///
/// Security model — nothing the non-elevated user (or malware running as them) can modify ever runs, loads or is written
/// by the elevated process without a UAC prompt:
/// <list type="bullet">
/// <item>The elevated exe is a copy in %ProgramFiles%\DisplaySplit (explicit ACL: only SYSTEM/Administrators can write).
///   It is started by an on-demand scheduled task (RunLevel HighestAvailable) whose own ACL lets the user run it but not
///   change it. Only the installer, which needs one UAC prompt, creates or updates the copy and the task.</item>
/// <item>Only the self-contained NativeAOT build is allowed. A framework-dependent build loads the .NET runtime from
///   DOTNET_ROOT and startup hooks from DOTNET_STARTUP_HOOKS, both user-controlled environment variables.</item>
/// <item>The elevated process keeps its data and log in %ProgramFiles%\DisplaySplit\data\&lt;SID&gt; (user: read-only), so it
///   never writes into user-writable folders (no symlink/junction/hard-link tricks). DISPLAYSPLIT_HOME is ignored.</item>
/// <item>DLLs load from System32 only; remote and low-integrity images are blocked; junctions created by non-admins are
///   not followed (RedirectionGuard, where Windows supports it).</item>
/// <item>Child processes (browser, Explorer, the non-elevated restart) are started with the shell's non-elevated token.</item>
/// <item>The task takes no arguments from anyone; its command line is fixed. The only signal a non-elevated process can
///   give it is "this is a logon start" (a named event), which merely changes how windows are arranged.</item>
/// </list>
/// </summary>
public static unsafe class AdminMode
{
    public const string ArgAdminMode = "--admin-mode";
    public const string ArgInstall = "--install-admin-mode";
    public const string ArgSetAutostart = "--set-autostart";

    // Installer exit codes
    public const int InstallOk = 0, InstallNotSupported = 2, InstallNotElevated = 3, InstallUnsafeLocation = 4,
        InstallCopyFailed = 5, InstallTaskFailed = 6, InstallSecurityFailed = 7, InstallFailed = 8;
    public const int InstallCancelled = -SecC.ERROR_CANCELLED;

    /// <summary>True in a NativeAOT build (no JIT, no runtime loaded from the environment).</summary>
    /// <remarks>
    /// <see cref="RuntimeFeature.IsDynamicCodeSupported"/> alone is a feature switch a runtimeconfig could flip, so also
    /// require that no CoreCLR host is loaded (a framework-dependent build always has coreclr.dll and hostfxr.dll).
    /// </remarks>
    public static bool IsNativeAot { get; } = !RuntimeFeature.IsDynamicCodeSupported &&
        Kernel32.GetModuleHandleW("coreclr.dll") == 0 && Kernel32.GetModuleHandleW("hostfxr.dll") == 0 &&
        Kernel32.GetModuleHandleW("hostpolicy.dll") == 0;

    /// <summary>The user is an administrator whose apps normally run non-elevated (UAC split token).</summary>
    public static bool AccountIsAdmin { get; } =
        TokenInfo.OfSelf(TokenInfo.ElevationType, 0) is SecC.TokenElevationTypeLimited or SecC.TokenElevationTypeFull;

    public static string? UserSid { get; } = TokenInfo.OfSelf(TokenInfo.UserSid, null);

    /// <summary>Elevated with a linked non-elevated token (UAC on): the case where elevation is a boundary.</summary>
    public static bool IsSplitTokenElevated { get; } = TokenInfo.OfSelf(TokenInfo.ElevationType, 0) == SecC.TokenElevationTypeFull;

    /// <summary>
    /// Elevated instance: change HKCU autostart through a short-lived non-elevated copy of ourselves, so the elevated
    /// process never writes into the user's registry hive (registry links there could redirect the write).
    /// </summary>
    public static bool SetAutostartUnelevated(bool on) =>
        ShellLauncher.RunUnelevated(InstalledExe, $"{ArgSetAutostart} {(on ? "on" : "off")}", null);

    static readonly string ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    public static bool Supported => IsNativeAot && AccountIsAdmin && AdminModeSpec.IsValidSid(UserSid) &&
                                    Path.IsPathFullyQualified(ProgramFiles);

    public static string InstallDir => Path.Combine(ProgramFiles, "DisplaySplit");
    public static string InstalledExe => Path.Combine(InstallDir, AdminModeSpec.ExeName);
    static string DataRoot => Path.Combine(InstallDir, "data");
    public static string DataDir => Path.Combine(DataRoot, UserSid!);
    static string TaskName => AdminModeSpec.TaskName(UserSid!);

    /// <summary>This process is the elevated admin-mode instance.</summary>
    public static bool IsActive { get; private set; }

    // ------------------------------------------------------------------ startup

    /// <summary>
    /// Called first thing in Main. Returns true if this is the elevated instance started by the task: hardens the process
    /// and moves storage to the protected data dir before anything is logged.
    /// </summary>
    public static bool TryEnterAdminMode(string[] args)
    {
        if (!args.Contains(ArgAdminMode, StringComparer.OrdinalIgnoreCase)) return false;
        if (!Supported || !WindowInfo.SelfElevated || !AdminModeSpec.PathsEqual(Environment.ProcessPath, InstalledExe)) return false;
        // Order matters: redirect storage (and so the log) before anything can be logged.
        Storage.UseDirectory(DataDir);
        HardenProcess();
        string? problem = null;
        try
        {
            bool ok = Directory.Exists(DataDir) ? IsSafeDirectory(DataDir) : EnsureSecureDirectory(DataDir, AdminModeSpec.DataDirSddl(UserSid!));
            if (!ok) problem = "it is a link or a file";
        }
        catch (Exception ex) { problem = ex.Message; }
        if (problem != null)
        {
            // Never fall back to the user's folder.
            User32.MessageBoxW(0, $"DisplaySplit can't use its protected data folder:\n{DataDir}\n\n{problem}", "DisplaySplit", 0x10);
            Environment.Exit(1);
        }
        IsActive = true;
        return true;
    }

    /// <summary>Elevated instance: was this a logon start? (Set by the non-elevated instance that started the task.)</summary>
    public static bool ConsumeAutostartSignal()
    {
        nint ev = Kernel32.OpenEventW(SecC.SYNCHRONIZE, false, AdminModeSpec.AutostartEventName(UserSid!));
        if (ev == 0) return false;
        Kernel32.CloseHandle(ev);
        return true;
    }

    static void HardenProcess()
    {
        if (!Kernel32.SetDefaultDllDirectories(SecC.LOAD_LIBRARY_SEARCH_SYSTEM32))
            Log.Warn($"SetDefaultDllDirectories: {Marshal.GetLastPInvokeError()}");
        uint imageLoad = 0x1 /*NoRemoteImages*/ | 0x2 /*NoLowMandatoryLabelImages*/ | 0x4 /*PreferSystem32Images*/;
        if (!Kernel32.SetProcessMitigationPolicy(SecC.ProcessImageLoadPolicy, &imageLoad, 4))
            Log.Warn($"image load policy: {Marshal.GetLastPInvokeError()}");
        uint redirectionTrust = 0x1; // EnforceRedirectionTrust (Windows 11 22H2+; older builds just refuse)
        if (!Kernel32.SetProcessMitigationPolicy(SecC.ProcessRedirectionTrustPolicy, &redirectionTrust, 4))
            Log.Info($"redirection trust policy unavailable: {Marshal.GetLastPInvokeError()}");
    }

    /// <summary>Non-elevated start: is admin mode set up, so this instance should hand over to the elevated one?</summary>
    public static bool ShouldRedirect() => Supported && !IsActive && File.Exists(InstalledExe) && TaskExists();

    /// <summary>
    /// Start the elevated instance through the task instead of running here. Releases <paramref name="mutex"/> first.
    /// Returns true when this process should exit; false (mutex re-acquired) to fall back to running non-elevated.
    /// </summary>
    public static bool Redirect(ref nint mutex, bool autostart, Func<nint> reacquireMutex)
    {
        string? self = Environment.ProcessPath;
        if (!autostart && !AdminModeSpec.PathsEqual(self, InstalledExe) && self != null && !SameFile(self, InstalledExe))
        {
            // Deliberately no "install this copy?" button here: any process can start some DisplaySplit.exe, and a
            // prompt that leads to UAC would help it phish a permanent elevation. Updating goes through the tray:
            // turn admin mode off, start the new copy, turn it on again (an explicit, user-initiated action).
            int r = User32.MessageBoxW(0,
                $"DisplaySplit runs with administrator rights from its installed copy ({VersionOf(InstalledExe)}, {InstallDir}).\n\n" +
                $"The copy you started is different and won't be used:\n{self}\n\n" +
                "To update the installed copy: untick \"Manage admin windows\" in the tray menu, exit, start the new copy and tick it again.\n\n" +
                "OK – start the installed copy.",
                "DisplaySplit", 0x1 /*OKCANCEL*/ | 0x40 /*INFORMATION*/ | 0x10000 /*SETFOREGROUND*/);
            if (r != 1 /*IDOK*/) { Release(ref mutex); return true; }
        }

        Release(ref mutex);
        nint ev = autostart ? Kernel32.CreateEventW(0, true, false, AdminModeSpec.AutostartEventName(UserSid!)) : 0;
        try
        {
            if (RunTask() && WaitForInstance(15_000)) return true;
        }
        finally { if (ev != 0) Kernel32.CloseHandle(ev); }

        Log.Warn("admin mode: the elevated instance did not start; running without administrator rights");
        mutex = reacquireMutex();
        return mutex == 0; // someone else got it meanwhile → exit
    }

    static void Release(ref nint mutex)
    {
        if (mutex != 0) { Kernel32.CloseHandle(mutex); mutex = 0; }
    }

    /// <summary>Wait until the single-instance mutex exists (owned by the elevated instance).</summary>
    static bool WaitForInstance(int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            nint m = Kernel32.OpenMutexW(SecC.SYNCHRONIZE, false, Program.MutexName);
            int err = Marshal.GetLastPInvokeError();
            if (m != 0) { Kernel32.CloseHandle(m); return true; }
            if (err == SecC.ERROR_ACCESS_DENIED) return true; // exists, created by the elevated instance
            Thread.Sleep(100);
        }
        return false;
    }

    // ------------------------------------------------------------------ handoff (admin mode turned off)

    /// <summary>
    /// Non-elevated start with admin mode off: if it was turned off since we last ran, take over the data the elevated
    /// instance kept (the protected folder is readable by the user). Runs before any store is loaded.
    /// </summary>
    public static void ImportHandoffIfPending()
    {
        if (!Supported || IsActive) return;
        try
        {
            string marker = Path.Combine(DataDir, AdminModeSpec.HandoffFile);
            if (!File.Exists(marker)) return;
            string? id = ReadSmall(marker);
            string mine = Storage.PathOf(AdminModeSpec.HandoffImportedFile);
            if (!AdminModeSpec.ShouldImportHandoff(id, File.Exists(mine) ? ReadSmall(mine) : null)) return;
            int n = CopyData(DataDir, Storage.Home);
            File.WriteAllText(mine, id);
            Log.Info($"admin mode: imported {n} file(s) from {DataDir}");
        }
        catch (Exception ex) { Log.Warn($"admin mode handoff import: {ex.Message}"); }
    }

    static int CopyData(string fromDir, string toDir)
    {
        int n = 0;
        if (Storage.TryLoadFrom(fromDir, "settings.json", JsonCtx.Default.Settings, out var s) && Storage.SaveTo(toDir, "settings.json", s!, JsonCtx.Default.Settings)) n++;
        if (Storage.TryLoadFrom(fromDir, "layouts.json", JsonCtx.Default.LayoutsFile, out var l) && Storage.SaveTo(toDir, "layouts.json", l!, JsonCtx.Default.LayoutsFile)) n++;
        if (Storage.TryLoadFrom(fromDir, "placements.json", JsonCtx.Default.PlacementsFile, out var p) && Storage.SaveTo(toDir, "placements.json", p!, JsonCtx.Default.PlacementsFile)) n++;
        return n;
    }

    static string? ReadSmall(string path)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists || fi.Length > 256 || (fi.Attributes & FileAttributes.ReparsePoint) != 0) return null;
        return File.ReadAllText(path).Trim();
    }

    // ------------------------------------------------------------------ turning it on / off (tray)

    /// <summary>Start the installer with a UAC prompt. Returns InstallCancelled if the user declined.</summary>
    public static int RunInstallerElevated(nint owner, out nint process)
    {
        process = 0;
        string? exe = Environment.ProcessPath;
        if (exe == null) return InstallFailed;
        var info = new SHELLEXECUTEINFOW
        {
            cbSize = (uint)sizeof(SHELLEXECUTEINFOW),
            fMask = SecC.SEE_MASK_NOCLOSEPROCESS | SecC.SEE_MASK_NOASYNC,
            hwnd = owner,
            nShow = 0, // SW_HIDE
        };
        fixed (char* verb = "runas")
        fixed (char* file = exe)
        fixed (char* prm = ArgInstall)
        {
            info.lpVerb = verb;
            info.lpFile = file;
            info.lpParameters = prm;
            if (!Shell32.ShellExecuteExW(ref info))
            {
                int err = Marshal.GetLastPInvokeError();
                if (err == SecC.ERROR_CANCELLED) return InstallCancelled;
                Log.Warn($"admin mode: ShellExecuteEx runas failed: {err}");
                return InstallFailed;
            }
        }
        process = info.hProcess;
        return process != 0 ? InstallOk : InstallFailed;
    }

    public static int WaitInstaller(nint process)
    {
        try
        {
            Kernel32.WaitForSingleObject(process, SecC.INFINITE);
            return Kernel32.GetExitCodeProcess(process, out uint code) ? (int)code : InstallFailed;
        }
        finally { Kernel32.CloseHandle(process); }
    }

    public static void ShowInstallError(nint owner, int code)
    {
        string why = code switch
        {
            InstallNotSupported => "Admin mode needs the standalone (NativeAOT) build and an administrator account.",
            InstallUnsafeLocation => $"{InstallDir} exists but isn't a normal folder (it is a link or a file). Remove it and try again.",
            InstallCopyFailed => $"Couldn't copy DisplaySplit to {InstallDir}.",
            InstallTaskFailed => "Couldn't create the scheduled task.",
            InstallSecurityFailed => $"Couldn't secure the folder {InstallDir}.",
            _ => "Setup failed.",
        };
        User32.MessageBoxW(owner, why + $"\n\nDetails are in {Path.Combine(DataDir, "log.txt")} (code {code}).", "DisplaySplit", 0x10 /*ICONERROR*/);
    }

    /// <summary>Non-elevated, after a successful install: start the elevated instance (call after the mutex is released).</summary>
    public static void StartInstalled()
    {
        if (RunTask() && WaitForInstance(15_000)) return;
        User32.MessageBoxW(0, "Admin mode is set up, but DisplaySplit couldn't be started with administrator rights.\n\nStart DisplaySplit again; if this keeps happening, turn admin mode off and on in the tray menu.",
            "DisplaySplit", 0x30);
    }

    /// <summary>Elevated instance: turn admin mode off. The data handoff and restart happen after shutdown.</summary>
    public static bool Disable()
    {
        if (!IsActive) return false;
        if (Schtasks("/delete", "/tn", TaskName, "/f") != 0 && TaskExists()) return false;
        Log.Info("admin mode: task deleted");
        return true;
    }

    /// <summary>After the elevated instance has flushed its data and released the mutex.</summary>
    public static void AfterDisable()
    {
        try { File.WriteAllText(Path.Combine(DataDir, AdminModeSpec.HandoffFile), Guid.NewGuid().ToString("N")); }
        catch (Exception ex) { Log.Warn($"admin mode handoff marker: {ex.Message}"); }
        if (!ShellLauncher.RunUnelevated(InstalledExe, "", null))
            User32.MessageBoxW(0, "Admin mode is off. Start DisplaySplit again to continue without administrator rights.", "DisplaySplit", 0x40);
    }

    // ------------------------------------------------------------------ installer (elevated, after UAC consent)

    /// <summary>
    /// <c>DisplaySplit.exe --install-admin-mode</c>, started via UAC by the tray. Copies this exe to Program Files,
    /// secures the folders, (re)creates the task and migrates the user's data. Takes no other input.
    /// </summary>
    public static int RunInstaller()
    {
        // First thing: pin the exe we were started from. Without FileShare.Delete/Write nobody can rename or replace it
        // while we hold this handle (a running exe can otherwise be renamed and swapped after the UAC consent).
        FileStream? source = null;
        try { source = new FileStream(Environment.ProcessPath!, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch { /* reported below */ }
        using var _ = source;

        if (!Supported) return InstallNotSupported;
        if (!WindowInfo.SelfElevated) return InstallNotElevated;
        // Before anything is logged: the log must not go to the user's folder.
        Storage.UseDirectory(DataDir);
        HardenProcess();
        try
        {
            if (source == null || !MatchesRunningImage(source))
            {
                Log.Error($"admin mode install: {Environment.ProcessPath} is not the image that is running");
                return InstallCopyFailed;
            }
            bool taskExisted = TaskExists();
            foreach (var dir in new[] { InstallDir, DataRoot })
                if (!EnsureSecureDirectory(dir, AdminModeSpec.InstallDirSddl)) return InstallUnsafeLocation;
            if (!EnsureSecureDirectory(DataDir, AdminModeSpec.DataDirSddl(UserSid!))) return InstallUnsafeLocation;
            Log.Info($"admin mode install: {Environment.ProcessPath} → {InstalledExe} (task existed: {taskExisted})");

            if (!CopySelf(source)) return InstallCopyFailed;
            if (!RegisterTask()) return InstallTaskFailed;
            MigrateUserData(taskExisted);
            Log.Info("admin mode install: done");
            return InstallOk;
        }
        catch (UnauthorizedAccessException ex) { Log.Error("admin mode install", ex); return InstallSecurityFailed; }
        catch (AclException ex) { Log.Error("admin mode install", ex); return InstallSecurityFailed; }
        catch (Exception ex) { Log.Error("admin mode install", ex); return InstallFailed; }
    }

    /// <summary>
    /// Sanity check that the pinned file is the image this process was loaded from: compare PE header fields of the
    /// mapped image with the file (link timestamp, checksum, image size, entry point, section count).
    /// </summary>
    static bool MatchesRunningImage(FileStream file)
    {
        byte* mem = (byte*)Kernel32.GetModuleHandleW(null);
        if (mem == null) return false;
        var head = new byte[4096];
        file.Position = 0;
        int read = file.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        file.Position = 0;
        int lfanew = *(int*)(mem + 0x3C);
        if (lfanew <= 0 || lfanew + 0x60 > read || BitConverter.ToInt32(head, 0x3C) != lfanew) return false;
        // NT headers: Signature(4) FileHeader(20: Machine, NumberOfSections, TimeDateStamp, …) OptionalHeader(…)
        int nt = lfanew, opt = lfanew + 24;
        bool Same(int offset, int size) => new ReadOnlySpan<byte>(mem + offset, size).SequenceEqual(head.AsSpan(offset, size));
        return Same(nt, 4)            // "PE\0\0"
            && Same(nt + 6, 2)        // NumberOfSections
            && Same(nt + 8, 4)        // TimeDateStamp
            && Same(opt + 16, 4)      // AddressOfEntryPoint
            && Same(opt + 56, 4)      // SizeOfImage
            && Same(opt + 64, 4);     // CheckSum
    }

    sealed class AclException(string message) : Exception(message);

    static bool IsSafeDirectory(string dir)
    {
        var a = File.GetAttributes(dir); // throws if missing
        return (a & FileAttributes.Directory) != 0 && (a & FileAttributes.ReparsePoint) == 0;
    }

    /// <summary>Create (or re-secure) a directory with an explicit, protected ACL. Refuses links and files in its place.</summary>
    static bool EnsureSecureDirectory(string dir, string sddl)
    {
        if (Directory.Exists(dir) || File.Exists(dir))
        {
            if (!IsSafeDirectory(dir)) { Log.Error($"admin mode: {dir} is a link or a file"); return false; }
        }
        else Directory.CreateDirectory(dir);
        if (!IsSafeDirectory(dir)) return false;
        ApplySddl(dir, sddl);
        return true;
    }

    static void ApplySddl(string path, string sddl)
    {
        if (!Advapi32.ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out nint sd, 0))
            throw new AclException($"bad SDDL ({Marshal.GetLastPInvokeError()})");
        try
        {
            if (!Advapi32.GetSecurityDescriptorDacl(sd, out int present, out nint dacl, out _) || present == 0 ||
                !Advapi32.GetSecurityDescriptorOwner(sd, out nint owner, out _) || owner == 0)
                throw new AclException("SDDL without owner/DACL");
            uint err = Advapi32.SetNamedSecurityInfoW(path, SecC.SE_FILE_OBJECT,
                SecC.OWNER_SECURITY_INFORMATION | SecC.DACL_SECURITY_INFORMATION | SecC.PROTECTED_DACL_SECURITY_INFORMATION,
                owner, 0, dacl, 0);
            if (err != 0) throw new AclException($"SetNamedSecurityInfo {path}: {err}");
        }
        finally { Kernel32.LocalFree(sd); }
    }

    static bool CopySelf(FileStream source)
    {
        if (AdminModeSpec.PathsEqual(Environment.ProcessPath, InstalledExe)) return true; // already running the installed copy
        string tmp = InstalledExe + ".new";
        try
        {
            if (File.Exists(tmp)) File.Delete(tmp);
            // Copy from the handle pinned at startup, never by re-opening the path.
            source.Position = 0;
            using (var d = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                source.CopyTo(d);
                d.Flush(flushToDisk: true);
            }
            try { File.Move(tmp, InstalledExe, overwrite: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The old copy is still running: a running exe can be renamed, not overwritten.
                string old = InstalledExe + ".old";
                try { if (File.Exists(old)) File.Delete(old); } catch { /* still in use: keep it */ }
                File.Move(InstalledExe, old, overwrite: true);
                File.Move(tmp, InstalledExe);
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("admin mode: copy", ex);
            try { File.Delete(tmp); } catch { /* ignore */ }
            return false;
        }
    }

    static bool RegisterTask()
    {
        string xml = AdminModeSpec.TaskXml(UserSid!, InstalledExe, InstallDir);
        // Written into the protected folder (not %TEMP%, which the user can modify between write and read).
        string path = Path.Combine(InstallDir, "task.xml");
        try
        {
            File.WriteAllText(path, xml, System.Text.Encoding.Unicode);
            int code = Schtasks("/create", "/tn", TaskName, "/xml", path, "/f");
            return code == 0;
        }
        finally { try { File.Delete(path); } catch { /* ignore */ } }
    }

    static void MigrateUserData(bool taskExisted)
    {
        string handoff = Path.Combine(DataDir, AdminModeSpec.HandoffFile);
        string? handoffId = File.Exists(handoff) ? ReadSmall(handoff) : null;
        bool hasData = AdminModeSpec.DataFiles.Any(f => File.Exists(Path.Combine(DataDir, f)));

        // Everything from the user's folder is read while impersonating the user's own non-elevated token, so a
        // junction, link or DISPLAYSPLIT_HOME can't make us read anything the user couldn't read anyway.
        string userHome = "";
        string? userImportedId = null;
        Settings? settings = null;
        LayoutsFile? layouts = null;
        PlacementsFile? placements = null;
        bool readOk = ShellLauncher.RunAsShellUser(() =>
        {
            userHome = Storage.UserHome();
            try { userImportedId = ReadSmall(Path.Combine(userHome, AdminModeSpec.HandoffImportedFile)); } catch { /* none */ }
            if (!Directory.Exists(userHome)) return;
            Storage.TryLoadFrom(userHome, "settings.json", JsonCtx.Default.Settings, out settings);
            Storage.TryLoadFrom(userHome, "layouts.json", JsonCtx.Default.LayoutsFile, out layouts);
            Storage.TryLoadFrom(userHome, "placements.json", JsonCtx.Default.PlacementsFile, out placements);
        });
        if (!readOk)
        {
            Log.Warn("admin mode install: couldn't read the user's data as the user; not importing");
            if (hasData) return; // keep what admin mode has
        }

        if (AdminModeSpec.InstallerShouldImportUserData(taskExisted, handoffId, userImportedId, hasData))
        {
            foreach (var f in AdminModeSpec.DataFiles)
            {
                // Start clean: data the user's copy no longer has must not come back from an older admin session.
                foreach (var suffix in new[] { "", ".bak" })
                {
                    try { File.Delete(Path.Combine(DataDir, f + suffix)); } catch { /* ignore */ }
                }
            }
            // Typed JSON in, typed JSON out (written as ourselves, into the protected folder).
            int n = 0;
            if (settings != null && Storage.SaveTo(DataDir, "settings.json", settings, JsonCtx.Default.Settings)) n++;
            if (layouts != null && Storage.SaveTo(DataDir, "layouts.json", layouts, JsonCtx.Default.LayoutsFile)) n++;
            if (placements != null && Storage.SaveTo(DataDir, "placements.json", placements, JsonCtx.Default.PlacementsFile)) n++;
            Log.Info($"admin mode install: imported {n} file(s) from {userHome}");
        }
        else Log.Info("admin mode install: keeping the existing admin-mode data");
        try { File.Delete(handoff); } catch { /* ignore */ }
    }

    // ------------------------------------------------------------------ task

    static bool TaskExists() => Schtasks("/query", "/tn", TaskName) == 0;

    static bool RunTask()
    {
        bool ok = Schtasks("/run", "/tn", TaskName) == 0;
        if (!ok) Log.Warn("admin mode: couldn't start the task");
        return ok;
    }

    /// <summary>Runs System32\schtasks.exe by full path (never resolved via the app dir, current dir or PATH).</summary>
    static int Schtasks(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Environment.SystemDirectory,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            string stderr = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(30_000)) { try { p.Kill(); } catch { /* ignore */ } return -1; }
            if (p.ExitCode != 0 && args[0] != "/query") Log.Warn($"schtasks {args[0]}: {p.ExitCode} {stderr.Trim()} {stdout.Result.Trim()}");
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            Log.Warn($"schtasks: {ex.Message}");
            return -1;
        }
    }

    // ------------------------------------------------------------------ helpers

    static bool SameFile(string a, string b)
    {
        try
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            if (!fa.Exists || !fb.Exists || fa.Length != fb.Length) return false;
            using var sa = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sb = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return SHA256.HashData(sa).AsSpan().SequenceEqual(SHA256.HashData(sb));
        }
        catch { return false; }
    }

    static string VersionOf(string path)
    {
        try { return "version " + (FileVersionInfo.GetVersionInfo(path).ProductVersion ?? "?"); }
        catch { return "unknown version"; }
    }
}
