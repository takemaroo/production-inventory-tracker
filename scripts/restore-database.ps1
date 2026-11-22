<#
.SYNOPSIS
  Puts a backup made by backup-database.ps1 back in place of the Production database.

.DESCRIPTION
  Stop the API first. Nothing is deleted: the current database files are moved into a "before-restore-<time>"
  folder next to the database, so a restore can itself be undone.

  The side files (inventory.db-wal, -shm) of the current database are moved aside too. Leaving an old -wal file
  next to a restored database would mix two different states and could corrupt it.

.PARAMETER BackupFolder
  The backup folder to restore, for example %LOCALAPPDATA%\InventoryTracker\backups\inventory-20261122-093000.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\restore-database.ps1 -BackupFolder "$env:LOCALAPPDATA\InventoryTracker\backups\inventory-20261122-093000"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $BackupFolder
)

. (Join-Path $PSScriptRoot 'common.ps1')

$databasePath = Resolve-DatabasePath
$databaseName = Split-Path -Leaf $databasePath

$backupDatabase = Join-Path $BackupFolder $databaseName
if (-not (Test-Path -LiteralPath $backupDatabase)) {
    throw "The backup folder $BackupFolder has no $databaseName."
}
# Refuses while any copy of the API runs, even an idle one that has let go of the file (common.ps1): moving a
# live database aside would lose whatever the API writes next.
Assert-ApiStopped $databasePath

$databaseFolder = Split-Path -Parent $databasePath
if (-not (Test-Path -LiteralPath $databaseFolder)) {
    New-Item -ItemType Directory -Path $databaseFolder | Out-Null
}

# Move the current files aside (never delete them).
$current = @(Get-DatabaseFiles $databasePath)
if ($current.Count -gt 0) {
    $aside = Join-Path $databaseFolder ('before-restore-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Path $aside | Out-Null
    foreach ($file in $current) {
        Move-Item -LiteralPath $file.FullName -Destination $aside
    }
    Write-Host "Moved the current database files to $aside"
}

# Copy the backup set (the database and any side files saved with it) into place.
$restored = @(Get-DatabaseFiles $backupDatabase)
foreach ($file in $restored) {
    Copy-Item -LiteralPath $file.FullName -Destination $databaseFolder
}

Write-Host "Restored $($restored.Count) file(s) to $databaseFolder" -ForegroundColor Green
Write-Host 'Start the API again with scripts\run.ps1. It applies any newer migrations to the restored database on start.'
