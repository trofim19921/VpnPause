# Замораживает процессы из apps.txt, пока у tun2 нет маршрутов VPN,
# и отпускает их, когда маршруты возвращаются. Одно замораживание — одно продолжение.
param(
    [switch]$Stop,
    [switch]$SelfTest,
    [switch]$SyncFirewall
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$AppsFile = Join-Path $Root 'apps.txt'
$LogFile = Join-Path $Root 'vpn-pause.log'
$SettingsFile = Join-Path $Root 'settings.txt'
$Tunnel = 'tun2'
if (Test-Path -LiteralPath $SettingsFile) {
    foreach ($line in Get-Content -LiteralPath $SettingsFile -Encoding UTF8) {
        if ($line -match '^\s*tunnel\s*=\s*(.+?)\s*$') { $Tunnel = $Matches[1]; break }
    }
}

Add-Type -TypeDefinition @'
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

public static class VpnPauseNative {
    public static readonly AutoResetEvent Changed = new AutoResetEvent(false);
    public static volatile bool Armed = true;
    public static volatile bool VpnIsUp;
    static volatile bool running = true;
    static volatile string tunnel = "tun2";
    static string[] rules = new string[0];
    static readonly HashSet<int> held = new HashSet<int>();
    static readonly object gate = new object();
    static readonly ConcurrentQueue<string> logs = new ConcurrentQueue<string>();
    const uint PROCESS_SUSPEND_RESUME = 0x0800;
    const uint PROCESS_QUERY_LIMITED = 0x1000;
    const uint TH32CS_SNAPPROCESS = 2;

    [DllImport("ntdll.dll")] public static extern int NtSuspendProcess(IntPtr h);
    [DllImport("ntdll.dll")] public static extern int NtResumeProcess(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder buf, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool Process32First(IntPtr snap, ref PROCESSENTRY32 pe);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool Process32Next(IntPtr snap, ref PROCESSENTRY32 pe);
    [DllImport("iphlpapi.dll")] static extern int NotifyRouteChange(IntPtr handle, IntPtr overlapped);
    [DllImport("iphlpapi.dll")] static extern int NotifyAddrChange(IntPtr handle, IntPtr overlapped);
    [DllImport("iphlpapi.dll", CharSet = CharSet.Unicode)]
    static extern int ConvertInterfaceAliasToLuid(string alias, out ulong luid);
    [DllImport("iphlpapi.dll")]
    static extern int ConvertInterfaceLuidToIndex(ref ulong luid, out uint index);
    [DllImport("iphlpapi.dll")]
    static extern int GetIpForwardTable(IntPtr table, ref int size, bool order);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32 {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    struct Proc {
        public int Id;
        public int Parent;
        public string Path;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Row {
        public uint dest, mask, policy, nextHop, ifIndex, type, proto, age, nextHopAS, m1, m2, m3, m4, m5;
    }

    public static int PausedCount { get { lock (gate) return held.Count; } }
    public static bool IsHeld(int pid) { lock (gate) return held.Contains(pid); }
    public static void SetArmed(bool on) { Armed = on; Changed.Set(); }
    public static void SetRules(string[] list) { rules = list ?? new string[0]; Changed.Set(); }
    public static string[] DrainLogs() {
        var list = new List<string>();
        string line;
        while (logs.TryDequeue(out line)) list.Add(line);
        return list.ToArray();
    }
    static void Log(string message) { logs.Enqueue(message); }

    public static string MatchKey(string path) {
        if (string.IsNullOrEmpty(path)) return "";
        var p = path.Replace('/', '\\');
        var folder = Regex.Match(p, @"\\WindowsApps\\([^\\]+)\\", RegexOptions.IgnoreCase);
        if (folder.Success) {
            var name = Regex.Match(folder.Groups[1].Value, @"^(.*)_\d[^_]*_[^_]+__([^_]+)$");
            if (name.Success)
                return "pkg:" + name.Groups[1].Value + "_" + name.Groups[2].Value + ":" + Path.GetFileName(p).ToLowerInvariant();
        }
        if (p.IndexOf(@"\claude-code\", StringComparison.OrdinalIgnoreCase) >= 0 &&
            p.IndexOf(@"\Ouroboros\", StringComparison.OrdinalIgnoreCase) < 0)
            return "family:ClaudeCode";
        return "path:" + p.ToLowerInvariant();
    }

    public static int Control(int pid, bool suspend) {
        IntPtr h = OpenProcess(PROCESS_SUSPEND_RESUME, false, pid);
        if (h == IntPtr.Zero) return Marshal.GetLastWin32Error();
        try {
            int st = suspend ? NtSuspendProcess(h) : NtResumeProcess(h);
            return st;
        } finally { CloseHandle(h); }
    }

    public static bool TunnelUp(string alias) {
        ulong luid;
        uint index;
        if (ConvertInterfaceAliasToLuid(alias, out luid) != 0) return false;
        if (ConvertInterfaceLuidToIndex(ref luid, out index) != 0) return false;
        int size = 0;
        GetIpForwardTable(IntPtr.Zero, ref size, false);
        if (size <= 0) return false;
        IntPtr buf = Marshal.AllocHGlobal(size);
        try {
            if (GetIpForwardTable(buf, ref size, false) != 0) return false;
            int n = Marshal.ReadInt32(buf);
            int row = Marshal.SizeOf(typeof(Row));
            bool low = false, high = false;
            for (int i = 0; i < n; i++) {
                var r = (Row)Marshal.PtrToStructure(new IntPtr(buf.ToInt64() + 4 + (long)i * row), typeof(Row));
                if (r.ifIndex != index || r.mask != 128) continue;
                if (r.dest == 0) low = true;
                else if (r.dest == 128) high = true;
            }
            return low && high;
        } finally { Marshal.FreeHGlobal(buf); }
    }

    static void Watch(Func<int> wait) {
        while (true) {
            int rc = wait();
            Changed.Set();
            if (rc != 0) Thread.Sleep(1000);
        }
    }

    public static void StartWatch() {
        var route = new Thread(() => Watch(() => NotifyRouteChange(IntPtr.Zero, IntPtr.Zero)));
        var addr = new Thread(() => Watch(() => NotifyAddrChange(IntPtr.Zero, IntPtr.Zero)));
        route.IsBackground = true;
        addr.IsBackground = true;
        route.Start();
        addr.Start();
    }

    static string ImagePath(int pid) {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED, false, pid);
        if (h == IntPtr.Zero) return "";
        try {
            var sb = new StringBuilder(1024);
            int n = sb.Capacity;
            if (!QueryFullProcessImageName(h, 0, sb, ref n)) return "";
            return sb.ToString();
        } finally { CloseHandle(h); }
    }

    static List<Proc> Snapshot() {
        var list = new List<Proc>();
        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return list;
        try {
            var pe = new PROCESSENTRY32();
            pe.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));
            if (!Process32First(snap, ref pe)) return list;
            do {
                int id = (int)pe.th32ProcessID;
                if (id <= 4) continue;
                list.Add(new Proc { Id = id, Parent = (int)pe.th32ParentProcessID, Path = ImagePath(id) });
            } while (Process32Next(snap, ref pe));
        } finally { CloseHandle(snap); }
        return list;
    }

    static List<int> Tree(int root, Dictionary<int, List<int>> kids) {
        var ids = new List<int>();
        var stack = new Stack<int>();
        stack.Push(root);
        while (stack.Count > 0) {
            int id = stack.Pop();
            if (ids.Contains(id)) continue;
            ids.Add(id);
            List<int> ch;
            if (kids.TryGetValue(id, out ch)) foreach (int c in ch) stack.Push(c);
        }
        return ids;
    }

    static bool IsSuspended(int pid) {
        try {
            var proc = Process.GetProcessById(pid);
            foreach (ProcessThread t in proc.Threads) {
                if (t.ThreadState == System.Diagnostics.ThreadState.Wait && t.WaitReason == ThreadWaitReason.Suspended) return true;
            }
        } catch { }
        return false;
    }

    static void ReleaseStuck() {
        var snap = Snapshot();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string rule in rules) keys.Add(MatchKey(rule));
        var kids = new Dictionary<int, List<int>>();
        var roots = new List<int>();
        foreach (var p in snap) {
            if (!kids.ContainsKey(p.Parent)) kids[p.Parent] = new List<int>();
            kids[p.Parent].Add(p.Id);
            if (keys.Contains(MatchKey(p.Path))) roots.Add(p.Id);
        }
        int n = 0;
        foreach (int root in roots) {
            foreach (int id in Tree(root, kids)) {
                if (!IsSuspended(id)) continue;
                if (Control(id, false) == 0) n++;
            }
        }
        if (n > 0) Log("отпустил застрявшие процессы: " + n);
    }

    public static void ResumeAll() {
        int[] ids;
        lock (gate) { ids = new int[held.Count]; held.CopyTo(ids); held.Clear(); }
        foreach (int id in ids) Control(id, false);
        if (ids.Length > 0) Log("продолжаю, процессов: " + ids.Length);
    }

    static void SuspendMatches() {
        var snap = Snapshot();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string rule in rules) keys.Add(MatchKey(rule));
        var kids = new Dictionary<int, List<int>>();
        var roots = new List<Proc>();
        foreach (var p in snap) {
            if (!kids.ContainsKey(p.Parent)) kids[p.Parent] = new List<int>();
            kids[p.Parent].Add(p.Id);
            if (keys.Contains(MatchKey(p.Path))) roots.Add(p);
        }
        int self = Process.GetCurrentProcess().Id;
        foreach (var root in roots) {
            int added = 0;
            foreach (int id in Tree(root.Id, kids)) {
                if (id == self) continue;
                bool mine;
                lock (gate) { mine = held.Contains(id); }
                if (mine) continue;
                if (Control(id, true) != 0) continue;
                lock (gate) { held.Add(id); }
                added++;
            }
            if (added > 0) Log("пауза " + Path.GetFileName(root.Path) + ", процессов: " + added);
        }
    }

    static void Loop() {
        bool prev = false;
        try { prev = TunnelUp(tunnel); } catch { prev = false; }
        VpnIsUp = prev;
        if (prev) ReleaseStuck();
        while (running) {
            Changed.WaitOne(400);
            bool up = false;
            try { up = TunnelUp(tunnel); } catch { up = false; }
            if (up != prev) {
                Log(up ? "туннель вернулся" : "туннель пропал");
                prev = up;
                VpnIsUp = up;
            }
            if (!Armed || up) {
                bool any;
                lock (gate) { any = held.Count > 0; }
                if (any) ResumeAll();
            } else SuspendMatches();
        }
    }

    public static void StartEngine(string alias, string[] list) {
        tunnel = string.IsNullOrEmpty(alias) ? "tun2" : alias;
        rules = list ?? new string[0];
        try { VpnIsUp = TunnelUp(tunnel); } catch { VpnIsUp = false; }
        StartWatch();
        var t = new Thread(Loop) { IsBackground = true, Name = "VpnPauseLoop" };
        t.Start();
    }

    public static void SetTunnel(string alias) {
        tunnel = string.IsNullOrEmpty(alias) ? "tun2" : alias.Trim();
        Changed.Set();
    }

    public static void StopEngine() {
        running = false;
        Armed = false;
        Changed.Set();
        ResumeAll();
    }
}
'@ -Language CSharp

function Write-Log([string]$Message) {
    $line = '{0:yyyy-MM-dd HH:mm:ss} {1}' -f (Get-Date), $Message
    Add-Content -Path $LogFile -Value $line -Encoding UTF8
    if ($script:logBox -and $script:logBox.IsHandleCreated) {
        [void]$script:logBox.Items.Add($line)
        while ($script:logBox.Items.Count -gt 300) { $script:logBox.Items.RemoveAt(0) }
        $script:logBox.TopIndex = $script:logBox.Items.Count - 1
    }
}

function Get-AppTitle([string]$Path) {
    if ($Path -match '\\claude-code\\') { return 'Claude Code' }
    if ($Path -match '\\WindowsApps\\Claude_') { return 'Claude' }
    if ($Path -match '\\OpenAI\.Codex_.*\\ChatGPT\.exe$') { return 'ChatGPT' }
    if ($Path -match '\\OpenAI\.Codex_.*\\Codex\.exe$') { return 'Codex' }
    [IO.Path]::GetFileNameWithoutExtension($Path)
}

function Get-MatchKey([string]$Path) {
    [VpnPauseNative]::MatchKey([string]$Path)
}

function Test-SameApp([string]$RunningPath, [string]$RulePath) {
    if (-not $RunningPath -or -not $RulePath) { return $false }
    if ($RunningPath -eq $RulePath) { return $true }
    (Get-MatchKey $RunningPath) -eq (Get-MatchKey $RulePath)
}

function Get-AppRules {
    if (-not (Test-Path $AppsFile)) { return @() }
    @(Get-Content -Path $AppsFile -Encoding UTF8 | ForEach-Object {
        $t = $_.Trim()
        if ($t -and -not $t.StartsWith('#')) { $t }
    })
}

function Save-AppRules([string[]]$Rules) {
    $utf8 = New-Object System.Text.UTF8Encoding $false
    [IO.File]::WriteAllLines($AppsFile, @($Rules), $utf8)
    $script:rules = @($Rules)
}

function Get-ProcessPath($Proc) {
    try { return [string]$Proc.Path } catch { return $null }
}

function Get-TargetProcesses([string[]]$Rules) {
    if (-not $Rules -or $Rules.Count -eq 0) { return @() }
    @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $path = Get-ProcessPath $_
        foreach ($rule in $Rules) {
            if (Test-SameApp $path $rule) { return $true }
        }
        $false
    })
}

