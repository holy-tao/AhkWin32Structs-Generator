# Gets the latest Win32 Api Docs from NuGet (major versions only)
# https://www.nuget.org/packages/Microsoft.Windows.SDK.Win32Metadata

$ErrorActionPreference = "Stop"

# Get the repository root directory using git
try {
    $repoRoot = (git rev-parse --show-toplevel 2>$null).Trim()
    if (-not $repoRoot) {
        throw "Not inside a Git repository."
    }
}
catch {
    Write-Error "Failed to determine Git repository root. Ensure 'git' is installed and this script is run inside a repo."
    exit 1
}

$destDir = Join-Path $repoRoot "metadata"

& "$PSScriptRoot\Get-NuGetPackageFile.ps1" `
    -PackageName "Microsoft.Windows.SDK.Win32Docs" `
    -FileExtension ".msgpack" `
    -DestDir $destDir
