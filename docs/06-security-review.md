# Security review: Production & Inventory Tracker API

Phase 6, `security-reviewer`, 2026-11-21, at commit `14c60fb` (main).

## Summary

The authentication and authorisation core holds up. Every forged, altered or expired token I tried was refused. The security stamp is checked on every request. The lock-out counts correctly under 40 parallel attempts, and the T-003 timing leak is still closed. All 48 endpoints match PRD 4.2, and no endpoint takes a user id from the request body. All SQL is parameterised. Error bodies and logs reveal nothing secret. No secret is in any tracked file or anywhere in git history, and the NuGet audit finds no known vulnerabilities. Even when flooded by authenticated clients, the stock rules held: stock never went below zero, only one order was in production at a time, and output was recorded once. There are no critical or high findings. There are four medium findings. (1) Any signed-in user, even Warehouse-only, can stall every write and take gigabytes of server memory with one large request. (2) An anonymous client can make the server write about 2.6 MB of Error-level stack traces per second with tiny requests. (3) The API serves plain HTTP in Production. (4) Anyone who knows a staff email can keep that account locked out, including the last Manager's. The first two are small code fixes. The third is a deployment condition. The fourth is an owner decision on AC-01.4 plus a rate limit. **Recommendation: go-with-conditions** (section 6).

## 1. Scope and method

**What the product exposes.** It is an HTTP JSON API (48 endpoints under `/api/v1`, plus anonymous `GET /health` and, in Development only, `GET /openapi/v1.json`). It has three roles and JWT bearer tokens, stores its data in a SQLite file, and runs as a single process. It holds staff emails, salted password hashes, Identity security stamps, and stock, order and audit data. It has no user interface yet; a front end is planned.

**What I did.**

