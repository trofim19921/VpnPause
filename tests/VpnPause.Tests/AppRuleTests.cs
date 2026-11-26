using VpnPause.Core;
using Xunit;

namespace VpnPause.Tests;

public class AppRuleTests
{
    static AppRule Rule(string path, MatchScope scope = MatchScope.Folder) => AppRule.From(new AppEntry { Path = path, Scope = scope });

    [Fact]
    public void StorePackageMatchesAnyVersionAndAnyExe()
    {
        var r = Rule(@"C:\Program Files\WindowsApps\OpenAI.Codex_26.1120.6357.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe");
        Assert.Equal(RuleKind.StorePackage, r.Kind);
        Assert.Equal("openai.codex_2p2nqsd0c76g0", r.StoreFamily);
        Assert.True(r.Matches(@"C:\Program Files\WindowsApps\OpenAI.Codex_26.1201.100.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe"));
        Assert.True(r.Matches(@"C:\Program Files\WindowsApps\OpenAI.Codex_26.1201.100.0_x64__2p2nqsd0c76g0\app\resources\codex.exe"));
        Assert.False(r.Matches(@"C:\Program Files\WindowsApps\Claude_6.28899.6.0_x64__pzs8sxrjxfjjc\claude.exe"));
    }

    [Fact]
    public void StoreExeScopeMatchesOnlyThatExe()
    {
        var r = Rule(@"C:\Program Files\WindowsApps\OpenAI.Codex_26.1120.6357.0_x64__2p2nqsd0c76g0\app\Codex.exe", MatchScope.Exe);
        Assert.True(r.Matches(@"C:\Program Files\WindowsApps\OpenAI.Codex_27.1.0.0_x64__2p2nqsd0c76g0\app\Codex.exe"));
        Assert.False(r.Matches(@"C:\Program Files\WindowsApps\OpenAI.Codex_27.1.0.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe"));
    }

    [Fact]
    public void ClaudeCodeMatchesNewVersionsButNotOtherClaude()
    {
        var r = Rule(@"C:\Users\Илья\AppData\Local\Claude-Data\claude_pzs8sxrjxfjjc\claude-code\2.1.330\9424831ad51c\claude.exe", MatchScope.Exe);
        Assert.Equal(RuleKind.ClaudeCode, r.Kind);
        Assert.True(r.Matches(@"C:\Users\Илья\AppData\Local\Claude-Data\claude_pzs8sxrjxfjjc\claude-code\2.2.0\abcdef\claude.exe"));
        Assert.False(r.Matches(@"C:\Program Files\WindowsApps\Claude_6.28899.6.0_x64__pzs8sxrjxfjjc\claude.exe"));
        Assert.False(r.Matches(@"C:\Users\Илья\Ouroboros\venv\Lib\site-packages\claude_agent_sdk\_bundled\claude.exe"));
    }

    [Fact]
    public void FolderScopeCoversHelpersAndVersionedSubfolders()
    {
        var r = Rule(@"C:\Users\Илья\AppData\Local\Programs\Opera\opera.exe");
        Assert.Equal(RuleKind.Folder, r.Kind);
        Assert.True(r.Matches(@"C:\Users\Илья\AppData\Local\Programs\Opera\115.0.5322.68\opera.exe"));
        Assert.True(r.Matches(@"C:\Users\Илья\AppData\Local\Programs\Opera\115.0.5322.68\opera_crashreporter.exe"));
        Assert.False(r.Matches(@"C:\Users\Илья\AppData\Local\Programs\Opera GX\opera.exe"));
    }

    [Fact]
    public void FolderOfVersionedExeClimbsToAppRoot()
    {
        var r = Rule(@"C:\Users\x\AppData\Local\Discord\app-1.0.9170\Discord.exe");
        Assert.True(r.Matches(@"C:\Users\x\AppData\Local\Discord\app-1.0.9200\Discord.exe"));
        Assert.True(r.Matches(@"C:\Users\x\AppData\Local\Discord\Update.exe"));
    }

