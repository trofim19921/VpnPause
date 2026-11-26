using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using VpnPause.Core;
using VpnPause.Service.Wfp;

namespace VpnPause.Service;

/// <summary>
/// End-to-end check of the filters on a private copy of curl.exe. Never touches real apps.
/// Expected: through the VPN works, bound to the physical adapter fails, and with no VPN adapter both fail.
/// </summary>
public static class SelfTest
{
    public static int Run(TextWriter output)
    {
        var cfg = ConfigStore.Load(AppPaths.ConfigFile);
        var adapter = Network.Status(cfg).Adapter ?? Network.Suggestions().FirstOrDefault(a => a.Up);
        if (adapter == null)
        {
            output.WriteLine("FAIL no VPN adapter");
            return 2;
        }
        var physicalIp = PhysicalAddress(adapter.Id);
        output.WriteLine($"vpn adapter: {adapter.Name} luid={adapter.Luid} up={Network.Status(adapter.ToRef()).Up}");
        output.WriteLine($"physical source ip: {physicalIp ?? "none"}");

        var dir = Path.Combine(Path.GetTempPath(), "vpnpause-selftest");
        Directory.CreateDirectory(dir);
        dir = WfpEngine.LongPath(dir);
        var curl = Path.Combine(dir, "curl.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "curl.exe"), curl, overwrite: true);

        var before = Probe(curl, physicalIp);
        output.WriteLine($"no filters:      vpn={before.vpn ?? "fail"} physical={before.physical ?? "fail"}");

        int failures = 0;
        using (var wfp = new WfpEngine(WfpEngine.SelfTestProvider, WfpEngine.SelfTestSubLayer))
        {
            var target = new[] { new FilterTarget("selftest", curl, null) };
            wfp.Apply(new FilterPlan { Enabled = true, VpnLuids = new[] { adapter.Luid }, Targets = target });
            output.WriteLine($"filters added: {wfp.FilterCount}");
            var with = Probe(curl, physicalIp);
            output.WriteLine($"with VPN permit: vpn={with.vpn ?? "fail"} physical={with.physical ?? "fail"}");
            if (before.vpn != null && with.vpn == null) { failures++; output.WriteLine("FAIL traffic through the VPN was blocked"); }
            if (physicalIp != null && with.physical != null) { failures++; output.WriteLine("FAIL traffic outside the VPN got through"); }

            wfp.Apply(new FilterPlan { Enabled = true, Targets = target });
            foreach (var line in wfp.Describe()) output.WriteLine("  " + line);
            output.WriteLine("  curl: " + curl);
            var none = Probe(curl, physicalIp);
            output.WriteLine($"no VPN adapter:  vpn={none.vpn ?? "fail"} physical={none.physical ?? "fail"}");
            if (none.vpn != null || none.physical != null) { failures++; output.WriteLine("FAIL traffic got through with VPN missing"); }

            wfp.Apply(new FilterPlan { Enabled = true, Strict = true, Targets = target });
            if (!wfp.Verify(full: false) || !wfp.Verify(full: true)) { failures++; output.WriteLine("FAIL verify reports missing filters while they are in place"); }
            var strictNone = Probe(curl, physicalIp);
            output.WriteLine($"strict, no VPN adapter: vpn={strictNone.vpn ?? "fail"} physical={strictNone.physical ?? "fail"}");
            if (strictNone.vpn != null || strictNone.physical != null) { failures++; output.WriteLine("FAIL strict filters let traffic through"); }
            WfpEngine.RemoveEverything(WfpEngine.SelfTestProvider, WfpEngine.SelfTestSubLayer);
            bool quick = wfp.Verify(full: false), full = wfp.Verify(full: true);
            output.WriteLine($"after external removal: quick verify={quick} full verify={full}");
            if (quick || full) { failures++; output.WriteLine("FAIL verify did not notice filters removed from outside"); }

            wfp.Apply(new FilterPlan { Enabled = false });
            var after = Probe(curl, physicalIp);
            output.WriteLine($"filters removed: vpn={after.vpn ?? "fail"} physical={after.physical ?? "fail"}");
        }
        output.WriteLine(failures == 0 ? "PASS" : $"FAILED {failures}");
        return failures == 0 ? 0 : 1;
    }

    static (string? vpn, string? physical) Probe(string curl, string? physicalIp) =>
        (Curl(curl, null), physicalIp == null ? null : Curl(curl, physicalIp));

    static string? Curl(string curl, string? bindIp)
    {
        var args = "-s -m 8 https://api.ipify.org";
        if (bindIp != null) args = $"--interface {bindIp} " + args;
        using var p = Process.Start(new ProcessStartInfo(curl, args) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
        var text = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit(10000);
        return p.ExitCode == 0 && text.Length > 0 ? text : null;
    }

    static string? PhysicalAddress(string vpnId)
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.Id == vpnId || ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var props = ni.GetIPProperties();
            if (!props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(System.Net.IPAddress.Any))) continue;
            var ip = props.UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork);
            if (ip != null) return ip.Address.ToString();
        }
        return null;
    }
}
