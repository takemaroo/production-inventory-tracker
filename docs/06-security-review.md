# Security review: Production & Inventory Tracker API

Phase 6, `security-reviewer`, 2026-11-21, at commit `14c60fb` (main). **Re-check** of the SEC-01 and SEC-02 fixes, and of the owner decisions, the same day at commit `a3c1d66` (main); see section 6. **Final check of the phase 7 release** on 2026-11-22, at commit `8dbdbe7` plus the uncommitted working tree listed in section 6.6. **Signing-key re-check** of an uncommitted change on 2026-11-22, on top of `1e820b4`; see section 6.12.

## Summary

**Final check (phase 7, 2026-11-22).** The release scripts, CI workflow, release and cloud guides, README, user guide and release notes meet all nine release conditions of section 7.2. Condition 2 is met for the released build: the published folder bundles runtime 10.0.15. Updating the runtime installed on the machine stays the owner's task, and the documents say so. I started `scripts\run.ps1 -Published` with `ASPNETCORE_URLS`, `DOTNET_URLS`, `URLS` and `ASPNETCORE_ENVIRONMENT=Development` all set to try to redirect it. It still listened only on `127.0.0.1` and `[::1]`, and only in Production (netstat). It refused every Kestrel endpoint variable I tried, and backup, restore and a second start all refused while it ran. With the Production log setting (#130), a 2 MB body got `413` and wrote no log line. The log held none of the throwaway secrets. The signing-key script uses the operating system's cryptographic generator. CI has a read-only token and no secrets, does not use `pull_request_target`, and interpolates no untrusted input. No real secret is in any tracked or new file or in the two new commits. Both SEC-12 places, and every password in the `.http` walk-through, now hold placeholders or `$dotenv` reads. The NuGet audit is clean. I found three new low or info items, none blocking: CI actions pinned to tags rather than commit SHAs (SEC-19), `az` commands in the cloud guide that would print secrets (SEC-20), and policy-passing literals in the design document and git history (SEC-21, Later). **Follow-up the same day:** `devops-engineer` fixed SEC-19 and SEC-20, and the README wording of SEC-21. I re-checked the diff (section 6.11). **Verdict: release approved from a security standpoint**, for localhost-only v1. One condition: the uncommitted files are committed as reviewed (section 7.1).

**Signing-key re-check (2026-11-22, section 6.12).** A code review found that the old documented key-save command, run from the wrong folder, saved PowerShell's 81-character start-up banner as the permanent key. The uncommitted fix makes and saves the key inside one script, and `run.ps1` now refuses keys with spaces. I verified it with stubs, without touching the saved key:
- 384 random bits from the operating system's generator;
- no path prints the key;
- the banner is now refused.

It is **approved to commit**. Two new low items, neither blocking: the API itself still accepts any 32 characters (SEC-22, Later), and the storage advice understates who receives a user variable (SEC-23).

**First review and re-check (phase 6).** The authentication and authorisation core holds up. Every forged, altered or expired token I tried was refused. The security stamp is checked on every request. The lock-out counts correctly under 40 parallel attempts, and the T-003 timing leak is still closed. All 48 endpoints match PRD 4.2, and no endpoint takes a user id from the request body. All SQL is parameterised. Error bodies and logs reveal nothing secret. No secret is in any tracked file or anywhere in git history. The NuGet audit finds no known vulnerabilities (re-run at `a3c1d66`). Even when flooded by authenticated clients, the stock rules held. The first review found no critical or high findings and four medium ones. **The two code findings are now fixed and re-verified against a live instance.** SEC-01: one request could stall every write and take 3.1 GB of memory. Now the same bodies are refused in 1 to 77 ms with a 0.3 KB answer, other users' writes are not delayed, and the process stayed at about 210 MB. SEC-02: an anonymous flood wrote Error-level stack traces at 2.6 MB/s. Now the same requests get a proper `413`, and each writes one Information line of 198 bytes, with no stack trace. The owner settled the other two mediums for v1: the API runs on localhost only (#119), and the lock-out stays without a rate limit (#120). The 401/403 checks still come before any body is read, and the error responses that are not capped are bounded in size. What remains are deployment and documentation conditions for phase 7 (section 7.2). **Recommendation: clear to release**, on those phase 7 conditions. This replaces the earlier "go-with-conditions".

## 1. Scope and method

**What the product exposes.** It is an HTTP JSON API (48 endpoints under `/api/v1`, plus anonymous `GET /health` and, in Development only, `GET /openapi/v1.json`). It has three roles and JWT bearer tokens, stores its data in a SQLite file, and runs as a single process. It holds staff emails, salted password hashes, Identity security stamps, and stock, order and audit data. It has no user interface yet; a front end is planned.

**What I did (first review, `14c60fb`).**

- Read the documents the brief named, then every file in `src/InventoryTracker.Api/Auth`, `Features/Users`, `Common/Errors`, `Common/Validation`, `Common/Paging`, `Data/DatabaseSetup.cs`, `Data/WriteTransaction.cs`, `Program.cs`, the settings files, every controller's route and `[Authorize]` attribute, the notification service, and the order-line validation used by send, complete and the complementary orders.
- Ran the app twice from a build in my scratchpad: once in Development (port 5191) and once in Production (port 5192). Each used its own throwaway SQLite file, throwaway `ADMIN_*` and `JWT_SIGNING_KEY` values, and `APPDATA` pointed at the scratchpad, so the owner's user-secrets were never loaded. Nothing touched `src/InventoryTracker.Api/inventory.db`. I stopped both instances afterwards.
- Probed both instances with small Python scripts, kept in `C:\Users\erick\AppData\Local\Temp\claude\0422f218cc2700ed\3fa433b3-adfa-4ff0-b327-6d52555ccac1\scratchpad\probes\` (`p1_jwt.py` to `p15_logflood.py`; `api.py` is the shared helper). Every number in this document comes from those runs on this machine (16 logical processors, Windows 11).
- Searched all 38 commits of git history for secrets (section 3.5).
- Ran `dotnet list package --vulnerable --include-transitive`, `dotnet list package --outdated` and `dotnet sdk check`.
- Re-ran the security-related test classes: `dotnet test --artifacts-path TestResults/sec-art --filter-class ...AuthTests --filter-class ...RoleMatrixTests --filter-class ...SecretsConfigurationAcceptanceTests --filter-class ...UsersTests`: **386 total, 386 passed**. (A first run with the artifacts outside the repository failed 7 `SecretsConfigurationAcceptanceTests`: they find the repository by walking up from the test binaries, so they cannot work from a folder outside it. This is not a product fault.)

**What I did (re-check, `a3c1d66`).**

- Read decisions #119 to #127, the fix commit `0961b35` (all product-code changes) and the later commits `eb3cf16`, `e79875d`, `d621b9a` and `a3c1d66`. Their product-code changes are comment-only.
- Built `a3c1d66` into `...\scratchpad\recheck\art`. I ran it in Development on `127.0.0.1:5193`, and in Production on `127.0.0.1:5194` with EF Core SQL logging raised to Information, so I could see what database work a request caused. Each instance had a fresh SQLite file in `...\scratchpad\recheck\db\`, throwaway `ADMIN_EMAIL`, `ADMIN_PASSWORD` and `JWT_SIGNING_KEY` values, and `APPDATA` pointed at `...\scratchpad\recheck\appdata`. The owner's `src/InventoryTracker.Api/inventory.db` does not exist on this machine and was not created. I stopped both instances afterwards (`taskkill`; no listener left on 5193 or 5194).
- Probes: `...\scratchpad\recheck\probes\` (`r1_sec01.py`, `r2_sec02.py`, `raw.py`) and `...\scratchpad\recheck\probes-prod\` (`r3_nodbwork.py`, `r4_409.py`, `r5_query.py`). Their outputs are saved next to them as `r1_out.txt` to `r4_out.txt`. `raw.py` writes the body on a separate thread while it reads the answer, so an early `413`, `401` or `403` is seen even while the body is still being sent.
- Ran the whole test suite: `dotnet test --artifacts-path TestResults/recheck-art`: **1,721 total, 1,721 passed, 0 failed** (4 min 13 s).
- Re-ran `dotnet list package --vulnerable --include-transitive` and `dotnet sdk check` (section 3.6).

**What I did (final check of the phase 7 release, 2026-11-22).**

- Read decisions #119 to #135, `git log -p 7590787..HEAD` (`43b3934`, `8dbdbe7`) and `git diff` of the working tree. Then I read every script in `scripts/`, `.github/workflows/ci.yml`, `appsettings*.json`, `.gitignore`, `.env.example`, the `.http` file, `docs/07-release.md`, `docs/07a-cloud-guide.md`, `README.md`, `docs/08-user-guide.md` and `docs/09-release-notes.md`. The product code changed only in `appsettings.Production.json` and the `.http` file.
- Ran the published folder `publish\win-x64` (built at 10:12, after the last change to `src/`; its `appsettings*.json` are identical to the source ones) through `scripts\run.ps1 -Published -Port 5201`. I used throwaway `JWT_SIGNING_KEY`, `ADMIN_EMAIL` and `ADMIN_PASSWORD` values and a scratch database. `APPDATA` and `LOCALAPPDATA` pointed under `...\scratchpad\final-sec\`. I also set redirection attempts (section 6.7). The scripts are `start.ps1` and `probe.ps1` in that folder, and the outputs are in its `logs\`. I stopped the instance (`taskkill /T`) and confirmed that nothing listened on 5200 to 5209 afterwards. The owner's databases and user-secrets were not touched: the scratch `appdata` folder stayed empty.
- Scanned every tracked and new file for policy-passing password literals and secret patterns (section 6.9), and checked `.gitignore` with `git check-ignore`.
- Ran `dotnet list InventoryTracker.slnx package --vulnerable --include-transitive` and `dotnet sdk check`.
- Did not run: the CI workflow (it runs only after a push) and the cloud guide (nothing may be deployed). I also did not test `HTTP_PORTS`/`ASPNETCORE_HTTP_PORTS` live: if it won over `--urls`, it would bind every network interface of the owner's machine (section 6.7).

**Not applicable, and why.**

- CSRF: there are no cookies. The token travels in the `Authorization` header, which a browser never adds to a cross-site request by itself.
- CORS: no CORS policy is configured, so browsers allow same-origin calls only. This becomes relevant when the front end is built (SEC-10).
- File uploads, file paths, templates and shell commands: the product has none.
- Password reset by email: there is none. Only a Manager can set another user's password (AC-03.7).
- XML and deserialisation of types: the only body format is JSON, with no polymorphic types.

## 2. Findings

Severity: **critical** (exploitable now, serious impact), **high**, **medium**, **low**, **info** (no change needed now, recorded for later). "Required" means I consider it a condition of release (section 7). "Later" means it can go on the PRD's Later list.

### SEC-01 (medium): one request from any signed-in user can stall all writes and take gigabytes of memory. **Fixed.**

**Status: fixed in `0961b35`, re-verified at `a3c1d66` (section 6.1).** Every body is limited to 1 MB (Kestrel and IIS), and every line-carrying body to 500 lines (#122, #123). User role lists are limited to 10 (#125). A `400` lists at most 100 problems. Both caps are checked before the write transaction. The original finding is kept below as the record.

**Where (as found).**
- `Features/ProductionOrders/SendOrderService.cs:100` opens the write transaction, and `:110` then validates the body inside it.
- The same pattern is in `CompleteOrderService.cs:125`/`:136`, `ComplementaryOrders/RequestComplementaryOrderService.cs:74`/`:85`, `ComplementaryOrders/ComplementarySendService.cs:96`/`:110` and `Recipes/RecipeService.cs:74`/`:87`.
- `OrderLineMatcher.cs:123` loops over every submitted line, with no limit.
- `Common/Validation/ValidationErrors.cs:32` joins every message into `detail`, with no limit.
- No body size limit is set anywhere, so Kestrel's default of 30,000,000 bytes applies.

**How it was exploited.** A Warehouse user (the lowest privilege that can send) posts `{"lines":[null,null,...]}` to `POST /api/v1/production-orders/{n}/send` for any order that exists, in any status. The 400 for the body comes before the 409 for the state, so a completed order works too. Each `null` line costs 5 bytes and produces two error messages. Model binding reads the body before the action runs. The matching, and building millions of error strings, then happen while the request holds SQLite's single write lock (`BEGIN IMMEDIATE`). Every other write in the company waits behind it. The 400 response then contains every message twice, once in `detail` and once in `errors`.

**Measured before the fix** (`p7_biglines.py`, Development instance):

| Body | Answer | Time | Response size | Worst receipt by another user meanwhile (normal: 16 ms) |
|------|--------|------|---------------|-------------------------------------------------------|
| 100,000 null lines (0.5 MB) | 400 | 0.24 s | 13.4 MB | 27 ms |
| 1,000,000 null lines (5 MB) | 400 | 3.6 s | 135.8 MB | 2,302 ms |
| 3,000,000 null lines (15 MB) | 400 | 9.1 s | 411.8 MB | 7,250 ms |

After the 15 MB request, the API process was holding **3.1 GB** of memory (`tasklist`). The same three bodies after the fix: section 6.1.

**Fix (recommended then, now done).** (1) `MaxRequestBodySize` of 1 MB. (2) A shared line cap checked before `InWriteTransactionAsync`. (3) A cap on the number of validation messages.

### SEC-02 (medium): any anonymous client can fill the log with Error-level stack traces, and oversized or broken bodies are answered `500`. **Fixed.**

**Status: fixed in `0961b35`, re-verified at `a3c1d66` (section 6.2).** `ApiExceptionHandler` now maps `BadHttpRequestException` to `413 request-too-large`, `408 request-timeout` or `400 validation` (#124). Each is logged as one Information line without a stack trace. The original finding is kept below as the record.

**Where (as found).** `Common/Errors/ApiExceptionHandler.cs:48` (`_ => Unexpected(...)`) and `:67` (`LogError(exception, ...)`). Kestrel reports a body that is too large, or a broken chunked body, as `Microsoft.AspNetCore.Http.BadHttpRequestException`, which carries a `StatusCode` of 413 or 400. The handler had no case for it, so it treated it as a programming error.

**How it was exploited.** No token is needed, and no real body is either. The header `Content-Length: 31000000` alone makes Kestrel refuse the request before reading it. The client got `500 internal-error`, and the server logged an Error with a stack trace of about 27 lines.

**Measured before the fix** (`p6_cl.py`, `p15_logflood.py`): from one client with 8 threads, the server answered **678 requests per second**, and the log grew by **26.5 MB in 10 s (2.6 MB/s)**, about 3.9 KB per request. A malformed chunked body also got `500`. The same probes after the fix: section 6.2.

**Residual (low).** A refused body still writes one Information line of 198 bytes. At the 597 requests per second one local client reached, that is about 0.12 MB/s, or 0.4 GB an hour if sustained. On a localhost-only v1 (#119), only a program already running on the owner's machine can do this. Decision #126 lets phase 7 raise the category `InventoryTracker.Api.Common.Errors.ApiExceptionHandler` to Warning, which drops these lines. I recommend doing that, or adding log rotation, before the API is reachable from any other machine (section 7.2, item 9).

**Residual closed in Production (final check).** `appsettings.Production.json` sets that category to Warning (#130). In the published instance, a 2 MB body got `413` and the log gained no `Request body refused` line. The category's only other messages are a Warning (unique-index conflict, `ApiExceptionHandler.cs:77`) and an Error (unhandled exception, `:112`), and both are kept. No other category changed, so sign-in and user-management logging are unaffected.

### SEC-03 (medium, required at deployment): plain HTTP in Production; passwords and tokens travel unencrypted

**Owner decision #119 (2026-11-21).** v1 runs on localhost only, on the owner's machine, so plain HTTP is acceptable for v1. The release guide must state that HTTPS and HSTS are required before the API is reachable from any other machine. It is on the PRD Later list (PRD section 3, Later list; PRD row 22). **Accepted for localhost-only v1.** This makes binding to localhost a release condition (section 7.2, item 1).

**Where.** `Program.cs:99-102` calls `UseHttpsRedirection()` in Production only. The app configures no HTTPS endpoint, and there is no `UseHsts()`.

**What I saw.** The Production instance listened on `http://` only and logged `warn: ... Failed to determine the https port for redirect.` It then served every request, including sign-in, over plain HTTP. Even where redirection works, it only helps after the client has already sent the password or token unencrypted.

