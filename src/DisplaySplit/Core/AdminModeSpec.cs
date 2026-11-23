using System.Text;
using System.Text.RegularExpressions;

namespace DisplaySplit.Core;

/// <summary>
/// Pure, unit-tested building blocks of admin mode: names, security descriptors and the scheduled-task XML.
/// See Platform/AdminMode.cs for the security model.
/// </summary>
public static partial class AdminModeSpec
{
    public const string ExeName = "DisplaySplit.exe";
    public const string HandoffFile = "handoff.txt";          // in the protected data dir, written when admin mode is turned off
    public const string HandoffImportedFile = "admin-handoff.txt"; // in the user's own data dir: last handoff id imported
    public static readonly string[] DataFiles = ["settings.json", "layouts.json", "placements.json"];

    [GeneratedRegex(@"^S-1-\d{1,3}(-\d{1,10}){1,14}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SidRegex();

    [GeneratedRegex(@"^[0-9a-f]{32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex HandoffIdRegex();

    /// <summary>The SID ends up in a task name, an SDDL string, XML and a path: accept only the canonical S-1-… form.</summary>
    public static bool IsValidSid(string? sid) => sid is { Length: < 200 } && SidRegex().IsMatch(sid);

    public static bool IsValidHandoffId(string? id) => id != null && HandoffIdRegex().IsMatch(id);

    public static string TaskName(string sid) => IsValidSid(sid) ? "DisplaySplit-Admin-" + sid : throw new ArgumentException("bad SID");

    public static string AutostartEventName(string sid) => @"Local\DisplaySplit.AdminAutostart." + sid;

    // FA = full access; 0x1200a9 = read + execute (FILE_GENERIC_READ | FILE_GENERIC_EXECUTE).
    // OW (OWNER RIGHTS) limits the implicit WRITE_DAC of an object's owner, so even if a file ends up owned by the user
    // (e.g. "default owner = object creator" policy), the non-elevated user still can't change its ACL.

    /// <summary>Program Files\DisplaySplit: only SYSTEM and Administrators can write; local users can read/run the exe.</summary>
    public const string InstallDirSddl = "O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)(A;OICI;0x1200a9;;;OW)";

    /// <summary>Per-user data dir: only SYSTEM and Administrators can write; only that user can read.</summary>
    public static string DataDirSddl(string sid) => IsValidSid(sid)
        ? $"O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;{sid})(A;OICI;0x1200a9;;;OW)"
        : throw new ArgumentException("bad SID");

    /// <summary>The task itself: SYSTEM/Administrators full control; the user may read and run it, not change it.</summary>
    public static string TaskSddl(string sid) => IsValidSid(sid)
        ? $"D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FRFX;;;{sid})"
        : throw new ArgumentException("bad SID");

    /// <summary>
    /// On-demand task (no triggers): runs <paramref name="exePath"/> --admin-mode as <paramref name="sid"/> in their
    /// interactive session with the highest rights they have. Autostart stays the HKCU Run key, whose (non-elevated)
    /// instance starts this task.
    /// </summary>
    public static string TaskXml(string sid, string exePath, string workingDir)
    {
        if (!IsValidSid(sid)) throw new ArgumentException("bad SID");
        // Task Scheduler expands %VARIABLES% in Command and WorkingDirectory: reject them, as well as quotes.
        if (!Path.IsPathFullyQualified(exePath) || !Path.IsPathFullyQualified(workingDir) ||
            exePath.IndexOfAny(['"', '%']) >= 0 || workingDir.IndexOfAny(['"', '%']) >= 0)
            throw new ArgumentException("paths must be absolute, without quotes or %");
        var sb = new StringBuilder();
        sb.Append("""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.3" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Author>DisplaySplit</Author>
                <Description>Starts DisplaySplit with administrator rights so it can also arrange windows of apps that run as administrator. Turn it off in the DisplaySplit tray menu (Manage admin windows).</Description>
            """).Append('\n');
        sb.Append("    <SecurityDescriptor>").Append(Escape(TaskSddl(sid))).Append("</SecurityDescriptor>\n");
        sb.Append("""
              </RegistrationInfo>
              <Triggers />
              <Principals>
                <Principal id="Author">
            """).Append('\n');
        sb.Append("      <UserId>").Append(Escape(sid)).Append("</UserId>\n");
        sb.Append("""
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
            """).Append('\n');
        sb.Append("      <Command>\"").Append(Escape(exePath)).Append("\"</Command>\n");
        sb.Append("      <Arguments>--admin-mode</Arguments>\n");
        sb.Append("      <WorkingDirectory>").Append(Escape(workingDir)).Append("</WorkingDirectory>\n");
        sb.Append("""
                </Exec>
              </Actions>
            </Task>
            """).Append('\n');
        return sb.ToString();
    }

    static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");

    public static bool PathsEqual(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// What the installer does with the protected data dir. The data dir is the newer copy when admin mode is still on
    /// (re-install / update) or when it was turned off but the user's copy never imported that handoff.
    /// </summary>
    public static bool InstallerShouldImportUserData(bool taskExisted, string? handoffId, string? userImportedId, bool dataDirHasData)
    {
        if (!dataDirHasData) return true;
        if (taskExisted) return false;
        bool handoffPending = IsValidHandoffId(handoffId) && handoffId != userImportedId;
        return !handoffPending;
    }

    /// <summary>Non-elevated start: import the admin-mode data once per handoff.</summary>
    public static bool ShouldImportHandoff(string? handoffId, string? userImportedId) =>
        IsValidHandoffId(handoffId) && handoffId != userImportedId;
}