    [Theory]
    [InlineData(@"C:\Program Files\tool.exe")]
    [InlineData(@"C:\Users\x\AppData\Local\Programs\tool.exe")]
    [InlineData(@"C:\Users\x\Desktop\tool.exe")]
    [InlineData(@"D:\tool.exe")]
    public void BroadFoldersFallBackToSingleExe(string path)
    {
        var r = Rule(path);
        Assert.Equal(RuleKind.Exe, r.Kind);
        Assert.False(r.Matches(Path.Combine(Path.GetDirectoryName(path)!, "other.exe")));
        Assert.True(r.Matches(path.ToUpperInvariant()));
    }

    [Fact]
    public void ExeScopeIgnoresVersionFolders()
    {
        var r = Rule(@"C:\Apps\Thing\1.2.3\thing.exe", MatchScope.Exe);
        Assert.True(r.Matches(@"C:\Apps\Thing\1.3.0\thing.exe"));
        Assert.False(r.Matches(@"C:\Apps\Thing\1.3.0\helper.exe"));
    }

    [Fact]
    public void ExclusionsWin()
    {
        var r = Rule(@"C:\Users\x\AppData\Local\Programs\cursor\Cursor.exe");
        Assert.True(r.Matches(@"C:\Users\x\AppData\Local\Programs\cursor\resources\app\bin\rg.exe"));
        Assert.False(r.Matches(@"C:\Users\x\AppData\Local\Programs\cursor\resources\app\bin\rg.exe", new[] { "rg.exe" }));
        Assert.False(r.Matches(@"C:\Users\x\AppData\Local\Programs\cursor\resources\app\bin\rg.exe", new[] { @"C:\Users\x\AppData\Local\Programs\cursor\resources\app\bin\RG.exe" }));
        Assert.False(r.Matches(@"C:\Users\x\AppData\Local\Programs\cursor\resources\app\bin\rg.exe", new[] { @"C:\Users\x\AppData\Local\Programs\cursor\resources\app\bin" }));
        Assert.True(r.Matches(@"C:\Users\x\AppData\Local\Programs\cursor\Cursor.exe", new[] { "rg.exe", @"C:\Users\x\AppData\Local\Programs\cursor\resources\app\bin" }));
    }

