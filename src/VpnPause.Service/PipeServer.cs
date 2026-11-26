using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using VpnPause.Core;

namespace VpnPause.Service;

/// <summary>Control pipe. Only SYSTEM, administrators and the interactive (logged-on) user may connect.</summary>
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
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = NamedPipeServerStreamAcl.Create(AppPaths.PipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous, 64 * 1024, 64 * 1024, Security());
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
            catch (Exception e)
            {
                server?.Dispose();
                log.Write("pipe error: " + e.Message);
                await Task.Delay(1000, ct).ContinueWith(_ => { });
            }
        }
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
                    resp = req == null ? Response.Fail("bad request") : await handler(req);
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
