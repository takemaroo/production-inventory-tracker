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

# (v1.2) The web front end's folder: its package.json, its tests, and the build that writes the API's wwwroot.
$script:FrontEndRoot = Join-Path $RepoRoot 'src\InventoryTracker.Web'

# (v1.2) The oldest Node.js the front end's tools accept (jsdom, used by the tests, needs 24.15 or later).
$script:MinimumNodeVersion = [version]'24.15.0'

function Test-SavedUserVariable {
    <#
    .SYNOPSIS
      True when a variable is saved for your Windows user (the 'User' scope), whatever this window holds.
    .DESCRIPTION
      Windows keeps two kinds of environment variable that matter here:
        - the process's own copy, $env:NAME, which every program receives from the program that started it, once,
          at the moment it starts;
        - the saved user variables ([Environment]::SetEnvironmentVariable(NAME, value, 'User')), stored in your
          Windows profile. A window opened after they were saved (from the Start menu or Explorer) gets them; a
          program started from a window that was already open gets that window's older copy instead.
      So a window opened before a variable was saved does not have it. VS Code is the common case: its terminals,
      new tabs included, inherit VS Code's environment from when VS Code itself started.
      Only true or false comes out of this function: the value itself is never returned or printed.
    #>
    param([Parameter(Mandatory)] [string] $Name)
    -not [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($Name, 'User'))
}

function Set-SavedUserVariable {
    <#
    .SYNOPSIS
      Saves a variable for your Windows user (the 'User' scope), so every window opened from now on has it.
    .DESCRIPTION
      A one-line wrapper, kept separate so the logic that decides whether to save (Save-NewSigningKeyForUser) can be
      tested with a stand-in that does not touch the real saved variables.
    #>
    param([Parameter(Mandatory)] [string] $Name, [Parameter(Mandatory)] [string] $Value)
    [Environment]::SetEnvironmentVariable($Name, $Value, 'User')
}

function New-SigningKey {
    <#
    .SYNOPSIS
      A new random token signing key: 48 random bytes, as 64 Base64 characters.
    .DESCRIPTION
      The bytes come from the operating system's cryptographic random number generator, not from Get-Random,
      which is predictable.
    #>
    $bytes = New-Object byte[] 48
    $generator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $generator.GetBytes($bytes)
    }
    finally {
        $generator.Dispose()
    }
    [Convert]::ToBase64String($bytes)
}

function Save-NewSigningKeyForUser {
    <#
    .SYNOPSIS
      Generates a key and saves it as JWT_SIGNING_KEY for your Windows user. Returns the lines to show; never the key.
    .DESCRIPTION
      Used by new-signing-key.ps1 -SaveForUser. The key is generated and saved inside this one PowerShell process,
      so nothing is captured from the output of another program. (Capturing "powershell -File ..." output is how a
      mistyped path once saved PowerShell's start-up banner as a key: when the script file is not found,
      powershell.exe prints its banner and an error instead, and that text became the "key".)
      Replacing an existing key signs everybody out, so an existing saved key is kept unless -Force is given.
      Throws on any failure; the caller turns that into a message and a non-zero exit code.
    #>
    param([switch] $Force)

    if ((Test-SavedUserVariable 'JWT_SIGNING_KEY') -and -not $Force) {
        throw ('A JWT_SIGNING_KEY is already saved for your Windows user, so it was kept. Replacing it signs everybody out. ' +
            'To replace it anyway, run this again with -Force.')
    }

    Set-SavedUserVariable 'JWT_SIGNING_KEY' (New-SigningKey)

    # Confirm the save without reading the value back: only whether something is now saved.
    if (-not (Test-SavedUserVariable 'JWT_SIGNING_KEY')) {
        throw 'Windows did not save JWT_SIGNING_KEY for your user.'
    }

    @(
        'Saved a new JWT_SIGNING_KEY for your Windows user (the key itself is not shown).',
        'Open a new PowerShell window to use it (in VS Code, close and reopen VS Code completely: a new terminal tab is not enough),',
        'or load it into this window with:',
        "    `$env:JWT_SIGNING_KEY = [Environment]::GetEnvironmentVariable('JWT_SIGNING_KEY', 'User')"
    )
}

