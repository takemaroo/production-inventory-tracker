# Release guide: Production & Inventory Tracker API

How to build, test, publish, run, back up and upgrade the API. Version 1 runs **on the owner's Windows machine, reachable from that machine only** (owner decision #119). Plain HTTP is acceptable only because of that. The cloud tutorial is a separate document: [`docs/07a-cloud-guide.md`](07a-cloud-guide.md).

Every command is for PowerShell on Windows (Windows PowerShell 5.1, which comes with Windows, or PowerShell 7), run from the repository root.

> **Before the API is reachable from any other machine** (another PC on the office network, the internet, a cloud host): HTTPS and HSTS are **required** (SEC-03), and so is a sign-in rate limit (SEC-05). Both need product changes. The security review must then be re-opened. See section 9.

## 1. Prerequisites

| Tool | Version | Check with |
|------|---------|------------|
| .NET SDK | 10.0.x; `global.json` asks for 10.0.100 or any later 10.0 SDK. This machine has 10.0.401; 10.0.404 or later is recommended, because it brings the patched runtime. | `dotnet --version` |
| ASP.NET Core runtime, on the machine that runs the API | **10.0.15 or later** (SEC-06). This machine has **10.0.12**. | `dotnet --list-runtimes`, then `dotnet sdk check` |
| Windows PowerShell | 5.1 (built in) or PowerShell 7 | `$PSVersionTable.PSVersion` |
| git | any recent version (2.55 here) | `git --version` |
| Optional: VS Code with the REST Client extension, or Visual Studio 2022 17.12 or later | for the sample requests in `src/InventoryTracker.Api/InventoryTracker.Api.http` | |

**SEC-06, the runtime.** The published folder made by `scripts\publish.ps1` carries its own copy of the .NET and ASP.NET Core runtime, **10.0.15** (checked in the output), so the API you run from it is patched even on this machine. Development runs (`dotnet run`) and the tests still use the installed runtime, 10.0.12. The owner should install the latest .NET 10 SDK from <https://dotnet.microsoft.com/download/dotnet/10.0> (free; installing is the owner's call). Then run `dotnet sdk check` and write its result below. Patch monthly, and stay on 10.x (decision #19).

| Date | `dotnet sdk check` result | By |
|------|---------------------------|----|
| 2026-11-22 | SDK 10.0.401, runtime 10.0.12: patch 10.0.15 available. **Not yet installed.** The published folder bundles 10.0.15. | devops-engineer |

**Running the scripts.** Windows' default execution policy refuses to run script files, and this machine still has that default. So every script command below starts with `powershell -NoProfile -ExecutionPolicy Bypass -File`. That allows scripts for that one command only and changes no setting. If you prefer the short form `.\scripts\build.ps1`, run `Set-ExecutionPolicy -Scope CurrentUser RemoteSigned` once (your choice; it changes a setting for your Windows user).

## 2. Commands

| To do this | Run |
|------------|-----|
| Install dependencies | `dotnet restore` (and `dotnet tool restore`, only if you will add database migrations) |
| Run in development | `dotnet run --project src/InventoryTracker.Api` (Development environment, reads user-secrets; `docs/03-architecture.md` section 8) |
| Build (0 warnings) | `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build.ps1` |
| Run all tests | `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test.ps1` |
| Build for production | `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\publish.ps1` |
| Run in production, on this machine only | `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run.ps1 -Published` |
| Make a token signing key | `$env:JWT_SIGNING_KEY = powershell -NoProfile -ExecutionPolicy Bypass -File scripts\new-signing-key.ps1` (stores the key straight into the variable, so it never appears on screen; to keep it for your user, see section 3) |
| Back up the database | `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\backup-database.ps1` |
| Restore the database | `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\restore-database.ps1 -BackupFolder <folder>` |

What each script does, in more detail (each file also explains itself in its header, and `powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-Help .\scripts\run.ps1 -Full"` prints it; a plain `Get-Help` is refused under the default execution policy):

