# Test helper: focus Mica and send keystrokes (SendKeys syntax, e.g. "^t" = Ctrl+T).
param([string]$Keys)
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class K { [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h); }
"@
# only the test copy built from this repo, never an installed Mica
$p = Get-Process Mica -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 -and $_.Path -like "*\Code\Mica\src\*" } | Select-Object -First 1
if (-not $p) { Write-Error "test Mica not found"; exit 1 }
[K]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 300
# never type into whatever else the user has in front
Add-Type 'using System; using System.Runtime.InteropServices; public static class F { [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow(); [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h); }'
if ([F]::IsIconic($p.MainWindowHandle) -or [F]::GetForegroundWindow() -ne $p.MainWindowHandle) { Write-Error "test Mica is not in front; not typing"; exit 1 }
[System.Windows.Forms.SendKeys]::SendWait($Keys)
Start-Sleep -Milliseconds 600
"sent $Keys"