- Read the documents the brief named, then every file in `src/InventoryTracker.Api/Auth`, `Features/Users`, `Common/Errors`, `Common/Validation`, `Common/Paging`, `Data/DatabaseSetup.cs`, `Data/WriteTransaction.cs`, `Program.cs`, the settings files, every controller's route and `[Authorize]` attribute, the notification service, and the order-line validation used by send, complete and the complementary orders.
- Ran the app twice from a build in my scratchpad: once in Development (port 5191) and once in Production (port 5192). Each used its own throwaway SQLite file, throwaway `ADMIN_*` and `JWT_SIGNING_KEY` values, and `APPDATA` pointed at the scratchpad, so the owner's user-secrets were never loaded. Nothing touched `src/InventoryTracker.Api/inventory.db`. I stopped both instances afterwards.
- Probed both instances with small Python scripts, kept in `C:\Users\erick\AppData\Local\Temp\claude\0422f218cc2700ed\3fa433b3-adfa-4ff0-b327-6d52555ccac1\scratchpad\probes\` (`p1_jwt.py` to `p15_logflood.py`; `api.py` is the shared helper). Every number in this document comes from those runs on this machine (16 logical processors, Windows 11).
- Searched all 38 commits of git history for secrets (section 4.5).
- Ran `dotnet list package --vulnerable --include-transitive`, `dotnet list package --outdated` and `dotnet sdk check`.
- Re-ran the security-related test classes: `dotnet test --artifacts-path TestResults/sec-art --filter-class ...AuthTests --filter-class ...RoleMatrixTests --filter-class ...SecretsConfigurationAcceptanceTests --filter-class ...UsersTests`: **386 total, 386 passed**. (A first run with the artifacts outside the repository failed 7 `SecretsConfigurationAcceptanceTests`: they find the repository by walking up from the test binaries, so they cannot work from a folder outside it. This is not a product fault.)

**Not applicable, and why.**

- CSRF: there are no cookies. The token travels in the `Authorization` header, which a browser never adds to a cross-site request by itself.
- CORS: no CORS policy is configured, so browsers allow same-origin calls only. This becomes relevant when the front end is built (SEC-10).
- File uploads, file paths, templates and shell commands: the product has none.
- Password reset by email: there is none. Only a Manager can set another user's password (AC-03.7).
- XML and deserialisation of types: the only body format is JSON, with no polymorphic types.

## 2. Findings

Severity: **critical** (exploitable now, serious impact), **high**, **medium**, **low**, **info** (no change needed now, recorded for later). "Required" means I consider it a condition of release (section 6). "Later" means it can go on the PRD's Later list.

### SEC-01 (medium, required): one request from any signed-in user can stall all writes and take gigabytes of memory

**Where.**
- `Features/ProductionOrders/SendOrderService.cs:100` opens the write transaction, and `:110` then validates the body inside it.
- The same pattern is in `CompleteOrderService.cs:125`/`:136`, `ComplementaryOrders/RequestComplementaryOrderService.cs:74`/`:85`, `ComplementaryOrders/ComplementarySendService.cs:96`/`:110` and `Recipes/RecipeService.cs:74`/`:87`.
- `OrderLineMatcher.cs:123` loops over every submitted line, with no limit.
- `Common/Validation/ValidationErrors.cs:32` joins every message into `detail`, with no limit.
- No body size limit is set anywhere, so Kestrel's default of 30,000,000 bytes applies.

**How it is exploited.** A Warehouse user (the lowest privilege that can send) posts `{"lines":[null,null,...]}` to `POST /api/v1/production-orders/{n}/send` for any order that exists, in any status. The 400 for the body comes before the 409 for the state, so a completed order works too. Each `null` line costs 5 bytes and produces two error messages. Model binding reads the body before the action runs. The matching, and building millions of error strings, then happen while the request holds SQLite's single write lock (`BEGIN IMMEDIATE`). Every other write in the company waits behind it. The 400 response then contains every message twice, once in `detail` and once in `errors`.

**Measured** (`p7_biglines.py`, Development instance):

| Body | Answer | Time | Response size | Worst receipt by another user meanwhile (normal: 16 ms) |
|------|--------|------|---------------|-------------------------------------------------------|
| 100,000 null lines (0.5 MB) | 400 | 0.24 s | 13.4 MB | 27 ms |
| 1,000,000 null lines (5 MB) | 400 | 3.6 s | 135.8 MB | 2,302 ms |
| 3,000,000 null lines (15 MB) | 400 | 9.1 s | 411.8 MB | 7,250 ms |

After the 15 MB request, the API process was holding **3.1 GB** of memory (`tasklist`). A 30 MB body is still allowed. A few of those sent in parallel would very likely exhaust memory and crash the single process; I did not try, to protect this machine. Several such requests sent back to back could also keep the write lock past SQLite's 30-second busy timeout, and then other users' writes fail with `500`. Production users can do the same through complete and complementary requests; Managers through recipes.

**Fix.**
1. Set a body limit in `Program.cs`: `builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 1_000_000);`. 1 MB is far above any real body: a 500-line recipe is about 30 KB.
2. Cap the number of lines **before** `InWriteTransactionAsync`, in one shared place (for example a `Common/Validation` constant `MaxLines = 1000`, checked by every service that takes `lines`). Answer `400 validation`, with a message that `ux-designer` adds to design 7.2.
3. Cap `ValidationErrors` at, say, 100 messages, with a final "and N more problems." message. This bounds the response for any future list input too.

### SEC-02 (medium, required): any anonymous client can fill the log with Error-level stack traces, and oversized or broken bodies are answered `500`

**Where.** `Common/Errors/ApiExceptionHandler.cs:48` (`_ => Unexpected(...)`) and `:67` (`LogError(exception, ...)`). Kestrel reports a body that is too large, or a broken chunked body, as `Microsoft.AspNetCore.Http.BadHttpRequestException`, which carries a `StatusCode` of 413 or 400. The handler has no case for it, so it treats it as a programming error.

**How it is exploited.** No token is needed, and no real body is either. The header `Content-Length: 31000000` alone makes Kestrel refuse the request before reading it. The client gets `500 internal-error`, and the server logs an Error with a stack trace of about 27 lines.

**Measured** (`p6_cl.py`, `p15_logflood.py`): 50 such requests took 0.12 s. From one client with 8 threads, the server answered **678 requests per second** for 10 seconds, and the log grew by **26.5 MB (2.6 MB/s, about 9 GB per hour)**. A malformed chunked body (`Transfer-Encoding: chunked` followed by `ZZ`) also gets `500`. The `500` body itself reveals nothing; the problem is the false alarm, the hidden real errors and the disk use. The release phase will decide where logs go (a file or a service); this matters most there.

**Fix.** In `ApiExceptionHandler.TryHandleAsync`, add a case before the default one: `BadHttpRequestException bad => ProblemResponse.Create(bad.StatusCode, ...)`, with code `request-too-large` for 413 (and `validation` for 400), logged at Debug or Information without the exception. The wording goes in design 7.2. SEC-01's smaller limit does not fix this one; both are needed.

### SEC-03 (medium, required at deployment): plain HTTP in Production; passwords and tokens travel unencrypted

**Where.** `Program.cs:90-93` calls `UseHttpsRedirection()` in Production only. The app configures no HTTPS endpoint, and there is no `UseHsts()`.

**What I saw.** The Production instance listened on `http://` only and logged `warn: ... Failed to determine the https port for redirect.` It then served every request, including sign-in, over plain HTTP. Even where redirection works, it only helps after the client has already sent the password or token unencrypted.

