using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;

namespace VpnPause.Service.Wfp;

public sealed record FilterTarget(string Name, string? Path, string? PackageFamily);

public sealed class FilterPlan
{
    public bool Enabled { get; init; }
    public bool Strict { get; init; }
    public bool AllowLan { get; init; }
    public IReadOnlyList<ulong> VpnLuids { get; init; } = Array.Empty<ulong>();
    public IReadOnlyList<FilterTarget> Targets { get; init; } = Array.Empty<FilterTarget>();

    public string Signature() =>
        $"{Enabled}|{Strict}|{AllowLan}|{string.Join(",", VpnLuids.OrderBy(l => l))}|" +
        string.Join(";", Targets.Select(t => t.Path ?? "pkg:" + t.PackageFamily).OrderBy(s => s, StringComparer.OrdinalIgnoreCase));
}

public sealed class WfpException : Exception
{
    public WfpException(string what, uint code) : base($"{what}: 0x{code:X8} {new Win32Exception((int)code).Message}") => Code = code;

    public uint Code { get; }
}

/// <summary>
/// Owns the VpnPause provider, sublayer and filters. For every target app: permit loopback, permit the VPN
/// interface, optionally permit private ranges, and block everything else. Every apply rebuilds all
/// objects in one transaction, so there is never a moment with half a rule set.
/// </summary>
public sealed unsafe class WfpEngine : IDisposable
{
    public static readonly Guid ProviderKey = new("6f0e4a6c-5f2e-4c38-9a53-1f6f1d7b3a01");
    public static readonly Guid SubLayerKey = new("6f0e4a6c-5f2e-4c38-9a53-1f6f1d7b3a02");

    static readonly Guid[] Layers = { FwpConst.LayerConnectV4, FwpConst.LayerRecvAcceptV4, FwpConst.LayerConnectV6, FwpConst.LayerRecvAcceptV6 };

    static readonly (uint addr, uint mask)[] LanV4 =
    {
        (0x0A000000, 0xFF000000),
        (0xAC100000, 0xFFF00000),
        (0xC0A80000, 0xFFFF0000),
        (0xA9FE0000, 0xFFFF0000),
        (0xE0000000, 0xF0000000)
    };

    static readonly (string prefix, byte len)[] LanV6 = { ("fe80::", 10), ("fc00::", 7), ("ff00::", 8) };

    IntPtr engine;
    bool strictSession;

    public int FilterCount { get; private set; }

    public bool IsOpen => engine != IntPtr.Zero;

    public void Open(bool strict)
    {
        if (IsOpen && strictSession == strict) return;
        Close();
        var session = new FWPM_SESSION0 { flags = strict ? 0 : FwpConst.FWPM_SESSION_FLAG_DYNAMIC, txnWaitTimeoutInMSec = 10000 };
        var name = Marshal.StringToHGlobalUni("VpnPause");
        try
        {
            session.displayData.name = name;
            Check(FwpNative.FwpmEngineOpen0(null, FwpConst.RPC_C_AUTHN_WINNT, IntPtr.Zero, &session, out engine), "FwpmEngineOpen0");
        }
        finally
        {
            Marshal.FreeHGlobal(name);
        }
        strictSession = strict;
    }

    public void Close()
    {
        if (engine != IntPtr.Zero)
        {
            FwpNative.FwpmEngineClose0(engine);
            engine = IntPtr.Zero;
        }
        FilterCount = 0;
    }

    public void Dispose() => Close();

    /// <summary>Replaces everything VpnPause owns with the given plan.</summary>
    public void Apply(FilterPlan plan, Action<string>? log = null)
    {
        Open(plan.Strict);
        using var arena = new Arena();
        Check(FwpNative.FwpmTransactionBegin0(engine, 0), "FwpmTransactionBegin0");
        int added = 0;
        try
        {
            RemoveOwned();
            if (plan.Enabled && plan.Targets.Count > 0)
            {
                AddProviderAndSublayer(plan.Strict, arena);
                foreach (var target in plan.Targets)
                {
                    var app = AppCondition(target, arena, log);
                    if (app == null) continue;
                    foreach (var layer in Layers)
                        added += AddTargetFilters(layer, app.Value, plan, arena, target.Name);
                }
            }
            Check(FwpNative.FwpmTransactionCommit0(engine), "FwpmTransactionCommit0");
            FilterCount = added;
        }
        catch
        {
            FwpNative.FwpmTransactionAbort0(engine);
            throw;
        }
    }