function Get-ProcessIndex {
    $kids = @{}
    Get-CimInstance Win32_Process | ForEach-Object {
        $pp = [int]$_.ParentProcessId
        if (-not $kids.ContainsKey($pp)) { $kids[$pp] = New-Object System.Collections.Generic.List[int] }
        $kids[$pp].Add([int]$_.ProcessId)
    }
    $kids
}

function Get-TreePids([int]$RootId, $Kids) {
    $out = New-Object System.Collections.Generic.List[int]
    $stack = New-Object System.Collections.Generic.Stack[int]
    $stack.Push($RootId)
    while ($stack.Count -gt 0) {
        $id = $stack.Pop()
        if ($out.Contains($id)) { continue }
        $out.Add($id)
        if ($Kids.ContainsKey($id)) {
            foreach ($child in $Kids[$id]) { $stack.Push([int]$child) }
        }
    }
    $out
}

function Invoke-Control([int]$ProcessId, [bool]$Suspend) {
    [VpnPauseNative]::Control($ProcessId, $Suspend)
}

if ($SelfTest) {
    $a = [VpnPauseNative]::MatchKey('C:\Program Files\WindowsApps\OpenAI.Codex_26.1120.6357.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe')
    $b = [VpnPauseNative]::MatchKey('C:\Program Files\WindowsApps\OpenAI.Codex_26.1111.2228.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe')
    $c = [VpnPauseNative]::MatchKey('C:\Program Files\WindowsApps\Claude_1.0.0.0_x64__pzs8sxrjxfjjc\claude.exe')
    $d = [VpnPauseNative]::MatchKey('C:\Users\Илья\AppData\Local\Claude-Data\x\claude-code\1\claude.exe')
    Write-Output ("chatgptSame={0} claude={1} code={2}" -f ($a -eq $b), $c, $d)
    $note = Start-Process notepad -PassThru
    Start-Sleep -Milliseconds 400
    $st = Invoke-Control $note.Id $true
    Start-Sleep -Milliseconds 200
    $p = Get-Process -Id $note.Id
    $suspended = @($p.Threads | Where-Object { $_.WaitReason -eq 'Suspended' }).Count
    $st2 = Invoke-Control $note.Id $false
    Start-Sleep -Milliseconds 200
    $p2 = Get-Process -Id $note.Id
    $still = @($p2.Threads | Where-Object { $_.WaitReason -eq 'Suspended' }).Count
    Stop-Process -Id $note.Id -Force
    Write-Output ("suspend={0} suspendedThreads={1} resume={2} stillSuspended={3} tunnel={4}" -f $st, $suspended, $st2, $still, [VpnPauseNative]::TunnelUp($Tunnel))
    exit 0
}