**How it is exploited.** On a shared office network or Wi-Fi, anyone who can see the traffic reads sign-in passwords and 8-hour bearer tokens, then uses them. On localhost-only, the traffic never leaves the machine.

**Fix before any non-localhost exposure (`devops-engineer`).** Serve the API only over HTTPS. Choose one of these:
- A Kestrel HTTPS endpoint with a certificate, with the certificate path and password supplied as secrets. Then bind HTTP to localhost or turn it off.
- A reverse proxy (IIS, nginx, Caddy) that terminates TLS, with `UseForwardedHeaders` limited to that proxy and Kestrel bound to `127.0.0.1`.

Add `app.UseHsts()` in Production. Record the choice in `docs/07-release.md`.

### SEC-04 (medium, owner decision taken): anyone can keep any known account locked out, including the last Manager's, and the lock-out reveals which emails exist

**Owner decision #120 (2026-11-21).** The 15-minute lock-out of AC-01.4 stays, with no sign-in rate limit in v1. The README says to keep at least two active Managers. Rate limiting is on the PRD Later list (verified: `docs/01-prd.md:55`). **Accepted for localhost-only v1**, where nobody else can reach sign-in. Phase 7 condition: the README states "keep at least two active Managers" (section 7.2, item 5). Before any non-localhost exposure, revisit this together with SEC-05.

**Where.** AC-01.4 as built: `Auth/SignInService.cs:113-127` and `Auth/AppUserManager.cs:60-76`. These are correct to the PRD; the risk is in the rule itself.

**How it is exploited.** No token is needed. Five wrong passwords for `manager@company` lock that account for 15 minutes; five more every 15 minutes keep it locked for ever. That is 20 requests an hour. If every Manager is locked out, nobody can manage users, approve complementary orders or read notifications. The same five requests tell the attacker whether an email has an account: a real account answers `account-locked` on the sixth try, while an unknown email always answers `invalid-credentials`. Decision #62 already notes this.