    /// <summary>Deletes every VpnPause filter, sublayer and provider, including persistent ones.</summary>
    public static int RemoveEverything()
    {
        using var e = new WfpEngine();
        e.Open(strict: true);
        Check(FwpNative.FwpmTransactionBegin0(e.engine, 0), "FwpmTransactionBegin0");
        try
        {
            var n = e.RemoveOwned();
            Check(FwpNative.FwpmTransactionCommit0(e.engine), "FwpmTransactionCommit0");
            return n;
        }
        catch
        {
            FwpNative.FwpmTransactionAbort0(e.engine);
            throw;
        }
    }

    /// <summary>Human-readable list of VpnPause filters, for the self-test.</summary>
    public List<string> Describe()
    {
        var lines = new List<string>();
        if (!IsOpen) return lines;
        Check(FwpNative.FwpmFilterCreateEnumHandle0(engine, IntPtr.Zero, out var handle), "FwpmFilterCreateEnumHandle0");
        try
        {
            while (true)
            {
                Check(FwpNative.FwpmFilterEnum0(engine, handle, 512, out var entries, out var count), "FwpmFilterEnum0");
                try
                {
                    for (int i = 0; i < count; i++)
                    {
                        var f = (FWPM_FILTER0*)Marshal.ReadIntPtr(entries, i * IntPtr.Size);
                        if (f->subLayerKey != SubLayerKey) continue;
                        var name = Marshal.PtrToStringUni(f->displayData.name);
                        var parts = new List<string>();
                        for (int k = 0; k < f->numFilterConditions; k++)
                        {
                            var cnd = ((FWPM_FILTER_CONDITION0*)f->filterCondition)[k];
                            if (cnd.fieldKey == FwpConst.CondAppId && cnd.conditionValue.pointer != IntPtr.Zero)
                            {
                                var blob = *(FWP_BYTE_BLOB*)cnd.conditionValue.pointer;
                                parts.Add("app=" + Marshal.PtrToStringUni(blob.data, (int)blob.size / 2).TrimEnd('\0'));
                            }
                            else parts.Add($"{cnd.fieldKey}:{cnd.matchType}:{cnd.conditionValue.type}");
                        }
                        lines.Add($"{f->filterId} layer={f->layerKey} action=0x{f->actionType:X} flags=0x{f->flags:X} w={f->effectiveWeight.type}/{f->weight.uint8} {name} [{string.Join(" ", parts)}]");
                    }
                }
                finally
                {
                    if (entries != IntPtr.Zero) FwpNative.FwpmFreeMemory0(ref entries);
                }
                if (count < 512) break;
            }
        }
        finally
        {
            FwpNative.FwpmFilterDestroyEnumHandle0(engine, handle);
        }
        return lines;
    }

    int RemoveOwned()
    {
        var ids = new List<ulong>();
        Check(FwpNative.FwpmFilterCreateEnumHandle0(engine, IntPtr.Zero, out var handle), "FwpmFilterCreateEnumHandle0");
        try
        {
            while (true)
            {
                Check(FwpNative.FwpmFilterEnum0(engine, handle, 512, out var entries, out var count), "FwpmFilterEnum0");
                try
                {
                    for (int i = 0; i < count; i++)
                    {
                        var f = (FWPM_FILTER0*)Marshal.ReadIntPtr(entries, i * IntPtr.Size);
                        if (f->subLayerKey == SubLayerKey) ids.Add(f->filterId);
                    }
                }
                finally
                {
                    if (entries != IntPtr.Zero) FwpNative.FwpmFreeMemory0(ref entries);
                }
                if (count < 512) break;
            }
        }
        finally
        {
            FwpNative.FwpmFilterDestroyEnumHandle0(engine, handle);
        }
        foreach (var id in ids)
        {
            var rc = FwpNative.FwpmFilterDeleteById0(engine, id);
            if (rc != 0 && rc != FwpConst.FWP_E_FILTER_NOT_FOUND) Check(rc, "FwpmFilterDeleteById0");
        }
        var sub = SubLayerKey;
        var rcs = FwpNative.FwpmSubLayerDeleteByKey0(engine, &sub);
        if (rcs != 0 && rcs != FwpConst.FWP_E_SUBLAYER_NOT_FOUND) Check(rcs, "FwpmSubLayerDeleteByKey0");
        var prov = ProviderKey;
        var rcp = FwpNative.FwpmProviderDeleteByKey0(engine, &prov);
        if (rcp != 0 && rcp != FwpConst.FWP_E_PROVIDER_NOT_FOUND && rcp != FwpConst.FWP_E_IN_USE) Check(rcp, "FwpmProviderDeleteByKey0");
        return ids.Count;
    }

