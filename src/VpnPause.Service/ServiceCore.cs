using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using VpnPause.Core;
using VpnPause.Service.Wfp;

namespace VpnPause.Service;

/// <summary>Runs as LocalSystem. Keeps the WFP filters in line with the config and the current VPN adapter.</summary>
public sealed class ServiceCore : IDisposable
{
    static readonly TimeSpan TargetsTtl = TimeSpan.FromSeconds(30);
    static readonly TimeSpan RetryAfterError = TimeSpan.FromSeconds(5);
    static readonly TimeSpan ProcessScanEvery = TimeSpan.FromSeconds(2);

    readonly RotatingLog log = new(AppPaths.ServiceLogDir, "service");
    readonly WfpEngine wfp = new();
    readonly AutoResetEvent wake = new(false);
    readonly object gate = new();
    readonly CancellationTokenSource stop = new();

    AppConfig config = new();
    volatile IReadOnlyList<AppRule> rules = Array.Empty<AppRule>();
    volatile bool targetsStale = true;
    List<FilterTarget> targets = new();
    HashSet<string> knownPaths = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> checkedPaths = new(StringComparer.OrdinalIgnoreCase);
    DateTime targetsAt;
    DateTime lastProcessScan;
    string appliedSignature = "";
    DateTime lastFailure = DateTime.MinValue;
    DateTime? vpnUpSince;
    DateTime lastStuckCheck;
    ServiceStatus status = new();
    Thread? loop;
    PipeServer? pipe;

    public static string Version => Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0";

    public RotatingLog Log => log;

    public void Start()
    {
        PrepareDataDir();
        lock (gate)
        {
            config = ConfigStore.Load(AppPaths.ConfigFile);
            rules = config.Apps.Select(AppRule.From).ToList();
        }
        log.Write($"service start {Version}, apps: {config.Apps.Count}, strict: {config.StrictMode}");
        RemoveLegacyFirewallRules();

        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailability;
        pipe = new PipeServer(Handle, log);
        pipe.Start(stop.Token);

        loop = new Thread(Loop) { IsBackground = true, Name = "VpnPause.Service" };
        loop.Start();
    }

    public void Stop()
    {
        if (stop.IsCancellationRequested) return;
        stop.Cancel();
        wake.Set();
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailability;
        loop?.Join(5000);
        lock (gate)
        {
            wfp.Close();
        }
        log.Write(config.StrictMode ? "service stop, strict filters stay" : "service stop, filters removed");
    }

    public void Dispose()
    {
        Stop();
        wfp.Dispose();
    }

    void OnNetworkChanged(object? sender, EventArgs e) => wake.Set();

    void OnNetworkAvailability(object? sender, NetworkAvailabilityEventArgs e) => wake.Set();

