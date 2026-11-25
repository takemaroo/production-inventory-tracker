# Release notes: Production & Inventory Tracker API

## Version 1.1, 2026-11-25

Sales orders. Customers' orders are now recorded, shipped out of Warehouse stock and totalled, by a new Seller role and the Warehouse. The build carries no version number of its own, and no git tag or GitHub release has been made (both need the owner's approval). "Version 1.1" is the API under `/api/v1` with the `SalesOrders` database migration, as of the commits on `main` up to 2026-11-25. The address stays `/api/v1`.

Still an HTTP API with no user interface, on one Windows machine and reachable from that machine only. How to upgrade: [below](#upgrading-from-version-1). How to use the new parts: [user guide, sections 15 to 21](08-user-guide.md#15-keep-the-customer-list).

### What is new

All 10 new user stories of [`01-prd.md`](01-prd.md) (US-24 to US-33), through 14 new endpoints, 62 in all. QA traced all 67 new acceptance criteria, and the version 1 criteria that version 1.1 changes, to passing tests ([`05-test-plan.md`](05-test-plan.md) Part 2).

- **Customers (US-29):** Sellers and Managers keep a customer list: a unique name and free-text notes. A customer can be deactivated (no new sales orders) and reactivated; one that never had a sales order can be deleted.
- **Take a sales order (US-24):** Sellers and Managers record what a customer ordered: one or more lines of any item, finished products and raw materials alike, with an optional customer reference such as the customer's purchase order number. Taking an order checks and reserves no stock.
- **View sales orders (US-25):** Warehouse, Managers and Sellers list sales orders by status, item and customer, and open one to see its lines and who created, shipped or cancelled it.
- **Ship a sales order (US-26):** Warehouse users and Managers record that the order has left. Every line ships in full, once, from Warehouse stock only. If any line is short, nothing ships, and the message names every short line.
- **Cancel a sales order (US-27):** Sellers and Managers cancel any order not yet shipped, with a reason. No stock moves.
- **Shipments in the stock history (US-28):** a shipment is a new kind of movement, **ShippedToCustomer**, linked to its sales order and filterable by it, so sales are no longer mixed in with adjustments.
- **Seller role (US-30):** a fourth role, given only by a Manager. A Seller keeps customers and takes and cancels sales orders, and reads units, items, stock, customers, sales orders and the reports. A Seller does not ship.
- **Reads by role (US-30):** for the first time some reads are limited. A Seller gets `403` on recipes, movements, production orders, complementary orders, returns, users and notifications. Production gets `403` on customers, sales orders and the reports, but sees shipment movements. Warehouse and Managers read everything they read in version 1.
- **Open demand (US-31):** the stock view shows, for every item and to every role, the quantity customers are still waiting for (Created sales orders). It is a figure to read; nothing is reserved.
- **Shipped totals per item (US-32) and per customer (US-33):** two reports of what was shipped in a date range, for Warehouse, Managers and Sellers.
- **Name rules (BUG-002):** unit, item and customer names are compared in one standard Unicode form, so a look-alike of an existing name ("é" typed as one character, or as "e" plus an accent mark) is refused as a duplicate. A name with no visible characters is refused ("Name is required."), and so is a name containing U+FFFE ("Name contains a character that is not allowed.").
- **Who changed a customer (T-033, SEC-26):** every rename, notes edit, deactivation, reactivation and delete of a customer writes one line in the API's console log with the customer id and the id of the user who did it, never the name or the notes.

Also changed for version 1 users: stock rows gain `openDemand` and movements gain `salesOrderNumber`; the unknown-role message now lists four roles; an item on any sales order line counts as in use, and the item delete message says so ("... has movements, recipe lines, order lines or sales order lines and cannot be deleted."). Warehouse, Production and Manager users keep every permission they had. Version 1.1 adds no setting and no secret; the sample requests gain `SAMPLE_SELLER_PASSWORD` in `src/InventoryTracker.Api/.env` ([README, Development](../README.md#development)).

### Upgrading from version 1

**Back up first.** Follow [`07-release.md` section 6, "Upgrading from version 1 to version 1.1 (once)"](07-release.md#upgrading-from-version-1-to-version-11-once), which was rehearsed end to end on scratch copies. In short: stop version 1, run `scripts\backup-database.ps1` and write down the backup folder, rename `publish\win-x64` to `publish\win-x64-v1`, publish version 1.1, and start it with `scripts\run.ps1 -Published`. On its first start version 1.1 applies one migration, `SalesOrders`, by itself, in one transaction: if it fails, the database stays as version 1 left it. Users, passwords and roles do not change, and nobody holds Seller until a Manager gives it.

- **Version 1 cannot read version 1.1 data.** Once version 1.1 has started on the database, the only way back is to restore the backup taken before the upgrade and swap the publish folders so the daily start runs version 1 again (same section, "If it goes wrong, or you want version 1 back"). Anything recorded in version 1.1 after the backup is lost.
- **Version 1 starts on an upgraded file without any warning.** Started on it by mistake, it logs "No migrations were applied" and runs, but the movement list fails with `500` once anything has shipped, and Sellers show no roles. Stop it and restore the backup. A start-up check that refuses this is on the Later list.
- **Names saved before version 1.1 are not rewritten** (decision #172). The new name rules apply to names created or renamed from now on. Only unit and item names can be affected, because customers are new. A unit or item name stored before the upgrade in the rare decomposed form (a letter followed by a separate accent mark; Windows keyboards type the single-character form) can still gain one look-alike twin: the same name typed the usual way is accepted as a new name.

### Known issues

There are no open bug reports. The one bug found in version 1.1 QA, BUG-002 (minor: the same name typed in two Unicode forms made two customers), is fixed and closed (decisions #170 to #174).

These limits and known behaviours are by decision or design. Every version 1 limit below still applies.

| Issue | What happens | Workaround or plan |
|-------|--------------|--------------------|
| A very large shipment holds other saves (SEC-24, #175) | Shipping a sales order with hundreds of lines takes seconds: about 2 s for 250 lines and 4 to 8 s for 500. Every other save, by every user, waits until it ends. Sales orders of up to 10 lines take under 50 ms. | Keep sales orders to normal sizes. Writing a shipment in one batch is planned before the web front end. |
| Blank-looking and look-alike names are still possible (SEC-25, #176) | Names made only of characters that show as blank (for example the Hangul filler U+3164 or the braille blank U+2800) are accepted, and invisible characters at the start or end of a real name are kept, so two customers can look identical in a list. NUL, tab and right-to-left characters inside a name are accepted too. | Check the customer list before adding a name, and when picking a customer for a sales order, check its id. Tighter rules are a small task planned right after this release. |
| Names saved before v1.1 (#172) | See "Upgrading" above: one look-alike twin of a decomposed unit or item name is still possible. | Check unit and item names with accents in the lists. |
| Shipping does not protect production (#149) | A shipment may take Warehouse raw material that a Created production order will need; that order's send is then refused for lack of stock. | Check the production orders before shipping raw material. A warning is on the Later list. |
| Full shipments only (#141, #142) | No partial shipments or backorders. A Shipped sales order cannot be cancelled, and there is no customer-return feature. | To ship less, cancel and take a smaller sales order. Goods sent back: a Manager's positive Warehouse adjustment whose reason names the sales order ([user guide, section 21](08-user-guide.md#21-correct-a-mistaken-shipment-or-take-goods-back)). The shipped-totals reports still count the original shipment. |
| No record on the customer of who changed it (SEC-26, #177) | The log line says who changed a customer, but it is only in the API's console window: `run.ps1` writes no log file, so the line is gone once the window closes or scrolls past it. The customer itself shows only who created it, and an edit replaces the notes, so the old text is gone. | Copy the log line from the API window if you need to keep it. "Last changed by" columns are on the Later list. |
| One person can sell and ship alone (SEC-29, #178) | A user with both Warehouse and Seller can take a sales order and ship it, with only their name on the records. | Give both roles to one person only when needed. A "cannot ship your own sales order" rule is on the Later list. |
| The sample requests ship stock (SEC-31) | The `.http` walk-through uses port 5133, the port of the real API, and its version 1.1 part creates a Seller account and ships stock. | Use it only with the Development API, after stopping the real one ([`07-release.md`, the note after section 6 step 8](07-release.md#6-deploying-on-the-owners-machine)). A separate Development port is on the Later list. |
| Cosmetic message | A sales order number beyond the integer range (or not a number) gets `404` "No endpoint matches this path." instead of "Sales order #... not found.", as for production orders in version 1. | The status code is correct. |

### Not tested

- **Writes under many parallel clients at the version 1.1 volume.** As in version 1, the performance tests time writes one at a time; the live races used at most 10 requests at once on a small database (QA) and up to 30 (security review).
- **Upgrading a large version 1 database.** The upgrade ran on small real version 1 databases (QA, and the release rehearsal) and on test-built ones. The tech lead's prototype upgraded a 100,000-movement database in 180 ms, but that was not repeated with the release build.
- **A migration failure on the owner's real data.** The security review forced one on a scratch database: the database stayed exactly as version 1 had it.
- **The CI runs on GitHub for the version 1.1 commits.** Nobody has looked at them from this machine; check the Actions tab.
- **PowerShell 7, Windows 10 and Linux**, as in version 1.

### Security notes

The security review, Part 2 ([`06-security-review.md`](06-security-review.md) sections 8 to 13), found no critical, high or medium finding and cleared version 1.1 for release on localhost only. The authorisation matched the design on all 62 endpoints in 671 live calls.

**Localhost only, Sellers included.** Sellers, like everyone else, use the API on the owner's machine only. Remote access for sales staff, from a laptop, a phone or another PC, first needs HTTPS, a sign-in rate limit and a new security review ([`07-release.md` section 9](07-release.md#9-before-the-api-is-reachable-from-any-other-machine)).

Its five release conditions (section 13.2):

| # | Condition | Status |
|---|-----------|--------|
| 1 | Back up before the first v1.1 start; the way back is that backup, because v1 cannot read v1.1 data | Met: [`07-release.md` section 6](07-release.md#upgrading-from-version-1-to-version-11-once) (rehearsed); "Upgrading from version 1" above; README, "Upgrading from version 1". |
| 2 | Localhost only, in Seller terms | Met: the paragraph above; README, "Localhost only"; the user guide's opening; `07-release.md` opening and section 9. |
| 3 | Customer notes (SEC-28): visible to Warehouse, Manager and Seller users; business contact details needed to deliver only | Met: [user guide, section 15](08-user-guide.md#15-keep-the-customer-list). |
| 4 | The runbook "remove someone's access now" names the Seller role (SEC-07) | Met: README, "Runbook"; user guide, section 13. |
| 5 | Start the released build with `scripts\run.ps1 -Published`; do not run the `.http` walk-through while the real instance runs (SEC-31) | Met: `07-release.md` sections 6, 10 and 11; README, "Development". |

Open recommendations, for later: batch a shipment's movements (SEC-24); tighter name rules (SEC-25); "last changed by" on customers (SEC-26); for the web front end, show API text as text only, with a strict Content-Security-Policy and customer ids next to names, and escape HTML characters in the JSON output (SEC-27, extending SEC-10 and SEC-11); a Seller can pile up Created sales orders without limit, which slows the stock view (146 ms at 300,000 lines); the planned sign-in rate limit is to cover sales order creation too (SEC-30). Customer notes are other businesses' and people's data: they stay on this machine, are never logged and are not shown in sales orders or reports, but old text survives in every backup (SEC-28). The version 1 recommendations below are still open.

### Planned next

Right after this release: the tighter name rules (SEC-25, #176). Then, in the owner's order: the web front end, then the path to the cloud. Before the web front end: writing a shipment in one batch (SEC-24) and escaping HTML characters in the API's output (SEC-27).

The rest of the PRD's Later list, not yet scheduled:

- From version 1: tolerance per item; several production lines at once; multiple warehouses; expiry dates and lots; supplier, lot and purchase price on receipts; email or chat alerts; PostgreSQL; the sign-in rate limit, also covering sales order creation (SEC-30); access from other machines, with HTTPS and HSTS; continuous delivery to Azure App Service.
- Sales follow-ons: partial shipments and backorders; reserving stock, with an "available to sell" figure; a shortfall figure on the stock view; a warning when a shipment would leave less stock than Created production orders need; structured customer fields and a name search; prices, order totals and invoices; customer returns as their own movement type; selling leftover straight from production storage; finer read permissions for Sellers; requested delivery dates; exports and open demand per customer; linking a production order to a sales order; a notification when a shipment is refused; "last changed by" on customers; "cannot ship your own sales order".
- Operations: a Development port for the sample requests (SEC-31); a start-up check that refuses a database from a newer version.

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
| Installed runtime (SEC-06) | At release the owner's machine had ASP.NET Core runtime 10.0.12; 10.0.15 is the patched one. The published folder bundles 10.0.15, so the released API was patched. | Resolved: the owner installed SDK 10.0.404 with runtime 10.0.15 on 2026-11-22, so `dotnet run` and the tests use 10.0.15 too (`dotnet sdk check`: up to date, re-checked 2026-11-25). |
| Names accept control and look-alike characters (SEC-09) | Item and unit names may contain tabs, NUL, invisible or right-to-left characters, or letters from other scripts that look identical. | Check names when you create them. A fix is on the Later list. |
| No `Cache-Control` or `nosniff` headers (SEC-11) | Responses carry no caching or content-type protection headers. | Harmless on localhost; needed before any browser front end. |
| Token lifetime not range-checked (SEC-13) | `Jwt__TokenLifetimeHours` accepts any number; a typo such as 8000 gives tokens that last almost a year. | Leave it unset (8 hours) or check what you type. |
| Cosmetic messages | A JSON number too large for a decimal (`1e30`) says "{field} has the wrong type."; a non-numeric number filter (`?itemId=abc`) gets the framework's wording; an order number beyond the integer range gets "No endpoint matches this path." instead of "Order #... not found." | Status codes are correct; only the sentence is less precise. |
| No sign-out | A token works until it expires (8 hours). | Changing your password ends all your sessions; a Manager can deactivate a user. |

### Not tested

- **Writes under many parallel clients at full data volume.** The performance tests time writes one at a time (test plan section 5). The security review measured parallel receipts with no errors but tail latencies of seconds (SEC-14).
- **The CI workflow on GitHub.** It had not run at release: it starts only after a push, which awaited the owner's approval. It was checked as valid YAML only. (After the push on 2026-11-22 its first run passed, in about 7 minutes.)
- **PowerShell 7.** The scripts and the user guide's helper were run in Windows PowerShell 5.1 only.
- **Linux, IIS hosting and the cloud guide.** Nothing was deployed. The 1 MB limit under IIS was checked by reading the code only.
- **Windows 10.** Everything was run on Windows 11.

### Security notes

The security review ([`06-security-review.md`](06-security-review.md)) found no critical or high findings and cleared v1 for release on localhost only. Its nine release conditions (section 7.2):

| # | Condition | Status |
|---|-----------|--------|
| 1 | SEC-03: bind to localhost only; state that HTTPS, HSTS and the rate limit are required before exposure | Met: `scripts\run.ps1`, README, release guide section 9. |
| 2 | SEC-06: runtime 10.0.15 or later on the machine that runs the API | Met: the published folder bundles 10.0.15, and since 2026-11-22 runtime 10.0.15 is installed on the machine too ([`07-release.md` section 1](07-release.md#1-prerequisites)). |
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

- Sales orders that remove finished goods from stock (delivered in version 1.1).
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
