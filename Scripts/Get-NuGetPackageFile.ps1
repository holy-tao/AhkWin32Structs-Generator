# Downloads a .nuget package and extracts a file into a directory. Also writes a
# version file to prevent unnecessary downloads.
param(
    [Parameter(Mandatory=$true)]
    [string]$PackageName,

    [Parameter(Mandatory=$true)]
    [string]$FileExtension,

    [Parameter(Mandatory=$true)]
    [string]$DestDir
)

$packageId = $PackageName.ToLower()
$nugetBaseUrl = "https://api.nuget.org/v3-flatcontainer/$packageId"

# Version tracking
$versionFile = Join-Path $DestDir "$($PackageName).version"

# Ensure destination exists
New-Item -ItemType Directory -Force -Path $DestDir | Out-Null

# 1. Fetch latest version
Write-Host "Fetching available versions for $PackageName..."
$versionsUrl = "$nugetBaseUrl/index.json"
$versionsResponse = Invoke-RestMethod -Uri $versionsUrl
$latestVersion = $versionsResponse.versions | Sort-Object -Descending | Select-Object -First 1

Write-Host "Latest version: $latestVersion"

# 2. Check stored version
$storedVersion = if (Test-Path $versionFile) { Get-Content $versionFile -Raw } else { "" }
$storedVersion = $storedVersion.Trim()

if ($storedVersion -eq $latestVersion) {
    # Verify the desired file still exists
    $existing = Get-ChildItem -Path $DestDir -Filter "*$FileExtension" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($existing) {
        Write-Host "Already up-to-date ($storedVersion). Skipping download."
        exit 0
    }
}

Write-Host "Updating from $storedVersion to $latestVersion"

# 3. Download .nupkg
$downloadUrl = "$nugetBaseUrl/$latestVersion/$packageId.$latestVersion.nupkg"
$downloadPath = Join-Path $env:TEMP "$packageId.$latestVersion.zip"  # renamed to .zip
Write-Host "Downloading $downloadUrl ..."
Invoke-WebRequest -Uri $downloadUrl -OutFile $downloadPath

# 4. Extract
$extractDir = Join-Path $env:TEMP "$packageId-$latestVersion"
if (Test-Path $extractDir) { Remove-Item -Recurse -Force $extractDir }
Write-Host "Extracting to $extractDir ..."
Expand-Archive -Path $downloadPath -DestinationPath $extractDir -Force

# 5. Find target file(s)
$foundFile = Get-ChildItem -Path $extractDir -Recurse -Filter "*$FileExtension" | Select-Object -First 1
if (-not $foundFile) {
    Write-Error "No file with extension '$FileExtension' found in package!"
    exit 1
}

# 6. Copy to destination
Copy-Item $foundFile.FullName -Destination $DestDir -Force
$latestVersion | Out-File -FilePath $versionFile -Encoding utf8

Write-Host "Downloaded and updated $($foundFile.Name)"
Write-Host "Version info saved to $versionFile"
