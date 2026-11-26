using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using VpnPause.Core;

namespace VpnPause.Service;

/// <summary>
/// Control pipe. SYSTEM, administrators and the interactive (logged-on) user may connect; only SYSTEM and
/// elevated administrators may change protection (see <see cref="Commands.NeedsAdmin"/>).
/// </summary>
public sealed class PipeServer
{
    readonly Func<Request, Task<Response>> handler;
    readonly RotatingLog log;

    public PipeServer(Func<Request, Task<Response>> handler, RotatingLog log)
    {
        this.handler = handler;
        this.log = log;
    }

    public void Start(CancellationToken ct) => _ = Task.Run(() => AcceptLoop(ct), ct);

    static PipeSecurity Security()
    {
        var sec = new PipeSecurity();
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return sec;
    }

    async Task AcceptLoop(CancellationToken ct)
    {
        bool first = true;
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                var options = PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
                server = NamedPipeServerStreamAcl.Create(AppPaths.PipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    options, 64 * 1024, 64 * 1024, Security());
                first = false;
                await server.WaitForConnectionAsync(ct);
                var connected = server;
                server = null;
                _ = Task.Run(() => Serve(connected, ct), ct);
            }
            catch (OperationCanceledException)
            {
                server?.Dispose();
                return;
            }
            catch (UnauthorizedAccessException) when (first)
            {
                log.Write("control pipe name is taken by another process, retrying");
                await Task.Delay(5000, ct).ContinueWith(_ => { });
            }
            catch (Exception e)
            {
                server?.Dispose();
                log.Write("pipe error: " + e.Message);
                await Task.Delay(1000, ct).ContinueWith(_ => { });
            }
        }
    }

    static bool CallerIsAdmin(NamedPipeServerStream stream)
    {
        bool admin = false;
        try
        {
            stream.RunAsClient(() =>
            {
                using var id = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
                admin = id.IsSystem || new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            });
        }
        catch (Exception)
        {
            admin = false;
        }
        return admin;
    }

    async Task Serve(NamedPipeServerStream stream, CancellationToken ct)
    {
        using (stream)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                var req = await Wire.ReadAsync<Request>(stream, timeout.Token);
                Response resp;
                try
                {
                    if (req == null) resp = Response.Fail("bad request");
                    else if (Commands.NeedsAdmin(req.Cmd) && !CallerIsAdmin(stream))
                    {
                        log.Write($"refused '{req.Cmd}' from a non-elevated client");
                        resp = Response.Fail(Errors.AdminRequired);
                    }
                    else resp = await handler(req);
                }
                catch (Exception e)
                {
                    resp = Response.Fail(e.Message);
                }
                await Wire.WriteAsync(stream, resp, timeout.Token);
            }
            catch (Exception)
            {
            }
        }
    }
}
