# Determinism test: runs the test copy twice from the same save and compares the
# run-state traces. Usage: powershell -File tools\run-dettest.ps1 [-GameDir <test copy>]
param(
    [string]$GameDir = (Join-Path $PSScriptRoot "..\..\test-installs\cloverpit-host"),
    [int]$TimeoutSec = 330
)

$ErrorActionPreference = "Stop"
$GameDir = (Resolve-Path $GameDir).Path
$save = Join-Path $GameDir "SaveData"
$snapshot = Join-Path $PSScriptRoot "..\local\dettest-base-save"
$traceRoot = Join-Path $GameDir "BepInEx\coop-trace"

# Freeze the starting save once, so both runs start from the same state.
if (Test-Path $snapshot) { Remove-Item $snapshot -Recurse -Force }
New-Item -ItemType Directory -Force $snapshot | Out-Null
Copy-Item "$save\*" $snapshot -Recurse

function Run-Once([string]$label) {
    Remove-Item "$save\*" -Recurse -Force
    Copy-Item "$snapshot\*" $save -Recurse
    $p = Start-Process -FilePath (Join-Path $GameDir "CloverPit.exe") `
        -ArgumentList "--coop-dettest", $label, "-screen-fullscreen", "0", "-screen-width", "960", "-screen-height", "540" `
        -WorkingDirectory $GameDir -PassThru
    if (-not $p.WaitForExit($TimeoutSec * 1000)) {
        Stop-Process -Id $p.Id -Force
        Write-Output "$label TIMEOUT"
    }
    Select-String -Path (Join-Path $GameDir "BepInEx\LogOutput.log") -Pattern "DETTEST" | ForEach-Object { "$label " + $_.Line }
}

Run-Once "A"
Run-Once "B"

# Leave the copy on the frozen save.
Remove-Item "$save\*" -Recurse -Force
Copy-Item "$snapshot\*" $save -Recurse

$a = Get-Content (Join-Path $traceRoot "A\trace.txt")
$b = Get-Content (Join-Path $traceRoot "B\trace.txt")
$n = [Math]::Max($a.Count, $b.Count)
$firstDiff = $null
for ($i = 0; $i -lt $n; $i++) {
    $same = ($i -lt $a.Count) -and ($i -lt $b.Count) -and ($a[$i] -eq $b[$i])
    Write-Output ("{0,-28} {1,-28} {2}" -f $a[$i], $b[$i], $(if ($same) { "same" } else { "DIFF" }))
    if (-not $same -and $null -eq $firstDiff) { $firstDiff = $i }
}
if ($null -eq $firstDiff) {
    Write-Output "DETERMINISM PASS: $n steps identical"
} else {
    Write-Output "DETERMINISM FAIL: first difference at step $firstDiff"
}
