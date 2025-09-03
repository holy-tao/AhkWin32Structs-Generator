# Looks through every .ahk file in the given directory and all its subdirectories and runs
# AutoHotkey64.exe /Validate on them with warnings (except for local same as global) enabled,
# and prints errors and warnings to the console. Warnings are printed to stdout, errors to
# stderr

param(
    #Path to AutoHotkey64.exe
    [Parameter(Mandatory=$true)]
    [string]$AhkExePath,

    #Directory containing .ahk files to validate
    [Parameter(Mandatory=$true)]
    [string]$Directory
)

function Get-AutoHotkeyProcStartInfo {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $AhkExePath
    $psi.Arguments = "/ErrorStdOut /Validate *"
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false

    return $psi
} 

$ahkFiles = Get-ChildItem -Path $Directory -Recurse -Filter "*.ahk" -File
Write-Output "Validating $($ahkFiles.Count) files..."

$i = 0

foreach($file in $ahkFiles){
    # Generated files don't warn and I don't want them to, but I do want to see warnings here
    $scriptText = "#Warn VarUnset, StdOut`n#Warn Unreachable, StdOut`n#Include $($File.FullName)"

    $psi = Get-AutoHotkeyProcStartInfo

    $proc = [System.Diagnostics.Process]::Start($psi)
    $proc.StandardInput.Write($scriptText)
    $proc.StandardInput.Close()

    $stdout = $proc.StandardOutput.ReadToEnd()
    $stderr = $proc.StandardError.ReadToEnd()
    $proc.WaitForExit()

    if (-not [string]::IsNullOrWhiteSpace($stdout)) {
        Write-Output $stdout
    }
    if (-not [string]::IsNullOrWhiteSpace($stderr)) {
        # Write-Error prints a stack, which I don't want
        $Host.UI.WriteErrorLine($stderr)
    }

    if(++$i % 1000 -eq 0){
        Write-Output "Validated $i files"
    }
}

Write-Output "Done"