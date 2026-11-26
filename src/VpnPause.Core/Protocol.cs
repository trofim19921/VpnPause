using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace VpnPause.Core;

public static class Commands
{
    public const string Status = "status";
    public const string GetConfig = "getConfig";
    public const string SetConfig = "setConfig";
    public const string SetEnabled = "setEnabled";
    public const string ClearFilters = "clearFilters";
    public const string ReleaseStuck = "releaseStuck";
    public const string FreezeLease = "freezeLease";

    /// <summary>Commands that change protection. Only SYSTEM and elevated administrators may send them.</summary>
    public static bool NeedsAdmin(string cmd) => cmd is SetConfig or SetEnabled or ClearFilters;
}

public static class Errors
{
    public const string AdminRequired = "admin-required";
    public const string Cancelled = "cancelled";
    public const string ServiceUnavailable = "service-unavailable";
}

public sealed class Request
{
    public string Cmd { get; set; } = "";
    public AppConfig? Config { get; set; }
    public bool? Enabled { get; set; }
}

public sealed class TargetInfo
{
    public string Name { get; set; } = "";
    public int Files { get; set; }
    public bool Package { get; set; }
}

public sealed class ServiceStatus
{
    public string Version { get; set; } = "";
    public bool Enabled { get; set; }
    public bool Strict { get; set; }
    public bool AdapterFound { get; set; }
    public bool VpnUp { get; set; }
    public string AdapterName { get; set; } = "";
    public bool DnsOutside { get; set; }
    public int FilterCount { get; set; }
    public List<TargetInfo> Targets { get; set; } = new();
    public string? LastError { get; set; }
    public string? ConfigError { get; set; }
    public DateTime AppliedAt { get; set; }
}

public sealed class Response
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public List<string>? Rejected { get; set; }
    public ServiceStatus? Status { get; set; }
    public AppConfig? Config { get; set; }

    public static Response Fail(string error) => new() { Ok = false, Error = error };
}

public static class Wire
{
    public const int MaxMessage = 1024 * 1024;

    public static async Task WriteAsync(Stream s, object message, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json.Compact);
        var len = BitConverter.GetBytes(bytes.Length);
        await s.WriteAsync(len, ct);
        await s.WriteAsync(bytes, ct);
        await s.FlushAsync(ct);
    }

    public static async Task<T?> ReadAsync<T>(Stream s, CancellationToken ct)
    {
        var len = new byte[4];
        await s.ReadExactlyAsync(len, ct);
        var n = BitConverter.ToInt32(len);
        if (n <= 0 || n > MaxMessage) throw new InvalidDataException("bad message size");
        var buf = new byte[n];
        await s.ReadExactlyAsync(buf, ct);
        return JsonSerializer.Deserialize<T>(buf, Json.Compact);
    }
}

public static class ServiceClient
{
    public static async Task<Response> SendAsync(Request request, int timeoutMs = 3000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            using var pipe = new NamedPipeClientStream(".", AppPaths.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
                System.Security.Principal.TokenImpersonationLevel.Identification);
            await pipe.ConnectAsync(Math.Min(timeoutMs, 1500), cts.Token);
            if (!TrustedServer(pipe)) return Response.Fail("untrusted-server");
            await Wire.WriteAsync(pipe, request, cts.Token);
            return await Wire.ReadAsync<Response>(pipe, cts.Token) ?? Response.Fail("empty response");
        }
        catch (TimeoutException)
        {
            return Response.Fail(Errors.ServiceUnavailable);
        }
        catch (OperationCanceledException)
        {
            return Response.Fail(Errors.ServiceUnavailable);
        }
        catch (IOException e)
        {
            return Response.Fail(Errors.ServiceUnavailable + ": " + e.Message);
        }
        catch (UnauthorizedAccessException)
        {
            return Response.Fail("access-denied");
        }
    }

    /// <summary>The pipe must be served by this very exe or by the installed one, not by a squatter.</summary>
    static bool TrustedServer(NamedPipeClientStream pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid)) return false;
        var path = ProcessScanner.Query((int)pid).path;
        if (path == null) return false;
        return string.Equals(path, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(path, AppPaths.InstalledExe, StringComparison.OrdinalIgnoreCase);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);
}
