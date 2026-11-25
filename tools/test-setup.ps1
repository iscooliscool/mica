# Test helper: runs the installer UI with a debug port and screenshots each stage.
# Usage: tools\test-setup.ps1 -Setup dist\Mica-1.0.0-Setup.exe -Out <folder> [-Uninstall <installDir>]
param([string]$Setup, [string]$Out, [string]$Uninstall = "", [string]$Dir = "")
$ErrorActionPreference = "Stop"
$env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=9334"
$args2 = @()
if ($Uninstall) { $exe = Join-Path $Uninstall "Uninstall Mica.exe"; $args2 = @("/uninstall") } else { $exe = $Setup; if ($Dir) { $args2 = @("`"/dir=$Dir`"") } }
if ($args2.Count) { $p = Start-Process -FilePath $exe -ArgumentList $args2 -PassThru } else { $p = Start-Process -FilePath $exe -PassThru }
$name = if ($Uninstall) { "MicaUninstall-*" } else { [IO.Path]::GetFileNameWithoutExtension($exe) }
Start-Sleep 5
$proc = Get-Process | Where-Object { $_.ProcessName -like $name -and $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $proc) { throw "setup window not found ($name)" }
$shot = { param($file) & "$PSScriptRoot\winshot.ps1" -Name $proc.ProcessName -Out (Join-Path $Out $file) | Out-Null }
& $shot "a-start.png"
$btn = if ($Uninstall) { "#un" } else { "#go" }
$env:CDP_PORT = "9334"
node "$PSScriptRoot\cdp.mjs" eval setup.html "document.querySelector('#desktop') && (document.querySelector('#desktop').checked=false); document.querySelector('$btn').click(); 1" | Out-Null
Start-Sleep -Milliseconds 2200
& $shot "b-progress.png"
Start-Sleep 3
Start-Sleep 5
& $shot "c-done.png"
node "$PSScriptRoot\cdp.mjs" eval setup.html "document.querySelector('.view.on').id + ' ' + document.body.className"
"process: $($proc.ProcessName) $($proc.Id)"