    void Loop()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                Tick();
            }
            catch (Exception e)
            {
                log.Write("tick error: " + e.Message);
            }
            wake.WaitOne(1000);
        }
    }

    void Tick()
    {
        AppConfig cfg;
        lock (gate) cfg = config;

        var vpn = Network.Status(cfg);
        var now = DateTime.UtcNow;

        if (!targetsStale) WatchNewBinaries(cfg, now);
        if (targetsStale || now - targetsAt > TargetsTtl) RebuildTargets(cfg);

        var plan = new FilterPlan
        {
            Enabled = cfg.Enabled,
            Strict = cfg.StrictMode,
            AllowLan = cfg.AllowLan,
            VpnLuids = vpn.Luids,
            Targets = targets
        };
        var signature = plan.Signature();
        string? error = status.LastError;
        if (signature != appliedSignature && now - lastFailure > RetryAfterError)
        {
            try
            {
                lock (gate) wfp.Apply(plan, log.Write);
                appliedSignature = signature;
                error = null;
                log.Write(plan.Enabled
                    ? $"filters: {wfp.FilterCount} for {plan.Targets.Count} targets, VPN adapter: {(vpn.Adapter?.Name ?? "not found")}"
                    : "protection off, filters removed");
            }
            catch (Exception e)
            {
                lastFailure = now;
                error = e.Message;
                log.Write("apply failed: " + e.Message);
            }
        }

        if (vpn.Up)
        {
            vpnUpSince ??= now;
            if (now - vpnUpSince > TimeSpan.FromSeconds(10) && now - lastStuckCheck > TimeSpan.FromSeconds(15))
            {
                lastStuckCheck = now;
                var released = Freezer.ReleaseStuck(rules, cfg.Exclusions);
                if (released > 0) log.Write($"released {released} processes left frozen while the VPN is up");
            }
        }
        else vpnUpSince = null;

        status = new ServiceStatus
        {
            Version = Version,
            Enabled = cfg.Enabled,
            Strict = cfg.StrictMode,
            AdapterFound = vpn.AdapterFound,
            VpnUp = vpn.Up,
            AdapterName = vpn.Adapter?.Name ?? cfg.Adapter?.Name ?? "",
            DnsOutside = vpn.DnsOutside,
            FilterCount = wfp.FilterCount,
            Targets = targets.GroupBy(t => t.Name).Select(g => new TargetInfo
            {
                Name = g.Key,
                Files = g.Count(t => t.Path != null),
                Package = g.Any(t => t.PackageFamily != null)
            }).ToList(),
            LastError = error,
            AppliedAt = DateTime.Now
        };
    }

    void RebuildTargets(AppConfig cfg)
    {
        targetsStale = false;
        targetsAt = DateTime.UtcNow;
        var list = new List<FilterTarget>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = rules;
        var running = ProcessScanner.Snapshot();
        foreach (var rule in current)
        {
            var name = string.IsNullOrWhiteSpace(rule.Entry.Name) ? AppRule.TitleOf(rule.Entry.Path) : rule.Entry.Name;
            if (rule.StoreFamily != null) list.Add(new FilterTarget(name, null, rule.StoreFamily));
            foreach (var f in rule.CandidateFiles())
                if (rule.Matches(f, cfg.Exclusions) && paths.Add(f)) list.Add(new FilterTarget(name, f, null));
            foreach (var p in running)
                if (rule.Matches(p.Path, cfg.Exclusions) && paths.Add(p.Path)) list.Add(new FilterTarget(name, p.Path, null));
        }
        targets = list;
        knownPaths = paths;
        checkedPaths.Clear();
    }

    void WatchNewBinaries(AppConfig cfg, DateTime now)
    {
        if (now - lastProcessScan < ProcessScanEvery) return;
        lastProcessScan = now;
        foreach (var p in ProcessScanner.Snapshot())
        {
            if (knownPaths.Contains(p.Path) || !checkedPaths.Add(p.Path)) continue;
            if (rules.Any(r => r.Matches(p.Path, cfg.Exclusions)))
            {
                log.Write("new app binary: " + p.Path);
                targetsStale = true;
            }
        }
        if (checkedPaths.Count > 5000) checkedPaths.Clear();
    }

    async Task<Response> Handle(Request req)
    {
        await Task.Yield();
        switch (req.Cmd)
        {
            case Commands.Status:
                return new Response { Ok = true, Status = status };
            case Commands.GetConfig:
                lock (gate) return new Response { Ok = true, Config = config.Clone() };
            case Commands.SetConfig:
                if (req.Config == null) return Response.Fail("no config");
                var cfg = ConfigStore.Normalize(req.Config.Clone(), out var rejected);
                lock (gate)
                {
                    ConfigStore.Save(AppPaths.ConfigFile, cfg);
                    config = cfg;
                    rules = cfg.Apps.Select(AppRule.From).ToList();
                }
                foreach (var r in rejected) log.Write("rejected " + r);
                log.Write($"config saved, apps: {cfg.Apps.Count}, adapter: {cfg.Adapter?.Name ?? "-"}, strict: {cfg.StrictMode}");
                targetsStale = true;
                wake.Set();
                return new Response { Ok = true, Config = cfg.Clone(), Rejected = rejected };
            case Commands.SetEnabled:
                lock (gate)
                {
                    config.Enabled = req.Enabled ?? true;
                    ConfigStore.Save(AppPaths.ConfigFile, config);
                }
                log.Write(config.Enabled ? "protection on" : "protection off");
                wake.Set();
                return new Response { Ok = true };
            case Commands.ClearFilters:
                lock (gate)
                {
                    config.Enabled = false;
                    ConfigStore.Save(AppPaths.ConfigFile, config);
                    wfp.Close();
                    var n = WfpEngine.RemoveEverything();
                    appliedSignature = "";
                    log.Write($"all filters removed by user ({n})");
                }
                wake.Set();
                return new Response { Ok = true };
            case Commands.ReleaseStuck:
                var released = Freezer.ReleaseStuck(rules, config.Exclusions, force: true);
                log.Write($"release requested, released {released}");
                return new Response { Ok = true };
            default:
                return Response.Fail("unknown command");
        }
    }

    static void PrepareDataDir()
    {
        var dir = Directory.CreateDirectory(AppPaths.DataDir);
        Directory.CreateDirectory(AppPaths.ServiceLogDir);
        try
        {
            var sec = new DirectorySecurity();
            sec.SetAccessRuleProtection(true, false);
            var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
            dir.SetAccessControl(sec);
        }
        catch (Exception)
        {
        }
    }

    void RemoveLegacyFirewallRules()
    {
        var marker = Path.Combine(AppPaths.DataDir, "legacy-firewall-removed");
        if (File.Exists(marker)) return;
        foreach (var name in new[] { "VpnPause ChatGPT", "VpnPause Claude", "VpnPause Claude Code", "chatGPT V" })
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("netsh", $"advfirewall firewall delete rule name=\"{name}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                });
                p?.WaitForExit(5000);
                if (p?.ExitCode == 0) log.Write("removed old firewall rule: " + name);
            }
            catch (Exception)
            {
            }
        }
        File.WriteAllText(marker, DateTime.Now.ToString("s"));
    }
}
