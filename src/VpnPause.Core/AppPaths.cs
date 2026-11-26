namespace VpnPause.Core;

public static class AppPaths
{
    public const string ServiceName = "VpnPauseService";
    public const string PipeName = "VpnPause.Control";
    public const string ProductName = "VpnPause";

    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), ProductName);

    public static string ConfigFile => Path.Combine(DataDir, "config.json");

    public static string ServiceLogDir => Path.Combine(DataDir, "logs");

    public static string UserDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductName);

    public static string UserLogDir => Path.Combine(UserDir, "logs");

    public static string InstalledExe =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), ProductName, ProductName + ".exe");

    public static string LegacyDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", ProductName);
}
