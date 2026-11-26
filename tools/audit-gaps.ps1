# Elevated: lease keeps a freeze, lease loss releases it; leak window after external removal with no VPN. Restores the config.
param([Parameter(Mandatory)][string]$Report)
$ErrorActionPreference = 'Continue'
$t = Join-Path $PSScriptRoot 'pipe-test.ps1'
$exe = 'C:\Program Files\VpnPause\VpnPause.exe'
$work = 'C:\Users\Public\vpnpause-audit'
New-Item -ItemType Directory "$work\app" -Force | Out-Null
$curl = "$work\app\tool.exe"
Copy-Item "$env:WINDIR\System32\curl.exe" $curl -Force
Copy-Item "$env:WINDIR\System32\PING.EXE" "$work\app\pinger.exe" -Force
Remove-Item $Report -ErrorAction SilentlyContinue
function Out($s) { $s | Out-File -Append -Encoding utf8 $Report }
function Send($cfg) { & $t -Json (@{ cmd = 'setConfig'; config = $cfg } | ConvertTo-Json -Depth 8 -Compress) | Out-Null }
function Lease($on) { & $t -Json "{`"cmd`":`"freezeLease`",`"enabled`":$($on.ToString().ToLower())}" | Out-Null }
Add-Type -Namespace A -Name N -MemberDefinition @'
[DllImport("ntdll.dll")] public static extern int NtSuspendProcess(IntPtr h);
[DllImport("ntdll.dll")] public static extern int NtResumeProcess(IntPtr h);
[DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint a, bool i, int p);
'@
function Frozen($id) { @((Get-Process -Id $id).Threads | Where-Object { $_.WaitReason -ne 'Suspended' }).Count -eq 0 }

$orig = (& $t -Json '{"cmd":"getConfig"}' | ConvertFrom-Json).config
$cfg = $orig | ConvertTo-Json -Depth 8 | ConvertFrom-Json
$cfg.apps = @(@{ name = 'audit'; path = $curl; scope = 'folder' })
$cfg.freeze.enabled = $true
$cfg.freeze.maxSeconds = 120
Send $cfg
Start-Sleep -Seconds 3

Out "== lease"
$p = Start-Process "$work\app\pinger.exe" -ArgumentList '-t 127.0.0.1' -WindowStyle Hidden -PassThru
Start-Sleep -Seconds 1
$h = [A.N]::OpenProcess(0x0800, $false, $p.Id)
Lease $true
$null = [A.N]::NtSuspendProcess($h)
$kept = $true
for ($i = 0; $i -lt 7; $i++) { Start-Sleep -Seconds 5; Lease $true; if (-not (Frozen $p.Id)) { $kept = $false; break } }
Out "lease renewed every 5 s for 35 s: still frozen=$kept"
$sw = [Diagnostics.Stopwatch]::StartNew()
while ((Frozen $p.Id) -and $sw.Elapsed.TotalSeconds -lt 60) { Start-Sleep -Seconds 1 }
Out "renewals stopped: released after $([int]$sw.Elapsed.TotalSeconds) s (lease 15 s + check every 15 s)"
Lease $true
$null = [A.N]::NtSuspendProcess($h)
Lease $false
$sw.Restart()
while ((Frozen $p.Id) -and $sw.Elapsed.TotalSeconds -lt 60) { Start-Sleep -Seconds 1 }
Out "lease dropped explicitly: released after $([int]$sw.Elapsed.TotalSeconds) s"
$null = [A.N]::NtResumeProcess($h)
Stop-Process -Id $p.Id -Force

Out "== leak window after external removal, VPN missing"
$cfg.freeze.enabled = $false
$cfg.autoAdapter = $false
$cfg.adapter = @{ id = '{00000000-0000-0000-0000-000000000001}'; name = 'missing-vpn' }
foreach ($strict in $true, $false) {
    $cfg.strictMode = $strict
    Send $cfg
    Start-Sleep -Seconds 3
    $before = & $curl -s -m 4 https://api.ipify.org
    & $exe --remove-filters | Out-Null
    $sw.Restart(); $open = 0; $tries = 0
    while ($sw.Elapsed.TotalSeconds -lt 12) {
        $tries++
        $r = & $curl -s -m 2 https://api.ipify.org
        if ($r) { $open++; $last = $sw.Elapsed.TotalSeconds }
        Start-Sleep -Milliseconds 300
    }
    $win = if ($open) { "up to {0:N1} s ($r)" -f $last } else { 'none' }
    Out "strict=$strict before=$(if ($before) { 'online' } else { 'blocked' }) after removal: $open of $tries probes got out, window $win"
}

Send $orig
Start-Sleep -Seconds 3
Out "restored apps: $((($orig.apps) | ForEach-Object name) -join ', '), strict=$($orig.strictMode)"
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
