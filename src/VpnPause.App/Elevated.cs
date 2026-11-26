using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Principal;
using System.Text.Json;
using VpnPause.Core;

namespace VpnPause.App;

/// <summary>
/// Changes to protection need an elevated administrator. A non-elevated window re-launches this exe with
/// "--admin &lt;request&gt;" through UAC; the request travels on the command line, so nothing can swap it on disk.
/// </summary>
static class Elevated
{
    const int MaxArg = 30000;

    public static bool IsAdmin { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public static async Task<Response> SendAsync(Request request, int timeoutMs = 10000)
    {
        if (IsAdmin) return await ServiceClient.SendAsync(request, timeoutMs);
        var arg = Encode(request);
        if (arg.Length > MaxArg) return Response.Fail("request is too large");
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--admin " + arg)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (p == null) return Response.Fail(Errors.Cancelled);
            using var cts = new CancellationTokenSource(timeoutMs + 60000);
            await p.WaitForExitAsync(cts.Token);
            return p.ExitCode switch
            {
                0 => new Response { Ok = true },
                3 => Response.Fail(Errors.AdminRequired),
                4 => Response.Fail(Errors.ServiceUnavailable),
                _ => Response.Fail("the change was not applied")
            };
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            return Response.Fail(Errors.Cancelled);
        }
        catch (OperationCanceledException)
        {
            return Response.Fail("timed out");
        }
    }

    /// <summary>Body of "--admin": exit 0 on success, 3 when not elevated, 4 when the service is down, 2 otherwise.</summary>
    public static int Run(string? arg)
    {
        if (!IsAdmin) return 3;
        Request? req;
        try
        {
            req = Decode(arg ?? "");
        }
        catch (Exception)
        {
            return 2;
        }
        if (req == null || !Commands.NeedsAdmin(req.Cmd)) return 2;
        var r = ServiceClient.SendAsync(req, 10000).GetAwaiter().GetResult();
        if (r.Ok) return 0;
        return r.Error?.StartsWith(Errors.ServiceUnavailable) == true ? 4 : r.Error == Errors.AdminRequired ? 3 : 2;
    }

    static string Encode(Request r)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(r, Json.Compact);
        using var ms = new MemoryStream();
        using (var z = new GZipStream(ms, CompressionLevel.SmallestSize, leaveOpen: true)) z.Write(json);
        return Convert.ToBase64String(ms.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    static Request? Decode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        using var z = new GZipStream(new MemoryStream(Convert.FromBase64String(s)), CompressionMode.Decompress);
        using var limited = new MemoryStream();
        var buf = new byte[8192];
        int n;
        while ((n = z.Read(buf)) > 0)
        {
            limited.Write(buf, 0, n);
            if (limited.Length > Wire.MaxMessage) throw new InvalidDataException("too large");
        }
        return JsonSerializer.Deserialize<Request>(limited.ToArray(), Json.Compact);
    }
}

/// <summary>Per-user window settings. They do not affect protection, so they need no administrator.</summary>
sealed class UserPrefs
{
    public string Language { get; set; } = "ru";
    public bool Notifications { get; set; } = true;
    public bool FirstRunDone { get; set; }

    static string FilePath => Path.Combine(AppPaths.UserDir, "prefs.json");

    public static bool Exists => File.Exists(FilePath);

    public static UserPrefs Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<UserPrefs>(File.ReadAllText(FilePath), Json.Options) ?? new UserPrefs();
        }
        catch (Exception)
        {
        }
        return new UserPrefs();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.UserDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json.Options));
        }
        catch (Exception)
        {
        }
    }
}
