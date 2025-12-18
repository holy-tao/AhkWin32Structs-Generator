# Gets the latest WinRT Metadata from the NuGet package (so major versions only) and merge the outputs into a single
# .winmd file
# https://www.nuget.org/packages/Microsoft.Windows.SDK.Contracts

# See also https://github.com/microsoft/windows-rs/blob/6893e4b55a66bece203555da697b571aff7304af/crates/libs/bindgen/default/readme.md

# Requires mdmerge: https://learn.microsoft.com/en-us/windows/win32/midl/mdmerge-and-metadata-files

$ErrorActionPreference = "Stop"
Import-Module -name "$PSScriptRoot\Modules\Git-Utils.psm1"

$repoRoot = Get-RepoRoot

# Download metadata

$tmpDir = Join-Path $env:TEMP "WinRTMetadata-Tmp-$(New-Guid)"
New-Item -Path $tmpDir -ItemType Directory

& "$PSScriptRoot\Get-NuGetPackageFile.ps1" `
    -PackageName "Microsoft.Windows.SDK.Contracts" `
    -FileExtension ".winmd" `
    -DestDir $tmpDir

Write-Host "Downloaded WinRT metadata to $tmpDir"

$destDir = Join-Path $repoRoot "metadata"

& "$PSScriptRoot\Ext\mdmerge.exe -i '$tmpDir' -o '$destDir' -n 1"

Copy-Item -Path "$tmpDir\Microsoft.Windows.SDK.Contracts.version" -Destination $destDir
Remove-Item -Path $tmpDir -Force