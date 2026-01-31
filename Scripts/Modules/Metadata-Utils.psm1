function Find-MdMerge {
    <#
    .SYNOPSIS
    Finds the most recent x64 version of mdmerge.exe.
    
    .DESCRIPTION
    Searches the versioned subdirectories of "C:\Program Files (x86)\Windows Kits\10\bin\" for
    the most recent version and retrieves the path to the installed mdmerge.exe.
    
    .OUTPUTS
    Returns the path to the found executable.
    #>
    
    $sdkRoot = "C:\Program Files (x86)\Windows Kits\10\bin\"

    if (-not (Test-Path $sdkRoot)) {
        throw "Windows SDK bin directory not found at: $sdkRoot"
    }

    # Get all version directories (e.g., 10.0.26100.0)
    $versionDirs = Get-ChildItem -Path $sdkRoot -Directory |
        Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
        Sort-Object { [Version]$_.Name } -Descending

    if ($versionDirs.Count -eq 0) {
        throw "No SDK version directories found in: $sdkRoot"
    }

    # Check each version directory for x64\mdmerge.exe, starting with the latest
    foreach ($versionDir in $versionDirs) {
        $mdmergePath = Join-Path $versionDir.FullName "x64\mdmerge.exe"
        if (Test-Path $mdmergePath) {
            return $mdmergePath
        }
    }

    throw "mdmerge.exe not found in any SDK version directory"
}

function Get-NuGetPackageFile {
    <#
    .SYNOPSIS
    Downloads a NuGet package and extracts files with a specific extension.

    .DESCRIPTION
    Downloads a .nupkg package and extracts files with the specified extension into a directory.
    Also writes a version file to prevent unnecessary downloads.

    .PARAMETER PackageName
    The name of the NuGet package to download

    .PARAMETER FileExtension
    The file extension to extract (e.g., ".winmd", ".msgpack")

    .PARAMETER DestDir
    The destination directory for extracted files

    .PARAMETER SkipFileExistenceCheck
    If true, skips checking if files exist in the destination directory when versions match.
    Useful when downloading to a temporary directory for processing.

    .OUTPUTS
    Returns $true if files were downloaded/updated, $false if already up-to-date
    #>
    param(
        [Parameter(Mandatory=$true)]
        [string]$PackageName,

        [Parameter(Mandatory=$true)]
        [string]$FileExtension,

        [Parameter(Mandatory=$true)]
        [string]$DestDir,

        [Parameter(Mandatory=$false)]
        [switch]$SkipFileExistenceCheck
    )

    $packageId = $PackageName.ToLower()
    $nugetBaseUrl = "https://api.nuget.org/v3-flatcontainer/$packageId"

    # Version tracking
    $versionFile = Join-Path $DestDir "$($PackageName).version"
    if(-not (Test-Path $versionFile)) {
        Write-Warning -Message "No version file found for $PackageName (checked '$versionFile')"
    }

    # Ensure destination exists
    New-Item -ItemType Directory -Force -Path $DestDir | Out-Null

    # 1. Fetch latest version
    Write-Host "Fetching available versions for $PackageName..."
    $versionsUrl = "$nugetBaseUrl/index.json"
    $versionsResponse = Invoke-RestMethod -Uri $versionsUrl
    $latestVersion = $versionsResponse.versions | Select-Object -Last 1

    Write-Host "Latest version: $latestVersion"

    # 2. Check stored version
    $storedVersion = if (Test-Path $versionFile) { Get-Content $versionFile -Raw } else { "" }
    $storedVersion = $storedVersion.Trim()

    if ($storedVersion -eq $latestVersion) {
        if ($SkipFileExistenceCheck) {
            Write-Host "Already up-to-date ($storedVersion). Skipping download."
            return $false
        }

        # Verify the desired file still exists
        $existing = Get-ChildItem -Path $DestDir -Filter "*$FileExtension" -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($existing) {
            Write-Host "Already up-to-date ($storedVersion). Skipping download."
            return $false
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
    $totalFiles = 0

    Write-Host "Copying $FileExtension files to $DestDir..."
    Get-ChildItem -Path $extractDir -Recurse -Filter "*$FileExtension" | Foreach-Object {
        Write-Host "`t$($_.Name)"
        Copy-Item $_.FullName -Destination $DestDir -Force
        $totalFiles++
    }

    if($totalFiles -eq 0){
        throw "Failed to find files with extension $FileExtension"
    }

    Write-Host "Copied $totalFiles file(s)"

    # 6. Save version info
    $latestVersion | Out-File -FilePath $versionFile -Encoding utf8

    Write-Host "Version info saved to $versionFile"

    return $true
}

Export-ModuleMember -Function Find-MdMerge, Get-NuGetPackageFile