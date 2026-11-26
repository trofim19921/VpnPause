# Full elevated audit of the installed VpnPause. Restores config.json and .bak at the end.
param([Parameter(Mandatory)][string]$Report)
$ErrorActionPreference = 'Continue'
$t = Join-Path $PSScriptRoot 'pipe-test.ps1'
$exe = 'C:\Program Files\VpnPause\VpnPause.exe'
$data = 'C:\ProgramData\VpnPause'
$work = 'C:\Users\Public\vpnpause-audit'
$log = "$data\logs\service.log"
New-Item -ItemType Directory $work -Force | Out-Null
Copy-Item "$data\config.json" "$work\config.saved" -Force
Copy-Item "$data\config.json.bak" "$work\config.bak.saved" -Force
$curl = "$work\curl.exe"
Copy-Item "$env:WINDIR\System32\curl.exe" $curl -Force
Remove-Item $Report -ErrorAction SilentlyContinue

function Out($s) { $s | Out-File -Append -Encoding utf8 $Report }
function Net { $r = & $curl -s -m 6 https://api.ipify.org; if ($r) { "online $r" } else { 'blocked' } }
function Ours { netsh wfp show filters file="$work\f.xml" | Out-Null; ([regex]::Matches((Get-Content "$work\f.xml" -Raw), '<name>VpnPause')).Count }
function Status { (& $t -Json '{"cmd":"status"}' | ConvertFrom-Json).status }
function Send($cfg) { & $t -Json (@{ cmd = 'setConfig'; config = $cfg } | ConvertTo-Json -Depth 8 -Compress) | ConvertFrom-Json }
function LogSince($mark) { Get-Content $log | Where-Object { $_ -gt $mark } }
function Mark { Get-Date -Format 'yyyy-MM-dd HH:mm:ss' }

Add-Type -Namespace A -Name N -MemberDefinition @'
[DllImport("ntdll.dll")] public static extern int NtSuspendProcess(IntPtr h);
[DllImport("ntdll.dll")] public static extern int NtResumeProcess(IntPtr h);
[DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint a, bool i, int p);
[DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
'@

$orig = (& $t -Json '{"cmd":"getConfig"}' | ConvertFrom-Json).config
$s = Status
Out "== 1. baseline"
Out "version=$($s.version) enabled=$($s.enabled) strict=$($s.strict) vpnUp=$($s.vpnUp) adapter=$($s.adapterName) dnsOutside=$($s.dnsOutside) filters=$($s.filterCount) wfp=$(Ours)"
Out ("targets: " + (($s.targets | ForEach-Object { "$($_.name):$($_.files)$(if($_.package){'+pkg'})" }) -join ', '))
Out ("depends: " + ((sc.exe qc VpnPauseService | Select-String 'DEPENDENCIES').Line.Trim()))
Out ("failure actions: " + ((sc.exe qfailure VpnPauseService | Select-String 'RESTART|ПЕРЕЗАП' | Select-Object -First 1).Line.Trim()))

Out "== 2. real app traffic (Cursor, Claude Code)"
$vpnIps = Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.InterfaceAlias -match 'Amnezia|tun|Подключение по локальной сети 2' } | ForEach-Object IPAddress
$phys = Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -like '192.168.*' } | ForEach-Object IPAddress
foreach ($n in 'Cursor', 'claude', 'ChatGPT') {
    $pids = (Get-Process -Name $n -ErrorAction SilentlyContinue).Id
    if (-not $pids) { Out "$n`: not running"; continue }
    $c = Get-NetTCPConnection -State Established -ErrorAction SilentlyContinue | Where-Object { $pids -contains $_.OwningProcess -and $_.RemoteAddress -notmatch '^(127\.|::1|0\.)' }
    $viaVpn = ($c | Where-Object { $vpnIps -contains $_.LocalAddress }).Count
    $viaPhys = ($c | Where-Object { $phys -contains $_.LocalAddress }).Count
    Out "$n`: established external=$($c.Count) via VPN=$viaVpn via physical=$viaPhys"
}

Out "== 3. strict mode, real VPN, external removal"
$cfg = $orig | ConvertTo-Json -Depth 8 | ConvertFrom-Json
$cfg.apps = @(@{ name = 'audit curl'; path = $curl; scope = 'exe' })
$cfg.autoAdapter = $true; $cfg.adapter = $null; $cfg.strictMode = $true
$null = Send $cfg
Start-Sleep -Seconds 3
Out "strict, VPN up: curl $(Net), filters=$((Status).filterCount), wfp=$(Ours)"
$m = Mark
& $exe --remove-filters | Out-Null
Start-Sleep -Milliseconds 500
Out "right after --remove-filters: wfp=$(Ours), curl $(Net)"
Start-Sleep -Seconds 10
Out "10 s later: wfp=$(Ours), curl $(Net)"
Out ("log: " + ((LogSince $m) -join ' / '))

Out "== 4. no VPN adapter (fake adapter chosen)"
$cfg.autoAdapter = $false
$cfg.adapter = @{ id = '{00000000-0000-0000-0000-000000000001}'; name = 'missing-vpn' }
$null = Send $cfg
Start-Sleep -Seconds 3
Out "strict, no VPN: curl $(Net)"
sc.exe stop VpnPauseService | Out-Null; Start-Sleep -Seconds 4
Out "strict, service stopped: curl $(Net), wfp=$(Ours)"
sc.exe start VpnPauseService | Out-Null; Start-Sleep -Seconds 5
$cfg.strictMode = $false
$null = Send $cfg
Start-Sleep -Seconds 3
Out "normal, no VPN: curl $(Net)"
sc.exe stop VpnPauseService | Out-Null; Start-Sleep -Seconds 4
Out "normal, service stopped: curl $(Net), wfp=$(Ours)"
sc.exe start VpnPauseService | Out-Null; Start-Sleep -Seconds 5
$cfg.allowLan = $true; $cfg.strictMode = $true
$null = Send $cfg
Start-Sleep -Seconds 3
$gw = (Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Sort-Object RouteMetric | Select-Object -First 1).NextHop
if (-not $gw) { $gw = '192.168.0.1' }
$lan = & $curl -s -m 4 -o NUL -w '%{http_code}' "http://$gw/"
Out "allowLan, no VPN: router http=$lan ($gw), internet $(Net)"
$cfg.allowLan = $false
$null = Send $cfg
Start-Sleep -Seconds 3
$lan = & $curl -s -m 4 -o NUL -w '%{http_code}' "http://$gw/"
Out "no LAN, no VPN: router http=$lan"

