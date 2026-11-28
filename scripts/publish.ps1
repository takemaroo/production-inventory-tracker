<#
.SYNOPSIS
  Publishes the API into one folder that can be copied and run.

.DESCRIPTION
  By default the folder is self-contained for 64-bit Windows: it carries its own copy of the .NET runtime, pinned
  to -RuntimeVersion, so it runs even where .NET is not installed, and it does not depend on the runtime installed
  on the machine. That is how the published API runs on a patched runtime (security review SEC-06) even while this
  machine's own runtime is older. Raise -RuntimeVersion (the default below) when Microsoft ships a new 10.0 patch,
  usually on the second Tuesday of each month, and publish again.

  With -FrameworkDependent the folder holds only the app, and the host's own .NET runtime runs it
  ("dotnet InventoryTracker.Api.dll"). Use that for Azure App Service, which installs and patches the runtime itself.

  Nothing is deleted: files in the output folder are overwritten. Never keep the database inside the output folder.

  (v1.2) The web front end is built first (Build-FrontEnd in common.ps1, which needs Node.js 24.15 or later) into
  src\InventoryTracker.Api\wwwroot, and dotnet publish copies that folder into the output's wwwroot. The published
  API then serves the front end at its own address, so "run.ps1 -Published" is still one process serving both (A-1).
  The machine that runs the published folder needs no Node.js.

.PARAMETER RuntimeVersion
  The .NET and ASP.NET Core runtime version to bundle in a self-contained folder. At least 10.0.15.

.PARAMETER Runtime
  The target platform of a self-contained folder: win-x64 (default), win-arm64, linux-x64 and so on.

.PARAMETER FrameworkDependent
  Publish the app without a runtime, for any platform with .NET 10 installed.

.PARAMETER Output
  The output folder. Default: publish\<Runtime>, or publish\framework-dependent.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\publish.ps1

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\publish.ps1 -FrameworkDependent
#>
[CmdletBinding()]
param(
    [ValidatePattern('^10\.0\.\d+$')]
    [string] $RuntimeVersion = '10.0.15',

    [string] $Runtime = 'win-x64',

    [switch] $FrameworkDependent,

    [string] $Output
)

. (Join-Path $PSScriptRoot 'common.ps1')

if ([version]$RuntimeVersion -lt [version]'10.0.15') {
    throw "RuntimeVersion $RuntimeVersion is older than 10.0.15, the minimum the security review accepts (SEC-06)."
}

if (-not $Output) {
    $Output = if ($FrameworkDependent) { Join-Path $RepoRoot 'publish\framework-dependent' } else { Join-Path $RepoRoot "publish\$Runtime" }
}
# Make a relative -Output (publish\win-x64-2026-12-01) a full path against PowerShell's current folder, which is
# where you ran the command from. (.NET's own GetFullPath would use the process's folder, which can differ.) The
# folder need not exist yet; dotnet publish creates it.
$Output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)

# A database inside the output folder is in the wrong place: a build folder is easy to delete or replace by
# mistake. Stop and say so rather than publish over it.
if (Test-Path -LiteralPath $Output) {
    $databases = @(Get-ChildItem -LiteralPath $Output -Filter '*.db*' -File -ErrorAction SilentlyContinue)
    if ($databases.Count -gt 0) {
        throw "The output folder $Output contains database files ($(($databases | ForEach-Object Name) -join ', ')). Move them out first (docs/07-release.md, data)."
    }
}

# (v1.2) The front end's built files must be in the API's wwwroot before dotnet publish copies that folder.
Build-FrontEnd

$project = Join-Path $RepoRoot 'src\InventoryTracker.Api\InventoryTracker.Api.csproj'
$arguments = @('publish', $project, '--configuration', 'Release', '--output', $Output, '-warnaserror')
if ($FrameworkDependent) {
    $arguments += @('--self-contained', 'false')
}
else {
    # RuntimeFrameworkVersion selects the exact runtime packs (Microsoft.NETCore.App and Microsoft.AspNetCore.App)
    # copied into the folder; without it the SDK bundles the runtime version it shipped with.
    $arguments += @('--runtime', $Runtime, '--self-contained', 'true', "-p:RuntimeFrameworkVersion=$RuntimeVersion")
}

Invoke-Checked 'dotnet' $arguments

# (v1.2) Check the front end arrived: without its page the published API would serve the API only.
if (-not (Test-Path -LiteralPath (Join-Path $Output 'wwwroot\index.html'))) {
    throw "The published folder has no wwwroot\index.html, so it would serve no front end. Check the front-end build output above."
}

if (-not $FrameworkDependent) {
    $kestrel = Get-Item -LiteralPath (Join-Path $Output 'Microsoft.AspNetCore.Server.Kestrel.Core.dll')
    Write-Host "Bundled ASP.NET Core runtime: $($kestrel.VersionInfo.ProductVersion.Split('+')[0])" -ForegroundColor Green
}
Write-Host "Published to $Output" -ForegroundColor Green
