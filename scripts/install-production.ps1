[CmdletBinding()]
param([switch]$Install, [switch]$VerifyOnly, [switch]$VerifyCorrection)
$ErrorActionPreference = 'Stop'
$result = [ordered]@{ mode = if ($Install) { 'install' } else { 'verify' }; success = $false; verification_complete = $false; error = $null; managed_path = $null; managed_version = $null; manifest_valid = $false; processes = @(); startup_target = $null; startup_valid = $false; managed_running = $false; credentials_present = $false; updater_present = $false; updater_last_success = $null; correction = 'not_run'; installer_digest_verified = $false; cleanup_candidates = @() }
try {
    if ($Install -and $VerifyOnly) { throw 'Choose either Install or VerifyOnly.' }
    $root = Join-Path $env:LOCALAPPDATA 'UniversalSpellCheck'
    $exe = Join-Path $root 'current\UniversalSpellCheck.exe'
    $repoRoot = Split-Path $PSScriptRoot -Parent
    # Channel data names have one authority; this agent command runs from the repository.
    $channelSource = Get-Content -LiteralPath (Join-Path $repoRoot 'rust/src/channel.rs') -Raw
    $dataName = [regex]::Match($channelSource, 'pub const SHARED_DATA_FOLDER: &str = "([^"]+)"').Groups[1].Value
    if (-not $dataName) { throw 'Cannot resolve the shared data folder from channel.rs.' }
    if ($Install) {
        $release = Invoke-RestMethod -Uri 'https://api.github.com/repos/ElliotDrel/Universal-Spell-Check/releases/latest' -Headers @{ 'User-Agent' = 'UniversalSpellCheck-installer' }
        $expectedVersion = $release.tag_name.TrimStart('v')
        $assets = @($release.assets | Where-Object { $_.name -like '*-Setup.exe' })
        if ($assets.Count -ne 1 -or $assets[0].digest -notmatch '^sha256:([a-fA-F0-9]{64})$') { throw 'Release must provide exactly one installer with a SHA-256 digest.' }
        $expected = $Matches[1]
        $temporary = Join-Path ([IO.Path]::GetTempPath()) ('usc-setup-' + [guid]::NewGuid().ToString() + '.exe')
        try {
            Invoke-WebRequest -Uri $assets[0].browser_download_url -OutFile $temporary -Headers @{ 'User-Agent' = 'UniversalSpellCheck-installer' }
            if ((Get-FileHash -Algorithm SHA256 -LiteralPath $temporary).Hash -ne $expected) { throw 'Installer checksum mismatch.' }
            $result.installer_digest_verified = $true
            $process = Start-Process -FilePath $temporary -ArgumentList '--silent' -Wait -PassThru -WindowStyle Hidden
            if ($process.ExitCode -ne 0) { throw "Installer failed with exit code $($process.ExitCode)." }
        } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
        # Silent setup does not guarantee that the resident app was launched.
        if (-not @(Get-Process -Name 'UniversalSpellCheck' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }).Count) {
            Start-Process -FilePath $exe -WindowStyle Hidden
            Start-Sleep -Seconds 2
        }
    }
    if (Test-Path -LiteralPath $exe) { $result.managed_path = $exe; $result.managed_version = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion }
    $result.updater_present = Test-Path -LiteralPath (Join-Path $root 'Update.exe')
    $result.credentials_present = Test-Path -LiteralPath (Join-Path (Join-Path $env:LOCALAPPDATA $dataName) 'apikey.dat')
    $manifestPath = Join-Path $root 'current/sq.version'
    if (Test-Path -LiteralPath $manifestPath) {
        [xml]$manifest = Get-Content -LiteralPath $manifestPath -Raw
        $id = $manifest.SelectSingleNode("//*[local-name()='metadata']/*[local-name()='id']")
        $version = $manifest.SelectSingleNode("//*[local-name()='metadata']/*[local-name()='version']")
        $result.manifest_valid = $null -ne $id -and $id.InnerText -eq 'UniversalSpellCheck' -and $null -ne $version -and $version.InnerText -eq $result.managed_version
    }
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $result.startup_target = (Get-ItemProperty -LiteralPath $runKey -Name 'UniversalSpellCheck' -ErrorAction SilentlyContinue).UniversalSpellCheck
    $result.startup_valid = -not $result.startup_target -or $result.startup_target.Trim('"').Equals($exe, [StringComparison]::OrdinalIgnoreCase)
    foreach ($process in @(Get-Process -Name 'UniversalSpellCheck','universal-spell-check' -ErrorAction SilentlyContinue)) {
        try { $path = $process.Path } catch { $path = $null }
        $info = if ($path) { (Get-Item -LiteralPath $path).VersionInfo } else { $null }
        $kind = if (-not $path) { 'unknown' } elseif ($path.Equals($exe, [StringComparison]::OrdinalIgnoreCase)) { 'managed' } elseif ($info.ProductName -match '\bDev\b' -or $info.ProductVersion -match 'dev') { 'dev' } else { 'standalone_or_unclassified' }
        $result.processes += @{ pid = $process.Id; path = $path; version = $info.ProductVersion; kind = $kind }
        if ($kind -eq 'managed' -and $info.ProductVersion -eq $result.managed_version) { $result.managed_running = $true }
    }
    foreach ($relative in @('publish/UniversalSpellCheck.exe','rust/dist/prod/UniversalSpellCheck.exe')) {
        $candidate = Join-Path $repoRoot $relative
        if (Test-Path -LiteralPath $candidate) { $result.cleanup_candidates += @{ path = $candidate; action = 'review_only'; reason = 'Reproducible standalone payload; never delete automatically.' } }
    }
    $reader = Join-Path $repoRoot '.agents/skills/read-logs/scripts/logs.py'
    $checks = & python $reader --event update_check_done --channel prod --last 1 --json 2>$null
    if ($LASTEXITCODE -eq 0 -and $checks) {
        $check = $checks | Select-Object -Last 1 | ConvertFrom-Json
        if ($check.version -eq $result.managed_version -and ([DateTimeOffset]::Now - [DateTimeOffset]::Parse($check.ts)).TotalHours -le 24) { $result.updater_last_success = $check.ts }
    }
    $result.correction = if ($result.credentials_present) { 'not_run' } else { 'missing_credentials' }
    if (-not $result.managed_path -or -not $result.updater_present) { throw 'Managed installation or updater is missing.' }
    if ($Install -and $result.managed_version -ne $expectedVersion) { throw 'Installed version does not match the downloaded release.' }
    if (-not $result.manifest_valid) { throw 'Installed package manifest is missing or disagrees with the executable.' }
    if (-not $result.startup_valid) { throw 'Enabled startup still targets another executable; launch the installed Production app to reconcile it.' }
    if (-not $result.managed_running) { throw 'The installed Production version is not running.' }
    if ($VerifyCorrection) {
        if (-not $result.credentials_present) { throw 'Missing credentials; add a key in Production Settings.' }
        $output = Join-Path ([IO.Path]::GetTempPath()) ('usc-install-check-' + [guid]::NewGuid())
        $arguments = @('--bench','--live','--case','short','--runs','1','--warmup','0','--cases',('"' + (Join-Path $repoRoot 'bench/cases.json') + '"'),'--replay',('"' + (Join-Path $repoRoot 'bench/replay.json') + '"'),'--replacements',('"' + (Join-Path $root 'current/replacements.json') + '"'),'--output',('"' + $output + '"'))
        # The currently published release predates the consolidated --bench command.
        $legacy = $result.managed_version -eq '0.13.4'
        if ($legacy) {
            New-Item -ItemType Directory -Path $output | Out-Null
            $casePath = Join-Path $output 'short-case.json'
            Set-Content -LiteralPath $casePath -Encoding Ascii -Value '[{"id":"short","text":"This is a tset.","expected":"This is a test."}]'
            $evidence = Join-Path $output 'evidence'
            $arguments = @('--bench-desktop','--allow-desktop','--mode','e2e','--runs','1','--warmup','0','--cases',('"' + $casePath + '"'),'--settings',('"' + (Join-Path $env:LOCALAPPDATA $dataName) + '"'),'--replacements',('"' + (Join-Path $root 'current/replacements.json') + '"'),'--output',('"' + $evidence + '"'))
        } else { $evidence = $output }
        $result.correction = 'failed'
        $probe = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru -WindowStyle Hidden
        if (-not $probe.WaitForExit(90000)) { $probe.Kill(); throw 'The isolated correction probe timed out.' }
        $summaryPath = Join-Path $evidence 'summary.json'
        if (-not (Test-Path -LiteralPath $summaryPath)) { throw "Correction probe produced no summary (exit $($probe.ExitCode)); inspect $output" }
        $summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
        $passed = if ($legacy) { $null -ne $summary.aggregate -and $summary.aggregate.attempted -eq 1 -and $summary.aggregate.succeeded -eq 1 -and $summary.aggregate.failed -eq 0 -and $summary.aggregate.quality_matches -eq 1 -and -not $summary.block_error } else { $summary.measured -eq 1 -and $summary.failed -eq 0 -and -not $summary.error }
        if ($probe.ExitCode -ne 0 -or -not $passed) { throw "Correction probe failed or did not complete exactly one correction; inspect $output" }
        $result.correction = 'passed'
    }
    $result.verification_complete = $result.correction -eq 'passed' -and $null -ne $result.updater_last_success
    $result.success = $true
} catch { $result.error = $_.Exception.Message }
$result | ConvertTo-Json -Depth 5
if (-not $result.success) { exit 1 }
