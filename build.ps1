# Builds VpnPause: tests -> single-file publish -> Inno Setup installer in .\out
param(
    [switch]$SkipTests,
    # Optional code signing: path to a .pfx; password is read from $env:VPNPAUSE_SIGN_PASSWORD.
    [string]$SignPfx
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

[xml]$props = Get-Content .\Directory.Build.props
$version = $props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

if (-not $SkipTests) {
    dotnet test .\tests\VpnPause.Tests\VpnPause.Tests.csproj -c Release -nologo
    if ($LASTEXITCODE) { throw 'tests failed' }
}

$publish = Join-Path $PSScriptRoot 'out\publish'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish .\src\VpnPause.App\VpnPause.App.csproj -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $publish -nologo
if ($LASTEXITCODE) { throw 'publish failed' }

function Sign([string]$file) {
    if (-not $SignPfx) { return }
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" | Sort-Object FullName -Descending | Select-Object -First 1
    & $signtool.FullName sign /fd SHA256 /f $SignPfx /p $env:VPNPAUSE_SIGN_PASSWORD /tr http://timestamp.digicert.com /td SHA256 $file
    if ($LASTEXITCODE) { throw "signing failed: $file" }
}
Sign "$publish\VpnPause.exe"

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found (winget install JRSoftware.InnoSetup)' }

& $iscc /Q "/DAppVersion=$version" "/DPublishDir=$publish" .\installer\VpnPause.iss
if ($LASTEXITCODE) { throw 'installer build failed' }
$setup = Join-Path $PSScriptRoot "out\VpnPause-Setup-$version.exe"
Sign $setup
Write-Host "OK: $setup"