function Get-SigningKeyProblem {
    <#
    .SYNOPSIS
      Checks JWT_SIGNING_KEY in this window. Returns nothing when it is usable, otherwise the message to show.
    .DESCRIPTION
      The cases need different advice:
        - set in this window but invalid: shorter than 32 characters (the API's own rule, architecture section 6), or
          containing spaces. A generated key never has spaces; a value with spaces is a phrase, or text captured by
          mistake (such as PowerShell's start-up banner), and either is guessable (security review SEC-13).
          What to do depends on whether a key is also saved for your Windows user:
            - nothing saved: save a generated key for your user;
            - a key is saved: the bad value may be only in this window (for example from the quick-test command run
              in the wrong folder) while the saved key is fine. Replacing the saved key would then sign everybody
              out and still leave the bad value here. The script cannot tell which value is bad without reading
              the saved key, which it never does. So the advice is: first use the saved key (clear this window's
              value or open a new window), and replace the saved key with -Force only if the message comes back;
        - missing here but saved for your Windows user: this window was most likely opened before it was saved
          (the same happens after Remove-Item Env:JWT_SIGNING_KEY). Making a new key now would be wrong: it would
          not match the saved one, and tokens would stop working at the next start. So the message says how to
          pick up the saved key instead, and the script does not load it silently, so you see what happened;
        - set nowhere: save a random key for your user once, then open a new window.
    #>
    $newLine = [Environment]::NewLine
    $key = $env:JWT_SIGNING_KEY
    if (-not [string]::IsNullOrWhiteSpace($key)) {
        $reason = $null
        if ($key.Length -lt 32) {
            $reason = 'JWT_SIGNING_KEY in this window is shorter than 32 characters.'
        }
        elseif ($key -match '\s') {
            $reason = @(
                'JWT_SIGNING_KEY in this window contains spaces, so it is not a generated key: it is a phrase, or text captured by mistake',
                '(running the key script from the wrong folder captures PowerShell''s start-up banner). Either can be guessed.'
            ) -join $newLine
        }
        if (-not $reason) {
            return $null
        }

        if (Test-SavedUserVariable 'JWT_SIGNING_KEY') {
            return @(
                $reason,
                'A key is also saved for your Windows user. Do not replace it yet: the bad value may be only in this window',
                '(for example from the quick-test command run in the wrong folder), and replacing the saved key signs everybody out.',
                'First use the saved key: clear this window''s value and load the saved one,',
                '    Remove-Item Env:JWT_SIGNING_KEY',
                "    `$env:JWT_SIGNING_KEY = [Environment]::GetEnvironmentVariable('JWT_SIGNING_KEY', 'User')",
                'or open a new PowerShell window (in VS Code, close and reopen VS Code completely), then run this again.',
                'Only if this message comes back, the saved key itself is the bad one. Then replace it, from the repository root:',
                '    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\new-signing-key.ps1 -SaveForUser -Force',
                'and open a new PowerShell window. Replacing the key signs everybody out.'
            ) -join $newLine
        }

        return @(
            $reason,
            'Replace it with a generated key saved for your Windows user, from the repository root:',
            '    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\new-signing-key.ps1 -SaveForUser',
            'then open a new PowerShell window (in VS Code, close and reopen VS Code completely) and run this again.'
        ) -join $newLine
    }

    if (Test-SavedUserVariable 'JWT_SIGNING_KEY') {
        return @(
            'JWT_SIGNING_KEY is saved for your Windows user, but this window does not have it: most likely it was opened before the key was saved.',
            'Do not make a new key: it would not match the saved one, and sign-in tokens would stop working at the next start.',
            'Either open a new PowerShell window and run this again (in VS Code, close and reopen VS Code completely: a new terminal tab is not enough),',
            'or load the saved key into this window with:',
            "    `$env:JWT_SIGNING_KEY = [Environment]::GetEnvironmentVariable('JWT_SIGNING_KEY', 'User')"
        ) -join $newLine
    }

    return @(
        'JWT_SIGNING_KEY is not set. Save a random key for your Windows user once, from the repository root:',
        '    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\new-signing-key.ps1 -SaveForUser',
        'then open a new PowerShell window (in VS Code, close and reopen VS Code completely) and run this again.',
        'Only for a quick test in this window, a window-only key works too (also from the repository root); every new one signs everybody out:',
        '    $env:JWT_SIGNING_KEY = powershell -NoProfile -ExecutionPolicy Bypass -File scripts\new-signing-key.ps1'
    ) -join $newLine
}

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

function Get-NpmPath {
    <#
    .SYNOPSIS
      (v1.2) Checks that Node.js 24.15 or later is installed and returns the full path of npm.cmd. Throws otherwise.
    .DESCRIPTION
      Building and testing the web front end needs Node.js, which brings npm, its package manager. The running product
      does not: the published folder holds the built files and the API serves them (PRD section 18).
      npm.cmd is called rather than plain "npm" because the npm.ps1 that Node.js also installs may be blocked by the
      machine's script execution policy, while a .cmd file is not.
    #>
    $node = Get-Command 'node.exe' -ErrorAction SilentlyContinue
    if (-not $node) {
        throw ('Node.js is not installed, and the web front end needs it to build. Install the LTS version 24 (free) from ' +
            'https://nodejs.org, then open a new PowerShell window (docs/03-architecture.md, part v1.2).')
    }
    $installed = [version](& $node.Source '--version').Trim().TrimStart('v')
    if ($installed -lt $MinimumNodeVersion) {
        throw "Node.js $installed is installed; the web front end needs $MinimumNodeVersion or later. Install the LTS version 24 from https://nodejs.org."
    }
    $npm = Join-Path (Split-Path -Parent $node.Source) 'npm.cmd'
    if (-not (Test-Path -LiteralPath $npm)) {
        throw "npm.cmd was not found next to $($node.Source). Reinstall Node.js from https://nodejs.org."
    }
    $npm
}

function Invoke-FrontEndNpm {
    <#
    .SYNOPSIS
      (v1.2) Runs one npm command in the front end's folder, src\InventoryTracker.Web, and stops the script if it fails.
    .EXAMPLE
      Invoke-FrontEndNpm @('run', 'build')
    #>
    param([Parameter(Mandatory)] [string[]] $Arguments)
    $npm = Get-NpmPath
    Push-Location $FrontEndRoot
    try {
        Invoke-Checked $npm $Arguments
    }
    finally {
        Pop-Location
    }
}

function Build-FrontEnd {
    <#
    .SYNOPSIS
      (v1.2) Installs the front end's packages exactly as package-lock.json lists them, checks its types and builds it
      into src\InventoryTracker.Api\wwwroot, where the API serves it from (A-1).
    .DESCRIPTION
      "npm ci" (clean install) deletes node_modules and installs exactly the versions in package-lock.json, so every
      machine, CI included, builds with the same packages. It never changes package.json or package-lock.json.
      "npm run build" runs the TypeScript check and then Vite, which empties wwwroot and writes the new files.
    #>
    Invoke-FrontEndNpm @('ci', '--no-audit', '--no-fund')
    Invoke-FrontEndNpm @('run', 'build')
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
      Opening the file with FileShare None asks Windows for exclusive access, which fails while any program has the
      file open. A busy API keeps its database open, but an idle one lets go of it after a few minutes (see
      Get-ApiProcess), so a free file does not prove the API is stopped. Assert-ApiStopped therefore checks the
      process first and uses this only to catch other programs, such as a database browser.
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