function Get-StoreFamily([string]$Path) {
    if ($Path -match '\\claude-code\\') { return $null }
    if ($Path -notmatch '\\WindowsApps\\([^\\]+)\\') { return $null }
    if ($Matches[1] -match '^(.*)_\d[^_]*_[^_]+__([^_]+)$') { return ($Matches[1] + '_' + $Matches[2]) }
    $null
}

function Sync-WifiBlock {
    $admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $admin) { return $false }
    Get-NetFirewallRule -DisplayName 'VpnPause *' -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
    Get-NetFirewallRule -DisplayName 'chatGPT V' -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
    $families = @{}
    $code = $null
    foreach ($rule in (Get-AppRules)) {
        if ($rule -match '\\claude-code\\') { $code = $rule; continue }
        $fam = Get-StoreFamily $rule
        if ($fam) { $families[$fam] = (Get-AppTitle $rule) }
    }
    $live = @(Get-Process -Name claude -ErrorAction SilentlyContinue | ForEach-Object { Get-ProcessPath $_ } | Where-Object { $_ -match '\\claude-code\\' })
    if ($live.Count -gt 0) { $code = $live[0] }
    foreach ($fam in @($families.Keys)) {
        New-NetFirewallRule -DisplayName ("VpnPause " + $families[$fam]) -Direction Outbound -Action Block -Package $fam -InterfaceType Wireless -Profile Any | Out-Null
    }
    if ($code -and (Test-Path -LiteralPath $code)) {
        New-NetFirewallRule -DisplayName 'VpnPause Claude Code' -Direction Outbound -Action Block -Program $code -InterfaceType Wireless -Profile Any | Out-Null
    }
    $true
}

