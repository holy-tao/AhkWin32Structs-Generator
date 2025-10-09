
function Get-RepoRoot{
    $repoRoot = (git rev-parse --show-toplevel 2>$null).Trim()
    if (-not $repoRoot) {
        throw "Not inside a Git repository."
    }

    return $repoRoot
}