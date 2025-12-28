# Compiles the parameterized interface identifier precomputer and runs it

$ErrorActionPreference = "Stop"

Import-Module -name "$PSScriptRoot\Modules\Git-Utils.psm1"

$repoRoot = Get-RepoRoot

$projectPath = Join-Path $repoRoot "PiidPrecompute\PiidPrecompute.csproj"
$exePath = Join-Path $repoRoot "PiidPrecompute\bin\release\net10.0\PiidPrecompute.exe"
$metadataDirPath = Join-Path $repoRoot "metadata"
$outputDirectory = Join-Path $repoRoot "metadata\piids.yml"

# Build project
& "dotnet" "build" -c "release" $projectPath

# Run project
& $exePath $metadataDirPath $outputDirectory