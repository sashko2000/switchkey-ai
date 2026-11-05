# Builds KeyboardSwitcher into dist\KeyboardSwitcher. -Zip also creates an archive for distribution.
# OpenVINO and the model are not needed here: the app installs them itself when text correction is enabled in settings.
param([switch]$Zip)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$Dist = "dist\KeyboardSwitcher"

$sdks = @()
try { $sdks = & dotnet --list-sdks 2>$null } catch { }
if (-not ($sdks | Where-Object { $_ -match "^1\d\." })) {
    Write-Host ".NET SDK 10 not found. Install it with:" -ForegroundColor Red
    Write-Host "    winget install Microsoft.DotNet.SDK.10" -ForegroundColor Yellow
    exit 1
}

if (Test-Path $Dist) { Remove-Item $Dist -Recurse -Force }
& dotnet publish src\KeyboardSwitcher\KeyboardSwitcher.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $Dist -nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Get-ChildItem $Dist -Filter *.pdb | Remove-Item
New-Item -ItemType Directory "$Dist\licenses" -Force | Out-Null
Copy-Item THIRD_PARTY_NOTICES.md, LICENSE "$Dist\licenses\" -Force

$size = (Get-ChildItem $Dist -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("Done: {0} ({1:N0} MB)" -f (Resolve-Path $Dist), $size) -ForegroundColor Green

if ($Zip) {
    $zipOut = "dist\KeyboardSwitcher.zip"
    if (Test-Path $zipOut) { Remove-Item $zipOut }
    Compress-Archive -Path $Dist -DestinationPath $zipOut
    Write-Host "Archive: $zipOut" -ForegroundColor Green
}
