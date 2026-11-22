<#
.SYNOPSIS
  Prints a new random token signing key (48 random bytes, Base64: 64 characters) for JWT_SIGNING_KEY.

.DESCRIPTION
  The API signs every sign-in token with JWT_SIGNING_KEY and refuses to start without a key of at least 32
  characters. Generate a separate key on every machine and for every instance: test and real instances must
  never share one, because a token signed by one would be accepted by the other (security review SEC-13).
  Never use a phrase, and never put the key in a file that git tracks.

  The key comes from the operating system's cryptographic random number generator, not from Get-Random,
  which is predictable.

  Changing the key later signs everybody out (their tokens no longer verify); nothing else is lost.

.EXAMPLE
  # For the current PowerShell window only:
  $env:JWT_SIGNING_KEY = powershell -NoProfile -ExecutionPolicy Bypass -File scripts\new-signing-key.ps1

.EXAMPLE
  # Kept for your Windows user, for every new window from now on:
  [Environment]::SetEnvironmentVariable('JWT_SIGNING_KEY', (powershell -NoProfile -ExecutionPolicy Bypass -File scripts\new-signing-key.ps1), 'User')
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$bytes = New-Object byte[] 48
$generator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
try {
    $generator.GetBytes($bytes)
}
finally {
    $generator.Dispose()
}

# Written to the output (not the screen only), so the caller can capture it straight into a variable.
[Convert]::ToBase64String($bytes)
