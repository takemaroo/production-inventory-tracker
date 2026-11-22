<#
.SYNOPSIS
  Copies the Production database (and any SQLite side files) into a new, time-stamped backup folder.

.DESCRIPTION
  Stop the API first (Ctrl+C in its window). A copy taken while the API writes could be inconsistent, so the
  script refuses to run while the database is open.

  The database holds every user's email and password hash (security review SEC-16). Each backup needs the same
  protection as the machine: keep it in your user profile or on an encrypted drive (BitLocker), and never in a
  shared, public or unencrypted cloud-synced folder.

.PARAMETER BackupRoot
  The folder that receives the backup folders. Default: %LOCALAPPDATA%\InventoryTracker\backups.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\backup-database.ps1
#>
[CmdletBinding()]
param(
    [string] $BackupRoot = (Join-Path $env:LOCALAPPDATA 'InventoryTracker\backups')
)

. (Join-Path $PSScriptRoot 'common.ps1')

# Same rule as run.ps1: ConnectionStrings__Default if set, otherwise the default location.
$databasePath = Resolve-DatabasePath
if (-not (Test-Path -LiteralPath $databasePath)) {
    throw "There is no database at $databasePath. If you use another location, set ConnectionStrings__Default as you do for run.ps1."
}
# Refuses while any copy of the API runs, even an idle one that has let go of the file (common.ps1).
Assert-ApiStopped $databasePath

$target = Join-Path $BackupRoot ('inventory-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $target | Out-Null

$files = @(Get-DatabaseFiles $databasePath)
foreach ($file in $files) {
    Copy-Item -LiteralPath $file.FullName -Destination $target
}

Write-Host "Backed up $($files.Count) file(s) from $databasePath" -ForegroundColor Green
Write-Host "to $target"
Write-Host 'It contains password hashes: keep it as protected as this machine.' -ForegroundColor Yellow
