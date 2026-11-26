using System.Diagnostics;
using VpnPause.Core;
using Xunit;

namespace VpnPause.Tests;

public class FreezePolicyTests
{
    static readonly TimeSpan Delay = TimeSpan.FromSeconds(5);
    static readonly TimeSpan Max = TimeSpan.FromSeconds(120);
    static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ShortDropFreezesNothing()
    {
        var p = new FreezePolicy();
        Assert.Equal(FreezeAction.None, p.Tick(true, false, T0, Delay, Max));
        Assert.Equal(FreezeAction.None, p.Tick(true, false, T0.AddSeconds(4.9), Delay, Max));
        Assert.Equal(FreezeAction.Release, p.Tick(true, true, T0.AddSeconds(5.5), Delay, Max));
    }

    [Fact]
    public void FreezesAfterDelayAndReleasesAfterMax()
    {
        var p = new FreezePolicy();
        p.Tick(true, false, T0, Delay, Max);
        Assert.Equal(FreezeAction.Freeze, p.Tick(true, false, T0.AddSeconds(5), Delay, Max));
        Assert.Equal(FreezeAction.Freeze, p.Tick(true, false, T0.AddSeconds(124), Delay, Max));
        Assert.Equal(FreezeAction.Release, p.Tick(true, false, T0.AddSeconds(125), Delay, Max));
        Assert.True(p.Expired);
        Assert.Equal(FreezeAction.None, p.Tick(true, false, T0.AddHours(10), Delay, Max));
    }

    [Fact]
    public void VpnReturnResetsTheClock()
    {
        var p = new FreezePolicy();
        p.Tick(true, false, T0, Delay, Max);
        p.Tick(true, false, T0.AddSeconds(200), Delay, Max);
        Assert.True(p.Expired);
        Assert.Equal(FreezeAction.Release, p.Tick(true, true, T0.AddSeconds(201), Delay, Max));
        Assert.False(p.Expired);
        Assert.Equal(FreezeAction.None, p.Tick(true, false, T0.AddSeconds(202), Delay, Max));
        Assert.Equal(FreezeAction.Freeze, p.Tick(true, false, T0.AddSeconds(207), Delay, Max));
    }

    [Fact]
    public void InactiveAlwaysReleases()
    {
        var p = new FreezePolicy();
        p.Tick(true, false, T0, Delay, Max);
        Assert.Equal(FreezeAction.Release, p.Tick(false, false, T0.AddSeconds(30), Delay, Max));
    }
}

public class FreezerTests
{
    [Fact]
    public void SuspendsOnlyMatchingProcessAndResumesIt()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vpnpause-freeze-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var ping = Path.Combine(dir, "ping.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), ping);
        using var p = Process.Start(new ProcessStartInfo(ping, "-n 60 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        try
        {
            Thread.Sleep(500);
            var rules = new[] { AppRule.From(new AppEntry { Path = ping, Scope = MatchScope.Exe }) };
            var f = new Freezer();
            Assert.Equal(1, f.Freeze(rules, Array.Empty<string>(), includeWindows: false));
            Assert.True(f.IsHeld(p.Id));
            Assert.True(Freezer.IsFullySuspended(p.Id));
            Assert.Equal(0, f.Freeze(rules, Array.Empty<string>(), includeWindows: false));
            Assert.Equal(1, f.ReleaseAll());
            Assert.False(Freezer.IsFullySuspended(p.Id));
            Assert.Equal(0, f.HeldCount);

            Assert.Equal(1, f.Freeze(rules, Array.Empty<string>(), includeWindows: false));
            Assert.Equal(0, Freezer.Control(p.Id, suspend: false));
            Assert.Equal(0, f.ReleaseAll());
            Assert.False(Freezer.IsFullySuspended(p.Id));
        }
        finally
        {
            Freezer.ReleaseStuck(new[] { AppRule.From(new AppEntry { Path = ping, Scope = MatchScope.Exe }) }, Array.Empty<string>());
            p.Kill();
            p.WaitForExit(5000);
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ReleaseStuckResumesAFrozenProcess()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vpnpause-stuck-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var ping = Path.Combine(dir, "ping.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), ping);
        using var p = Process.Start(new ProcessStartInfo(ping, "-n 60 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        try
        {
            Thread.Sleep(500);
            Assert.Equal(0, Freezer.Control(p.Id, suspend: true));
            Assert.True(Freezer.IsFullySuspended(p.Id));
            var rules = new[] { AppRule.From(new AppEntry { Path = ping, Scope = MatchScope.Exe }) };
            Assert.Equal(1, Freezer.ReleaseStuck(rules, Array.Empty<string>()));
            Assert.False(Freezer.IsFullySuspended(p.Id));
            Assert.Equal(0, Freezer.ReleaseStuck(rules, Array.Empty<string>()));
        }
        finally
        {
            p.Kill();
            p.WaitForExit(5000);
            Directory.Delete(dir, true);
        }
    }
}
