# Host + client lockstep test on one PC over localhost.
# Both test copies start from the same frozen save; the host's bot plays, the client replays
# the host's actions and both compare run-state hashes after every action.
# Usage: powershell -File tools\run-nettest.ps1
param(
    [string]$HostDir = (Join-Path $PSScriptRoot "..\..\test-installs\cloverpit-host"),
    [string]$ClientDir = (Join-Path $PSScriptRoot "..\..\test-installs\cloverpit-client"),
    [int]$Port = 27777,
    [int]$TimeoutSec = 900,
    # Mutation check: the client drops this action number; the run must then report a desync.
    [int]$MutateSkip = -1,
    # tcp: localhost socket. steam: Steam relay, client connects to the same account's SteamID.
    [ValidateSet("tcp", "steam")][string]$Transport = "tcp",
    # Client starts with an empty save (a new player) instead of the host's save; join sync
    # has to give it the host's run.
    [switch]$ClientFreshSave,
    # Host gets coins to pay the debt, so the deadline cutscene runs.
    [switch]$Rich
)

$ErrorActionPreference = "Stop"
$HostDir = (Resolve-Path $HostDir).Path
$snapshot = (Resolve-Path (Join-Path $PSScriptRoot "..\local\dettest-base-save")).Path
$dll = (Resolve-Path (Join-Path $PSScriptRoot "..\src\CloverPitCoop\bin\Release\netstandard2.1\CloverPitCoop.dll")).Path

# Client copy = host copy (game files, BepInEx, steam_appid.txt) without logs and traces.
robocopy $HostDir $ClientDir /E /XD (Join-Path $HostDir "BepInEx\coop-trace") /XF LogOutput.log /NFL /NDL /NJH /NJS /NP | Out-Null
$ClientDir = (Resolve-Path $ClientDir).Path

foreach ($dir in $HostDir, $ClientDir) {
    Copy-Item $dll (Join-Path $dir "BepInEx\plugins\") -Force
    $save = Join-Path $dir "SaveData"
    Remove-Item "$save\*" -Recurse -Force
    if (-not ($ClientFreshSave -and $dir -eq $ClientDir)) {
        Copy-Item "$snapshot\*" $save -Recurse
    }
}

$window = "-screen-fullscreen", "0", "-screen-width", "1280", "-screen-height", "720", "--coop-test-phonecall"
if ($Rich) { $window += "--coop-test-rich" }
if ($Transport -eq "steam") {
    $hostArgs = @("--coop-host-steam", "--coop-dettest", "H")
    $clientArgs = @("--coop-join-steam") + $window
} else {
    $hostArgs = @("--coop-host", "$Port", "--coop-dettest", "H")
    $clientArgs = @("--coop-join", "$Port") + $window
}
$h = Start-Process -FilePath (Join-Path $HostDir "CloverPit.exe") -WorkingDirectory $HostDir -PassThru `
    -ArgumentList ($hostArgs + $window)
Start-Sleep -Seconds 8
if ($MutateSkip -ge 0) { $clientArgs += @("--coop-mutate-skip", "$MutateSkip") }
$c = Start-Process -FilePath (Join-Path $ClientDir "CloverPit.exe") -WorkingDirectory $ClientDir -PassThru `
    -ArgumentList $clientArgs

$deadline = (Get-Date).AddSeconds($TimeoutSec)
while ((Get-Date) -lt $deadline -and -not ($h.HasExited -and $c.HasExited)) { Start-Sleep -Seconds 2 }
foreach ($p in $h, $c) { if (-not $p.HasExited) { Write-Output "TIMEOUT: stopping pid $($p.Id)"; Stop-Process -Id $p.Id -Force } }

Write-Output "---- host"
Select-String -Path (Join-Path $HostDir "BepInEx\LogOutput.log") -Pattern "\[coop\] (net|NETTEST|DETTEST)|Exception" | ForEach-Object { $_.Line }
Write-Output "---- client"
Select-String -Path (Join-Path $ClientDir "BepInEx\LogOutput.log") -Pattern "\[coop\] net|Exception" | ForEach-Object { $_.Line }
