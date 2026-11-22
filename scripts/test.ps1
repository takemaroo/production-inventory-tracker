<#
.SYNOPSIS
  Builds the solution (0 warnings, see build.ps1) and runs the full test suite.

.DESCRIPTION
  The suite needs no secrets and no database setup: each test starts the API in memory with its own temporary
  SQLite file and test-only values for JWT_SIGNING_KEY, ADMIN_EMAIL and ADMIN_PASSWORD (TestApiFactory), in the
  "Testing" environment, so your own environment variables and user-secrets are never used.
  It takes about 2 to 4 minutes on the owner's machine; the performance tests run last, on their own.

.PARAMETER ArtifactsPath
  Optional folder for all build and test output. Passed to both the build and the test run, which must agree.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test.ps1

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test.ps1 -ArtifactsPath TestResults\my-art
#>
[CmdletBinding()]
param(
    [string] $ArtifactsPath
)

. (Join-Path $PSScriptRoot 'common.ps1')

& (Join-Path $PSScriptRoot 'build.ps1') -ArtifactsPath $ArtifactsPath

$extra = @()
if ($ArtifactsPath) { $extra = @('--artifacts-path', $ArtifactsPath) }

Push-Location $RepoRoot
try {
    # --no-build: test exactly what build.ps1 just produced, rather than building a second time.
    Invoke-Checked 'dotnet' (@('test', '--solution', 'InventoryTracker.slnx', '--no-build', '--configuration', 'Release') + $extra)
    Write-Host 'All tests passed.' -ForegroundColor Green
}
finally {
    Pop-Location
}
