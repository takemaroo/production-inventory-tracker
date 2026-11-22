<#
.SYNOPSIS
  Starts the API in the Production environment on this machine only (127.0.0.1 and [::1]), after checking the
  environment variables it needs.

.DESCRIPTION
  Version 1 runs on localhost only (owner decision #119, security review SEC-03): plain HTTP is acceptable only
  because the traffic never leaves this machine. This script therefore binds the API to the loopback addresses and
  offers no way to choose another address. HTTPS and HSTS are required before the API is reachable from any other
  machine (docs/07-release.md).

  It always starts in Production and never through the launch profile in Properties\launchSettings.json, which
  would select Development (SEC-13): Development reads user-secrets and publishes the OpenAPI document anonymously.

  Checks before starting, each with a clear message:
    - JWT_SIGNING_KEY is set, at least 32 characters and without spaces (save one for your Windows user with
      scripts\new-signing-key.ps1 -SaveForUser);
    - if JWT_SIGNING_KEY is missing from this window but saved for your Windows user, it says so and how to pick
      it up, instead of suggesting a new key that would not match the saved one;
    - on the first start (no database file yet), ADMIN_EMAIL and ADMIN_PASSWORD are set: the API creates the
      first Manager from them;
    - after the first start, a warning if ADMIN_PASSWORD is still set: it is never read again (AC-02.2) and
      should be removed;
    - no Kestrel endpoint is configured through environment variables, which could add a non-local address.
  The API itself then checks the password policy and the tolerance, and stops with a message if either is wrong.

  Stop the API with Ctrl+C.

.PARAMETER Port
  The local port, 5133 by default (the port the sample requests in InventoryTracker.Api.http use).

.PARAMETER Published
  Run the published folder made by scripts\publish.ps1 instead of building and running from source.

.PARAMETER PublishFolder
  The published folder to run with -Published. Default: publish\win-x64 in the repository.

.EXAMPLE
  # From source (builds first):
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run.ps1

.EXAMPLE
  # The published, self-contained folder:
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run.ps1 -Published
#>
[CmdletBinding()]
param(
    [ValidateRange(1024, 65535)]
    [int] $Port = 5133,

    [switch] $Published,

    [string] $PublishFolder
)

. (Join-Path $PSScriptRoot 'common.ps1')

function Stop-WithMessage([string] $Message) {
    Write-Host "Not started: $Message" -ForegroundColor Red
    exit 1
}

# --- Environment: always Production (SEC-13). ---------------------------------------------------------------------
if ($env:ASPNETCORE_ENVIRONMENT -and $env:ASPNETCORE_ENVIRONMENT -ne 'Production') {
    Write-Host "ASPNETCORE_ENVIRONMENT was '$($env:ASPNETCORE_ENVIRONMENT)'; this script always runs Production." -ForegroundColor Yellow
}
$env:ASPNETCORE_ENVIRONMENT = 'Production'

# --- What to run: check the published folder first, before anything is created. --------------------------------
if ($Published) {
    if (-not $PublishFolder) { $PublishFolder = Join-Path $RepoRoot 'publish\win-x64' }
    if (-not (Test-Path -LiteralPath (Join-Path $PublishFolder 'InventoryTracker.Api.exe'))) {
        Stop-WithMessage "no published API at $(Join-Path $PublishFolder 'InventoryTracker.Api.exe'). Run scripts\publish.ps1 first."
    }
    # Turn a relative folder (publish\win-x64) into a full path now, against the folder you ran the script from.
    # The start step below moves into the folder, and a relative path would then point somewhere else.
    $PublishFolder = (Resolve-Path -LiteralPath $PublishFolder).ProviderPath
    $exe = Join-Path $PublishFolder 'InventoryTracker.Api.exe'
}

# --- Local addresses only (SEC-03, #119). ------------------------------------------------------------------------
# Kestrel endpoints set in configuration win over --urls, so one set through an environment variable could add an
# address such as 0.0.0.0. Refuse to start rather than guess. The host reads such settings under three prefixes
# (none, ASPNETCORE_, DOTNET_), and the section separator may be written "__" or ":". -match ignores letter case.
$kestrelVariables = @(Get-ChildItem Env: | Where-Object { $_.Name -match '^(ASPNETCORE_|DOTNET_)?Kestrel(__|:)' })
if ($kestrelVariables.Count -gt 0) {
    Stop-WithMessage "remove these environment variables, which could make the API listen on other addresses: $(($kestrelVariables | ForEach-Object Name) -join ', ')."
}
# Both loopback addresses, so "localhost" answers at once whichever address a client tries first.
$urls = "http://127.0.0.1:$Port;http://[::1]:$Port"

# --- The token signing key. ----------------------------------------------------------------------------------------
# Get-SigningKeyProblem (common.ps1) tells apart "not set anywhere" from "saved for your Windows user, but this
# window started before it was saved", which need opposite advice.
$keyProblem = Get-SigningKeyProblem
if ($keyProblem) {
    Stop-WithMessage $keyProblem
}

# --- The database and the first Manager. ---------------------------------------------------------------------------
try {
    $databasePath = Resolve-DatabasePath
}
catch {
    Stop-WithMessage $_.Exception.Message
}

# Only one copy of the API at a time (common.ps1, Assert-ApiStopped), checked before anything is created.
try {
    Assert-ApiStopped $databasePath
}
catch {
    Stop-WithMessage $_.Exception.Message
}

if (-not (Test-Path -LiteralPath $databasePath)) {
    # First start: the API will create the database and the first Manager from these two values (US-02).
    $missing = @('ADMIN_EMAIL', 'ADMIN_PASSWORD' | Where-Object { [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($_)) })
    if ($missing.Count -gt 0) {
        Stop-WithMessage "this is the first start (no database at $databasePath), so ADMIN_EMAIL and ADMIN_PASSWORD are needed to create the first Manager. Missing: $($missing -join ', ')."
    }
    $databaseFolder = Split-Path -Parent $databasePath
    if (-not (Test-Path -LiteralPath $databaseFolder)) {
        New-Item -ItemType Directory -Path $databaseFolder | Out-Null
    }
    Write-Host 'First start: the API will create the database and the first Manager.' -ForegroundColor Cyan
}
elseif (-not [string]::IsNullOrWhiteSpace($env:ADMIN_PASSWORD)) {
    Write-Host 'ADMIN_PASSWORD is still set. The database already exists, so it is never read again: remove it from your environment (docs/07-release.md, section 6, step 7).' -ForegroundColor Yellow
}

Write-Host "Environment: Production"
Write-Host "Database:    $databasePath"
Write-Host "Listening:   http://127.0.0.1:$Port and http://[::1]:$Port (this machine only)"
Write-Host 'Press Ctrl+C to stop.'

# --- Start. ---------------------------------------------------------------------------------------------------------
# --environment and --urls on the command line take precedence over any environment variable or settings file.
$appArguments = @('--environment', 'Production', '--urls', $urls)

if ($Published) {
    # The published app's content root (where it finds appsettings.json) is its working directory.
    Push-Location $PublishFolder
    try {
        & $exe @appArguments
    }
    finally {
        Pop-Location
    }
}
else {
    $project = Join-Path $RepoRoot 'src\InventoryTracker.Api\InventoryTracker.Api.csproj'
    # --no-launch-profile: ignore launchSettings.json, which would switch to Development (SEC-13).
    & dotnet run --project $project --configuration Release --no-launch-profile -- @appArguments
}

exit $LASTEXITCODE
