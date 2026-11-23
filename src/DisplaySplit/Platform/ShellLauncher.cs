using System.Runtime.InteropServices;
using DisplaySplit.Native;

namespace DisplaySplit.Platform;

/// <summary>
/// Starts programs, folders and URLs without administrator rights, also from the elevated admin-mode instance, so a
/// browser or Explorer window opened from the tray never inherits our elevation.
/// </summary>
public static unsafe class ShellLauncher
{
    static string Explorer => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    /// <summary>Open a URL in the default browser (never elevated).</summary>
    public static bool OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) return false;
        if (!WindowInfo.SelfElevated)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); return true; }
            catch (Exception ex) { Log.Warn($"open url: {ex.Message}"); return false; }
        }
        return RunUnelevated(Explorer, $"\"{u.AbsoluteUri}\"", null);
    }

    /// <summary>Open a folder in Explorer (never elevated).</summary>
    public static bool OpenFolder(string dir)
    {
        if (dir.Contains('"')) return false;
        if (!WindowInfo.SelfElevated)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Explorer) { UseShellExecute = false, ArgumentList = { dir } });
                return true;
            }
            catch (Exception ex) { Log.Warn($"open folder: {ex.Message}"); return false; }
        }
        return RunUnelevated(Explorer, $"\"{dir}\"", null);
    }

    /// <summary>
    /// From an elevated process: start <paramref name="exe"/> with the token of the desktop shell (Explorer), i.e. as the
    /// same user without elevation. Fails closed: if the shell token is missing, elevated or belongs to someone else,
    /// nothing is started.
    /// </summary>
    public static bool RunUnelevated(string exe, string args, string? workingDir)
    {
        if (!Path.IsPathFullyQualified(exe) || exe.Contains('"')) return false;
        const uint access = SecC.TOKEN_QUERY | SecC.TOKEN_DUPLICATE | SecC.TOKEN_ASSIGN_PRIMARY | SecC.TOKEN_ADJUST_DEFAULT | SecC.TOKEN_ADJUST_SESSIONID;
        nint primary = DuplicateShellToken(access, SecC.TokenPrimary);
        if (primary == 0) return false;
        try
        {
            string cmd = $"\"{exe}\" {args}";
            string dir = workingDir ?? Path.GetDirectoryName(exe)!;
            var si = new STARTUPINFOW { cb = (uint)sizeof(STARTUPINFOW) };
            PROCESS_INFORMATION pi;
            char[] cmdBuf = (cmd + "\0").ToCharArray(); // CreateProcess* may write to the command line buffer
            fixed (char* pExe = exe)
            fixed (char* pCmd = cmdBuf)
            fixed (char* pDir = dir)
            {
                // Environment = null: built from the user's profile. Needs SeImpersonatePrivilege (elevated admins have it).
                if (!Advapi32.CreateProcessWithTokenW(primary, 0, pExe, pCmd, 0, 0, pDir, &si, &pi))
                {
                    Log.Warn($"unelevated launch: CreateProcessWithTokenW {Marshal.GetLastPInvokeError()}");
                    return false;
                }
            }
            Kernel32.CloseHandle(pi.hThread);
            Kernel32.CloseHandle(pi.hProcess);
            return true;
        }
        finally { Kernel32.CloseHandle(primary); }
    }

    /// <summary>
    /// From an elevated process: run <paramref name="action"/> on this thread while impersonating the non-elevated shell
    /// user, so file reads get exactly the access the user has anyway (no privileged reads through links). Returns false
    /// without running it if that isn't possible.
    /// </summary>
    public static bool RunAsShellUser(Action action)
    {
        nint imp = DuplicateShellToken(SecC.TOKEN_QUERY | SecC.TOKEN_IMPERSONATE, SecC.TokenImpersonation);
        if (imp == 0) return false;
        try
        {
            if (!Advapi32.SetThreadToken(0, imp)) { Log.Warn($"impersonate: {Marshal.GetLastPInvokeError()}"); return false; }
            try { action(); }
            finally
            {
                // If we can't revert, this thread must not continue with a foreign token.
                if (!Advapi32.RevertToSelf()) Environment.FailFast("RevertToSelf failed");
            }
            return true;
        }
        finally { Kernel32.CloseHandle(imp); }
    }

    /// <summary>
    /// Duplicate the desktop shell's token, after checking it belongs to us and is not elevated (fail closed).
    /// If Explorer isn't running, any other window registered as shell can only belong to a non-elevated process of ours.
    /// </summary>
    static nint DuplicateShellToken(uint access, int tokenType)
    {
        nint shell = User32.GetShellWindow();
        if (shell == 0) { Log.Warn("shell token: no shell window"); return 0; }
        User32.GetWindowThreadProcessId(shell, out uint pid);
        nint proc = Kernel32.OpenProcess(C.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (proc == 0) { Log.Warn($"shell token: OpenProcess {Marshal.GetLastPInvokeError()}"); return 0; }
        nint token = 0;
        try
        {
            if (!Advapi32.OpenProcessToken(proc, SecC.TOKEN_DUPLICATE | SecC.TOKEN_QUERY, out token))
            {
                Log.Warn($"shell token: OpenProcessToken {Marshal.GetLastPInvokeError()}");
                return 0;
            }
            if (TokenInfo.IsElevated(token) || TokenInfo.IntegrityRid(token) > SecC.MediumPlusRid ||
                TokenInfo.UserSid(token) is not { } sid || sid != AdminMode.UserSid)
            {
                Log.Warn("shell token: elevated, high integrity or another user's");
                return 0;
            }
            if (!Advapi32.DuplicateTokenEx(token, access, 0, SecC.SecurityImpersonation, tokenType, out nint dup))
            {
                Log.Warn($"shell token: DuplicateTokenEx {Marshal.GetLastPInvokeError()}");
                return 0;
            }
            return dup;
        }
        finally
        {
            if (token != 0) Kernel32.CloseHandle(token);
            Kernel32.CloseHandle(proc);
        }
    }
}