    void AddProviderAndSublayer(bool persistent, Arena arena)
    {
        var provider = new FWPM_PROVIDER0
        {
            providerKey = ProviderKey,
            flags = persistent ? FwpConst.FWPM_PROVIDER_FLAG_PERSISTENT : 0
        };
        provider.displayData.name = arena.Str("VpnPause");
        provider.displayData.description = arena.Str("Per-app VPN kill switch");
        Check(FwpNative.FwpmProviderAdd0(engine, &provider, IntPtr.Zero), "FwpmProviderAdd0", FwpConst.FWP_E_ALREADY_EXISTS);

        var providerKey = arena.Struct(ProviderKey);
        var sub = new FWPM_SUBLAYER0
        {
            subLayerKey = SubLayerKey,
            flags = persistent ? FwpConst.FWPM_SUBLAYER_FLAG_PERSISTENT : 0,
            providerKey = providerKey,
            weight = 0xFFFF
        };
        sub.displayData.name = arena.Str("VpnPause");
        sub.displayData.description = arena.Str("Apps may reach the internet only through the VPN adapter");
        Check(FwpNative.FwpmSubLayerAdd0(engine, &sub, IntPtr.Zero), "FwpmSubLayerAdd0", FwpConst.FWP_E_ALREADY_EXISTS);
    }

    static FWPM_FILTER_CONDITION0? AppCondition(FilterTarget target, Arena arena, Action<string>? log)
    {
        if (target.Path != null)
        {
            var id = AppId(target.Path);
            if (id == null)
            {
                log?.Invoke($"skip {target.Path}: no app id");
                return null;
            }
            var bytes = System.Text.Encoding.Unicode.GetBytes(id + "\0");
            var data = arena.Alloc(bytes.Length);
            Marshal.Copy(bytes, 0, data, bytes.Length);
            var blob = arena.Struct(new FWP_BYTE_BLOB { size = (uint)bytes.Length, data = data });
            return new FWPM_FILTER_CONDITION0
            {
                fieldKey = FwpConst.CondAppId,
                matchType = FwpConst.FWP_MATCH_EQUAL,
                conditionValue = new FWP_VALUE0 { type = FwpConst.FWP_BYTE_BLOB_TYPE, pointer = blob }
            };
        }
        if (target.PackageFamily != null)
        {
            if (FwpNative.DeriveAppContainerSidFromAppContainerName(target.PackageFamily.ToLowerInvariant(), out var sid) != 0 || sid == IntPtr.Zero)
            {
                log?.Invoke($"skip package {target.PackageFamily}: no SID");
                return null;
            }
            arena.OnDispose(() => FwpNative.FreeSid(sid));
            return new FWPM_FILTER_CONDITION0
            {
                fieldKey = FwpConst.CondPackageId,
                matchType = FwpConst.FWP_MATCH_EQUAL,
                conditionValue = new FWP_VALUE0 { type = FwpConst.FWP_SID, pointer = sid }
            };
        }
        return null;
    }

    int AddTargetFilters(Guid layer, FWPM_FILTER_CONDITION0 app, FilterPlan plan, Arena arena, string name)
    {
        bool v6 = layer == FwpConst.LayerConnectV6 || layer == FwpConst.LayerRecvAcceptV6;
        int n = 0;

        var loopback = new FWPM_FILTER_CONDITION0
        {
            fieldKey = FwpConst.CondFlags,
            matchType = FwpConst.FWP_MATCH_FLAGS_ALL_SET,
            conditionValue = new FWP_VALUE0 { type = FwpConst.FWP_UINT32, uint32 = FwpConst.FWP_CONDITION_FLAG_IS_LOOPBACK }
        };
        AddFilter(layer, FwpConst.FWP_ACTION_PERMIT, 15, plan.Strict, arena, $"VpnPause permit loopback: {name}", app, loopback);
        n++;

        foreach (var luid in plan.VpnLuids)
        {
            var luidPtr = arena.Struct(luid);
            var iface = new FWPM_FILTER_CONDITION0
            {
                fieldKey = FwpConst.CondLocalInterface,
                matchType = FwpConst.FWP_MATCH_EQUAL,
                conditionValue = new FWP_VALUE0 { type = FwpConst.FWP_UINT64, pointer = luidPtr }
            };
            AddFilter(layer, FwpConst.FWP_ACTION_PERMIT, 14, plan.Strict, arena, $"VpnPause permit VPN: {name}", app, iface);
            n++;
        }

        if (plan.AllowLan)
        {
            if (!v6)
            {
                foreach (var (addr, mask) in LanV4)
                {
                    var p = arena.Struct(new FWP_V4_ADDR_AND_MASK { addr = addr, mask = mask });
                    var c = new FWPM_FILTER_CONDITION0
                    {
                        fieldKey = FwpConst.CondRemoteAddress,
                        matchType = FwpConst.FWP_MATCH_EQUAL,
                        conditionValue = new FWP_VALUE0 { type = FwpConst.FWP_V4_ADDR_MASK, pointer = p }
                    };
                    AddFilter(layer, FwpConst.FWP_ACTION_PERMIT, 13, plan.Strict, arena, $"VpnPause permit LAN: {name}", app, c);
                    n++;
                }
            }
            else
            {
                foreach (var (prefix, len) in LanV6)
                {
                    var m = new FWP_V6_ADDR_AND_MASK { prefixLength = len };
                    var bytes = IPAddress.Parse(prefix).GetAddressBytes();
                    for (int i = 0; i < 16; i++) m.addr[i] = bytes[i];
                    var p = arena.Struct(m);
                    var c = new FWPM_FILTER_CONDITION0
                    {
                        fieldKey = FwpConst.CondRemoteAddress,
                        matchType = FwpConst.FWP_MATCH_EQUAL,
                        conditionValue = new FWP_VALUE0 { type = FwpConst.FWP_V6_ADDR_MASK, pointer = p }
                    };
                    AddFilter(layer, FwpConst.FWP_ACTION_PERMIT, 13, plan.Strict, arena, $"VpnPause permit LAN: {name}", app, c);
                    n++;
                }
            }
        }

        AddFilter(layer, FwpConst.FWP_ACTION_BLOCK, 0, plan.Strict, arena, $"VpnPause block: {name}", app);
        n++;
        return n;
    }

