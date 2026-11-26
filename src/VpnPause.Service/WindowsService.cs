using System.ServiceProcess;
using VpnPause.Core;

namespace VpnPause.Service;

public sealed class WindowsService : ServiceBase
{
    readonly ServiceCore core = new();

    public WindowsService()
    {
        ServiceName = AppPaths.ServiceName;
        CanStop = true;
        CanShutdown = true;
        AutoLog = false;
    }

    protected override void OnStart(string[] args) => core.Start();

    protected override void OnStop() => core.Stop();

    protected override void OnShutdown() => core.Stop();

    public static void RunAsService() => Run(new WindowsService());

    /// <summary>Runs the same core in a console for debugging. Needs an elevated prompt.</summary>
    public static void RunInConsole()
    {
        using var core = new ServiceCore();
        core.Log.Written += Console.WriteLine;
        core.Start();
        Console.WriteLine("VpnPause service core running. Press Enter to stop.");
        Console.ReadLine();
        core.Stop();
    }
}
