# Builds Mica and its custom installers.
#   .\build.ps1                  (version from Mica.csproj)
#   .\build.ps1 -Version 1.1.0
# Makes two installers in dist\:
#   Mica-<v>-Setup.exe         runs as 64-bit on 64-bit Windows and 32-bit on 32-bit Windows
#   Mica-<v>-Setup-32bit.exe   always runs Mica as a 32-bit app (a little less memory, for very old PCs)
param([string]$Version = "")
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$projects = "src\Mica\Mica.csproj", "src\MicaSetup\MicaSetup.csproj"
if ($Version) {
    foreach ($p in $projects) {
        (Get-Content $p -Raw) -replace '<Version>[^<]*</Version>', "<Version>$Version</Version>" | Set-Content $p -Encoding utf8 -NoNewline
    }
} else {
    $Version = ([xml](Get-Content $projects[0])).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = "$env:ProgramFiles\dotnet\dotnet.exe" }

# Microsoft's small WebView2 bootstrapper, for PCs that don't have WebView2 yet
$boot = "installer\redist\MicrosoftEdgeWebview2Setup.exe"
if (-not (Test-Path $boot)) {
    New-Item -ItemType Directory -Force (Split-Path $boot) | Out-Null
    Invoke-WebRequest "https://go.microsoft.com/fwlink/p/?LinkId=2124703" -OutFile $boot
}
if ((Get-AuthenticodeSignature $boot).Status -ne 'Valid') { Remove-Item $boot; throw "WebView2 bootstrapper signature check failed" }

Add-Type -AssemblyName System.IO.Compression.FileSystem
New-Item -ItemType Directory -Force dist | Out-Null

function Build-Flavor([string]$platform, [string]$suffix) {
    $outDir = "bin\$platform\"
    # 1. the browser
    & $dotnet build $projects[0] -c Release -nologo -v q "-p:PlatformTarget=$platform" "-p:OutputPath=$outDir" "-p:IntermediateOutputPath=obj\$platform\"
    if ($LASTEXITCODE) { throw "Mica build failed ($platform)" }
    $bin = "src\Mica\$outDir"

    # 2. pack it into the installer's payload
    $stage = "src\MicaSetup\obj\stage"
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Force $stage | Out-Null
    Copy-Item "$bin\Mica.exe", "$bin\Mica.exe.config", "$bin\Microsoft.Web.WebView2.Core.dll", "$bin\Microsoft.Web.WebView2.WinForms.dll" $stage
    Copy-Item "$bin\runtimes", "$bin\ui" $stage -Recurse
    $zipDir = "src\MicaSetup\payload"
    New-Item -ItemType Directory -Force $zipDir | Out-Null
    if (Test-Path "$zipDir\app.zip") { Remove-Item "$zipDir\app.zip" }
    [IO.Compression.ZipFile]::CreateFromDirectory((Resolve-Path $stage), (Join-Path (Resolve-Path $zipDir) "app.zip"), [IO.Compression.CompressionLevel]::Optimal, $false)

    # 3. the installer (one self-contained .exe)
    & $dotnet build $projects[1] -c Release -nologo -v q "-p:MicaBin=..\Mica\$outDir" "-p:LoaderBin=..\Mica\bin\AnyCPU\" "-p:IntermediateOutputPath=obj\$platform\"
    if ($LASTEXITCODE) { throw "Setup build failed ($platform)" }
    $out = "dist\Mica-$Version-Setup$suffix.exe"
    Copy-Item "src\MicaSetup\bin\Release\net462\MicaSetup.exe" $out -Force
    Write-Host ("Built {0} ({1:N1} MB)" -f $out, ((Get-Item $out).Length / 1MB))
}

Build-Flavor "AnyCPU" ""
Build-Flavor "x86" "-32bit"
