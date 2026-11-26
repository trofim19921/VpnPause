using System;
using System.IO;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Threading;

static class VpnPauseHost {
    [STAThread]
    static int Main() {
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        string script = Path.Combine(dir, "VpnPause.ps1");
        string log = Path.Combine(dir, "vpn-pause.log");
        Runspace rs = null;
        try {
            rs = RunspaceFactory.CreateRunspace();
            rs.ApartmentState = ApartmentState.STA;
            rs.ThreadOptions = PSThreadOptions.UseCurrentThread;
            rs.Open();
            using (var ps = PowerShell.Create()) {
                ps.Runspace = rs;
                ps.AddScript("Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force");
                ps.Invoke();
                ps.Commands.Clear();
                ps.AddScript("& '" + script.Replace("'", "''") + "'");
                ps.Invoke();
                if (ps.HadErrors) {
                    File.AppendAllText(log, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " host: " + ps.Streams.Error[0] + Environment.NewLine);
                    return 1;
                }
            }
            return 0;
        } catch (Exception ex) {
            File.AppendAllText(log, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " host: " + ex + Environment.NewLine);
            return 1;
        } finally {
            if (rs != null) rs.Dispose();
        }
    }
}
