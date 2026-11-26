# Run as a non-elevated user (runas /trustlevel:0x20000): changes must be refused, reads allowed.
param([Parameter(Mandatory)][string]$Out)
$t = Join-Path $PSScriptRoot 'pipe-test.ps1'
function Try($label, $json) {
    try { "$label`: " + (& $t -Json $json) } catch { "$label`: EXCEPTION " + $_.Exception.Message }
}
@(
    'admin? ' + ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole('Administrators'),
    (Try 'setEnabled' '{"cmd":"setEnabled","enabled":false}'),
    (Try 'clearFilters' '{"cmd":"clearFilters"}'),
    (Try 'setConfig' '{"cmd":"setConfig","config":{"apps":[]}}'),
    (Try 'status' '{"cmd":"status"}'),
    (Try 'releaseStuck' '{"cmd":"releaseStuck"}')
) | Out-File -Encoding utf8 $Out
