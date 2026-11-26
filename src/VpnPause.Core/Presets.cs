using System.Reflection;
using System.Text.Json;
using Microsoft.Win32;

namespace VpnPause.Core;

public sealed class StoreSpec
{
    public string Family { get; set; } = "";
    public string Exe { get; set; } = "";
}

public sealed class Preset
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<StoreSpec> Store { get; set; } = new();
    public List<string> Paths { get; set; } = new();
    public MatchScope Scope { get; set; } = MatchScope.Folder;
    public string? Hint { get; set; }
}

public sealed record PresetHit(Preset Preset, string? Path)
{
    public bool Installed => Path != null;

    public AppEntry ToEntry() => new() { Name = Preset.Name, Path = Path!, Scope = Preset.Scope, Preset = Preset.Id };
}

public static class Presets
{
    const string PackagesKey = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    public static IReadOnlyList<Preset> All { get; } = LoadEmbedded();

    static IReadOnlyList<Preset> LoadEmbedded()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("VpnPause.presets.json")!;
        return JsonSerializer.Deserialize<List<Preset>>(s, Json.Options)!;
    }

    /// <summary>Finds where each preset is installed for the current user. Must run in the user's session.</summary>
    public static IReadOnlyList<PresetHit> Resolve()
    {
        var packages = InstalledPackages();
        return All.Select(p => new PresetHit(p, Find(p, packages))).ToList();
    }

    static string? Find(Preset preset, Dictionary<string, string> packages)
    {
        foreach (var spec in preset.Store)
        {
            if (!packages.TryGetValue(spec.Family.ToLowerInvariant(), out var root)) continue;
            var exe = FindFile(root, spec.Exe, 3);
            if (exe != null) return exe;
        }
        foreach (var pattern in preset.Paths)
        {
            var hit = ExpandPattern(Environment.ExpandEnvironmentVariables(pattern)).FirstOrDefault();
            if (hit != null) return hit;
        }
        return null;
    }

    /// <summary>Package family name (lower case) to the root folder of its newest installed version.</summary>
    public static Dictionary<string, string> InstalledPackages()
    {
        var result = new Dictionary<string, (Version ver, string root)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PackagesKey);
            if (key == null) return new();
            foreach (var full in key.GetSubKeyNames())
            {
                var parts = full.Split('_');
                if (parts.Length < 5 || !Version.TryParse(parts[1], out var ver)) continue;
                using var sub = key.OpenSubKey(full);
                if (sub?.GetValue("PackageRootFolder") is not string root || !Directory.Exists(root)) continue;
                var family = $"{parts[0]}_{parts[^1]}".ToLowerInvariant();
                if (!result.TryGetValue(family, out var cur) || ver > cur.ver) result[family] = (ver, root);
            }
        }
        catch (Exception)
        {
        }
        return result.ToDictionary(kv => kv.Key, kv => kv.Value.root, StringComparer.OrdinalIgnoreCase);
    }

    static string? FindFile(string root, string name, int depth)
    {
        var direct = System.IO.Path.Combine(root, name);
        if (File.Exists(direct)) return direct;
        try
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = depth, IgnoreInaccessible = true };
            return Directory.EnumerateFiles(root, name, opts).FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Expands '*' path segments to existing directories; returns existing files, newest first.</summary>
    public static IEnumerable<string> ExpandPattern(string pattern)
    {
        var parts = pattern.Split('\\');
        var current = new List<string> { parts[0] };
        for (int i = 1; i < parts.Length; i++)
        {
            var next = new List<string>();
            foreach (var b in current)
            {
                if (parts[i] == "*")
                {
                    var dir = b + "\\";
                    if (!Directory.Exists(dir)) continue;
                    try
                    {
                        next.AddRange(Directory.EnumerateDirectories(dir).OrderByDescending(Directory.GetLastWriteTimeUtc));
                    }
                    catch (Exception)
                    {
                    }
                }
                else next.Add(b + "\\" + parts[i]);
            }
            current = next.Take(64).ToList();
        }
        return current.Where(File.Exists);
    }
}
