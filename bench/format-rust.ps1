param([string]$Rustfmt, [switch]$Write)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
if($Rustfmt) {
    if(!(Test-Path -LiteralPath $Rustfmt -PathType Leaf)){throw 'Formatter executable missing'}
    $arguments=@('--edition','2024')
    if(!$Write){$arguments+='--check'}
    $arguments+=@((Join-Path $root 'rust/src/main.rs'), (Join-Path $root 'rust/build.rs'), (Join-Path $root 'rust/tests/dpi_resources.rs'))
    & $Rustfmt @arguments
} else {
    $cargo=Get-Command cargo -ErrorAction SilentlyContinue
    if(!$cargo){throw 'Cargo is unavailable; provide -Rustfmt with a verified pinned portable formatter'}
    $arguments=@('fmt','--manifest-path',(Join-Path $root 'rust/Cargo.toml'))
    if(!$Write){$arguments+='--check'}
    & $cargo.Source @arguments
}
if($LASTEXITCODE -ne 0){throw "Rust formatting failed: $LASTEXITCODE"}
