# Commits changes to the metadata or documentation to a new branch
# and exports the branch name and version information for use in 
# downstream (downpipe?) workflows. Wound up not using this but
# keeping it around in case it becomes useful again

param(
    [Parameter(Mandatory)]
    [string]$PackageName,

    [Parameter(Mandatory)]
    [string]$Extension      #probably always "winmd" or "msgpack"
)

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

$versionFile = "$repoRoot/metadata/$PackageName.version"
if (!(Test-Path $versionFile)) {
    Write-Host "Version file not found: $versionFile"
    exit 0
}

$version = (Get-Content $versionFile -Raw).Trim()
$file = "$repoRoot/metadata/*.$Extension"

git add $file
git add $versionFile
git status -s

# Run git diff-index and capture exit code
git diff-index --quiet HEAD --
$exitCode = $LASTEXITCODE

if ($exitCode -ne 0) {
    $branchName = "update-$($PackageName.Split(".")[-1])-$version".ToLower()

    git checkout -B $branchName

    # Export the branch name to GitHub Actions
    Add-Content -Path $env:GITHUB_OUTPUT -Value "branchName=$branchName"
    Add-Content -Path $env:GITHUB_OUTPUT -Value "version=$version"

    $msg = "Update $PackageName to $version"
    git commit -m $msg
    #git push
} else {
    Write-Host "No changes to commit for $PackageName"
}
