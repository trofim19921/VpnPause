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
    static readonly TimeSpan TargetsTtl = TimeSpan.FromMinutes(5);
    static readonly TimeSpan RetryAfterError = TimeSpan.FromSeconds(5);
    static readonly TimeSpan ProcessScanEvery = TimeSpan.FromSeconds(2);
    static readonly TimeSpan VerifyEvery = TimeSpan.FromSeconds(5);
    static readonly TimeSpan FullVerifyEvery = TimeSpan.FromSeconds(60);
    static readonly TimeSpan StuckCheckEvery = TimeSpan.FromSeconds(15);
    static readonly TimeSpan LeaseLength = TimeSpan.FromSeconds(15);
    const int MaxFiles = 2000;

    readonly RotatingLog log = new(AppPaths.ServiceLogDir, "service");
    readonly WfpEngine wfp = new();
    readonly NetworkWatch network = new();
    readonly AutoResetEvent wake = new(false);
    readonly object gate = new();
    readonly CancellationTokenSource stop = new();

    AppConfig config = new();
    string? configError;
    bool holdFilters;
    volatile IReadOnlyList<AppRule> rules = Array.Empty<AppRule>();
    volatile bool targetsStale = true;
    volatile bool watchersStale = true;
    List<FilterTarget> targets = new();
    HashSet<string> knownPaths = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<int, long> seenPids = new();
    readonly List<FileSystemWatcher> watchers = new();
    DateTime targetsAt;
    DateTime lastProcessScan;
    DateTime lastVerify;
    DateTime lastFullVerify;
    string appliedSignature = "";
    DateTime lastFailure = DateTime.MinValue;
    DateTime lastStuckCheck = DateTime.MinValue;
    DateTime leaseUntil = DateTime.MinValue;
    DateTime? leaseSince;
    ServiceStatus status = new();
    Thread? loop;
    PipeServer? pipe;

    public static string Version => Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0";

    public RotatingLog Log => log;

    public void Start()
    {
        PrepareDataDir(log);
        lock (gate)
        {
            config = ConfigStore.Load(AppPaths.ConfigFile, out configError, out holdFilters);
            rules = config.Apps.Select(AppRule.From).ToList();
        }
        log.Write($"service start {Version}, apps: {config.Apps.Count}, strict: {config.StrictMode}");
        if (configError != null) log.Write("CONFIG: " + configError + (holdFilters ? "; existing filters are kept" : ""));
        RemoveLegacyFirewallRules();

        network.Changed += OnNetworkChanged;
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
        network.Changed -= OnNetworkChanged;
        network.Dispose();
        loop?.Join(5000);
        DisposeWatchers();
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

    void OnNetworkChanged() => wake.Set();

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
            wake.WaitOne(2000);
        }
    }

    void Tick()
    {
        AppConfig cfg;
        lock (gate) cfg = config;

        var vpn = network.Get(cfg);
        var now = DateTime.UtcNow;

        if (watchersStale) RebuildWatchers();
        if (!targetsStale) WatchNewProcesses(cfg, now);
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
        if (!holdFilters && signature != appliedSignature && now - lastFailure > RetryAfterError)
        {
            try
            {
                lock (gate) wfp.Apply(plan, log.Write);
                appliedSignature = signature;
                lastVerify = lastFullVerify = now;
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
        else if (appliedSignature.Length > 0 && now - lastVerify > VerifyEvery)
        {
            VerifyFilters(now);
        }

        ReleaseStuckIfOrphaned(cfg, now);

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
            ConfigError = configError,
            AppliedAt = DateTime.Now
        };
    }

    /// <summary>Filters can vanish behind our back: a BFE restart, or another tool wiping WFP state.</summary>
    void VerifyFilters(DateTime now)
    {
        lastVerify = now;
        bool full = now - lastFullVerify > FullVerifyEvery;
        if (full) lastFullVerify = now;
        bool ok;
        try
        {
            lock (gate) ok = wfp.Verify(full);
        }
        catch (Exception)
        {
            ok = false;
        }
        if (ok) return;
        log.Write("filters are missing from Windows, restoring them");
        lock (gate) wfp.Close();
        appliedSignature = "";
        wake.Set();
    }

    /// <summary>
    /// The app freezes processes only while it holds a lease. Without a live lease, or once the freeze has
    /// lasted longer than the configured limit, fully suspended app processes are resumed here, whatever
    /// the VPN state: a crashed or closed app must never leave programs frozen.
    /// </summary>
    void ReleaseStuckIfOrphaned(AppConfig cfg, DateTime now)
    {
        if (now - lastStuckCheck < StuckCheckEvery) return;
        bool leased = now < leaseUntil && leaseSince is { } since && now - since < TimeSpan.FromSeconds(cfg.Freeze.MaxSeconds) + LeaseLength;
        if (leased) return;
        if (!cfg.Freeze.Enabled && lastStuckCheck != DateTime.MinValue) return;
        lastStuckCheck = now;
        leaseSince = null;
        var released = Freezer.ReleaseStuck(rules, cfg.Exclusions);
        if (released > 0) log.Write($"released {released} app processes left frozen");
    }

    void WatchNewProcesses(AppConfig cfg, DateTime now)
    {
        if (now - lastProcessScan < ProcessScanEvery) return;
        lastProcessScan = now;
        var alive = ProcessScanner.Pids();
        var current = new HashSet<int>(alive);
        foreach (var gone in seenPids.Keys.Where(p => !current.Contains(p)).ToList()) seenPids.Remove(gone);
        foreach (var pid in alive)
        {
            if (seenPids.ContainsKey(pid)) continue;
            var (path, start) = ProcessScanner.Query(pid);
            seenPids[pid] = start;
            if (path == null || knownPaths.Contains(path)) continue;
            if (rules.Any(r => r.Matches(path, cfg.Exclusions)))
            {
                log.Write("new app binary: " + path);
                targetsStale = true;
            }
        }
    }

    void RebuildWatchers()
    {
        watchersStale = false;
        DisposeWatchers();
        foreach (var root in rules.Select(r => r.WatchRoot).Where(r => r != null).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                var w = new FileSystemWatcher(root!, "*.exe")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                    InternalBufferSize = 64 * 1024
                };
                w.Created += OnExeAppeared;
                w.Renamed += OnExeAppeared;
                w.Error += (_, _) => { targetsStale = true; wake.Set(); };
                w.EnableRaisingEvents = true;
                watchers.Add(w);
            }
            catch (Exception e)
            {
                log.Write($"cannot watch {root}: {e.Message}");
            }
        }
    }

    void OnExeAppeared(object sender, FileSystemEventArgs e)
    {
        if (knownPaths.Contains(e.FullPath)) return;
        targetsStale = true;
        wake.Set();
    }

    void DisposeWatchers()
    {
        foreach (var w in watchers) w.Dispose();
        watchers.Clear();
    }

    void RebuildTargets(AppConfig cfg)
    {
        targetsStale = false;
        targetsAt = DateTime.UtcNow;
        var list = new List<FilterTarget>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = rules;
        var running = ProcessScanner.Snapshot();
        bool capped = false;
        foreach (var rule in current)
        {
            var name = string.IsNullOrWhiteSpace(rule.Entry.Name) ? AppRule.TitleOf(rule.Entry.Path) : rule.Entry.Name;
            if (rule.StoreFamily != null) list.Add(new FilterTarget(name, null, rule.StoreFamily));
            foreach (var p in running)
                if (rule.Matches(p.Path, cfg.Exclusions) && paths.Add(p.Path)) list.Add(new FilterTarget(name, p.Path, null));
            foreach (var f in rule.CandidateFiles())
            {
                if (paths.Count >= MaxFiles)
                {
                    capped = true;
                    break;
                }
                if (rule.Matches(f, cfg.Exclusions) && paths.Add(f)) list.Add(new FilterTarget(name, f, null));
            }
        }
        if (capped) log.Write($"more than {MaxFiles} executables in the listed folders; only running ones and the first {MaxFiles} are pre-filtered");
        targets = list;
        knownPaths = paths;
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
                    configError = null;
                    holdFilters = false;
                    rules = cfg.Apps.Select(AppRule.From).ToList();
                }
                foreach (var r in rejected) log.Write("rejected " + r);
                log.Write($"config saved, apps: {cfg.Apps.Count}, adapter: {cfg.Adapter?.Name ?? "-"}, strict: {cfg.StrictMode}");
                targetsStale = true;
                watchersStale = true;
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
                    holdFilters = false;
                    log.Write($"all filters removed by user ({n})");
                }
                wake.Set();
                return new Response { Ok = true };
            case Commands.ReleaseStuck:
                var released = Freezer.ReleaseStuck(rules, config.Exclusions);
                log.Write($"release requested, released {released}");
                return new Response { Ok = true };
            case Commands.FreezeLease:
                var now = DateTime.UtcNow;
                if (req.Enabled == false)
                {
                    leaseUntil = DateTime.MinValue;
                    leaseSince = null;
                }
                else
                {
                    if (now >= leaseUntil) leaseSince = now;
                    leaseUntil = now + LeaseLength;
                }
                return new Response { Ok = true };
            default:
                return Response.Fail("unknown command");
        }
    }

    /// <summary>
    /// The data folder must belong to administrators. A folder pre-created by a regular user stays theirs
    /// (owners can rewrite the ACL), which would let them plant links under files SYSTEM writes. Such a folder is moved aside.
    /// </summary>
    static void PrepareDataDir(RotatingLog log)
    {
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var path = AppPaths.DataDir;
        string? note = null;
        try
        {
            if (Directory.Exists(path))
            {
                var info = new DirectoryInfo(path);
                var owner = info.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
                bool trusted = (info.Attributes & FileAttributes.ReparsePoint) == 0 && (owner == admins || owner == system);
                if (!trusted)
                {
                    var aside = path + ".untrusted-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    Directory.Move(path, aside);
                    note = $"data folder was not owned by administrators, moved to {aside}";
                }
            }
            var dir = Directory.CreateDirectory(path);
            var sec = new DirectorySecurity();
            sec.SetOwner(admins);
            sec.SetAccessRuleProtection(true, false);
            var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            sec.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            sec.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
            dir.SetAccessControl(sec);
            var logs = new DirectoryInfo(AppPaths.ServiceLogDir);
            if (logs.Exists && (logs.Attributes & FileAttributes.ReparsePoint) != 0) logs.Delete();
            Directory.CreateDirectory(AppPaths.ServiceLogDir);
        }
        catch (Exception e)
        {
            note = (note == null ? "" : note + "; ") + "data folder setup: " + e.Message;
        }
        if (note != null) log.Write(note);
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
