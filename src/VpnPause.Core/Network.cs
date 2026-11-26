using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace VpnPause.Core;

public sealed record AdapterInfo(
    string Id,
    string Name,
    string Description,
    bool Up,
    bool LooksLikeVpn,
    ulong Luid,
    uint IfIndex,
    bool HasDns)
{
    public AdapterRef ToRef() => new() { Id = Id, Name = Name, Description = Description };

    public override string ToString() => string.IsNullOrEmpty(Description) || Description == Name ? Name : $"{Name} — {Description}";
}

public sealed record VpnStatus(bool AdapterFound, bool Up, AdapterInfo? Adapter, bool DnsOutside, IReadOnlyList<ulong> Luids)
{
    public bool Configured { get; init; } = true;
}

public static class Network
{
    static readonly string[] VpnHints = { "wintun", "wireguard", "amnezia", "tap-windows", "tap-", "openvpn", "outline", "tun", "vpn", "xray", "sing-box", "hiddify", "v2ray", "nekoray", "zerotier", "tailscale" };
    static readonly string[] NotVpn = { "hyper-v", "vmware", "virtualbox", "loopback", "bluetooth", "wi-fi direct", "kernel debug", "teredo", "isatap", "6to4", "wan miniport" };
    static readonly IPAddress[] Probes = { IPAddress.Parse("1.1.1.1"), IPAddress.Parse("9.9.9.9") };

    public static IReadOnlyList<AdapterInfo> Adapters()
    {
        var list = new List<AdapterInfo>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (!Guid.TryParse(ni.Id, out var guid)) continue;
            ulong luid = 0;
            uint index = 0;
            if (ConvertInterfaceGuidToLuid(ref guid, out luid) != 0) continue;
            ConvertInterfaceLuidToIndex(ref luid, out index);
            bool dns = false;
            try
            {
                dns = ni.GetIPProperties().DnsAddresses.Any(a => !IPAddress.IsLoopback(a) && !a.IsIPv6SiteLocal);
            }
            catch (NetworkInformationException)
            {
            }
            list.Add(new AdapterInfo(ni.Id, ni.Name, ni.Description, ni.OperationalStatus == OperationalStatus.Up, LooksLikeVpn(ni), luid, index, dns));
        }
        return list;
    }

    public static bool LooksLikeVpn(NetworkInterface ni)
    {
        var text = (ni.Name + " " + ni.Description).ToLowerInvariant();
        if (NotVpn.Any(text.Contains)) return false;
        if (ni.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp) return true;
        return VpnHints.Any(text.Contains);
    }

    public static AdapterInfo? Find(AdapterRef? r, IReadOnlyList<AdapterInfo>? all = null)
    {
        if (r == null) return null;
        all ??= Adapters();
        return all.FirstOrDefault(a => string.Equals(a.Id, r.Id, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(a => !string.IsNullOrEmpty(r.Name) && string.Equals(a.Name, r.Name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The VPN counts as up when the chosen adapter is up and Windows would send internet traffic through it.
    /// Works for both full-tunnel styles: 0.0.0.0/0 on the adapter and the 0.0.0.0/1 + 128.0.0.0/1 pair.
    /// </summary>
    public static VpnStatus Status(AppConfig cfg) => cfg.AutoAdapter ? AutoStatus() : Status(cfg.Adapter);

    public static VpnStatus Status(AdapterRef? r)
    {
        var all = Adapters();
        var a = Find(r, all);
        if (a == null) return new VpnStatus(false, false, null, false, Array.Empty<ulong>()) { Configured = r != null };
        var best = BestIndexes();
        bool routed = a.Up && best.Contains(a.IfIndex);
        return new VpnStatus(true, routed, a, routed && DnsOutside(a, all), new[] { a.Luid });
    }

    /// <summary>Any adapter that looks like a VPN counts. Covers clients that switch adapters between protocols.</summary>
    public static VpnStatus AutoStatus()
    {
        var all = Adapters();
        var vpns = all.Where(a => a.LooksLikeVpn).ToList();
        if (vpns.Count == 0) return new VpnStatus(false, false, null, false, Array.Empty<ulong>());
        var best = BestIndexes();
        var routed = vpns.FirstOrDefault(a => a.Up && best.Contains(a.IfIndex));
        var shown = routed ?? vpns.FirstOrDefault(a => a.Up) ?? vpns[0];
        return new VpnStatus(true, routed != null, shown, routed != null && DnsOutside(routed, all), vpns.Select(a => a.Luid).ToList());
    }

    static HashSet<uint> BestIndexes()
    {
        var set = new HashSet<uint>();
        foreach (var probe in Probes)
        {
            var i = BestInterface(probe);
            if (i != 0) set.Add(i);
        }
        return set;
    }

    static bool DnsOutside(AdapterInfo vpn, IReadOnlyList<AdapterInfo> all) =>
        !vpn.HasDns && all.Any(o => o.Up && o.Id != vpn.Id && !o.LooksLikeVpn && o.HasDns);

    public static uint BestInterface(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return 0;
        var dest = BitConverter.ToUInt32(address.GetAddressBytes(), 0);
        return GetBestInterface(dest, out var index) == 0 ? index : 0;
    }

    public static IReadOnlyList<AdapterInfo> Suggestions(IReadOnlyList<AdapterInfo>? all = null) =>
        (all ?? Adapters()).Where(a => a.LooksLikeVpn).OrderByDescending(a => a.Up).ToList();

    [DllImport("iphlpapi.dll")]
    static extern int GetBestInterface(uint dwDestAddr, out uint pdwBestIfIndex);

    [DllImport("iphlpapi.dll")]
    static extern int ConvertInterfaceGuidToLuid(ref Guid interfaceGuid, out ulong interfaceLuid);

    [DllImport("iphlpapi.dll")]
    static extern int ConvertInterfaceLuidToIndex(ref ulong interfaceLuid, out uint interfaceIndex);
}
