using System.Net.NetworkInformation;
using VpnPause.Core;

namespace VpnPause.App;

/// <summary>
/// Talks to the service and runs the optional freezing. Freezing lives here, in the user's session,
/// because it needs the desktop to tell which processes own windows.
/// </summary>
sealed class AppController : IDisposable
{
    readonly SynchronizationContext ui;
    readonly FreezePolicy policy = new();
    readonly AutoResetEvent wake = new(false);
    volatile bool running = true;
    volatile AppConfig snapshot = new();
    volatile IReadOnlyList<AppRule> rules = Array.Empty<AppRule>();
    Thread? freezeThread;
    bool? lastVpnUp;

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
        var st = Network.Status(snapshot);
        LocalVpnUp = st.Up;
        if (st.Up)
        {
            var n = Freezer.ReleaseStuck(rules, snapshot.Exclusions);
            if (n > 0) Log.Write($"released {n} processes left frozen");
        }
        NetworkChange.NetworkAddressChanged += OnNetwork;
        freezeThread = new Thread(FreezeLoop) { IsBackground = true, Name = "VpnPause.Freeze" };
        freezeThread.Start();
        await RefreshStatusAsync();
    }

    void OnNetwork(object? s, EventArgs e) => wake.Set();

    async Task LoadConfigAsync()
    {
        var r = await ServiceClient.SendAsync(new Request { Cmd = Commands.GetConfig });
        if (r.Ok && r.Config != null) SetSnapshot(r.Config);
        else SetSnapshot(ConfigStore.Load(AppPaths.ConfigFile));
    }

    void SetSnapshot(AppConfig cfg)
    {
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
        Changed?.Invoke();
    }

    public async Task<Response> SaveConfigAsync(AppConfig cfg)
    {
        var r = await ServiceClient.SendAsync(new Request { Cmd = Commands.SetConfig, Config = cfg }, 10000);
        if (r.Ok && r.Config != null)
        {
            SetSnapshot(r.Config);
            L.Lang = r.Config.Language;
            Log.Write($"config saved, apps: {r.Config.Apps.Count}");
        }
        await RefreshStatusAsync();
        return r;
    }

    public async Task<Response> SetEnabledAsync(bool on)
    {
        var r = await ServiceClient.SendAsync(new Request { Cmd = Commands.SetEnabled, Enabled = on });
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
        Freezer.ReleaseStuck(rules, snapshot.Exclusions, force: true);
        await ServiceClient.SendAsync(new Request { Cmd = Commands.ReleaseStuck });
        Log.Write("release all");
        Changed?.Invoke();
    }

    public async Task<Response> ClearFiltersAsync()
    {
        ReleaseLocal();
        var r = await ServiceClient.SendAsync(new Request { Cmd = Commands.ClearFilters }, 10000);
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
    }

    void FreezeLoop()
    {
        while (running)
        {
            try
            {
                var cfg = snapshot;
                var st = Network.Status(cfg);
                LocalVpnUp = st.Up;
                if (lastVpnUp != st.Up)
                {
                    if (lastVpnUp != null)
                    {
                        Log.Write(st.Up ? "VPN up" : "VPN down");
                        var up = st.Up;
                        ui.Post(_ => VpnChanged?.Invoke(up), null);
                    }
                    lastVpnUp = st.Up;
                }

                var active = cfg.Enabled && cfg.Freeze.Enabled && (cfg.AutoAdapter || cfg.Adapter != null) && cfg.Apps.Count > 0;
                var wasExpired = policy.Expired;
                var action = policy.Tick(active, st.Up, DateTime.UtcNow,
                    TimeSpan.FromSeconds(cfg.Freeze.DelaySeconds), TimeSpan.FromSeconds(cfg.Freeze.MaxSeconds));
                if (action == FreezeAction.Freeze)
                {
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
                    Log.Write(policy.Expired && !wasExpired ? $"freeze limit reached, resumed {n}" : $"resumed {n} processes");
                    ui.Post(_ => Changed?.Invoke(), null);
                }
            }
            catch (Exception e)
            {
                Log.Write("freeze loop: " + e.Message);
            }
            wake.WaitOne(500);
        }
    }

    async Task MigrateLegacyAsync()
    {
        var imported = Legacy.TryImport(out var entries, out var adapterName);
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
        NetworkChange.NetworkAddressChanged -= OnNetwork;
        freezeThread?.Join(2000);
        ReleaseLocal();
    }
}
