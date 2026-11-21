# Security review: Production & Inventory Tracker API

Phase 6, `security-reviewer`, 2026-11-21, at commit `14c60fb` (main). **Re-check** of the SEC-01 and SEC-02 fixes, and of the owner decisions, the same day at commit `a3c1d66` (main); see section 6.

## Summary

The authentication and authorisation core holds up. Every forged, altered or expired token I tried was refused. The security stamp is checked on every request. The lock-out counts correctly under 40 parallel attempts, and the T-003 timing leak is still closed. All 48 endpoints match PRD 4.2, and no endpoint takes a user id from the request body. All SQL is parameterised. Error bodies and logs reveal nothing secret. No secret is in any tracked file or anywhere in git history. The NuGet audit finds no known vulnerabilities (re-run at `a3c1d66`). Even when flooded by authenticated clients, the stock rules held. The first review found no critical or high findings and four medium ones. **The two code findings are now fixed and re-verified against a live instance.** SEC-01: one request could stall every write and take 3.1 GB of memory. Now the same bodies are refused in 1 to 77 ms with a 0.3 KB answer, other users' writes are not delayed, and the process stayed at about 210 MB. SEC-02: an anonymous flood wrote Error-level stack traces at 2.6 MB/s. Now the same requests get a proper `413`, and each writes one Information line of 198 bytes, with no stack trace. The owner settled the other two mediums for v1: the API runs on localhost only (#119), and the lock-out stays without a rate limit (#120). The 401/403 checks still come before any body is read, and the error responses that are not capped are bounded in size. What remains are deployment and documentation conditions for phase 7 (section 7.2). **Recommendation: clear to release**, on those phase 7 conditions. This replaces the earlier "go-with-conditions".

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

### SEC-12 (low, required in phase 7 docs): a sample admin password that passes the policy is published in the repository

**Still open (re-checked at `a3c1d66`).** `docs/03-architecture.md:475` (`dotnet user-secrets set ADMIN_PASSWORD "Choose-A-Strong1"`) and `src/InventoryTracker.Api/InventoryTracker.Api.http:21`.

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

- `.gitignore` covers `.env`, `.env.*` (except `.env.example`), `*.db`, `*.db-shm`, `*.db-wal` and `*.sqlite`, but not `*.db-journal` (still so at `a3c1d66`). SQLite creates that file briefly during the first migration, before WAL mode is switched on. Add it.
- Duplicate JSON keys are accepted, and the last one wins (`{"name":"dupA","name":"dupB"}` gave 201). This is harmless here and noted only for completeness.
- Users cannot end their own session (there is no sign-out). Changing one's password ends every session at once (stamp check). This is acceptable with 8-hour tokens.
- The `stamp` claim exposes the user's Identity security stamp to its holder. No token providers are registered (no email or reset tokens), so it cannot be misused. Keep it that way, or switch to a separate session id, if reset-by-email is ever added.

### SEC-18 (info, new in the re-check): the `pending-return` 409 lists every Pending return, without a cap

**Where.** `Features/ProductionOrders/SendOrderService.cs:199-216` writes one sentence per Pending return of the order's materials and joins them all into `detail`.

**Why it is only info.** The 500-line cap does not bound this list. It is bounded by how many Pending returns exist, and only a Manager can create them (`ReturnsController`, `[Authorize(Roles = Manager)]`), one request each, each limited by free leftover (AC-22.2). Each sentence is about 100 to 200 bytes, so 10,000 Pending returns would make a 1 to 2 MB answer. That takes 10,000 deliberate requests by the fully trusted role (SEC-15). The list is read with one query inside the write lock. I did not measure this case. **Later:** list the first few returns and add "and N more", like `ValidationErrors`.

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

### 3.6 Dependencies

