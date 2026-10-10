[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$ExpectedVersion,
    [string]$InstallRoot = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'UniversalSpellCheck')
)
$ErrorActionPreference = 'Stop'
$repo = 'ElliotDrel/Universal-Spell-Check'
function Read-GitHubJson([string[]]$Arguments) {
    $result = & gh @Arguments
    if ($LASTEXITCODE -ne 0) { throw 'GitHub verification failed; installation was not started' }
    return ($result | ConvertFrom-Json)
}
$release = Read-GitHubJson -Arguments @('api', "repos/$repo/releases/tags/v$ExpectedVersion")
if ($release.draft -or $release.prerelease -or $release.tag_name -ne "v$ExpectedVersion") {
    throw 'Expected a public stable release for the requested version'
}
$commit = Read-GitHubJson -Arguments @('api', "repos/$repo/commits/v$ExpectedVersion")
$runs = Read-GitHubJson -Arguments @('run', 'list', '--repo', $repo, '--workflow', 'release.yml', '--limit', '30', '--json', 'headSha,status,conclusion,databaseId')
$run = $runs | Where-Object { $_.headSha -eq $commit.sha } | Select-Object -First 1
if (-not $run -or $run.status -ne 'completed' -or $run.conclusion -ne 'success') {
    throw 'The requested release workflow has not completed successfully'
}
$asset = @($release.assets | Where-Object name -eq "UniversalSpellCheck-$ExpectedVersion-full.nupkg")
if ($asset.Count -ne 1 -or $asset[0].digest -notmatch '^sha256:([a-fA-F0-9]{64})$') {
    throw 'Published full package has no unambiguous SHA256 digest'
}
$expectedHash = $Matches[1]
$root = (Resolve-Path -LiteralPath $InstallRoot).Path.TrimEnd('\')
$exe = Join-Path $root 'current/UniversalSpellCheck.exe'
$updater = Join-Path $root 'Update.exe'
if (-not (Test-Path -LiteralPath $exe) -or -not (Test-Path -LiteralPath $updater)) {
    throw 'Existing production installation or Velopack updater is missing'
}
function Get-InstalledProcesses {
    return @(Get-CimInstance Win32_Process -Filter "Name = 'UniversalSpellCheck.exe'" |
        Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase) })
}
function Confirm-StartupLog {
    # Use the supported log reader, never parse daily JSONL directly.
    $repoRoot = (Resolve-Path "$PSScriptRoot/../../../..").Path
    $reader = Join-Path $repoRoot '.agents/skills/read-logs/scripts/logs.py'
    $logRoot = Join-Path (Split-Path $root -Parent) 'UniversalSpellCheck.Data/logs'
    $log = & python $reader --log-dir $logRoot --channel prod --event started --last 1 --json
    if ($LASTEXITCODE -ne 0) { throw 'Production startup log verification failed' }
    $started = $log | ConvertFrom-Json
    $current = @(Get-InstalledProcesses)
    if ($current.Count -ne 1 -or $started.version -ne $ExpectedVersion -or [int]$started.pid -ne $current[0].ProcessId) {
        throw 'Fresh production startup log does not match the released version and running PID'
    }
}
$installed = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion.Split('+')[0]
$processes = @(Get-InstalledProcesses)
if ($processes.Count -gt 1) { throw 'Multiple installed production processes; refusing an ambiguous restart' }
if ([version]$installed -gt [version]$ExpectedVersion) { throw 'Refusing to downgrade the production installation' }
if ($installed -eq $ExpectedVersion) {
    if ($processes.Count -eq 0) { Start-Process -FilePath $exe -WindowStyle Hidden | Out-Null; Start-Sleep -Seconds 2 }
    & "$PSScriptRoot/verify-local-prod.ps1" -ExpectedVersion $ExpectedVersion -InstallRoot $root
    Confirm-StartupLog
    Write-Output 'Requested production version is already installed and running'
    return
}
# Restarting the installed app invokes its existing CheckAsync(Launch) downloader.
# Do not duplicate the feed/downloader or copy a build into the installer root.
if ($processes.Count -eq 1) {
    Stop-Process -Id $processes[0].ProcessId
    Wait-Process -Id $processes[0].ProcessId -Timeout 15 -ErrorAction SilentlyContinue
}
Start-Process -FilePath $exe -WindowStyle Hidden | Out-Null
Write-Output "Waiting for the installed app to download v$ExpectedVersion"
$package = Join-Path $root "packages/UniversalSpellCheck-$ExpectedVersion-full.nupkg"
$deadline = (Get-Date).AddMinutes(5)
$packageVerified = $false
while ((Get-Date) -lt $deadline) {
    if (Test-Path -LiteralPath $package) {
        try {
            $packageVerified = (Get-Item -LiteralPath $package).Length -eq $asset[0].size -and
                (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash -eq $expectedHash
        } catch { $packageVerified = $false }
    }
    if ($packageVerified) { break }
    Start-Sleep -Seconds 2
}
if (-not $packageVerified) { throw 'App-downloaded package did not match the published size/hash within five minutes' }
$processes = @(Get-InstalledProcesses)
if ($processes.Count -ne 1) { throw 'Expected one installed app process before applying the staged update' }
$startedAfter = Get-Date
Stop-Process -Id $processes[0].ProcessId
Wait-Process -Id $processes[0].ProcessId -Timeout 15 -ErrorAction SilentlyContinue
try {
    $apply = Start-Process -FilePath $updater -ArgumentList @('apply', '--silent', '--package', ('"' + $package + '"')) -WindowStyle Hidden -PassThru
    # Start-Process -Wait waits for descendants, including the restarted tray app.
    # Wait only for Update.exe so publishing can finish while Prod stays running.
    if (-not $apply.WaitForExit(120000)) { throw 'Velopack apply has not exited within two minutes; inspect it before retrying' }
    if ($apply.ExitCode -ne 0) { throw "Velopack apply failed with exit code $($apply.ExitCode)" }
} catch {
    if (Test-Path -LiteralPath $exe) {
        if (@(Get-InstalledProcesses).Count -eq 0) { Start-Process -FilePath $exe -WindowStyle Hidden | Out-Null }
    }
    throw
}
$deadline = (Get-Date).AddSeconds(30)
while (@(Get-InstalledProcesses).Count -eq 0 -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 1 }
& "$PSScriptRoot/verify-local-prod.ps1" -ExpectedVersion $ExpectedVersion -InstallRoot $root -StartedAfter $startedAfter
Confirm-StartupLog
Write-Output "Production v$ExpectedVersion installed and startup verified"
