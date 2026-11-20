using DisplaySplit.Core;
using DisplaySplit.Native;

namespace DisplaySplit.Platform;

/// <summary>Resolves a stable per-app key for a window (AUMID → package AUMID → normalised exe path) + window class.</summary>
public static unsafe class AppIdentity
{
    static readonly Guid IID_IPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    static readonly PROPERTYKEY PKEY_AppUserModel_ID = new() { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
    const ushort VT_LPWSTR = 31;

    public sealed record Result(string Key, string DisplayName, string? ExePath);

    /// <summary>Returns null when the identity can't be resolved (yet) – e.g. a UWP frame without its CoreWindow.</summary>
    public static Result? Resolve(nint hwnd)
    {
        string cls = WindowInfo.ClassName(hwnd);
        uint pid = WindowInfo.ProcessIdOf(hwnd);
        string? exe = WindowInfo.ProcessImagePath(pid);
        bool isFrameHost = exe != null && exe.EndsWith("\\ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase);

        string? aumid = WindowAumid(hwnd);
        if (aumid is null && isFrameHost)
        {
            nint core = User32.FindWindowExW(hwnd, 0, "Windows.UI.Core.CoreWindow", null);
            if (core == 0) return null; // not attached yet → unresolved
            uint corePid = WindowInfo.ProcessIdOf(core);
            aumid = ProcessAumid(corePid);
            exe = WindowInfo.ProcessImagePath(corePid) ?? exe;
            if (aumid is null && exe is null) return null;
        }
        aumid ??= ProcessAumid(pid);

        string identity;
        if (!string.IsNullOrEmpty(aumid)) identity = "aumid:" + aumid.ToLowerInvariant();
        else if (!string.IsNullOrEmpty(exe) && !isFrameHost) identity = "exe:" + AppIdentityRules.NormalizeExePath(exe);
        else return null;

        string display = DisplayNameFor(aumid, exe);
        return new Result(AppIdentityRules.MakeKey(identity, cls), display, exe);
    }

    static string DisplayNameFor(string? aumid, string? exe)
    {
        if (!string.IsNullOrEmpty(exe)) return Path.GetFileNameWithoutExtension(exe);
        if (!string.IsNullOrEmpty(aumid))
        {
            int bang = aumid.IndexOf('!');
            string s = bang > 0 ? aumid[..bang] : aumid;
            int us = s.IndexOf('_');
            return us > 0 ? s[..us] : s;
        }
        return "app";
    }

    static string? WindowAumid(nint hwnd)
    {
        nint store = 0;
        try
        {
            if (Shell32.SHGetPropertyStoreForWindow(hwnd, IID_IPropertyStore, out store) != 0 || store == 0) return null;
            var vtbl = *(nint**)store;
            var getValue = (delegate* unmanaged[Stdcall]<nint, PROPERTYKEY*, PROPVARIANT*, int>)vtbl[5];
            var key = PKEY_AppUserModel_ID;
            var pv = new PROPVARIANT();
            if (getValue(store, &key, &pv) != 0) return null;
            string? s = pv.vt == VT_LPWSTR && pv.p != 0 ? new string((char*)pv.p) : null;
            Ole32.PropVariantClear(ref pv);
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
        catch { return null; }
        finally
        {
            if (store != 0)
            {
                var vtbl = *(nint**)store;
                ((delegate* unmanaged[Stdcall]<nint, uint>)vtbl[2])(store);
            }
        }
    }

    static string? ProcessAumid(uint pid)
    {
        nint proc = Kernel32.OpenProcess(C.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (proc == 0) return null;
        try
        {
            uint len = 512;
            char* buf = stackalloc char[512];
            return Kernel32.GetApplicationUserModelId(proc, ref len, buf) == 0 && len > 1 ? new string(buf, 0, (int)len - 1) : null;
        }
        finally { Kernel32.CloseHandle(proc); }
    }
}