function Test-WifiRules {
    foreach ($name in @('VpnPause Claude', 'VpnPause ChatGPT', 'VpnPause Claude Code')) {
        if (-not (Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue)) { return $false }
        $kind = Get-NetFirewallRule -DisplayName $name | Get-NetFirewallInterfaceTypeFilter
        if ($kind.InterfaceType -ne 'Wireless') { return $false }
    }
    $prog = @(Get-NetFirewallRule -DisplayName 'VpnPause Claude Code' | Get-NetFirewallApplicationFilter | ForEach-Object { $_.Program })
    $live = @(Get-Process -Name claude -ErrorAction SilentlyContinue | ForEach-Object { Get-ProcessPath $_ } | Where-Object { $_ -match '\\claude-code\\' })
    if ($live.Count -gt 0 -and $prog.Count -gt 0 -and ($live[0] -ne $prog[0])) { return $false }
    $true
}

function Request-WifiSync {
    if ($script:wifiSyncing) { return }
    if (Test-WifiRules) { return }
    $script:wifiSyncing = $true
    $exe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $file = Join-Path $Root 'VpnPause.ps1'
    Start-Process -FilePath $exe -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$file`" -SyncFirewall"
}

if ($SyncFirewall) {
    if (-not (Sync-WifiBlock)) { Write-Error 'нужны права администратора'; exit 1 }
    Write-Log 'правила Wi-Fi обновлены'
    exit 0
}

if ($Stop) {
    $kids = Get-ProcessIndex
    foreach ($p in (Get-TargetProcesses (Get-AppRules))) {
        foreach ($id in (Get-TreePids $p.Id $kids)) { Invoke-Control $id $false | Out-Null }
    }
    Get-CimInstance Win32_Process -Filter "Name = 'powershell.exe'" | Where-Object {
        $_.ProcessId -ne $PID -and $_.CommandLine -match 'VpnPause\.ps1' -and $_.CommandLine -notmatch '-Stop'
    } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Get-Process -Name VpnPause -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $PID } | Stop-Process -Force
    Write-Log 'остановлен'
    exit 0
}

if ([Diagnostics.Process]::GetCurrentProcess().ProcessName -ne 'VpnPause' -and [System.Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') {
    $ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    Start-Process -FilePath $ps -ArgumentList "-NoProfile -STA -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$($MyInvocation.MyCommand.Path)`""
    exit 0
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$mutex = New-Object System.Threading.Mutex($false, 'Local\VpnPause')
if (-not $mutex.WaitOne(0)) { exit 0 }

$script:held = @{}
$script:vpnUp = $false
$script:armed = $true
$script:quitting = $false

function New-DotIcon([System.Drawing.Color]$Color) {
    $bmp = New-Object System.Drawing.Bitmap 32, 32
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $brush = New-Object System.Drawing.SolidBrush $Color
    $g.FillEllipse($brush, 2, 2, 28, 28)
    $g.Dispose()
    $brush.Dispose()
    [System.Drawing.Icon]::FromHandle($bmp.GetHicon())
}

$iconOn = New-DotIcon ([System.Drawing.Color]::FromArgb(46, 160, 67))
$iconOff = New-DotIcon ([System.Drawing.Color]::FromArgb(210, 70, 60))
$iconIdle = New-DotIcon ([System.Drawing.Color]::FromArgb(140, 140, 140))

$form = New-Object System.Windows.Forms.Form
$form.Text = 'VpnPause'
$form.ClientSize = New-Object System.Drawing.Size 760, 600
$form.StartPosition = 'CenterScreen'
$form.MinimumSize = New-Object System.Drawing.Size 640, 520
$form.Font = New-Object System.Drawing.Font 'Segoe UI', 10
$icoPath = Join-Path $Root 'VpnPause.ico'
if (Test-Path -LiteralPath $icoPath) {
    try { $form.Icon = New-Object System.Drawing.Icon -ArgumentList $icoPath } catch { }
}

$tip = New-Object System.Windows.Forms.ToolTip
$tip.AutoPopDelay = 12000
$tip.InitialDelay = 350
$tip.ReshowDelay = 200
$tip.ShowAlways = $true

$status = New-Object System.Windows.Forms.Label
$status.Location = New-Object System.Drawing.Point 16, 12
$status.Size = New-Object System.Drawing.Size 728, 36
$status.Anchor = 'Top,Left,Right'
$status.Text = 'Запуск…'
$form.Controls.Add($status)
$tip.SetToolTip($status, "Зелёный — туннель Amnezia есть, программы работают.`nКрасный — туннеля нет, выбранные программы заморожены.`nСерый — слежение выключено кнопкой Стоп.")

$btnStart = New-Object System.Windows.Forms.Button
$btnStart.Text = 'Старт'
$btnStart.Location = New-Object System.Drawing.Point 16, 54
$btnStart.Size = New-Object System.Drawing.Size 90, 30
$form.Controls.Add($btnStart)
$tip.SetToolTip($btnStart, 'Включить слежение. Если туннеля сейчас нет, программы из списка сразу встанут на паузу.')

$btnStop = New-Object System.Windows.Forms.Button
$btnStop.Text = 'Стоп'
$btnStop.Location = New-Object System.Drawing.Point 112, 54
$btnStop.Size = New-Object System.Drawing.Size 90, 30
$form.Controls.Add($btnStop)
$tip.SetToolTip($btnStop, 'Выключить слежение и сразу отпустить всё, что сейчас заморожено.')

$btnRunning = New-Object System.Windows.Forms.Button
$btnRunning.Text = 'Из запущенных…'
$btnRunning.Location = New-Object System.Drawing.Point 214, 54
$btnRunning.Size = New-Object System.Drawing.Size 150, 30
$form.Controls.Add($btnRunning)
$tip.SetToolTip($btnRunning, 'Выбрать программу, которая уже запущена. В список попадёт её полный путь, а не только имя файла.')

$btnBrowse = New-Object System.Windows.Forms.Button
$btnBrowse.Text = 'Обзор…'
$btnBrowse.Location = New-Object System.Drawing.Point 370, 54
$btnBrowse.Size = New-Object System.Drawing.Size 100, 30
$form.Controls.Add($btnBrowse)
$tip.SetToolTip($btnBrowse, 'Указать exe-файл на диске, даже если программа сейчас закрыта.')

$btnRemove = New-Object System.Windows.Forms.Button
$btnRemove.Text = 'Удалить'
$btnRemove.Location = New-Object System.Drawing.Point 476, 54
$btnRemove.Size = New-Object System.Drawing.Size 100, 30
$form.Controls.Add($btnRemove)
$tip.SetToolTip($btnRemove, 'Убрать выбранные строки из списка. Если программа была на паузе, она продолжится.')

$lblTunnel = New-Object System.Windows.Forms.Label
$lblTunnel.Text = 'Адаптер'
$lblTunnel.Location = New-Object System.Drawing.Point 16, 96
$lblTunnel.Size = New-Object System.Drawing.Size 72, 24
$form.Controls.Add($lblTunnel)

$tunnelBox = New-Object System.Windows.Forms.ComboBox
$tunnelBox.Location = New-Object System.Drawing.Point 92, 93
$tunnelBox.Size = New-Object System.Drawing.Size 220, 26
$tunnelBox.DropDownStyle = 'DropDown'
try {
    foreach ($name in @(Get-NetAdapter -ErrorAction Stop | Select-Object -ExpandProperty Name)) {
        if ($name) { [void]$tunnelBox.Items.Add($name) }
    }
} catch { }
if ($tunnelBox.Items -notcontains $Tunnel) { [void]$tunnelBox.Items.Insert(0, $Tunnel) }
$tunnelBox.Text = $Tunnel
$form.Controls.Add($tunnelBox)
$tip.SetToolTip($tunnelBox, "Имя адаптера, на котором Amnezia поднимает туннель. Сейчас это tun2.`nАдрес сервера VPN сюда не вписывается: сторож смотрит только маршруты на этом адаптере.`nСменился сервер — ничего менять не надо. Другая программа VPN с другим именем адаптера — выбери его и нажми Запомнить.")

$btnTunnel = New-Object System.Windows.Forms.Button
$btnTunnel.Text = 'Запомнить'
$btnTunnel.Location = New-Object System.Drawing.Point 322, 91
$btnTunnel.Size = New-Object System.Drawing.Size 110, 28
$form.Controls.Add($btnTunnel)
$tip.SetToolTip($btnTunnel, 'Сохранить имя адаптера. Слежение сразу переключится на него, перезапуск не нужен.')

$lblTunnelHint = New-Object System.Windows.Forms.Label
$lblTunnelHint.Text = 'Сервер VPN здесь не нужен'
$lblTunnelHint.Location = New-Object System.Drawing.Point 444, 96
$lblTunnelHint.Size = New-Object System.Drawing.Size 290, 24
$lblTunnelHint.ForeColor = [System.Drawing.Color]::FromArgb(90, 90, 90)
$form.Controls.Add($lblTunnelHint)
$tip.SetToolTip($lblTunnelHint, 'Смена IP сервера в Amnezia эту программу не касается. Нужно только имя адаптера, если VPN больше не поднимает tun2.')

$apps = New-Object System.Windows.Forms.ListView
$apps.Location = New-Object System.Drawing.Point 16, 132
$apps.Size = New-Object System.Drawing.Size 728, 190
$apps.Anchor = 'Top,Left,Right'
$apps.View = 'Details'
$apps.FullRowSelect = $true
$apps.GridLines = $true
$apps.MultiSelect = $true
$apps.ShowItemToolTips = $true
[void]$apps.Columns.Add('Программа', 130)
[void]$apps.Columns.Add('Путь', 470)
[void]$apps.Columns.Add('Сейчас', 110)
$form.Controls.Add($apps)
$tip.SetToolTip($apps, "Эти программы замирают вместе со всем, что они запустили, когда пропадает VPN.`nНовая версия из Microsoft Store подхватывается сама: смотрится семейство, а не номер сборки.")

$logLabel = New-Object System.Windows.Forms.Label
$logLabel.Text = 'Журнал'
$logLabel.Location = New-Object System.Drawing.Point 16, 332
$logLabel.Size = New-Object System.Drawing.Size 200, 22
$logLabel.Anchor = 'Top,Left'
$form.Controls.Add($logLabel)
$tip.SetToolTip($logLabel, 'Когда туннель пропал, кого поставили на паузу и когда отпустили.')

$logBox = New-Object System.Windows.Forms.ListBox
$logBox.Location = New-Object System.Drawing.Point 16, 356
$logBox.Size = New-Object System.Drawing.Size 728, 228
$logBox.Anchor = 'Top,Bottom,Left,Right'
$logBox.Font = New-Object System.Drawing.Font 'Consolas', 9
$logBox.HorizontalScrollbar = $true
$form.Controls.Add($logBox)
$script:logBox = $logBox
$tip.SetToolTip($logBox, 'Последние события сторожа. Файл журнала лежит рядом с программой: vpn-pause.log.')

if (Test-Path $LogFile) {
    Get-Content -Path $LogFile -Tail 80 -Encoding UTF8 | ForEach-Object { [void]$logBox.Items.Add($_) }
    if ($logBox.Items.Count -gt 0) { $logBox.TopIndex = $logBox.Items.Count - 1 }
}

function Resume-Held { [VpnPauseNative]::ResumeAll() }

function Add-Rule([string]$Path) {
    if (-not $Path) { return }
    $exists = $false
    foreach ($rule in $script:rules) {
        if (Test-SameApp $Path $rule) { $exists = $true }
    }
    if ($exists) { return }
    $script:rules = @($script:rules) + $Path
    Save-AppRules $script:rules
    [VpnPauseNative]::SetRules([string[]]$script:rules)
    Write-Log ("добавил {0}" -f (Get-AppTitle $Path))
    Rebuild-AppList
    Request-WifiSync
}

function Remove-SelectedRules {
    $drop = @{}
    foreach ($item in @($apps.SelectedItems)) { $drop[[string]$item.Tag] = $true }
    if ($drop.Count -eq 0) { return }
    $kept = @($script:rules | Where-Object { -not $drop.ContainsKey($_) })
    Save-AppRules $kept
    [VpnPauseNative]::SetRules([string[]]$script:rules)
    [VpnPauseNative]::ResumeAll()
    Write-Log 'убрал программу из списка'
    Rebuild-AppList
    Request-WifiSync
}

function Rebuild-AppList {
    $apps.BeginUpdate()
    $apps.Items.Clear()
    foreach ($rule in $script:rules) {
        $item = New-Object System.Windows.Forms.ListViewItem (Get-AppTitle $rule)
        [void]$item.SubItems.Add($rule)
        [void]$item.SubItems.Add('…')
        $item.Tag = $rule
        $item.ToolTipText = $rule
        [void]$apps.Items.Add($item)
    }
    $apps.EndUpdate()
    Update-AppStates
}

function Update-AppStates {
    $running = @(Get-Process -ErrorAction SilentlyContinue)
    foreach ($item in @($apps.Items)) {
        $rule = [string]$item.Tag
        $hit = $false
        $paused = $false
        foreach ($p in $running) {
            if (-not (Test-SameApp (Get-ProcessPath $p) $rule)) { continue }
            $hit = $true
            if ([VpnPauseNative]::IsHeld($p.Id)) { $paused = $true }
        }
        $item.SubItems[2].Text = if ($paused) { 'на паузе' } elseif ($hit) { 'работает' } else { 'не запущена' }
    }
}

function Show-RunningPicker {
    $dlg = New-Object System.Windows.Forms.Form
    $dlg.Text = 'Запущенные программы'
    $dlg.StartPosition = 'CenterParent'
    $dlg.ClientSize = New-Object System.Drawing.Size 720, 460
    $dlg.Font = $form.Font
    $hint = New-Object System.Windows.Forms.Label
    $hint.Text = 'Отметь программы. В список попадёт полный путь к exe.'
    $hint.Location = New-Object System.Drawing.Point 12, 10
    $hint.Size = New-Object System.Drawing.Size 690, 22
    $dlg.Controls.Add($hint)
    $filter = New-Object System.Windows.Forms.TextBox
    $filter.Location = New-Object System.Drawing.Point 12, 36
    $filter.Size = New-Object System.Drawing.Size 696, 24
    $dlg.Controls.Add($filter)
    $list = New-Object System.Windows.Forms.ListView
    $list.Location = New-Object System.Drawing.Point 12, 68
    $list.Size = New-Object System.Drawing.Size 696, 340
    $list.View = 'Details'
    $list.FullRowSelect = $true
    $list.CheckBoxes = $true
    $list.GridLines = $true
    [void]$list.Columns.Add('Программа', 160)
    [void]$list.Columns.Add('Окно', 160)
    [void]$list.Columns.Add('Путь', 360)
    $dlg.Controls.Add($list)
    $rows = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { Get-ProcessPath $_ } | Group-Object { Get-ProcessPath $_ } | ForEach-Object {
        $p = $_.Group | Select-Object -First 1
        [PSCustomObject]@{ Name = $p.ProcessName; Title = (($_.Group | Where-Object MainWindowTitle | Select-Object -First 1).MainWindowTitle); Path = (Get-ProcessPath $p) }
    } | Sort-Object Name)
    $fill = {
        $list.Items.Clear()
        $q = $filter.Text.Trim()
        foreach ($row in $rows) {
            if ($q -and ($row.Name -notlike "*$q*") -and ($row.Path -notlike "*$q*") -and ($row.Title -notlike "*$q*")) { continue }
            $item = New-Object System.Windows.Forms.ListViewItem $row.Name
            [void]$item.SubItems.Add([string]$row.Title)
            [void]$item.SubItems.Add($row.Path)
            $item.Tag = $row.Path
            [void]$list.Items.Add($item)
        }
    }
    & $fill
    $filter.Add_TextChanged($fill)
    $ok = New-Object System.Windows.Forms.Button
    $ok.Text = 'Добавить'
    $ok.Location = New-Object System.Drawing.Point 500, 418
    $ok.Size = New-Object System.Drawing.Size 100, 30
    $ok.DialogResult = 'OK'
    $cancel = New-Object System.Windows.Forms.Button
    $cancel.Text = 'Отмена'
    $cancel.Location = New-Object System.Drawing.Point 608, 418
    $cancel.Size = New-Object System.Drawing.Size 100, 30
    $cancel.DialogResult = 'Cancel'
    $dlg.Controls.Add($ok)
    $dlg.Controls.Add($cancel)
    $dlg.AcceptButton = $ok
    $dlg.CancelButton = $cancel
    $tip.SetToolTip($filter, 'Фильтр по имени, заголовку окна или пути.')
    if ($dlg.ShowDialog($form) -eq 'OK') {
        foreach ($item in @($list.CheckedItems)) { Add-Rule ([string]$item.Tag) }
    }
    $dlg.Dispose()
}

