<#
.SYNOPSIS
  Shared helpers for the other scripts in this folder. Not meant to be run on its own.

.DESCRIPTION
  The other scripts "dot-source" this file (". $PSScriptRoot\common.ps1"), which runs it inside their own scope,
  so the functions below become available to them. Keeping these here means the database location is worked
  out in exactly one place, and run, backup and restore always agree on it.

  Every script in this folder is plain ASCII on purpose: Windows PowerShell 5.1 reads a script file without a
  byte-order mark as the old Windows code page, so a typographic dash or quote would be garbled.

  PowerShell concepts used across these scripts, explained once here:
    - The block comment at the top of each script, like this one, is "comment-based help": Get-Help reads its .SYNOPSIS,
      .PARAMETER and .EXAMPLE sections, so the documentation lives next to the code it describes.
    - [CmdletBinding()] makes a script behave like a built-in command: it gains the common switches such as
      -Verbose and -ErrorAction, and an unknown parameter is refused instead of silently ignored.
    - param(...) declares the script's parameters. Each can have a type ([int], [string], [switch]), a default
      value and validation attributes such as [ValidateRange(1024, 65535)], which PowerShell checks before any
      line of the script runs.
    - $PSScriptRoot is an automatic variable holding the folder of the running script file. Building paths from
      it means a script works whatever folder you start it from.
#>

# Stop at the first error instead of carrying on with half the work done.
$ErrorActionPreference = 'Stop'
# Strict mode turns typos in variable names into errors instead of silently empty values.
Set-StrictMode -Version Latest

# The repository root is the folder above scripts\.
$script:RepoRoot = Split-Path -Parent $PSScriptRoot

function Get-ApiProcess {
    <#
    .SYNOPSIS
      Every running copy of the API on this machine: the published InventoryTracker.Api.exe, or dotnet.exe running
      the project or its DLL (dotnet run, or "dotnet InventoryTracker.Api.dll").
    .DESCRIPTION
      A file lock alone cannot tell whether the API is running. EF Core keeps idle database connections in a pool
      and closes them after a few idle minutes; SQLite then folds the write-ahead log into the main file and lets
      go of it, although the API is still serving and will reopen the file on the next request. So the scripts
      check for the process itself as well.
      Win32_Process is Windows' list of processes; Get-CimInstance reads it with each process's command line.
      The test project (InventoryTracker.Api.Tests) is not the API and is not matched.
    #>
    $apiPattern = 'InventoryTracker\.Api(?!\.Tests)'
    @(Get-CimInstance -ClassName Win32_Process | Where-Object {
        $_.Name -eq 'InventoryTracker.Api.exe' -or
        ($_.Name -eq 'dotnet.exe' -and $_.CommandLine -and $_.CommandLine -match $apiPattern)
    })
}

function Assert-ApiStopped {
    <#
    .SYNOPSIS
      Stops the calling script with a clear message if the API is running, or if any program has the database open.
    .DESCRIPTION
      Used by run.ps1 (never two APIs on one database: the sign-in lock-out lives in one process, architecture
      risk 9) and by backup and restore (a copy taken, or a file moved, under a live API could lose or corrupt
      data). It refuses even when the running API uses another database: stop every copy first.
    #>
    param([Parameter(Mandatory)] [string] $DatabasePath)

    $running = @(Get-ApiProcess)
    if ($running.Count -gt 0) {
        $ids = ($running | ForEach-Object { "$($_.Name) (process id $($_.ProcessId))" }) -join ', '
        throw "The API is running: $ids. Stop it first (Ctrl+C in its window)."
    }
    if (Test-FileInUse $DatabasePath) {
        throw "The database $DatabasePath is open in another program. Close it first."
    }
}