    [Theory]
    [InlineData("e")]
    [InlineData("rg")]
    [InlineData(@"\bin\")]
    [InlineData(@"\\server\share\x.exe")]
    [InlineData(@"C:\Program Files")]
    public void BadExclusionsAreRejected(string ex) => Assert.NotNull(AppRule.ValidateExclusion(ex));

    [Theory]
    [InlineData("rg.exe")]
    [InlineData(@"C:\Tools\thing.exe")]
    [InlineData(@"C:\Users\x\AppData\Local\Programs\cursor\resources")]
    public void GoodExclusionsAreAccepted(string ex) => Assert.Null(AppRule.ValidateExclusion(ex));

    [Theory]
    [InlineData(@"\\127.0.0.1\c$\Tools\x.exe")]
    [InlineData(@"\\attacker\share\app.exe")]
    [InlineData(@"\\?\C:\Tools\y.exe")]
    [InlineData(@"\\.\C:\Tools\z.exe")]
    [InlineData(@"\\?\UNC\server\share\z.exe")]
    [InlineData(@"C:\Tools\..\..\Windows\System32\cmd.exe")]
    public void NonLocalOrSneakyPathsAreRejected(string path) =>
        Assert.NotNull(AppRule.Validate(new AppEntry { Path = path }));

    [Fact]
    public void AppCountIsLimited()
    {
        var cfg = new AppConfig();
        for (int i = 0; i < ConfigStore.MaxApps + 50; i++) cfg.Apps.Add(new AppEntry { Path = $@"C:\Apps\A{i}\a.exe", Scope = MatchScope.Exe });
        ConfigStore.Normalize(cfg, out var rejected);
        Assert.Equal(ConfigStore.MaxApps, cfg.Apps.Count);
        Assert.Equal(50, rejected.Count);
    }

    [Fact]
    public void DamagedConfigFallsBackToBackup()
    {
        var file = Path.Combine(Path.GetTempPath(), $"vpnpause-test-{Guid.NewGuid():N}.json");
        try
        {
            ConfigStore.Save(file, new AppConfig { Apps = { new AppEntry { Name = "One", Path = @"C:\Apps\One\one.exe" } } });
            ConfigStore.Save(file, new AppConfig { Apps = { new AppEntry { Name = "Two", Path = @"C:\Apps\Two\two.exe" } } });
            Assert.True(File.Exists(file + ".bak"));

            File.WriteAllText(file, "");
            var back = ConfigStore.Load(file, out var error, out var unusable);
            Assert.False(unusable);
            Assert.NotNull(error);
            Assert.Equal("One", back.Apps.Single().Name);

            ConfigStore.Save(file, new AppConfig { Apps = { new AppEntry { Name = "Three", Path = @"C:\Apps\Three\three.exe" } } });
            Assert.Equal("One", ConfigStore.Load(file + ".bak").Apps.Single().Name);

            File.WriteAllText(file, "{");
            File.WriteAllText(file + ".bak", "{");
            ConfigStore.Load(file, out error, out unusable);
            Assert.True(unusable);
            Assert.NotNull(error);
        }
        finally
        {
            File.Delete(file);
            File.Delete(file + ".bak");
        }
    }

    [Fact]
    public void StrictIsTheDefault() => Assert.True(new AppConfig().StrictMode);

    [Theory]
    [InlineData(@"C:\Windows\System32\svchost.exe")]
    [InlineData(@"relative\app.exe")]
    [InlineData(@"C:\Apps\readme.txt")]
    [InlineData(@"C:\Program Files\AmneziaVPN\AmneziaVPN.exe")]
    [InlineData(@"C:\Program Files\WireGuard\wireguard.exe")]
    [InlineData(@"C:\Program Files\VpnPause\VpnPause.exe")]
    public void DangerousEntriesAreRejected(string path) =>
        Assert.NotNull(AppRule.Validate(new AppEntry { Path = path }));

    [Fact]
    public void NormalizeDropsDuplicatesAndClamps()
    {
        var cfg = new AppConfig
        {
            Freeze = new FreezeSettings { DelaySeconds = 500, MaxSeconds = 1 },
            Language = "de",
            Apps =
            {
                new AppEntry { Path = @"C:\Program Files\WindowsApps\Claude_6.1.0.0_x64__pzs8sxrjxfjjc\claude.exe" },
                new AppEntry { Path = @"C:\Program Files\WindowsApps\Claude_6.2.0.0_x64__pzs8sxrjxfjjc\claude.exe" },
                new AppEntry { Path = @"C:\Windows\notepad.exe" }
            }
        };
        ConfigStore.Normalize(cfg, out var rejected);
        Assert.Single(cfg.Apps);
        Assert.Single(rejected);
        Assert.Equal(60, cfg.Freeze.DelaySeconds);
        Assert.Equal(10, cfg.Freeze.MaxSeconds);
        Assert.Equal("ru", cfg.Language);
        Assert.Equal("Claude", cfg.Apps[0].Name);
    }

    [Fact]
    public void ConfigRoundTrips()
    {
        var file = Path.Combine(Path.GetTempPath(), $"vpnpause-test-{Guid.NewGuid():N}.json");
        try
        {
            var cfg = new AppConfig { StrictMode = true, Apps = { new AppEntry { Name = "Cursor", Path = @"C:\Users\x\AppData\Local\Programs\cursor\Cursor.exe" } } };
            ConfigStore.Save(file, cfg);
            var back = ConfigStore.Load(file);
            Assert.True(back.StrictMode);
            Assert.Equal("Cursor", back.Apps.Single().Name);
        }
        finally
        {
            File.Delete(file);
            File.Delete(file + ".bak");
        }
    }

    [Fact]
    public void PresetsLoad()
    {
        Assert.Contains(Presets.All, p => p.Id == "cursor");
        Assert.Contains(Presets.All, p => p.Id == "opera");
        Assert.All(Presets.All, p => Assert.False(string.IsNullOrWhiteSpace(p.Name)));
    }
}
