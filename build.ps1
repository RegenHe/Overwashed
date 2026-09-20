param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$gameRoot = Split-Path -Parent $projectDir
$managedDir = Join-Path $gameRoot 'Overcooked2_Data\Managed'
$bepInExCore = Join-Path $gameRoot 'BepInEx\core'
$localDotnetHome = Join-Path $gameRoot 'tmp\dotnet-home'
$outputDir = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $projectDir ("bin\" + $Configuration)
} else {
    $OutputDirectory
}
$output = Join-Path $outputDir 'Overwashed.dll'
$outputPdb = [System.IO.Path]::ChangeExtension($output, '.pdb')
$deployed = Join-Path $gameRoot 'BepInEx\plugins\Overwashed.dll'
$deployedPdb = Join-Path $gameRoot 'BepInEx\plugins\Overwashed.pdb'
$legacyOutput = Join-Path $outputDir 'Overcooked2.DishwasherBot.dll'
$legacyPdb = Join-Path $outputDir 'Overcooked2.DishwasherBot.pdb'
$legacyDeployed = Join-Path $gameRoot 'BepInEx\plugins\Overcooked2.DishwasherBot.dll'

# Keep every cache/temp write inside the game directory as requested.
$env:DOTNET_CLI_HOME = $localDotnetHome
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

$sdkRows = @(dotnet --list-sdks)
if ($LASTEXITCODE -ne 0 -or $sdkRows.Count -eq 0) {
    throw 'No .NET SDK was found. Roslyn csc.dll is required to compile the Mono-targeted plugin.'
}
$lastSdk = $sdkRows[$sdkRows.Count - 1]
if ($lastSdk -notmatch '^([^ ]+) \[(.+)\]$') {
    throw "Could not parse dotnet SDK location: $lastSdk"
}
$csc = Join-Path (Join-Path $Matches[2] $Matches[1]) 'Roslyn\bincore\csc.dll'
if (-not (Test-Path -LiteralPath $csc)) {
    throw "Roslyn compiler not found: $csc"
}

New-Item -ItemType Directory -Force -Path $localDotnetHome | Out-Null
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

$references = @(
    (Join-Path $managedDir 'mscorlib.dll'),
    (Join-Path $managedDir 'System.dll'),
    (Join-Path $managedDir 'System.Core.dll'),
    (Join-Path $bepInExCore 'BepInEx.dll'),
    (Join-Path $bepInExCore '0Harmony.dll'),
    (Join-Path $managedDir 'Assembly-CSharp.dll'),
    (Join-Path $managedDir 'UnityEngine.dll'),
    (Join-Path $managedDir 'UnityEngine.CoreModule.dll'),
    (Join-Path $managedDir 'UnityEngine.IMGUIModule.dll'),
    (Join-Path $managedDir 'UnityEngine.PhysicsModule.dll')
)

foreach ($reference in $references) {
    if (-not (Test-Path -LiteralPath $reference)) {
        throw "Required reference not found: $reference"
    }
}

$sources = @(Get-ChildItem -LiteralPath $projectDir -Filter '*.cs' -File | ForEach-Object { $_.FullName })
$compilerArgs = @(
    $csc,
    '/noconfig',
    '/nostdlib+',
    '/target:library',
    '/deterministic+',
    '/langversion:7.3',
    ("/out:" + $output)
)
if ($Configuration -eq 'Release') {
    # A release DLL must not contain a CodeView record pointing to a local PDB
    # path. Remove stale symbols as well so they cannot be published by mistake.
    $compilerArgs += @('/optimize+', '/debug-')
    if (Test-Path -LiteralPath $outputPdb) {
        Remove-Item -LiteralPath $outputPdb -Force
    }
} else {
    $compilerArgs += @('/optimize-', '/debug:portable', ("/pdb:" + $outputPdb))
}
$compilerArgs += $references | ForEach-Object { '/reference:' + $_ }
$compilerArgs += $sources

& dotnet @compilerArgs
if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE"
}

Copy-Item -LiteralPath $output -Destination $deployed -Force
if (Test-Path -LiteralPath $deployedPdb) {
    Remove-Item -LiteralPath $deployedPdb -Force
}
if (Test-Path -LiteralPath $legacyOutput) {
    Remove-Item -LiteralPath $legacyOutput -Force
}
if (Test-Path -LiteralPath $legacyPdb) {
    Remove-Item -LiteralPath $legacyPdb -Force
}
if (Test-Path -LiteralPath $legacyDeployed) {
    Remove-Item -LiteralPath $legacyDeployed -Force
}
Write-Host "Built:    $output"
Write-Host "Deployed: $deployed"
