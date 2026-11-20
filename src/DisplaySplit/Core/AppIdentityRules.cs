using System.Text;
using System.Text.RegularExpressions;

namespace DisplaySplit.Core;

/// <summary>Pure string rules used to build stable app keys and comparable window titles.</summary>
public static partial class AppIdentityRules
{
    [GeneratedRegex(@"\\app-\d+(\.\d+)*[^\\]*\\", RegexOptions.IgnoreCase)]
    private static partial Regex SquirrelVersionDir();

    // WindowsApps\Publisher.Name_1.2.3.0_x64__8wekyb3d8bbwe\  ->  WindowsApps\Publisher.Name__8wekyb3d8bbwe\
    [GeneratedRegex(@"\\windowsapps\\([^\\_]+)_[^\\_]*_[^\\_]*_[^\\_]*_([^\\]+)\\", RegexOptions.IgnoreCase)]
    private static partial Regex WindowsAppsDir();

    // Generic version folders such as \1.2.3\ or \v12.0.1\ or \12.0.6099.130\ (Chrome style)
    [GeneratedRegex(@"\\v?\d+\.\d+(\.\d+){0,3}\\", RegexOptions.IgnoreCase)]
    private static partial Regex VersionDir();

    [GeneratedRegex(@"^\(\d+\+?\)\s*|\s*\(\d+\+?\)$|\[\d+\+?\]\s*")]
    private static partial Regex Counters();

    [GeneratedRegex(@"^[●•*]\s*|\s*[●•*]$")]
    private static partial Regex DirtyMarkers();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}", RegexOptions.IgnoreCase)]
    private static partial Regex Guid();

    /// <summary>Window classes that embed per-launch GUIDs (WPF: HwndWrapper[app.exe;;GUID]) are made stable.</summary>
    public static string NormalizeClass(string windowClass) =>
        string.IsNullOrEmpty(windowClass) ? "" : Guid().Replace(windowClass, "*");

    /// <summary>Normalise a full key (used when loading keys stored by older builds).</summary>
    public static string NormalizeKey(string key)
    {
        int i = key.LastIndexOf('|');
        return i < 0 ? key : MakeKey(key[..i], key[(i + 1)..]);
    }

    /// <summary>Normalise an executable path so app updates don't change the key.</summary>
    public static string NormalizeExePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        string p = path.Replace('/', '\\').ToLowerInvariant();
        p = SquirrelVersionDir().Replace(p, "\\");
        p = WindowsAppsDir().Replace(p, m => $"\\windowsapps\\{m.Groups[1].Value}__{m.Groups[2].Value}\\");
        p = VersionDir().Replace(p, "\\");
        return p;
    }

    public static string MakeKey(string identity, string windowClass) => $"{identity}|{NormalizeClass(windowClass)}";

    public static string IdentityPart(string key)
    {
        int i = key.LastIndexOf('|');
        return i < 0 ? key : key[..i];
    }

    /// <summary>
    /// Normalise a window title for matching multi-window apps: drop the trailing app-name segment
    /// ("foo.cs - MyProj - Visual Studio Code" → "foo.cs - myproj"), unread counters "(3)", dirty markers "●".
    /// </summary>
    public static string NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        string t = title.Trim();
        t = Counters().Replace(t, "");
        t = DirtyMarkers().Replace(t, "");
        // Split on " - ", " — ", " – ", " | "
        var parts = SplitSegments(t);
        if (parts.Count >= 2) parts.RemoveAt(parts.Count - 1);
        t = string.Join(" - ", parts);
        t = Spaces().Replace(t, " ").Trim().ToLowerInvariant();
        return t;
    }

    static List<string> SplitSegments(string t)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        for (int i = 0; i < t.Length; i++)
        {
            if (i + 2 < t.Length && t[i] == ' ' && t[i + 2] == ' ' && (t[i + 1] is '-' or '—' or '–' or '|'))
            {
                list.Add(sb.ToString().Trim()); sb.Clear(); i += 2; continue;
            }
            sb.Append(t[i]);
        }
        list.Add(sb.ToString().Trim());
        list.RemoveAll(string.IsNullOrEmpty);
        return list;
    }

    /// <summary>Title similarity score: 3 exact, 2 one contains the other, 1 shares the first segment, 0 none.</summary>
    public static int TitleScore(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 3;
        if (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal)) return 2;
        string fa = FirstSegment(a), fb = FirstSegment(b);
        return fa.Length > 0 && fa == fb ? 1 : 0;
    }

    static string FirstSegment(string s)
    {
        int i = s.IndexOf(" - ", StringComparison.Ordinal);
        return i < 0 ? s : s[..i];
    }
}
