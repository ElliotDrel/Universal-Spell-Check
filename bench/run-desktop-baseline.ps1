param(
    [Parameter(Mandatory=$true)][string]$Fixtures,
    [Parameter(Mandatory=$true)][string]$Output,
    [string]$Dotnet = 'dotnet',
    [string]$Runner,
    [string]$Cases,
    [string]$Settings,
    [switch]$DeveloperLogging,
    [switch]$AllowDesktop
)
$ErrorActionPreference = 'Stop'
if (-not $AllowDesktop) { throw 'Requires -AllowDesktop and an idle computer. This changes focus and the clipboard.' }
$repo = Split-Path $PSScriptRoot -Parent
if (-not $Cases) { $Cases = Join-Path $PSScriptRoot 'baseline-cases.json' }
if (-not $Runner) {
    $finalRunner = Join-Path $PSScriptRoot 'desktop-runner/UniversalSpellCheck.Bench.dll'
    $frozenRunner = Join-Path $PSScriptRoot 'runner/UniversalSpellCheck.Bench.dll'
    $Runner = if (Test-Path -LiteralPath $finalRunner) { $finalRunner } elseif (Test-Path -LiteralPath $frozenRunner) { $frozenRunner } else { Join-Path $PSScriptRoot 'bin/Release/net10.0-windows/UniversalSpellCheck.Bench.dll' }
}
Push-Location $repo
try {
    $localArgs = @($Runner, '--mode', 'local', '--cases', $Cases, '--fixtures', $Fixtures, '--runs', '10', '--warmup', '2', '--allow-desktop', '--output', (Join-Path $Output 'local-a'))
    if ($DeveloperLogging) { $localArgs += '--developer-logging' }
    & $Dotnet @localArgs
    $localExit = $LASTEXITCODE
    if ($localExit -notin 0,1) { throw "Local benchmark safety/setup failure: $localExit" }
    $liveArgs = @($Runner, '--mode', 'e2e', '--cases', $Cases, '--runs', '10', '--warmup', '2', '--allow-desktop', '--output', (Join-Path $Output 'e2e-a'))
    if ($Settings) { $liveArgs += @('--settings', $Settings) }
    if ($DeveloperLogging) { $liveArgs += '--developer-logging' }
    & $Dotnet @liveArgs
    $liveExit = $LASTEXITCODE
    if ($liveExit -notin 0,1) { throw "Live E2E benchmark safety/setup failure: $liveExit" }
    [pscustomobject]@{ local_exit=$localExit; live_exit=$liveExit; note='Exit 1 retains completed product failures; inspect summaries for reliability.' } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Output 'block-status.json') -Encoding utf8
}
finally { Pop-Location }
