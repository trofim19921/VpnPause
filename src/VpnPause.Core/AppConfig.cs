using System.Text.Json;
using System.Text.Json.Serialization;

namespace VpnPause.Core;

public enum MatchScope
{
    Folder,
    Exe
}

public sealed class AdapterRef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
}

public sealed class FreezeSettings
{
    public bool Enabled { get; set; }
    public int DelaySeconds { get; set; } = 5;
    public int MaxSeconds { get; set; } = 120;
    public bool IncludeWindows { get; set; }
}

public sealed class AppEntry
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public MatchScope Scope { get; set; } = MatchScope.Folder;
    public string? Preset { get; set; }
}

public sealed class AppConfig
{
    public int Version { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public bool AutoAdapter { get; set; } = true;
    public AdapterRef? Adapter { get; set; }
    public bool StrictMode { get; set; }
    public bool AllowLan { get; set; }
    public FreezeSettings Freeze { get; set; } = new();
    public bool Notifications { get; set; } = true;
    public string Language { get; set; } = "ru";
    public bool FirstRunDone { get; set; }
    public List<AppEntry> Apps { get; set; } = new();
    public List<string> Exclusions { get; set; } = new();

    public AppConfig Clone() =>
        JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(this, Json.Options), Json.Options)!;
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };
}
