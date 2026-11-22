<#
.SYNOPSIS
  Restores NuGet packages and builds the whole solution in Release, with every warning treated as an error.

.DESCRIPTION
  Same steps as the continuous-integration workflow (.github/workflows/ci.yml), so a build that passes here
  passes there. "-warnaserror" makes the build fail on any warning, including CS1591 (a public member without a
  /// comment), which enforces the owner's comment standard (architecture section 6: the build has 0 warnings).

.PARAMETER ArtifactsPath
  Optional folder for all build output instead of each project's bin\ and obj\. Use a separate folder when two
  builds run at the same time in one working tree, or they lock each other's files (decision #67).

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build.ps1

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build.ps1 -ArtifactsPath TestResults\my-art
#>
[CmdletBinding()]
param(
    [string] $ArtifactsPath
)

. (Join-Path $PSScriptRoot 'common.ps1')

$extra = @()
if ($ArtifactsPath) { $extra = @('--artifacts-path', $ArtifactsPath) }

Push-Location $RepoRoot
try {
    # Restore downloads the exact package versions listed in the .csproj files (the SDK version comes from global.json).
    Invoke-Checked 'dotnet' (@('restore', 'InventoryTracker.slnx') + $extra)
    Invoke-Checked 'dotnet' (@('build', 'InventoryTracker.slnx', '--no-restore', '--configuration', 'Release', '-warnaserror') + $extra)
    Write-Host 'Build succeeded with 0 warnings.' -ForegroundColor Green
}
finally {
    Pop-Location
}
