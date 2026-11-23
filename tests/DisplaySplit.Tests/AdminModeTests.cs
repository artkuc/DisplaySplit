using System.Security.AccessControl;
using System.Security.Principal;
using System.Xml.Linq;
using DisplaySplit.Core;
using DisplaySplit.Platform;

namespace DisplaySplit.Tests;

public class AdminModeTests
{
    const string Sid = "S-1-5-21-422921545-2140749537-363824554-1002";
    const string Exe = @"C:\Program Files\DisplaySplit\DisplaySplit.exe";
    const string Dir = @"C:\Program Files\DisplaySplit";
    static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    // Rights that would let a principal change a file, directory or task.
    const int WriteBits = 0x2 /*WRITE_DATA/ADD_FILE*/ | 0x4 /*APPEND/ADD_SUBDIR*/ | 0x10 /*WRITE_EA*/ | 0x40 /*DELETE_CHILD*/ |
                          0x100 /*WRITE_ATTRIBUTES*/ | 0x10000 /*DELETE*/ | 0x40000 /*WRITE_DAC*/ | 0x80000 /*WRITE_OWNER*/ |
                          0x40000000 /*GENERIC_WRITE*/ | 0x10000000 /*GENERIC_ALL*/;

    [Theory]
    [InlineData("S-1-5-21-1-2-3-1001")]
    [InlineData(Sid)]
    public void Accepts_canonical_sids(string sid) => Assert.True(AdminModeSpec.IsValidSid(sid));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("S-1-5")]
    [InlineData("S-1-5-21-1)(A;;FA;;;WD)")]               // SDDL injection
    [InlineData("S-1-5-21-1</UserId><RunLevel>")]          // XML injection
    [InlineData("S-1-5-21-1 /tr calc")]                    // command-line injection
    [InlineData("S-1-5-21-1\\..\\..\\Windows")]            // path traversal
    [InlineData("WD")]
    [InlineData("S-1-5-21-1-2\n")]                         // trailing newline (a plain $ would accept it)
    public void Rejects_anything_else(string? sid)
    {
        Assert.False(AdminModeSpec.IsValidSid(sid));
        if (sid != null)
        {
            Assert.Throws<ArgumentException>(() => AdminModeSpec.TaskXml(sid, Exe, Dir));
            Assert.Throws<ArgumentException>(() => AdminModeSpec.DataDirSddl(sid));
            Assert.Throws<ArgumentException>(() => AdminModeSpec.TaskSddl(sid));
            Assert.Throws<ArgumentException>(() => AdminModeSpec.TaskName(sid));
        }
    }

    [Fact]
    public void Task_xml_is_an_on_demand_highest_available_task_for_this_user()
    {
        var doc = XDocument.Parse(AdminModeSpec.TaskXml(Sid, Exe, Dir));
        var task = doc.Root!;
        Assert.Equal(Ns + "Task", task.Name);
        Assert.Empty(task.Element(Ns + "Triggers")!.Elements()); // started only by the non-elevated instance
        var principal = task.Element(Ns + "Principals")!.Element(Ns + "Principal")!;
        Assert.Equal(Sid, principal.Element(Ns + "UserId")!.Value);
        Assert.Equal("InteractiveToken", principal.Element(Ns + "LogonType")!.Value); // no stored password
        Assert.Equal("HighestAvailable", principal.Element(Ns + "RunLevel")!.Value);
        var settings = task.Element(Ns + "Settings")!;
        Assert.Equal("IgnoreNew", settings.Element(Ns + "MultipleInstancesPolicy")!.Value);
        Assert.Equal("false", settings.Element(Ns + "DisallowStartIfOnBatteries")!.Value);
        Assert.Equal("false", settings.Element(Ns + "StopIfGoingOnBatteries")!.Value);
        Assert.Equal("PT0S", settings.Element(Ns + "ExecutionTimeLimit")!.Value);
        var exec = task.Element(Ns + "Actions")!.Element(Ns + "Exec")!;
        Assert.Equal($"\"{Exe}\"", exec.Element(Ns + "Command")!.Value);
        Assert.Equal("--admin-mode", exec.Element(Ns + "Arguments")!.Value);
        Assert.Equal(Dir, exec.Element(Ns + "WorkingDirectory")!.Value);
        Assert.Equal(AdminModeSpec.TaskSddl(Sid), task.Element(Ns + "RegistrationInfo")!.Element(Ns + "SecurityDescriptor")!.Value);
    }

    [Fact]
    public void Task_xml_escapes_paths()
    {
        const string odd = @"D:\Apps & <Tools>\DisplaySplit";
        var doc = XDocument.Parse(AdminModeSpec.TaskXml(Sid, odd + @"\DisplaySplit.exe", odd));
        var exec = doc.Root!.Element(Ns + "Actions")!.Element(Ns + "Exec")!;
        Assert.Equal($"\"{odd}\\DisplaySplit.exe\"", exec.Element(Ns + "Command")!.Value);
        Assert.Equal(odd, exec.Element(Ns + "WorkingDirectory")!.Value);
    }

    [Theory]
    [InlineData(@"DisplaySplit.exe", Dir)]
    [InlineData(Exe, @"relative\dir")]
    [InlineData(@"C:\a""b\DisplaySplit.exe", Dir)]
    [InlineData(@"C:\%TEMP%\DisplaySplit.exe", Dir)]     // Task Scheduler would expand it
    [InlineData(Exe, @"C:\%USERPROFILE%")]
    public void Task_xml_rejects_relative_or_quoted_paths(string exe, string dir) =>
        Assert.Throws<ArgumentException>(() => AdminModeSpec.TaskXml(Sid, exe, dir));

    static void AssertOnlyAdminsCanWrite(string sddl, bool expectOwner)
    {
        var sd = new RawSecurityDescriptor(sddl);
        Assert.True((sd.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0, "DACL must not inherit from the parent");
        if (expectOwner) Assert.Equal(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), sd.Owner);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        foreach (var ace in sd.DiscretionaryAcl!.Cast<CommonAce>())
        {
            Assert.Equal(AceQualifier.AccessAllowed, ace.AceQualifier);
            if (ace.SecurityIdentifier == admins || ace.SecurityIdentifier == system) continue;
            Assert.True((ace.AccessMask & WriteBits) == 0, $"{ace.SecurityIdentifier} must not get write access (mask 0x{ace.AccessMask:X})");
        }
    }

    [Fact]
    public void Install_dir_acl_is_admin_write_only() => AssertOnlyAdminsCanWrite(AdminModeSpec.InstallDirSddl, expectOwner: true);

    [Fact]
    public void Data_dir_acl_is_admin_write_only_and_readable_by_the_user_only()
    {
        string sddl = AdminModeSpec.DataDirSddl(Sid);
        AssertOnlyAdminsCanWrite(sddl, expectOwner: true);
        var sids = new RawSecurityDescriptor(sddl).DiscretionaryAcl!.Cast<CommonAce>().Select(a => a.SecurityIdentifier.Value).ToList();
        Assert.Contains(Sid, sids);
        Assert.DoesNotContain(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null).Value, sids);
    }

    [Fact]
    public void Task_acl_lets_the_user_run_but_not_change_it()
    {
        string sddl = AdminModeSpec.TaskSddl(Sid);
        AssertOnlyAdminsCanWrite(sddl, expectOwner: false);
        var user = new RawSecurityDescriptor(sddl).DiscretionaryAcl!.Cast<CommonAce>().Single(a => a.SecurityIdentifier.Value == Sid);
        Assert.True((user.AccessMask & 0x20 /*FILE_EXECUTE*/) != 0);
    }

    [Theory]
    // installer: (taskExisted, handoffId, userImported, dataDirHasData) → import user data?
    [InlineData(false, null, null, false, true)]                                                          // first enable
    [InlineData(false, null, null, true, true)]                                                           // re-enable, user copy is current
    [InlineData(true, null, null, true, false)]                                                           // update while on: keep admin data
    [InlineData(false, "0123456789abcdef0123456789abcdef", null, true, false)]                            // turned off, never imported: keep
    [InlineData(false, "0123456789abcdef0123456789abcdef", "0123456789abcdef0123456789abcdef", true, true)] // turned off and imported
    [InlineData(true, null, null, false, true)]                                                           // data dir lost: import
    public void Installer_import_decision(bool taskExisted, string? handoff, string? imported, bool hasData, bool expected) =>
        Assert.Equal(expected, AdminModeSpec.InstallerShouldImportUserData(taskExisted, handoff, imported, hasData));

    [Fact]
    public void Handoff_is_imported_once_and_only_with_a_valid_id()
    {
        const string id = "0123456789abcdef0123456789abcdef";
        Assert.True(AdminModeSpec.ShouldImportHandoff(id, null));
        Assert.True(AdminModeSpec.ShouldImportHandoff(id, "ffffffffffffffffffffffffffffffff"));
        Assert.False(AdminModeSpec.ShouldImportHandoff(id, id));
        Assert.False(AdminModeSpec.ShouldImportHandoff(null, null));
        Assert.False(AdminModeSpec.ShouldImportHandoff("../../x", null));
    }

    [Fact]
    public void Paths_compare_case_and_trailing_slash_insensitive()
    {
        Assert.True(AdminModeSpec.PathsEqual(Exe, Exe.ToUpperInvariant()));
        Assert.True(AdminModeSpec.PathsEqual(Dir + "\\", Dir));
        Assert.True(AdminModeSpec.PathsEqual(@"C:\Program Files\DisplaySplit\x\..\DisplaySplit.exe", Exe));
        Assert.False(AdminModeSpec.PathsEqual(Exe, @"C:\Users\x\Downloads\DisplaySplit.exe"));
        Assert.False(AdminModeSpec.PathsEqual(null, Exe));
    }

    [Fact]
    public void Framework_dependent_builds_never_offer_admin_mode()
    {
        // Tests run on the JIT, like the framework-dependent build: DOTNET_ROOT / startup hooks are user-controlled.
        Assert.False(AdminMode.IsNativeAot);
        Assert.False(AdminMode.Supported);
        Assert.False(AdminMode.IsActive);
        Assert.False(AdminMode.TryEnterAdminMode([AdminMode.ArgAdminMode]));
    }

    [Fact]
    public void Storage_migration_refuses_links_and_reads_typed_data_only()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ds-admin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "settings.json"), """{ "gapPx": 8, "evil": "ignored" }""");
            Assert.True(Storage.TryLoadFrom(dir, "settings.json", JsonCtx.Default.Settings, out var s));
            Assert.Equal(8, s!.GapPx);

            File.WriteAllText(Path.Combine(dir, "layouts.json"), "not json");
            Assert.False(Storage.TryLoadFrom(dir, "layouts.json", JsonCtx.Default.LayoutsFile, out _));
            Assert.False(Storage.TryLoadFrom(dir, "missing.json", JsonCtx.Default.Settings, out _));

            // A symlink (needs Developer Mode or admin; skipped otherwise) must not be followed.
            string link = Path.Combine(dir, "placements.json");
            bool linked = false;
            try { File.CreateSymbolicLink(link, Path.Combine(dir, "settings.json")); linked = true; } catch { /* no privilege */ }
            if (linked) Assert.False(Storage.TryLoadFrom(dir, "placements.json", JsonCtx.Default.PlacementsFile, out _));
        }
        finally { Directory.Delete(dir, true); }
    }
}
