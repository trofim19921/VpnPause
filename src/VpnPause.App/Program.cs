using System.Runtime.InteropServices;
using VpnPause.Core;
using VpnPause.Service;
using VpnPause.Service.Wfp;

namespace VpnPause.App;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        var mode = args.FirstOrDefault()?.ToLowerInvariant();
        switch (mode)
        {
            case "--service":
                WindowsService.RunAsService();
                return 0;
            case "--console":
                AttachConsole();
                WindowsService.RunInConsole();
                return 0;
            case "--admin":
                return Elevated.Run(args.Skip(1).FirstOrDefault());
            case "--import-legacy":
                return ImportLegacy();
            case "--remove-filters":
                try
                {
                    WfpEngine.RemoveEverything();
                    return 0;
                }
                catch (Exception)
                {
                    return 1;
                }
            case "--selftest":
                return RunSelfTest(args.Skip(1).FirstOrDefault());
            case "--release-all":
                var cfg = ConfigStore.Load(AppPaths.ConfigFile);
                Freezer.ReleaseStuck(cfg.Apps.Select(AppRule.From).ToList(), cfg.Exclusions);
                return 0;
        }

        using var single = new Mutex(true, @"Local\VpnPause.App", out var first);
        if (!first)
        {
            NativeUi.SignalExisting();
            return 0;
        }

        ApplicationConfiguration.Initialize();
        var controller = new AppController();
        var startHidden = mode == "--tray";
        Application.Run(new TrayContext(controller, startHidden));
        return 0;
    }

    /// <summary>Run by the installer (elevated, as the installing user): moves the old apps.txt list into the service config.</summary>
    static int ImportLegacy()
    {
        if (!Legacy.TryImport(out var entries, out _)) return 0;
        var log = new RotatingLog(AppPaths.UserLogDir, "app");
        for (int i = 0; i < 15; i++)
        {
            var r = ServiceClient.SendAsync(new Request { Cmd = Commands.GetConfig }).GetAwaiter().GetResult();
            if (r.Ok && r.Config != null)
            {
                var cfg = r.Config;
                foreach (var e in entries)
                    if (!cfg.Apps.Any(a => string.Equals(a.Path, e.Path, StringComparison.OrdinalIgnoreCase))) cfg.Apps.Add(e);
                var s = ServiceClient.SendAsync(new Request { Cmd = Commands.SetConfig, Config = cfg }, 10000).GetAwaiter().GetResult();
                if (!s.Ok) return 2;
                Legacy.Retire(log);
                log.Write($"installer imported {entries.Count} apps from the previous version");
                return 0;
            }
            Thread.Sleep(1000);
        }
        return 4;
    }

    static int RunSelfTest(string? outFile)
    {
        if (outFile == null)
        {
            AttachConsole();
            return SelfTest.Run(Console.Out);
        }
        using var w = new StreamWriter(outFile, false);
        try
        {
            return SelfTest.Run(w);
        }
        catch (Exception e)
        {
            w.WriteLine("ERROR " + e);
            return 3;
        }
    }

    static void AttachConsole()
    {
        if (!AttachConsoleNative(-1)) AllocConsole();
    }

    [DllImport("kernel32.dll", EntryPoint = "AttachConsole")] static extern bool AttachConsoleNative(int pid);
    [DllImport("kernel32.dll")] static extern bool AllocConsole();
}
