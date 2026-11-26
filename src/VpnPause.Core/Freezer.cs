using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VpnPause.Core;

/// <summary>
/// Suspends only processes whose own image belongs to an app (no child-tree walk, so terminals and tools
/// launched from an app are never touched). Processes that own a visible window or the clipboard are
/// skipped unless asked otherwise: a suspended window or clipboard owner stalls the Windows shell.
/// One suspend is always paired with one resume, and a PID is resumed only if its start time still matches.
/// </summary>
public sealed class Freezer
{
    const uint PROCESS_SUSPEND_RESUME = 0x0800;

    readonly Dictionary<int, long> held = new();
    readonly object gate = new();

    public int HeldCount
    {
        get { lock (gate) return held.Count; }
    }

    public bool IsHeld(int pid)
    {
        lock (gate) return held.ContainsKey(pid);
    }

    public int Freeze(IReadOnlyList<AppRule> rules, IReadOnlyCollection<string> exclusions, bool includeWindows)
    {
        if (rules.Count == 0) return 0;
        var self = Environment.ProcessId;
        var skip = includeWindows ? new HashSet<int>() : ShellSensitivePids();
        int n = 0;
        foreach (var p in ProcessScanner.Snapshot())
        {
            if (p.Pid == self || skip.Contains(p.Pid)) continue;
            lock (gate)
                if (held.ContainsKey(p.Pid)) continue;
            if (!rules.Any(r => r.Matches(p.Path, exclusions))) continue;
            if (Control(p.Pid, suspend: true) != 0) continue;
            lock (gate) held[p.Pid] = p.StartTicks;
            n++;
        }
        return n;
    }

    public int ReleaseAll()
    {
        KeyValuePair<int, long>[] list;
        lock (gate)
        {
            list = held.ToArray();
            held.Clear();
        }
        int n = 0;
        foreach (var (pid, start) in list)
        {
            if (ProcessScanner.StartTicks(pid) != start) continue;
            if (Control(pid, suspend: false) == 0) n++;
        }
        return n;
    }

    /// <summary>
    /// Resumes app processes that are fully suspended, e.g. after a crash while frozen.
    /// With <paramref name="force"/> every matching process gets a resume call; that is a no-op for running ones.
    /// </summary>
    public static int ReleaseStuck(IReadOnlyList<AppRule> rules, IReadOnlyCollection<string> exclusions, bool force = false)
    {
        if (rules.Count == 0) return 0;
        int n = 0;
        foreach (var p in ProcessScanner.Snapshot())
        {
            if (!rules.Any(r => r.Matches(p.Path, exclusions))) continue;
            if (force)
            {
                for (int i = 0; i < 4 && IsFullySuspended(p.Pid); i++) Control(p.Pid, suspend: false);
                if (Control(p.Pid, suspend: false) == 0) n++;
                continue;
            }
            int tries = 0;
            while (IsFullySuspended(p.Pid) && tries++ < 4) Control(p.Pid, suspend: false);
            if (tries > 0) n++;
        }
        return n;
    }

    public static bool IsFullySuspended(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            var threads = proc.Threads;
            if (threads.Count == 0) return false;
            foreach (ProcessThread t in threads)
            {
                if (t.ThreadState != System.Diagnostics.ThreadState.Wait || t.WaitReason != ThreadWaitReason.Suspended) return false;
            }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static int Control(int pid, bool suspend)
    {
        var h = ProcessScanner.OpenProcess(PROCESS_SUSPEND_RESUME, false, pid);
        if (h == IntPtr.Zero) return Marshal.GetLastWin32Error();
        try
        {
            return suspend ? NtSuspendProcess(h) : NtResumeProcess(h);
        }
        finally
        {
            ProcessScanner.CloseHandle(h);
        }
    }

    /// <summary>PIDs that own a visible top-level window or the clipboard. Works only inside a user session.</summary>
    public static HashSet<int> ShellSensitivePids()
    {
        var pids = new HashSet<int>();
        EnumWindows((hwnd, _) =>
        {
            if (IsWindowVisible(hwnd) && GetWindowThreadProcessId(hwnd, out var pid) != 0) pids.Add((int)pid);
            return true;
        }, IntPtr.Zero);
        var owner = GetClipboardOwner();
        if (owner != IntPtr.Zero && GetWindowThreadProcessId(owner, out var cpid) != 0) pids.Add((int)cpid);
        return pids;
    }

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("ntdll.dll")] static extern int NtSuspendProcess(IntPtr h);
    [DllImport("ntdll.dll")] static extern int NtResumeProcess(IntPtr h);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] static extern IntPtr GetClipboardOwner();
}
