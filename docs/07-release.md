# Release guide: Production & Inventory Tracker API

How to build, test, publish, run, back up and upgrade the API. Version 1, and version 1.1 after it, run **on the owner's Windows machine, reachable from that machine only** (owner decision #119; security review Part 2, 13.1). Plain HTTP is acceptable only because of that. That applies to Sellers too: they use the API on this machine, like everybody else. To move from version 1 to version 1.1, follow section 6, "Upgrading from version 1 to version 1.1". The cloud tutorial is a separate document: [`docs/07a-cloud-guide.md`](07a-cloud-guide.md).

Every command is for PowerShell on Windows (Windows PowerShell 5.1, which comes with Windows, or PowerShell 7), run from the repository root.

> **Before the API is reachable from any other machine** (another PC on the office network, the internet, a cloud host): HTTPS and HSTS are **required** (SEC-03), and so is a sign-in rate limit (SEC-05). Both need product changes. The security review must then be re-opened. See section 9.

## 1. Prerequisites

| Tool | Version | Check with |
|------|---------|------------|
| .NET SDK | 10.0.x; `global.json` asks for 10.0.100 or any later 10.0 SDK, and uses the newest one installed. This machine has 10.0.404 (and the older 10.0.401). | `dotnet --version` |
| ASP.NET Core runtime, on the machine that runs the API | **10.0.15 or later** (SEC-06). This machine has **10.0.15** (and the older 10.0.12). | `dotnet --list-runtimes`, then `dotnet sdk check` |
| Windows PowerShell | 5.1 (built in) or PowerShell 7 | `$PSVersionTable.PSVersion` |
| git | any recent version (2.55 here) | `git --version` |
| Optional: VS Code with the REST Client extension, or Visual Studio 2022 17.12 or later | for the sample requests in `src/InventoryTracker.Api/InventoryTracker.Api.http` | |

**SEC-06, the runtime.** The published folder made by `scripts\publish.ps1` carries its own copy of the .NET and ASP.NET Core runtime, **10.0.15** (checked in the output), so the API you run from it is patched whatever is installed. Development runs (`dotnet run`) and the tests use the newest installed 10.0 runtime, 10.0.15 since the owner installed SDK 10.0.404 on 2026-11-22. Install each new .NET 10 SDK from <https://dotnet.microsoft.com/download/dotnet/10.0> (free; installing is the owner's call), raise `publish.ps1`'s `-RuntimeVersion` default to match, then run `dotnet sdk check` and write its result below. Patch monthly, and stay on 10.x (decision #19).

| Date | `dotnet sdk check` result | By |
|------|---------------------------|----|
| 2026-11-22 | SDK 10.0.401, runtime 10.0.12: patch 10.0.15 available. **Not yet installed.** The published folder bundles 10.0.15. | devops-engineer |
| 2026-11-25 (v1.1 release) | SDK 10.0.404 and runtime 10.0.15: **up to date**. The older SDK 10.0.401, runtime 10.0.12 and the unsupported 8.0.18 are still installed beside them; .NET uses the newest, so removing them is housekeeping only. The published folder bundles 10.0.15. | devops-engineer |

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
| Make a token signing key | `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\new-signing-key.ps1 -SaveForUser` (saves it for your Windows user without showing it; then open a new window; section 3) |
| Back up the database | `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\backup-database.ps1` |
| Restore the database | `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\restore-database.ps1 -BackupFolder <folder>` |

What each script does, in more detail (each file also explains itself in its header, and `powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-Help .\scripts\run.ps1 -Full"` prints it; a plain `Get-Help` is refused under the default execution policy):