- **Re-check, `a3c1d66`:** `dotnet list package --vulnerable --include-transitive`: "The given project `InventoryTracker.Api` has no vulnerable packages given the current sources." The same holds for `InventoryTracker.Api.Tests`.
- First review: the transitive packages include Microsoft.IdentityModel 8.19.2, System.IdentityModel.Tokens.Jwt 8.19.2, Microsoft.Data.Sqlite 10.0.15 and SQLitePCLRaw 2.1.13. `dotnet list package --outdated`: all top-level packages are 10.0.15, and 11.0.0 is offered. That is a new major, short-term-support version; stay on 10 (decision #19). The fix commit added no package.
- Runtime: still 10.0.12, see SEC-06.

### 3.7 Concurrency and stock integrity under abuse

Tested with `p11_race.py`, by authenticated but hostile clients. An item had 15 kg in the Warehouse, with two Created orders each needing 9.8 to 10.2 kg. In one burst: 25 sends of order A and 25 of order B (Warehouse user), plus 20 adjustments of −1 kg (Manager), 70 writes in 1.8 s. The results:

- exactly **1** send succeeded; the others got 24 `already-sent` and 25 `another-order-in-production`
- 4 adjustments succeeded and 16 got `insufficient-stock`
- final stock: Warehouse **0.8**, production storage 10.2 (15 − 10.2 − 4 = 0.8), never below zero
- exactly one order in production

Then 30 parallel completions of that order: **1** succeeded and 29 got `already-completed`. The order's movements were exactly one each of SentToProduction, ConsumedInProduction and ProducedOutput. The invariants hold. The only indirect route, stalling writes (SEC-01), is now closed.

## 4. Summary table

| ID | Severity | Finding | Status at `a3c1d66` | Before release? | Owner of the remaining work |
|----|----------|---------|---------------------|-----------------|-----------------------------|
| SEC-01 | medium | Unlimited request lines validated inside the write lock | **fixed** (`0961b35`, #122, #123, #125), re-verified | done | none |
| SEC-02 | medium | Oversized or broken bodies gave `500` and Error stack traces | **fixed** (`0961b35`, #124), re-verified; low residual: one 198-byte Information line per refused body | done; #126 log level before any non-localhost exposure | devops-engineer (optional for localhost) |
| SEC-03 | medium | Plain HTTP in Production, no HSTS | **accepted for localhost-only v1** (#119) | bind to localhost **required**; HTTPS + HSTS before any other exposure | devops-engineer, technical-writer |
| SEC-04 | medium | Lock-out denial of service; lock-out reveals account existence | **accepted** (#120), README: two active Managers | README note **required** | technical-writer |
| SEC-05 | low | No rate limit on sign-in | **accepted for v1** (#120), on PRD Later list | before any non-localhost exposure | backend-developer (later) |
| SEC-06 | low | Runtime 10.0.12, patch 10.0.15 available | open (re-checked) | **required on the deployment machine** | devops-engineer / owner |
| SEC-07 | low | Removing a role leaves 8-hour sessions with the old rights | **accepted** (#121), README runbook | runbook **required** | technical-writer |
| SEC-08 | low | User-management logs lack the acting Manager; failed sign-ins and lock-outs not logged | open | Later | backend-developer |
| SEC-09 | low | Control, invisible, bidi and look-alike characters accepted in names | open | Later | ux-designer, backend-developer |
| SEC-10 | low | Raw HTML in stored text: a front-end XSS risk | open | front-end requirement | product-manager, future frontend-developer |
| SEC-11 | low | No `Cache-Control`, `nosniff`; `Server` header | open | Later | backend-developer |
| SEC-12 | low | Policy-passing sample admin password in docs and `.http` | open (re-checked: `03-architecture.md:475`, `.http:21`) | **required (phase 7 docs)** | technical-writer |
| SEC-13 | low | `dotnet run` gives Development; token lifetime unchecked; shared issuer/audience; key strength; `AllowedHosts *` | open | start in Production **required**; key advice in README **required**; the rest Later | devops-engineer, technical-writer, backend-developer |
| SEC-14 | info | Parallel-writer tail latency (no errors) | accepted | no | none |
| SEC-15 | info | Manager is fully trusted | accepted (PRD design) | no | owner (awareness) |
| SEC-16 | info | Personal data: emails visible to all roles; no erasure path; unencrypted file | open | protect the file and backups **required (phase 7)** | devops-engineer |
| SEC-17 | info | `*.db-journal` not ignored; duplicate JSON keys; no sign-out; stamp claim | open | no | backend-developer (gitignore line) |
| SEC-18 | info | `pending-return` 409 lists every Pending return, uncapped (Manager-created data) | new | no (Later) | backend-developer |

No critical or high findings, and no open medium finding without an owner decision.

## 5. What QA routed to this phase, answered

- **Lock-out by guessing (AC-01.4):** a real denial of service; SEC-04, accepted by the owner for v1 (#120).
- **No sign-in rate limit, about 45 ms of CPU each:** SEC-05, measured; Later (#120).
- **Timing signal and lost failure counts (T-003):** both still fixed (section 3.1).
- **TAB and NUL in names:** accepted. Low risk for the API itself; it matters for look-alike items and for the PostgreSQL port (SEC-09).
- **HTML-like text returned unescaped in `application/json`:** correct for a JSON API and not exploitable in this API. It must be handled by the future front end (SEC-10).
- **Writes under many parallel clients:** measured, with no errors and seconds of tail latency (SEC-14). The real danger was SEC-01, now fixed.

## 6. Re-check of the SEC-01 and SEC-02 fixes (`a3c1d66`)

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

## 7. Release recommendation

### 7.1 Verdict

**Clear to release. Phase 6 can close.** This replaces "go-with-conditions". No critical or high finding is open. Both required code fixes (SEC-01, SEC-02) are done and verified against a live instance, and all 1,721 tests pass. The two remaining medium findings (SEC-03, SEC-04) are owner-accepted for a localhost-only v1 (#119, #120). What remains are deployment and documentation conditions that phase 7 must meet. None of them needs a product-code change.

If the API is ever made reachable from another machine, this verdict no longer holds. SEC-03 (HTTPS and HSTS) and SEC-05 (rate limit) then become blocking, SEC-04 and the SEC-02 residual must be revisited, and this review should be re-opened.

### 7.2 Release conditions for phase 7

1. **SEC-03, #119: localhost only.** The run scripts and the release guide bind the API to `http://127.0.0.1:<port>` or `http://localhost:<port>` explicitly, never `0.0.0.0`, `*` or `+`. The release guide states that HTTPS and HSTS (and SEC-05's rate limit) are required before the API is reachable from any other machine. (`devops-engineer`, `technical-writer`)
2. **SEC-06: latest .NET 10 runtime.** Install ASP.NET Core runtime 10.0.15 or later (this machine has 10.0.12, re-checked today) on the machine that runs the API, re-run `dotnet sdk check`, and record the result in `docs/07-release.md`. (`devops-engineer`; installing software on the owner's machine is the owner's call)
3. **SEC-13: start in Production.** Run the published DLL, or `dotnet run --no-launch-profile`, with `ASPNETCORE_ENVIRONMENT=Production`. Do not use the launch profile, which sets Development. (`devops-engineer`)
4. **SEC-12: no usable sample password.** Replace `Choose-A-Strong1` in `docs/03-architecture.md:475` and `src/InventoryTracker.Api/InventoryTracker.Api.http:21` with a placeholder that fails the policy. The README tells the owner to change the first Manager's password after the first sign-in, and to remove `ADMIN_PASSWORD` from the environment and user-secrets once the first Manager exists. (`technical-writer`)
5. **SEC-04, #120:** the README says to keep at least two active Manager accounts. (`technical-writer`)
6. **SEC-07, #121:** the README runbook says: to remove someone's access now, deactivate them, then reactivate them with the new roles if they should continue. (`technical-writer`)
7. **SEC-13, signing key:** the README insists on a random key per machine (the architecture section 8 command), never a phrase, and never shared between a test and a real instance. (`technical-writer`)
8. **SEC-16:** the release guide says the SQLite file and its backups hold password hashes and must be protected like the machine itself. (`technical-writer`, `devops-engineer`)
9. **SEC-02 residual, #126 (optional for localhost-only):** set `Logging:LogLevel:InventoryTracker.Api.Common.Errors.ApiExceptionHandler` to `Warning` in the release configuration, or rotate the log. This becomes required together with item 1's HTTPS before any non-localhost exposure. (`devops-engineer`)

Everything else (SEC-05, SEC-08 to SEC-11, the rest of SEC-13, SEC-17, SEC-18) belongs on the PRD's Later list. SEC-10 is a requirement for the future front end.