function Invoke-Checked {
    <#
    .SYNOPSIS
      Runs an external program (dotnet, for example) and stops the script if it fails.
    .DESCRIPTION
      PowerShell does not stop on its own when an external program fails: it only records the program's exit
      code in $LASTEXITCODE. This wrapper checks it, so a failed build never looks like a success.
    #>
    param(
        [Parameter(Mandatory)] [string] $FilePath,
        [string[]] $Arguments = @()
    )
    Write-Host "> $FilePath $($Arguments -join ' ')" -ForegroundColor DarkGray
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "'$FilePath $($Arguments -join ' ')' failed with exit code $LASTEXITCODE."
    }
}

function Get-DefaultDatabasePath {
    <#
    .SYNOPSIS
      Where the Production database lives when ConnectionStrings__Default is not set:
      %LOCALAPPDATA%\InventoryTracker\inventory.db.
    .DESCRIPTION
      Outside the repository (so it can never be committed or wiped by a clean checkout) and inside the user's
      own profile, which Windows already restricts to that user, SYSTEM and administrators. The file holds
      password hashes (security review SEC-16), so it must not sit in a shared or synced folder.
    #>
    Join-Path $env:LOCALAPPDATA 'InventoryTracker\inventory.db'
}

function Resolve-DatabasePath {
    <#
    .SYNOPSIS
      Returns the absolute path of the SQLite database file the API will use, and makes sure the
      ConnectionStrings__Default environment variable points at it.
    .DESCRIPTION
      The API reads its connection string from the configuration key ConnectionStrings:Default. An environment
      variable named ConnectionStrings__Default feeds that key (a double underscore stands for the ':' that
      separates configuration sections). If the variable is not set, this function sets it, for this
      PowerShell process and the API it starts, to the default path above.
    #>
    $connection = $env:ConnectionStrings__Default
    if ([string]::IsNullOrWhiteSpace($connection)) {
        $path = Get-DefaultDatabasePath
        $env:ConnectionStrings__Default = "Data Source=$path"
        return $path
    }

    # A SQLite connection string looks like "Data Source=C:\path\inventory.db;Mode=ReadWriteCreate".
    $match = [regex]::Match($connection, '(?i)(?:^|;)\s*(?:Data Source|DataSource|Filename)\s*=\s*([^;]+)')
    if (-not $match.Success) {
        throw "ConnectionStrings__Default is set but has no 'Data Source=...' part: '$connection'."
    }
    $path = $match.Groups[1].Value.Trim().Trim('"')

    # A relative path would be resolved by the API against its content root, which differs between running
    # from source and running the published folder. An absolute path removes that surprise.
    if (-not [System.IO.Path]::IsPathRooted($path)) {
        throw "ConnectionStrings__Default must use an absolute path, for example 'Data Source=C:\InventoryData\inventory.db'. Current value: '$connection'."
    }
    return $path
}

function Test-FileInUse {
    <#
    .SYNOPSIS
      True when another process (normally the running API) has the file open.
    .DESCRIPTION
      Opening the file with FileShare None asks Windows for exclusive access. SQLite keeps its database open
      for as long as the API runs, so the request fails exactly when the API is running.
    #>
    param([Parameter(Mandatory)] [string] $Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    try {
        $stream = [System.IO.File]::Open($Path, 'Open', 'Read', 'None')
        $stream.Dispose()
        return $false
    }
    catch [System.IO.IOException] {
        return $true
    }
}

function Get-DatabaseFiles {
    <#
    .SYNOPSIS
      The database file plus the side files SQLite may keep next to it, for those that exist.
    .DESCRIPTION
      In write-ahead-log mode (set by the API at start-up) recent changes can sit in inventory.db-wal until
      SQLite folds them into the main file. A clean stop of the API does that and removes the side files; after
      a crash they remain and belong to the database. A backup or restore must therefore treat all of them
      as one set.
    #>
    param([Parameter(Mandatory)] [string] $Path)
    foreach ($candidate in @($Path, "$Path-wal", "$Path-shm", "$Path-journal")) {
        if (Test-Path -LiteralPath $candidate) { Get-Item -LiteralPath $candidate }
    }
}
