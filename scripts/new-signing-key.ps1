<#
.SYNOPSIS
  Makes a new random token signing key (48 random bytes, Base64: 64 characters) for JWT_SIGNING_KEY. With
  -SaveForUser it saves the key for your Windows user itself and never shows it; without it, it prints the key.

.DESCRIPTION
  The API signs every sign-in token with JWT_SIGNING_KEY and refuses to start without a key of at least 32
  characters. Generate a separate key on every machine and for every instance: test and real instances must
  never share one, because a token signed by one would be accepted by the other (security review SEC-13).
  Never use a phrase, and never put the key in a file that git tracks.

  Use -SaveForUser for the key you keep. The key is generated and saved inside this one script, so it never passes
  through another program's output. (Capturing that output is risky: if PowerShell cannot find the script file, it
  prints its start-up banner and an error instead, and that text would be saved as the "key".) If a key is already
  saved, it is kept unless you add -Force, because replacing the key signs everybody out.

  Without -SaveForUser the key is printed, so it can be captured into one window's variable for a quick test.
  Run that form from the repository root, so the path scripts\new-signing-key.ps1 exists.

  The key comes from the operating system's cryptographic random number generator, not from Get-Random,
  which is predictable.

.PARAMETER SaveForUser
  Save the new key as JWT_SIGNING_KEY for your Windows user instead of printing it.

.PARAMETER Force
  With -SaveForUser: replace a key that is already saved. Everybody is signed out.

.EXAMPLE
  # The key you keep, saved for your Windows user (then open a new window):
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\new-signing-key.ps1 -SaveForUser

.EXAMPLE
  # A quick test in the current window only (from the repository root):
  $env:JWT_SIGNING_KEY = powershell -NoProfile -ExecutionPolicy Bypass -File scripts\new-signing-key.ps1
#>
[CmdletBinding()]
param(
    [switch] $SaveForUser,
    [switch] $Force
)

. (Join-Path $PSScriptRoot 'common.ps1')

if ($Force -and -not $SaveForUser) {
    Write-Host 'Not done: -Force only applies together with -SaveForUser.' -ForegroundColor Red
    exit 1
}

if ($SaveForUser) {
    try {
        # Save-NewSigningKeyForUser (common.ps1) generates and saves the key, and returns only the lines to show.
        Save-NewSigningKeyForUser -Force:$Force | ForEach-Object { Write-Host $_ }
    }
    catch {
        Write-Host "Not saved: $($_.Exception.Message)" -ForegroundColor Red
        exit 1
    }
    exit 0
}

# Written to the output (not the screen only), so the caller can capture it straight into a variable.
New-SigningKey
