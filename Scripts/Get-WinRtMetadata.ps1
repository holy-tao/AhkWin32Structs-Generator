# Gets the latest WinRT Metadata from the NuGet package (so major versions only) and merge the outputs into a single
# .winmd file
# https://www.nuget.org/packages/Microsoft.Windows.SDK.Contracts

# See also https://github.com/microsoft/windows-rs/blob/6893e4b55a66bece203555da697b571aff7304af/crates/libs/bindgen/default/readme.md

# Requires mdmerge to be installed: https://learn.microsoft.com/en-us/windows/win32/midl/mdmerge-and-metadata-files

$ErrorActionPreference = "Stop"

$PackageName = "Microsoft.Windows.SDK.Contracts"
$tmpDir = $null
$mergeOutputDir = $null

try {
    Import-Module -name "$PSScriptRoot\Modules\Git-Utils.psm1" -Force
    Import-Module -name "$PSScriptRoot\Modules\Metadata-Utils.psm1" -Force

    $repoRoot = Get-RepoRoot
    $destDir = Join-Path $repoRoot "metadata"
    $mdmerge = Find-MdMerge

    Write-Host "Using mdmerge.exe from: $mdmerge"

    # Download metadata

    $tmpDir = Join-Path $env:TEMP "WinRTMetadata-Tmp-$(New-Guid)"
    New-Item -Path $tmpDir -ItemType Directory | Out-Null

    # Copy existing version file to temp dir so version check works
    $versionFile = Join-Path $destDir "$PackageName.version"
    if (Test-Path $versionFile) {
        Copy-Item $versionFile (Join-Path $tmpDir "$PackageName.version")
    }

    $downloaded = Get-NuGetPackageFile `
        -PackageName $PackageName `
        -FileExtension ".winmd" `
        -DestDir $tmpDir `
        -SkipFileExistenceCheck

    if (-not $downloaded) {
        Write-Host "No updates needed. Windows.winmd is already up-to-date."
        return
    }

    Write-Host "Downloaded WinRT metadata to $tmpDir"

    $mergeOutputDir = Join-Path $env:TEMP "WinRTMetadata-Merge-$(New-Guid)"
    New-Item -Path $mergeOutputDir -ItemType Directory | Out-Null

    Write-Host "Merging dowloaded .winmd files with mdmerge"
    & $mdmerge -i $tmpDir -o $mergeOutputDir -n 1
    if ($LASTEXITCODE -ne 0) {
        throw "mdmerge.exe failed with exit code $LASTEXITCODE"
    }

    # Copy only the merged Windows.winmd file to preserve existing Win32/Wdk metadata
    Copy-Item -Path "$mergeOutputDir\Windows.winmd" -Destination "$destDir\Windows.winmd" -Force
    Copy-Item -Path "$tmpDir\$PackageName.version" -Destination "$destDir\$PackageName.version"
}
catch {
    Write-Error $_.Exception.Message
    exit 1
}
finally {
    # Clean up temporary directories
    if ($tmpDir -and (Test-Path $tmpDir)) {
        Remove-Item -Path $tmpDir -Force -Recurse -ErrorAction SilentlyContinue
    }
    if ($mergeOutputDir -and (Test-Path $mergeOutputDir)) {
        Remove-Item -Path $mergeOutputDir -Force -Recurse -ErrorAction SilentlyContinue
    }
}