function Update-Tray {
    $armed = [VpnPauseNative]::Armed
    $ni.Icon = if (-not $armed) { $iconIdle } elseif ($script:vpnUp) { $iconOn } else { $iconOff }
    $state = if (-not $armed) { 'на стопе' } elseif ($script:vpnUp) { 'туннель есть' } else { 'туннель пропал' }
    $paused = [VpnPauseNative]::PausedCount
    $tray = "VpnPause — $state, на паузе: $paused"
    if ($tray.Length -gt 63) { $tray = $tray.Substring(0, 63) }
    $ni.Text = $tray
    $status.Text = "$state   ·   в списке: $($script:rules.Count)   ·   на паузе: $paused"
    $status.ForeColor = if (-not $armed) { [System.Drawing.Color]::DimGray } elseif ($script:vpnUp) { [System.Drawing.Color]::FromArgb(30, 120, 50) } else { [System.Drawing.Color]::FromArgb(180, 40, 30) }
    $btnStart.Enabled = -not $armed
    $btnStop.Enabled = $armed
}

function Update-Watch {
    try {
        foreach ($msg in [VpnPauseNative]::DrainLogs()) {
            Write-Log $msg
            if ($msg -eq 'туннель пропал' -or $msg -eq 'туннель вернулся') {
                $ni.ShowBalloonTip(3000, 'VpnPause', $msg, $(if ($msg -eq 'туннель вернулся') { 'Info' } else { 'Warning' }))
            }
        }
        $script:vpnUp = [VpnPauseNative]::VpnIsUp
        Update-Tray
        $script:uiTick++
        if ($form.Visible -and ($script:uiTick % 3 -eq 0)) { Update-AppStates }
    } catch {
        Write-Log ("ошибка: {0}" -f $_.Exception.Message)
    }
}

