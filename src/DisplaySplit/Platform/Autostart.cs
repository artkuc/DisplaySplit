using Microsoft.Win32;

namespace DisplaySplit.Platform;

/// <summary>Per-user autostart via HKCU\...\Run (no admin rights needed). Launches with --autostart.</summary>
public static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "DisplaySplit";

    static string Command => $"\"{Environment.ProcessPath}\" --autostart";

    public static bool IsEnabled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return k?.GetValue(ValueName) is string s && s.Length > 0;
        }
        catch { return false; }
    }

    /// <summary>Enable/disable; also refreshes the stored path if the exe moved.</summary>
    public static void Set(bool enabled)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey, true);
            if (enabled)
            {
                if (k.GetValue(ValueName) as string != Command) k.SetValue(ValueName, Command, RegistryValueKind.String);
            }
            else if (k.GetValue(ValueName) != null) k.DeleteValue(ValueName, false);
        }
        catch (Exception ex) { Log.Warn($"autostart: {ex.Message}"); }
    }
}
