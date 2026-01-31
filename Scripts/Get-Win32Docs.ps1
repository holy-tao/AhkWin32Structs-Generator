# Gets the latest Win32 Api Docs from NuGet (major versions only)
# https://www.nuget.org/packages/Microsoft.Windows.SDK.Win32Metadata

$ErrorActionPreference = "Stop"

Import-Module -name "$PSScriptRoot\Modules\Git-Utils.psm1" -Force
Import-Module -name "$PSScriptRoot\Modules\Metadata-Utils.psm1" -Force

$repoRoot = Get-RepoRoot

$destDir = Join-Path $repoRoot "metadata"

Get-NuGetPackageFile `
    -PackageName "Microsoft.Windows.SDK.Win32Docs" `
    -FileExtension ".msgpack" `
    -DestDir $destDir | Out-Null