    void AddFilter(Guid layer, uint action, byte weight, bool persistent, Arena arena, string name, params FWPM_FILTER_CONDITION0[] conditions)
    {
        var conds = arena.Alloc(sizeof(FWPM_FILTER_CONDITION0) * conditions.Length);
        for (int i = 0; i < conditions.Length; i++)
            ((FWPM_FILTER_CONDITION0*)conds)[i] = conditions[i];

        var filter = new FWPM_FILTER0
        {
            filterKey = Guid.NewGuid(),
            flags = (persistent ? FwpConst.FWPM_FILTER_FLAG_PERSISTENT : 0) |
                    (action == FwpConst.FWP_ACTION_BLOCK ? FwpConst.FWPM_FILTER_FLAG_CLEAR_ACTION_RIGHT : 0),
            providerKey = arena.Struct(ProviderKey),
            layerKey = layer,
            subLayerKey = SubLayerKey,
            weight = new FWP_VALUE0 { type = FwpConst.FWP_UINT8, uint8 = weight },
            numFilterConditions = (uint)conditions.Length,
            filterCondition = conds,
            actionType = action
        };
        filter.displayData.name = arena.Str(name.Length > 200 ? name[..200] : name);
        Check(FwpNative.FwpmFilterAdd0(engine, &filter, IntPtr.Zero, out _), "FwpmFilterAdd0");
    }

    /// <summary>
    /// NT device path of the image, fully lower-cased. FwpmGetAppIdFromFileName0 lower-cases only ASCII,
    /// so for paths like C:\Users\Илья\… its result never matches what the kernel reports.
    /// </summary>
    public static string? AppId(string path)
    {
        if (FwpNative.FwpmGetAppIdFromFileName0(LongPath(path), out var blobPtr) != 0 || blobPtr == IntPtr.Zero) return null;
        try
        {
            var blob = *(FWP_BYTE_BLOB*)blobPtr;
            return Marshal.PtrToStringUni(blob.data, (int)blob.size / 2).TrimEnd('\0').ToLowerInvariant();
        }
        finally
        {
            FwpNative.FwpmFreeMemory0(ref blobPtr);
        }
    }

    /// <summary>App IDs are compared against the long path of the image; 8.3 short names never match.</summary>
    public static string LongPath(string path)
    {
        var full = Path.GetFullPath(path);
        var buf = new char[1024];
        var n = FwpNative.GetLongPathNameW(full, buf, buf.Length);
        return n > 0 && n < buf.Length ? new string(buf, 0, (int)n) : full;
    }

    static void Check(uint rc, string what, uint allowed = 0)
    {
        if (rc != 0 && rc != allowed) throw new WfpException(what, rc);
    }

    sealed class Arena : IDisposable
    {
        readonly List<IntPtr> blocks = new();
        readonly List<Action> cleanups = new();

        public IntPtr Alloc(int size)
        {
            var p = Marshal.AllocHGlobal(size);
            new Span<byte>((void*)p, size).Clear();
            blocks.Add(p);
            return p;
        }

        public IntPtr Str(string s)
        {
            var p = Marshal.StringToHGlobalUni(s);
            blocks.Add(p);
            return p;
        }

        public IntPtr Struct<T>(T value) where T : unmanaged
        {
            var p = Alloc(sizeof(T));
            *(T*)p = value;
            return p;
        }

        public void OnDispose(Action a) => cleanups.Add(a);

        public void Dispose()
        {
            foreach (var a in cleanups) a();
            foreach (var p in blocks) Marshal.FreeHGlobal(p);
        }
    }
}