**How it is exploited.** On a shared office network or Wi-Fi, anyone who can see the traffic reads sign-in passwords and 8-hour bearer tokens, then uses them.

**Fix (`devops-engineer`, phase 7).** Serve the API only over HTTPS. Choose one of these:
- A Kestrel HTTPS endpoint with a certificate, with the certificate path and password supplied as secrets. Then bind HTTP to localhost or turn it off.
- A reverse proxy (IIS, nginx, Caddy) that terminates TLS, with `UseForwardedHeaders` limited to that proxy and Kestrel bound to `127.0.0.1`.

Add `app.UseHsts()` in Production. Record the choice in `docs/07-release.md`. If v1 only ever runs on `localhost` for one person, this can wait. Say so in the release notes.

### SEC-04 (medium, owner decision): anyone can keep any known account locked out, including the last Manager's, and the lock-out reveals which emails exist

**Where.** AC-01.4 as built: `Auth/SignInService.cs:113-127` and `Auth/AppUserManager.cs:60-76`. These are correct to the PRD; the risk is in the rule itself.

**How it is exploited.** No token is needed. Five wrong passwords for `manager@company` lock that account for 15 minutes; five more every 15 minutes keep it locked for ever. That is 20 requests an hour. If every Manager is locked out, nobody can manage users, approve complementary orders or read notifications. The same five requests tell the attacker whether an email has an account: a real account answers `account-locked` on the sixth try, while an unknown email always answers `invalid-credentials`. Decision #62 already notes this.

