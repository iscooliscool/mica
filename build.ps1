# Builds Mica and its custom installer.  Usage:  .\build.ps1            (version from Mica.csproj)
#                                               .\build.ps1 -Version 1.0.1
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

# 1. the browser
& $dotnet build $projects[0] -c Release -nologo -v q
if ($LASTEXITCODE) { throw "Mica build failed" }

# 2. pack the browser into the installer's payload
$bin = "src\Mica\bin\Release\net462"
$stage = "src\MicaSetup\obj\stage"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item "$bin\Mica.exe", "$bin\Mica.exe.config", "$bin\Microsoft.Web.WebView2.Core.dll", "$bin\Microsoft.Web.WebView2.WinForms.dll" $stage
Copy-Item "$bin\runtimes", "$bin\ui" $stage -Recurse
$zip = "src\MicaSetup\payload\app.zip"
New-Item -ItemType Directory -Force (Split-Path $zip) | Out-Null
if (Test-Path $zip) { Remove-Item $zip }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory((Resolve-Path $stage), (Join-Path (Resolve-Path (Split-Path $zip)) "app.zip"), [IO.Compression.CompressionLevel]::Optimal, $false)

# 3. Microsoft's small WebView2 bootstrapper, for PCs that don't have WebView2 yet
$boot = "installer\redist\MicrosoftEdgeWebview2Setup.exe"
if (-not (Test-Path $boot)) {
    New-Item -ItemType Directory -Force (Split-Path $boot) | Out-Null
    Invoke-WebRequest "https://go.microsoft.com/fwlink/p/?LinkId=2124703" -OutFile $boot
}
if ((Get-AuthenticodeSignature $boot).Status -ne 'Valid') { Remove-Item $boot; throw "WebView2 bootstrapper signature check failed" }

# 4. the installer (one self-contained .exe)
& $dotnet build $projects[1] -c Release -nologo -v q
if ($LASTEXITCODE) { throw "Setup build failed" }
New-Item -ItemType Directory -Force dist | Out-Null
$out = "dist\Mica-$Version-Setup.exe"
Copy-Item "src\MicaSetup\bin\Release\net462\MicaSetup.exe" $out -Force
Write-Host ("Built {0} ({1:N1} MB)" -f $out, ((Get-Item $out).Length / 1MB))
