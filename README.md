# Production & Inventory Tracker API

An HTTP API for a small food manufacturer that turns bulk raw materials into packaged products. It tracks stock in the Warehouse and in production storage, plans how much raw material each production order should use, nets leftover in production storage against the next order, and records and flags every difference between what was planned, used and produced. It is for the production and inventory manager, and for the warehouse and production line staff who record what they receive, send, produce and have left over.

Version 1 is an API only, with no user interface. The full rules are in the product requirements, [`docs/01-prd.md`](docs/01-prd.md). How to use the API day to day is in the [user guide](docs/08-user-guide.md). What is in this version is in the [release notes](docs/09-release-notes.md).

## Terms used here

- **Warehouse** and **production storage** are the two stock locations. Stock in each is the sum of its movements and never goes below zero.
- **Manager**, **Warehouse** and **Production** are the three roles. One user can hold several.
- **First Manager**: the one account the API creates by itself, on its first start, from `ADMIN_EMAIL` and `ADMIN_PASSWORD`.
- **Signing key**: `JWT_SIGNING_KEY`, the secret that signs every sign-in token.

## Prerequisites

| Tool | Version | Check with |
|------|---------|------------|
| Windows | 11, 64-bit (the only system it has been run on) | |
| .NET SDK | 10.0 (`global.json` accepts 10.0.100 or any later 10.0 SDK) | `dotnet --version` |
| ASP.NET Core runtime | 10.0.15 or later recommended (security review SEC-06). The folder made by `scripts\publish.ps1` bundles its own 10.0.15 runtime, so the published API is patched even if the installed runtime is older. Development runs and the tests use the installed runtime. | `dotnet --list-runtimes`, then `dotnet sdk check` |
| Windows PowerShell | 5.1, built into Windows (PowerShell 7 has not been tested) | `$PSVersionTable.PSVersion` |
| git | any recent version | `git --version` |
| Optional | VS Code with the REST Client extension, or Visual Studio 2022 17.12 or later, for the sample requests | |

Download the .NET 10 SDK from <https://dotnet.microsoft.com/download/dotnet/10.0>.

## Quick start on Windows

Every command runs in PowerShell from the repository root.

**Why the long form of every script command.** Windows' default execution policy refuses to run script files, so `.\scripts\test.ps1` fails with "running scripts is disabled on this system". `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test.ps1` allows scripts for that one command only and changes no setting on your machine. `-NoProfile` skips your personal PowerShell profile, so the script runs the same way everywhere.

1. **Get the code and run the tests** (about 2 to 4 minutes; no secrets needed):

   ```powershell
   git clone <repository-url> production-inventory-tracker
   cd production-inventory-tracker
   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test.ps1
   ```

   It ends with `Build succeeded with 0 warnings.` and `All tests passed.`