/// <summary>Token queries shared by admin mode.</summary>
public static unsafe class TokenInfo
{
    public static bool IsElevated(nint token)
    {
        uint elevated = 0;
        // Fail closed: if we can't tell, assume elevated.
        return !Advapi32.GetTokenInformation(token, C.TokenElevation, &elevated, 4, out _) || elevated != 0;
    }

    public static int ElevationType(nint token)
    {
        int type = 0;
        return Advapi32.GetTokenInformation(token, SecC.TokenElevationType, &type, 4, out _) ? type : 0;
    }

    /// <summary>Mandatory integrity level RID (0x2000 medium, 0x3000 high); int.MaxValue if unknown (fail closed).</summary>
    public static int IntegrityRid(nint token)
    {
        byte* buf = stackalloc byte[128];
        if (!Advapi32.GetTokenInformation(token, SecC.TokenIntegrityLevel, buf, 128, out _)) return int.MaxValue;
        void* sid = *(void**)buf; // TOKEN_MANDATORY_LABEL { SID_AND_ATTRIBUTES Label }
        byte count = *Advapi32.GetSidSubAuthorityCount(sid);
        return count == 0 ? int.MaxValue : (int)*Advapi32.GetSidSubAuthority(sid, (uint)(count - 1));
    }

    public static string? UserSid(nint token)
    {
        byte* buf = stackalloc byte[256];
        if (!Advapi32.GetTokenInformation(token, SecC.TokenUser, buf, 256, out _)) return null;
        void* sid = *(void**)buf; // TOKEN_USER { SID_AND_ATTRIBUTES { PSID Sid; DWORD Attributes } }
        if (!Advapi32.ConvertSidToStringSidW(sid, out nint str)) return null;
        try { return Marshal.PtrToStringUni(str); }
        finally { Kernel32.LocalFree(str); }
    }

    /// <summary>Run <paramref name="query"/> against this process's token.</summary>
    public static T OfSelf<T>(Func<nint, T> query, T fallback)
    {
        if (!Advapi32.OpenProcessToken(Kernel32.GetCurrentProcess(), C.TOKEN_QUERY, out nint token)) return fallback;
        try { return query(token); }
        finally { Kernel32.CloseHandle(token); }
    }
}
