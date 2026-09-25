<#
.SYNOPSIS
    Installs a packed RigShift release, starts it and asks it a few read-only questions, then uninstalls it.

.DESCRIPTION
    Runs in the release workflow before anything is published: an update that does not start would never reach anyone
    with the fix. With -PreviousSetup it first installs that version, lets it write its settings and a profile, and
    then installs the new setup over it - the path every user takes. Nothing here switches a display.

    It installs into %LocalAppData%\RigShift and writes to %AppData%\RigShift, so it belongs on a build machine,
    not on a PC that uses RigShift.
#>
param(
    [Parameter(Mandatory)] [string] $Setup,
    [Parameter(Mandatory)] [string] $ExpectedVersion,
    [string] $PreviousSetup
)

$ErrorActionPreference = 'Stop'
$installDir = Join-Path $env:LOCALAPPDATA 'RigShift'
$dataDir = Join-Path $env:APPDATA 'RigShift'
$exe = Join-Path $installDir 'current\RigShift.exe'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\RigShift'

# RigShift is a Windows app that writes to its parent's console. Redirected, the text arrives on stdout.
function Invoke-RigShift([string[]] $Arguments, [int] $TimeoutSeconds = 60) {
    $info = [System.Diagnostics.ProcessStartInfo]::new($exe)
    $Arguments | ForEach-Object { $info.ArgumentList.Add($_) }
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.UseShellExecute = $false
    $process = [System.Diagnostics.Process]::Start($info)
    $output = $process.StandardOutput.ReadToEndAsync()
    $errors = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        $process.Kill()
        throw "RigShift $Arguments did not end within $TimeoutSeconds s"
    }
    [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $output.Result + $errors.Result }
}

function Assert-Command([string[]] $Arguments) {
    $result = Invoke-RigShift $Arguments
    Write-Host "> RigShift $Arguments (exit $($result.ExitCode))"
    Write-Host $result.Output.Trim()
    if ($result.ExitCode -ne 0) { throw "RigShift $Arguments ended with exit code $($result.ExitCode)" }
    $result.Output
}

function Install-Setup([string] $File) {
    Write-Host "Installing $File"
    $process = Start-Process $File -ArgumentList '--silent' -PassThru -Wait
    if ($process.ExitCode -ne 0) { throw "$File ended with exit code $($process.ExitCode)" }
    if (-not (Test-Path $exe)) { throw "$exe is missing after the installation" }
}

# Setup may start the app itself; either way one tray instance must answer on its pipe.
function Start-Tray {
    if (-not (Get-Process RigShift -ErrorAction SilentlyContinue)) {
        Start-Process $exe -ArgumentList '--minimized'
    }
    $deadline = [datetime]::UtcNow.AddSeconds(60)
    while ($true) {
        $pipes = [System.IO.Directory]::GetFiles('\\.\pipe\') | Where-Object { $_ -like '*RigShift.*' }
        if ($pipes) { break }
        if ([datetime]::UtcNow -gt $deadline) { throw 'The tray app did not open its command pipe within 60 s' }
        Start-Sleep -Milliseconds 250
    }
}

function Stop-Tray {
    Get-Process RigShift -ErrorAction SilentlyContinue | Stop-Process -Force
    Get-Process RigShift -ErrorAction SilentlyContinue | Wait-Process -Timeout 30
}

# Errors of the whole run. Warnings are fine: a build machine has no sound devices and no NVIDIA driver.
function Assert-Log {
    $logs = Get-ChildItem (Join-Path $dataDir 'logs') -Filter 'rigshift-*.log' -ErrorAction SilentlyContinue
    if (-not $logs) { throw "No log file in $dataDir\logs" }
    $bad = $logs | Select-String -Pattern '\[(ERR|FTL)\]'
    if ($bad) {
        $logs | Get-Content | Write-Host
        throw "The log has errors:`n$($bad -join "`n")"
    }
}

if (Test-Path $uninstallKey) { throw "RigShift is installed already; run this on a clean machine" }

if ($PreviousSetup) {
    Install-Setup $PreviousSetup
    Start-Tray
    Assert-Command 'status' | Out-Null
    Assert-Command 'save', 'Smoke test' | Out-Null
    Stop-Tray
}

Install-Setup $Setup
Start-Tray
$version = (Invoke-RigShift '--version').Output.Trim()
Write-Host "Installed version: $version"
if (-not $version.StartsWith($ExpectedVersion)) { throw "Expected version $ExpectedVersion, got $version" }
Assert-Command 'status' | Out-Null
$list = Assert-Command 'list'
if ($PreviousSetup -and $list -notmatch 'Smoke test') { throw 'The profile saved by the previous version is gone' }
Assert-Log

Stop-Tray
$uninstall = (Get-ItemProperty $uninstallKey).QuietUninstallString
if (-not $uninstall) { throw 'No QuietUninstallString in the uninstall key' }
Write-Host "Uninstalling: $uninstall"
$process = Start-Process cmd.exe -ArgumentList '/c', $uninstall -PassThru -Wait
if ($process.ExitCode -ne 0) { throw "The uninstall ended with exit code $($process.ExitCode)" }
if (Test-Path $exe) { throw "$exe is still there after the uninstall" }
if (Test-Path $uninstallKey) { throw 'The uninstall key is still there' }
if (-not (Get-ChildItem (Join-Path $dataDir 'logs') -ErrorAction SilentlyContinue)) { throw 'The uninstall took the data folder along' }

Write-Host 'Smoke test passed'
