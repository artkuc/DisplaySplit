using System.Runtime.InteropServices;

namespace DisplaySplit.Native;

// Interop used only by admin mode (Platform/AdminMode.cs, Platform/ShellLauncher.cs).

[StructLayout(LayoutKind.Sequential)]
public unsafe struct SHELLEXECUTEINFOW
{
    public uint cbSize;
    public uint fMask;
    public nint hwnd;
    public char* lpVerb;
    public char* lpFile;
    public char* lpParameters;
    public char* lpDirectory;
    public int nShow;
    public nint hInstApp;
    public nint lpIDList;
    public char* lpClass;
    public nint hkeyClass;
    public uint dwHotKey;
    public nint hIconOrMonitor;
    public nint hProcess;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct STARTUPINFOW
{
    public uint cb;
    public char* lpReserved;
    public char* lpDesktop;
    public char* lpTitle;
    public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
    public ushort wShowWindow, cbReserved2;
    public nint lpReserved2;
    public nint hStdInput, hStdOutput, hStdError;
}

[StructLayout(LayoutKind.Sequential)]
public struct PROCESS_INFORMATION
{
    public nint hProcess, hThread;
    public uint dwProcessId, dwThreadId;
}

public static unsafe partial class Advapi32
{
    [LibraryImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DuplicateTokenEx(nint existing, uint access, nint attributes, int impersonationLevel, int tokenType, out nint newToken);

    [LibraryImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreateProcessWithTokenW(nint token, uint logonFlags, char* applicationName, char* commandLine,
        uint creationFlags, nint environment, char* currentDirectory, STARTUPINFOW* startupInfo, PROCESS_INFORMATION* processInfo);

    [LibraryImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ConvertSidToStringSidW(void* sid, out nint stringSid);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out nint sd, nint size);

    [LibraryImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetSecurityDescriptorDacl(nint sd, out int present, out nint dacl, out int defaulted);

    [LibraryImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetSecurityDescriptorOwner(nint sd, out nint owner, out int defaulted);

    [LibraryImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetThreadToken(nint threadHandlePtr, nint token); // threadHandlePtr 0 = current thread

    [LibraryImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RevertToSelf();

    [LibraryImport("advapi32.dll")] public static partial byte* GetSidSubAuthorityCount(void* sid);
    [LibraryImport("advapi32.dll")] public static partial uint* GetSidSubAuthority(void* sid, uint index);

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint SetNamedSecurityInfoW(string name, int objectType, uint securityInfo, nint owner, nint group, nint dacl, nint sacl);
}

public static unsafe partial class Kernel32
{
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetDefaultDllDirectories(uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetProcessMitigationPolicy(int policy, void* buffer, nuint length);

    [LibraryImport("kernel32.dll")] public static partial nint LocalFree(nint mem);
    [LibraryImport("kernel32.dll", SetLastError = true)] public static partial uint WaitForSingleObject(nint handle, uint ms);

    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetExitCodeProcess(nint process, out uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint OpenMutexW(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateEventW(nint attributes, [MarshalAs(UnmanagedType.Bool)] bool manualReset,
        [MarshalAs(UnmanagedType.Bool)] bool initialState, string name);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint OpenEventW(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);
}

[StructLayout(LayoutKind.Sequential)]
public struct NOTIFYICONIDENTIFIER
{
    public uint cbSize;
    public nint hWnd;
    public uint uID;
    public Guid guidItem;
}

public static unsafe partial class Shell32
{
    [LibraryImport("shell32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShellExecuteExW(ref SHELLEXECUTEINFOW info);

    [LibraryImport("shell32.dll")] public static partial int Shell_NotifyIconGetRect(in NOTIFYICONIDENTIFIER id, out RECT rect);
}

public static class SecC
{
    public const uint TOKEN_ASSIGN_PRIMARY = 0x0001, TOKEN_DUPLICATE = 0x0002, TOKEN_IMPERSONATE = 0x0004, TOKEN_QUERY = 0x0008,
        TOKEN_ADJUST_DEFAULT = 0x0080, TOKEN_ADJUST_SESSIONID = 0x0100;
    public const int TokenUser = 1, TokenElevationType = 18, TokenIntegrityLevel = 25;
    public const int MediumPlusRid = 0x2100;
    public const int TokenElevationTypeDefault = 1, TokenElevationTypeFull = 2, TokenElevationTypeLimited = 3;
    public const int SecurityImpersonation = 2, TokenPrimary = 1, TokenImpersonation = 2;

    public const int SE_FILE_OBJECT = 1;
    public const uint OWNER_SECURITY_INFORMATION = 0x1, DACL_SECURITY_INFORMATION = 0x4, PROTECTED_DACL_SECURITY_INFORMATION = 0x80000000;

    public const uint LOAD_LIBRARY_SEARCH_SYSTEM32 = 0x800;
    public const int ProcessImageLoadPolicy = 10, ProcessRedirectionTrustPolicy = 16;

    public const uint SEE_MASK_NOCLOSEPROCESS = 0x40, SEE_MASK_NOASYNC = 0x100, SEE_MASK_FLAG_NO_UI = 0x400;
    public const int ERROR_CANCELLED = 1223, ERROR_FILE_NOT_FOUND = 2, ERROR_ACCESS_DENIED = 5;
    public const uint SYNCHRONIZE = 0x00100000, WAIT_OBJECT_0 = 0, INFINITE = 0xFFFFFFFF;
}
