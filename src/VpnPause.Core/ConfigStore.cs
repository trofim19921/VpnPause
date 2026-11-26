using System.Text.Json;

namespace VpnPause.Core;

public static class ConfigStore
{
    public const int MaxApps = 200;
    public const int MaxExclusions = 100;

    public static AppConfig Load(string path) => Load(path, out _, out _);

    /// <summary>
    /// Reads the config, falling back to the .bak copy. <paramref name="error"/> describes damage;
    /// <paramref name="unusable"/> is true when neither file could be read and an empty config is returned.
    /// </summary>
    public static AppConfig Load(string path, out string? error, out bool unusable)
    {
        error = null;
        unusable = false;
        var bak = path + ".bak";
        if (!File.Exists(path) && !File.Exists(bak)) return new AppConfig();
        if (TryRead(path, out var cfg, out var why)) return cfg;
        if (TryRead(bak, out cfg, out _))
        {
            error = $"config.json is damaged ({why}), restored from config.json.bak";
            return cfg;
        }
        error = $"config.json is damaged ({why}) and there is no usable backup";
        unusable = true;
        return new AppConfig();
    }

    static bool TryRead(string path, out AppConfig cfg, out string? why)
    {
        cfg = new AppConfig();
        why = null;
        try
        {
            if (!File.Exists(path))
            {
                why = "missing";
                return false;
            }
            var read = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), Json.Options);
            if (read == null)
            {
                why = "empty";
                return false;
            }
            cfg = Normalize(read, out _);
            return true;
        }
        catch (Exception e)
        {
            why = e.Message;
            return false;
        }
    }

    /// <summary>Writes through to disk and keeps the previous good file as .bak.</summary>
    public static void Save(string path, AppConfig cfg)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(fs, cfg, Json.Options);
            fs.Flush(true);
        }
        if (File.Exists(path) && TryRead(path, out _, out _))
            File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);
        else
            File.Move(tmp, path, overwrite: true);
    }

    public static AppConfig Normalize(AppConfig cfg, out List<string> rejected)
    {
        rejected = new List<string>();
        cfg.Freeze ??= new FreezeSettings();
        cfg.Freeze.DelaySeconds = Math.Clamp(cfg.Freeze.DelaySeconds, 0, 60);
        cfg.Freeze.MaxSeconds = Math.Clamp(cfg.Freeze.MaxSeconds, 10, 3600);
        cfg.Language = cfg.Language is "en" ? "en" : "ru";

        var exclusions = new List<string>();
        foreach (var raw in cfg.Exclusions ?? new())
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var e = raw.Trim();
            var why = AppRule.ValidateExclusion(e);
            if (why != null) rejected.Add($"{e}: {why}");
            else if (exclusions.Count >= MaxExclusions) rejected.Add($"{e}: too many exclusions (limit {MaxExclusions})");
            else if (!exclusions.Contains(e, StringComparer.OrdinalIgnoreCase)) exclusions.Add(e);
        }
        cfg.Exclusions = exclusions;

        var kept = new List<AppEntry>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in cfg.Apps ?? new())
        {
            if (app == null) continue;
            var why = AppRule.Validate(app);
            if (why != null)
            {
                rejected.Add($"{app.Path}: {why}");
                continue;
            }
            var rule = AppRule.From(app);
            if (!keys.Add(rule.Key)) continue;
            if (kept.Count >= MaxApps)
            {
                rejected.Add($"{app.Path}: too many apps (limit {MaxApps})");
                continue;
            }
            if (string.IsNullOrWhiteSpace(app.Name)) app.Name = AppRule.TitleOf(app.Path);
            kept.Add(app);
        }
        cfg.Apps = kept;
        return cfg;
    }
}
