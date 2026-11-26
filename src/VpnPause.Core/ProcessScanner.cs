using System.Runtime.InteropServices;
using System.Text;

namespace VpnPause.Core;

public readonly record struct ProcInfo(int Pid, int ParentPid, string Path, long StartTicks);

public static class ProcessScanner
{
    const uint TH32CS_SNAPPROCESS = 0x2;
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>All processes whose image path could be read.</summary>
    public static List<ProcInfo> Snapshot()
    {
        var result = new List<ProcInfo>();
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return result;
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snap, ref e)) return result;
            do
            {
                var pid = (int)e.th32ProcessID;
                if (pid <= 4) continue;
                var (path, start) = Query(pid);
                if (path != null) result.Add(new ProcInfo(pid, (int)e.th32ParentProcessID, path, start));
            } while (Process32NextW(snap, ref e));
        }
        finally
        {
            CloseHandle(snap);
        }
        return result;
    }

    /// <summary>Process ids only, without opening the processes.</summary>
    public static List<int> Pids() => Entries().Select(e => e.Pid).ToList();

    /// <summary>Process ids, parents and exe file names, without opening the processes.</summary>
    public static List<(int Pid, int ParentPid, string Exe)> Entries()
    {
        var result = new List<(int, int, string)>();
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return result;
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snap, ref e)) return result;
            do
            {
                if (e.th32ProcessID > 4) result.Add(((int)e.th32ProcessID, (int)e.th32ParentProcessID, e.szExeFile));
            } while (Process32NextW(snap, ref e));
        }
        finally
        {
            CloseHandle(snap);
        }
        return result;
    }

    public static (string? path, long startTicks) Query(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return (null, 0);
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            string? path = QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
            long start = GetProcessTimes(h, out var c, out _, out _, out _) ? c : 0;
            return (path, start);
        }
        finally
        {
            CloseHandle(h);
        }
    }

    public static long StartTicks(int pid) => Query(pid).startTicks;

    /// <summary>
    /// Snapshot for repeated polling: only processes not seen before are opened. A reused PID shows up
    /// with a different exe name or parent and is queried again. Good for display, not for decisions
    /// that need the start time checked.
    /// </summary>
    public sealed class Cache
    {
        Dictionary<int, (int Parent, string Exe, ProcInfo? Info)> known = new();

        public List<ProcInfo> Snapshot()
        {
            var next = new Dictionary<int, (int, string, ProcInfo?)>();
            var result = new List<ProcInfo>();
            foreach (var (pid, parent, exe) in Entries())
            {
                ProcInfo? info;
                if (known.TryGetValue(pid, out var k) && k.Parent == parent && k.Exe == exe) info = k.Info;
                else
                {
                    var (path, start) = Query(pid);
                    info = path == null ? null : new ProcInfo(pid, parent, path, start);
                }
                next[pid] = (parent, exe, info);
                if (info is { } i) result.Add(i);
            }
            known = next;
            return result;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    internal static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool QueryFullProcessImageNameW(IntPtr h, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);
}
