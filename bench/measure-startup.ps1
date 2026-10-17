param(
    [Parameter(Mandatory=$true)][string]$ReferenceExe,
    [Parameter(Mandatory=$true)][string]$InstalledExe,
    [Parameter(Mandatory=$true)][string]$DataDirectory,
    [Parameter(Mandatory=$true)][string]$Output,
    [int]$Runs = 10
)
$ErrorActionPreference = 'Stop'
if ($Runs -lt 2) { throw 'At least two startup samples are required.' }
if (-not (Test-Path -LiteralPath $ReferenceExe) -or -not (Test-Path -LiteralPath $InstalledExe)) { throw 'Missing executable.' }
if (-not (Test-Path -LiteralPath (Join-Path $DataDirectory 'startup.initialized'))) {
    throw 'First-run registration flag is absent; refusing to alter login startup registration.'
}
if (Test-Path -LiteralPath $Output) { throw 'Startup output exists; refusing overwrite.' }
$installedPath = [IO.Path]::GetFullPath($InstalledExe)
$referencePath = [IO.Path]::GetFullPath($ReferenceExe)
if ($installedPath -eq $referencePath) { throw 'Reference must be the standalone frozen copy.' }
$installed = @(Get-Process UniversalSpellCheck -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $installedPath })
if ($installed.Count -ne 1) { throw 'Expected exactly one installed app; refusing ambiguous process control.' }
$settingsPath = Join-Path $DataDirectory 'settings.json'
$settingsHash = (Get-FileHash -LiteralPath $settingsPath).Hash
New-Item -ItemType Directory -Path $Output | Out-Null
$trials = @()
$child = $null
try {
    # Startup smoke runs the real constructor and registered hotkey, then exits at first idle.
    # Release the installed hotkey for the standalone reference, restoring the app in finally.
    Stop-Process -Id $installed[0].Id
    if (-not $installed[0].WaitForExit(10000)) { throw 'Installed app did not release its hotkey.' }
    foreach ($index in 1..$Runs) {
        $wall = [Diagnostics.Stopwatch]::StartNew()
        $child = Start-Process -FilePath $referencePath -ArgumentList '--startup-smoke' -PassThru -WindowStyle Hidden
        $processId = $child.Id
        $startedUtc = $child.StartTime.ToUniversalTime().ToString('o')
        if (-not $child.WaitForExit(30000)) { throw "Startup reference timed out: $processId" }
        $wall.Stop()
        $child.Refresh()
        $trials += [pscustomobject]@{ index=$index; pid=$processId; process_started_utc=$startedUtc; exit_code=$child.ExitCode; process_through_exit_ms=$wall.Elapsed.TotalMilliseconds }
        $trials | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Output 'processes.json') -Encoding utf8
        if ($child.ExitCode -ne 0) { throw "Startup smoke failed: $processId exit $($child.ExitCode)" }
        $child = $null
    }
}
finally {
    if ($child -and -not $child.HasExited) { Stop-Process -Id $child.Id }
    $restored = Start-Process -FilePath $installedPath -PassThru -WindowStyle Hidden
    [pscustomobject]@{ restored_pid=$restored.Id; installed_exe=$installedPath; settings_unchanged=((Get-FileHash -LiteralPath $settingsPath).Hash -eq $settingsHash) } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Output 'restore.json') -Encoding utf8
}

$reader = Join-Path (Split-Path $PSScriptRoot -Parent) '.agents/skills/read-logs/scripts/logs.py'
$lines = & python $reader --log-dir (Join-Path $DataDirectory 'logs') --event started --last 100 --json
if ($LASTEXITCODE -ne 0) { throw 'Log reader failed.' }
$startedEvents = @($lines | ForEach-Object { $_ | ConvertFrom-Json })
$measurements = foreach ($trial in $trials) {
    $lower = [DateTimeOffset]::Parse($trial.process_started_utc)
    $upper = $lower.AddMilliseconds($trial.process_through_exit_ms + 1000)
    $matching = @($startedEvents | Where-Object {
        $_.pid -eq $trial.pid -and [DateTimeOffset]::Parse($_.ts) -ge $lower -and [DateTimeOffset]::Parse($_.ts) -le $upper
    })
    if ($matching.Count -ne 1) { throw "Missing or ambiguous ready milestone for $($trial.pid)" }
    $ready = [DateTimeOffset]::Parse($matching[0].ts)
    $launch = [DateTimeOffset]::Parse($trial.process_started_utc)
    [pscustomobject]@{ index=$trial.index; pid=$trial.pid; startup_ready_ms=($ready-$launch).TotalMilliseconds; process_through_exit_ms=$trial.process_through_exit_ms; ready_timestamp=$matching[0].ts }
}
[pscustomobject]@{
    boundary='Standalone Release OS process start -> started log after real hotkey registration and tray/context initialization; excludes installed updater checks and automatic dashboard rendering.'
    clock='Cross-process wall-clock timestamps; no clock adjustment observed; not a stopwatch measurement of the child.'
    cold_limit='Retain first launch separately; subsequent runs use warm filesystem/OS caches. No reboot/cold-cache claim.'
    trials=@($measurements)
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Output 'startup.json') -Encoding utf8
Write-Output "Saved $($measurements.Count) startup readiness measurements; installed app relaunched."
