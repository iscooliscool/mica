# Builds Mica and its installer.  Usage:  .\build.ps1            (version from Mica.csproj)
#                                        .\build.ps1 -Version 1.0.1
param([string]$Version = "")
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$proj = "src\Mica\Mica.csproj"
if ($Version) {
    (Get-Content $proj -Raw) -replace '<Version>[^<]*</Version>', "<Version>$Version</Version>" | Set-Content $proj -Encoding utf8
} else {
    $Version = ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = "$env:ProgramFiles\dotnet\dotnet.exe" }
& $dotnet build $proj -c Release -nologo
if ($LASTEXITCODE) { throw "build failed" }

# Microsoft's small WebView2 bootstrapper, bundled for PCs that don't have WebView2 yet.
$boot = "installer\redist\MicrosoftEdgeWebview2Setup.exe"
if (-not (Test-Path $boot)) {
    New-Item -ItemType Directory -Force (Split-Path $boot) | Out-Null
    Invoke-WebRequest "https://go.microsoft.com/fwlink/p/?LinkId=2124703" -OutFile $boot
    if ((Get-AuthenticodeSignature $boot).Status -ne 'Valid') { Remove-Item $boot; throw "WebView2 bootstrapper signature check failed" }
}

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found (winget install JRSoftware.InnoSetup)" }
& $iscc "/DAppVersion=$Version" installer\mica.iss
if ($LASTEXITCODE) { throw "installer build failed" }
Write-Host "Built dist\Mica-$Version-Setup.exe"
