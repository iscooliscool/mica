# Test helper: total private memory of Mica + its WebView2 processes (like Task Manager's "Memory" column).
$all = Get-CimInstance Win32_Process
$mica = $all | Where-Object { $_.Name -eq 'Mica.exe' }
$ids = New-Object System.Collections.Generic.HashSet[int]
foreach ($m in $mica) { [void]$ids.Add([int]$m.ProcessId) }
do {
  $added = $false
  foreach ($p in $all) { if ($ids.Contains([int]$p.ParentProcessId) -and $ids.Add([int]$p.ProcessId)) { $added = $true } }
} while ($added)
$total = 0
$rows = foreach ($id in $ids) {
  $p = Get-Process -Id $id -ErrorAction SilentlyContinue
  if ($p) { $mb = [math]::Round($p.PrivateMemorySize64 / 1MB); $total += $mb; [pscustomobject]@{ Id = $id; Name = $p.ProcessName; PrivateMB = $mb } }
}
$rows | Sort-Object PrivateMB -Descending | Format-Table -AutoSize
"TOTAL private (commit) MB: $total  across $($rows.Count) processes"
