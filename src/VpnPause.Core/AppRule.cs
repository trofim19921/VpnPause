using System.Text.RegularExpressions;

namespace VpnPause.Core;

public enum RuleKind
{
    StorePackage,
    ClaudeCode,
    Folder,
    Exe
}

/// <summary>Compiled form of an <see cref="AppEntry"/>: decides which process images belong to the app.</summary>
public sealed class AppRule
{
    static readonly Regex StoreFolder = new(@"\\windowsapps\\([^\\]+)\\", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex PackageFullName = new(@"^([^_]+)_[^_]+_[^_]*_[^_]*_([^_]+)$", RegexOptions.CultureInvariant);
    static readonly Regex VersionSegment = new(@"^(app-)?v?\d+(\.\d+){1,4}([-+][0-9a-z.]+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex BroadFolder = new(
        @"^[a-z]:\\(?:(?:program files(?: \(x86\))?(?:\\windowsapps|\\common files)?)|programdata|users|users\\[^\\]+(?:\\appdata(?:\\(?:local|roaming|locallow)(?:\\programs)?)?|\\desktop|\\downloads|\\documents|\\onedrive)?)?\\?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static readonly string[] VpnClients =
    {
        "amneziavpn", "amneziawg", "xray", "tun2socks", "wireguard", "openvpn", "v2ray", "sing-box", "hiddify",
        "outline", "nekoray", "nekobox", "clash", "mihomo", "tailscale", "zerotier", "proton", "nordvpn", "expressvpn"
    };

    public AppEntry Entry { get; }
    public RuleKind Kind { get; }
    public string Key { get; }
    public string? StoreFamily { get; }
    public string? StorePackageName { get; }
    public string? StorePublisherId { get; }

    readonly string? exeName;
    readonly string? prefix;
    readonly string? exact;

    AppRule(AppEntry entry, RuleKind kind, string key, string? family = null, string? packageName = null,
        string? publisher = null, string? exeName = null, string? prefix = null, string? exact = null)
    {
        Entry = entry;
        Kind = kind;
        Key = key;
        StoreFamily = family;
        StorePackageName = packageName;
        StorePublisherId = publisher;
        this.exeName = exeName;
        this.prefix = prefix;
        this.exact = exact;
    }

    public static AppRule From(AppEntry entry)
    {
        var p = Normalize(entry.Path);
        var exe = System.IO.Path.GetFileName(p);

        var store = TryStoreFamily(p, out var name, out var publisher);
        if (store != null)
        {
            var scopedExe = entry.Scope == MatchScope.Exe ? exe : null;
            var key = scopedExe == null ? $"pkg:{store}" : $"pkg:{store}:{scopedExe}";
            return new AppRule(entry, RuleKind.StorePackage, key, store, name, publisher, exeName: scopedExe);
        }

        var cc = p.IndexOf(@"\claude-code\", StringComparison.Ordinal);
        if (cc >= 0)
        {
            var root = p[..(cc + @"\claude-code\".Length)];
            return new AppRule(entry, RuleKind.ClaudeCode, $"claudecode:{root}", exeName: exe, prefix: root);
        }

        if (entry.Scope == MatchScope.Folder)
        {
            var folder = FolderOf(p);
            if (!IsBroadFolder(folder))
            {
                var pre = folder.TrimEnd('\\') + "\\";
                return new AppRule(entry, RuleKind.Folder, $"dir:{pre}", prefix: pre);
            }
        }

        var norm = StripVersions(p);
        return new AppRule(entry, RuleKind.Exe, $"exe:{norm}", exact: norm);
    }

    /// <summary>Returns null when the entry is acceptable, otherwise a short reason.</summary>
    public static string? Validate(AppEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Path)) return "empty path";
        string full;
        try
        {
            if (!System.IO.Path.IsPathFullyQualified(entry.Path)) return "path is not absolute";
            full = System.IO.Path.GetFullPath(entry.Path);
        }
        catch (Exception)
        {
            return "invalid path";
        }
        if (!full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return "not an .exe";
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(win) && full.StartsWith(win.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            return "system programs from the Windows folder cannot be added";
        var file = System.IO.Path.GetFileNameWithoutExtension(full).ToLowerInvariant();
        if (file == "vpnpause") return "VpnPause itself cannot be added";
        if (VpnClients.Any(v => file.Contains(v)) || full.Contains(@"\AmneziaVPN\", StringComparison.OrdinalIgnoreCase))
            return "this is a VPN client: blocking it would stop the VPN itself";
        return null;
    }

    public bool Matches(string? processPath, IReadOnlyCollection<string>? exclusions = null)
    {
        if (string.IsNullOrEmpty(processPath)) return false;
        var p = Normalize(processPath);
        if (exclusions != null)
        {
            foreach (var ex in exclusions)
                if (p.Contains(ex.ToLowerInvariant(), StringComparison.Ordinal)) return false;
        }
        switch (Kind)
        {
            case RuleKind.StorePackage:
                if (TryStoreFamily(p, out _, out _) is not { } fam || !string.Equals(fam, StoreFamily, StringComparison.OrdinalIgnoreCase)) return false;
                return exeName == null || System.IO.Path.GetFileName(p) == exeName;
            case RuleKind.ClaudeCode:
                return p.StartsWith(prefix!, StringComparison.Ordinal) && System.IO.Path.GetFileName(p) == exeName;
            case RuleKind.Folder:
                return p.StartsWith(prefix!, StringComparison.Ordinal);
            default:
                return StripVersions(p) == exact;
        }
    }

    /// <summary>Executables on disk that belong to this app. Used to pre-create network filters before the app starts.</summary>
    public IEnumerable<string> CandidateFiles(int limit = 400)
    {
        var found = new List<string>();
        try
        {
            switch (Kind)
            {
                case RuleKind.StorePackage:
                    var root = StoreRoot(Entry.Path);
                    if (root == null || !Directory.Exists(root)) break;
                    foreach (var dir in Directory.EnumerateDirectories(root, $"{StorePackageName}_*_{StorePublisherId}"))
                    {
                        if (TryStoreFamily(Normalize(dir) + "\\", out _, out _) == null) continue;
                        Collect(dir, exeName ?? "*.exe", found, limit);
                    }
                    break;
                case RuleKind.ClaudeCode:
                    Collect(prefix!, exeName!, found, limit);
                    break;
                case RuleKind.Folder:
                    Collect(prefix!, "*.exe", found, limit);
                    break;
                default:
                    foreach (var f in ExpandVersions(Entry.Path)) found.Add(f);
                    break;
            }
        }
        catch (Exception)
        {
        }
        if (File.Exists(Entry.Path)) found.Add(Entry.Path);
        return found.Distinct(StringComparer.OrdinalIgnoreCase).Take(limit);
    }

    public static string TitleOf(string path)
    {
        var p = path.Replace('/', '\\').Trim();
        if (TryStoreFamily(p, out var name, out _) != null && name != null)
        {
            var dot = name.LastIndexOf('.');
            return dot >= 0 && dot < name.Length - 1 ? name[(dot + 1)..] : name;
        }
        return System.IO.Path.GetFileNameWithoutExtension(path);
    }

    public static string? TryStoreFamily(string normalizedPath, out string? name, out string? publisher)
    {
        name = publisher = null;
        var m = StoreFolder.Match(normalizedPath);
        if (!m.Success) return null;
        var n = PackageFullName.Match(m.Groups[1].Value);
        if (!n.Success) return null;
        name = n.Groups[1].Value;
        publisher = n.Groups[2].Value;
        return $"{name}_{publisher}";
    }

    public static bool IsBroadFolder(string folder) => BroadFolder.IsMatch(Normalize(folder).TrimEnd('\\') + "\\");

    public static bool IsVersionSegment(string segment) => VersionSegment.IsMatch(segment);

    internal static string Normalize(string path) => path.Replace('/', '\\').Trim().ToLowerInvariant();

    internal static string FolderOf(string normalizedPath)
    {
        var dir = System.IO.Path.GetDirectoryName(normalizedPath) ?? normalizedPath;
        while (true)
        {
            var leaf = System.IO.Path.GetFileName(dir);
            var parent = System.IO.Path.GetDirectoryName(dir);
            if (parent == null || !IsVersionSegment(leaf)) return dir;
            dir = parent;
        }
    }

    internal static string StripVersions(string normalizedPath)
    {
        var parts = normalizedPath.Split('\\');
        for (int i = 0; i < parts.Length - 1; i++)
            if (IsVersionSegment(parts[i])) parts[i] = "*";
        return string.Join('\\', parts);
    }

    static string? StoreRoot(string path)
    {
        var i = path.IndexOf(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);
        return i < 0 ? null : path[..(i + @"\WindowsApps".Length)];
    }

    static void Collect(string dir, string pattern, List<string> found, int limit)
    {
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            MaxRecursionDepth = 8
        };
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir, pattern, opts))
        {
            found.Add(f);
            if (found.Count >= limit) return;
        }
    }

    static IEnumerable<string> ExpandVersions(string path)
    {
        var parts = path.Split('\\');
        var current = new List<string> { parts[0] };
        for (int i = 1; i < parts.Length; i++)
        {
            var next = new List<string>();
            foreach (var basePath in current)
            {
                if (i < parts.Length - 1 && IsVersionSegment(parts[i]))
                {
                    var root = basePath + "\\";
                    if (!Directory.Exists(root)) continue;
                    foreach (var d in Directory.EnumerateDirectories(root))
                        if (IsVersionSegment(System.IO.Path.GetFileName(d))) next.Add(d);
                }
                else next.Add(basePath + "\\" + parts[i]);
            }
            current = next;
            if (current.Count > 50) current = current.Take(50).ToList();
        }
        return current.Where(File.Exists);
    }
}
