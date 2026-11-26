using System.Text.Json;
using VpnPause.Core;

namespace VpnPause.App;

/// <summary>
/// Talks to the service and runs the optional freezing. Freezing lives here, in the user's session,
/// because it needs the desktop to tell which processes own windows.
/// </summary>
sealed class AppController : IDisposable
{
    static readonly TimeSpan LeaseEvery = TimeSpan.FromSeconds(5);

    readonly SynchronizationContext ui;
    readonly FreezePolicy policy = new();
    readonly AutoResetEvent wake = new(false);
    readonly NetworkWatch network = new();
    volatile bool running = true;
    volatile AppConfig snapshot = new();
    volatile IReadOnlyList<AppRule> rules = Array.Empty<AppRule>();
    UserPrefs prefs = UserPrefs.Load();
    Thread? freezeThread;
    bool? lastVpnUp;
    DateTime lastLease;

    public AppController()
    {
        ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
    }

    public RotatingLog Log { get; } = new(AppPaths.UserLogDir, "app");
    public Freezer Freezer { get; } = new();
    public AppConfig Config => snapshot;
    public ServiceStatus? Status { get; private set; }
    public bool ServiceOk { get; private set; }
    public string? ServiceError { get; private set; }
    public bool LocalVpnUp { get; private set; }
    public bool FreezeExpired => policy.Expired;
    public string? LegacyNote { get; private set; }

    public event Action? Changed;
    public event Action<bool>? VpnChanged;

    public async Task InitAsync()
    {
        await LoadConfigAsync();
        L.Lang = snapshot.Language;
        await MigrateLegacyAsync();
        var n = Freezer.ReleaseStuck(rules, snapshot.Exclusions);
        if (n > 0) Log.Write($"released {n} processes left frozen");
        freezeThread = new Thread(FreezeLoop) { IsBackground = true, Name = "VpnPause.Freeze" };
        freezeThread.Start();
        await RefreshStatusAsync();
    }

    async Task LoadConfigAsync()
    {
        var r = await ServiceClient.SendAsync(new Request { Cmd = Commands.GetConfig });
        var cfg = r.Ok && r.Config != null ? r.Config : ConfigStore.Load(AppPaths.ConfigFile);
        if (!UserPrefs.Exists)
        {
            prefs = new UserPrefs { Language = cfg.Language, Notifications = cfg.Notifications, FirstRunDone = cfg.FirstRunDone };
            prefs.Save();
        }
        SetSnapshot(cfg);
    }

    void SetSnapshot(AppConfig cfg)
    {
        cfg.Language = prefs.Language;
        cfg.Notifications = prefs.Notifications;
        cfg.FirstRunDone = prefs.FirstRunDone;
        snapshot = cfg;
        rules = cfg.Apps.Select(AppRule.From).ToList();
        wake.Set();
    }

    public async Task RefreshStatusAsync()
    {
        var r = await ServiceClient.SendAsync(new Request { Cmd = Commands.Status }, 2000);
        ServiceOk = r.Ok;
        ServiceError = r.Ok ? null : r.Error;
        Status = r.Status;
        if (r.Status != null && Freezer.HeldCount == 0) LocalVpnUp = r.Status.VpnUp;
        if (r.Status != null) NoteVpn(r.Status.VpnUp);
        Changed?.Invoke();
    }

    void NoteVpn(bool up)
    {
        if (lastVpnUp == up) return;
        if (lastVpnUp != null)
        {
            Log.Write(up ? "VPN up" : "VPN down");
            ui.Post(_ => VpnChanged?.Invoke(up), null);
        }
        lastVpnUp = up;
    }

    /// <summary>The part of the config the service acts on; window preferences are left out.</summary>
    static string ProtectionPart(AppConfig cfg)
    {
        var c = cfg.Clone();
        c.Language = "ru";
        c.Notifications = true;
        c.FirstRunDone = false;
        return JsonSerializer.Serialize(c, Json.Compact);
    }

    public async Task<Response> SaveConfigAsync(AppConfig cfg)
    {
        var langChanged = prefs.Language != cfg.Language;
        prefs.Language = cfg.Language;
        prefs.Notifications = cfg.Notifications;
        prefs.FirstRunDone = cfg.FirstRunDone;
        prefs.Save();
        if (langChanged) L.Lang = cfg.Language;

        if (ProtectionPart(cfg) == ProtectionPart(snapshot))
        {
            SetSnapshot(cfg.Clone());
            return new Response { Ok = true, Config = snapshot };
        }

        var normalized = ConfigStore.Normalize(cfg.Clone(), out var rejected);
        var r = await Elevated.SendAsync(new Request { Cmd = Commands.SetConfig, Config = normalized });
        if (r.Ok)
        {
            var fresh = await ServiceClient.SendAsync(new Request { Cmd = Commands.GetConfig });
            SetSnapshot(fresh.Ok && fresh.Config != null ? fresh.Config : normalized);
            Log.Write($"config saved, apps: {snapshot.Apps.Count}");
            r = new Response { Ok = true, Config = snapshot, Rejected = rejected };
        }
        await RefreshStatusAsync();
        return r;
    }