| Script | What it does |
|--------|--------------|
| `scripts\build.ps1` | `dotnet restore`, then `dotnet build` in Release with `-warnaserror`, so any warning fails the build. `-ArtifactsPath <folder>` puts the output elsewhere, which you need when two builds run at once (decision #67). |
| `scripts\test.ps1` | Runs `build.ps1`, then the full test suite (about 2 to 4 minutes). It needs no secrets: the tests use their own throwaway values. Accepts `-ArtifactsPath`. |
| `scripts\publish.ps1` | Writes a **self-contained** folder, `publish\win-x64`, with the 10.0.15 runtime inside (about 120 MB). Copy that folder to any 64-bit Windows machine and it runs, with no .NET installed. `-RuntimeVersion 10.0.16` (and so on) bundles a newer patch; raise the default in the script each month. `-FrameworkDependent` makes the small folder for hosts that supply the runtime (Azure App Service). `-Output <folder>` chooses the folder. |
| `scripts\run.ps1` | Starts the API in **Production**, never through the launch profile (SEC-13), on `http://127.0.0.1:5133` and `http://[::1]:5133` only: both loopback addresses, never `0.0.0.0` (SEC-03, #119). First it checks the variables of section 3 and stops with a clear message if one is missing. `-Published` runs `publish\win-x64` (or `-PublishFolder <folder>`); without it, the script builds and runs from source. `-Port <n>` changes the port. It refuses to start a second copy while one is already running (architecture risk 9). Stop the API with Ctrl+C. |
| `scripts\new-signing-key.ps1` | Prints 48 random bytes from the operating system's cryptographic generator, as 64 Base64 characters. |
| `scripts\backup-database.ps1`, `scripts\restore-database.ps1` | Section 7. |
| `scripts\common.ps1` | Shared helpers; not run on its own. |

## 3. Configuration

The API reads its settings from `src/InventoryTracker.Api/appsettings.json`, then `appsettings.Production.json`, then environment variables. Later sources win. Secrets come **only** from environment variables, or, in Development, from `dotnet user-secrets`. In Production, user-secrets are never read. `.env.example` lists every name. The API and the scripts never read a `.env` file. The only `.env` anything reads is `src/InventoryTracker.Api/.env`, which the sample requests file `InventoryTracker.Api.http` uses for the sign-in email and password (git-ignored, like every `.env`).

| Variable | Required | Example | Purpose |
|----------|----------|---------|---------|
| `JWT_SIGNING_KEY` | **Every start** | output of `scripts\new-signing-key.ps1` | Signs the sign-in tokens. At least 32 characters, random, one per machine and instance, never a phrase, never shared between a test and a real instance (SEC-13). Changing it signs everybody out. |
| `ADMIN_EMAIL` | **First start only** (no database yet) | `you@yourcompany.example` | Email of the first Manager. Ignored once any user exists (AC-02.2). |
| `ADMIN_PASSWORD` | **First start only** | (type your own) | Password of the first Manager: 8+ characters with an upper-case letter, a lower-case letter and a digit. Remove it after the first start. |
| `ConnectionStrings__Default` | No | `Data Source=D:\InventoryData\inventory.db` | The SQLite file. `run.ps1` uses `%LOCALAPPDATA%\InventoryTracker\inventory.db` when it is not set, and accepts absolute paths only. |
| `Inventory__TolerancePercent` | No | `2` | The tolerance for sends and usage variance, 0 to 100 (default 2). Outside that range the API does not start. |
| `Jwt__TokenLifetimeHours` | No | `8` | How long a token lasts (default 8, AC-01.2). Not range-checked (SEC-13), so check what you type. |
| `Jwt__Issuer`, `Jwt__Audience` | No | `InventoryTracker` | Names written into each token. Leave as they are. |
| `ASPNETCORE_ENVIRONMENT` | No | `Production` | `run.ps1` always sets `Production`. |
| `Logging__LogLevel__InventoryTracker.Api.Common.Errors.ApiExceptionHandler` | No | `Information` | Decision #126. `appsettings.Production.json` already sets it to `Warning` (decision #130). That drops the one Information line written for each refused oversized or broken request body; Warning and Error lines stay. Set it to `Information` only to see those lines again. |
| `AllowedHosts` | No | `*` | Host names the API answers to. Leave as is on localhost; set it to the real host name before any exposure (SEC-13). |

The names with `__` (two underscores) are configuration sections: `Inventory__TolerancePercent` is `Inventory:TolerancePercent`, the `"Inventory": { "TolerancePercent": 2 }` entry in `appsettings.json`.

**Setting them.** For the current PowerShell window only:

```powershell
$env:JWT_SIGNING_KEY = powershell -NoProfile -ExecutionPolicy Bypass -File scripts\new-signing-key.ps1
```

To keep a value for your Windows user, for every window you open afterwards (open a new window to see it):

```powershell
[Environment]::SetEnvironmentVariable('JWT_SIGNING_KEY', (powershell -NoProfile -ExecutionPolicy Bypass -File scripts\new-signing-key.ps1), 'User')
```

User variables are stored in your Windows profile, readable by programs running as you, which is the same protection the database has. Keep `JWT_SIGNING_KEY` as a user variable, so tokens survive a restart of the API. Set `ADMIN_EMAIL` and `ADMIN_PASSWORD` in the window only, for the first start. To remove a value: `Remove-Item Env:ADMIN_PASSWORD` (this window) and `[Environment]::SetEnvironmentVariable('ADMIN_PASSWORD', $null, 'User')` (your user).

## 4. Continuous integration

`.github/workflows/ci.yml` runs on every push to `main` and every pull request into `main`. On a fresh GitHub-hosted Windows machine it installs the SDK that `global.json` asks for (the newest 10.0 SDK), restores, builds in Release with warnings as errors, and runs the whole test suite. These are the same steps as `scripts\test.ps1`. It needs **no secrets**: the tests set their own throwaway `JWT_SIGNING_KEY`, `ADMIN_EMAIL` and `ADMIN_PASSWORD` in code (`TestApiFactory`) and use temporary SQLite files. Its token can only read the repository. A newer push to the same pull request cancels its older run; pushes to `main` are never cancelled, so every commit on `main` gets a result. The checkout does not keep the job's token in the repository's git settings, and a run is stopped after 25 minutes.

**Unconfirmed.** The workflow has not run yet: it starts only once the file is pushed to GitHub, which needs the owner's approval. No workflow linter (actionlint) is installed here; the file was checked as valid YAML only (section 11).

**Minutes.** The suite takes 2 to 4 minutes on the owner's machine. On GitHub's standard 2-core Windows runners for private repositories, expect about 6 to 10 minutes a run in total, including installing the SDK and building (an estimate; the first real run will tell). Windows minutes count **double** against the free allowance. GitHub Free includes 2,000 minutes a month for private repositories, which is about 100 to 160 runs a month. Without a payment method and a spending limit above zero, runs stop when the allowance is used up; nothing is charged. Check current figures under **Settings > Billing and plans** on GitHub. Switching `runs-on` to `ubuntu-latest` would count minutes once instead of twice, but the suite has not been run on Linux.

If a timing test (the 95th-percentile checks in the performance tests) fails only on the slower CI machine, report it to QA; do not loosen the test in the workflow.

## 5. Hosting options

| Option | Good for | Trade-offs | Rough cost |
|--------|----------|------------|------------|
| **The owner's Windows machine, localhost only** (v1) | One person or one PC; the security review's basis (#119) | Nobody else can reach it. Data lives on one disk, so backups matter (section 7). The machine must be on. | Free |
| Azure App Service + Azure Database for PostgreSQL | Several people or sites, on a managed host | Needs the PostgreSQL port, HSTS, a rate limit and a security re-review first. Monthly bill. | About 30 to 35 USD a month for a small setup (guide, section 9) |
| A small Linux virtual server with Caddy (HTTPS) and PostgreSQL | Lowest cloud price, full control | You patch the operating system, the runtime and the database yourself, and set up backups. The same product changes are needed. | About 5 to 15 USD a month |
| A container platform (Azure Container Apps, Google Cloud Run) | Scale to zero when idle | Needs a Dockerfile (none yet) and the same product changes. Must be limited to one instance (architecture risk 9). | Low for little use |

**Recommended: the owner's machine**, because that is what v1 was reviewed for and decided (#119). For the cloud, Azure App Service is recommended: it runs .NET 10 directly, supplies HTTPS, patches the runtime, and pairs with a managed PostgreSQL that backs itself up. See [`docs/07a-cloud-guide.md`](07a-cloud-guide.md).

**No Dockerfile** (decision #132). v1 is not hosted. Docker is not installed on the owner's machine, so an image could neither be built nor tested here. App Service needs no container, and SQLite has no database server to put in a compose file. Add one if a container platform is ever chosen.

## 6. Deploying on the owner's machine

Nothing here leaves the machine, costs money or creates an account.

1. **Get the code and check it.** In a PowerShell window at the repository root:

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test.ps1
   ```

   It ends with `Build succeeded with 0 warnings.` and `All tests passed.`

2. **Publish.**

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\publish.ps1
   ```

   It ends with `Bundled ASP.NET Core runtime: 10.0.15` and `Published to ...\publish\win-x64`.

3. **Set the variables for the first start** (section 3):

   ```powershell
   [Environment]::SetEnvironmentVariable('JWT_SIGNING_KEY', (powershell -NoProfile -ExecutionPolicy Bypass -File scripts\new-signing-key.ps1), 'User')
   $env:JWT_SIGNING_KEY = [Environment]::GetEnvironmentVariable('JWT_SIGNING_KEY', 'User')
   $env:ADMIN_EMAIL    = 'you@yourcompany.example'
   $env:ADMIN_PASSWORD = Read-Host 'First Manager password'   # not saved in the command history
   ```

   `Read-Host` shows what you type on the screen: it keeps the password out of the command history, but does not hide it. Make sure nobody is watching, and clear the window afterwards with `Clear-Host`. The same applies to the `Read-Host` prompts in steps 5 and 6.

4. **Start it.**

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run.ps1 -Published
   ```

   On the first start the script says `First start: the API will create the database and the first Manager.` The API creates `%LOCALAPPDATA%\InventoryTracker\inventory.db`, applies the migrations, creates the three roles and the first Manager (log line `Created the first Manager (user ...)`), and shows `Now listening on: http://127.0.0.1:5133`. One warning, `Failed to determine the https port for redirect.`, is expected on localhost (section 10).

5. **Check it and sign in**, in a second PowerShell window:

   ```powershell
   Invoke-RestMethod http://localhost:5133/health          # -> Healthy
   $body  = @{ email = 'you@yourcompany.example'; password = (Read-Host 'Password') } | ConvertTo-Json
   $token = (Invoke-RestMethod -Method Post http://localhost:5133/api/v1/auth/sign-in -ContentType 'application/json' -Body $body).accessToken
   ```

6. **Change the first password at once.** It has been typed into a terminal and held in an environment variable.

   ```powershell
   $change = @{ currentPassword = (Read-Host 'Current password'); newPassword = (Read-Host 'New password') } | ConvertTo-Json
   Invoke-RestMethod -Method Post http://localhost:5133/api/v1/auth/change-password -Headers @{ Authorization = "Bearer $token" } -ContentType 'application/json' -Body $change
   ```

   This signs out every session of that user (including `$token`); sign in again with the new password.

7. **Remove `ADMIN_PASSWORD` (and `ADMIN_EMAIL`) from the environment.** They are never read again once a user exists (AC-02.2). Close the window you started the API from, or run `Remove-Item Env:ADMIN_PASSWORD, Env:ADMIN_EMAIL`. If you ever stored them as user variables or in user-secrets, remove them there too (`[Environment]::SetEnvironmentVariable('ADMIN_PASSWORD', $null, 'User')`, `dotnet user-secrets remove ADMIN_PASSWORD --project src/InventoryTracker.Api`). From now on `run.ps1` needs only `JWT_SIGNING_KEY`, and warns you if `ADMIN_PASSWORD` is still set.

8. **Create a second Manager** (`POST /api/v1/users`; the README explains why to keep at least two, SEC-04).

Later starts: open a PowerShell window and run step 4. Stop with Ctrl+C.

### Upgrading to a new version

At start-up, `Program.cs` (1) checks the tolerance and the signing key, (2) applies any migrations the database has not seen yet (`MigrateDatabaseAsync`, which uses EF Core's `MigrateAsync`), and (3) creates any missing role, all before it listens. There is no separate database step.

1. Run the tests on the new code: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test.ps1`
2. Stop the API (Ctrl+C).
3. Back up the database (section 7): `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\backup-database.ps1`. This backup is your way back: migrations only go forward.
4. Publish the new version into a **new** folder, so the old one stays as your rollback: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\publish.ps1 -Output publish\win-x64-2026-12-01`
5. Start it: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run.ps1 -Published -PublishFolder publish\win-x64-2026-12-01`. The log shows `Applying migration '...'` for each new migration.
6. Check `/health` and sign in.

If start-up stops during a migration, nothing else happens: the API does not listen. Restore the backup from step 3, run the old folder (section 8) and report the log to the developers.

## 7. Data: backup and restore

**Where the data is.** One SQLite file, by default `%LOCALAPPDATA%\InventoryTracker\inventory.db` (for example `C:\Users\<you>\AppData\Local\InventoryTracker\inventory.db`), or wherever `ConnectionStrings__Default` points. While the API runs, SQLite also keeps `inventory.db-wal` and `inventory.db-shm` next to it. These side files are part of the database. The Development database, `src/InventoryTracker.Api/inventory.db`, is a separate file for development only, and git ignores it.

**Protect it (SEC-16).** The file and **every backup of it** hold each user's email and password hash, and the whole stock and order history. The file is not encrypted. Protect them like the machine itself:

- Keep them in your user profile (the default location, which Windows limits to your user, SYSTEM and administrators) or on a BitLocker-encrypted drive.
- Never put them in the repository, a build folder (`publish\`), a shared network folder or an unencrypted cloud-synced folder, and never email them.
- An off-machine copy goes on an encrypted drive (for example a BitLocker To Go USB stick) kept somewhere safe.

**Back up** (daily or weekly, and always before an upgrade). Stop the API first: a copy taken during a write could be inconsistent, so the script refuses while any copy of the API is running, or while another program has the file open. It checks the running processes, not only the file, because an idle API lets go of the file for a while and then reopens it on the next request.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\backup-database.ps1
```

It copies the database and any side files into a new folder `%LOCALAPPDATA%\InventoryTracker\backups\inventory-<yyyyMMdd-HHmmss>`. `-BackupRoot <folder>` chooses another place, for example the encrypted USB drive.

**Restore.** Stop the API, then:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\restore-database.ps1 -BackupFolder "$env:LOCALAPPDATA\InventoryTracker\backups\inventory-20261122-093000"
```

Nothing is deleted. The current database and its side files are moved into `before-restore-<time>` next to the database, then the backup set is copied into place. Moving the old `-wal` file aside matters: an old write-ahead log next to a restored database would mix two states. Start the API again; if the backup is from an older version, the API applies the newer migrations to it on start.

**Test a restore** once, soon. Stop the API first (only one copy may run at a time). In a new window, set `ConnectionStrings__Default` to a test path that keeps the file name `inventory.db`, for example `$env:ConnectionStrings__Default = 'Data Source=C:\RestoreTest\inventory.db'`: the restore script looks for a file of that same name in the backup folder. Restore into it, start the API with `-Port 5134`, sign in, stop it, and close the window so the test path is forgotten.

## 8. Rolling back

- **Code only (no new migrations in the new version):** stop the API and start the previous folder: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run.ps1 -Published -PublishFolder publish\win-x64-<previous>`. If you no longer have it, check out the previous version with git and publish it again: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\publish.ps1`
- **Code and data (the new version applied migrations):** the old version may not understand the newer schema. Stop the API, restore the backup taken just before the upgrade (section 7), then start the previous folder. Anything recorded since that backup is lost, so note it first and enter it again.

## 9. Before the API is reachable from any other machine

Version 1 is cleared for release **on localhost only** (`docs/06-security-review.md` 7.1). `run.ps1` makes that the only way it starts. Opening it to another machine, by any route (a different `--urls`, a port forward, a reverse proxy, a cloud host), makes these **blocking**:

1. **HTTPS (SEC-03).** Either a Kestrel HTTPS endpoint with a certificate whose path and password are secrets, or a reverse proxy (IIS, nginx, Caddy, or a cloud front end) that terminates TLS, with Kestrel kept on `127.0.0.1` and forwarded headers trusted from that proxy only. **Recorded choice for v1: none, localhost HTTP only (#119).** For the cloud, App Service's front end terminates TLS (cloud guide, step 6).
2. **HSTS (SEC-03):** `app.UseHsts()` in Production, a code change for `backend-developer`.
3. **A sign-in rate limit (SEC-05)**, a code change; and revisit SEC-04, the lock-out abuse (owner decision).
4. **`AllowedHosts`** set to the real host name (SEC-13), and the log level of section 3 kept at `Warning` (#126; already so in `appsettings.Production.json`).
5. **Re-open the security review** before going live.

## 10. Troubleshooting

| You see | Cause and fix |
|---------|---------------|
| `... cannot be loaded because running scripts is disabled on this system` | You ran `.\scripts\x.ps1` directly. Use the `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\x.ps1` form (section 1). |
| `Not started: JWT_SIGNING_KEY is not set.` / `... is shorter than 32 characters.` | Set it (section 3). A user variable is visible only in windows opened after you set it. |
| `Not started: this is the first start (no database at ...), so ADMIN_EMAIL and ADMIN_PASSWORD are needed` | First start, or `ConnectionStrings__Default` points somewhere new. Set both (section 6, step 3), or fix the path. |
| `Not started: ConnectionStrings__Default must use an absolute path` | Use a full path such as `Data Source=D:\InventoryData\inventory.db`. |
| `Not started: remove these environment variables, which could make the API listen on other addresses: ...` (names starting `Kestrel`, `ASPNETCORE_Kestrel` or `DOTNET_Kestrel`) | Remove them; v1 listens on this machine only (section 9). |
| `Not started: The API is running: InventoryTracker.Api.exe (process id ...)` | A copy of the API is already running, perhaps in another window, even if it has been idle for a long time. Stop it (Ctrl+C in its window; `Stop-Process -Id <id>` only as a last resort). |
| `Not started: The database ... is open in another program.` | Another program (a database browser, for example) has the file open. Close it. |
| `Not started: no published API at ...` | Publish first (`powershell -NoProfile -ExecutionPolicy Bypass -File scripts\publish.ps1`), or pass the right `-PublishFolder`. |
| `crit: ... Start-up stopped: ADMIN_PASSWORD does not meet the password policy...` | Choose a password with 8+ characters, an upper-case letter, a lower-case letter and a digit. |
| `crit: ... Start-up stopped: ADMIN_EMAIL is not a valid email address.` | Fix `ADMIN_EMAIL`. |
| `crit: ... Start-up stopped: ADMIN_EMAIL is not set.` on a start after a failed first start | The failed first start already created the (empty) database file, so `run.ps1` no longer treats it as a first start, but the API still has no user. Set `ADMIN_EMAIL` and `ADMIN_PASSWORD` and start again. |
| `crit: ... Start-up stopped: Tolerance must be between 0 and 100.` | Fix `Inventory__TolerancePercent`. |
| `warn: ... Failed to determine the https port for redirect.` | Expected on localhost. Production asks for HTTPS redirection, but v1 has no HTTPS endpoint, so requests are served over HTTP on this machine only (SEC-03). |
| `Failed to bind to address http://127.0.0.1:5133: address already in use` | Another program uses the port. Find it with `Get-NetTCPConnection -LocalPort 5133 \| Select-Object OwningProcess`, or start on another port with `-Port 5134`. |
| `401` with `account-locked` on sign-in | Five wrong passwords in a row lock the account for 15 minutes (AC-01.4). Wait, or have another Manager reset the password. |
| Everybody is signed out after a restart | `JWT_SIGNING_KEY` changed, for example a new key in each window. Store one as a user variable (section 3). |
| `/openapi/v1.json` answers `401` | By design in Production. Run in Development (`dotnet run --project src/InventoryTracker.Api`) to read it. |
| The sample requests (`.http`) fail to sign in | Create `src/InventoryTracker.Api/.env` with your own `ADMIN_EMAIL` and `ADMIN_PASSWORD` (not a `.env` at the repository root), and use port 5133. |
| `publish.ps1` fails with "file is being used by another process" | The API is running from that folder. Stop it, or publish to a new folder with `-Output`. |
| `dotnet test` or a build reports files locked by another process | Two builds in one working tree at once. Give each its own `-ArtifactsPath` (decision #67). |
| The backup or restore script refuses: `The API is running: ...` or `The database ... is open in another program.` | Stop the API first (Ctrl+C in its window), or close the other program. |

## 11. Verified from a clean copy

Run on 2026-11-22 on the owner's machine (Windows 11, Windows PowerShell 5.1, .NET SDK 10.0.401, installed runtime 10.0.12) by `devops-engineer`. To keep the tables short, `scripts\x.ps1 ...` below stands for `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\x.ps1 ...`, the form every script was actually run in.

**Clean copy.** The files a fresh clone would hold (every tracked file plus the new phase 7 files; nothing git ignores, so no `bin\`, `obj\`, database, `.env` or `publish\`) were copied into an empty temporary folder. There, with an **empty NuGet package cache** (`NUGET_PACKAGES` pointed at a new folder, so every package and both 10.0.15 runtime packs were downloaded from nuget.org):

| Command (from the clean copy's root) | Result |
|--------------------------------------|--------|
| `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test.ps1` | Build succeeded, **0 warnings, 0 errors**; tests **1,721 of 1,721 passed**, 0 failed (test run 1 min 57 s; whole script 141 s). |
| `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\publish.ps1` | `publish\win-x64` written; `Bundled ASP.NET Core runtime: 10.0.15`. |
| `scripts\run.ps1 -Port 5197` (from source), with throwaway `JWT_SIGNING_KEY`, `ADMIN_EMAIL`, `ADMIN_PASSWORD`, a temporary `ConnectionStrings__Default` and `APPDATA` pointed at a temporary folder | `Hosting environment: Production`; listening on `127.0.0.1:5197` and `[::1]:5197` only (checked with `netstat`); first Manager created; `/health` `Healthy`; sign-in `200`; `/openapi/v1.json` `401`. No user-secrets folder was created or read. Stopped. |

**In the working tree** (same machine, same day):

| Command | Result |
|---------|--------|
| `scripts\test.ps1 -ArtifactsPath TestResults\devops-art` | 0 warnings; 1,721 of 1,721 passed (1 min 58 s). |
| `scripts\publish.ps1` | 364 files, 118 MB; runtime 10.0.15 (file versions of `Microsoft.AspNetCore.Server.Kestrel.Core.dll`, `System.Private.CoreLib.dll` and `hostfxr.dll` checked); `appsettings.Production.json` included; no database or `.env` in the folder. |
| `scripts\publish.ps1 -FrameworkDependent` | 34 files; zipped with Windows' `tar.exe` into 102 entries, all with `/` separators, including `runtimes/linux-x64` (cloud guide, step 9). |
| `scripts\run.ps1 -Published -Port 5199`, throwaway values as above | Four migrations applied, first Manager created, `Production`, loopback only. `/health` `Healthy`; sign-in `200`, a `Manager` token for 8 hours; `GET /api/v1/users` `200` with the token and `401` without; `/openapi/v1.json` `401`; change-password `204`, then the old token `401` and the new password `200`; a 2 MB body `413 request-too-large` with **no** Information log line (#126 in effect). The only warning was `Failed to determine the https port for redirect.` (expected, section 10). |
| `scripts\backup-database.ps1` right after a sign-in, while the API ran | Refused (the database file was still open). |
| After stopping the API (forced, so the `-wal` and `-shm` files remained): `backup-database.ps1`, then `restore-database.ps1` | Backup of 3 files. Restore moved the current 3 files to `before-restore-<time>` and copied the backup in. A second start then said "No migrations were applied", and the changed password signed in (`200`) while the old one did not (`401`). With `ADMIN_PASSWORD` still set, `run.ps1` warned it is no longer read. |
| `run.ps1` refusals | Missing key, short key, first start without `ADMIN_*`, relative database path, a Kestrel endpoint variable and a missing published folder: each stopped with its message and exit code 1. A weak `ADMIN_PASSWORD` was refused by the API with the design 7.4 message, exit code 1. |
| `scripts\new-signing-key.ps1` twice | Two different 64-character keys. |
| `.github\workflows\ci.yml` | Parses as YAML with the intended triggers, permissions and steps. **Not run**: GitHub runs it only after a push, which awaits the owner's approval. actionlint is not installed. |
| Docker | Not installed; no image built (section 5). |

**Re-check after code review (2026-11-22).** The review found that an idle API releases the database file: after a few idle minutes EF Core closes its pooled connections, SQLite folds the `-wal` file into the database, and the file is no longer locked, although the API still serves. The first version of the backup and restore scripts checked only the lock, so they could run against a live API. They now refuse while any copy of the API process is running, and still check the lock as well; `run.ps1` uses the same check. Re-run on this machine:

| Command | Result |
|---------|--------|
| Published API on port 5195 (throwaway values, scratch database, `APPDATA` and `LOCALAPPDATA` in a temporary folder), one sign-in, then left idle | After about 6.5 minutes the file was still locked. After about 10 minutes the `-wal` and `-shm` files were gone and the lock check alone said the file was free: the reviewer's finding, reproduced. |
| Then `backup-database.ps1`, `restore-database.ps1` and a second `run.ps1 -Published` | All three refused with `The API is running: InventoryTracker.Api.exe (process id ...)`, exit code 1. No backup folder was created and nothing was moved. The idle API still answered `/health` `Healthy`. |
| `run.ps1` with each of `DOTNET_Kestrel__Endpoints__Http__Url`, `Kestrel:Endpoints:Http:Url`, `ASPNETCORE_Kestrel__Endpoints__Http__Url` and `kestrel__endpoints__http__url` set | Each refused with its name, exit code 1, before anything was created. The first two passed the earlier guard. |
| From the repository root, in a PowerShell session whose process folder was elsewhere: `scripts\publish.ps1 -Output publish\win-x64-2026-12-01` (section 6, upgrade step 4) | Published into the repository's `publish\win-x64-2026-12-01`; the earlier code would have resolved the path against the process folder. |
| `scripts\run.ps1 -Published -PublishFolder publish\win-x64-2026-12-01` (upgrade step 5), then `scripts\run.ps1 -Published -PublishFolder publish\win-x64` (rollback, section 8), both with relative folders | Each started (content root the full folder path), `/health` `Healthy`, sign-in `200` as Manager; stopped. The earlier code stopped with `CommandNotFoundException`. |
| `scripts\test.ps1 -ArtifactsPath TestResults\devops-art` | 0 warnings; 1,721 of 1,721 passed (1 min 49 s). |

Not verified: the CI workflow on GitHub, the cloud guide (nothing was deployed, by design), Linux, and the scripts in PowerShell 7 (only 5.1 is installed; the scripts use nothing specific to 5.1).

## 12. Security release conditions (`docs/06-security-review.md` 7.2)

| # | Condition | Status |
|---|-----------|--------|
| 1 | SEC-03, #119: bind to localhost only; state HTTPS, HSTS and the rate limit are required before exposure | Met: `run.ps1` binds `127.0.0.1` and `[::1]` only and refuses Kestrel endpoint variables under every prefix (`Kestrel`, `ASPNETCORE_Kestrel`, `DOTNET_Kestrel`, written with `__` or `:`); sections 9 and the cloud guide state the requirements. |
| 2 | SEC-06: runtime 10.0.15 or later, `dotnet sdk check` recorded | Partly met: the published folder bundles 10.0.15. The machine's installed runtime is still 10.0.12 (recorded in section 1); installing is the owner's call. |
| 3 | SEC-13: start in Production, not through the launch profile | Met: `run.ps1` passes `--environment Production` and runs the published exe or `dotnet run --no-launch-profile`. |
| 4 | SEC-12: no usable sample password | Met. `docs/03-architecture.md` (section 8, about line 490) and `InventoryTracker.Api.http` (header, line 24) hold the placeholder `<choose-your-own-strong-password>`. Every password the `.http` walk-through sends, including the sample Warehouse user's three, is read with `{{$dotenv ...}}` from the git-ignored `src/InventoryTracker.Api/.env` (decision #135); `.env.example` lists the names. No password literal that passes the policy is left in `src/`, `scripts/`, the README or this guide. Remaining literals are in test code against throwaway databases, and in the design, test-plan, bug and security-review documents (reported to the delivery manager). First-password steps: section 6, steps 6 and 7. |
| 5 | SEC-04, #120: keep at least two active Managers | For the README (`technical-writer`); referenced in section 6, step 8. |
| 6 | SEC-07, #121: runbook "deactivate to remove access now" | For the README (`technical-writer`). |
| 7 | SEC-13: random signing key per machine | Met here (`new-signing-key.ps1`, section 3); the README must repeat it. |
| 8 | SEC-16: the database and its backups hold password hashes | Met: section 7, "Protect it", and the backup script's message. |
| 9 | SEC-02 residual, #126: `ApiExceptionHandler` at Warning | Met: `appsettings.Production.json` (decision #130). |
