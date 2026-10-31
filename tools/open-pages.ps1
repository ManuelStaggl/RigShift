# Opens every page of a Debug build once, each in a fresh process with an empty data folder, and fails when the app
# ends or logs an unhandled exception. Debug only: the RIGSHIFT_PREVIEW_PAGE and RIGSHIFT_DATA_DIR hooks do not exist in Release.
# ./tools/open-pages.ps1 -Exe src/RigShift.Avalonia/bin/Debug/net10.0-windows10.0.26100.0/win-x64/RigShift.exe
param(
    [Parameter(Mandatory)][string]$Exe,
    [int]$Seconds = 8
)
$ErrorActionPreference = 'Stop'

$pages = 'Overview', 'Profiles', 'Games', 'FieldOfView', 'Settings', 'Help'
$failed = @()
foreach ($page in $pages) {
    $dataDir = Join-Path ([IO.Path]::GetTempPath()) "rigshift-pages-$page-$PID"
    Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory $dataDir | Out-Null
    $env:RIGSHIFT_DATA_DIR = $dataDir
    $env:RIGSHIFT_PREVIEW_PAGE = $page
    try {
        $app = Start-Process $Exe -PassThru
        Start-Sleep -Seconds $Seconds
        $exited = $app.HasExited
        if (-not $exited) { Stop-Process -Id $app.Id -Force; $app.WaitForExit() }
    }
    finally {
        Remove-Item Env:RIGSHIFT_DATA_DIR, Env:RIGSHIFT_PREVIEW_PAGE
    }

    $log = Get-ChildItem (Join-Path $dataDir 'logs') -Filter 'rigshift-*.log' -ErrorAction SilentlyContinue | Get-Content
    # Not every [ERR]: without a desktop session (RDP, SSH) reading the displays fails, which the pages handle.
    $errors = $log | Select-String -Pattern '\[FTL\]|Unhandled UI exception' -Context 0, 8
    if ($exited -or -not $log -or $errors) {
        Write-Host "::error::$page page: exited=$exited, log lines=$($log.Count)"
        if ($errors) { $errors | Write-Host } else { $log | Select-Object -Last 20 | Write-Host }
        $failed += $page
    }
    else {
        Write-Host "$page page opened"
        Remove-Item $dataDir -Recurse -Force
    }
}
if ($failed) { throw "Pages that failed: $($failed -join ', ')" }
