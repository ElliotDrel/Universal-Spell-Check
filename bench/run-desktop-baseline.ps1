param(
    [Parameter(Mandatory=$true)][string]$Fixtures,
    [Parameter(Mandatory=$true)][string]$Output,
    [string]$Dotnet = 'dotnet',
    [string]$Settings,
    [switch]$DeveloperLogging,
    [switch]$AllowDesktop
)
$ErrorActionPreference = 'Stop'
if (-not $AllowDesktop) { throw 'Requires -AllowDesktop and an idle computer. This changes focus and the clipboard.' }
$repo = Split-Path $PSScriptRoot -Parent
$runner = Join-Path $PSScriptRoot 'bin/Release/net10.0-windows/UniversalSpellCheck.Bench.dll'
Push-Location $repo
try {
    $localArgs = @($runner, '--mode', 'local', '--fixtures', $Fixtures, '--runs', '20', '--warmup', '2', '--allow-desktop', '--output', (Join-Path $Output 'local-a'))
    if ($DeveloperLogging) { $localArgs += '--developer-logging' }
    & $Dotnet @localArgs
    if ($LASTEXITCODE -ne 0) { throw 'Local benchmark failed; inspect saved attempts before continuing.' }
    $liveArgs = @($runner, '--mode', 'e2e', '--runs', '10', '--warmup', '2', '--allow-desktop', '--output', (Join-Path $Output 'e2e-a'))
    if ($Settings) { $liveArgs += @('--settings', $Settings) }
    if ($DeveloperLogging) { $liveArgs += '--developer-logging' }
    & $Dotnet @liveArgs
    if ($LASTEXITCODE -ne 0) { throw 'Live E2E benchmark failed; inspect saved attempts.' }
}
finally { Pop-Location }
