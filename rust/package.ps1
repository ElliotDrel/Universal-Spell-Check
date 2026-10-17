param(
    [Parameter(Mandatory=$true)][string]$Output,
    [switch]$Dev,
    [switch]$SkipBuild
)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
if(Test-Path -LiteralPath $Output){throw 'Output already exists; choose a new candidate folder.'}
if(-not $SkipBuild){
    $buildArgs=@('build','--locked','--manifest-path',(Join-Path $PSScriptRoot 'Cargo.toml'),'--release')
    if(-not $Dev){$buildArgs+='--no-default-features'}
    & cargo @buildArgs
    if($LASTEXITCODE -ne 0){throw 'Rust release build failed.'}
}
$binary=Join-Path $PSScriptRoot 'target/release/universal-spell-check.exe'
if(-not(Test-Path -LiteralPath $binary)){throw 'Release executable is missing.'}
New-Item -ItemType Directory -Path $Output | Out-Null
Copy-Item -LiteralPath $binary -Destination (Join-Path $Output 'UniversalSpellCheck.exe')
Copy-Item -LiteralPath (Join-Path $repo 'replacements.json') -Destination $Output
$licenses=Join-Path $Output 'licenses'
New-Item -ItemType Directory -Path $licenses | Out-Null
Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'assets') -Filter '*OFL*' | Copy-Item -Destination $licenses
Write-Output "Prepared local candidate: $Output"
