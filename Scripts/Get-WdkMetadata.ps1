# Gets the latest WDK Metadata from the NuGet package (so major versions only)
# https://www.nuget.org/packages/Microsoft.Windows.WDK.Win32Metadata/

$ErrorActionPreference = "Stop"

try {
    Import-Module -name "$PSScriptRoot\Modules\Git-Utils.psm1" -Force
    Import-Module -name "$PSScriptRoot\Modules\Metadata-Utils.psm1" -Force

    $repoRoot = Get-RepoRoot

    $destDir = Join-Path $repoRoot "metadata"

    Get-NuGetPackageFile `
        -PackageName "Microsoft.Windows.WDK.Win32Metadata" `
        -FileExtension ".winmd" `
        -DestDir $destDir | Out-Null
}
catch {
    Write-Error $_.Exception.Message
    exit 1
}