**Verified** (`p2_signin.py`): 5 wrong passwords, then the right one, gave `account-locked`. Change-password guesses count too (#71): after 5 wrong current passwords, both change-password and sign-in answered `account-locked` (`p13_cp.py`). A Manager's reset clears the lock (#72); this is the recovery path, and it fails when the locked account is the only Manager.

**Fix (owner decides; it touches AC-01.4).**
1. Add a per-IP rate limit on sign-in and change-password (SEC-05). This slows an attacker on one address but does not stop one using many.
2. Log lock-outs (SEC-08), so Managers can see an attack.
3. Keep at least two active Manager accounts, and say so in the README.
4. Optionally, lock per account **and** client address, or use a growing delay instead of a hard lock. That changes AC-01.4, so the product manager would need to update the PRD.

For an API reachable only on the company network, 2 and 3 are enough for v1. Before the API is reachable from the internet, 1 is needed as well.

### SEC-05 (low, Later unless exposed to the internet): no rate limit on sign-in; each attempt costs a password hash

**Where.** No `AddRateLimiter` anywhere. `SignInService.cs:78-83` runs a full PBKDF2 check (about 45 ms of CPU) on every refusal, by design (#62).

**Measured** (`p4_flood.py`): one client process sent sign-ins with random emails over 32 parallel connections and got about 49 answers per second. During the flood, a legitimate sign-in's median time rose from **91 ms to 670 ms**. `GET /units` was not affected (15 ms before, 22 ms during). Several machines with a proper load tool would use all CPU cores.

**Fix.** Use ASP.NET Core's built-in rate limiter (`builder.Services.AddRateLimiter`, `app.UseRateLimiter()`): a fixed window partitioned by `RemoteIpAddress` on the sign-in and change-password endpoints (for example 10 a minute), and a concurrency limiter so that at most about twice the core count of hashes run at once. Answer `429` with a ProblemDetails body (new design 7.1 row). If a reverse proxy is used (SEC-03), the client address must come from forwarded headers set by that proxy only.

### SEC-06 (low, required at deployment): the .NET runtime on this machine is three patches behind

**Measured.** `dotnet sdk check`: `Microsoft.AspNetCore.App 10.0.12`, "Patch 10.0.15 is available"; SDK 10.0.401, "Patch 10.0.404 is available". The app's NuGet packages are 10.0.15, but the web server (Kestrel) and the rest of ASP.NET Core run from the installed shared runtime, 10.0.12. `dotnet list package --vulnerable` does not check the runtime. I did not look up which security fixes 10.0.13 to 10.0.15 contain, so this rating covers the gap I verified, not a specific vulnerability. The runtime 8.0.18 is also installed and out of support; the app does not use it.

**Fix.** Install the latest .NET 10 runtime (or SDK 10.0.404) on every machine that runs the API, then re-run `dotnet sdk check`. Patch monthly. Stay on 10.x: `--outdated` lists 11.0.0, but decision #19 chose 10 LTS on purpose.

### SEC-07 (low, owner decision): removing a role does not end that user's sessions

**Where.** `Features/Users/UserService.cs:180-218`, by design (AC-03.5, PRD row 15, decision #66a).

**Verified** (`p9_demote.py`): a Manager signed in. Another Manager removed their Manager role (200). With the old token, the demoted user then **created a new Manager account (201)**. For up to 8 hours they keep every right they had, including creating a fresh Manager account that survives the demotion. Deactivation, on the other hand, ended the token at once (401), and it stayed ended after reactivation.

**Fix.** At minimum, the README states the runbook: to remove someone's access *now*, deactivate them (and reactivate them with the new roles if they should continue). Optionally, the owner changes AC-03.5 so that removing a role ends sessions: `SetRolesAsync` calls `UpdateSecurityStampAsync` when `toRemove` is not empty.

### SEC-08 (low, Later): security events are not fully recorded

**Where.**
- `UserService.cs:171, 214, 250, 266` and `PasswordService.cs:221` log the user whose account changed, not the Manager who changed it.
- `SignInService.cs:125` records a failed sign-in in the database but writes nothing to the log. A lock-out starting is not logged either; only a successful sign-in is (`:138`).

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

**Where.** `docs/03-architecture.md:458` (`dotnet user-secrets set ADMIN_PASSWORD "Choose-A-Strong1"`) and `src/InventoryTracker.Api/InventoryTracker.Api.http:21`.

**Risk.** The value meets the policy, so if the owner copies the command as written, the first Manager's password is published in the repository's history. AC-02.5 is technically met (it is a sample, not a real credential), and no real value was found (section 4.5).

**Fix (`technical-writer`).** Use a placeholder that fails the policy and cannot be pasted by mistake (`"<choose-your-own-strong-password>"`). In the README, add: change the first Manager's password after the first sign-in, and remove `ADMIN_PASSWORD` from the environment once the first Manager exists (it is never read again, AC-02.2).

### SEC-13 (low, Later): configuration that is easy to get wrong in production

- **`dotnet run` starts in Development.** `Properties/launchSettings.json` sets `ASPNETCORE_ENVIRONMENT=Development` for both profiles. If the API is started on a server with `dotnet run`, it runs in Development: the OpenAPI document is anonymous, user-secrets are read and there is no HTTPS redirection. I checked the safe case: when the environment is not set, it defaults to Production, and there `/openapi/v1.json` answered `401`. Phase 7 should run the published DLL, or `dotnet run --no-launch-profile`, with `ASPNETCORE_ENVIRONMENT=Production`.
- **The token lifetime is not range-checked.** `Jwt__TokenLifetimeHours` (`Common/Configuration/JwtOptions.cs:34`) accepts any whole number; a typo of `8000` gives tokens that last almost a year. Add a 1 to 24 check to `StartupChecks.CheckSettings`, in the style of the tolerance check.
- **Every environment has the same issuer and audience** (`InventoryTracker`). A token from a test instance is accepted by production if the two share `JWT_SIGNING_KEY`. Generate a separate key per machine (the architecture section 8 command does this) and state it in the README.
- **`JWT_SIGNING_KEY` is only checked for length** (`Auth/StartupChecks.cs:126`, at least 32 characters, which is at least 32 bytes as UTF-8, enough for HS256). A long but guessable phrase would pass. The documented random 48-byte key is good; the README should insist on it.
- **`AllowedHosts` is `*`** (`appsettings.json:9`). Set it to the real host name in production. The impact is small here, because the API builds no absolute URLs.

### SEC-14 (info): writes under many parallel clients

**Measured** (`p12_load.py`, receipts as a Warehouse user, Development instance):

| Parallel writers | Receipts | Throughput | Median | p95 | Max | Errors |
|------------------|----------|-----------|--------|-----|-----|--------|
| 10 | 300 | 61/s | 18 ms | 340 ms | 2.9 s | none |
| 40 | 600 | 78/s | 25 ms | 2.5 s | 5.7 s | none |
| 100 | 600 | 117/s | 153 ms | 1.7 s | 3.9 s | none |

SQLite serialises writers (architecture risk 3). No request failed, but waiting is not first-come-first-served, so a few requests wait seconds. This client is single-process Python on the same machine, so the absolute numbers are rough. A small company will rarely have 10 people saving at the same instant, so I accept this for v1. A request that holds the lock for a long time (SEC-01) is the real danger, and fixing SEC-01 removes it. PostgreSQL (Later) removes the limit.

### SEC-15 (info): the Manager role is fully trusted

These are all by PRD design (decisions #14, #66b, #72), recorded here so the owner decides knowingly:

- A Manager can raise, approve and send a complementary order and complete the order alone (PRD row 6), so no second person has to look at "extra material", which is where decision #4 says waste hides.
- A Manager can reset any other Manager's password without notice, and unlock any account.
- `PUT /users/{own id}/password` needs no current password (verified: 204), so a stolen Manager token can take over the account permanently. The change-password lock-out (#71) protects only the other roles.

Later options: require the current password when a Manager resets their own; notify the other Managers of resets; require two different people to approve a complementary order and send it.

### SEC-16 (info): personal data

The product stores, per staff member, an email, a password hash, roles, an active flag, a creation time and Identity's lock-out fields. The other Identity columns (phone number, two-factor) are unused and stay empty. Every signed-in user sees the emails of the people on every movement, order and return (`PersonResponse`). This is the audit trail of AC-05.3, and it is the only personal data in the product. Users are never deleted (PRD section 5), so there is no erasure path. If a former employee asks for erasure, the only option would be replacing their email with a pseudonym (Later; needs owner and product decision). The SQLite file is not encrypted. It holds the password hashes, so the file and its backups need the same protection as the server (phase 7).

### SEC-17 (info): small items

- `.gitignore` covers `.env`, `.env.*` (except `.env.example`), `*.db`, `*.db-shm`, `*.db-wal` and `*.sqlite`, but not `*.db-journal`. SQLite creates that file briefly during the first migration, before WAL mode is switched on. Add it.
- Duplicate JSON keys are accepted, and the last one wins (`{"name":"dupA","name":"dupB"}` gave 201). This is harmless here and noted only for completeness.
- Users cannot end their own session (there is no sign-out). Changing one's password ends every session at once (stamp check). This is acceptable with 8-hour tokens.
- The `stamp` claim exposes the user's Identity security stamp to its holder. No token providers are registered (no email or reset tokens), so it cannot be misused. Keep it that way, or switch to a separate session id, if reset-by-email is ever added.

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
- **T-003 timing leak** (decision #62). Verified with 160 interleaved pairs (`p3_timing.py`): unknown email median 76.7 ms, real email with a wrong password 78.4 ms. The ranges overlap almost completely (p10 59.5 vs 62.1, p90 99.7 vs 100.2). The remaining difference of about 2 ms is the failure-count write, smaller than ordinary network jitter. The lock-out message is a much stronger existence check (SEC-04), so nothing more is needed here. Locked accounts, inactive users and successful sign-ins all took 83 to 89 ms.
- **Password storage and policy.**
  - Identity's PBKDF2 hasher (salted, HMAC-SHA512, 100,000 rounds). `AuthTests` `Passwords_are_stored_only_as_salted_hashes` passes.
  - The code-point length rule (#118): "Aa1🧀🧀🧀" was refused and `abcdefg1` was refused. Verified.
  - Passwords of 1, 10 and 25 MB took 93 to 229 ms, so length does not multiply the hashing cost. The body limit (SEC-01) bounds it anyway.
  - Common passwords that meet the policy (`Password1`, `Qwerty123`) are accepted. That is the PRD's policy; checking against a list of breached passwords goes on the Later list.
- **Start-up admin** (`Auth/StartupChecks.cs:70-183`). Created only when there are no users, with the same password validators as `POST /users`, and with the Manager role only. The log line names the user id, not the email. Verified on both instances.
- **Token handling.** Tokens appear only in the sign-in response. They are never logged and never stored server-side.

### 3.2 Authorisation and IDOR

- All 48 endpoints' `[Authorize]` attributes match PRD 4.2 (I read every route), and `RoleMatrixTests` passes.
- Live spot-check (`p8_roles.py`): 44 calls by the wrong role (Warehouse, Production, and Warehouse plus Production) across adjustments, orders, cancel, approve, returns, notifications, users, recipes, items and units all got `403`. Anonymous `POST /users` got `401` (AC-01.6).
- **User ids come from the token, never the body.** Every service that records a user gets it from the controller's `User.FindFirstValue("sub")`. No request record has a user-id field. `change-password` with a victim's id in the body was ignored: the wrong guess counted against the caller, and the victim still signed in with their own password.
- **IDOR.** Read access to orders, movements, returns and stock is open to every signed-in role by PRD 4.2, and that is all those endpoints allow. Notification read status is the caller's own (`NotificationService.cs:112-198`, user id from the token). User records are Manager-only. No endpoint lets one role read or change something PRD 4.2 keeps from it.

### 3.3 Input handling

- **SQL.** All product queries are EF Core LINQ. The one raw statement in product code, `NotificationService.cs:189`, uses `ExecuteSqlAsync` with an interpolated `FormattableString`, so `{userId}` and `{now}` become parameters. `DatabaseSetup.cs:59` is a constant `PRAGMA`. In the tests, every `ExecuteSqlAsync`/`SqlQuery` uses interpolation (parameters), and the one `ExecuteSqlRawAsync` is a constant `DROP INDEX`. Live: the unit name `x'); DROP TABLE Units;--` and an adjustment location of `Warehouse'; DROP TABLE Movements;--` were treated as plain data (201, and 400 for the location); the tables were intact.
- **JSON.** `NumberHandling = Strict`. Enums are parsed by `EnumParser` (names only, any letter case). Verified: `type: 0` and `"0"` are refused; so are `" RawMaterial"`, `1e30`, `NaN` and nesting 5,000 deep (400 each). Quantity limits (#80) were re-tested by QA.
- **Lengths.** Lengths are counted in code points (#116). The sign-in email has no length check, but SEC-01's body limit bounds it.

### 3.4 Errors and leakage

- `500` bodies are the generic ProblemDetails in both Development and Production, with no stack trace, SQL or type name (verified). The `traceId` is the W3C trace id: it reveals nothing and lets a support request be matched to the log.
- **Logs.** After all the probes, the two logs contained **0** occurrences of any password I used, the signing key, any token (`eyJ`) and any email. They contain only user ids.
- `/health` returns only `Healthy` or `Unhealthy`, and its check is cheap. `/openapi/v1.json` returns 200 in Development and **401 in Production** (verified).

### 3.5 Secrets and configuration

- **Tracked files.** `.env.example` has names only. `appsettings*.json` and `launchSettings.json` have no secret, and never had one in any commit (`git log -p` over those files). The test-only signing key and passwords in `tests/` are labelled as such and used only by test factories.
- **History.** I dumped `git log --all -p` (38 commits, the product's paths) and searched for `KEY=`, `PASSWORD=`, `Password=` in connection strings, `SigningKey = "`, long Base64 strings and real email addresses. I also ran `git grep` across every revision of the whole tree for AWS, GitHub, Slack and OpenAI key patterns and PEM private keys. Nothing was found apart from the documented samples (SEC-12) and test-only values. No `.db`, `.env` or certificate file was ever committed; I checked every file ever added.
- `.gitignore` ignores `src/InventoryTracker.Api/.env` and `inventory.db` (checked with `git check-ignore`). User-secrets live in `%APPDATA%`, outside the repository. `git status --ignored` shows only `TestResults/`.

### 3.6 Dependencies

- `dotnet list package --vulnerable --include-transitive`: "no vulnerable packages" for both projects. The transitive packages include Microsoft.IdentityModel 8.19.2, System.IdentityModel.Tokens.Jwt 8.19.2, Microsoft.Data.Sqlite 10.0.15 and SQLitePCLRaw 2.1.13.
- `dotnet list package --outdated`: all top-level packages are 10.0.15, and 11.0.0 is offered. That is a new major, short-term-support version; stay on 10 (decision #19).
- Runtime: see SEC-06.

### 3.7 Concurrency and stock integrity under abuse

Tested with `p11_race.py`, by authenticated but hostile clients. An item had 15 kg in the Warehouse, with two Created orders each needing 9.8 to 10.2 kg. In one burst: 25 sends of order A and 25 of order B (Warehouse user), plus 20 adjustments of −1 kg (Manager), 70 writes in 1.8 s. The results:

- exactly **1** send succeeded; the others got 24 `already-sent` and 25 `another-order-in-production`
- 4 adjustments succeeded and 16 got `insufficient-stock`
- final stock: Warehouse **0.8**, production storage 10.2 (15 − 10.2 − 4 = 0.8), never below zero
- exactly one order in production

Then 30 parallel completions of that order: **1** succeeded and 29 got `already-completed`. The order's movements were exactly one each of SentToProduction, ConsumedInProduction and ProducedOutput. Leftover is applied inside the same serialised send transaction, so it cannot be counted twice either (architecture 5.2). The invariants hold. The only way an authenticated client can hurt them is indirectly, by stalling writes (SEC-01).

## 4. Summary table

| ID | Severity | Finding | Before release? | Owner of the fix |
|----|----------|---------|-----------------|------------------|
| SEC-01 | medium | Unlimited request lines are validated inside the write lock: write stall and GBs of memory from one request by any role | **required** | backend-developer (+ ux-designer for the message) |
| SEC-02 | medium | Oversized or broken bodies give `500` and Error-level stack traces; anonymous log flood at 2.6 MB/s | **required** | backend-developer (+ ux-designer) |
| SEC-03 | medium | Plain HTTP in Production, no HSTS | **required for any deployment beyond localhost** | devops-engineer |
| SEC-04 | medium | Lock-out denial of service on any known email, including Managers; lock-out reveals account existence | owner decision; at minimum the runbook and two Managers | owner, then product-manager / backend-developer |
| SEC-05 | low | No rate limit on sign-in; each attempt costs a hash | Later, unless exposed to the internet | backend-developer |
| SEC-06 | low | Runtime 10.0.12, patch 10.0.15 available | **required on the deployment machine** | devops-engineer |
| SEC-07 | low | Removing a role leaves 8-hour sessions with the old rights | runbook required; code change is an owner decision | technical-writer / owner |
| SEC-08 | low | User-management logs lack the acting Manager; failed sign-ins and lock-outs not logged | Later | backend-developer |
| SEC-09 | low | Control, invisible, bidi and look-alike characters accepted in names | Later | ux-designer, backend-developer |
| SEC-10 | low | Raw HTML in stored text: a front-end XSS risk | front-end requirement | product-manager (Later list), future frontend-developer |
| SEC-11 | low | No `Cache-Control`, `nosniff`; `Server` header | Later | backend-developer |
| SEC-12 | low | Policy-passing sample admin password in docs and `.http` | **required (phase 7 docs)** | technical-writer |
| SEC-13 | low | `dotnet run` gives Development; token lifetime unchecked; shared issuer/audience; `AllowedHosts *` | run in Production **required**; the rest Later | devops-engineer, backend-developer |
| SEC-14 | info | Parallel-writer tail latency (no errors) | no | none (accepted) |
| SEC-15 | info | Manager is fully trusted (self-approval, resets, self-reset without current password) | no | owner (awareness) |
| SEC-16 | info | Personal data: emails visible to all roles; no erasure path; unencrypted file | no (protect file and backups in phase 7) | owner, devops-engineer |
| SEC-17 | info | `*.db-journal` not ignored; duplicate JSON keys; no sign-out; stamp claim | no | backend-developer (gitignore line) |

No critical or high findings.

## 5. What QA routed to this phase, answered

- **Lock-out by guessing (AC-01.4):** a real denial of service; SEC-04 (owner decision).
- **No sign-in rate limit, about 45 ms of CPU each:** SEC-05, measured.
- **Timing signal and lost failure counts (T-003):** both still fixed (section 3.1).
- **TAB and NUL in names:** accepted. Low risk for the API itself; it matters for look-alike items and for the PostgreSQL port (SEC-09).
- **HTML-like text returned unescaped in `application/json`:** correct for a JSON API and not exploitable in this API. It must be handled by the future front end (SEC-10).
- **Writes under many parallel clients:** measured, with no errors and seconds of tail latency (SEC-14). The real danger is SEC-01.

## 6. Release recommendation

**Go-with-conditions.** No critical or high finding is open. The product is safe to release once these conditions are met:

1. **SEC-01**: set `MaxRequestBodySize` (about 1 MB), cap `lines` before the write transaction, and cap the number of validation messages. Small code change plus one design message; `code-reviewer` and a re-run of the suite.
2. **SEC-02**: map `BadHttpRequestException` to its own 413/400 status without an Error stack trace. Small code change plus one design message.
3. **SEC-03, SEC-06, SEC-13 (first point)**, at deployment: HTTPS only, with `UseHsts`; the latest .NET 10 runtime; started in Production, not through `dotnet run`'s launch profile.
4. **SEC-12, SEC-07 runbook, SEC-04 minimum** in the phase 7 documents: no policy-passing sample password; "deactivate to remove access now"; keep two active Managers; remove `ADMIN_PASSWORD` after the first start.

The owner should also decide whether to accept SEC-04's lock-out rule as is, or to add a rate limit (SEC-05) and change AC-01.4. Everything else can go on the PRD's Later list.

If the owner decides v1 will only ever run on `localhost` for one user, condition 3's HTTPS requirement falls away. Conditions 1 and 2 still apply.