Out "== 5. new exe appears in a watched folder"
New-Item -ItemType Directory "$work\app\v1" -Force | Out-Null
Copy-Item $curl "$work\app\v1\tool.exe" -Force
$cfg.apps = @(@{ name = 'audit folder'; path = "$work\app\v1\tool.exe"; scope = 'folder' })
$null = Send $cfg
Start-Sleep -Seconds 3
New-Item -ItemType Directory "$work\app\v2" -Force | Out-Null
Copy-Item $curl "$work\app\v2\tool.exe" -Force
Start-Sleep -Seconds 3
$r = & "$work\app\v2\tool.exe" -s -m 5 https://api.ipify.org
Out "new version v2\tool.exe, first run: $(if ($r) { "online $r (LEAK)" } else { 'blocked' })"
$m = Mark
New-Item -ItemType Directory "$work\app\v3" -Force | Out-Null
Copy-Item $curl "$work\app\v3\tool.exe" -Force
$r = & "$work\app\v3\tool.exe" -s -m 5 https://api.ipify.org
Out "v3 started immediately after copy: $(if ($r) { "online $r (LEAK)" } else { 'blocked' })"

Out "== 6. orphaned freeze"
Copy-Item "$env:WINDIR\System32\PING.EXE" "$work\app\v1\pinger.exe" -Force
$cfg.freeze.enabled = $true
$cfg.autoAdapter = $true; $cfg.adapter = $null
$null = Send $cfg
Start-Sleep -Seconds 3
$p = Start-Process "$work\app\v1\pinger.exe" -ArgumentList '-t 127.0.0.1' -WindowStyle Hidden -PassThru
Start-Sleep -Seconds 1
$h = [A.N]::OpenProcess(0x0800, $false, $p.Id); $null = [A.N]::NtSuspendProcess($h)
function Frozen($id) { $q = Get-Process -Id $id; @($q.Threads | Where-Object { $_.WaitReason -ne 'Suspended' }).Count -eq 0 }
Out "suspended by 'crashed app': frozen=$(Frozen $p.Id)"
$sw = [Diagnostics.Stopwatch]::StartNew()
while ((Frozen $p.Id) -and $sw.Elapsed.TotalSeconds -lt 40) { Start-Sleep -Seconds 1 }
Out "service released it: frozen=$(Frozen $p.Id) after $([int]$sw.Elapsed.TotalSeconds) s"
& $t -Json '{"cmd":"freezeLease","enabled":true}' | Out-Null
$null = [A.N]::NtSuspendProcess($h)
Start-Sleep -Seconds 20
Out "with live lease (renewed once, 20 s): frozen=$(Frozen $p.Id) (lease 15 s, expected released after expiry)"
$null = [A.N]::NtResumeProcess($h); [A.N]::CloseHandle($h) | Out-Null
Stop-Process -Id $p.Id -Force

Out "== 7. damaged config"
sc.exe stop VpnPauseService | Out-Null; Start-Sleep -Seconds 3
Copy-Item "$work\config.saved" "$data\config.json.bak" -Force
Set-Content "$data\config.json" '{ broken' -NoNewline
$m = Mark
sc.exe start VpnPauseService | Out-Null; Start-Sleep -Seconds 5
$s = Status
Out "config.json broken: apps=$(((& $t -Json '{"cmd":"getConfig"}' | ConvertFrom-Json).config.apps).Count) filters=$($s.filterCount) configError=$($s.configError)"
Out ("log: " + ((LogSince $m) -join ' / '))
sc.exe stop VpnPauseService | Out-Null; Start-Sleep -Seconds 3
Set-Content "$data\config.json" 'garbage' -NoNewline
Set-Content "$data\config.json.bak" 'garbage' -NoNewline
$m = Mark
sc.exe start VpnPauseService | Out-Null; Start-Sleep -Seconds 5
$s = Status
Out "both broken: filters=$($s.filterCount) wfp=$(Ours) configError=$($s.configError)"
Out ("log: " + ((LogSince $m) -join ' / '))

Out "== 8. pipe robustness"
& (Join-Path $PSScriptRoot 'audit-pipe.ps1') 2>&1 | ForEach-Object { Out $_ }

Out "== 9. data folder"
Out ((icacls $data | Select-Object -First 3) -join ' ; ')
Out ("owner: " + (Get-Acl $data).Owner)

Out "== restore"
sc.exe stop VpnPauseService | Out-Null; Start-Sleep -Seconds 3
Copy-Item "$work\config.saved" "$data\config.json" -Force
Copy-Item "$work\config.bak.saved" "$data\config.json.bak" -Force
sc.exe start VpnPauseService | Out-Null; Start-Sleep -Seconds 5
$s = Status
Out "restored: strict=$($s.strict) filters=$($s.filterCount) vpnUp=$($s.vpnUp) apps=$((($orig.apps) | ForEach-Object name) -join ', ') wfp=$(Ours)"
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
