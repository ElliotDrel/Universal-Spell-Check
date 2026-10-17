param(
    [Parameter(Mandatory=$true)][string]$RustExe,
    [Parameter(Mandatory=$true)][string]$BaselinePackage,
    [Parameter(Mandatory=$true)][string]$Dotnet,
    [Parameter(Mandatory=$true)][string]$DataDirectory,
    [Parameter(Mandatory=$true)][string]$Output,
    [ValidateSet('compute','local','e2e')][string]$Mode='compute',
    [switch]$AllowDesktop,
    [int]$Runs=0,
    [int]$Warmup=2
)
$ErrorActionPreference='Stop'
if ($Mode -ne 'compute' -and -not $AllowDesktop) { throw 'Desktop comparison requires -AllowDesktop and an explicitly idle computer.' }
if (Test-Path -LiteralPath $Output) { throw 'Output already exists; use a fresh directory.' }
$repo=Split-Path $PSScriptRoot -Parent
$cases=Join-Path $repo 'bench/baseline-cases.json'
$replacements=Join-Path $repo 'replacements.json'
$runner=Join-Path $BaselinePackage 'reference/safe-runner/UniversalSpellCheck.Bench.dll'
$fixtures=Join-Path $BaselinePackage 'api-a/fixtures.json'
foreach($path in @($RustExe,$runner,$Dotnet,$cases,$replacements,$fixtures)) { if(-not(Test-Path -LiteralPath $path)){throw "Required benchmark file missing: $path"} }
if($Runs -eq 0){$Runs=if($Mode -eq 'compute'){20}else{10}}
if($Runs -lt 1 -or $Warmup -lt 0){throw 'Invalid sample counts.'}
$env:DOTNET_ROOT=Split-Path ([IO.Path]::GetFullPath($Dotnet)) -Parent
$env:PATH="$env:DOTNET_ROOT;$env:PATH"
$protectedPaths=@((Join-Path $DataDirectory 'settings.json'),(Join-Path $DataDirectory 'apikey.dat'))
$before=@{}
foreach($path in $protectedPaths){if(Test-Path -LiteralPath $path){$before[$path]=(Get-FileHash -LiteralPath $path).Hash}}
$free=([IO.DriveInfo]::new([IO.Path]::GetPathRoot([IO.Path]::GetFullPath($Output)))).AvailableFreeSpace
if($free -lt 256MB){throw 'Free at least 256 MiB before a comparison block.'}
New-Item -ItemType Directory -Path $Output | Out-Null
$blocks=@(@{name='csharp-a';rust=$false},@{name='rust-a';rust=$true},@{name='rust-b';rust=$true},@{name='csharp-b';rust=$false})
try {
    foreach($block in $blocks){
        $directory=Join-Path $Output $block.name
        $arguments=@('--mode',$Mode,'--cases',$cases,'--runs',"$Runs",'--warmup',"$Warmup",'--output',$directory,'--model','gpt-4.1')
        if($Mode -ne 'e2e'){$arguments+=@('--fixtures',$fixtures)}
        if($Mode -ne 'compute'){$arguments+=@('--allow-desktop','--developer-logging')}
        $stdout=Join-Path $Output ($block.name+'.stdout.log')
        $stderr=Join-Path $Output ($block.name+'.stderr.log')
        if($block.rust){
            $kind=if($Mode -eq 'compute'){'--bench-compute'}else{'--bench-desktop'}
            $arguments=@($kind)+$arguments+@('--replacements',$replacements)
            if($Mode -ne 'compute'){$arguments+=@('--evidence-log-dir',(Join-Path $DataDirectory 'logs'))}
            if($Mode -eq 'e2e'){$arguments+=@('--settings',$DataDirectory)}
            # Call operator waits for a GUI-subsystem executable when its output is redirected.
            & $RustExe @arguments 1> $stdout 2> $stderr
        }else{
            $arguments=@($runner)+$arguments+@('--settings',$DataDirectory)
            & $Dotnet @arguments 1> $stdout 2> $stderr
        }
        $blockExit=$LASTEXITCODE
        if($blockExit -ne 0){
            $summaryPath=Join-Path $directory 'summary.json'
            if(-not(Test-Path -LiteralPath $summaryPath)){throw "Block $($block.name) ended with $blockExit before a summary."}
            $summary=Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
            $caseCount=@(Get-Content -LiteralPath $cases -Raw | ConvertFrom-Json).Count
            if($summary.aggregate.attempted -ne $Runs*$caseCount){throw "Block $($block.name) is incomplete (exit $blockExit). Preserve its diagnostics."}
            Write-Output "Retained completed failures in $($block.name); timing statistics describe successes only."
        }
        Write-Output "Completed $($block.name)."
    }
    & python (Join-Path $repo 'bench/baseline_compare.py') (Join-Path $Output 'csharp-a') (Join-Path $Output 'rust-a') > (Join-Path $Output 'comparison-a.txt')
    if($LASTEXITCODE -ne 0){throw 'Comparison A rejected incompatible or incomplete blocks.'}
    & python (Join-Path $repo 'bench/baseline_compare.py') (Join-Path $Output 'csharp-b') (Join-Path $Output 'rust-b') > (Join-Path $Output 'comparison-b.txt')
    if($LASTEXITCODE -ne 0){throw 'Comparison B rejected incompatible or incomplete blocks.'}
}
finally{
    $integrity=foreach($path in $before.Keys){[pscustomobject]@{path=$path;unchanged=((Get-FileHash -LiteralPath $path).Hash -eq $before[$path])}}
    $integrity | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Output 'settings-integrity.json') -Encoding utf8
    if(@($integrity|Where-Object{-not $_.unchanged}).Count -ne 0){throw 'User settings or encrypted credentials changed unexpectedly; stop benchmarking.'}
}
