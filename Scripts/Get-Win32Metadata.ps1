# Gets the latest Win32Metadata from the NuGet package (so major versions only)
# https://www.nuget.org/packages/Microsoft.Windows.SDK.Win32Metadata/

$ErrorActionPreference = "Stop"
Import-Module -name "$PSScriptRoot\Modules\Git-Utils.psm1"

$repoRoot = Get-RepoRoot

$destDir = Join-Path $repoRoot "metadata"

& "$PSScriptRoot\Get-NuGetPackageFile.ps1" `
    -PackageName "Microsoft.Windows.SDK.Win32Metadata" `
    -FileExtension ".winmd" `
    -DestDir $destDir