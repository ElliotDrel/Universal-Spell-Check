[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$ExpectedVersion,
    [string]$InstallRoot = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'UniversalSpellCheck'),
    [datetime]$StartedAfter
)

$ErrorActionPreference = 'Stop'
$resolvedRoot = (Resolve-Path -LiteralPath $InstallRoot).Path.TrimEnd('\')
$installedExe = Join-Path $resolvedRoot 'current/UniversalSpellCheck.exe'
if (-not (Test-Path -LiteralPath $installedExe -PathType Leaf)) {
    throw "Installed production executable not found: $installedExe"
}
function Read-ProductVersion([string]$Path) {
    $version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($Path).ProductVersion
    if (-not $version) { throw "Executable has no product version: $Path" }
    return $version.Split('+')[0]
}
$installedVersion = Read-ProductVersion $installedExe
if ($installedVersion -ne $ExpectedVersion) {
    throw "Installed version is $installedVersion; expected $ExpectedVersion"
}
$installedProcesses = @(Get-CimInstance Win32_Process -Filter "Name = 'UniversalSpellCheck.exe'" |
    Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($resolvedRoot + '\', [StringComparison]::OrdinalIgnoreCase) })
if ($installedProcesses.Count -ne 1) {
    throw "Expected one installed production process; found $($installedProcesses.Count)"
}
$installedProcess = $installedProcesses[0]
$runningVersion = Read-ProductVersion $installedProcess.ExecutablePath
if ($runningVersion -ne $ExpectedVersion) {
    throw "Running binary version is $runningVersion; expected $ExpectedVersion"
}
if ($PSBoundParameters.ContainsKey('StartedAfter') -and $installedProcess.CreationDate -lt $StartedAfter) {
    throw 'Production process predates the update; a fresh restart was not verified'
}
[pscustomobject]@{
    expected_version = $ExpectedVersion
    installed_version = $installedVersion
    running_binary_version = $runningVersion
    pid = $installedProcess.ProcessId
    started_at = $installedProcess.CreationDate
    executable = $installedProcess.ExecutablePath
    status = 'verified_binary_and_process; verify matching fresh started log separately'
} | ConvertTo-Json
