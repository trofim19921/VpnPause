using System.Diagnostics;
using System.Management;
using VpnPause.Core;

namespace VpnPause.App;

/// <summary>Imports and retires the PowerShell prototype from %LOCALAPPDATA%\Programs\VpnPause.</summary>
static class Legacy
{
    static string Marker => Path.Combine(AppPaths.UserDir, "legacy-imported");

    public static bool TryImport(out List<AppEntry> entries, out string? adapterName)
    {
        entries = new();
        adapterName = null;
        var apps = Path.Combine(AppPaths.LegacyDir, "apps.txt");
        if (File.Exists(Marker) || !File.Exists(apps)) return false;
        var presets = Presets.Resolve().Where(h => h.Path != null).Select(h => (hit: h, rule: AppRule.From(h.ToEntry()))).ToList();
        foreach (var line in File.ReadAllLines(apps))
        {
            var p = line.Trim();
            if (p.Length == 0 || p.StartsWith('#')) continue;
            var known = presets.FirstOrDefault(x => x.rule.Matches(p)).hit;
            entries.Add(known != null
                ? new AppEntry { Name = known.Preset.Name, Path = p, Scope = known.Preset.Scope, Preset = known.Preset.Id }
                : new AppEntry { Name = AppRule.TitleOf(p), Path = p, Scope = MatchScope.Folder });
        }
        var settings = Path.Combine(AppPaths.LegacyDir, "settings.txt");
        if (File.Exists(settings))
        {
            foreach (var line in File.ReadAllLines(settings))
            {
                var t = line.Trim();
                if (t.StartsWith("tunnel=", StringComparison.OrdinalIgnoreCase)) adapterName = t["tunnel=".Length..].Trim();
            }
        }
        adapterName ??= "tun2";
        return true;
    }

    public static void Retire(RotatingLog log)
    {
        try
        {
            var lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "VpnPause.lnk");
            if (File.Exists(lnk))
            {
                File.Delete(lnk);
                log.Write("removed old autostart shortcut");
            }
        }
        catch (Exception e)
        {
            log.Write("old shortcut: " + e.Message);
        }

        var legacyDir = AppPaths.LegacyDir.TrimEnd('\\') + "\\";
        try
        {
            using var search = new ManagementObjectSearcher("SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process WHERE Name = 'powershell.exe' OR Name = 'VpnPause.exe'");
            foreach (ManagementObject mo in search.Get())
            {
                var pid = Convert.ToInt32(mo["ProcessId"]);
                if (pid == Environment.ProcessId) continue;
                var exe = mo["ExecutablePath"] as string ?? "";
                var cmd = mo["CommandLine"] as string ?? "";
                var old = exe.StartsWith(legacyDir, StringComparison.OrdinalIgnoreCase) ||
                          cmd.Contains(legacyDir + "VpnPause.ps1", StringComparison.OrdinalIgnoreCase);
                if (!old) continue;
                try
                {
                    Process.GetProcessById(pid).Kill();
                    log.Write($"stopped old VpnPause process {pid}");
                }
                catch (Exception)
                {
                }
            }
        }
        catch (Exception e)
        {
            log.Write("old processes: " + e.Message);
        }

        Directory.CreateDirectory(AppPaths.UserDir);
        File.WriteAllText(Marker, DateTime.Now.ToString("s"));
    }
}
