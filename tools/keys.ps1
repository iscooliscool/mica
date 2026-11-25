# Test helper: focus Mica and send keystrokes (SendKeys syntax, e.g. "^t" = Ctrl+T).
param([string]$Keys)
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class K { [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h); }
"@
# only the test copy built from this repo, never an installed Mica
$p = Get-Process Mica -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 -and $_.Path -like "*\Code\Mica\src\*" } | Select-Object -First 1
[K]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 300
[System.Windows.Forms.SendKeys]::SendWait($Keys)
Start-Sleep -Milliseconds 600
"sent $Keys"