| Script | What it does |
|--------|--------------|
| `scripts\build.ps1` | `dotnet restore`, then `dotnet build` in Release with `-warnaserror`, so any warning fails the build. `-ArtifactsPath <folder>` puts the output elsewhere, which you need when two builds run at once (decision #67). |
| `scripts\test.ps1` | Runs `build.ps1`, then the full test suite (2,510 tests in version 1.1, about 3 to 7 minutes). It needs no secrets: the tests use their own throwaway values. Accepts `-ArtifactsPath`. |
| `scripts\publish.ps1` | Writes a **self-contained** folder, `publish\win-x64`, with the 10.0.15 runtime inside (about 120 MB). Copy that folder to any 64-bit Windows machine and it runs, with no .NET installed. `-RuntimeVersion 10.0.16` (and so on) bundles a newer patch; raise the default in the script each month. `-FrameworkDependent` makes the small folder for hosts that supply the runtime (Azure App Service). `-Output <folder>` chooses the folder. |
| `scripts\run.ps1` | Starts the API in **Production**, never through the launch profile (SEC-13), on `http://127.0.0.1:5133` and `http://[::1]:5133` only: both loopback addresses, never `0.0.0.0` (SEC-03, #119). First it checks the variables of section 3 and stops with a clear message if one is missing. `-Published` runs `publish\win-x64` (or `-PublishFolder <folder>`); without it, the script builds and runs from source. `-Port <n>` changes the port. It refuses to start a second copy while one is already running (architecture risk 9). Stop the API with Ctrl+C. |
| `scripts\new-signing-key.ps1` | Makes 48 random bytes from the operating system's cryptographic generator, as 64 Base64 characters. `-SaveForUser` saves them as `JWT_SIGNING_KEY` for your Windows user and never shows them; it keeps a key that is already saved unless you add `-Force` (replacing the key signs everybody out). Without `-SaveForUser` it prints the key, for a one-window test. |
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

Version 1.1 adds no setting to this table: the API reads the same variables as version 1, and no new secret. The one new name in `.env.example`, `SAMPLE_SELLER_PASSWORD`, is read only by the sample requests file, from `src/InventoryTracker.Api/.env`.

The names with `__` (two underscores) are configuration sections: `Inventory__TolerancePercent` is `Inventory:TolerancePercent`, the `"Inventory": { "TolerancePercent": 2 }` entry in `appsettings.json`.

**Setting them.** Save the signing key for your Windows user, so every window you open afterwards has it. From the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\new-signing-key.ps1 -SaveForUser
```

It generates the key and saves it inside the script, prints only `Saved a new JWT_SIGNING_KEY ...` and never the key, and exits with an error if anything fails. If a key is already saved it keeps it and says so; add `-Force` only if you mean to replace it, which signs everybody out. Run from another folder, PowerShell cannot find the script, prints an error, and nothing is saved.

Do not save a key with `[Environment]::SetEnvironmentVariable('JWT_SIGNING_KEY', (powershell ... -File scripts\new-signing-key.ps1), 'User')`, a form earlier versions of this guide showed. Run from the wrong folder, it saves PowerShell's start-up banner as the key: text anyone can guess. `run.ps1` now refuses a key with spaces in it for that reason (section 10).

For a quick test in the current window only, from the repository root (a window-only key is lost when the window closes, and every new one signs everybody out):

```powershell
$env:JWT_SIGNING_KEY = powershell -NoProfile -ExecutionPolicy Bypass -File scripts\new-signing-key.ps1
```

Windows copies user variables into a program only when it starts. A PowerShell window that was already open does not see the new value, and neither does any terminal in VS Code, new tabs included, until VS Code itself is closed and reopened. `run.ps1` detects this case and says so (section 10); never answer it by making a second key.

User variables are stored in your Windows profile, readable by programs running as you, which is the same protection the database has. Windows also hands a user variable to every program you start, and such a program could expose it (a crash report, a log, or `set` on a shared screen); if the key may have been exposed, replace it with `-SaveForUser -Force` (SEC-23). Keep `JWT_SIGNING_KEY` as a user variable, so tokens survive a restart of the API. Set `ADMIN_EMAIL` and `ADMIN_PASSWORD` in the window only, for the first start. To remove a value: `Remove-Item Env:ADMIN_PASSWORD` (this window) and `[Environment]::SetEnvironmentVariable('ADMIN_PASSWORD', $null, 'User')` (your user).

## 4. Continuous integration

`.github/workflows/ci.yml` runs on every push to `main` and every pull request into `main`. On a fresh GitHub-hosted Windows machine it installs the SDK that `global.json` asks for (the newest 10.0 SDK), restores, builds in Release with warnings as errors, and runs the whole test suite. These are the same steps as `scripts\test.ps1`. It needs **no secrets**: the tests set their own throwaway `JWT_SIGNING_KEY`, `ADMIN_EMAIL` and `ADMIN_PASSWORD` in code (`TestApiFactory`) and use temporary SQLite files. Its token can only read the repository. A newer push to the same pull request cancels its older run; pushes to `main` are never cancelled, so every commit on `main` gets a result. The checkout does not keep the job's token in the repository's git settings, and a run is stopped after 25 minutes. Both actions are pinned to full commit SHAs with the release named in a comment (see `ci.yml` for the current versions), so a moved tag cannot change what runs (SEC-19). The first pins were checked against the official repositories on 2026-11-22; later ones come from Dependabot. `.github/dependabot.yml` asks GitHub to open a pull request each month when a newer release of either action exists. Each such pull request runs CI and is merged by hand.

**Confirmed for version 1.** The first run on GitHub, after the owner pushed on 2026-11-22, passed in about 7 minutes, and so did the runs on Dependabot's first two pull requests (`docs/00-status.md`). No workflow linter (actionlint) is installed here; the file was checked as valid YAML only (section 11).

**Version 1.1.** The workflow needed no change. The test step is the same command, `dotnet test --solution InventoryTracker.slnx --no-build --configuration Release`, and it picks up the new tests by itself. Version 1.1 adds no package, no new secret and no setting the tests need: they still set their own values in `TestApiFactory`, and `UpgradeTests` builds its own version 1 database in a temporary file. The suite in a clean copy took 2 min 55 s here, against about 2 minutes in version 1, so the 25-minute limit still leaves plenty of room. **Not verified here:** how the version 1.1 runs on GitHub went (the v1.1 commits have been pushed to `main`, so CI has run on them). `gh` is not installed on this machine, so nobody has looked from here. Check the **Actions** tab on GitHub: the latest run on `main` should be green, and its duration tells you the real minutes per run.

**Minutes.** The suite takes about 3 to 7 minutes on the owner's machine. On GitHub's standard 2-core Windows runners for private repositories, expect about 8 to 14 minutes a run in total, including installing the SDK and building. That is an estimate, scaled from version 1's first real run, which took about 7 minutes; the Actions tab shows the real figure. Windows minutes count **double** against the free allowance. GitHub Free includes 2,000 minutes a month for private repositories, which is about 70 to 125 runs a month. Without a payment method and a spending limit above zero, runs stop when the allowance is used up; nothing is charged. Check current figures under **Settings > Billing and plans** on GitHub. Switching `runs-on` to `ubuntu-latest` would count minutes once instead of twice, but the suite has not been run on Linux.

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
   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\new-signing-key.ps1 -SaveForUser
   $env:JWT_SIGNING_KEY = [Environment]::GetEnvironmentVariable('JWT_SIGNING_KEY', 'User')   # this window too
   $env:ADMIN_EMAIL    = 'you@yourcompany.example'
   $env:ADMIN_PASSWORD = Read-Host 'First Manager password'   # not saved in the command history
   ```

   `Read-Host` shows what you type on the screen: it keeps the password out of the command history, but does not hide it. Make sure nobody is watching, and clear the window afterwards with `Clear-Host`. The same applies to the `Read-Host` prompts in steps 5 and 6.

4. **Start it.**

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run.ps1 -Published
   ```

   On the first start the script says `First start: the API will create the database and the first Manager.` The API creates `%LOCALAPPDATA%\InventoryTracker\inventory.db`, applies the migrations, creates the roles (Warehouse, Production, Manager and, since version 1.1, Seller) and the first Manager (log line `Created the first Manager (user ...)`), and shows `Now listening on: http://127.0.0.1:5133`. One warning, `Failed to determine the https port for redirect.`, is expected on localhost (section 10).

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

   The snippet reads the response the way Windows PowerShell 5.1 provides it, which is what this machine has; PowerShell 7 reports errors differently.

6. **Change the first password at once.** It has been typed into a terminal and held in an environment variable.

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

   On success it prints `Password changed. Sign in again with the new password.` A change signs out every session of that user (including `$token`), so sign in again with step 5. When the change is refused it prints the reason: `Current password is incorrect.` (this counts towards the same 5-try lock-out as sign-in), the password-policy sentence for a weak new password, `Account temporarily locked. Try again later.`, or `Your sign-in is missing or has expired. Sign in again.` (no valid `$token`: run step 5 first).

7. **Remove `ADMIN_PASSWORD` (and `ADMIN_EMAIL`) from the environment.** They are never read again once a user exists (AC-02.2). Close the window you started the API from, or run `Remove-Item Env:ADMIN_PASSWORD, Env:ADMIN_EMAIL`. If you ever stored them as user variables or in user-secrets, remove them there too (`[Environment]::SetEnvironmentVariable('ADMIN_PASSWORD', $null, 'User')`, `dotnet user-secrets remove ADMIN_PASSWORD --project src/InventoryTracker.Api`). From now on `run.ps1` needs only `JWT_SIGNING_KEY`, and warns you if `ADMIN_PASSWORD` is still set.

8. **Create a second Manager** (`POST /api/v1/users`; the README explains why to keep at least two, SEC-04).

Later starts: open a PowerShell window and run step 4. Stop with Ctrl+C.

> **Do not run the sample requests while the real API is running (SEC-31).** The walk-through in `src/InventoryTracker.Api/InventoryTracker.Api.http` sends its requests to `http://localhost:5133`, which is also the port of the real API started by `run.ps1`. It writes data that can never be deleted (PRD section 5), and its version 1.1 part creates a Seller account whose password sits in a plain-text file and ships stock out of the Warehouse. Use it only with the Development API (`dotnet run --project src/InventoryTracker.Api`), after stopping the real one (Ctrl+C). If you are not sure which one answers on port 5133, ask it: the real API refuses the OpenAPI document, Development serves it.
>
> ```powershell
> try { (Invoke-WebRequest -UseBasicParsing http://localhost:5133/openapi/v1.json).StatusCode } catch { [int]$_.Exception.Response.StatusCode }
> ```
>
> `401` means the real API (Production) is running: do not send anything. `200` means Development. `0` means nothing answers on that port. A separate Development port is on the PRD Later list.

### Upgrading to a new version

At start-up, `Program.cs` (1) checks the tolerance and the signing key, (2) applies any migrations the database has not seen yet (`MigrateDatabaseAsync`, which uses EF Core's `MigrateAsync`), and (3) creates any missing role, all before it listens. There is no separate database step.

1. Run the tests on the new code: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test.ps1`
2. Stop the API (Ctrl+C).
3. Back up the database (section 7): `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\backup-database.ps1`. This backup is your way back: migrations only go forward.
4. Publish the new version into a **new** folder, so the old one stays as your rollback: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\publish.ps1 -Output publish\win-x64-2026-12-01`
5. Start it: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run.ps1 -Published -PublishFolder publish\win-x64-2026-12-01`. The log shows `Applying migration '...'` for each new migration.
6. Check `/health` and sign in.

If start-up stops during a migration, nothing else happens: the API does not listen. Restore the backup from step 3, run the old folder (section 8) and report the log to the developers.

### Upgrading from version 1 to version 1.1 (once)

Version 1.1 (sales orders, customers, the Seller role) changes the database. On its first start it applies one new migration, `SalesOrders`: three new tables (`Customers`, `SalesOrders`, `SalesOrderLines`) and one new column on `Movements`. It also adds the Seller role. Nothing has to be run by hand, but **back up first**, because this is a one-way step:

- The migration runs as **one transaction**. If it fails part-way, the database stays exactly as version 1 left it and the API does not start.
- Once version 1.1 has started on the database, **version 1 cannot use it any more**: version 1 does not know the new kind of stock movement (`ShippedToCustomer`). The way back is to restore the backup you take in step 3, never to start version 1 on the upgraded file.

Do this in a PowerShell window at the repository root. The commands assume the default database location. If you set `ConnectionStrings__Default` for `run.ps1`, set it the same way in this window, so the backup, the start and any restore all use the same file.

1. **Check the new code** (optional, about 3 to 7 minutes): `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test.ps1`. It ends with `Build succeeded with 0 warnings.` and `All tests passed.`

2. **Stop version 1**: Ctrl+C in the window where it runs. Then check that no copy is left. This prints nothing when the API is stopped:

   ```powershell
   Get-Process InventoryTracker.Api -ErrorAction SilentlyContinue
   ```

3. **Back up the database:**

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\backup-database.ps1
   ```

   It ends with `Backed up 1 file(s) from ...\inventory.db` or `Backed up 3 file(s) ...` (the database and its two side files, section 7; both are complete backups) and `to C:\Users\<you>\AppData\Local\InventoryTracker\backups\inventory-<yyyyMMdd-HHmmss>`. **Write that folder down**: it is your way back. If the script answers `The API is running: ...`, go back to step 2. For an extra copy off the machine, copy that folder to an encrypted drive (section 7, "Protect it").

4. **Keep the version 1 folder for the way back**, by renaming it:

   ```powershell
   Rename-Item publish\win-x64 win-x64-v1
   ```

   If the rename fails with "being used by another process", version 1 is still running: go back to step 2. If you have no `publish\win-x64` folder, skip this step; the way back below says how to rebuild version 1.

5. **Publish version 1.1:**

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\publish.ps1
   ```

   It ends with `Bundled ASP.NET Core runtime: 10.0.15` and `Published to ...\publish\win-x64`.

6. **Start version 1.1.** It upgrades the database by itself:

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run.ps1 -Published
   ```

   The log shows `Applying migration '20261123155451_SalesOrders'.`, then `Now listening on: http://127.0.0.1:5133`. The expected `Failed to determine the https port for redirect.` warning (section 10) comes later, at the first request. If the window shows `crit:` or `fail:` lines and returns to the prompt instead, the upgrade did not happen: see "If it goes wrong" below.

7. **Check that it works**, in a second PowerShell window:
   - `Invoke-RestMethod http://localhost:5133/health` answers `Healthy`.
   - Sign in with the snippet of section 6, step 5. Use your usual email and password: the upgrade changes no user, password or role.
   - The new tables are there. As a Manager, the customer list answers, empty at first:

     ```powershell
     (Invoke-RestMethod http://localhost:5133/api/v1/customers -Headers @{ Authorization = "Bearer $token" }).totalCount
     ```

     It prints `0`.
   - Look at a few of your own items and stock figures. They should be exactly as they were in version 1.

From then on, start the API every day with `scripts\run.ps1 -Published`, as before. Keep the backup from step 3 and the `publish\win-x64-v1` folder until version 1.1 has run well for a few weeks; deleting them then is your call.

**If it goes wrong, or you want version 1 back.** Version 1 cannot read version 1.1 data, so the way back is **always** to restore the backup from step 3. Anything recorded in version 1.1 since that backup is lost: note it first, and enter it again in version 1.

1. Stop version 1.1 (Ctrl+C) and check with `Get-Process InventoryTracker.Api -ErrorAction SilentlyContinue` that no copy is left.
2. Restore the backup, with the folder you wrote down in step 3:

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\restore-database.ps1 -BackupFolder "$env:LOCALAPPDATA\InventoryTracker\backups\inventory-<yyyyMMdd-HHmmss>"
   ```

   The upgraded database is not deleted: it is moved into a `before-restore-<time>` folder next to the database.
3. **Make version 1 the daily start again** by swapping the two folders, so that `publish\win-x64` holds version 1 and the daily command starts it. If you have no `publish\win-x64-v1` folder, rebuild it first, as described after these steps.

   ```powershell
   Rename-Item publish\win-x64 win-x64-v1.1
   Rename-Item publish\win-x64-v1 win-x64
   ```

   Do this only while the API is stopped (step 1): a running API keeps its folder in use, and the rename then fails with "being used by another process". Without the swap, `publish\win-x64` would still hold version 1.1, and the next `run.ps1 -Published`, the next day for example, would upgrade the restored file again without asking.
4. **Start version 1** with the daily command:

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run.ps1 -Published
   ```

   The log shows `No migrations were applied. The database is already up to date.`, and `Content root path:` ends in `publish\win-x64`. If it shows `Applying migration '20261123155451_SalesOrders'.` instead, version 1.1 started: the swap did not happen. Stop it (Ctrl+C) and go back to step 1. Check `/health` and sign in as in step 7, and check that the movement list answers:

   ```powershell
   (Invoke-RestMethod http://localhost:5133/api/v1/movements -Headers @{ Authorization = "Bearer $token" }).totalCount
   ```

   It prints a number. **Version 1 does not refuse an upgraded file**: started on one by mistake, it logs the same "No migrations were applied" line and starts, but once anything has shipped, the movement list fails with `(500) Internal Server Error`, and Sellers show no roles. That is the sign that the restore was skipped: stop it and do step 2.

If you have no version 1 folder, rebuild it from git first. Version 1's code is commit `807346b`. From the repository root:

```powershell
git worktree add ..\inventory-tracker-v1 807346b
powershell -NoProfile -ExecutionPolicy Bypass -File ..\inventory-tracker-v1\scripts\publish.ps1 -Output publish\win-x64-v1
git worktree remove ..\inventory-tracker-v1
```

The first line makes a second, temporary copy of the code at version 1 next to the repository. The second publishes it into this repository's `publish\win-x64-v1`. The third deletes the temporary copy.

**Trying version 1.1 again later.** Stop the API and take a **fresh** backup with `scripts\backup-database.ps1`: it is now your way back, because it holds what you recorded in version 1 since the restore. Then either swap the folders back, if version 1.1 itself was not the problem:

```powershell
Rename-Item publish\win-x64 win-x64-v1
Rename-Item publish\win-x64-v1.1 win-x64
```

or, if the developers gave you a fixed version, delete `publish\win-x64-v1.1` and follow the upgrade from step 4 (keep the version 1 folder, publish). In both cases, start with `scripts\run.ps1 -Published` and check as in steps 6 and 7.

If the upgrade failed, report the text of the failed start to the developers before trying it again.

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

Nothing is deleted. The current database and its side files are moved into `before-restore-<time>` next to the database, then the backup set is copied into place. Moving the old `-wal` file aside matters: an old write-ahead log next to a restored database would mix two states. Start the API again; if the backup is from an older version, the API applies the newer migrations to it on start. Start the version the backup belongs to (section 6 or 8 for going back).

**Test a restore** once, soon. Stop the API first (only one copy may run at a time). In a new window, set `ConnectionStrings__Default` to a test path that keeps the file name `inventory.db`, for example `$env:ConnectionStrings__Default = 'Data Source=C:\RestoreTest\inventory.db'`: the restore script looks for a file of that same name in the backup folder. Restore into it, start the API with `-Port 5134`, sign in, stop it, and close the window so the test path is forgotten.

## 8. Rolling back

- **Code only (no new migrations in the new version):** stop the API and start the previous folder: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run.ps1 -Published -PublishFolder publish\win-x64-<previous>`. If you no longer have it, check out the previous version with git and publish it again: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\publish.ps1`
- **Code and data (the new version applied migrations):** the old version may not understand the newer schema. Stop the API, restore the backup taken just before the upgrade (section 7), then start the previous folder. Anything recorded since that backup is lost, so note it first and enter it again.
- **From version 1.1 back to version 1:** always code and data, because version 1.1 applied the `SalesOrders` migration and version 1 cannot read version 1.1 data. Follow "If it goes wrong, or you want version 1 back" in section 6.

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
| `Not started: JWT_SIGNING_KEY is saved for your Windows user, but this window does not have it` | The key exists; this window just does not have it. A window receives the user variables once, when it starts, and VS Code's terminals (new tabs included) inherit VS Code's environment from when VS Code started. **Do not make a new key**: it would not match the saved one. Open a new PowerShell window (in VS Code, close and reopen VS Code completely), or load the saved key into this window with `$env:JWT_SIGNING_KEY = [Environment]::GetEnvironmentVariable('JWT_SIGNING_KEY', 'User')`. |
| `Not started: JWT_SIGNING_KEY is not set. Save a random key for your Windows user once` | No key in this window or for your user. Save one with `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\new-signing-key.ps1 -SaveForUser` (section 3), then open a new window. A window-only `$env:` key is for a quick test only. |
| `Not started: JWT_SIGNING_KEY in this window is shorter than 32 characters.` or `... in this window contains spaces, so it is not a generated key` | The value in **this window** is not a usable key: too short, or a phrase or captured text (running the key script from the wrong folder captures PowerShell's start-up banner), which can be guessed. The message then depends on whether a key is also saved for your Windows user, because the script never reads the saved key and so cannot tell which one is bad. **Nothing saved:** save a generated key with `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\new-signing-key.ps1 -SaveForUser`, then open a new window. **A key is saved** (the message says so): do not replace it yet, because the bad value may be only in this window. Run `Remove-Item Env:JWT_SIGNING_KEY` and load the saved key with `$env:JWT_SIGNING_KEY = [Environment]::GetEnvironmentVariable('JWT_SIGNING_KEY', 'User')`, or open a new window, and run `run.ps1` again. Only if the same message comes back is the saved key itself bad: then replace it with `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\new-signing-key.ps1 -SaveForUser -Force` and open a new window. Replacing the saved key signs everybody out. |
| `Not started: this is the first start (no database at ...), so ADMIN_EMAIL and ADMIN_PASSWORD are needed` | First start, or `ConnectionStrings__Default` points somewhere new. Set both (section 6, step 3), or fix the path. |
| `Not started: ConnectionStrings__Default must use an absolute path` | Use a full path such as `Data Source=D:\InventoryData\inventory.db`. |
| `Not started: remove these environment variables, which could make the API listen on other addresses: ...` (names starting `Kestrel`, `ASPNETCORE_Kestrel` or `DOTNET_Kestrel`) | Remove them; v1 listens on this machine only (section 9). |
| `Not started: The API is running: InventoryTracker.Api.exe (process id ...)` or `... dotnet.exe (process id ...)` | A copy of the API is already running, perhaps in another window, even if it has been idle for a long time. Stop it (Ctrl+C in its window; `Stop-Process -Id <id>` only as a last resort). `dotnet.exe` means a `dotnet` command whose command line names the API project, such as `dotnet run --project src/InventoryTracker.Api` or `dotnet watch`, in another window; the scripts count it as a running API too. |
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
| The sample requests (`.http`) fail to sign in | Create `src/InventoryTracker.Api/.env` with your own `ADMIN_EMAIL` and `ADMIN_PASSWORD` (not a `.env` at the repository root), and use port 5133 with the **Development** API only. Never send them while the real API is running (SEC-31, the note after section 6 step 8). |
| (v1.1) The first start of version 1.1 shows `crit:` or `fail:` lines about the database and returns to the prompt | The `SalesOrders` migration failed. It runs in one transaction, so the database is still as version 1 left it. Keep the window's text, restore the backup and start version 1 (section 6, "If it goes wrong, or you want version 1 back"), and report the text to the developers. |
| (v1.1) Version 1 was started on the database after version 1.1 had upgraded it (it starts without complaint, but the movement list answers `500 internal-error` once anything has shipped, and Sellers show no roles) | Stop it at once and restore the backup taken before the upgrade (section 6, "If it goes wrong"). Version 1 cannot read version 1.1 data. |
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

### Version 1.1: upgrade rehearsal from a clean copy (2026-11-25)

Run on the owner's machine (Windows 11, Windows PowerShell 5.1.26100, .NET SDK 10.0.404, installed runtime 10.0.15) by `devops-engineer`. As above, `scripts\x.ps1 ...` stands for the full `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\x.ps1 ...` form, which is what was run.

**Set-up.** A clean copy of version 1.1 (`git archive` of commit `6639784`, plus this guide and the two script comment and message changes of this phase) in an empty temporary folder. Version 1 came from `git worktree add` of commit `807346b`, published with that worktree's own `publish.ps1 -Output publish\win-x64` into the clean copy, so it stood where the owner's version 1 folder stands. Every API start used port 5301 or 5302 (never 5133), throwaway `JWT_SIGNING_KEY`, `ADMIN_EMAIL` and `ADMIN_PASSWORD` values set in the process only, and `APPDATA` and `LOCALAPPDATA` pointed into the temporary folder, so `run.ps1`, the backup and the restore used their **default** database and backup locations inside it. The owner's database, backups, user-secrets, `.env` and saved key were not touched. Afterwards the temporary `APPDATA` held only NuGet's configuration: no user-secrets were read. The package cache was the machine's usual one (not emptied, unlike the version 1 check).

| Step (from the clean copy's root) | Result |
|-----------------------------------|--------|
| `scripts\test.ps1 -ArtifactsPath TestResults\rel11-devops` | Build succeeded, **0 warnings, 0 errors**; tests **2,510 of 2,510 passed**, 0 failed, 0 skipped (test run 2 min 55 s; whole script 3 min 10 s). Output folder deleted afterwards. |
| First start of version 1: `scripts\run.ps1 -Published -Port 5301` | `First start: ...`; the 4 version 1 migrations applied; first Manager created; `Production`; listening on `127.0.0.1:5301` and `[::1]:5301` only (`netstat`). Then: sign-in `200`, a unit, a raw material and a 1,200 kg receipt created; `GET /api/v1/customers` `404` (no such endpoint in version 1). Stopped (forced, so the `-wal` and `-shm` files remained). |
| Upgrade step 2: `Get-Process InventoryTracker.Api -ErrorAction SilentlyContinue` | Printed nothing. |
| Step 3: `scripts\backup-database.ps1` | `Backed up 3 file(s)`, into `...\InventoryTracker\backups\inventory-20261125-180337`. |
| Step 4: `Rename-Item publish\win-x64 win-x64-v1` | Renamed. A separate try while an API ran from the folder failed with `The process cannot access the file because it is being used by another process.`, as step 4 says; the backup script then refused with `The API is running: InventoryTracker.Api.exe (process id ...)`. |
| Step 5: `scripts\publish.ps1` | `Bundled ASP.NET Core runtime: 10.0.15`; `Published to ...\publish\win-x64` (364 files, no database or `.env`). |
| Step 6: `scripts\run.ps1 -Published -Port 5301` | `Applying migration '20261123155451_SalesOrders'.`, then listening on the two loopback addresses only, `Hosting environment: Production`, content root the new `publish\win-x64`. |
| Step 7: the section 6 step 5 snippet and the step 7 checks, in Windows PowerShell 5.1 | `/health` `Healthy`; `Signed in.` with the version 1 password; customers `totalCount` `0`; Cheese still 1,200 kg. The SEC-31 check printed `401` (the real API); with nothing listening it prints `0`. Then version 1.1 data: a customer, a sales order for 100 kg, shipped (`200`, a `ShippedToCustomer` movement, stock 1,100 kg), and a Seller user (`201`, so the Seller role existed). Stopped. |
| Side check, on a **copy** of the upgraded files: version 1 started on them with `ConnectionStrings__Default` | Started without complaint (`No migrations were applied.`); stock and items read; `GET /api/v1/movements` `500 internal-error`; the Seller listed with no roles. Recorded in the way back (step 4) and in section 10. |
| Way back, step 1, then step 2: `scripts\restore-database.ps1 -BackupFolder "$env:LOCALAPPDATA\InventoryTracker\backups\inventory-20261125-180337"` | The 3 upgraded files moved to `before-restore-20261125-180536`; 3 files restored. |
| Step 3: `scripts\run.ps1 -Published -PublishFolder publish\win-x64-v1 -Port 5301` (the first version of the way back, without the folder swap) | `No migrations were applied. The database is already up to date.`; content root `publish\win-x64-v1`; `/health` `Healthy`; `Signed in.`; Cheese 1,200 kg; movements `totalCount` 1 (the receipt only); no Seller user; `/customers` `404`. Version 1 runs again on the backup. Stopped. Code review then found that the next plain `run.ps1 -Published` would start version 1.1 again, so the way back now swaps the folders; see the re-check below. |
| `scripts\restore-database.ps1` again, after its closing message was changed to point at `-PublishFolder` for going back to an older version | Same result (3 files moved aside, 3 restored), with the new message. All eight scripts parse without errors and are plain ASCII. |
| `git worktree remove` of the version 1 worktree (with `bin\` and `obj\` from its publish inside), then `git worktree prune` | Removed without `--force`: the commands in the way back work as written. |

**Re-check of the way back with the folder swap (2026-11-25, after code review).** Same isolation as above, on new scratch copies (v1.1 from `git archive` plus this phase's files; v1 from a new `git worktree` of `807346b`). The version 1 first start, a receipt of 1,200 kg, upgrade steps 2 to 7 and a shipped sales order (stock 1,100 kg) went as in the table above. Then:

| Step | Result |
|------|--------|
| Way back, step 1, then step 2: `scripts\restore-database.ps1 -BackupFolder ...` | Upgraded files moved aside, 3 files restored. Closing line: `Start the API again with scripts\run.ps1 -Published. ...` |
| Step 3: `Rename-Item publish\win-x64 win-x64-v1.1`, then `Rename-Item publish\win-x64-v1 win-x64` | `publish` then held `win-x64` (version 1) and `win-x64-v1.1`. |
| Step 4: the daily `scripts\run.ps1 -Published -Port 5301` | `No migrations were applied. The database is already up to date.`; content root `publish\win-x64`; `/health` `Healthy`; `Signed in.`; movements `totalCount` 1 (the shipment is gone); Cheese 1,200 kg: version 1 on the backup. Stopped. |
| Trying version 1.1 again: `scripts\backup-database.ps1`, then `Rename-Item publish\win-x64 win-x64-v1` and `Rename-Item publish\win-x64-v1.1 win-x64`, then `scripts\run.ps1 -Published -Port 5301` | Fresh backup of 3 files; `Applying migration '20261123155451_SalesOrders'.`; `/health` `Healthy`; `Signed in.`; movements 1, customers 0, Cheese 1,200 kg. Stopped. |

The worktree was removed with `git worktree remove` and `git worktree prune`; the scratch `APPDATA` held only NuGet's configuration; the scratch folder was deleted.

Every instance was stopped by its process id after its command line was checked; afterwards nothing listened on 5301 or 5302. The temporary folder, with its databases, backups and published folders, was deleted.

Not verified: a clean Ctrl+C stop (the scratch instances were stopped by process id, so each backup had 3 files; after Ctrl+C expect 1), a failure of the migration on the owner's real data (the security review forced one on a scratch database, 06 section 11.6: the database stayed as version 1 left it), CI on GitHub for the version 1.1 commits (section 4), and PowerShell 7.

## 12. Security release conditions (`docs/06-security-review.md` 7.2)

| # | Condition | Status |
|---|-----------|--------|
| 1 | SEC-03, #119: bind to localhost only; state HTTPS, HSTS and the rate limit are required before exposure | Met: `run.ps1` binds `127.0.0.1` and `[::1]` only and refuses Kestrel endpoint variables under every prefix (`Kestrel`, `ASPNETCORE_Kestrel`, `DOTNET_Kestrel`, written with `__` or `:`); sections 9 and the cloud guide state the requirements. |
| 2 | SEC-06: runtime 10.0.15 or later, `dotnet sdk check` recorded | Met: the published folder bundles 10.0.15, and since 2026-11-22 the machine has runtime 10.0.15 installed too (owner; re-checked 2026-11-25, section 1). |
| 3 | SEC-13: start in Production, not through the launch profile | Met: `run.ps1` passes `--environment Production` and runs the published exe or `dotnet run --no-launch-profile`. |
| 4 | SEC-12: no usable sample password | Met. `docs/03-architecture.md` (section 8, about line 490) and `InventoryTracker.Api.http` (header, line 24) hold the placeholder `<choose-your-own-strong-password>`. Every password the `.http` walk-through sends, including the sample Warehouse user's three, is read with `{{$dotenv ...}}` from the git-ignored `src/InventoryTracker.Api/.env` (decision #135); `.env.example` lists the names. No password literal that passes the policy is left in `src/`, `scripts/`, the README or this guide. Remaining literals are in test code against throwaway databases, and in the design, test-plan, bug and security-review documents (reported to the delivery manager). First-password steps: section 6, steps 6 and 7. |
| 5 | SEC-04, #120: keep at least two active Managers | For the README (`technical-writer`); referenced in section 6, step 8. |
| 6 | SEC-07, #121: runbook "deactivate to remove access now" | For the README (`technical-writer`). |
| 7 | SEC-13: random signing key per machine | Met here (`new-signing-key.ps1`, section 3); the README must repeat it. |
| 8 | SEC-16: the database and its backups hold password hashes | Met: section 7, "Protect it", and the backup script's message. |
| 9 | SEC-02 residual, #126: `ApiExceptionHandler` at Warning | Met: `appsettings.Production.json` (decision #130). |

## 13. Security release conditions for version 1.1 (`docs/06-security-review.md` 13.2)

| # | Condition | Status |
|---|-----------|--------|
| 1 | Back up before the first v1.1 start; the way back is that backup, because v1 cannot read v1.1 data | Met in this guide: section 6, "Upgrading from version 1 to version 1.1" (stop v1, `backup-database.ps1`, keep the v1 folder, publish, `run.ps1 -Published`, health and sign-in) and "If it goes wrong, or you want version 1 back" (`restore-database.ps1`, swap the folders while the API is stopped so the daily `run.ps1 -Published` starts v1, then start it; trying v1.1 again needs a fresh backup first). Rehearsed end to end on scratch copies (section 11, v1.1). The release notes must say the same (`technical-writer`). |
| 2 | Localhost only, in Seller terms | This guide: the opening paragraph and section 9. The release notes and user guide are for `technical-writer`. |
| 3 | Customer notes (SEC-28) in the user guide | For `technical-writer`. |
| 4 | README runbook names Sellers (SEC-07) | For `technical-writer`. |
| 5 | Start the released build through `scripts\run.ps1 -Published`; do not run the `.http` walk-through while the real instance runs (SEC-31) | Met: re-checked for the v1.1 build (section 11, v1.1): Production, loopback only, published folder with runtime 10.0.15. The `.http` warning is the note after section 6 step 8, with a check of which API answers on port 5133, and section 10. A separate Development port stays on the PRD Later list. |
