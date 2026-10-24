using System.Diagnostics;

namespace ArnoA2.App;

/// <summary>
/// KWin puts a borderless game that covers the screen in the fullscreen layer, above any
/// "keep above" window, so the overlays need a forced Overlay layer rule (the same fix
/// A2Tools DPS Meter applies). Keys are KWin's (rulesettings.kcfg): rule 2 = Force,
/// match 1 = exact, 2 = substring. KWin deletes groups not listed in General/rules, so
/// ours are appended to the existing list and nothing is removed.
/// </summary>
public static class KdeWindowRules
{
    const string Class = "arno-pet-tracker";
    public const string TrackerTitle = "Arno's Pet Tracker", MiniMapTitle = "Arno's Pet Tracker · mini-map";
    static readonly (string Group, (string Key, string Value)[] Entries)[] Rules =
    {
        ("arno-pet-tracker-minimap", new[]
        {
            ("Description", "Arno's Pet Tracker mini-map above fullscreen games (added by Arno's Pet Tracker)"),
            ("wmclass", Class), ("wmclassmatch", "1"), ("wmclasscomplete", "false"),
            ("title", MiniMapTitle), ("titlematch", "1"),
            ("layer", "overlay"), ("layerrule", "2"), ("above", "true"), ("aboverule", "2"),
            // Clicking the overlay leaves keyboard focus with the game.
            ("acceptfocus", "false"), ("acceptfocusrule", "2"),
            ("skipswitcher", "true"), ("skipswitcherrule", "2"), ("skiptaskbar", "true"), ("skiptaskbarrule", "2")
        }),
        ("arno-pet-tracker-overlay", new[]
        {
            ("Description", "Arno's Pet Tracker overlay above fullscreen games (added by Arno's Pet Tracker)"),
            ("wmclass", Class), ("wmclassmatch", "1"), ("wmclasscomplete", "false"),
            ("title", TrackerTitle), ("titlematch", "1"),
            ("layer", "overlay"), ("layerrule", "2"), ("above", "true"), ("aboverule", "2")
        })
    };

    public static bool Available => (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "").Contains("KDE", StringComparison.OrdinalIgnoreCase);

    public static bool Installed()
    {
        string listed = Run("kreadconfig6", "--file", "kwinrulesrc", "--group", "General", "--key", "rules");
        return Rules.All(r => listed.Split(',').Contains(r.Group));
    }

    public static void Install()
    {
        string listed = Run("kreadconfig6", "--file", "kwinrulesrc", "--group", "General", "--key", "rules").Trim();
        var groups = listed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        foreach (var (group, entries) in Rules)
        {
            foreach (var (key, value) in entries)
                Run("kwriteconfig6", "--file", "kwinrulesrc", "--group", group, "--key", key, value);
            if (!groups.Contains(group))
                groups.Add(group);
        }

        Run("kwriteconfig6", "--file", "kwinrulesrc", "--group", "General", "--key", "rules", string.Join(",", groups));
        Run("kwriteconfig6", "--file", "kwinrulesrc", "--group", "General", "--key", "count", groups.Count.ToString());
        Run("qdbus6", "org.kde.KWin", "/KWin", "reconfigure");
    }

    static string Run(string program, params string[] args)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string arg in args)
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException(program + " did not start.");
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(5000);
        if (process.ExitCode != 0)
            throw new IOException(program + " failed: " + process.StandardError.ReadToEnd().Trim());
        return output;
    }
}
