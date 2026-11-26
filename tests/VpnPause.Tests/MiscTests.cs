using VpnPause.Core;
using Xunit;

namespace VpnPause.Tests;

public class MiscTests
{
    [Fact]
    public void LogKeepsAtMostFiveFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vpnpause-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new RotatingLog(dir, "t", maxBytes: 200, keep: 5);
            for (int i = 0; i < 200; i++) log.Write($"line {i} " + new string('x', 40));
            var files = Directory.GetFiles(dir, "t*.log");
            Assert.Equal(5, files.Length);
            Assert.Contains("line 199", log.Tail(1).Single());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void PatternExpandsWildcardSegments()
    {
        var root = Path.Combine(Path.GetTempPath(), "vpnpause-pat-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var v in new[] { "1.0.0", "2.0.0" })
            {
                var d = Path.Combine(root, "claude-code", v, "abc");
                Directory.CreateDirectory(d);
                File.WriteAllText(Path.Combine(d, "claude.exe"), "");
            }
            var hits = Presets.ExpandPattern(Path.Combine(root, "claude-code", "*", "*", "claude.exe")).ToList();
            Assert.Equal(2, hits.Count);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