**Verified** (`p2_signin.py`): 5 wrong passwords, then the right one, gave `account-locked`. Change-password guesses count too (#71): after 5 wrong current passwords, both change-password and sign-in answered `account-locked` (`p13_cp.py`). A Manager's reset clears the lock (#72); this is the recovery path, and it fails when the locked account is the only Manager.

**Options recorded for later.** A per-IP rate limit (SEC-05). Logging lock-outs (SEC-08). Locking per account and client address, or a growing delay instead of a hard lock (this changes AC-01.4).

### SEC-05 (low, Later; required before any non-localhost exposure): no rate limit on sign-in; each attempt costs a password hash

**Owner decision #120 (2026-11-21).** There is no sign-in rate limit in v1; it is on the PRD Later list. **Accepted for localhost-only v1.**

**Where.** No `AddRateLimiter` anywhere. `SignInService.cs:78-83` runs a full PBKDF2 check (about 45 ms of CPU) on every refusal, by design (#62).

**Measured** (`p4_flood.py`): one client process sent sign-ins with random emails over 32 parallel connections and got about 49 answers per second. During the flood, a legitimate sign-in's median time rose from **91 ms to 670 ms**. `GET /units` was not affected (15 ms before, 22 ms during).

**Fix (when exposed).** ASP.NET Core's built-in rate limiter (`builder.Services.AddRateLimiter`, `app.UseRateLimiter()`): a fixed window partitioned by `RemoteIpAddress` on sign-in and change-password (for example 10 a minute), plus a concurrency limiter for hashing. Answer `429` with a ProblemDetails body. Behind a reverse proxy, the client address must come from forwarded headers set by that proxy only.

### SEC-06 (low, required at deployment): the .NET runtime on this machine is three patches behind

**Still open (re-checked at `a3c1d66`).** `dotnet sdk check`: `Microsoft.AspNetCore.App 10.0.12`, "Patch 10.0.15 is available"; SDK 10.0.401, "Patch 10.0.404 is available". The app's NuGet packages are 10.0.15, but the web server (Kestrel) and the rest of ASP.NET Core run from the installed shared runtime, 10.0.12. `dotnet list package --vulnerable` does not check the runtime. I did not look up which security fixes 10.0.13 to 10.0.15 contain, so this rating covers the gap I verified, not a specific vulnerability. The runtime 8.0.18 is also installed and out of support; the app does not use it.

**Fix.** Install the latest .NET 10 runtime (or SDK 10.0.404) on every machine that runs the API, then re-run `dotnet sdk check`. Patch monthly. Stay on 10.x (decision #19).

**Final check: mitigated for the released build; the installed runtime is the owner's task.** `scripts\publish.ps1` bundles runtime 10.0.15 (#129). In `publish\win-x64`, `Microsoft.AspNetCore.Server.Kestrel.Core.dll` is 10.0.15 and `System.Private.CoreLib.dll` is `10.0.15-servicing` (verified). `dotnet sdk check` still reports the installed runtime as 10.0.12. `docs/07-release.md` section 1 records this, and the release notes list it. One caveat: `run.ps1` *without* `-Published` runs from source on the installed runtime. Until the owner installs 10.0.15 or later, start the real instance with `-Published` only, as the README quick start and release guide section 6 already do.

### SEC-07 (low, owner decision taken): removing a role does not end that user's sessions

**Owner decision #121 (2026-11-21).** Role changes keep AC-03.5 and take effect at the user's next sign-in. The README gets a runbook: to remove someone's access immediately, deactivate them. **Accepted.** Phase 7 condition: the runbook is in the README (section 7.2, item 6).

**Where.** `Features/Users/UserService.cs` `SetRolesAsync`, by design (AC-03.5, PRD row 15, decision #66a).

**Verified** (`p9_demote.py`): a Manager signed in. Another Manager removed their Manager role (200). With the old token, the demoted user then **created a new Manager account (201)**. For up to 8 hours they keep every right they had. Deactivation, on the other hand, ended the token at once (401), and it stayed ended after reactivation. The runbook therefore works.

### SEC-08 (low, Later): security events are not fully recorded

**Where.**
- `UserService.cs` and `PasswordService.cs` log the user whose account changed, not the Manager who changed it.
- `SignInService.cs:125` records a failed sign-in in the database but writes nothing to the log. A lock-out starting is not logged either; only a successful sign-in is.

**Why it matters.** A Manager can reset another Manager's password, sign in as them, and act under their name (AC-03.7 allows the reset). Nothing records who did the reset. Password-guessing attacks (SEC-04) are invisible in the log.

**Fix.**
- Pass the caller's `sub` from `UsersController` into the service and log "... by user {ActorId}."
- Log a Warning "Account {UserId} locked after 5 failed sign-ins.", and an Information for each failed attempt on a known account (user id only; never the email or password).

### SEC-09 (low, Later): names accept control, invisible and look-alike characters

**Where.** `Common/Validation/Text.cs:12-16` only trims, and `Domain/NameNormalizer.cs:19` only upper-cases.

**Verified** (`p10_text.py`). These item names were all accepted (201) and returned as typed:
- `"Salt\tfine"` (a tab)
- `"Sugar\u0000white"` (NUL)
- `"Line1\nLine2"`
- `"\u0001\u0002"`: control characters only, which looks blank
- `"Milk\u202Egnp.exe"`: a right-to-left override, which displays as "Milkexe.png"
- `"Che\u200Bese"` and `"Ch\u0435ese"` (Cyrillic е), both accepted next to the existing `"Cheese"`

**Risk.** In a product built to expose waste, two items that look identical but hold separate stock let someone hide material, and a reviewer reading a list cannot tell them apart. NUL bytes are also refused by PostgreSQL text columns, so after the planned port those rows would cause `500`s. Any of these needs Manager rights (items, units) or Production rights (reasons and notes).

**Fix.**
- In one shared helper, used by every name, reason and note: refuse C0 and C1 control characters (U+0000 to U+001F, U+007F to U+009F; allow line breaks in reasons and notes only), bidirectional controls (U+202A to U+202E, U+2066 to U+2069) and zero-width characters (U+200B to U+200D, U+FEFF).
- Normalise names to Unicode NFC before storing, and compare uniqueness on NFKC.
- `ux-designer` adds the message to design 7.2. Look-alikes from other scripts (Cyrillic е) cannot be refused without breaking legitimate names; the front end should show item ids next to names in lists where it matters.

### SEC-10 (low, front-end requirement): stored text comes back raw; safe in this API, dangerous in a careless front end

**Verified** (`p10_text.py`). The item name `<img src=x onerror=alert(1)>` was stored and returned exactly so in `application/json; charset=utf-8`. The JSON is correct, and a modern browser will not run it, because it does not treat `application/json` as a web page. The risk is in the front end: names, reasons and notes appear in lists and inside notification `message` text. Any signed-in user who can write a reason (Production) or a name (Manager) could run script in a Manager's browser if the front end inserts text as HTML. With the token stored in the browser, that script could steal the token.

**Fix.** No server change: escaping HTML in the API would corrupt the data. Add to the front-end requirements (PRD Later list and the future design):
- Show every API string as text (the framework's default binding; never `innerHTML`, `v-html` or `dangerouslySetInnerHTML`).
- Set a strict Content-Security-Policy.
- Keep the token in memory, not `localStorage`.
- Configure CORS for the front end's exact origin only.

Add `X-Content-Type-Options: nosniff` (SEC-11) as a cheap safeguard for older browsers.

### SEC-11 (low, Later): missing security headers; responses with personal data may be cached

**Verified** (`p10_text.py`). An authenticated `GET /api/v1/items` answered with only `Content-Type`, `Date`, `Server: Kestrel` and `Transfer-Encoding`. There was no `Cache-Control`, so the sign-in response (token) and lists containing staff emails may be kept by browsers or proxies. There was also no `X-Content-Type-Options`.

**Fix.** A small middleware in `Program.cs`:
- `Cache-Control: no-store` on `/api/*`
- `X-Content-Type-Options: nosniff`
- `Referrer-Policy: no-referrer`
- `ConfigureKestrel(o => o.AddServerHeader = false)`

### SEC-12 (low, required in phase 7 docs): a sample admin password that passes the policy is published in the repository. **Fixed.**

**Status: fixed (final check, 2026-11-22).** Both places the re-check named now hold the placeholder `<choose-your-own-strong-password>`, which fails the policy (no digit): `docs/03-architecture.md:490` (committed in `43b3934`) and `src/InventoryTracker.Api/InventoryTracker.Api.http:24`, a comment. Beyond the original finding, every password the `.http` walk-through sends is now read with `{{$dotenv ...}}` from the git-ignored `src/InventoryTracker.Api/.env` (lines 52, 381, 414, 421, 428; #135, in the working tree, not yet committed). `.env.example` lists the names with empty values. The README "First password" section tells the owner to change the first password and remove `ADMIN_PASSWORD`, and `run.ps1` warns while it is still set. The former sample value remains in git history, which may not be rewritten (SEC-21). The original finding is kept below as the record.

**As found (re-checked at `a3c1d66`).** `docs/03-architecture.md:475` (`dotnet user-secrets set ADMIN_PASSWORD "..."` with a policy-passing sample) and `src/InventoryTracker.Api/InventoryTracker.Api.http:21`.

**Risk.** The value meets the policy, so if the owner copies the command as written, the first Manager's password is published in the repository's history. AC-02.5 is technically met (it is a sample, not a real credential), and no real value was found (section 3.5).

**Fix (`technical-writer`).** Use a placeholder that fails the policy and cannot be pasted by mistake (`"<choose-your-own-strong-password>"`). In the README, add: change the first Manager's password after the first sign-in, and remove `ADMIN_PASSWORD` from the environment once the first Manager exists (it is never read again, AC-02.2).

### SEC-13 (low; first point required at deployment): configuration that is easy to get wrong in production

- **`dotnet run` starts in Development (required).** `Properties/launchSettings.json` sets `ASPNETCORE_ENVIRONMENT=Development` for both profiles. If the API is started with `dotnet run`, it runs in Development: the OpenAPI document is anonymous, user-secrets are read and there is no HTTPS redirection. When the environment is not set, it defaults to Production, and there `/openapi/v1.json` answered `401` (verified). Phase 7 should run the published DLL, or `dotnet run --no-launch-profile`, with `ASPNETCORE_ENVIRONMENT=Production`.
- **The token lifetime is not range-checked (Later).** `Jwt__TokenLifetimeHours` (`Common/Configuration/JwtOptions.cs:34`) accepts any whole number; a typo of `8000` gives tokens that last almost a year. Add a 1 to 24 check to `StartupChecks.CheckSettings`.
- **Every environment has the same issuer and audience** (`InventoryTracker`). A token from a test instance is accepted by production if the two share `JWT_SIGNING_KEY`. Generate a separate key per machine (the architecture section 8 command does this) and state it in the README (phase 7 docs).
- **`JWT_SIGNING_KEY` is only checked for length** (`Auth/StartupChecks.cs`, at least 32 characters). A long but guessable phrase would pass. The documented random 48-byte key is good; the README should insist on it (phase 7 docs).
- **`AllowedHosts` is `*`** (`appsettings.json:9`). Set it to the real host name before any non-localhost exposure. The impact is small, because the API builds no absolute URLs.

### SEC-14 (info): writes under many parallel clients

**Measured** (`p12_load.py`, receipts as a Warehouse user, Development instance):

| Parallel writers | Receipts | Throughput | Median | p95 | Max | Errors |
|------------------|----------|-----------|--------|-----|-----|--------|
| 10 | 300 | 61/s | 18 ms | 340 ms | 2.9 s | none |
| 40 | 600 | 78/s | 25 ms | 2.5 s | 5.7 s | none |
| 100 | 600 | 117/s | 153 ms | 1.7 s | 3.9 s | none |

SQLite serialises writers (architecture risk 3). No request failed, but waiting is not first-come-first-served, so a few requests wait seconds. I accept this for v1. The real danger was a request that holds the lock for a long time (SEC-01), and that is now fixed. PostgreSQL (Later) removes the limit.

### SEC-15 (info): the Manager role is fully trusted

These are all by PRD design (decisions #14, #66b, #72), recorded here so the owner decides knowingly:

- A Manager can raise, approve and send a complementary order and complete the order alone (PRD row 6), so no second person has to look at "extra material", which is where decision #4 says waste hides.
- A Manager can reset any other Manager's password without notice, and unlock any account.
- `PUT /users/{own id}/password` needs no current password (verified: 204), so a stolen Manager token can take over the account permanently. The change-password lock-out (#71) protects only the other roles.

Later options: require the current password when a Manager resets their own; notify the other Managers of resets; require two different people to approve a complementary order and send it.

### SEC-16 (info; protect the file in phase 7): personal data

The product stores, per staff member, an email, a password hash, roles, an active flag, a creation time and Identity's lock-out fields. The other Identity columns (phone number, two-factor) are unused and stay empty. Every signed-in user sees the emails of the people on every movement, order and return (`PersonResponse`). This is the audit trail of AC-05.3, and it is the only personal data in the product. Users are never deleted (PRD section 5), so there is no erasure path. If a former employee asks for erasure, the only option would be replacing their email with a pseudonym (Later; needs owner and product decision). The SQLite file is not encrypted. It holds the password hashes, so the file and its backups need the same protection as the server (phase 7).

### SEC-17 (info): small items

- `.gitignore` covers `.env`, `.env.*` (except `.env.example`), `*.db`, `*.db-shm`, `*.db-wal` and `*.sqlite`, but not `*.db-journal` (still so at `a3c1d66`). SQLite creates that file briefly during the first migration, before WAL mode is switched on. Add it. **Fixed in `43b3934` (#133):** `.gitignore:493`. Verified with `git check-ignore` (section 6.9).
- Duplicate JSON keys are accepted, and the last one wins (`{"name":"dupA","name":"dupB"}` gave 201). This is harmless here and noted only for completeness.
- Users cannot end their own session (there is no sign-out). Changing one's password ends every session at once (stamp check). This is acceptable with 8-hour tokens.
- The `stamp` claim exposes the user's Identity security stamp to its holder. No token providers are registered (no email or reset tokens), so it cannot be misused. Keep it that way, or switch to a separate session id, if reset-by-email is ever added.

### SEC-18 (info, new in the re-check): the `pending-return` 409 lists every Pending return, without a cap

**Where.** `Features/ProductionOrders/SendOrderService.cs:199-216` writes one sentence per Pending return of the order's materials and joins them all into `detail`.

**Why it is only info.** The 500-line cap does not bound this list. It is bounded by how many Pending returns exist, and only a Manager can create them (`ReturnsController`, `[Authorize(Roles = Manager)]`), one request each, each limited by free leftover (AC-22.2). Each sentence is about 100 to 200 bytes, so 10,000 Pending returns would make a 1 to 2 MB answer. That takes 10,000 deliberate requests by the fully trusted role (SEC-15). The list is read with one query inside the write lock. I did not measure this case. **Later:** list the first few returns and add "and N more", like `ValidationErrors`.

### SEC-19 (low; new in the final check): CI actions are pinned to movable tags, not commit SHAs. **Fixed.**

**Status: fixed (follow-up, 2026-11-22, working tree, not yet committed; diff reviewed, CI not run).** `ci.yml:41` now reads `actions/checkout@fbc6f3992d24b796d5a048ff273f7fcc4a7b6c09 # v5.1.0` and `:49` reads `actions/setup-dotnet@26b0ec14cb23fa6904739307f278c14f94c95bf1 # v5.4.0`. Both are full 40-character SHAs, explained in a comment at `:37-38`. The new `.github/dependabot.yml` watches `github-actions` only, monthly, with at most 5 pull requests and no automatic merge. A Dependabot pull request runs the same read-only, secret-free CI. `docs/07-release.md` section 4 describes the pins. I did not check the SHAs against GitHub myself: I make no outside lookups. `devops-engineer` reports checking them against the official repositories. A SHA that does not exist makes the job fail rather than run other code. The first CI run will confirm that both resolve. The original finding is kept below.

**Where.** `.github/workflows/ci.yml:39` `actions/checkout@v5` and `:47` `actions/setup-dotnet@v5`.

**Risk.** A major-version tag can be moved to different code by whoever controls the action's repository. If either action were compromised, the next CI run would execute the new code with the job's token and the checked-out source of this private repository. The blast radius is small, and that is why this is low. Both actions are GitHub's own. The token is `contents: read` only (`:16-17`). `persist-credentials: false` (`:42`) keeps the token out of the checkout. The job has no secrets and no cache. The impact would be a copy of the source leaving GitHub, not a change to the repository.

**Fix (`devops-engineer`, Later).** Pin each action to its full commit SHA, with the version in a comment (`uses: actions/checkout@<40-hex-sha> # v5.x.y`). Add a Dependabot `github-actions` entry so the pins are kept current.

### SEC-20 (low; new in the final check): the cloud guide's `az` commands would print secrets to the terminal. **Fixed.**

**Status: fixed in the guide (follow-up, 2026-11-22, working tree, not yet committed; diff reviewed, nothing run against Azure).**
- **Printed secrets.** Every `az` command whose result could contain a secret now ends in `-o none`: `flexible-server create` and `db create` (step 3), `firewall-rule create` (step 4b), `secret set` (step 7, inside `Set-VaultSecret`), `appsettings set` and `appsettings delete`, `secret delete` and `flexible-server restore`. The remaining commands without `-o` print no secret: login, resource group, plan, `webapp create`, role assignments, deploy, logs, restart and teardown. The commands that read values use `--query ... -o tsv`.
- **`az.cmd` quoting and the process list.** All three vault secrets now go through `Set-VaultSecret`. It writes the value with `[IO.File]::WriteAllText` (UTF-8, no byte-order mark, no newline) to a randomly named file in the user's `%TEMP%`, calls `az keyvault secret set --file ... --encoding utf-8 -o none`, throws if `az` failed, and deletes the file in `finally`. No secret is passed as `--value` any more. The guide says to keep any future `--value` to letters, digits and `-`. The PostgreSQL admin password is still passed as `--admin-password` on the command line. It is letters and digits only, so it cannot be mangled. It is visible to other programs of the same user only while that one command runs. I accept that.
- **Database exposure.** Step 3 now uses `--public-access None` (public endpoint, no firewall rule). The new step 4b opens it only to the Web App's `possibleOutboundIpAddresses`, one rule each, and says these addresses are shared with other App Service customers in the same deployment unit. `0.0.0.0` is explicitly discouraged, with the reason ("any resource in Azure, including other customers'"). Private access is described as the strongest option and marked **[COSTS MONEY]**. Restores repeat step 4b for the new server. A temporary rule for the owner's own address is to be deleted straight after use.
- Still optional for the re-review: turning off basic-authentication publishing credentials on the Web App.

The original finding is kept below.

**Where.** `docs/07a-cloud-guide.md` step 3 (`:85-90`, `az postgres flexible-server create ... --admin-password $pgPassword`) and step 7 (`:143`, `:148`, `:152`, `az keyvault secret set ... --value ...`).

**Risk.** **Not run** (no Azure account, by design). This rests on the Azure CLI's documented default output, not on a demonstration. `az keyvault secret set` returns the stored secret, `value` included, as JSON on the screen. `az postgres flexible-server create` prints the admin user, password and a connection string. Followed as written, the steps would show the signing key, the first Manager's password and the database password in the terminal and its scrollback. The guide's own care elsewhere (`Read-Host`, `Remove-Variable`, "make sure nobody is watching") shows this is not intended. Two smaller points:
- `az` on Windows runs through `az.cmd`. The guide already warns (`:166`) that it mangles `;` and parentheses. An admin password containing `&`, `|`, `^`, `%`, `<` or `>` passed as `--value` may be cut short or partly read as a command.
- `--public-access 0.0.0.0` (`:90`, explained at `:95`) admits connections from any Azure-hosted resource, including other customers'. "Azure services only" is Microsoft's wording, but it reads as narrower than it is. The random database password and enforced TLS still protect the server.

**Fix (`devops-engineer`, `technical-writer`, before anyone follows the cloud guide; not blocking for localhost v1).** Add `-o none` (or `--query id -o tsv`) to each `az keyvault secret set` and to `az postgres flexible-server create`. Pass the admin password with `--file` from a temporary file that is deleted at once, or limit it to letters, digits and `-`. Explain that `0.0.0.0` means all of Azure, and recommend private access, or a firewall rule for the Web App's outbound addresses, as the default. Optional hardening for the re-review: turn off basic-authentication publishing credentials (SCM and FTP) on the Web App. The guide already limits deployment to `az webapp deploy`.

**The rest of the cloud guide is sound.** Section 1 lists the blocking product changes: PostgreSQL first (item 1), HSTS plus HTTPS-only (item 2, step 6), a rate limit (item 3), forwarded headers (item 4), one instance (item 5) and re-opening this review (item 6). Secrets go in Key Vault through Key Vault references and a managed identity, never in plain App Service settings or files (steps 7 and 8). Every chargeable step is marked **[COSTS MONEY]**, account steps are marked **[ACCOUNT]**, and the guide adds a budget alert and a teardown section. At the re-review, check that `ASPNETCORE_FORWARDEDHEADERS_ENABLED` (`:171`) is acceptable: it trusts forwarded headers from any address, which is safe only because App Service's front end is the only route in, and only if the rate limiter uses the address that front end appended.

### SEC-21 (info, Later; new in the final check): policy-passing password literals left in documents, tests and git history

None of these is a real credential. None is in anything the owner is told to run against a real instance: the README, release guide, user guide, cloud guide, scripts, `.env.example` and the `.http` file contain placeholders, `Read-Host` prompts or `$dotenv` reads only (scan in section 6.9). My judgement on each:

| Where | Literals | Judgement |
|-------|----------|-----------|
| `docs/02-design.md:367`, `:387`, `:406`, `:410` | `Cheddar2026`, `Gouda-Wheel-77`, `Initial2026`, `Reset-2026a` | **Later** (`ux-designer`). These are API reference examples, not steps to run. "Initial2026" and "Reset-2026a" are realistic choices for a Manager's initial and reset passwords, and a future front end may copy them into fixtures. Replace them with values that fail the policy (`"<initial-password>"`) at the next design edit. |
| `docs/05-test-plan.md:224`, `docs/bugs/BUG-001-text-limits-count-code-units.md:50` | `Abcdefg1`, an emoji password | **Keep.** They are records of what QA sent to a scratch instance. Changing them would falsify the record. |
| This report, section 3.1 | `Password1`, `Qwerty123` | **Keep.** They show that common passwords pass the PRD's policy, which is the point being made. |
| This report, former SEC-12 text and condition 4 | the former architecture sample | **Removed** from this report in the final check. The value is named only as "the former sample". |
| `tests/` (for example `Initial2026` 23 times, `Reset-2026a` 6 times, `Admin-Test-2026`) | test fixtures | **Keep.** They are used only by `TestApiFactory`'s in-memory instances with temporary SQLite files and the test-only signing key, which is labelled as such. |
| git history (pushed) | the former architecture sample; the former `.http` values `Initial2026`, `Reset-2026a`, `Gouda-Wheel-77` | **Accept.** The owner's rules forbid rewriting pushed history. Treat these values as public. If the old walk-through was ever run against a database that is kept, change or deactivate the `warehouse.user@example.com` account there. |

The README said (`README.md:117`): "Where this repository shows a password, it is a placeholder". That was true of everything a person runs, but not of the design document or the tests. **Fixed (follow-up, working tree):** it now reads "Every password in a command or sample request here is a placeholder such as `<choose-your-own-strong-password>`. Example values in the design document and the tests are for throwaway databases only; never use them for a real account." That is accurate.

### SEC-22 (low, Later; new in the signing-key re-check): the API accepts any 32 characters as the signing key; only `run.ps1` refuses captured text

**Where.** `src/InventoryTracker.Api/Auth/StartupChecks.cs:126` checks only `key.Length < 32`. The new no-whitespace rule is in `scripts/common.ps1` (`Get-SigningKeyProblem`) only. This sharpens the fourth point of SEC-13.

**What I verified** (stub, section 6.12). The value the old unsafe command saved when run from the wrong folder is 81 characters and starts "Windows PowerShell". `Get-SigningKeyProblem` now refuses it. The API would still start with it whenever it is not started through `run.ps1`, for example by running `publish\win-x64\InventoryTracker.Api.exe` directly or on a cloud host. So would `aaaa...` (32 times) and `MyInventoryTrackerSecretKey2026!`; the script check accepts both of those too. Anyone who knows the value can mint a Manager token (section 3.1).

**Is a Base64 check better?** Partly. No format check can prove a value is random, so generating the key with the script remains the real control. A *strict* "Base64 of at least 48 bytes" rule would refuse the tests' key (`TestApiFactory.cs:46` contains `-` and is 72 characters long) and hex keys of other lengths. A rule that keeps every key made so far:
- only the characters `A-Z a-z 0-9 + / = - _` (Base64 and Base64url): this refuses spaces, the banner's `(`, `)` and `.`, and punctuated phrases;
- at least 43 characters (32 bytes of Base64);
- at least 10 different characters: this refuses `aaaa...` and similar repeats.

Every key `New-SigningKey` made in my stub run would pass: 1,000 of 1,000 were 64 characters with 33 to 46 different characters. So would the architecture section 8 command (the same 48-byte Base64), the cloud guide's `New-RandomSecret`, a 64-character hex key and the test key. A long phrase written as one word without punctuation would still pass. That gap is accepted.

**Fix (`backend-developer`, Later).** Put that rule in `StartupChecks.CheckSettings`, using the existing too-short message style, and keep `Get-SigningKeyProblem` in step with it, so every start path is protected and not only `run.ps1`. Add tests for the banner, the repeated letter and a generated key.

### SEC-23 (low, doc wording; new in the signing-key re-check): the user-variable advice understates who receives the key

**Where.** `README.md:183` says "User variables are readable by programs running as you". `docs/07-release.md:90` says "... which is the same protection the database has".

**What is missing.** Both statements are true as far as they go: the value sits unencrypted in your profile's registry hive (`HKCU\Environment`), which you, administrators and SYSTEM can read, like the database file. But a user variable is also **copied into the environment of every program you start from then on**: browsers, editors and their extensions, build tools, package scripts. Some programs write their environment into crash reports, debug logs or support bundles, and `Get-ChildItem Env:` or `set` show it on screen during a screen share. A database file is read only by a program that chooses to open it, so for accidental exposure this is wider than "the same protection". On a domain with roaming profiles, the value also travels with the profile.

**Fix (`technical-writer`; cheap, can be done now, not blocking).** Add one sentence to both places, for example: "Windows also hands it to every program you start, so never run a tool that uploads its environment (crash reporters, `set` in a shared screen) while it is set, and replace the key (`-SaveForUser -Force`) if it may have been exposed." Optional, Later: a file readable only by your user, or Windows' protected storage (DPAPI), passed only to the API process.

## 3. Checked and found sound

### 3.1 Authentication and sessions

- **Algorithm and key** (`Auth/AuthSetup.cs:121-152`). `ValidAlgorithms = [HS256]`, issuer, audience and key are all validated. Expiry is required, with no clock skew, on the app's clock. Verified with `p1_jwt.py`: each of these 16 forged or altered tokens got `401 not-authenticated` with `WWW-Authenticate: Bearer` and no `error_description`:
  - `alg: none`, with and without the original signature
  - HS256 with a wrong key
  - a payload with a role added but the old signature
  - HS384 and HS512 with the right key
  - right key, but no `exp`, an `exp` in the past, an `nbf` in the future, no `stamp`, a wrong `stamp`, a wrong issuer, a wrong audience or an unknown `sub`
  - garbage, and an empty token

  A real token had a lifetime of exactly 8.0 h. A token minted with the right key and a 10-year `exp` was accepted: the key is the root of trust, so keep it secret (SEC-13).
- **Stamp check on every request** (`Auth/TokenUserCheck.cs:22-47`). Verified: a wrong stamp gets 401. After deactivation, the old token got 401, and it still got 401 after reactivation (`p9_demote.py`).
- **Lock-out counts** (decisions #61, #71). Verified: 40 wrong passwords in parallel for one account gave exactly 5 `invalid-credentials` and 35 `account-locked`, and the right password was then refused (`p2_signin.py`). No failures were lost.
- **T-003 timing leak** (decision #62). Verified with 160 interleaved pairs (`p3_timing.py`): unknown email median 76.7 ms, real email with a wrong password 78.4 ms. The ranges overlap almost completely (p10 59.5 vs 62.1, p90 99.7 vs 100.2). The remaining difference of about 2 ms is the failure-count write, smaller than ordinary network jitter. The lock-out message is a much stronger existence check (SEC-04), so nothing more is needed here.
- **Password storage and policy.**
  - Identity's PBKDF2 hasher (salted, HMAC-SHA512, 100,000 rounds). `AuthTests` `Passwords_are_stored_only_as_salted_hashes` passes.
  - The code-point length rule (#118): "Aa1🧀🧀🧀" was refused and `abcdefg1` was refused. Verified.
  - Before the fix, passwords of 1, 10 and 25 MB took 93 to 229 ms. The 1 MB body limit now bounds them.
  - Common passwords that meet the policy (`Password1`, `Qwerty123`) are accepted. That is the PRD's policy; checking against a list of breached passwords goes on the Later list.
- **Start-up admin** (`Auth/StartupChecks.cs`). Created only when there are no users, with the same password validators as `POST /users`, and with the Manager role only. The log line names the user id, not the email. Verified on all four instances (both reviews).
- **Token handling.** Tokens appear only in the sign-in response. They are never logged and never stored server-side.

### 3.2 Authorisation and IDOR

- All 48 endpoints' `[Authorize]` attributes match PRD 4.2 (I read every route), and `RoleMatrixTests` passes.
- Live spot-check (`p8_roles.py`): 44 calls by the wrong role (Warehouse, Production, and Warehouse plus Production) across adjustments, orders, cancel, approve, returns, notifications, users, recipes, items and units all got `403`. Anonymous `POST /users` got `401` (AC-01.6).
- **401 and 403 still come before any body work** after the fix (re-check, section 6.3).
- **User ids come from the token, never the body.** Every service that records a user gets it from the controller's `User.FindFirstValue("sub")`. No request record has a user-id field. `change-password` with a victim's id in the body was ignored: the wrong guess counted against the caller, and the victim still signed in with their own password.
- **IDOR.** Read access to orders, movements, returns and stock is open to every signed-in role by PRD 4.2, and that is all those endpoints allow. Notification read status is the caller's own (`NotificationService.cs`, user id from the token). User records are Manager-only. No endpoint lets one role read or change something PRD 4.2 keeps from it.

### 3.3 Input handling

- **SQL.** All product queries are EF Core LINQ. The one raw statement in product code, `NotificationService.cs`, uses `ExecuteSqlAsync` with an interpolated `FormattableString`, so `{userId}` and `{now}` become parameters. `DatabaseSetup.cs` runs a constant `PRAGMA`. Live: the unit name `x'); DROP TABLE Units;--` and an adjustment location of `Warehouse'; DROP TABLE Movements;--` were treated as plain data; the tables were intact.
- **JSON.** `NumberHandling = Strict`. Enums are parsed by `EnumParser` (names only, any letter case). Verified: `type: 0` and `"0"` are refused; so are `" RawMaterial"`, `1e30`, `NaN` and nesting 5,000 deep (400 each).
- **Sizes (re-check).** Bodies are limited to 1 MB, line lists to 500, role lists to 10, and `400` messages to 100 (section 6.1). Lengths are counted in code points (#116). Query strings are limited by Kestrel's 8 KB request line (`414` beyond it, verified).

### 3.4 Errors and leakage

- `500` bodies are the generic ProblemDetails in both Development and Production, with no stack trace, SQL or type name (verified). The `traceId` is the W3C trace id: it reveals nothing and lets a support request be matched to the log.
- **Bodies the web server refuses** now answer `413`, `408` or `400` ProblemDetails with the design 7.2 wording. Their log line holds the status, method, path and Kestrel's reason, and no user data (section 6.2).
- **Logs.** After all the first-review probes, the two logs contained **0** occurrences of any password I used, the signing key, any token (`eyJ`) and any email. They contain only user ids. In the re-check, the Development log (31,220 lines) and the Production log contained **0** `fail` entries, **0** stack-trace lines, and **0** occurrences of the throwaway passwords, the signing key, any token (`eyJ`) or any email.
- `/health` returns only `Healthy` or `Unhealthy`. `/openapi/v1.json` returns 200 in Development and **401 in Production** (verified).

### 3.5 Secrets and configuration

- **Tracked files.** `.env.example` has names only. `appsettings*.json` and `launchSettings.json` have no secret, and never had one in any commit. The test-only signing key and passwords in `tests/` are labelled as such and used only by test factories.
- **History.** I dumped `git log --all -p` (38 commits at the first review) and searched for `KEY=`, `PASSWORD=`, `Password=` in connection strings, `SigningKey = "`, long Base64 strings and real email addresses. I also ran `git grep` across every revision for AWS, GitHub, Slack and OpenAI key patterns and PEM private keys. I found nothing apart from the documented samples (SEC-12) and test-only values. In the re-check I read the five commits since `60ba6e4`. They add no secrets, and no `.db`, `.env` or certificate file.
- `.gitignore` ignores `src/InventoryTracker.Api/.env` and `inventory.db`. User-secrets live in `%APPDATA%`, outside the repository. `git status --short` was empty after the re-check.
- **Final check.** The two new commits (`43b3934`, `8dbdbe7`), the working-tree diff and the two new documents contain no long Base64 string, private key, cloud or GitHub token pattern, `AccountKey=` or SAS signature (grep of `git log -p 7590787..HEAD`, `git diff` and the new files). No `.db`, `.env`, `.pfx` or certificate file was added. `appsettings.Production.json` holds only a log level. `git status --ignored` shows `publish/` and build output only, with no `.env` and no database.

### 3.6 Dependencies

- **Re-check, `a3c1d66`:** `dotnet list package --vulnerable --include-transitive`: "The given project `InventoryTracker.Api` has no vulnerable packages given the current sources." The same holds for `InventoryTracker.Api.Tests`.
- First review: the transitive packages include Microsoft.IdentityModel 8.19.2, System.IdentityModel.Tokens.Jwt 8.19.2, Microsoft.Data.Sqlite 10.0.15 and SQLitePCLRaw 2.1.13. `dotnet list package --outdated`: all top-level packages are 10.0.15, and 11.0.0 is offered. That is a new major, short-term-support version; stay on 10 (decision #19). The fix commit added no package.
- Runtime: still 10.0.12, see SEC-06.
- **Final check, `8dbdbe7`:** `dotnet list InventoryTracker.slnx package --vulnerable --include-transitive`: "no vulnerable packages given the current sources" for both projects. No package was added in phase 7. `dotnet sdk check`: SDK 10.0.401 (10.0.404 available); runtimes 10.0.12 (10.0.15 available) and 8.0.18 (out of support, unused). The published folder bundles 10.0.15 (SEC-06).

### 3.7 Concurrency and stock integrity under abuse

Tested with `p11_race.py`, by authenticated but hostile clients. An item had 15 kg in the Warehouse, with two Created orders each needing 9.8 to 10.2 kg. In one burst: 25 sends of order A and 25 of order B (Warehouse user), plus 20 adjustments of −1 kg (Manager), 70 writes in 1.8 s. The results:

- exactly **1** send succeeded; the others got 24 `already-sent` and 25 `another-order-in-production`
- 4 adjustments succeeded and 16 got `insufficient-stock`
- final stock: Warehouse **0.8**, production storage 10.2 (15 − 10.2 − 4 = 0.8), never below zero
- exactly one order in production

Then 30 parallel completions of that order: **1** succeeded and 29 got `already-completed`. The order's movements were exactly one each of SentToProduction, ConsumedInProduction and ProducedOutput. The invariants hold. The only indirect route, stalling writes (SEC-01), is now closed.

## 4. Summary table

Status as of the final check (2026-11-22, `8dbdbe7` plus the working tree).

| ID | Severity | Finding | Status at the final check | Before release? | Owner of the remaining work |
|----|----------|---------|---------------------------|-----------------|-----------------------------|
| SEC-01 | medium | Unlimited request lines validated inside the write lock | **fixed** (`0961b35`, #122, #123, #125), re-verified | done | none |
| SEC-02 | medium | Oversized or broken bodies gave `500` and Error stack traces | **fixed** (`0961b35`, #124); residual log line **dropped in Production** (#130, verified) | done | none |
| SEC-03 | medium | Plain HTTP in Production, no HSTS | **accepted for localhost-only v1** (#119); loopback binding **verified** (section 6.7) | done for v1; HTTPS + HSTS before any other exposure | backend-developer, devops-engineer (at exposure) |
| SEC-04 | medium | Lock-out denial of service; lock-out reveals account existence | **accepted** (#120); README "Keep two active Managers" | done | none for v1 |
| SEC-05 | low | No rate limit on sign-in | **accepted for v1** (#120), on PRD Later list | before any non-localhost exposure | backend-developer (later) |
| SEC-06 | low | Runtime 10.0.12, patch 10.0.15 available | **mitigated**: published folder bundles 10.0.15 (#129, verified); installed runtime still 10.0.12 | release build: done; installed runtime: **owner's task** | owner |
| SEC-07 | low | Removing a role leaves 8-hour sessions with the old rights | **accepted** (#121); README runbook | done | none |
| SEC-08 | low | User-management logs lack the acting Manager; failed sign-ins and lock-outs not logged | open | Later | backend-developer |
| SEC-09 | low | Control, invisible, bidi and look-alike characters accepted in names | open | Later | ux-designer, backend-developer |
| SEC-10 | low | Raw HTML in stored text: a front-end XSS risk | open | front-end requirement | product-manager, future frontend-developer |
| SEC-11 | low | No `Cache-Control`, `nosniff`; `Server` header | open | Later | backend-developer |
| SEC-12 | low | Policy-passing sample admin password in docs and `.http` | **fixed** (placeholder in `03-architecture.md:490` and `.http:24`; walk-through passwords via `$dotenv`, #135) | done (commit the `.http` change) | delivery manager (commit) |
| SEC-13 | low | `dotnet run` gives Development; token lifetime unchecked; shared issuer/audience; key strength; `AllowedHosts *` | start in Production **met** (verified); key advice **met** (README); the rest open | the rest Later | backend-developer |
| SEC-14 | info | Parallel-writer tail latency (no errors) | accepted | no | none |
| SEC-15 | info | Manager is fully trusted | accepted (PRD design) | no | owner (awareness) |
| SEC-16 | info | Personal data: emails visible to all roles; no erasure path; unencrypted file | file and backups protection **met** (release guide 7, README "Data", backup script) | done | none for v1 |
| SEC-17 | info | `*.db-journal` not ignored; duplicate JSON keys; no sign-out; stamp claim | `.gitignore` line **fixed** (#133); the rest accepted | no | none |
| SEC-18 | info | `pending-return` 409 lists every Pending return, uncapped (Manager-created data) | open | no (Later) | backend-developer |
| SEC-19 | low | CI actions pinned to movable tags, not SHAs | **fixed** (follow-up: SHA pins, Dependabot; diff reviewed, CI not yet run) | done (commit) | delivery manager (commit) |
| SEC-20 | low | Cloud guide's `az` commands would print secrets; `az.cmd` quoting; `0.0.0.0` wording | **fixed** in the guide (follow-up: `-o none`, `--file` secrets, `--public-access None` + step 4b; diff reviewed, not run) | done (commit); the cloud re-review still applies | delivery manager (commit) |
| SEC-21 | info | Policy-passing literals in the design doc, QA records, tests and git history | judged in the finding; README wording **fixed** (follow-up) | no (design examples: Later) | ux-designer |
| SEC-22 | low | API accepts any 32 characters as the key; only `run.ps1` refuses text with spaces | **new**, open | no (Later) | backend-developer |
| SEC-23 | low | User-variable advice omits that every started program receives the key | **new**, open | no (cheap doc fix, can be done now) | technical-writer |

No critical or high findings, and no open medium finding without an owner decision.

## 5. What QA routed to this phase, answered

- **Lock-out by guessing (AC-01.4):** a real denial of service; SEC-04, accepted by the owner for v1 (#120).
- **No sign-in rate limit, about 45 ms of CPU each:** SEC-05, measured; Later (#120).
- **Timing signal and lost failure counts (T-003):** both still fixed (section 3.1).
- **TAB and NUL in names:** accepted. Low risk for the API itself; it matters for look-alike items and for the PostgreSQL port (SEC-09).
- **HTML-like text returned unescaped in `application/json`:** correct for a JSON API and not exploitable in this API. It must be handled by the future front end (SEC-10).
- **Writes under many parallel clients:** measured, with no errors and seconds of tail latency (SEC-14). The real danger was SEC-01, now fixed.

## 6. Re-checks

Sections 6.1 to 6.5 are the re-check of the SEC-01 and SEC-02 fixes at `a3c1d66`. Sections 6.6 to 6.10 are the final check of the phase 7 release, 6.11 its follow-up, and 6.12 the signing-key re-check.

### 6.1 SEC-01: fixed

**The original attack, same bodies** (`r1_sec01.py` section 1, Development instance, Warehouse token, send on a Created order). Another user recorded receipts throughout; a receipt alone took 14.5 ms (median of 10).

| Body | Before the fix | After the fix | Worst receipt meanwhile, before → after |
|------|----------------|---------------|------------------------------------------|
| 100,000 null lines (0.5 MB) | 400 in 0.24 s, 13.4 MB answer | **400 `too-many-lines`** in 77 ms, 0.3 KB | 27 → 33 ms |
| 1,000,000 null lines (5 MB) | 400 in 3.6 s, 135.8 MB | **413 `request-too-large`** in 6 ms, 0.3 KB | 2,302 → 27 ms |
| 3,000,000 null lines (15 MB) | 400 in 9.1 s, 411.8 MB | **413 `request-too-large`** in 1 ms, 0.3 KB | 7,250 → 29 ms |

After every SEC-01 probe, the API process held **about 210 MB** (`tasklist`: 209,748 K), against 3.1 GB before.

**Every line-carrying endpoint** (`r1_sec01.py` section 2). 500 null lines is the largest list the cap allows; 501 is one over it.

| Endpoint (role) | 500 null lines | 501 null lines |
|-----------------|----------------|----------------|
| `PUT /items/{id}/recipe` (Manager) | 400 `validation`, **100** messages in `errors`, 7.6 KB, `detail` ends "900 more problems are not shown." | 400 `too-many-lines`, 0.3 KB, 1 ms |
| `POST /production-orders/{n}/send` (Warehouse) | 400 `validation`, 100 messages, 6.6 KB, "901 more problems are not shown." | 400 `too-many-lines`, 2 ms |
| `POST /production-orders/{n}/complete` (Production) | 400 `validation`, 100 messages, 6.6 KB, "902 more ..." | 400 `too-many-lines`, 2 ms |
| `POST /production-orders/{n}/complementary-orders` (Production) | 400 `validation`, 100 messages, 7.8 KB, "900 more ..." | 400 `too-many-lines`, 1 ms |
| `POST /complementary-orders/{n}/send` (Warehouse) | 400 `validation`, 100 messages, 6.6 KB, "901 more ..." | 400 `too-many-lines`, 2 ms |
| send on order 99999, which does not exist | 404 `not-found` | 400 `too-many-lines`, before the 404, as decision #123 says |

**Role cap** (`r1_sec01.py` section 3). `PUT /users/{id}/roles` with 10 null roles gave the normal per-entry messages (1.6 KB). With 11 it gave only "At most 10 roles are allowed." (0.3 KB). With 199,990 null roles (1 MB) it gave the same answer in 31 ms. `POST /users` with 11 or 199,990 roles and a bad email answered only under `roles`, as decision #125 says. 11 roles for an unknown user gave 400 before the 404.

**1 MB boundary** (`r1_sec01.py` section 4). A body of exactly 1,000,000 bytes was read (400 `validation`). A body of 1,000,001 bytes got **413**, "The request body is too large. The limit is 1 MB."

**The write lock is no longer held by bad bodies** (`r1_sec01.py` section 5). Twenty 500-line bad sends in a row: median 3 ms. Twenty 199,990-line (1 MB) bad sends: median 18 ms, max 39 ms. During each run, the other user's worst receipt took **31 ms and 30 ms**. In the Production instance, with SQL logging on, a 501-line recipe ran only the token's user lookup, no query on the recipe or items (`r3_nodbwork.py`). So the cap is checked before the transaction.

### 6.2 SEC-02: fixed

`r2_sec02.py` (Development) and `r3_nodbwork.py` (Production), all anonymous, all against `POST /api/v1/auth/sign-in`:

| Request | Before the fix | After the fix |
|---------|----------------|---------------|
| Header `Content-Length: 31000000`, 2 bytes sent (50 times) | `500 internal-error`, Error + 27-line stack trace each | **`413 request-too-large`**, title "Request too large", "The request body is too large. The limit is 1 MB."; 50 took 0.04 s |
| Malformed chunked body (`ZZ`) | `500` | **`400 validation`**, "The request body could not be read.", no `errors` field |
| Body 1 byte every 2 s (`Content-Length: 1000`) | not tested | **`408 request-timeout`** after 5.7 s, "The request body arrived too slowly. Send it again." |
| Body that ends early, then the client half-closes | not tested | connection closed, no answer (the client is gone), no log entry |

**Log flood.** In Development, 8 threads sent 15,531 header-only oversize requests. The log grew 3.08 MB, which is **198 bytes per request**. It held 0 `fail` entries, 0 stack-trace lines, and one Information line per request. (The run lasted 26 s, not 10, because my own client ran out of local ports; the server answered every connection it got, and `/health` answered 200 afterwards.) In Production, 8 threads × 3 s gave 1,827 requests (597/s, no client errors), 362 KB of log (198 bytes per request, **0.118 MB/s**), 0 `fail` entries, 0 stack-trace lines and **0 SQL commands**. Before the fix: 2.6 MB/s and about 3.9 KB per request, at Error. That is about 20 times less log per request, and none of it at Error. What remains is a low residual (section 2, SEC-02 residual).

### 6.3 Point (a): 401 and 403 still come before any body work

Design 2.2 says 401 and 403 come first. `UseAuthentication()` and `UseAuthorization()` run before `MapControllers()` (`Program.cs:111-114`). Model binding, which is what reads the body, runs only inside the endpoint. Verified live on the Production instance with SQL logging on (`r3_nodbwork.py`) and on the Development instance (`r2_sec02.py`):

| Request (bodies really sent unless noted) | Answer | SQL commands run | Body read (`Request body refused` log line) |
|-------------------------------------------|--------|------------------|---------------------------------------------|
| No token, send, 2 MB | 401 | 0 | no |
| No token, send, `Content-Length: 31000000` header only | 401 | 0 | no |
| Garbage token, send, 2 MB | 401 | (Development) | no |
| No token, recipe, 501 lines | 401 | 0 | no |
| Production token, send (Warehouse/Manager only), 2 MB | 403 | 1: the token's user and stamp check on `AspNetUsers` | no |
| Warehouse token, recipe (Manager only), 2 MB / `Content-Length: 31000000` header only / 501 lines | 403 | 1: the same user check | no |
| Warehouse token, `POST /users`, 2 MB; Warehouse token, complete, 501 lines | 403 | (Development) | no |
| Control: Manager token, recipe, 2 MB | 413 | 1: the same user check | **yes**, one line |
| Control: Manager token, recipe, 501 lines | 400 `too-many-lines` | 1: the same user check | n/a |

No request without a valid token or the right role caused a body read, a query beyond authentication itself, or a write transaction. The controls show that the method detects a body read. **Holds.**

### 6.4 Point (b): are the error lists that are not capped bounded?

- **`insufficient-stock` 409** (send `SendOrderService.cs:299-310`, complementary send `ComplementarySendService.cs:267-282`). There is one sentence per short line. An order has exactly one line per recipe line (AC-13.1), a recipe has at most 500, and the line matcher refuses unknown and repeated lines. So the 500-line cap bounds it. I measured the worst realistic case (`r4_409.py`): a Manager created 500 raw materials with 100-character names (the maximum, `ColumnRules.ItemNameLength`), a 50-character unit, a 500-line recipe and an order. A Warehouse send short on every line then gave **409 with 500 sentences: 95.2 KB** with ASCII names and **143.7 KB** with names of "é", in a median of 54 to 73 ms. With 4-byte characters it would be roughly 0.25 MB; that is an estimate, not measured. Setting this up needs a Manager. **Bounded enough.**
- **`pending-return` 409**: not bounded by the line cap. It is bounded only by the number of Pending returns, which only a Manager creates. Recorded as SEC-18 (info, Later).
- **Query-binding 400s** (`InvalidRequestResponse.cs:54-71`, not capped). There is at most one message per typed query key, and `/movements`, the endpoint with the most, has five. Measured (`r5_query.py`): all five wrong gave 5 messages (734 bytes). Each key repeated 80 times (6.5 KB URL) still gave 5 messages (4.5 KB). Past Kestrel's 8 KB request line, the answer is `414` with no body. **Bounded.**
- Every other list-shaped `400` goes through `ValidationErrors` (I checked every `new ValidationException` and `string.Join` in `src`). The rest are single messages.

### 6.5 Point (c): the 1 MB limit for Kestrel and IIS

- `Program.cs:35`: `builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = RequestLimits.MaxBodyBytes);`. Verified live: exactly 1,000,000 bytes is read, and 1,000,001 bytes gets 413.
- `Program.cs:36`: `builder.Services.Configure<IISServerOptions>(iis => iis.MaxRequestBodySize = RequestLimits.MaxBodyBytes);`. I checked this by reading the code only; there is no IIS on this machine to run it under. It covers IIS in-process hosting. Out-of-process hosting forwards to Kestrel, so line 35 applies there. IIS's own request filtering, with a default of 30,000,000 bytes, sits in front and is looser, so the app's 1 MB still governs.
- No endpoint overrides the limit. `RequestSizeLimit`, `DisableRequestSizeLimit` and `IHttpMaxRequestBodySizeFeature` appear nowhere in `src`. Nothing reads `Request.Body` directly.
- Not covered: HTTP.sys (`HttpSysOptions.MaxRequestBodySize`), which the app does not use. If phase 7 chose HTTP.sys, it would need its own line.

**No code problem found in the fix.**

### 6.6 Final check: what was reviewed

Commits `43b3934` (scripts, CI, `07-release`, `07a`, `appsettings.Production.json`, the `.http` placeholder) and `8dbdbe7` (architecture). The uncommitted working tree as of this check: `README.md` (rewritten), `docs/08-user-guide.md` and `docs/09-release-notes.md` (new), the `.http` walk-through passwords moved to `$dotenv` (#135), `.env.example`, and fixes to `docs/07-release.md`, `docs/07a-cloud-guide.md` and `docs/decisions.md`. The verdict covers these files **as they are now**. They must be committed unchanged, or re-checked if they change. **Update:** the delivery manager committed these files in `9e49d42`. The follow-up (section 6.11) is still uncommitted and adds `.github/workflows/ci.yml`, the new `.github/dependabot.yml`, and further changes to `docs/07a-cloud-guide.md`, `docs/07-release.md` section 4 and `README.md:117`.

### 6.7 Scripts

**`run.ps1` binds to loopback only, and the URL and Kestrel variables cannot redirect it.**

- How it works. It always passes `--environment Production --urls http://127.0.0.1:<port>;http://[::1]:<port>` on the command line (`run.ps1:136`). Command-line values are the last configuration source, so they beat `ASPNETCORE_URLS`, `DOTNET_URLS`, `URLS` and `ASPNETCORE_ENVIRONMENT`. Kestrel endpoints configured in settings would beat `--urls`. The script refuses to start if any environment variable matches `^(ASPNETCORE_|DOTNET_)?Kestrel(__|:)`, ignoring case (`:82-85`). The port is validated to 1024 to 65535 (`:45`). `[CmdletBinding()]` refuses unknown parameters, so there is no way to pass another address. Neither `appsettings.json` nor `appsettings.Production.json` has a `Kestrel` or `urls` key, and `Program.cs` configures no listener (`:35` sets only the body limit).
- **Live** (`final-sec\start.ps1`, `logs\run-out.txt`). Started with `ASPNETCORE_URLS=http://127.0.0.1:5202`, `DOTNET_URLS=http://127.0.0.1:5203`, `URLS=http://127.0.0.1:5205`, `ASPNETCORE_ENVIRONMENT=Development` and `DOTNET_ENVIRONMENT=Development`. The script printed `ASPNETCORE_ENVIRONMENT was 'Development'; this script always runs Production.` The process command line was `InventoryTracker.Api.exe --environment Production --urls http://127.0.0.1:5201;http://[::1]:5201`. The log showed `Hosting environment: Production` and `Now listening on` only for `http://127.0.0.1:5201` and `http://[::1]:5201`. `netstat -ano` for the API's process id showed exactly two listeners, `127.0.0.1:5201` and `[::1]:5201`. Ports 5202, 5203 and 5205 did not answer.
- **Live refusals** (`probe.ps1`). `KESTREL__ENDPOINTS__X__URL` (upper case) and `ASPNETCORE_Kestrel__EndpointDefaults__Protocols` each gave "Not started: remove these environment variables, ..." with exit code 1. The developer's own run covers the `DOTNET_`, `:` and lower-case forms (`07-release.md` section 11).
- **Not tested live:** `HTTP_PORTS`/`ASPNETCORE_HTTP_PORTS`. ASP.NET Core uses them only when no `urls` value is set, and `run.ps1` always sets one. If that ever failed, they would bind all interfaces, so I did not try it on the owner's machine. Out of scope: `DOTNET_STARTUP_HOOKS` and hosting-startup variables. They inject code, which needs the same control over the owner's environment as running any program.
- Also verified live on that instance: sign-in `200` (Manager, 8 hours); `/openapi/v1.json` `401`; `GET /api/v1/users` `401` without a token and `200` with one; a 2 MB body `413`.

**`new-signing-key.ps1` uses a cryptographic generator.** `[System.Security.Cryptography.RandomNumberGenerator]::Create()` fills 48 bytes (`:29-36`), giving 64 Base64 characters. `Get-Random` is not called anywhere in `scripts/`; it appears only in the comment explaining why it is not used.

**No script prints a secret.** `new-signing-key.ps1` writes the key to its output by design. Every documented use captures it straight into a variable (README step 3, release guide section 3, cloud guide step 7). `run.ps1` checks `JWT_SIGNING_KEY`, `ADMIN_EMAIL` and `ADMIN_PASSWORD` for presence and length only, and names variables, never values (`:90-95`, `:115-127`). `Invoke-Checked` echoes only `dotnet` build, test and publish arguments. The live run's output (1,949 bytes) contained **0** occurrences of the throwaway key, password or email, **0** `eyJ`, **0** `fail:` and **0** `crit:`. One small item (info, Later): `common.ps1:127` and `:134` repeat the whole `ConnectionStrings__Default` value in an error message. For SQLite that holds no secret. If the scripts are reused for a connection string with a password, print only the path.

**Backup and restore refuse while the API runs.** Live, with the instance running on the same scratch database: `backup-database.ps1` stopped with "The API is running: InventoryTracker.Api.exe (process id 76780)", exit code 1, and created no backup folder. `restore-database.ps1`, given a backup folder that exists, stopped with the same message and moved nothing; the database, `-wal` and `-shm` files were still in place. A second `run.ps1` also refused. The check is by process (`common.ps1:33-51`), plus the file lock (`:139-157`), so the idle-API case the code review found is covered.

**`appsettings.Production.json` does not weaken security logging.** See the SEC-02 residual. It changes one category, `ApiExceptionHandler`, from Information to Warning. That drops only the refused-body line (`ApiExceptionHandler.cs:104`), and its Warning and Error lines stay. The sign-in line (`User ... signed in.`) still appeared in the live Production log.

### 6.8 CI (`.github/workflows/ci.yml`)

| Check | Result |
|-------|--------|
| Least privilege | `permissions: contents: read` at workflow level (`:16-17`); no job widens it. `persist-credentials: false` on checkout (`:42`). |
| No secrets | No `secrets.` reference; the tests set their own throwaway values (`TestApiFactory`). |
| Triggers | `push` and `pull_request` to `main` only (`:9-13`). **No `pull_request_target`**, no `workflow_run`, no `issue_comment`. |
| Untrusted input | No `${{ github.event.* }}` or other user-controlled expression in any `run:` step. The only expressions are `github.ref` and `github.event_name` in `concurrency` (`:22-23`), which are not run as shell. |
| Pinned actions | Final check: major tags `@v5` (**SEC-19**). Follow-up: full commit SHAs with the version in a comment (`:41`, `:49`), plus `.github/dependabot.yml` for `github-actions` (**fixed**). |
| Other | `timeout-minutes: 25`; no cache (so no cache poisoning); no artefact upload; no deployment. |

Not run on GitHub: it runs only after a push.

### 6.9 Secrets in the repository

- **`.gitignore`** (`git check-ignore -v`): `.env` (root and `src/InventoryTracker.Api/.env`), `.env.local` and `inventory.db` are ignored. So are `inventory.db-wal`, `-shm` and `-journal` (`:486-494`), `*.sqlite`, and any database under `publish/` (`:188`). `.env.example` is not ignored, as intended.
- **No real secret** in the two new commits, the working-tree diff or the new documents (section 3.5).
- **Policy-passing literals.** I scanned all 1,921 tracked and new files with a script that finds quoted strings of 8 or more characters with an upper-case letter, a lower-case letter and a digit, on lines mentioning a password, secret or key. I also grepped for unquoted `PASSWORD=`, `_KEY=` and `Password=` assignments. Outside `tests/`, the only hits are those judged in SEC-21: `docs/02-design.md`, the QA records, and this report. The README, `docs/07-release.md`, `07a`, `08`, `09`, `scripts/`, `.env.example` and the `.http` file have none. **Nothing must change before release.** The design examples go on the Later list (SEC-21).

### 6.10 Cloud guide (`docs/07a-cloud-guide.md`)

| Requirement | Where | Result |
|-------------|-------|--------|
| HTTPS-only and HSTS required | Section 1 item 2; step 6 (`--https-only true`, TLS 1.2 minimum, FTPS disabled); HSTS header to be checked after deployment | met |
| Secrets in App Service settings or Key Vault | Steps 7 and 8: Key Vault, RBAC, managed identity, `@Microsoft.KeyVault(...)` references; the settings file holds references only and is deleted; first-Manager secret and settings deleted after first sign-in | met |
| PostgreSQL first | Section 1 item 1; section 2 explains why SQLite is unsafe on App Service | met |
| Re-open the security review | Section 1 item 6; section 12's last line; README and release guide section 9 | met |
| Steps that cost money marked | **[COSTS MONEY]** on steps 3, 4 and 7, the custom domain, Application Insights and restores; **[ACCOUNT]** on account and identity steps; a budget alert (step 1); teardown (section 11) | met |
| Does not lead into an insecure deployment | Section 1 is a "Stop" list of blocking changes before any step goes live; one instance only (step 5); database firewall limited to the app's outbound addresses (step 4b, follow-up) | met; **SEC-20 fixed** in the follow-up |

### 6.11 Follow-up: SEC-19, SEC-20 and the README wording (diff only)

Same day, after the final check. `devops-engineer` changed `ci.yml`, added `.github/dependabot.yml`, and changed `07a-cloud-guide.md`, `07-release.md` section 4 and `README.md:117`. As the delivery manager asked, I reviewed `git diff` and the new file only, with no live run. The details are under SEC-19, SEC-20 and SEC-21. The diff:

- changes nothing in product code, scripts or settings;
- adds no secret: the only new long hex strings are the two action SHAs;
- widens no CI permission and adds no trigger: still `contents: read`, `push` and `pull_request`.

The cloud guide still requires everything in section 6.10. **SEC-19 and SEC-20 are fixed.** The verdict in 7.1 stands.

### 6.12 Re-check: how the signing key is created and checked (uncommitted, on top of `1e820b4`)

**Background.** `code-reviewer` found that the documented key-save command, `[Environment]::SetEnvironmentVariable('JWT_SIGNING_KEY', (powershell ... -File scripts\new-signing-key.ps1), 'User')`, run from any folder but the repository root, saved PowerShell's start-up banner as the permanent key. That value passed the old 32-character rule. The delivery manager reports that the owner's own saved key passed a format check that did not print it.

**Reviewed.** `git diff` of:
- `scripts/new-signing-key.ps1`;
- `scripts/common.ps1` (`New-SigningKey`, `Save-NewSigningKeyForUser`, `Test-SavedUserVariable`, `Set-SavedUserVariable`, `Get-SigningKeyProblem`);
- `scripts/run.ps1`. Not only its header changed: its key check at `:92-98` now calls `Get-SigningKeyProblem`. I reviewed that too;
- `README.md`, `docs/07-release.md`, `docs/07a-cloud-guide.md` and `docs/08-user-guide.md`.

**How I tested it.** By reading the code and running stubs only: `...\scratchpad\final-sec\keystub.ps1` and `capture.ps1`, with output in `logs\keystub-out.txt`. The stubs load `common.ps1`, then replace `Test-SavedUserVariable` and `Set-SavedUserVariable` with stand-ins that use an in-memory table. `-SaveForUser` was never run for real, and the owner's saved `JWT_SIGNING_KEY` was never read, printed or changed.

| Check | Result |
|-------|--------|
| Randomness and length | `New-SigningKey` (`common.ps1:64-80`) fills 48 bytes from `RandomNumberGenerator`, which is 384 bits, and returns Base64. Stub: 1,000 keys, all different, all 64 characters, each decoding to 48 bytes, none with whitespace, 33 to 46 different characters each. **Sound.** |
| `-SaveForUser` path | Stub: with nothing saved, it stored one 64-character key and returned 4 lines, none containing the key. With a key saved and no `-Force`, it refused with a message that did not contain the key, and the saved value was unchanged. With `-Force` it replaced the key, and the output contained neither the old nor the new key. When the store failed, the error message was Windows' own ("... value is too long."), without the value. `new-signing-key.ps1:45-59` prints only those lines or `Not saved: <message>`, and refuses `-Force` without `-SaveForUser`. The save is confirmed without reading the value back (`Test-SavedUserVariable` returns only true or false). **No leak.** |
| Plain mode | Prints the key by design (`new-signing-key.ps1:63`), for one-window tests captured into `$env:`. The documents present it only that way. |
| Error messages | `Get-SigningKeyProblem` never includes the value. Stub: none of 9 cases (missing, saved but missing here, too short, spaces, tab, repeated letter, no-space phrase, hex, generated key) put the value in its message. The new sign-in and change-password snippets (07a step 10, `08-user-guide.md` `Get-ApiMessage`) show only the status and the API's `detail`, or PowerShell's own error when there is no answer. Neither includes the request body or token. |
| Logging | The key goes to `[Environment]::SetEnvironmentVariable` as a function argument. With Windows' optional PowerShell module logging (event 4103, off by default and not set on this machine), parameter values can be recorded. Info only. |
| No-spaces rule | Stub, `capture.ps1`: the documented one-window form, run from `%TEMP%` with standard error hidden, stored an 81-character value starting "Windows PowerShell", with whitespace. `Get-SigningKeyProblem` refused it: "... contains spaces, so it is not a generated key". A tab is refused too (`\s` covers every Unicode white space). The rule plus the 32-character minimum stops the reported failure. It does not stop a repeated letter or a no-space phrase (both accepted in the stub), and it lives only in the script: **SEC-22**, with the stronger rule that keeps existing keys. |
| Storage advice | Honest about other programs running as you, and the database comparison holds for who can *read* it. It does not say that every program you start *receives* it: **SEC-23**. |
| Cloud guide `New-RandomSecret` | Same as `New-SigningKey`: 48 bytes from `RandomNumberGenerator`, Base64. It is defined in the window, so nothing is captured from another program. The signing key is stored through `Set-VaultSecret` (`--file`, `-o none`). The database password keeps letters and digits only, about 60 characters or roughly 350 bits. **Sound.** |
| Unsafe capture form left in the documents | The `SetEnvironmentVariable(..., (powershell ... new-signing-key.ps1), 'User')` form appears only as a "Do not" warning (`README.md:185`, `docs/07-release.md:80`). I grepped every `.md`, `.ps1`, `.http` and `.example` file. The one-window form `$env:JWT_SIGNING_KEY = powershell ... new-signing-key.ps1` remains in `README.md:63` and `:186`, `docs/07-release.md:85`, the script help and `Get-SigningKeyProblem`'s last hint. It is always labelled "quick test, from the repository root", and `run.ps1` now refuses its failure output. The cloud guide no longer captures the script at all. Acceptable. |

**Verdict: approved to commit from a security standpoint.** The change closes the reported weakness for every documented start path, and it adds no path that prints or leaks the key. SEC-22 (Later) and SEC-23 (a cheap wording fix) are low and do not block the commit. The release verdict in 7.1 is unchanged.

## 7. Release recommendation

### 7.1 Verdict

**Final check (2026-11-22): release approved from a security standpoint**, for v1 on localhost only. All nine conditions of section 7.2 are met. The installed-runtime half of condition 2 is recorded as the owner's task. No critical or high finding is open. The two medium findings still open (SEC-03, SEC-04) are owner-accepted for localhost-only v1, and the binding they depend on is verified live (section 6.7). The three new items (SEC-19, SEC-20, SEC-21) are low or info, and none blocks a localhost release.

This approval holds on these terms:

1. **The reviewed files are committed as reviewed** (section 6.6): done in `9e49d42` for the final-check files. The follow-up files (section 6.11) still need committing as reviewed. Conditions 4 to 8 depend on the rewritten README, the `.http` `$dotenv` change (#135) and the release-guide fixes. If any of them changes before the commit, it needs another look.
2. **The owner's own tasks:** install the latest .NET 10 runtime or SDK on this machine, then record `dotnet sdk check` in `docs/07-release.md` section 1. Until then, start the real instance with `run.ps1 -Published` only (SEC-06). After the first start, change the first password and remove `ADMIN_PASSWORD` (README "First password"). Create a second Manager.
3. **SEC-20 is fixed in the guide** (follow-up, section 6.11). Following the cloud guide still requires the product changes and the security re-review of its section 1.

If the API is ever made reachable from another machine, this verdict no longer holds. SEC-03 (HTTPS and HSTS) and SEC-05 (rate limit) then become blocking, SEC-04 must be revisited, and this review must be re-opened. The release guide (section 9), the README ("Localhost only"), the release notes and the cloud guide (section 1) all say so.

**History.** At the re-check (`a3c1d66`), the verdict was "clear to release, on the phase 7 conditions". That replaced the first review's "go-with-conditions".

### 7.2 Release conditions for phase 7

Each condition below was set at the re-check and is marked as found in the final check.

1. **SEC-03, #119: localhost only.** The run scripts and the release guide bind the API to `http://127.0.0.1:<port>` or `http://localhost:<port>` explicitly, never `0.0.0.0`, `*` or `+`. The release guide states that HTTPS and HSTS (and SEC-05's rate limit) are required before the API is reachable from any other machine. (`devops-engineer`, `technical-writer`)
   **Met.** `scripts/run.ps1:87` and `:136` bind `127.0.0.1` and `[::1]` only, and `:82-85` refuses Kestrel endpoint variables. Verified live: netstat showed only the two loopback listeners, and the `ASPNETCORE_URLS`, `DOTNET_URLS` and `URLS` redirections were ignored (section 6.7). The requirements are stated in `docs/07-release.md` (top note, section 9), `README.md` "Localhost only", `docs/09-release-notes.md` "Known issues" and `docs/07a-cloud-guide.md` section 1.
2. **SEC-06: latest .NET 10 runtime.** Install ASP.NET Core runtime 10.0.15 or later (this machine has 10.0.12, re-checked today) on the machine that runs the API, re-run `dotnet sdk check`, and record the result in `docs/07-release.md`. (`devops-engineer`; installing software on the owner's machine is the owner's call)
   **Met for the released build; the installed runtime is the owner's task, clearly stated.** `scripts/publish.ps1` bundles 10.0.15 and refuses anything older (`:49-51`). Verified in `publish\win-x64`: Kestrel 10.0.15, CoreLib `10.0.15-servicing`. `docs/07-release.md` section 1 records `dotnet sdk check` (10.0.12 installed, not yet updated) as the owner's call. `README.md` (Prerequisites) and `docs/09-release-notes.md` (Known issues, Security notes) say the same. Today's `dotnet sdk check` still shows 10.0.12.
3. **SEC-13: start in Production.** Run the published DLL, or `dotnet run --no-launch-profile`, with `ASPNETCORE_ENVIRONMENT=Production`. Do not use the launch profile, which sets Development. (`devops-engineer`)
   **Met.** `scripts/run.ps1:64` and `:136` always pass `--environment Production`; `:151` uses `--no-launch-profile` from source. Verified live: with `ASPNETCORE_ENVIRONMENT=Development` and `DOTNET_ENVIRONMENT=Development` set, the instance logged `Hosting environment: Production`, `/openapi/v1.json` answered `401`, and the scratch `APPDATA` stayed empty, so no user-secrets were read.
4. **SEC-12: no usable sample password.** Replace the former policy-passing sample in `docs/03-architecture.md` and `src/InventoryTracker.Api/InventoryTracker.Api.http` with a placeholder that fails the policy. The README tells the owner to change the first Manager's password after the first sign-in, and to remove `ADMIN_PASSWORD` from the environment and user-secrets once the first Manager exists. (`technical-writer`)
   **Met** (the `.http` part is uncommitted). The placeholder `<choose-your-own-strong-password>` is at `docs/03-architecture.md:490` and `InventoryTracker.Api.http:24`. Every walk-through password is read via `$dotenv` (#135). The steps are in `README.md` "First password" and `docs/07-release.md` section 6, steps 6 and 7, and `run.ps1:125-127` warns while `ADMIN_PASSWORD` is set. The remaining literals are judged in SEC-21: none must change before release.
5. **SEC-04, #120:** the README says to keep at least two active Manager accounts. (`technical-writer`)
   **Met.** `README.md` "Keep two active Managers" (`:119-123`). Also in `docs/07-release.md` section 6, step 8, `docs/08-user-guide.md` section 2, the cloud guide step 10 and the release notes.
6. **SEC-07, #121:** the README runbook says: to remove someone's access now, deactivate them, then reactivate them with the new roles if they should continue. (`technical-writer`)
   **Met.** `README.md` "Runbook: remove someone's access now" (`:125-132`), with examples in `docs/08-user-guide.md` section 13.
7. **SEC-13, signing key:** the README insists on a random key per machine (the architecture section 8 command), never a phrase, and never shared between a test and a real instance. (`technical-writer`)
   **Met.** `README.md` "Signing key" (`:134-148`): `new-signing-key.ps1` (cryptographic generator, verified, section 6.7), one key per machine and instance, never a phrase, never shared, never in a tracked file. The same advice is in `docs/07-release.md` section 3, `.env.example:22-24` and the cloud guide section 8.
8. **SEC-16:** the release guide says the SQLite file and its backups hold password hashes and must be protected like the machine itself. (`technical-writer`, `devops-engineer`)
   **Met.** `docs/07-release.md` section 7, "Protect it"; `README.md` "Data"; `scripts/backup-database.ps1:9-11` and its closing message. The default location is the user's profile (`common.ps1:93-104`), outside the repository and `publish\`, and `publish.ps1:63-68` refuses to publish over a folder holding database files.
9. **SEC-02 residual, #126 (optional for localhost-only):** set `Logging:LogLevel:InventoryTracker.Api.Common.Errors.ApiExceptionHandler` to `Warning` in the release configuration, or rotate the log. This becomes required together with item 1's HTTPS before any non-localhost exposure. (`devops-engineer`)
   **Met.** `src/InventoryTracker.Api/appsettings.Production.json:4` (#130), included in `publish\win-x64` (identical file). Verified live: a 2 MB body gave `413` and no log line.

Everything else belongs on the PRD's Later list: SEC-05, SEC-08 to SEC-11, the rest of SEC-13, SEC-17's remaining points, SEC-18, SEC-21's design examples and SEC-22. SEC-23 is a cheap wording fix that can be done now. SEC-10 is a requirement for the future front end. SEC-19 and SEC-20 are fixed (section 6.11).