2. **Publish** a self-contained folder, `publish\win-x64`, with the 10.0.15 runtime inside:

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\publish.ps1
   ```

   It ends with `Bundled ASP.NET Core runtime: 10.0.15` and `Published to ...\publish\win-x64`.

3. **Set the variables for the first start.**

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\new-signing-key.ps1 -SaveForUser
   $env:JWT_SIGNING_KEY = [Environment]::GetEnvironmentVariable('JWT_SIGNING_KEY', 'User')   # this window too
   $env:ADMIN_EMAIL    = 'you@yourcompany.example'
   $env:ADMIN_PASSWORD = Read-Host 'First Manager password'   # not saved in the command history
   ```

   - The first line makes a random signing key and saves it for your Windows user, so every window you open later has the same key and tokens survive a restart. It prints only `Saved a new JWT_SIGNING_KEY ...`, never the key. Run it **once per machine**, from the repository root. If a key is already saved it keeps it and says so ([Signing key](#signing-key)).
   - The second line loads the saved key into this window, which was opened before the key was saved and so does not have it yet.
   - `ADMIN_EMAIL` and `ADMIN_PASSWORD` are for this window and this first start only. Write down the exact email: it is the one you sign in with. The password must have at least 8 characters, with an upper-case letter, a lower-case letter and a digit. `Read-Host` keeps it out of the command history but shows it while you type; run `Clear-Host` afterwards.
   - Only for a quick test in one window, from the repository root, `$env:JWT_SIGNING_KEY = powershell -NoProfile -ExecutionPolicy Bypass -File scripts\new-signing-key.ps1` instead of the first two lines makes a window-only key. It is lost when the window closes, and every new key signs everybody out.

4. **Start the API**:

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run.ps1 -Published
   ```

   On the first start it says `First start: the API will create the database and the first Manager.`, applies the database migrations, logs `Created the first Manager (user ...)`, then shows `Now listening on: http://127.0.0.1:5133`. The warning `Failed to determine the https port for redirect.` appears once and is expected (v1 has no HTTPS, see [Localhost only](#localhost-only)). Leave this window open while the API runs; stop it with Ctrl+C.

5. **Check it and sign in**, in a second PowerShell window:

   ```powershell
   Invoke-RestMethod http://localhost:5133/health
   $body  = @{ email = 'you@yourcompany.example'; password = (Read-Host 'Password') } | ConvertTo-Json
   $token = $null
   try {
       $token = (Invoke-RestMethod -Method Post http://localhost:5133/api/v1/auth/sign-in -ContentType 'application/json' -Body $body).accessToken
       'Signed in.'
   }
   catch {
       # No answer at all (the API is not running, or the address or port is wrong): show PowerShell's own error.
       if ($null -eq $_.Exception.Response) { throw }
       # Windows PowerShell 5.1 shows only the status, such as "(401) Unauthorized". The API's reason is in the response body: read it.
       # PowerShell has already read the body once while building the error, so rewind the stream to its start first.
       $stream = $_.Exception.Response.GetResponseStream(); $stream.Position = 0
       (ConvertFrom-Json (New-Object System.IO.StreamReader($stream)).ReadToEnd()).detail
   }
   ```

   `/health` answers `Healthy`. On success the snippet prints `Signed in.` and `$token` holds a bearer token, valid for 8 hours. When sign-in is refused it prints the API's reason instead:

   - `Invalid email or password.` The email or the password is not the one the account was created with. The email must be exactly the `ADMIN_EMAIL` of the very first start (letter case does not matter). The first Manager is created once, with the values set at that moment. Changing `ADMIN_EMAIL` or `ADMIN_PASSWORD` later does nothing, because once a user exists the API never reads them again.
   - `Account temporarily locked. Try again later.` Five wrong passwords in a row lock the account for 15 minutes, even for the right password. **Stop retrying**: each try while it is locked changes nothing. Wait 15 minutes, then sign in with the right values.

   This snippet and the change-password one under [First password](#first-password) read the response the way Windows PowerShell 5.1 provides it, which is what this machine has; PowerShell 7 reports errors differently.

6. **Change the first password and remove `ADMIN_PASSWORD`** at once. See [First password](#first-password).

More detail, including later starts, upgrades and troubleshooting: [`docs/07-release.md`](docs/07-release.md), sections [2 (commands)](docs/07-release.md#2-commands), [3 (configuration)](docs/07-release.md#3-configuration) and [6 (deploying on the owner's machine)](docs/07-release.md#6-deploying-on-the-owners-machine).

## Localhost only

Version 1 runs on this machine only. `scripts\run.ps1` binds the API to `http://127.0.0.1:5133` and `http://[::1]:5133` and offers no way to choose another address. Traffic is plain HTTP, which is acceptable only because it never leaves the machine (owner decision #119).

Before the API is reachable from any other machine (another PC, the office network, the internet, a cloud host), these are **required**: HTTPS, HSTS and a sign-in rate limit. All three need product changes. The security review ([`docs/06-security-review.md`](docs/06-security-review.md)) must then be re-opened. See [`docs/07-release.md` section 9](docs/07-release.md#9-before-the-api-is-reachable-from-any-other-machine).

## First password

The first Manager's password has been typed into a terminal and held in an environment variable. Straight after the first sign-in:

1. Change it (`$token` from quick start step 5):

   ```powershell
   $change = @{ currentPassword = (Read-Host 'Current password'); newPassword = (Read-Host 'New password') } | ConvertTo-Json
   try {
       Invoke-RestMethod -Method Post http://localhost:5133/api/v1/auth/change-password -Headers @{ Authorization = "Bearer $token" } -ContentType 'application/json' -Body $change | Out-Null
       'Password changed. Sign in again with the new password.'
   }
   catch {
       # No answer at all (the API is not running, or the address or port is wrong): show PowerShell's own error.
       if ($null -eq $_.Exception.Response) { throw }
       # Windows PowerShell 5.1 shows only the status, such as "(401) Unauthorized". The API's reason is in the response body: read it.
       # PowerShell has already read the body once while building the error, so rewind the stream to its start first.
       $stream = $_.Exception.Response.GetResponseStream(); $stream.Position = 0
       (ConvertFrom-Json (New-Object System.IO.StreamReader($stream)).ReadToEnd()).detail
   }
   ```

   On success it prints `Password changed. Sign in again with the new password.` A change signs out every session of that user (including `$token`), so sign in again with quick start step 5. When the change is refused it prints the reason:
   - `Current password is incorrect.` This counts towards the same 5-try lock-out as sign-in.
   - The password-policy sentence, for a weak new password.
   - `Account temporarily locked. Try again later.`
   - `Your sign-in is missing or has expired. Sign in again.` There is no valid `$token`: run quick start step 5 first.

2. Remove `ADMIN_PASSWORD` (and `ADMIN_EMAIL`) from the environment. Once a user exists the API never reads them again (AC-02.2). Close the window you started the API from, or run:

   ```powershell
   Remove-Item Env:ADMIN_PASSWORD, Env:ADMIN_EMAIL
   ```

   If you ever stored them as user variables or in user-secrets, remove them there too:

   ```powershell
   [Environment]::SetEnvironmentVariable('ADMIN_PASSWORD', $null, 'User')
   dotnet user-secrets remove ADMIN_PASSWORD --project src/InventoryTracker.Api
   ```

   `run.ps1` warns on every start while `ADMIN_PASSWORD` is still set.

Never write a real password in a file git tracks. Every password in a command or sample request here is a placeholder such as `<choose-your-own-strong-password>`. Example values in the design document and the tests are for throwaway databases only; never use them for a real account.

## Keep two active Managers

Keep at least two active Manager accounts (security review SEC-04). Five wrong passwords in a row lock an account for 15 minutes, and anyone who can reach sign-in can keep doing that. A second Manager can reset a locked Manager's password, which also clears the lock. With only one Manager, nobody can. The API itself refuses to remove the Manager role from, or deactivate, the last active Manager.

Create the second Manager right after the first sign-in: [user guide, "Add people and give them roles"](docs/08-user-guide.md#2-add-people-and-give-them-roles).

## Runbook: remove someone's access now

A role change takes effect at the user's **next sign-in** (AC-03.5). A token issued before the change keeps the old roles until it expires, up to 8 hours later. So:

- **To remove someone's access now, deactivate them** (`POST /api/v1/users/{id}/deactivate`). Every token they hold stops working at once, and they cannot sign in.
- **If they should continue with fewer roles**, set the new roles (`PUT /api/v1/users/{id}/roles`) and then reactivate them (`POST /api/v1/users/{id}/reactivate`). They sign in again and get only the new roles.

Users are never deleted, so their name stays on every record they touched. Examples: [user guide, "Remove someone's access"](docs/08-user-guide.md#remove-someones-access).

## Signing key

`JWT_SIGNING_KEY` signs every sign-in token. Anyone who has it can make a valid token for any user.

- Make it with `scripts\new-signing-key.ps1`: 48 random bytes from Windows' cryptographic generator, 64 characters. Never use a phrase or anything you made up.
- Use one key per machine and per instance. Never share a key between a test instance and a real one: a token from one would be accepted by the other.
- Never put it in a file git tracks.
- Keep it as a variable for your Windows user, so it stays the same across windows and restarts. Once per machine, from the repository root:

  ```powershell
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\new-signing-key.ps1 -SaveForUser
  ```

  The script makes the key and saves it itself. It prints only `Saved a new JWT_SIGNING_KEY ...`, never the key. If a key is already saved, it keeps it and says so. Run from another folder, PowerShell cannot find the script and nothing is saved. User variables are readable by programs running as you. Windows also hands a user variable to every program you start, and such a program could expose it (a crash report, a log, or `set` on a shared screen); if the key may have been exposed, replace it with `-SaveForUser -Force` (SEC-23).
- **To replace the key**, add `-Force`: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\new-signing-key.ps1 -SaveForUser -Force`. This signs everybody out.
- **Do not save a key with** `[Environment]::SetEnvironmentVariable('JWT_SIGNING_KEY', (powershell ... -File scripts\new-signing-key.ps1), 'User')`, a form earlier versions of these documents showed. Run from the wrong folder, it saves PowerShell's start-up banner as the key: text anyone can guess. So `run.ps1` refuses a key that contains spaces, with "JWT_SIGNING_KEY in this window contains spaces, so it is not a generated key". If a key is also saved for your user, the bad value may be only in this window: first open a new window (or clear and reload it, as the message shows). Replace the saved key with `-SaveForUser -Force` from the repository root only if the message comes back in a new window; that signs everybody out.
- For a quick test in one window only, from the repository root: `$env:JWT_SIGNING_KEY = powershell -NoProfile -ExecutionPolicy Bypass -File scripts\new-signing-key.ps1`. That key is lost when the window closes, and every new one signs everybody out.
- **Windows hands user variables to a program only when it starts.** A PowerShell window that was already open does not see a newly saved key. Neither does any terminal in VS Code, new tabs included, until VS Code itself is closed completely and reopened. `scripts\run.ps1` notices this and says "JWT_SIGNING_KEY is saved for your Windows user, but this window does not have it: most likely it was opened before the key was saved". The fix is either of these:
  - open a new PowerShell window (in VS Code, close and reopen VS Code completely);
  - or load the saved key into this window:

    ```powershell
    $env:JWT_SIGNING_KEY = [Environment]::GetEnvironmentVariable('JWT_SIGNING_KEY', 'User')
    ```

  **Never answer it by making a second key.** A new key would not match the saved one, and everybody's sign-in would stop working at the next start.
- Changing the key signs everyone out. Nothing else is lost; people sign in again.

## Data

- **Where it is.** One SQLite file, `%LOCALAPPDATA%\InventoryTracker\inventory.db` (for example `C:\Users\<you>\AppData\Local\InventoryTracker\inventory.db`), unless `ConnectionStrings__Default` points elsewhere. While the API runs, SQLite also keeps `inventory.db-wal` and `inventory.db-shm` next to it; they are part of the database.
- **Protect it.** The file and every backup of it hold each user's email and password hash, and the whole stock and order history. The file is not encrypted. Keep it and its backups in your user profile or on a BitLocker-encrypted drive, never in a shared folder, a cloud-synced folder, the repository or `publish\`, and never email them.
- **Stopped means stopped.** Only "stop the API" (Ctrl+C in its window) counts as stopped. An idle API is still running: after some idle minutes it may let go of the file, but it opens it again on the next request. So never copy, move or replace the file while the API runs, however quiet it is. The backup and restore scripts refuse to run while any copy of the API is running.
- **One copy at a time.** Only one copy of the API may run at a time. `run.ps1` refuses to start a second one.
- **Backup and restore**: stop the API, then `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\backup-database.ps1`. Restoring, testing a restore and where backups go: [`docs/07-release.md` section 7](docs/07-release.md#7-data-backup-and-restore).

## Configuration

Settings come from `src/InventoryTracker.Api/appsettings.json`, then `appsettings.Production.json` in Production, then environment variables; later sources win. Secrets come only from environment variables (or, in Development, from `dotnet user-secrets`). [`.env.example`](.env.example) lists every name. The API and the scripts never read a `.env` file.

| Variable | Needed | Purpose |
|----------|--------|---------|
| `JWT_SIGNING_KEY` | every start | Signs tokens. At least 32 characters; use `scripts\new-signing-key.ps1`. |
| `ADMIN_EMAIL`, `ADMIN_PASSWORD` | first start only | The first Manager. Remove them afterwards. |
| `ConnectionStrings__Default` | no | The SQLite file, with an absolute path, for example `Data Source=D:\InventoryData\inventory.db`. |
| `Inventory__TolerancePercent` | no | The tolerance, 0 to 100, default 2. Outside that range the API does not start. |
| `Jwt__TokenLifetimeHours` | no | Token lifetime, default 8. Not range-checked, so check what you type. |

The full list: [`docs/07-release.md` section 3](docs/07-release.md#3-configuration).

## Development

```powershell
dotnet run --project src/InventoryTracker.Api
```

This runs in the **Development** environment through the launch profile, on `http://localhost:5133`. It reads secrets from `dotnet user-secrets` (set them as in [`docs/03-architecture.md` section 8](docs/03-architecture.md#8-running-locally)), uses its own database, `src/InventoryTracker.Api/inventory.db`, and publishes the OpenAPI description at `http://localhost:5133/openapi/v1.json`. Never use it as the real instance: that is what `scripts\run.ps1` is for.

The sample requests in [`src/InventoryTracker.Api/InventoryTracker.Api.http`](src/InventoryTracker.Api/InventoryTracker.Api.http) walk through every endpoint, for VS Code's REST Client or Visual Studio. They read every password from **`src/InventoryTracker.Api/.env`**, next to the `.http` file, not from a `.env` at the repository root. Create that file yourself (git ignores it) with these five lines, replacing each placeholder with a value of your own:

```
ADMIN_EMAIL=owner@example.com
ADMIN_PASSWORD=<choose-your-own-strong-password>
SAMPLE_USER_PASSWORD=<choose-a-password-for-the-sample-user>
SAMPLE_USER_RESET_PASSWORD=<choose-a-second-password>
SAMPLE_USER_NEW_PASSWORD=<choose-a-third-password>
```

`ADMIN_EMAIL` and `ADMIN_PASSWORD` are the first Manager's, the values you stored with `dotnet user-secrets`. The three `SAMPLE_USER_` passwords are for the Warehouse user that section 11 of the walk-through creates, then resets, then changes. Each must pass the password policy, and the three must differ. The names are also in [`.env.example`](.env.example) (decision #135). The API itself never reads this file.

The walk-through creates fixed names and users, and records are never deleted, so run it once against a fresh Development database, never against your real one. Port 5133 is also the port of the Production API that `scripts\run.ps1` starts, so check which one is running before you send anything.

## Tests

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test.ps1
```

It builds in Release with warnings as errors, then runs the whole suite (unit, integration and acceptance tests; 1,721 tests, about 2 minutes on the owner's machine). The tests set their own throwaway secrets and temporary databases, so they never touch your variables, user-secrets or data. If two builds run at once in one working tree, give each its own `-ArtifactsPath` folder ([`docs/07-release.md` section 2](docs/07-release.md#2-commands)).

## Continuous integration

[`.github/workflows/ci.yml`](.github/workflows/ci.yml) restores, builds with warnings as errors and runs the whole suite on GitHub's Windows machines, on every push and pull request to `main`. It has not run on GitHub yet, so this README shows no status badge.

## Project structure

```
src/InventoryTracker.Api/     the API: Program.cs, Auth/, Common/, Data/ (EF Core, migrations), Domain/ (rules
                              and calculations), Features/ (one folder per resource), settings, sample requests
tests/InventoryTracker.Api.Tests/   Unit/, Integration/, Acceptance/, Infrastructure/ (test helpers)
scripts/                      PowerShell: build, test, publish, run, new-signing-key, backup and restore
docs/                         requirements, design, architecture, tests, security, release, user guide
.github/workflows/ci.yml      CI
```

Details: [`docs/03-architecture.md` section 2](docs/03-architecture.md#2-project-structure).

## Documents

| Document | What it is |
|----------|------------|
| [`docs/01-prd.md`](docs/01-prd.md) | Product requirements: users, roles, every rule and acceptance criterion, the Later list. |
| [`docs/02-design.md`](docs/02-design.md) | The API design: endpoints, request and response shapes, flows, every error message. |
| [`docs/03-architecture.md`](docs/03-architecture.md) | Stack, data model, consistency rules, configuration, running locally. |
| [`docs/04-tasks.md`](docs/04-tasks.md) | The build task board. |
| [`docs/05-test-plan.md`](docs/05-test-plan.md) | Test plan and results: every acceptance criterion and the tests that prove it. |
| [`docs/06-security-review.md`](docs/06-security-review.md) | Security review and its release conditions. |
| [`docs/07-release.md`](docs/07-release.md) | Release guide: commands, configuration, deploying, backup, upgrade, rollback, troubleshooting. |
| [`docs/07a-cloud-guide.md`](docs/07a-cloud-guide.md) | Cloud tutorial (Azure App Service). **Guidance only**: it needs product changes first, notably moving from SQLite to PostgreSQL, plus HTTPS, HSTS, a rate limit and a new security review. |
| [`docs/08-user-guide.md`](docs/08-user-guide.md) | User guide: how each role does its daily work through the API. |
| [`docs/09-release-notes.md`](docs/09-release-notes.md) | Release notes for v1. |
| [`docs/decisions.md`](docs/decisions.md) | Every decision, numbered (#1, #2, ...). |

`CLAUDE.md`, `company/` and `.claude/` describe the AI company that built this project, not the product.
