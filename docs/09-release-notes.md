# Release notes: Production & Inventory Tracker API

## Version 1, 2026-11-22

The first release. The build carries no version number of its own, and no git tag or GitHub release has been made (both need the owner's approval). "Version 1" is the API under `/api/v1`, as of the commits on `main` up to 2026-11-22.

An HTTP API, with no user interface, that runs on one Windows machine and is reachable from that machine only. How to install it: [README](../README.md). How to use it: [user guide](08-user-guide.md).

### What is included

All 23 user stories of [`01-prd.md`](01-prd.md), through 48 endpoints. QA traced all 130 acceptance criteria to passing tests ([`05-test-plan.md`](05-test-plan.md)).

- **Sign in (US-01):** staff sign in with email and password and get a bearer token valid for 8 hours; 5 wrong passwords in a row lock the account for 15 minutes.
- **First Manager account (US-02):** on its first start the API creates one Manager from `ADMIN_EMAIL` and `ADMIN_PASSWORD`, and never reads them again.
- **Manage users (US-03):** Managers create users with one or more of the roles Warehouse, Production and Manager, change roles, deactivate, reactivate and reset passwords. At least one active Manager always remains.
- **Change own password (US-04):** every user can change their own password.
- **Role checks and audit trail (US-05):** every action is limited to the roles in PRD section 4.2, and every movement and status change records who did it and when (UTC).
- **Units of measure (US-06):** Managers create, rename and delete unused units.
- **Items (US-07):** Managers maintain raw materials and finished products, each with one unit.
- **Recipes (US-08):** Managers set how much of each raw material one unit of a finished product needs.
- **Stock receipts (US-09):** Warehouse users record raw materials arriving in the Warehouse.
- **Current stock (US-10):** everyone sees Warehouse and production storage stock, and for raw materials the committed amount, pending returns and free leftover.
- **Movement history (US-11):** everyone lists every stock change, with filters by item, location, type, order and dates. Movements are never edited or deleted.
- **Adjustments and write-offs (US-12):** Managers correct stock or write it off with a reason; stock never goes below zero.
- **Create a production order (US-13):** Managers create orders from a recipe; each line shows a leftover estimate and the accepted send range.
- **View orders (US-14):** everyone sees orders with every planned, sent, used and variance figure.
- **Send materials (US-15):** Warehouse users send each order's materials once, within the tolerance window, net of free leftover, oldest order first. One order is in production at a time.
- **Request a complementary order (US-16):** Production users ask for extra material for the running order, with a reason.
- **Approve or reject (US-17):** Managers decide on each request.
- **Send for a complementary order (US-18):** Warehouse users send approved extra material, within the tolerance window.
- **Complete an order (US-19):** Production users register the produced quantity and the leftover once; finished goods enter the Warehouse and the leftover stays in production storage.
- **Output and usage variance (US-20):** every completion records output and usage variance per material, and flags usage above the tolerance and any output difference.
- **Cancel an order (US-21):** Managers cancel Created or InProduction orders with a reason; no stock moves.
- **Return leftover (US-22):** Managers instruct returns of free leftover, and the Warehouse records receiving them.
- **Notifications (US-23):** Managers read five types of notification, with read status kept per Manager.

Also included: `scripts\` (build, test, publish, run, signing key, backup, restore), a published folder that bundles the patched .NET runtime 10.0.15, a CI workflow, sample requests for every endpoint (for a Development database only; their passwords come from `src/InventoryTracker.Api/.env`, see the README, "Development"), an OpenAPI description in Development, and the user guide, which uses a small PowerShell helper (decision #134).

### Known issues

There are no open bug reports. The one bug found in QA, BUG-001 (minor: text limits counted an emoji as two characters), is fixed and closed (decision #116).

These limits and known behaviours are by decision or design. Each is in the documents named.

| Issue | What happens | Workaround or plan |
|-------|--------------|--------------------|
| Localhost only (#119, SEC-03) | The API serves plain HTTP on `127.0.0.1` and `[::1]` only, with no HTTPS, no HSTS and no sign-in rate limit. | Do not expose it to any other machine. HTTPS, HSTS and a rate limit are required first, and the security review must be re-opened ([`07-release.md` section 9](07-release.md#9-before-the-api-is-reachable-from-any-other-machine)). |
| SQLite, one process | All data is in one SQLite file. Writes run one at a time; under heavy parallel load a request can wait seconds (SEC-14). Only one copy of the API may run (the sign-in lock-out lives in the process). | Fine for one small site. PostgreSQL is on the Later list. |
| Request limits (#122 to #125) | A request body over 1 MB gets `413`. A recipe, send, completion, complementary request or complementary send over 500 lines gets `400` "At most 500 lines are allowed." A user with more than 10 roles in the list gets `400` "At most 10 roles are allowed." A `400` lists at most 100 problems. | Real requests are far below these limits. |
| Quantity limits (#80) | Quantities at most 999,999,999.999 with 3 decimal places; recipe amounts at most 999,999.999999 with 6. | None needed for real values. |
| Lock-out abuse (SEC-04, #120) | Anyone who can reach sign-in can keep any account locked with 5 wrong passwords every 15 minutes, and the lock-out message shows which emails have accounts. | Keep at least two active Managers; a Manager's password reset clears a lock. Localhost-only limits who can try. |
| Role changes wait for the next sign-in (AC-03.5, SEC-07, #121) | A token issued before a role change keeps the old roles for up to 8 hours. | To remove access now, deactivate the user ([README runbook](../README.md#runbook-remove-someones-access-now)). |
| Installed runtime 10.0.12 (SEC-06) | The owner's machine has ASP.NET Core runtime 10.0.12; 10.0.15 is the patched one. The published folder bundles 10.0.15, so the released API is patched; `dotnet run` and the tests use 10.0.12. | Install the latest .NET 10 SDK or runtime, then run `dotnet sdk check` (owner's call). |
| Names accept control and look-alike characters (SEC-09) | Item and unit names may contain tabs, NUL, invisible or right-to-left characters, or letters from other scripts that look identical. | Check names when you create them. A fix is on the Later list. |
| No `Cache-Control` or `nosniff` headers (SEC-11) | Responses carry no caching or content-type protection headers. | Harmless on localhost; needed before any browser front end. |
| Token lifetime not range-checked (SEC-13) | `Jwt__TokenLifetimeHours` accepts any number; a typo such as 8000 gives tokens that last almost a year. | Leave it unset (8 hours) or check what you type. |
| Cosmetic messages | A JSON number too large for a decimal (`1e30`) says "{field} has the wrong type."; a non-numeric number filter (`?itemId=abc`) gets the framework's wording; an order number beyond the integer range gets "No endpoint matches this path." instead of "Order #... not found." | Status codes are correct; only the sentence is less precise. |
| No sign-out | A token works until it expires (8 hours). | Changing your password ends all your sessions; a Manager can deactivate a user. |

### Not tested

- **Writes under many parallel clients at full data volume.** The performance tests time writes one at a time (test plan section 5). The security review measured parallel receipts with no errors but tail latencies of seconds (SEC-14).
- **The CI workflow on GitHub.** It has not run: it starts only after a push, which awaits the owner's approval. It was checked as valid YAML only.
- **PowerShell 7.** The scripts and the user guide's helper were run in Windows PowerShell 5.1 only.
- **Linux, IIS hosting and the cloud guide.** Nothing was deployed. The 1 MB limit under IIS was checked by reading the code only.
- **Windows 10.** Everything was run on Windows 11.

### Security notes

The security review ([`06-security-review.md`](06-security-review.md)) found no critical or high findings and cleared v1 for release on localhost only. Its nine release conditions (section 7.2):

| # | Condition | Status |
|---|-----------|--------|
| 1 | SEC-03: bind to localhost only; state that HTTPS, HSTS and the rate limit are required before exposure | Met: `scripts\run.ps1`, README, release guide section 9. |
| 2 | SEC-06: runtime 10.0.15 or later on the machine that runs the API | Partly met: the published folder bundles 10.0.15; the machine's installed runtime is still 10.0.12 (owner's call). |
| 3 | SEC-13: start in Production, not through the launch profile | Met: `scripts\run.ps1`. |
| 4 | SEC-12: no usable sample admin password; change the first password and remove `ADMIN_PASSWORD` | Met: placeholders in `InventoryTracker.Api.http` and `03-architecture.md`; steps in the README, "First password". The `.http` walk-through also reads the passwords of the user it creates from the git-ignored `src/InventoryTracker.Api/.env` (`SAMPLE_USER_PASSWORD`, `SAMPLE_USER_RESET_PASSWORD`, `SAMPLE_USER_NEW_PASSWORD`, listed in `.env.example`), so no password that passes the policy is published (decision #135). |
| 5 | SEC-04: keep at least two active Managers | Met: README, "Keep two active Managers". |
| 6 | SEC-07: runbook "deactivate to remove access now" | Met: README, "Runbook". |
| 7 | SEC-13: a random signing key per machine and instance | Met: `scripts\new-signing-key.ps1`; README, "Signing key". |
| 8 | SEC-16: the database and its backups hold password hashes | Met: README, "Data"; release guide section 7; the backup script's message. |
| 9 | SEC-02 residual: `ApiExceptionHandler` log level Warning | Met: `appsettings.Production.json`. |

Open recommendations, for later: log who changed a user and log lock-outs (SEC-08); refuse control and invisible characters in names (SEC-09); for a future front end, show API text as text only, with a strict Content-Security-Policy (SEC-10); add security headers (SEC-11); range-check the token lifetime (SEC-13); cap the pending-return list in the send refusal (SEC-18). A Manager is fully trusted, by design: one Manager can raise, approve and send extra material alone, and reset another Manager's password (SEC-15).

### How to install or upgrade

This is the first release, so there is nothing to upgrade from: install it with the README's [quick start](../README.md#quick-start-on-windows).

For a later version ([`07-release.md` section 6, "Upgrading to a new version"](07-release.md#upgrading-to-a-new-version)): the API applies new database migrations itself at start-up, and migrations only go forward, so the backup is your way back.

1. Run the tests on the new code: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test.ps1`.
2. Stop the API (Ctrl+C in its window).
3. Back up: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\backup-database.ps1`.
4. Publish into a new folder, keeping the old one for rollback: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\publish.ps1 -Output publish\win-x64-<date>`.
5. Start it: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run.ps1 -Published -PublishFolder publish\win-x64-<date>`.
6. Check `http://localhost:5133/health` and sign in.

If start-up stops during a migration, restore the backup and run the old folder ([`07-release.md` sections 7 and 8](07-release.md#8-rolling-back)).

### Planned next

From the PRD's Later list, not yet scheduled:

- Sales orders that remove finished goods from stock.
- Tolerance per item.
- Several production lines running orders at the same time.
- Multiple warehouses (general multi-location stock).
- Expiry dates and lot tracking.
- Supplier, lot and purchase price on receipts.
- Email or chat alerts.
- PostgreSQL database.
- A web front end.
- A rate limit on sign-in.
- Access from other machines, with HTTPS and HSTS.
