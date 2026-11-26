# Talks to the VpnPause service over its control pipe from a normal (non-elevated) shell.
param(
    [Parameter(Mandatory)][string]$Json
)

$pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'VpnPause.Control', [System.IO.Pipes.PipeDirection]::InOut)
$pipe.Connect(3000)
$bytes = [System.Text.Encoding]::UTF8.GetBytes($Json)
$pipe.Write([BitConverter]::GetBytes($bytes.Length), 0, 4)
$pipe.Write($bytes, 0, $bytes.Length)
$pipe.Flush()
$len = New-Object byte[] 4
$read = 0
while ($read -lt 4) { $read += $pipe.Read($len, $read, 4 - $read) }
$n = [BitConverter]::ToInt32($len, 0)
$buf = New-Object byte[] $n
$read = 0
while ($read -lt $n) { $read += $pipe.Read($buf, $read, $n - $read) }
$pipe.Dispose()
[System.Text.Encoding]::UTF8.GetString($buf)
