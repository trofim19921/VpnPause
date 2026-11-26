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
                Freezer.ReleaseStuck(cfg.Apps.Select(AppRule.From).ToList(), cfg.Exclusions, force: true);
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