    public async Task<Response> SetEnabledAsync(bool on)
    {
        var r = await Elevated.SendAsync(new Request { Cmd = Commands.SetEnabled, Enabled = on });
        if (r.Ok)
        {
            var cfg = snapshot.Clone();
            cfg.Enabled = on;
            SetSnapshot(cfg);
            if (!on) ReleaseLocal();
            Log.Write(on ? "protection on" : "protection off");
        }
        await RefreshStatusAsync();
        return r;
    }

    public async Task ReleaseAllAsync()
    {
        ReleaseLocal();
        Freezer.ReleaseStuck(rules, snapshot.Exclusions);
        await ServiceClient.SendAsync(new Request { Cmd = Commands.ReleaseStuck });
        Log.Write("release all");
        Changed?.Invoke();
    }

    public async Task<Response> ClearFiltersAsync()
    {
        ReleaseLocal();
        var r = await Elevated.SendAsync(new Request { Cmd = Commands.ClearFilters });
        if (r.Ok)
        {
            var cfg = snapshot.Clone();
            cfg.Enabled = false;
            SetSnapshot(cfg);
        }
        await RefreshStatusAsync();
        return r;
    }

    void ReleaseLocal()
    {
        var n = Freezer.ReleaseAll();
        if (n > 0) Log.Write($"resumed {n} processes");
        SendLease(false);
    }

    void SendLease(bool hold)
    {
        try
        {
            ServiceClient.SendAsync(new Request { Cmd = Commands.FreezeLease, Enabled = hold }, 1500).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
        }
    }

    void FreezeLoop()
    {
        while (running)
        {
            var cfg = snapshot;
            var configured = cfg.Enabled && cfg.Freeze.Enabled && (cfg.AutoAdapter || cfg.Adapter != null) && cfg.Apps.Count > 0;
            if (!configured && Freezer.HeldCount == 0)
            {
                policy.Tick(false, true, DateTime.UtcNow, TimeSpan.Zero, TimeSpan.Zero);
                wake.WaitOne(2000);
                continue;
            }
            try
            {
                var st = network.Get(cfg);
                LocalVpnUp = st.Up;
                var wasExpired = policy.Expired;
                var action = policy.Tick(configured, st.Up, DateTime.UtcNow,
                    TimeSpan.FromSeconds(cfg.Freeze.DelaySeconds), TimeSpan.FromSeconds(cfg.Freeze.MaxSeconds));
                if (action == FreezeAction.Freeze)
                {
                    if (Freezer.HeldCount == 0 || DateTime.UtcNow - lastLease > LeaseEvery)
                    {
                        SendLease(true);
                        lastLease = DateTime.UtcNow;
                    }
                    var n = Freezer.Freeze(rules, cfg.Exclusions, cfg.Freeze.IncludeWindows);
                    if (n > 0)
                    {
                        Log.Write($"froze {n} processes");
                        ui.Post(_ => Changed?.Invoke(), null);
                    }
                }
                else if (action == FreezeAction.Release && Freezer.HeldCount > 0)
                {
                    var n = Freezer.ReleaseAll();
                    SendLease(false);
                    Log.Write(policy.Expired && !wasExpired ? $"freeze limit reached, resumed {n}" : $"resumed {n} processes");
                    ui.Post(_ => Changed?.Invoke(), null);
                }
            }
            catch (Exception e)
            {
                Log.Write("freeze loop: " + e.Message);
            }
            wake.WaitOne(1000);
        }
    }

    async Task MigrateLegacyAsync()
    {
        var imported = Legacy.TryImport(out var entries, out _);
        if (!imported) return;
        var cfg = snapshot.Clone();
        foreach (var e in entries)
            if (!cfg.Apps.Any(a => string.Equals(a.Path, e.Path, StringComparison.OrdinalIgnoreCase))) cfg.Apps.Add(e);
        var r = await SaveConfigAsync(cfg);
        if (r.Ok)
        {
            Legacy.Retire(Log);
            LegacyNote = L.T("wiz.legacy");
            Log.Write($"imported {entries.Count} apps from the previous version");
        }
    }

    public void Dispose()
    {
        running = false;
        wake.Set();
        freezeThread?.Join(2000);
        ReleaseLocal();
        network.Dispose();
    }
}
