# Elevated checks: external filter removal, strict -> normal switch, service stop. Writes a report.
param([Parameter(Mandatory)][string]$Report)
$ErrorActionPreference = 'Continue'
$t = Join-Path $PSScriptRoot 'pipe-test.ps1'
$exe = 'C:\Program Files\VpnPause\VpnPause.exe'
$curl = 'C:\Users\Public\vpnpause-audit\curl.exe'
New-Item -ItemType Directory (Split-Path $curl) -Force | Out-Null
Copy-Item "$env:WINDIR\System32\curl.exe" $curl -Force
function Out($s) { $s | Out-File -Append -Encoding utf8 $Report }
function Net { $r = & $curl -s -m 6 https://api.ipify.org; if ($r) { "online $r" } else { 'blocked' } }
function Ours { netsh wfp show filters file=C:\Users\Public\vpnpause-audit\f.xml | Out-Null; ([regex]::Matches((Get-Content C:\Users\Public\vpnpause-audit\f.xml -Raw), 'VpnPause')).Count }
function Status { (& $t -Json '{"cmd":"status"}' | ConvertFrom-Json).status }

$orig = (& $t -Json '{"cmd":"getConfig"}' | ConvertFrom-Json).config
Out "start: filters=$((Status).filterCount) ours-in-wfp=$(Ours)"

& $exe --remove-filters | Out-Null
Start-Sleep -Seconds 3
Out "after external removal: service says $((Status).filterCount), wfp has $(Ours)"
Start-Sleep -Seconds 10
Out "10 s later: service says $((Status).filterCount), wfp has $(Ours)"

$cfg = $orig | ConvertTo-Json -Depth 8 | ConvertFrom-Json
$cfg.apps = @(@{ name = 'audit curl'; path = $curl; scope = 'exe' })
$cfg.autoAdapter = $false
$cfg.adapter = @{ id = '{00000000-0000-0000-0000-000000000001}'; name = 'missing-vpn' }
$cfg.strictMode = $true
& $t -Json (@{ cmd = 'setConfig'; config = $cfg } | ConvertTo-Json -Depth 8 -Compress) | Out-Null
Start-Sleep -Seconds 3
Out "strict, no VPN adapter: curl $(Net)"
$cfg.strictMode = $false
& $t -Json (@{ cmd = 'setConfig'; config = $cfg } | ConvertTo-Json -Depth 8 -Compress) | Out-Null
Start-Sleep -Seconds 3
Out "normal, no VPN adapter: curl $(Net)"
sc.exe stop VpnPauseService | Out-Null
Start-Sleep -Seconds 4
Out "normal, service stopped: curl $(Net), wfp has $(Ours)"
sc.exe start VpnPauseService | Out-Null
Start-Sleep -Seconds 5
& $t -Json (@{ cmd = 'setConfig'; config = $orig } | ConvertTo-Json -Depth 8 -Compress) | Out-Null
Start-Sleep -Seconds 3
Out "restored: filters=$((Status).filterCount), apps=$((($orig.apps) | ForEach-Object name) -join ', ')"
Remove-Item 'C:\Users\Public\vpnpause-audit' -Recurse -Force
