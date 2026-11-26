using System.Text.Json;

namespace VpnPause.Core;

public static class ConfigStore
{
    public static AppConfig Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new AppConfig();
            var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), Json.Options);
            return Normalize(cfg ?? new AppConfig(), out _);
        }
        catch (Exception)
        {
            return new AppConfig();
        }
    }

    public static void Save(string path, AppConfig cfg)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(cfg, Json.Options));
        File.Move(tmp, path, overwrite: true);
    }

    public static AppConfig Normalize(AppConfig cfg, out List<string> rejected)
    {
        rejected = new List<string>();
        cfg.Freeze ??= new FreezeSettings();
        cfg.Freeze.DelaySeconds = Math.Clamp(cfg.Freeze.DelaySeconds, 0, 60);
        cfg.Freeze.MaxSeconds = Math.Clamp(cfg.Freeze.MaxSeconds, 10, 3600);
        cfg.Language = cfg.Language is "en" ? "en" : "ru";
        cfg.Exclusions = (cfg.Exclusions ?? new()).Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var kept = new List<AppEntry>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in cfg.Apps ?? new())
        {
            var why = AppRule.Validate(app);
            if (why != null)
            {
                rejected.Add($"{app.Path}: {why}");
                continue;
            }
            var rule = AppRule.From(app);
            if (!keys.Add(rule.Key)) continue;
            if (string.IsNullOrWhiteSpace(app.Name)) app.Name = AppRule.TitleOf(app.Path);
            kept.Add(app);
        }
        cfg.Apps = kept;
        return cfg;
    }
}
