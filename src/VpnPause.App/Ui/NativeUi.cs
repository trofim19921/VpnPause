using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace VpnPause.App;

static class NativeUi
{
    public const int HotkeyId = 0x5650;
    public const int WM_HOTKEY = 0x0312;
    const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;
    static readonly IntPtr HWND_BROADCAST = new(0xffff);

    public static readonly int ShowMessage = RegisterWindowMessage("VpnPause.ShowWindow");

    public static void SignalExisting() => PostMessage(HWND_BROADCAST, ShowMessage, IntPtr.Zero, IntPtr.Zero);

    public static bool RegisterReleaseHotkey(IntPtr hwnd) =>
        RegisterHotKey(hwnd, HotkeyId, MOD_CONTROL | MOD_ALT | MOD_SHIFT | MOD_NOREPEAT, (uint)Keys.P);

    public static void UnregisterReleaseHotkey(IntPtr hwnd) => UnregisterHotKey(hwnd, HotkeyId);

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool AutostartEnabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue("VpnPause") is string;
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue("VpnPause", $"\"{Environment.ProcessPath}\" --tray");
            else k.DeleteValue("VpnPause", false);
        }
    }

    public static void StartServiceElevated()
    {
        try
        {
            Process.Start(new ProcessStartInfo("sc.exe", "start VpnPauseService") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
        }
        catch (Exception)
        {
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int RegisterWindowMessage(string name);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