$ni = New-Object System.Windows.Forms.NotifyIcon
$ni.Icon = $iconIdle
$ni.Visible = $true
$ni.Text = 'VpnPause'

$menu = New-Object System.Windows.Forms.ContextMenuStrip
$miShow = $menu.Items.Add('Открыть')
$miStart = $menu.Items.Add('Старт')
$miStop = $menu.Items.Add('Стоп')
[void]$menu.Items.Add('-')
$miExit = $menu.Items.Add('Выход')
$ni.ContextMenuStrip = $menu

$ni.Add_Click({
    if ($_.Button -eq [System.Windows.Forms.MouseButtons]::Left) { $form.Show(); $form.Activate() }
})
$miShow.Add_Click({ $form.Show(); $form.Activate() })
$miStart.ToolTipText = 'Включить слежение за туннелем.'
$miStop.ToolTipText = 'Выключить слежение и отпустить замороженные программы.'
$btnStart.Add_Click({ [VpnPauseNative]::SetArmed($true); Write-Log 'старт'; Update-Watch })
$miStart.Add_Click({ [VpnPauseNative]::SetArmed($true); Write-Log 'старт'; Update-Watch })
$stopAction = {
    [VpnPauseNative]::SetArmed($false)
    Resume-Held
    Write-Log 'стоп'
    Update-Watch
    Update-AppStates
}
$btnStop.Add_Click($stopAction)
$miStop.Add_Click($stopAction)
$btnRunning.Add_Click({ Show-RunningPicker })
$btnBrowse.Add_Click({
    $dlg = New-Object System.Windows.Forms.OpenFileDialog
    $dlg.Filter = 'Программы (*.exe)|*.exe'
    $dlg.Multiselect = $true
    $dlg.Title = 'Какие программы ставить на паузу без VPN'
    if ($dlg.ShowDialog($form) -eq 'OK') {
        foreach ($path in $dlg.FileNames) { Add-Rule $path }
    }
})
$btnRemove.Add_Click({ Remove-SelectedRules })
$btnTunnel.Add_Click({
    $name = $tunnelBox.Text.Trim()
    if (-not $name) { return }
    Set-Content -LiteralPath $SettingsFile -Value ("tunnel=" + $name) -Encoding UTF8
    [VpnPauseNative]::SetTunnel($name)
    Write-Log ("адаптер: {0}" -f $name)
})
$quit = {
    $script:quitting = $true
    [VpnPauseNative]::StopEngine()
    Write-Log 'выход'
    $timer.Stop()
    $ni.Visible = $false
    $form.Close()
}
$miExit.Add_Click($quit)
$form.Add_FormClosing({
    if (-not $script:quitting) {
        $_.Cancel = $true
        $form.Hide()
    }
})

$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 400
$timer.Add_Tick({ Update-Watch })

$script:rules = @(Get-AppRules)
$script:uiTick = 0
$script:vpnUp = [VpnPauseNative]::TunnelUp($Tunnel)
Rebuild-AppList
Write-Log ("старт, туннель {0}, программ в списке: {1}" -f $(if ($script:vpnUp) { 'есть' } else { 'нет' }), $script:rules.Count)
Update-Tray
[VpnPauseNative]::StartEngine($Tunnel, [string[]]$script:rules)
$timer.Start()
$form.Show()
Request-WifiSync
try {
    [System.Windows.Forms.Application]::Run($form)
} finally {
    [VpnPauseNative]::StopEngine()
    $ni.Visible = $false
    $ni.Dispose()
    $mutex.ReleaseMutex() | Out-Null
}
