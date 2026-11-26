# Elevated: does a brand-new exe under a protected folder get blocked, and how fast. Restores the config.
param([Parameter(Mandatory)][string]$Report)
$ErrorActionPreference = 'Continue'
$t = Join-Path $PSScriptRoot 'pipe-test.ps1'
$work = 'C:\Users\Public\vpnpause-audit'
$app = "$work\app"
$src = "$env:WINDIR\System32\curl.exe"
New-Item -ItemType Directory "$app\1.0.0" -Force | Out-Null
Copy-Item $src "$app\1.0.0\tool.exe" -Force
Remove-Item $Report -ErrorAction SilentlyContinue
function Out($s) { $s | Out-File -Append -Encoding utf8 $Report }
function Net($exe) { $r = & $exe -s -m 5 https://api.ipify.org; if ($r) { "online $r" } else { 'blocked' } }
function Send($cfg) { & $t -Json (@{ cmd = 'setConfig'; config = $cfg } | ConvertTo-Json -Depth 8 -Compress) | Out-Null }

$orig = (& $t -Json '{"cmd":"getConfig"}' | ConvertFrom-Json).config
$cfg = $orig | ConvertTo-Json -Depth 8 | ConvertFrom-Json
$cfg.apps = @(@{ name = 'audit folder'; path = "$app\1.0.0\tool.exe"; scope = 'folder' })
$cfg.autoAdapter = $false
$cfg.adapter = @{ id = '{00000000-0000-0000-0000-000000000001}'; name = 'missing-vpn' }
Send $cfg
Start-Sleep -Seconds 3
Out "existing 1.0.0\tool.exe: $(Net "$app\1.0.0\tool.exe")"

New-Item -ItemType Directory "$app\1.0.1" -Force | Out-Null
Copy-Item $src "$app\1.0.1\tool.exe" -Force
Start-Sleep -Seconds 3
Out "new version 1.0.1, 3 s after copy: $(Net "$app\1.0.1\tool.exe")"

New-Item -ItemType Directory "$app\1.0.0\helpers" -Force | Out-Null
Copy-Item $src "$app\1.0.0\helpers\h.exe" -Force
Start-Sleep -Seconds 3
Out "new helper in a subfolder, 3 s after copy: $(Net "$app\1.0.0\helpers\h.exe")"

$leaks = 0
for ($i = 0; $i -lt 5; $i++) {
    New-Item -ItemType Directory "$app\2.0.$i" -Force | Out-Null
    Copy-Item $src "$app\2.0.$i\tool.exe" -Force
    if ((Net "$app\2.0.$i\tool.exe") -like 'online*') { $leaks++ }
}
Out "started instantly after copy: $leaks of 5 got out"

$delays = foreach ($ms in 50, 200, 500, 1000) {
    $d = "$app\3.$ms.0"
    New-Item -ItemType Directory $d -Force | Out-Null
    Copy-Item $src "$d\tool.exe" -Force
    Start-Sleep -Milliseconds $ms
    "$ms ms: $(Net "$d\tool.exe")"
}
Out ("run after delay -> " + ($delays -join '; '))

$renamed = "$app\4.0.0"
New-Item -ItemType Directory $renamed -Force | Out-Null
Copy-Item $src "$renamed\tool.tmp" -Force
Start-Sleep -Seconds 2
Rename-Item "$renamed\tool.tmp" 'tool.exe'
Start-Sleep -Seconds 2
Out "renamed .tmp -> .exe (installer style), 2 s later: $(Net "$renamed\tool.exe")"

Send $orig
Start-Sleep -Seconds 3
Out "restored apps: $((($orig.apps) | ForEach-Object name) -join ', ')"
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
