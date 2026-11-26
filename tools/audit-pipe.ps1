# Robustness checks of the control pipe: malformed frames, idle connections, path validation.
$ErrorActionPreference = 'Continue'
$t = Join-Path $PSScriptRoot 'pipe-test.ps1'

function Raw([byte[]]$bytes, [int]$waitMs = 500) {
    try {
        $p = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'VpnPause.Control', [System.IO.Pipes.PipeDirection]::InOut)
        $p.Connect(2000)
        $p.Write($bytes, 0, $bytes.Length); $p.Flush()
        Start-Sleep -Milliseconds $waitMs
        $p.Dispose(); 'sent'
    } catch { 'error: ' + $_.Exception.Message }
}

"negative length: " + (Raw ([BitConverter]::GetBytes(-1)))
"huge length: " + (Raw ([BitConverter]::GetBytes(2000000000)))
$junk = [Text.Encoding]::UTF8.GetBytes('{not json')
"bad json: " + (Raw ([BitConverter]::GetBytes($junk.Length) + $junk))
"unknown cmd: " + (& $t -Json '{"cmd":"format-c"}')
"null config: " + (& $t -Json '{"cmd":"setConfig"}')

$idle = @()
for ($i = 0; $i -lt 300; $i++) {
    try { $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'VpnPause.Control', [System.IO.Pipes.PipeDirection]::InOut); $c.Connect(1000); $idle += $c } catch { break }
}
"idle connections opened: $($idle.Count)"
$sw = [Diagnostics.Stopwatch]::StartNew()
$s = & $t -Json '{"cmd":"status"}'
"status while flooded: ok=$(($s | ConvertFrom-Json).ok) in $($sw.ElapsedMilliseconds) ms"
$idle | ForEach-Object { $_.Dispose() }

$orig = (& $t -Json '{"cmd":"getConfig"}' | ConvertFrom-Json).config
$probe = $orig | ConvertTo-Json -Depth 8 | ConvertFrom-Json
$probe.apps = @(
    @{ name = 'unc'; path = '\\127.0.0.1\c$\Tools\x.exe'; scope = 'folder' },
    @{ name = 'device'; path = '\\?\C:\Tools\y.exe'; scope = 'exe' },
    @{ name = 'dot'; path = '\\.\C:\Tools\z.exe'; scope = 'exe' }
)
$probe.exclusions = @('e')
$resp = & $t -Json (@{ cmd = 'setConfig'; config = $probe } | ConvertTo-Json -Depth 8 -Compress) | ConvertFrom-Json
"accepted paths: " + (($resp.config.apps | ForEach-Object path) -join ' | ')
"rejected: " + ($resp.rejected -join ' | ')
"one-letter exclusion kept: " + ($resp.config.exclusions -join ',')
$back = & $t -Json (@{ cmd = 'setConfig'; config = $orig } | ConvertTo-Json -Depth 8 -Compress) | ConvertFrom-Json
"restored apps: " + (($back.config.apps | ForEach-Object name) -join ', ')
