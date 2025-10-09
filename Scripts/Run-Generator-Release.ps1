# Compiles the generator in release mode and runs it, targeting
# the AhkWin32Projection submodule

$ErrorActionPreference = "Stop"

Import-Module -name "$PSScriptRoot\Modules\Git-Utils.psm1"

$repoRoot = Get-RepoRoot

$projectPath = Join-Path $repoRoot "Generator\AhkWin32Structs.csproj"
$exePath = Join-Path $repoRoot "Generator\bin\release\net9.0\AhkWin32Structs.exe"
$metadataDirPath = Join-Path $repoRoot "metadata"
$outputDirectory = Join-Path $repoRoot "AhkWin32projection"

# Build project
& "dotnet" "build" -c "release" $projectPath

# Run project
& $exePath $metadataDirPath $outputDirectory