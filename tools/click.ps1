# Test helper: click inside the Mica test window. X/Y are pixels in a winshot.ps1 screenshot.
param([int]$X, [int]$Y, [switch]$Right)
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class C {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(int f, int x, int y, int d, int e);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int s);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
"@
[C]::SetProcessDPIAware() | Out-Null
# only the test copy built from this repo, never an installed Mica
$p = Get-Process Mica -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 -and $_.Path -like "*\Code\Mica\src\*" } | Select-Object -First 1
if (-not $p) { Write-Error "test Mica not found"; exit 1 }
[C]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 300
$r = New-Object C+RECT
[C]::DwmGetWindowAttribute($p.MainWindowHandle, 9, [ref]$r, 16) | Out-Null
[C]::SetCursorPos($r.L + $X, $r.T + $Y) | Out-Null
Start-Sleep -Milliseconds 150
if ($Right) { [C]::mouse_event(8, 0, 0, 0, 0); [C]::mouse_event(16, 0, 0, 0, 0) }
else { [C]::mouse_event(2, 0, 0, 0, 0); [C]::mouse_event(4, 0, 0, 0, 0) }
Start-Sleep -Milliseconds 500
"clicked $X,$Y"
