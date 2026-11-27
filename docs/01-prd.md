# Product requirements: Production & Inventory Tracker API

Status: version 1 (sections 1 to 6) approved by the owner on 2026-11-11, written 2026-11-11 from the owner interview of the same day. Version 1.1: Sales orders (sections 7 to 13): approved by the owner on 2026-11-23. Every question is settled by decisions #138 to #151 (section 13). (v1.2) Version 1.2: Web front end (sections 14 to 20): written 2026-11-26; every question (Q98 to Q109) settled by the owner on 2026-11-27 (section 20); approved by the owner on 2026-11-27, including assumption rows 92 and 93.

This document has two parts ((v1.2) three). Sections 1 to 6 are version 1. Sections 7 to 13 add version 1.1: Sales orders. Where version 1.1 had to change a version 1 section, the change is marked "(v1.1)" in place and listed in section 12. Everything else in sections 1 to 6 is unchanged. (v1.2) Sections 14 to 20 add version 1.2: Web front end; its changes to earlier sections are marked "(v1.2)" in place and listed in section 19.2.

Source of truth: this document and the agreed design from the 2026-11-11 interview. Where `README.md` says something different (one warehouse, four movement types, no users, a single "read" flag on notifications, no Cancelled status), this document wins.

## 1. Problem and users

A small food manufacturer turns bulk raw materials into packaged products, for example 1 kg bags of shredded cheese made from cheese bought by the kilogram. The manager needs to know how much raw material each production order should use, how much was actually sent to the line and used, and where the numbers do not match. Today this is tracked by hand or in spreadsheets, so material left over in production storage is forgotten or counted twice, differences are found late, and waste goes unnoticed.

**Primary user:** the production and inventory manager, who plans production orders, approves extra material, and investigates every difference between what was planned, what was used and what was produced.

**Other users:** warehouse staff, who receive raw materials, send them to production and take back returned leftover; production line staff, who register the output, count the leftover and ask for extra material when it runs short.

## 2. Goal for version 1

A manager can run every production order through the API, from creation to registered output, with stock tracked in the warehouse and in production storage, leftover netted against the next order automatically, and every usage or output difference recorded on the order and raised as a notification.

## 3. Scope

In version 1:

- Sign-in with email and password (bearer token), three fixed roles ((v1.1) four: Seller is added, section 4.2), user administration by Managers, first Manager created from environment variables.
- Master data managed by Managers: units of measure, items, recipes.
- Stock in two fixed locations (Warehouse, Production storage), calculated from movements; neither location can go below zero.
- Stock receipts, Manager adjustments (including write-offs), current stock and movement history queries.
- Production orders: creation with a recipe snapshot and a leftover estimate, one send per order with a tolerance window and leftover netting (oldest order first), complementary orders with Manager approval, completion with output and leftover, cancellation.
- Output variance and usage variance, recorded on the order.
- Returning leftover from production storage to the warehouse.
- Five notification types, readable by every Manager, with read status per Manager.
- One global tolerance setting (default 2%) in application settings.

Out of scope:

- Any user interface. Version 1 is an HTTP API only.
- Public sign-up, external login providers, password reset by email.
- Editing or deleting a movement. Mistakes are corrected with a new movement (an adjustment).
- Editing a production order after creation. To change it, cancel it and create a new one.
- More than one send per order. Extra material always goes through a complementary order.
- Receiving finished products from outside. Finished products enter stock only through order completion or a Manager adjustment.
- Automatic stock corrections when a variance is found. A person decides.
- Reports, exports and dashboards beyond the stock, history, order and notification queries below.

Later:

- Tolerance per item.
- Several production lines running orders at the same time.
- Multiple warehouses (general multi-location stock).
- Expiry dates and lot tracking.
- Supplier, lot and purchase price on receipts.
- Email or chat alerts.
- PostgreSQL database.
- A web front end. (v1.2) Moved into sections 14 to 20.
- A rate limit on sign-in. Version 1 keeps only the 15-minute lock-out of AC-01.4 (decision #120).
- Access from other machines. HTTPS (with HSTS) is required before the API is reachable from anywhere but the owner's machine; version 1 runs on localhost only (decision #119, row 22 in section 6).
- Continuous delivery (CD) to Azure App Service (decision #137). After CI passes on `main`, a GitHub Actions job publishes the API to Azure; each deploy waits for a manual approval, not fully automatic deployment. It comes only after the cloud prerequisites: the PostgreSQL database (port: `docs/03-architecture.md` section 5.2), HTTPS with HSTS and the sign-in rate limit above, the other changes in `docs/07a-cloud-guide.md` section 1, a re-opened security review, and Azure resources the owner creates and pays for. Deployment credentials are kept in GitHub secrets or OIDC federation, never in the repository.

Later, follow-ons to version 1.1 sales orders (v1.1; "Sales orders that remove finished goods from stock" moved off this list into sections 7 to 13):

- Partial shipments and backorders: shipping part of a sales order now and the rest later, or shipping less than ordered and closing the order.
- Reserving stock for sales orders at creation, with an "available to sell" figure per item.
- A shortfall figure on the stock view: open demand minus Warehouse stock, where positive.
- Structured customer fields (addresses, contact people, phone, email, tax number) beyond the free-text notes of US-29, and a name search on the customer list.
- Prices and money values: sale prices, order totals and invoices on sales orders, together with the purchase prices on receipts above.
- Customer returns of shipped goods, with a movement type of their own linked to the sales order.
- Selling material from production storage leftover directly, without first returning it to the Warehouse.
- A production shortfall warning: telling the shipper when a shipment would leave less Warehouse stock than Created production orders need (decision #149). Protecting that stock outright would be a reservation, ruled out by decision #140.
- Finer read permissions, for example a Seller seeing only their own customers or sales orders, or a read-only view of production orders for Sellers.
- Requested delivery dates on sales orders, and an alert when one is missed.
- Exports (for example CSV) of the shipped-totals reports, and reports of open demand per customer.
- Linking a production order to the sales order it is made for (make to order).
- A notification when a shipment is refused for lack of stock.
- Writing a shipment's movements in one batch, so a sales order with hundreds of lines does not hold the database for seconds (SEC-24, decision #175). Before the web front end. (v1.2) Part of version 1.2 as US-55.
- Tighter name rules after v1.1 (SEC-25, decision #176): refuse invisible characters at the start or end of a name, count blank-looking characters (Hangul fillers, U+2800) as blank, refuse noncharacters, NUL and tab; joining characters (ZWJ, ZWNJ) stay allowed between letters. A small task right after the v1.1 release, before the web front end.
- "Last changed by" columns on customers (SEC-26, decision #177). v1.1 records changes in the log only.
- A rule that the person who created a sales order cannot ship it (SEC-29, decision #178).
- Escaping HTML-sensitive characters in the API's JSON output and adding `nosniff` (SEC-27, SEC-11); the web front end's own rules (text binding only, strict Content-Security-Policy, ids shown next to customer names) go into its requirements when it is planned. (v1.2) Part of version 1.2: the front end's rules are US-37, and the server escaping is AC-37.9 (Q108). `nosniff` is AC-37.6.
- The sign-in rate limit above (SEC-05) also covers sales order creation (SEC-30).
- A Development port for the sample requests (`InventoryTracker.Api.http`) that `run.ps1` never uses, so the walk-through cannot write to the real instance (SEC-31).
- A start-up check that refuses to start when the database has migrations this version of the code does not know (an older version started on a newer database), and optionally `run.ps1` refusing a pending migration without a fresh backup (v1.1 release rehearsal).
- A persistent log file (for example a daily rolling file next to the database), so log lines such as the customer-change line of T-033 survive after the API window closes (final v1.1 security check, F1).
- Explicit ASP.NET Core DataProtection configuration (fixed application name, a chosen key folder that is backed up, a shared store in the cloud) before the web front end or the cloud path adds cookies, antiforgery or emailed tokens; and an optional key-folder setting so scratch and test runs do not read the owner's key folder (final v1.1 security check). (v1.2) Not needed by version 1.2, which adds no cookies, antiforgery or emailed tokens (section 19.1; the owner chose a token in memory, Q106).

Later, follow-ons to version 1.2 web front end (v1.2):

- Next step after version 1.2: the cloud path, that is the PostgreSQL, sign-in rate limit, access from other machines (HTTPS with HSTS) and CD items above. Owner, 2026-11-27: "later on will be on cloud" (Q105).
- A movement history screen (Q100a): the movement list with its filters (US-11, US-28), with date filters labelled as UTC days (Q107).
- Shipped-totals report screens (Q100b): US-32 and US-33 on screen, with date filters labelled as UTC days (Q107).
- An adjustments screen (Q100c): US-12 on screen, with a signed quantity and a confirmation dialog.
- Using the front end on real phones and tablets, which needs access from other machines: the cloud step (Q102, Q105, Q109). The layout already works from 360 pixels wide.
- Spanish, by translating the API's messages from their `code` (Q103).
- Staying signed in across a page reload or a new tab, with a cookie session, antiforgery and the DataProtection configuration above (Q106).
- A warning before the 8-hour sign-in ends, and renewing it without signing in again.
- A "work to do" home screen per role: Created orders to send, requests to approve, Created sales orders to ship.
- Printing a pick list or delivery note for a shipment.
- Live updates pushed by the server instead of the 60-second notification refresh.
- A name search in the API for items and customers, if the lists grow well past 500.
- Showing open demand above Warehouse stock, together with the shortfall figure above.
- Date filters in local days instead of UTC days (an API change, Q107).
- Remembered list filters and other saved preferences per user.
- Demo data, so someone evaluating the public repository can try the screens from an empty database.

## 4. User stories and acceptance criteria

### 4.1 Conventions used in every criterion

- **Rejected** means: the API returns an error status, nothing is saved, no stock changes, and the response body contains a readable message stating the reason. Status codes: `400` invalid input, `401` missing, invalid or expired token, `403` the user's roles do not allow the action, `404` the record does not exist, `409` the request conflicts with the current state (order status, available stock, another order in production).
- **Unreadable bodies:** `408` and `413` come from the web server when a request body does not arrive in time or is too large (decision #124). See `docs/02-design.md` section 2.2.
- **Quantities** are decimals. Entered quantities have at most 3 decimal places; recipe amounts per unit have at most 6. Input with more decimal places is rejected with `400`. Computed quantities (planned amount, range limits, required amount) are rounded to 3 decimal places, half away from zero. Percentages are shown rounded to 2 decimal places, but threshold comparisons use the unrounded value.
- **Times** are stored and returned in UTC, in ISO 8601 format.
- **Lists** are returned newest first, in pages: default 50 records, maximum 100, with the total count.
- **t** is the tolerance setting (section 4.3). With the default, t = 2%.

### 4.2 Roles and permissions

A user holds one or more of three roles ((v1.1) four: Warehouse, Production, Manager and Seller) and may do everything any of their roles allows. (v1.1) Viewing is also limited by role: a "View" row with a blank cell means that role gets `403` for that read (decision #147).

| Action | Warehouse | Production | Manager | (v1.1) Seller |
|--------|:---------:|:----------:|:-------:|:------:|
| Sign in, change own password | yes | yes | yes | yes |
| (v1.1, split from the row below) View units, items, stock (stock includes open demand, AC-10.5) | yes | yes | yes | yes |
| View recipes, movement history, orders, complementary orders, returns ((v1.1) units, items and stock moved to the row above; Seller: `403`) | yes | yes | yes | |
| Record a stock receipt | yes | | yes | |
| Send materials for an order or an approved complementary order | yes | | yes | |
| Record receiving a leftover return | yes | | yes | |
| Complete an order (output, leftover, reason) | | yes | yes | |
| Request a complementary order | | yes | yes | |
| Create or cancel an order | | | yes | |
| Approve or reject a complementary order | | | yes | |
| Instruct or cancel a leftover return | | | yes | |
| Make an adjustment | | | yes | |
| Manage units, items, recipes | | | yes | |
| Read notifications and mark them read | | | yes | |
| Manage users | | | yes | |
| (v1.1) View customers, sales orders and shipped-totals reports (Production: `403`) | yes | | yes | yes |
| (v1.1) Create, edit, deactivate, reactivate or delete a customer | | | yes | yes |
| (v1.1) Create or cancel a sales order | | | yes | yes |
| (v1.1) Ship a sales order | yes | | yes | |

### 4.3 Terms and calculations

These definitions are part of the acceptance criteria.

- **D-1 Tolerance.** One global percentage in application settings, default 2. It sets the send ranges (D-7) and the usage variance threshold (D-9). It is not used for output variance, which is reported on any difference.
- **D-2 Stock.** Stock of an item in a location is the sum of that item's movement quantities in that location. Movement types and their effect:

  | Movement type | Warehouse | Production storage |
  |---------------|-----------|--------------------|
  | Received | + | |
  | SentToProduction | − | + (same quantity) |
  | ConsumedInProduction (at completion) | | − |
  | ProducedOutput (finished product, at completion) | + | |
  | ReturnedToWarehouse | + (same quantity) | − |
  | Adjustment | + or − | + or − |
  | (v1.1) ShippedToCustomer (any item, at shipment of a sales order, US-26) | − | |

  Every movement records its location (or both locations for a transfer), the user who performed it, the UTC time, and optionally the production order or complementary order it belongs to, or (v1.1) the sales order it belongs to.
- **D-3 Committed material.** While an order is InProduction, the quantity of each of its materials committed to it is: leftover applied + warehouse send + complementary sends.
- **D-4 Free leftover** of a raw material = production storage stock − committed material of the InProduction order (if any) − quantity in Pending returns (US-22). Never less than zero.
- **D-5 Oldest order first.** Free leftover of a material is allocated to orders in Created status in order of creation time (oldest first). Each order takes the smaller of (its planned amount) and (free leftover not yet allocated to older Created orders). Each unit of leftover is allocated to one order only.
- **D-6 Leftover applied.** At the moment of sending for an order, its leftover applied per material is its allocation under D-5, calculated at that moment. It is then fixed on the order line.
- **D-7 Send range** for a line with planned amount P and leftover applied L: the send S is accepted when S ≥ 0 and P × (1 − t) ≤ L + S ≤ P × (1 + t), limits included. In other words, S from max(0, P × (1 − t) − L) to P × (1 + t) − L. The width of the window is always based on the full planned amount P, not the net amount. The nominal send is P − L.
- **D-8 Carried leftover** per material = production storage stock just before the send − leftover applied. It is leftover that sat in production storage but was not applied to this order (for example, because it exceeded the need or was allocated to an older order). It is zero in the normal case.
- **D-9 Variance at completion**, per material line:
  - RequiredForActualOutput = amount per unit (from the order's recipe snapshot) × produced quantity.
  - LeftoverRemaining (for this order) = leftover entered at completion − carried leftover.
  - ActualUsage = leftover applied + warehouse send + complementary sends − LeftoverRemaining.
  - Usage variance = ActualUsage − RequiredForActualOutput. Usage variance % = usage variance ÷ RequiredForActualOutput × 100.
  - A usage variance notification is raised when the absolute value of the usage variance % is greater than t.
  - Output variance = produced quantity − ordered quantity. Output variance % = output variance ÷ ordered quantity × 100. A notification is raised when the output variance is not zero.

### US-01: Sign in

As a staff member, I want to sign in with my email and password so that I can use the API under my own name.

- **AC-01.1** Posting the email and password of an active user to the sign-in endpoint returns `200` with a bearer token, its expiry time (UTC), the user's email and roles.
- **AC-01.2** A token is valid for 8 hours. A request made with an expired token is rejected with `401`.
- **AC-01.3** A wrong password, an unknown email, or a deactivated user all return `401` with the same message, "Invalid email or password."
- **AC-01.4** After 5 failed sign-ins in a row for one account, that account cannot sign in for 15 minutes, even with the correct password; the response is `401` with "Account temporarily locked. Try again later."
- **AC-01.5** Every endpoint except sign-in returns `401` when called without a token or with an invalid token.
- **AC-01.6** No endpoint lets an unauthenticated caller create a user.

### US-02: First Manager account

As the owner, I want the first Manager created from environment variables so that no password is ever stored in the repository.

- **AC-02.1** When the application starts with no users in the database and with `ADMIN_EMAIL` and `ADMIN_PASSWORD` set, it creates one active user with that email and password and the Manager role only. Signing in with those values succeeds.
- **AC-02.2** When at least one user already exists, starting the application creates and changes no user, even if `ADMIN_EMAIL` or `ADMIN_PASSWORD` now hold different values.
- **AC-02.3** When no users exist and either variable is missing or empty, the application stops at start-up with a message naming the missing variable, and no user is created.
- **AC-02.4** When no users exist and `ADMIN_PASSWORD` does not meet the password policy (section 5), the application stops at start-up with a message stating the policy, and no user is created.
- **AC-02.5** `.env.example` lists `ADMIN_EMAIL`, `ADMIN_PASSWORD` and every other secret the application needs (such as the token signing key) with placeholder values. No real credential or key appears in any committed file.

### US-03: Manage users

As a Manager, I want to create staff accounts and set their roles so that each person can do exactly their job.

- **AC-03.1** A Manager creates a user with an email, an initial password and one or more roles from Warehouse, Production, Manager ((v1.1) and Seller). The response is `201`; the new user can sign in and holds exactly those roles.
- **AC-03.2** Creating a user with an email that already exists (compared without regard to case) is rejected with `409` "A user with this email already exists."
- **AC-03.3** An invalid email format, an empty role list, an unknown role, or a password that fails the policy is rejected with `400` naming the problem.
- **AC-03.4** A Manager can list users with email, roles and active status. No response ever contains a password or password hash.
- **AC-03.5** A Manager can change a user's roles (at least one role). The new roles apply from that user's next sign-in.
- **AC-03.6** A Manager can deactivate a user. From then on that user cannot sign in, and any request carrying a token issued to them before deactivation is rejected with `401`. A Manager can reactivate the user. Users are never deleted, so their name stays on every movement and order they touched.
- **AC-03.7** A Manager can set a new password for another user; the policy applies.
- **AC-03.8** Removing the Manager role from, or deactivating, the last active Manager is rejected with `409` "At least one active Manager is required."
- **AC-03.9** A user without the Manager role calling any user-management endpoint (every action in US-03) gets `403`. Changing one's own password (US-04) is not user management: it is open to every signed-in role (section 4.2).

### US-04: Change own password

As a staff member, I want to change my own password so that only I know it.

- **AC-04.1** A signed-in user who gives the correct current password and a new password that meets the policy gets a success response. The old password no longer signs in; the new one does.
- **AC-04.2** A wrong current password is rejected with `400` "Current password is incorrect." The password does not change.

### US-05: Role checks and audit trail

As a Manager, I want every action limited to the right roles and attributed to a person so that I can audit what happened.

- **AC-05.1** Each action in the table in section 4.2 succeeds for users holding a role marked "yes" and is rejected with `403` for users holding none of them.
- **AC-05.2** A user with several roles can do every action allowed to any of them. Example: a user with Warehouse and Production can both send materials and complete an order.
- **AC-05.3** Every movement, every order and complementary order status change, every return status change, and (v1.1) every sales order status change records the user who did it and the UTC time. Reading the record returns both.

### US-06: Units of measure

As a Manager, I want to maintain the list of units so that every item is measured consistently.

- **AC-06.1** A Manager creates a unit with a name of 1 to 50 characters (leading and trailing spaces removed). Response `201`. All signed-in users can list units.
- **AC-06.2** A name that already exists, compared without regard to case, is rejected with `409`. An empty name is rejected with `400`.
- **AC-06.3** A Manager can rename a unit, under the same rules.
- **AC-06.4** A Manager can delete a unit that no item uses. Deleting a unit used by an item is rejected with `409` "Unit is used by one or more items."

### US-07: Items

As a Manager, I want to maintain raw materials and finished products so that orders, recipes and stock refer to the same items.

- **AC-07.1** A Manager creates an item with a name (required, unique without regard to case), an optional description, a type (RawMaterial or FinishedProduct) and an existing unit. Response `201`.
- **AC-07.2** A missing name, an unknown type or an unknown unit is rejected with `400`. A duplicate name is rejected with `409`.
- **AC-07.3** Name and description can be changed at any time. Type and unit can be changed only while the item has no movements, recipe lines, order lines or (v1.1) sales order lines; otherwise the change is rejected with `409` "Type and unit cannot change once the item is in use."
- **AC-07.4** An item can be deleted only if it has no movements, recipe lines, order lines or (v1.1) sales order lines; otherwise the delete is rejected with `409`.
- **AC-07.5** All signed-in users can list and view items; each item shows its type and unit.

### US-08: Recipes

As a Manager, I want to define how much of each raw material one unit of a finished product needs so that orders are planned from it.

- **AC-08.1** A Manager sets the recipe of a FinishedProduct as a list of lines, each with a RawMaterial item and an amount per 1 unit of finished product, expressed in the raw item's unit. Saving replaces the whole previous list. Viewing the recipe returns each line with its unit.
- **AC-08.2** Rejected with `400`: a recipe for an item of type RawMaterial; a line whose item is a FinishedProduct; the same raw item on two lines; an amount of zero or less; an amount with more than 6 decimal places; a recipe with no lines.
- **AC-08.3** Example: the recipe of "Shredded cheese 1 kg bag" (unit Bag) is 1 kg of Cheese (unit kg). An order for 1,000 bags plans 1,000 kg of Cheese.
- **AC-08.4** Changing a recipe does not change any existing order, in any status: neither its planned amounts nor the amount per unit used for RequiredForActualOutput. Only orders created after the change use the new recipe. Example: order #1 is created for 1,000 bags at 1 kg per bag; the recipe is then changed to 1.01 kg per bag. Order #1 still shows 1,000 kg planned and, when completed with 1,000 bags, RequiredForActualOutput 1,000 kg. A new order for 1,000 bags shows 1,010 kg planned.

### US-09: Record a stock receipt

As a warehouse user, I want to record raw materials arriving so that warehouse stock is correct.

- **AC-09.1** A Warehouse or Manager user records a receipt with a raw material item, a quantity greater than zero, a receipt date, and an optional note of up to 500 characters. A Received movement adds the quantity to Warehouse stock and stores the date, note and user.
- **AC-09.2** Rejected with `400`: quantity zero or less; more than 3 decimal places; missing or unknown item; a receipt date later than today (UTC); a note longer than 500 characters.
- **AC-09.3** A receipt for a FinishedProduct is rejected with `400` "Receipts are for raw materials only."
- **AC-09.4** Example: Cheese in Warehouse is 200 kg; a receipt of 1,000 kg makes it 1,200 kg. Production storage is unchanged.

### US-10: View current stock

As any staff member, I want to see what is in the warehouse and in production storage so that I know what is available.

- **AC-10.1** Any signed-in user can get, for each item, its Warehouse stock and its Production storage stock, with the unit. Each value equals the sum of that item's movements in that location (D-2).
- **AC-10.2** An item with no movements shows 0 in both locations.
- **AC-10.3** For each raw material, the response also shows: material committed to the InProduction order (D-3), quantity in Pending returns, and free leftover (D-4).
- **AC-10.4** The result can be requested for one item; an unknown item returns `404`.
- **AC-10.5** (v1.1) For every item, raw material or finished product, the response also shows its open demand (D-12, US-31). It is information only and changes no other figure in this story.

### US-11: Movement history

As a Manager, I want a history of every stock change so that I can audit what happened.

- **AC-11.1** Any signed-in user ((v1.1) except a user whose only role is Seller, who gets `403`, AC-30.6) can list movements. Each shows item, location and signed quantity (both locations for a transfer), type, UTC time, user, linked order or complementary order if any, (v1.1) linked sales order if any, and note or reason if any.
- **AC-11.2** The list can be filtered by item, location, movement type, order, (v1.1) sales order, and a date range (from and to, both included). Filters combine. No matches returns `200` with an empty list.
- **AC-11.3** No endpoint edits or deletes a movement.
- **AC-11.4** For any item and location, the sum of the listed movement quantities equals the stock shown in US-10.

### US-12: Adjustments and write-offs

As a Manager, I want to correct stock after a physical count or write off spoiled material, with a reason, so that stock matches reality and the reason is on record.

- **AC-12.1** A Manager records an adjustment with an item, a location, a non-zero signed quantity and a reason of 1 to 500 characters. An Adjustment movement changes that location's stock by the quantity.
- **AC-12.2** A zero quantity, a missing or blank reason, or more than 3 decimal places is rejected with `400`.
- **AC-12.3** A negative adjustment larger than the stock is rejected with `409` naming the item, location and available stock. Example: Cheese in Production storage is 12 kg. An adjustment of −13 kg is rejected ("Not enough stock: Cheese in Production storage has 12 kg."); an adjustment of −12 kg is accepted and leaves 0 kg.
- **AC-12.4** Write-off example: 20 kg of Cheese leftover in Production storage has spoiled. An adjustment of −20 kg in Production storage with reason "Spoiled" leaves 0 kg, and the next order's leftover estimate for Cheese drops by 20 kg.
- **AC-12.5** An adjustment in Production storage of a material on the order that is currently InProduction is rejected with `409` "Cheese in production storage is in use by order #12. Adjust it after the order is completed or cancelled."
- **AC-12.6** An adjustment of a FinishedProduct in Production storage is rejected with `400`. Finished products are adjusted in the Warehouse only.
- **AC-12.7** A user without the Manager role gets `403`.

### US-13: Create a production order

As a Manager, I want to create a production order from a recipe so that the warehouse knows what to send and I can see how much leftover will be used.

- **AC-13.1** A Manager creates an order with a FinishedProduct and an ordered quantity greater than zero. Response `201`. The order gets the next order number, status Created, and records who created it and when. It has one line per recipe line with the raw item, the amount per unit copied from the recipe, and planned amount = amount per unit × ordered quantity.
- **AC-13.2** Each line of a Created order shows, labelled as an estimate: estimated leftover applied (D-5), estimated nominal send (planned − estimated leftover), and the estimated accepted send range (D-7). The estimate is recalculated each time the order is read while it is Created.
- **AC-13.3** Rejected with `400`: an item that is not a FinishedProduct; a finished product with no recipe; an ordered quantity of zero or less or with more than 3 decimal places.
- **AC-13.4** Creating an order moves no stock.
- **AC-13.5** An order can be created while another order is InProduction, and several orders can be Created at the same time.
- **AC-13.6** No endpoint changes an order's finished product, ordered quantity or planned amounts after creation.
- **AC-13.7** A user without the Manager role gets `403`.

### US-14: View orders

As any staff member, I want to see orders and their details so that I know what to send, produce or investigate.

- **AC-14.1** Any signed-in user ((v1.1) except a user whose only role is Seller, who gets `403`, AC-30.6) can list orders, optionally filtered by status (Created, InProduction, Completed, Cancelled).
- **AC-14.2** The order detail shows: order number, finished product, ordered quantity, status, user and time of each status change, and per line: raw item, unit, amount per unit, planned amount, leftover applied, warehouse send, complementary sends, carried leftover, leftover entered at completion, ActualUsage, RequiredForActualOutput, usage variance and usage variance %. It also shows produced quantity, output variance and output variance %, the tolerance in force at completion, the completion or cancellation reason, and its complementary orders with their status. Values not yet known are empty.
- **AC-14.3** An unknown order number returns `404`.

### US-15: Send materials to production

As a warehouse user, I want to send the materials for an order in one go, net of the leftover already in production storage, so that production gets what it needs without double counting.

- **AC-15.1** A Warehouse or Manager user submits one send for a Created order, giving a quantity of zero or more for every line of the order. If every quantity is within its send range (D-7) and Warehouse stock covers every quantity: the leftover applied per line is fixed (D-6); for each line with a quantity above zero, a SentToProduction movement moves that quantity from Warehouse to Production storage, linked to the order and the user; the order becomes InProduction; who sent and when are recorded.
- **AC-15.2** Send range, no leftover. Planned 1,000 kg, leftover applied 0. Sends of 980, 1,000 and 1,020 kg are accepted. Sends of 979.999 and 1,020.001 kg are rejected.
- **AC-15.3** Leftover netting. Production storage holds 5 kg of free Cheese leftover; planned 1,000 kg. Leftover applied is 5 kg, nominal send 995 kg, accepted range 975 to 1,015 kg. Sending 995 kg is accepted and Production storage then holds 1,000 kg of Cheese. Sends of 974.999 and 1,015.001 kg are rejected.
- **AC-15.4** Leftover covers the need. Production storage holds 50 kg of free Cheese leftover; planned 30 kg. Leftover applied is 30 kg, nominal send 0 kg, accepted range 0 to 0.6 kg. A send of 0 kg is accepted: no movement is created, the order becomes InProduction, Production storage still holds 50 kg, and the 20 kg not applied is carried leftover (D-8) that stays put and remains free leftover for later orders. A send of 1 kg is rejected.
- **AC-15.5** Oldest order first. Production storage holds 50 kg of free Cheese leftover; no order is InProduction. Order #7 (created 08:00, planned 30 kg), #8 (created 09:00, planned 1,000 kg) and #9 (created 10:00, planned 500 kg) are Created.
  - Estimates: #7 leftover 30, nominal send 0, range 0 to 0.6. #8 leftover 20, nominal send 980, range 960 to 1,000. #9 leftover 0, nominal send 500, range 490 to 510.
  - If #8 is sent before #7, its leftover applied is 20 kg (not 50), because 30 kg is allocated to the older #7. The 30 kg remains free leftover and stays allocated to #7.
  - If #7 is cancelled while Created, its allocation is released: #8 then shows leftover 50, nominal send 950, range 930 to 970.
- **AC-15.6** If any line is outside its range, the whole send is rejected with `400`, and the message names each failing line, up to the 100-problem limit (row 21), with its range, for example "Cheese: send must be between 975 and 1,015 kg (planned 1,000 kg, leftover applied 5 kg)." No movement is created and the status stays Created.
- **AC-15.7** If Warehouse stock is less than the quantity for any line, the whole send is rejected with `409` naming the item and the available stock.
- **AC-15.8** Rejected with `400`: a missing line, an item that is not on the order, a negative quantity, more than 3 decimal places.
- **AC-15.9** A send for an order that is not Created is rejected with `409`. For an InProduction or Completed order the message is "Materials have already been sent for this order. Raise a complementary order for more material."; for a Cancelled order, "This order is cancelled."
- **AC-15.10** A send while another order is InProduction is rejected with `409` "Order #N is in production. Only one order can be in production at a time."
- **AC-15.11** A send is rejected with `409` while a Pending return (US-22) exists for any material on the order, with the message "Return #R of Cheese is waiting to be received by the warehouse. Record it first."
- **AC-15.12** A user with only the Production role gets `403`.

### US-16: Request a complementary order

As a production user, I want to ask for more material for the running order, with a reason, so that the line does not stop and the extra use is on record.

- **AC-16.1** A Production or Manager user raises a complementary order for an InProduction order with one or more lines (raw item and requested amount greater than zero) and a reason of 1 to 500 characters. Response `201`. It gets its own number, status PendingApproval, a link to the parent order, and the requesting user and time. A ComplementaryAwaitingApproval notification is created (US-23).
- **AC-16.2** A request for an order that is not InProduction is rejected with `409`.
- **AC-16.3** Rejected with `400`: missing or blank reason; no lines; an amount of zero or less or with more than 3 decimal places; a raw item that is not on the parent order; the same item twice.
- **AC-16.4** A parent order can have several complementary orders; each has its own approval and its own send.
- **AC-16.5** A complementary order has no ordered or produced quantity and never creates a ProducedOutput movement.
- **AC-16.6** A user with only the Warehouse role gets `403`.

### US-17: Approve or reject a complementary order

As a Manager, I want to approve or reject requests for extra material so that no extra material leaves the warehouse without my decision.

- **AC-17.1** A Manager approves a PendingApproval complementary order. It becomes Approved, recording who and when.
- **AC-17.2** A Manager rejects a PendingApproval complementary order, with an optional note. It becomes Rejected and can never be sent.
- **AC-17.3** Approving or rejecting a complementary order that is not PendingApproval is rejected with `409`.
- **AC-17.4** A user without the Manager role gets `403`.

### US-18: Send material for a complementary order

As a warehouse user, I want to send the approved extra material so that production can continue.

- **AC-18.1** A Warehouse or Manager user submits one send for an Approved complementary order with a quantity for every line. Each quantity must be within requested amount × (1 − t) to requested amount × (1 + t), limits included; no leftover is netted. If accepted and Warehouse stock covers every quantity, a SentToProduction movement per line moves the quantity from Warehouse to Production storage, linked to the complementary order and its parent; the complementary order becomes Sent; the quantity is added to the parent line's complementary sends.
- **AC-18.2** Example: requested 20 kg. Sends of 19.6, 20 and 20.4 kg are accepted; 19.599 and 20.401 kg are rejected with `400` stating the range.
- **AC-18.3** A send for a complementary order that is PendingApproval, Rejected, Sent or Cancelled is rejected with `409` stating its status.
- **AC-18.4** Insufficient Warehouse stock is rejected with `409` naming the item and available stock.
- **AC-18.5** A user with only the Production role gets `403`.

### US-19: Complete an order

As a production user, I want to register the output and the leftover once, at the end, so that finished goods enter the warehouse and the usage can be checked.

- **AC-19.1** A Production or Manager user completes an InProduction order by giving: produced quantity (greater than zero), the leftover remaining in production storage for every line (zero or more; this is the total quantity of that material physically in production storage), and a reason. The reason (1 to 500 characters) is required when any leftover entered is above zero.
- **AC-19.2** On success: the order becomes Completed, recording who and when; a ProducedOutput movement adds the produced quantity of the finished product to Warehouse stock; for each line, a ConsumedInProduction movement removes (production storage stock just before completion − leftover entered) from Production storage, or no movement if that is zero; variances are calculated and recorded (US-20); notifications are created (US-20, AC-19.7).
- **AC-19.3** After completion, Production storage stock of each line's material equals the leftover entered. That leftover stays in production storage and is free leftover for later orders.
- **AC-19.4** Rejected with `400`: produced quantity zero or less; a missing line; a negative leftover; more than 3 decimal places; a leftover above zero with no reason ("A reason is required when leftover is recorded."); a leftover larger than that material's production storage stock ("Leftover for Cheese cannot exceed the 1,020 kg in production storage.").
- **AC-19.5** Completing an order that is not InProduction is rejected with `409`. Output is registered once only: a second completion of the same order is rejected.
- **AC-19.6** On completion, the order's complementary orders that are PendingApproval or Approved become Cancelled with the note "Parent order completed".
- **AC-19.7** When any leftover entered is above zero, one LeftoverAtCompletion notification is created listing every material with leftover above zero (amount and unit) and the reason.
- **AC-19.8** A user with only the Warehouse role gets `403`.

### US-20: Output and usage variance

As a Manager, I want differences between plan, use and output recorded and flagged so that I can investigate waste.

- **AC-20.1** Output variance and its percentage (D-9) are recorded on every completed order, including when they are zero.
- **AC-20.2** When the output variance is not zero, one OutputVariance notification is created with ordered quantity, produced quantity, signed difference and percentage. When it is zero, none is created.
- **AC-20.3** For every line, RequiredForActualOutput, carried leftover, LeftoverRemaining, ActualUsage, usage variance and usage variance % (D-9) are recorded on the order.
- **AC-20.4** For each line whose absolute usage variance % is greater than t, one UsageVariance notification is created for that material with RequiredForActualOutput, ActualUsage, signed difference and percentage. A line at exactly t (for example exactly 2.00%) creates none; a line at 2.0001% creates one (shown as 2.00%).
- **AC-20.5** Worked example A (within tolerance). Order #12: 1,000 bags, recipe 1 kg Cheese per bag, planned 1,000 kg. Free leftover 5 kg, so leftover applied 5 kg, carried leftover 0. Warehouse send 995 kg. Complementary order requested and sent 20 kg. Production storage before completion: 1,020 kg. Completion: produced 1,001 bags, leftover entered 4 kg, reason "Last bag line jammed".
  - RequiredForActualOutput = 1 × 1,001 = 1,001 kg.
  - ActualUsage = 5 + 995 + 20 − 4 = 1,016 kg. ConsumedInProduction movement: −1,016 kg. Production storage after: 4 kg.
  - Usage variance = +15 kg, +1.50% (1.4985...). Not above 2%: no UsageVariance notification.
  - Output variance = +1 bag, +0.10%: one OutputVariance notification.
  - Leftover 4 kg above zero: one LeftoverAtCompletion notification. Total: 2 notifications.
- **AC-20.6** Worked example B (above tolerance). As example A, but the complementary send is 30 kg (requested 30 kg) and leftover entered is 0 kg, no reason. ActualUsage = 5 + 995 + 30 − 0 = 1,030 kg. Usage variance = +29 kg, +2.90%: one UsageVariance notification. Output variance +1 bag: one OutputVariance notification. No LeftoverAtCompletion notification.
- **AC-20.7** Worked example C (under-use). Planned 1,000 kg, leftover applied 0, send 1,000 kg, no complementary order, produced 1,000 bags, leftover entered 25 kg. ActualUsage = 975 kg; usage variance −25 kg, −2.50%: one UsageVariance notification. Output variance 0: no OutputVariance notification. One LeftoverAtCompletion notification.
- **AC-20.8** Worked example D (carried leftover). Continuing AC-15.5: #8 was sent first with leftover applied 20 kg and a send of 980 kg, so carried leftover is 50 − 20 = 30 kg and production storage before completion is 1,030 kg. #8 completes with 1,000 bags produced and 30 kg leftover entered. LeftoverRemaining = 30 − 30 = 0; ActualUsage = 20 + 980 − 0 = 1,000 kg; usage variance 0. ConsumedInProduction −1,000 kg; production storage after: 30 kg, which is again allocated to #7.
- **AC-20.9** No movement is created to correct a variance. Stock changes at completion are only those in AC-19.2.
- **AC-20.10** The tolerance in force at completion is stored on the order. Changing the setting later does not change any recorded value or notification.

### US-21: Cancel an order

As a Manager, I want to cancel an order that will not be produced, with a reason, so that it no longer holds leftover or blocks the line.

- **AC-21.1** A Manager cancels a Created or InProduction order with a reason of 1 to 500 characters. The order becomes Cancelled, recording who, when and the reason.
- **AC-21.2** Cancelling a Created order moves no stock and releases its leftover allocation to the next oldest Created order (see AC-15.5).
- **AC-21.3** Cancelling an InProduction order moves no stock. All material sent for it stays in Production storage as free leftover, and another order can then be sent. If at least one material on the order has stock above zero in Production storage, one OrderCancelledWithMaterial notification is created listing each such material with its production storage quantity, and the reason.
- **AC-21.4** On cancellation, the order's complementary orders that are PendingApproval or Approved become Cancelled with the note "Parent order cancelled".
- **AC-21.5** Cancelling a Completed or Cancelled order is rejected with `409`. A missing or blank reason is rejected with `400`.
- **AC-21.6** A user without the Manager role gets `403`.

### US-22: Return leftover to the warehouse

As a Manager, I want to send leftover back to the warehouse so that it is available for other uses and no longer counts against the next order.

- **AC-22.1** A Manager instructs a return with a raw material item, a quantity greater than zero and a reason of 1 to 500 characters, optionally linked to an order (for example, a cancelled one). Response `201`; the return is Pending. No stock moves yet, but the quantity is no longer free leftover (D-4).
- **AC-22.2** A quantity greater than the item's free leftover is rejected with `409` stating the free leftover. A missing or blank reason is rejected with `400`.
- **AC-22.3** A return of a material on the order that is currently InProduction is rejected with `409`.
- **AC-22.4** A Warehouse or Manager user records receiving a Pending return. A ReturnedToWarehouse movement moves the quantity from Production storage to Warehouse, and the return becomes Received, recording who and when. Example: 20 kg of Cheese leftover; a return of 20 kg is received; Production storage holds 0 kg, Warehouse gains 20 kg, and the next order's leftover estimate for Cheese is 0.
- **AC-22.5** A Manager can cancel a Pending return; it becomes Cancelled and the quantity is free leftover again. Receiving or cancelling a return that is not Pending is rejected with `409`.
- **AC-22.6** Instructing a return (AC-22.1) and cancelling a return (AC-22.5) are Manager only; recording receipt of a return (AC-22.4) is Warehouse or Manager. A Production-only user gets `403` for instructing, receiving and cancelling; a Warehouse-only user gets `403` for instructing and cancelling. Listing and viewing returns are open to every signed-in role (section 4.2) and do not return `403` for any role ((v1.1) except Seller: a user whose only role is Seller gets `403`, AC-30.6).

### US-23: Notifications

As a Manager, I want to see a list of events that need my attention, and track which ones I have read, so that nothing is missed.

- **AC-23.1** A Manager lists notifications. Each shows: id, type, order number (and complementary order number where relevant), UTC creation time, message, the structured values listed below, and whether the calling Manager has read it. The list can be filtered to unread only.
- **AC-23.2** There are exactly five types, created only by these events:

  | Type | Created when | Structured values (also in the message) |
  |------|--------------|------------------------------------------|
  | UsageVariance | AC-20.4 | material, unit, RequiredForActualOutput, ActualUsage, signed difference, percentage |
  | OutputVariance | AC-20.2 | ordered, produced, unit, signed difference, percentage |
  | LeftoverAtCompletion | AC-19.7 | each material with leftover amount and unit, reason |
  | ComplementaryAwaitingApproval | AC-16.1 | complementary order number, each material with requested amount and unit, reason, requesting user |
  | OrderCancelledWithMaterial | AC-21.3 | each material with quantity in production storage and unit, reason |

  Example messages: "Order #12, Cheese: used 1,030 kg, required 1,001 kg for actual output. Difference +29 kg (+2.90%)." and "Order #12: produced 1,001 Bag, ordered 1,000 Bag. Difference +1 Bag (+0.10%)." Wording may differ; the structured values are what QA checks.
- **AC-23.3** Read status is per Manager. When Manager A marks a notification read, A sees it as read and Manager B still sees it as unread. A Manager can also mark all notifications read for themselves.
- **AC-23.4** A Manager created after a notification exists sees that notification, as unread.
- **AC-23.5** Notifications are never deleted. An unknown notification id returns `404`.
- **AC-23.6** A user without the Manager role gets `403` for every notification endpoint.

## 5. Non-functional requirements

- **Platforms:** an HTTP API exchanging JSON, with no user interface. It must build and run on the owner's Windows 11 machine using the documented commands. It publishes an OpenAPI description listing every endpoint, at least in the development environment. (v1.2) The web front end's platforms are in section 18.
- **Performance:** with 100,000 movements, 1,000 orders and 500 items in the database, and (v1.1) 500 customers and 5,000 sales orders of 1 to 10 lines each, 95% of requests (the shipped-totals reports of US-32 and US-33 included) answer within 1 second on the owner's machine.
- **Consistency under concurrent use:** when two requests that conflict arrive at the same time (two sends for the same order, sends for two different orders, two movements that together would take stock below zero, and (v1.1) two shipments of the same sales order, a shipment and a cancellation of the same sales order, or deleting a customer while a sales order is created for it), at most one succeeds and stock never goes below zero in either location. Every request that changes data either completes fully or saves nothing.
- **Security:** every endpoint except sign-in requires a valid token. Passwords are stored only as salted hashes. Password policy: at least 8 characters with an upper-case letter, a lower-case letter and a digit. The token signing key, `ADMIN_EMAIL` and `ADMIN_PASSWORD` come from environment variables, never from committed files.
- **Configuration:** the tolerance is a percentage in application settings, default 2. A value below 0 or above 100 stops the application at start-up with a message.
- **Accessibility:** not applicable in version 1 (no user interface). (v1.2) The web front end's accessibility requirements are in section 18.
- **Data:** stored in a SQL database (SQLite in version 1). Movements, orders, complementary orders, returns, (v1.1) sales orders, notifications and users are kept indefinitely and never deleted (users are deactivated). (v1.1) Customers are kept indefinitely too; only a customer that never had any sales order can be deleted (AC-29.5). Everything is visible to signed-in users according to section 4.2.
- **Language:** English for all messages and field names. Times in UTC.

## 6. Assumptions and open questions

Rows 1 to 4 were questions for the owner; the owner confirmed each default on 2026-11-11. Rows 5 to 18 are assumptions made on the owner's behalf; each can be overruled. Rows 19 to 22 record decisions made during the build and the security review.

| # | Assumption or question | Decided by | Answer |
|---|------------------------|------------|--------|
| 1 | QUESTION. At completion, does the production user enter the total quantity physically in production storage, or only what is left from this order? The agreed formula (applied + sends − leftover remaining) gives a wrong result when carried leftover exists: in the 50 kg / 30 kg case, a literal "remaining 20 kg" gives usage 10 kg instead of 30 kg. Default: the user enters the physical total; the system subtracts carried leftover (D-8, D-9, AC-20.8). | Owner | Confirmed by owner, 2026-11-11. |
| 2 | QUESTION. Can a newer order be sent before an older Created order, and if so, may it use the leftover allocated to the older one? Default: yes it can be sent, but it only uses leftover not allocated to older Created orders (D-5, AC-15.5). Alternative: force orders to be sent in creation order. | Owner | Confirmed by owner, 2026-11-11. |
| 3 | QUESTION. Leftover returns are two steps: Manager instructs (Pending, quantity stops counting as leftover), Warehouse records receipt (stock moves). Sends are blocked while a return of one of the order's materials is Pending; returns and production-storage adjustments are blocked for materials of the InProduction order. These blocks keep the usage calculation exact. | Owner | Confirmed by owner, 2026-11-11. |
| 4 | QUESTION. "Usage variance above 2%" is read as above 2% in either direction (over-use and under-use both notify, AC-20.7). Alternative: over-use only. | Owner | Confirmed by owner, 2026-11-11. |
| 5 | The same global tolerance sets the send ranges and the usage threshold. Limits are inclusive: exactly 2% is accepted for a send and does not notify. | PM | Assumed |
| 6 | Complementary orders can be raised only while the parent is InProduction, only for materials already on the parent order. Production users normally raise them (owner, 2026-11-11); a Manager can also raise one and may approve a request they raised themselves. Managers can also reject requests. Unsent complementary orders are cancelled automatically when the parent is completed or cancelled. | PM, confirmed by owner | Confirmed 2026-11-11 |
| 7 | Produced quantity must be greater than zero. For a run with no usable output, the Manager cancels the order and writes off the material. | PM | Assumed |
| 8 | One completion reason covers all materials. One LeftoverAtCompletion notification per completion; one UsageVariance notification per material over tolerance. | PM | Assumed |
| 9 | Cancelling an order requires a reason. | PM | Assumed |
| 10 | Receipts are for raw materials only, into the Warehouse, with a date not in the future. Finished products are adjusted in the Warehouse only. | PM | Assumed |
| 11 | Orders cannot be edited after creation; cancel and recreate. | PM | Assumed |
| 12 | Precision: 3 decimal places for quantities, 6 for recipe amounts, computed values rounded half away from zero to 3 places, percentages shown to 2 places but compared unrounded. | PM | Assumed |
| 13 | Tokens last 8 hours with no refresh; lock-out after 5 failed sign-ins for 15 minutes; password policy as in section 5; users can change their own password. | PM | Assumed |
| 14 | All signed-in users can read master data, stock, movement history and orders. Only Managers read notifications. (v1.1) Superseded in part by decision #147: a Seller reads only units, items, stock, customers, sales orders and the shipped reports; Production cannot read customers, sales orders or the shipped reports (section 4.2). | PM | Assumed; (v1.1) amended by owner 2026-11-23 |
| 15 | Deactivation takes effect immediately; role changes take effect at the user's next sign-in. | PM | Assumed |
| 16 | A Manager added later sees all earlier notifications as unread. | PM | Assumed |
| 17 | The consumption movement type is named ConsumedInProduction. No movement is created for a zero quantity. | PM | Assumed |
| 18 | The application refuses to start if there are no users and `ADMIN_EMAIL` or `ADMIN_PASSWORD` is missing. | PM | Assumed |
| 19 | Owner decision during the build (2026-11-11): when RequiredForActualOutput rounds to 0, the completion succeeds; the usage variance percentage is empty (null) and the line is above tolerance whenever any of that material was used. The UsageVariance notification then shows the difference with "(percentage not applicable)" instead of a percentage. See decisions #49 and #57. | Owner | Decided 2026-11-11 |
| 20 | Owner decision during the build (2026-11-13): every entered quantity is at most 999,999,999.999 in size (so a negative adjustment is at least -999,999,999.999), and a recipe amount per unit is at most 999,999.999999. Above the limit is refused with a validation error. See decision #80. | Owner | Decided 2026-11-13 |
| 21 | Decision during the security review (2026-11-21): request limits. A request body is at most 1 MB. A recipe, and every body that carries lines (send, completion, complementary request, complementary send), has at most 500 lines. Creating a user or changing their roles takes at most 10 roles in the list. A request over any of these three limits is refused and nothing is saved. A `400` response lists at most 100 problems and then says how many more were not shown. Status codes, messages and the order of checks are in `docs/02-design.md` section 7.2. See decisions #122 to #125. | Delivery manager | Decided 2026-11-21 |
| 22 | Owner decision during the security review (2026-11-21): version 1 runs on localhost only, on the owner's machine, so plain HTTP is acceptable. HTTPS (with HSTS) is required before the API is reachable from any other machine. There is no sign-in rate limit; the lock-out of AC-01.4 stays. Both are on the Later list. See decisions #119 and #120. | Owner | Decided 2026-11-21 |

---

# Version 1.1: Sales orders

Status: v1.1 approved by the owner on 2026-11-23. Chosen by the owner on 2026-11-23 as the first item from the Later list. Owner's words in the v1 interview (Q6): "For v1 is out of scope, a future version will add a Sales order table, which is when the stock is removed." The owner settled every question on 2026-11-23, in answers and a grilling session: decisions #138 to #150. No question is open (section 13).

Everything in sections 1 to 6 still applies to version 1.1 unless this part says otherwise: the conventions in 4.1 (including what "rejected" means, status codes, quantities, times and lists), the roles in 4.2, the terms in 4.3, the non-functional requirements in section 5, and the limits in section 6 rows 20 to 22.

## 7. Problem and users

In version 1, finished products enter Warehouse stock when a production order is completed, but nothing takes them out again except a Manager's adjustment. The same is true of raw material the business sells on (for example surplus cheese). When goods are delivered to a customer, the Manager has to write a negative adjustment with a reason such as "Sold to Deli Norte". Sales are then mixed in with count corrections and write-offs, only a Manager can record them, nothing records what a customer ordered before it was shipped, and there is no way to see how much was sold per product or per customer, or how much customers are still waiting for. Stock is therefore either wrong (shipments not recorded) or hard to read (shipments hidden among adjustments).

**Primary user:** the seller, who keeps the customer list, takes customers' orders and wants to know what has been shipped to whom and what customers are still waiting for.

**Other users:** warehouse staff, who pick and ship the customer's order and record that it has left; the production and inventory manager, who needs stock to match the shelves and uses open demand to decide what to produce.

## 8. Goal for version 1.1

A Seller records what a customer from the customer list ordered, and the warehouse records the shipment, which removes the items from Warehouse stock with a movement type of its own linked to the sales order; stock then matches the shelf, every item that leaves is traced to a customer order, every role sees open demand per item on the stock view, and Sellers, Warehouse and Managers see totals shipped per item and per customer.

## 9. Scope

In version 1.1:

- A fourth role, Seller, given only by a Manager like the other roles (decisions #138, #146).
- Role-based reading for the first time: a Seller reads only units, items, stock, customers, sales orders and the shipped reports; Production cannot read customers, sales orders or the shipped reports (decision #147).
- A customer list kept by Sellers and Managers: unique name, optional notes, deactivate and reactivate, and delete for a customer that never had a sales order (decisions #139, #148).
- Sales orders created by a Seller or Manager for an active customer from the list, with an optional customer reference and one or more lines of any item, finished product or raw material, with quantities (decision #144).
- One shipment per sales order, recorded by Warehouse or Manager, shipping every line in full from Warehouse stock only. Refused as a whole when Warehouse stock does not cover every line (decisions #140, #141, #149).
- Cancellation of any unshipped sales order, by a Seller or Manager, with a reason (decision #146).
- A new movement type, ShippedToCustomer, shown in the movement history and filterable by sales order.
- Listing and viewing sales orders, for Warehouse, Manager and Seller, filtered by status, item and customer.
- Open demand per item on the stock view, for every role, as information only (decisions #145, #150).
- Two reports: totals shipped per item, and totals shipped per customer and item, for a date range (decisions #145, #150).

Out of scope for version 1.1 (non-goals):

- Shipping part of a sales order, shipping less than ordered, more than one shipment per sales order, and backorders. If the customer accepts less, the sales order is cancelled and a new one created for the smaller quantity (decision #141).
- Reserving or allocating stock to sales orders. Creating a sales order does not check or hold stock; stock is checked only when shipping. Open demand is a figure to read, not a reservation (decisions #140, #145).
- Customer data beyond a name, notes and an active flag: no structured address, contact, tax or credit fields. A customer that has ever had a sales order, in any status, cannot be deleted (decision #148).
- Prices, money totals, currency, taxes, invoices and payments.
- Customer returns and cancelling a shipped sales order. Goods a customer sends back are recorded with a Manager's positive Warehouse adjustment (US-12) whose reason names the sales order (decision #142).
- Editing a sales order after creation. To change it, cancel it and create a new one.
- Shipping from production storage. A raw material's leftover in production storage is never shipped; to sell it, a Manager first returns it to the Warehouse (US-22).
- Holding back, or warning about, Warehouse raw-material stock that Created production orders will need. A shipment may take it; the shipper decides, and the later send is then refused for lack of stock (AC-15.7, decision #149).
- Linking a sales order to a production order, or suggesting production from open demand.
- Delivery dates and carrier details beyond a free-text shipment note.
- New notification types. AC-23.2 stays at exactly five types (decision #143). Notifications stay Manager-only (decision #147).
- Reports beyond the two shipped-totals reports, the open-demand figure, the sales order list and the movement history; no exports.
- Any user interface. Version 1.1 is still an HTTP API only.

Later: the follow-on items are on the Later list in section 3, marked "(v1.1)".

## 10. User stories and acceptance criteria

### 10.1 Terms

These definitions are part of the acceptance criteria, in addition to D-1 to D-9.

- **D-10 Sales order.** A customer's order for items, finished products or raw materials. It has its own number sequence starting at 1, separate from production orders, complementary orders and returns, and is called "sales order #N" in messages so it cannot be confused with production "order #N". Statuses: Created (recorded, not yet shipped), Shipped and Cancelled. A Created sales order can become Shipped (US-26) or Cancelled (US-27); Shipped and Cancelled are final.
- **D-11 Stock available to ship.** For any item, its Warehouse stock (D-2), and nothing else. Production storage stock is never available to ship, and a Pending return (US-22) becomes available only when the Warehouse records receiving it. Created sales orders do not reduce stock available to ship and nothing is reserved. A shipment takes from Warehouse stock whatever put it there (receipts, ProducedOutput, received returns, positive adjustments).
- **D-12 Open demand** of an item = the sum of the quantities of that item on the lines of all Created sales orders. Shipped and Cancelled sales orders do not count. It is 0 when no Created sales order has the item. It is information only: no rule in this document checks or holds stock against it.
- **D-13 Shipped quantity** of an item in a period = the sum of the quantities of that item on the lines of sales orders whose shipment time (UTC) falls in the period. A period is given as dates (`from`, `to`), whole UTC days, both included, as in AC-11.2; either may be left out, and with neither the period is all time. By construction it equals the sum of that item's ShippedToCustomer movements in the period, with the sign reversed. Adjustments, including corrections of a mistaken shipment (AC-28.6), are not part of it.

### US-24: Create a sales order

As a seller, I want to record what a customer has ordered so that the warehouse knows what to ship and every item that leaves can be traced to a customer.

- **AC-24.1** A Seller or Manager creates a sales order with a customer chosen from the customer list (US-29), an optional customer reference (up to 100 characters, for example the customer's purchase order number) and one or more lines, each with an item (RawMaterial or FinishedProduct) and a quantity greater than zero in that item's unit. Response `201`. The sales order gets the next sales order number (D-10), status Created, and records who created it and when. Lines keep the order in which they were entered, and each line shows its item, item type, unit and quantity.
- **AC-24.2** Creating a sales order moves no stock and does not check stock. Example: Warehouse holds 0 Bag of "Shredded cheese 1 kg bag"; a sales order for 600 Bag of it is created with `201`, and Warehouse stock stays 0 Bag.
- **AC-24.3** Rejected with `400`: a missing or unknown customer; a customer reference longer than 100 characters; no lines; a line with a missing or unknown item; a line quantity that is missing, zero or less, has more than 3 decimal places, or is above 999,999,999.999 (row 20); the same item on two lines.
- **AC-24.4** A sales order for a deactivated customer (AC-29.4) is rejected with `409` "Customer Deli Norte is deactivated. Reactivate the customer or choose another one." (with the customer's name).
- **AC-24.5** A sales order with more than 500 lines is rejected with `400` "At most 500 lines are allowed.", like the other bodies that carry lines (row 21). A `400` lists at most 100 problems (row 21).
- **AC-24.6** Several sales orders can be Created at the same time, for the same or different items and customers, and a sales order can be created whatever the status of any production order. A customer can have any number of sales orders.
- **AC-24.7** No endpoint changes a sales order's customer, customer reference or lines after creation.
- **AC-24.8** Raw material example: Pizzeria Lago is an active customer. A sales order for Pizzeria Lago with one line of 120 kg of Cheese (a RawMaterial) is accepted with `201`. A sales order with one line of Cheese and one of "Shredded cheese 1 kg bag" is also accepted.
- **AC-24.9** A user holding neither the Seller nor the Manager role (for example Warehouse only, or Warehouse and Production) gets `403`.

### US-25: View sales orders

As a seller or warehouse user, I want to see sales orders and their details so that I know what to ship and what has been shipped.

- **AC-25.1** A Warehouse, Manager or Seller user can list sales orders, newest first and paged as in 4.1. The list can be filtered by status (Created, Shipped, Cancelled), by item (sales orders with at least one line for that item) and by customer. Filters combine. No matches returns `200` with an empty list.
- **AC-25.2** The sales order detail shows: sales order number, the customer (its id, its current name and whether it is active, AC-29.3), customer reference, status, the user and UTC time of each status change (created, shipped, cancelled), the shipment note, the cancellation reason, and per line: item, item type, unit and quantity. Values not yet known are empty.
- **AC-25.3** An unknown sales order number returns `404`.
- **AC-25.4** A user holding none of the Warehouse, Manager and Seller roles (that is, Production only) gets `403` for listing sales orders and for viewing one (decision #147). Warehouse, Manager and Seller users do not get `403`.

### US-26: Ship a sales order

As a warehouse user, I want to record that a customer's order has left the warehouse so that Warehouse stock goes down and the shipment is on record.

- **AC-26.1** A Warehouse or Manager user ships a Created sales order, optionally with a note of up to 500 characters (for example a delivery note number). No quantities are entered: every line ships its full quantity. If the Warehouse stock of every line's item covers that line's quantity: for each line, a ShippedToCustomer movement removes the line's quantity from Warehouse stock, linked to the sales order and the user; the sales order becomes Shipped; who shipped it, when, and the note are recorded.
- **AC-26.2** Example. Warehouse holds 1,001 Bag of "Shredded cheese 1 kg bag" (the output of worked example A, AC-20.5) and 80 Bag of "Grated cheese 200 g bag". Sales order #1 (customer "Deli Norte") has one line of 600 Bag of Shredded cheese 1 kg bag. Shipping it leaves 401 Bag in the Warehouse and creates one ShippedToCustomer movement of −600 Bag linked to sales order #1. Production storage is unchanged.
- **AC-26.3** If Warehouse stock is less than the quantity of any line, the whole shipment is rejected with `409`. The message names every short line with the item, the quantity on the sales order, the Warehouse stock available and the unit. No movement is created for any line and the status stays Created. Example, continuing AC-26.2: sales order #2 has 500 Bag of Shredded cheese 1 kg bag and 50 Bag of Grated cheese 200 g bag. Shipping it is rejected with a message such as "Not enough stock: Shredded cheese 1 kg bag in Warehouse has 401 Bag; sales order #2 needs 500 Bag." Grated cheese is not named, no movement is created for either line, and the Warehouse still holds 401 Bag and 80 Bag.
- **AC-26.4** Continuing AC-26.3: a production order for Shredded cheese 1 kg bag is completed with 200 Bag produced, so the Warehouse holds 601 Bag. Shipping sales order #2 now succeeds and leaves 101 Bag of Shredded cheese 1 kg bag and 30 Bag of Grated cheese 200 g bag. Sales order #3 for exactly 101 Bag of Shredded cheese 1 kg bag is then shipped and leaves 0 Bag: stock equal to the quantity is enough.
- **AC-26.5** Sales orders can be shipped in any order. An older Created sales order for the same item has no priority and does not block a newer one.
- **AC-26.6** Shipping changes only the Warehouse stock of the items on the sales order (and their open demand, D-12). It does not change production storage stock, committed material (D-3), pending returns, free leftover (D-4), the estimates of Created production orders, or anything recorded on any production order. It is allowed while a production order is InProduction, including for a raw material on that order, because the material committed to it is in production storage.
- **AC-26.7** Shipping a sales order that is not Created is rejected with `409`. For a Shipped sales order the message is "This sales order has already been shipped."; for a Cancelled one, "This sales order is cancelled."
- **AC-26.8** A note longer than 500 characters is rejected with `400`. A note that is blank after removing leading and trailing spaces is saved as no note.
- **AC-26.9** A user holding neither the Warehouse nor the Manager role (for example Production only, Seller only, or Production and Seller) gets `403`.
- **AC-26.10** Requests at the same time:
  - Two shipments of the same sales order: exactly one succeeds; the other is rejected with `409` "This sales order has already been shipped."; there is exactly one ShippedToCustomer movement per line.
  - Warehouse holds 1,000 Bag; sales orders #5 and #6 for 600 Bag each are shipped at the same time: exactly one succeeds, the other is rejected with `409` for lack of stock, and the Warehouse holds 400 Bag.
  - A shipment and a cancellation of the same sales order: exactly one succeeds. The sales order ends either Shipped, with its movements, or Cancelled, with no movement.
  - A shipment and a Manager's negative Warehouse adjustment of the same item that together exceed the stock: at most one succeeds and the stock never goes below zero.
  - A shipment of a raw material and a production send (US-15) or complementary send (US-18) of the same material that together exceed its Warehouse stock: at most one succeeds and the stock never goes below zero.
- **AC-26.11** Raw material from the Warehouse only. Cheese: Warehouse 100 kg, production storage 50 kg of free leftover, no order InProduction. Sales order #8 (Pizzeria Lago) has one line of 120 kg of Cheese.
  - Shipping it is rejected with `409` naming Cheese, 120 kg needed and 100 kg in the Warehouse. The 50 kg in production storage is not counted.
  - A Manager instructs a return of 20 kg of Cheese (US-22). While the return is Pending, shipping is still rejected the same way.
  - The Warehouse records receiving the return: Warehouse 120 kg, production storage 30 kg. Shipping #8 now succeeds: Warehouse 0 kg, production storage still 30 kg, free leftover still 30 kg.
- **AC-26.12** No priority for production. Cheese: Warehouse 1,000 kg; Created production order #20 plans 1,000 kg with no leftover. Shipping a sales order for 100 kg of Cheese succeeds and leaves 900 kg. Order #20's estimate is unchanged; its send of 1,000 kg is then rejected for lack of Warehouse stock (AC-15.7).
- **AC-26.13** A sales order whose customer has been deactivated after the order was created can still be shipped (AC-29.4).

### US-27: Cancel a sales order

As a seller, I want to cancel a sales order that will not be shipped, with a reason, so that the warehouse no longer sees it as work to do, it no longer counts as open demand, and the reason is on record.

- **AC-27.1** A Seller or Manager cancels any Created sales order, whoever created it, with a reason of 1 to 500 characters (decision #146). It becomes Cancelled, recording who, when and the reason. No stock moves. Its lines no longer count in open demand (D-12). This also applies when the customer has been deactivated. Example: a sales order created by a Manager is cancelled by a Seller with `200`.
- **AC-27.2** Cancelling a Shipped sales order is rejected with `409` "This sales order has been shipped and cannot be cancelled." Cancelling a Cancelled one is rejected with `409` "This sales order is already cancelled." A missing or blank reason, or one longer than 500 characters, is rejected with `400`.
- **AC-27.3** A cancelled sales order can never be shipped (AC-26.7).
- **AC-27.4** A user holding neither the Seller nor the Manager role gets `403`.

### US-28: Shipments in stock and movement history

As a Manager, I want shipments to appear in the stock history with their own type so that sales can be told apart from write-offs and corrections, and audited.

- **AC-28.1** ShippedToCustomer is a movement type that lowers Warehouse stock and does not touch production storage (D-2). Each ShippedToCustomer movement shows the item, the Warehouse change (negative), the UTC time, the user and the sales order number. It has no production order, complementary order, return, receipt date, note or reason of its own; the shipment note is on the sales order.
- **AC-28.2** The movement list can be filtered by type ShippedToCustomer and by sales order number, combined with the other filters in AC-11.2. Example: item "Shredded cheese 1 kg bag", type ShippedToCustomer, from 2026-12-01 to 2026-12-31 lists every shipment of that product in December. Filtering by a production order number never returns a ShippedToCustomer movement.
- **AC-28.3** For every item, the Warehouse stock in US-10 equals the sum of its movements, ShippedToCustomer included (AC-11.4 still holds).
- **AC-28.4** Created sales orders change no figure in US-10 other than open demand (AC-10.5). Example: Warehouse holds 1,001 Bag and two Created sales orders ask for 600 Bag each; the stock view still shows 1,001 Bag in the Warehouse (D-11), with open demand 1,200 Bag.
- **AC-28.5** An item on any sales order line, whatever the sales order's status, is in use: its type and unit cannot change and it cannot be deleted (AC-07.3, AC-07.4).
- **AC-28.6** No endpoint edits or deletes a ShippedToCustomer movement (AC-11.3). A shipment recorded by mistake is corrected with a Manager's positive Warehouse adjustment (US-12) whose reason names the sales order; the sales order stays Shipped.
- **AC-28.7** Every role that can list movements (Warehouse, Production, Manager) sees ShippedToCustomer movements, each with its sales order number, and can filter by sales order number (decision #147). A user whose only role is Production sees them in the movement list but gets `403` when opening the sales order itself (AC-25.4). A user whose only role is Seller gets `403` for the movement list (AC-30.6).

### US-29: Customers

As a seller, I want to keep a list of customers so that every sales order names its customer the same way and sales can be totalled per customer.

- **AC-29.1** A Seller or Manager creates a customer with a name (required, 1 to 100 characters) and optional notes (up to 500 characters, for example address and phone). Response `201`. The customer is active and records who created it and when.
- **AC-29.2** A name that already belongs to another customer, compared without regard to case, is rejected with `409` "A customer named 'Deli Norte' already exists." A missing or blank name, a name longer than 100 characters, or notes longer than 500 characters are rejected with `400`.
- **AC-29.3** A Seller or Manager can change a customer's name and notes at any time, under the same rules. Every sales order and report shows the customer's current name, including sales orders created before the change. Example: after "Deli Norte" is renamed "Deli Norte SA", sales order #1 shows "Deli Norte SA".
- **AC-29.4** A Seller or Manager can deactivate and reactivate a customer, whatever its sales orders. A deactivated customer cannot be chosen for a new sales order (AC-24.4). Its existing Created sales orders can still be shipped (AC-26.13) and cancelled (AC-27.1). It still appears, marked inactive, in the customer list, in sales orders and in reports. Deactivating a customer that is already deactivated, or reactivating an active one, succeeds and changes nothing.
- **AC-29.5** A Seller or Manager can delete a customer that has never had a sales order, in any status (decision #148). After the delete, viewing that customer returns `404`, it is not in the customer list, and its name can be used for a new customer. Deleting a customer with at least one sales order, even if every one is Cancelled, is rejected with `409` "Deli Norte has sales orders and cannot be deleted. Deactivate it instead." (with the customer's name); deactivate it instead. Deleting an unknown customer returns `404`. Example: "Pizzeria Lago" is created by mistake and never used; deleting it succeeds. "Deli Norte" has one Cancelled sales order; deleting it is rejected with `409`.
- **AC-29.6** A Warehouse, Manager or Seller user can list customers, newest first and paged as in 4.1, optionally filtered by active status, and view one customer. Each shows id, name, notes, active status, and who created it and when. An unknown customer id returns `404`.
- **AC-29.7** A user holding neither the Seller nor the Manager role gets `403` for creating, editing, deactivating, reactivating and deleting a customer. A user holding none of the Warehouse, Manager and Seller roles (that is, Production only) also gets `403` for listing customers and viewing one (decision #147).
- **AC-29.8** A delete of a customer and the creation of a sales order for that customer at the same time: exactly one succeeds. Either the customer is deleted and the sales order is rejected (unknown customer, `400`), or the sales order is created and the delete is rejected with `409`. No sales order ever refers to a customer that does not exist.

### US-30: Seller role

As a Manager, I want to give sales staff a Seller role so that they can keep customers and take sales orders without being given Manager rights.

- **AC-30.1** Seller is a fourth role beside Warehouse, Production and Manager. Only a Manager can give it, when creating a user (AC-03.1) or changing a user's roles (AC-03.5), alone or with any of the other roles (decision #146); a Seller gets `403` for every user-management action (AC-03.9). Role names are accepted in any letter case, as for the other roles. An unknown role is still rejected with `400` (AC-03.3), and the message lists all four roles.
- **AC-30.2** Signing in as a user with the Seller role returns "Seller" among the roles (AC-01.1).
- **AC-30.3** Upgrade. When version 1.1 starts on a version 1 database, the Seller role exists afterwards, every existing user keeps exactly the roles they had, no user holds Seller until a Manager gives it, and no other data changes. On an empty database, the first user is still a Manager only (AC-02.1).
- **AC-30.4** A user holding only the Seller role can do exactly what the Seller column of the section 4.2 table allows. Actions that return `403` for that user: recording a receipt, sending materials, recording receipt of a return, completing an order, requesting, approving or rejecting a complementary order, creating or cancelling a production order, instructing or cancelling a return, making an adjustment, shipping a sales order, managing units, items, recipes or users, and every notification action (AC-23.6).
- **AC-30.5** The rule that at least one active Manager is required (AC-03.8) is unchanged; Seller does not count as a Manager.
- **AC-30.6** Reads refused to a Seller (decision #147). A user holding only the Seller role gets `403` for: viewing a recipe; listing or viewing movements; listing or viewing production orders; listing or viewing complementary orders; listing or viewing returns; listing or viewing users; listing or viewing notifications.
- **AC-30.7** Reads allowed to a Seller. The same user gets `200` for: listing and viewing units, items, stock (all items and one item), customers and sales orders; both shipped-totals reports; and changing their own password (US-04). The stock view shows a Seller the same fields as every other role, including the number of the production order a material is committed to (AC-10.3); opening that production order is `403` (AC-30.6).
- **AC-30.8** Roles combine for reads as for actions (AC-05.2). A user holding Seller and Production can view movements and production orders (from Production) and customers, sales orders and both reports (from Seller).

### US-31: Open demand on the stock view

As a Manager, I want to see how much of each item customers are still waiting for so that I can plan production and purchases.

- **AC-31.1** The stock view (US-10, all items or one item) shows, for every item, its open demand (D-12) with the unit. With no Created sales order for the item, it is 0.
- **AC-31.2** Example. Warehouse holds 1,001 Bag of Shredded cheese 1 kg bag. Created sales orders: #1 for 600 Bag and #4 for 500 Bag. Open demand is 1,100 Bag and Warehouse stock is still 1,001 Bag. After #1 is shipped, open demand is 500 Bag and Warehouse stock 401 Bag. After #4 is cancelled, open demand is 0 Bag.
- **AC-31.3** Open demand may be larger than the Warehouse stock. It never blocks or changes a send, shipment, adjustment, return, estimate or any other figure. For raw materials it is shown beside the AC-10.3 figures and does not change committed material, pending returns or free leftover.
- **AC-31.4** Open demand is shown to every signed-in role, Production and Seller included, on the one stock view (decisions #147, #150). No role gets `403` for it.

### US-32: Shipped totals per item

As a seller, I want the total quantity shipped of each item for a period so that I can see what sells.

- **AC-32.1** A Warehouse, Manager or Seller user can ask for shipped totals per item, with optional filters: `from` and `to` dates (D-13), item type, and customer. The result has one row per item with a shipped quantity above zero in the period: item, item type, unit, shipped quantity (D-13), and the number of sales orders shipped with that item. Rows are ordered by item name, A to Z, and paged as in 4.1 (default 50, maximum 100, with the total count). Ordering by name, not newest first, is because a total has no creation time.
- **AC-32.2** Only Shipped sales orders count, by shipment time. Created and Cancelled sales orders do not count, and neither do adjustments (D-13).
- **AC-32.3** Example. Shipments: sales order #1 (Deli Norte, 600 Bag of Shredded cheese 1 kg bag) on 2026-12-02; #2 (Cafe Sur, 500 Bag of Shredded cheese 1 kg bag and 50 Bag of Grated cheese 200 g bag) on 2026-12-05; #3 (Deli Norte, 101 Bag of Shredded cheese 1 kg bag) on 2026-12-05. Sales order #7 (Deli Norte, 300 Bag of Shredded cheese 1 kg bag) is Cancelled.
  - From 2026-12-01 to 2026-12-31: "Grated cheese 200 g bag" 50 Bag, 1 sales order; "Shredded cheese 1 kg bag" 1,201 Bag, 3 sales orders. In that order.
  - The same with customer Deli Norte: "Shredded cheese 1 kg bag" 701 Bag, 2 sales orders. No Grated cheese row.
  - From 2026-12-03 to 2026-12-31: Grated cheese 50 Bag, 1 sales order; Shredded cheese 601 Bag, 2 sales orders.
- **AC-32.4** For any item and period, the shipped quantity equals the sum of that item's ShippedToCustomer movements in the same period (AC-28.2), with the sign reversed.
- **AC-32.5** No shipments in the period returns `200` with an empty list. A `from` later than `to`, or a date or item type in the wrong format, is rejected with `400`. A customer id that does not exist returns `200` with an empty list, as for list filters.
- **AC-32.6** A user holding none of the Warehouse, Manager and Seller roles (that is, Production only) gets `403` for this report (decision #147). Warehouse, Manager and Seller users do not.

### US-33: Shipped totals per customer

As a seller, I want the quantities shipped to each customer for a period so that I can see who buys what.

- **AC-33.1** A Warehouse, Manager or Seller user can ask for shipped totals per customer, with optional filters: `from` and `to` dates (D-13), customer, and item. The result has one row per customer and item with a shipped quantity above zero in the period: customer (id, current name, active status), item, item type, unit, shipped quantity, and the number of the customer's sales orders shipped with that item. Rows are ordered by customer name, then item name, A to Z, and paged as in 4.1. There is no total across items, because items have different units.
- **AC-33.2** The rules of AC-32.2, AC-32.4 and AC-32.5 apply here too. A user holding none of the Warehouse, Manager and Seller roles (that is, Production only) gets `403` for this report (decision #147).
- **AC-33.3** Example, with the shipments of AC-32.3, from 2026-12-01 to 2026-12-31: Cafe Sur, Grated cheese 200 g bag, 50 Bag, 1 sales order; Cafe Sur, Shredded cheese 1 kg bag, 500 Bag, 1 sales order; Deli Norte, Shredded cheese 1 kg bag, 701 Bag, 2 sales orders. In that order. Filtered by item Grated cheese 200 g bag: only the Cafe Sur row.
- **AC-33.4** A deactivated customer's shipments still appear, with the customer marked inactive.

## 11. Non-functional requirements

Section 5 applies to version 1.1 unchanged, with the additions marked "(v1.1)" there: the performance data set includes 500 customers and 5,000 sales orders, and the 1-second target covers the shipped-totals reports and the stock view with open demand; the consistency rule covers shipments and cancellations of sales orders (tested by AC-26.10) and a customer delete racing a new sales order (AC-29.8); sales orders are kept indefinitely and never deleted, and customers are kept indefinitely unless deleted under AC-29.5. Customer notes may hold business contact details; they are visible to Warehouse, Manager and Seller users only (section 4.2) and stay on the owner's machine (row 22). Read permissions now differ by role (decision #147), so every read endpoint has an expected `200` or `403` per role, given by the section 4.2 table. Accessibility: not applicable (no user interface). Language: English.

## 12. Changes to the version 1 parts

Every change version 1.1 makes to sections 1 to 6. Each is also marked "(v1.1)" in place.

| # | Where | Change |
|---|-------|--------|
| 1 | Status line and introduction at the top | The document now has a version 1.1 part, approved by the owner on 2026-11-23, settled by decisions #138 to #151. |
| 2 | Section 3, In version 1, sign-in bullet | "Three fixed roles" notes that version 1.1 has four, with Seller. |
| 3 | Section 3, Later | "Sales orders that remove finished goods from stock" removed. A "(v1.1)" sub-list of follow-on ideas added. |
| 4 | Section 4.2, sentence above the table | "One or more of three roles" notes the fourth role, Seller, and that a blank cell in a "View" row now means `403` for that read (decision #147). |
| 5 | Section 4.2, roles table, v1 rows | A Seller column added to every row. The v1 row "View units, items, recipes, stock, movement history, orders, complementary orders, returns" is split in two: "View units, items, stock" (all four roles) and "View recipes, movement history, orders, complementary orders, returns" (Warehouse, Production, Manager; Seller `403`). Every other v1 row is blank for Seller except sign-in and own password. Production's v1 cells are unchanged. |
| 6 | Section 4.2, roles table, new rows | Four rows added: view customers, sales orders and shipped-totals reports (Warehouse, Manager, Seller; Production `403`); create, edit, deactivate, reactivate or delete a customer (Manager, Seller); create or cancel a sales order (Manager, Seller); ship a sales order (Warehouse, Manager). |
| 7 | Section 4.3, D-2 | Movement type ShippedToCustomer added (any item, Warehouse −). The sentence on what a movement records now includes the sales order it belongs to. |
| 8 | AC-03.1 | Seller is in the list of roles a Manager can give. |
| 9 | AC-05.3 | Sales order status changes record who and when. |
| 10 | AC-07.3, AC-07.4 | Sales order lines count as use of an item: they block changing type and unit and block deleting it. |
| 11 | AC-10.5 (new) | The stock view shows open demand for every item, to every role (D-12, US-31). |
| 12 | AC-11.1 | A Seller-only user gets `403` for the movement list (AC-30.6). A movement shows its linked sales order. |
| 13 | AC-11.2 | The movement list can be filtered by sales order. |
| 14 | AC-14.1 | A Seller-only user gets `403` for listing production orders (AC-30.6). |
| 15 | AC-22.6 | A Seller-only user gets `403` for listing and viewing returns (AC-30.6). |
| 16 | Section 5, Performance | Data set adds 500 customers and 5,000 sales orders of 1 to 10 lines; the target covers both shipped-totals reports. |
| 17 | Section 5, Consistency | Adds two shipments of the same sales order, a shipment and a cancellation of the same sales order, and a customer delete racing a new sales order for it, to the examples of conflicting requests. |
| 18 | Section 5, Data | Sales orders are kept indefinitely and never deleted; customers are kept unless deleted under AC-29.5 (never had a sales order). |
| 19 | Section 6, row 14 | Marked as superseded in part by decision #147 (role-based reads). |

Deliberately not changed: D-1 and D-3 to D-9 (open demand and shipments never enter the leftover or variance calculations); AC-01.1 (it already returns the user's roles, now possibly Seller, AC-30.2); AC-02.1 (the first user is still a Manager only); AC-03.3 (an unknown role is still `400`; only the list of known roles grows, AC-30.1); AC-03.8 and AC-03.9 (user management stays Manager only, so a Seller gets `403`); AC-06.1 and AC-07.5 (units and items stay readable by every role, Seller included); AC-10.1 to AC-10.4 (stock stays readable by every role, one view for all, decision #147; only AC-10.5 is added); US-12 (adjustments work as before, for raw materials and finished products in the Warehouse); US-13 to US-22 apart from AC-14.1 and AC-22.6 (production orders, sends, complementary orders, completion, cancellation, returns; the Seller's `403` on recipes and complementary orders comes from the 4.2 table and AC-30.6); AC-23.2 and AC-23.6 (still exactly five notification types, Managers only); rows 20 and 21 of section 6 (the quantity and line limits apply to sales orders through AC-24.3 and AC-24.5, without changing the rows).

Not part of this document, but changed by the Seller role and the role-based reads, for the roles that own them: the "Unknown role" message and the `403` role names in `docs/02-design.md` 7.1 and 7.2 (and the `Role` enum in 2.3), and the "Roles" column of the endpoint map in design section 4, where read endpoints marked "All" now differ by role; the role seeding at start-up in `docs/03-architecture.md` 3.2 and 6; the "Who does what" table in `docs/08-user-guide.md`.

## 13. Assumptions and open questions for version 1.1

Rows 23 to 30 and 41 to 46 were questions for the owner; the owner settled all of them on 2026-11-23 (decisions #138 to #150). Rows 31 to 40 and 47 to 55 are assumptions made on the owner's behalf; each can be overruled. No question is open.

| # | Assumption or question | Decided by | Answer |
|---|------------------------|------------|--------|
| 23 | Q50. Who creates, ships and cancels a sales order? | Owner | Decided 2026-11-23: a new Seller role creates sales orders (decision #138). Cancelling and shipping are settled in row 41 (decision #146). |
| 24 | Q51. Does a sales order have a customer, and how? | Owner | Decided 2026-11-23: a customer list (master data), and sales orders pick from it (decision #139, US-29, AC-24.1). Details in rows 43 and 44 (decision #148). |
| 25 | Q52. Is stock reserved when a sales order is created? | Owner | Decided 2026-11-23: no. Stock is checked and removed only at shipment (decision #140, AC-24.2, AC-26.3). |
| 26 | Q53. One shipment per sales order, in full? | Owner | Decided 2026-11-23: yes. One shipment, every line in full, refused as a whole if any line is short; to ship less, cancel and create a smaller sales order (decision #141, AC-26.1, AC-26.3). |
| 27 | Q54. Cancel after shipment, and customer returns? | Owner | Decided 2026-11-23: neither in version 1.1. A customer return is a positive Warehouse adjustment naming the sales order (decision #142, AC-27.2, section 9). |
| 28 | Q55. Any new notification? | Owner | Decided 2026-11-23: none; AC-23.2 stays at five types (decision #143). |
| 29 | Q56. Which items on a sales order? | Owner | Decided 2026-11-23: any item, raw materials included (decision #144, AC-24.1, AC-24.8). Where raw material is shipped from is row 45 (decision #149). |
| 30 | Q57. Which reports or queries? | Owner | Decided 2026-11-23: totals shipped per product and per customer, and open demand on the stock view, information only (decision #145, US-31 to US-33). Exact figures are row 46 (decision #150). |
| 31 | No prices or money values on sales orders. They come later together with purchase prices on receipts (Later list). | PM | Assumed |
| 32 | Statuses are Created, Shipped and Cancelled. Sales orders have their own number sequence and are called "sales order #N" (D-10). | PM | Assumed |
| 33 | The shipment time is the time it is recorded, in UTC. There is no shipment date field and no backdating (unlike the receipt date, AC-09.1). The shipped-totals reports use this time (D-13). | PM | Assumed |
| 34 | Text limits: customer name 1 to 100 characters (the same as item names), customer notes up to 500, customer reference on a sales order up to 100, shipment note up to 500, cancellation reason 1 to 500, all after removing leading and trailing spaces and counted as in version 1. | PM | Assumed |
| 35 | No priority between sales orders: unlike leftover allocation (D-5), there is no oldest-first rule (AC-26.5). The warehouse decides which order to ship. | PM | Assumed |
| 36 | A sales order is not linked to any production order. Shipping has no effect on production orders, leftover or variances (AC-26.6). | PM | Assumed |
| 37 | Cancelling a sales order requires a reason, as for production orders (row 9). | PM | Assumed |
| 38 | Quantities on sales order lines follow the version 1 rules: at most 3 decimal places, at most 999,999,999.999 (row 20), in the item's unit. | PM | Assumed |
| 39 | Replaced by decision #147 (row 42): viewing customers, sales orders and the shipped reports is limited by role. | Owner | Superseded 2026-11-23 |
| 40 | The new movement type is named ShippedToCustomer, following SentToProduction and ReturnedToWarehouse. A shipment recorded by mistake is corrected by a positive adjustment; the sales order stays Shipped (AC-28.6), and the shipped totals still count it (row 50). | PM | Assumed |
| 41 | Q58, settled in the owner's grilling session (its Q58-Q60, Q72). What may a Seller do? | Owner | Decided 2026-11-23 (decision #146): a Seller creates and cancels any unshipped sales order, with a reason, and manages customers (create, edit, deactivate, reactivate, delete). A Seller does not ship; Warehouse or Manager ships. A Manager can do everything a Seller can. Only a Manager gives the Seller role, and existing users do not get it. See US-24, US-27, US-29, US-30. |
| 42 | Q59, settled in the grilling session (its Q61, Q69-Q71, Q76-Q80). What may each role see? | Owner | Decided 2026-11-23 (decision #147): a Seller sees units and items (row 55), stock, customers, sales orders and both shipped reports, and gets `403` on recipes, movements, production orders, complementary orders, returns, users and notifications. Production gets `403` on customers, sales orders and the shipped reports, but sees ShippedToCustomer movements with the sales order number, and open demand. One stock view for all roles. Warehouse and Manager see everything; notifications stay Manager-only. See section 4.2, AC-25.4, AC-28.7, AC-29.7, AC-30.6 to AC-30.8, AC-31.4, AC-32.6, AC-33.2. |
| 43 | Q60, settled in the grilling session (its Q62, Q63, Q73-Q75). What is on a customer, and can it be deleted? | Owner | Decided 2026-11-23 (decision #148): a name (unique ignoring case, 1 to 100) and free-text notes (up to 500); deactivate and reactivate; delete only if the customer never had any sales order, cancelled ones included. See US-29. Structured contact fields are on the Later list. |
| 44 | Q61, settled in the grilling session (decision #148). What does deactivating a customer block? | Owner | Decided 2026-11-23 (decision #148): new sales orders only. Existing Created sales orders can still be shipped or cancelled, and deactivating a customer with open orders is allowed (AC-24.4, AC-26.13, AC-27.1, AC-29.4). |
| 45 | Q62, settled in the grilling session (its Q64, Q65). Where are raw materials shipped from? | Owner | Decided 2026-11-23 (decision #149): Warehouse stock only, never production storage; a Pending return is not available until received. Shipping material a Created production order will need is allowed: the shipper decides, with no protection. A shortfall warning is on the Later list. See D-11, AC-26.11, AC-26.12. |
| 46 | Q63, settled in the grilling session (its Q66-Q68). Exactly which report figures? | Owner | Decided 2026-11-23 (decision #150): shipped per item over a date range (type, unit, quantity, number of orders; filters: dates, item type, customer); shipped per customer per item (filters: dates, customer, item; no cross-item total); periods by shipment time in whole UTC days with optional ends; open demand per item on the stock view (all items and one item), visible to all roles. See D-12, D-13, US-31 to US-33. |
| 47 | Seller can be combined with any other role, like the existing roles (AC-05.2, AC-30.1). | PM | Assumed |
| 48 | On upgrade from version 1, the Seller role is created and no existing user is given it (AC-30.3); a Manager assigns it. | PM | Assumed |
| 49 | Sales orders and reports show a customer's current name; renaming a customer does not keep the old name on earlier sales orders (AC-29.3). The customer reference on each sales order is unchanged. | PM | Assumed |
| 50 | Shipped totals count shipments only. An adjustment that corrects a mistaken shipment, or records a customer return, does not reduce the totals (D-13), because adjustments are not linked to sales orders. | PM | Assumed |
| 51 | Report rows are ordered by name, not newest first (AC-32.1, AC-33.1), because a total has no creation time; this is an exception to 4.1's list order. | PM | Assumed |
| 52 | Customers are listed newest first like every other list (4.1), filterable by active status only; a name search is on the Later list. | PM | Assumed |
| 53 | Deactivating or reactivating a customer that is already in that state succeeds and changes nothing, as for users (AC-29.4). | PM | Assumed |
| 54 | A deactivated customer is rejected on a new sales order with `409` (a state conflict), while an unknown or deleted customer id is `400` (invalid input), following the conventions in 4.1. Deleting a customer that has sales orders is a `409` (AC-29.5). | PM | Assumed |
| 55 | A Seller can list and view units, as every other role can (AC-06.1), because every item and every sales order line shows its unit and the unit list holds nothing sensitive (decision #151). | Owner | Confirmed 2026-11-23 |

---

# Version 1.2: Web front end

Status: written 2026-11-26 by `product-manager`; every question (Q98 to Q109) settled by the owner in a grilling session on 2026-11-27 and worked in on the same day; approved by the owner on 2026-11-27, including assumption rows 92 and 93. Chosen by the owner on 2026-11-23 as the next item from the Later list, after sales orders (v1.1) and before the cloud path (PostgreSQL, HTTPS, rate limit, CD), which stays the next step after this version (owner, 2026-11-27: "later on will be on cloud"). The questions, their answers and every assumption made on the owner's behalf are in section 20.

Everything in sections 1 to 13 still applies to version 1.2: every business rule, role, endpoint, message and limit of versions 1 and 1.1 is unchanged. This part adds screens in a web browser that call the existing API, and the few changes to the API's web server listed in section 19.1. Where this part changes a version 1 or 1.1 section, the change is marked "(v1.2)" in place and listed in section 19.2.

## 14. Problem and users

Today every task in the product is an HTTP request typed in PowerShell on the one computer that runs the API: a helper pasted into each window, ids and order numbers looked up in JSON answers, quantities typed into hashtables (`docs/08-user-guide.md`). That suits a developer, not the staff of a small food manufacturer. In practice the people who should record each step, such as the warehouse worker who sends cheese to the line or the seller who takes an order by phone, ask the Manager to type it for them, or write it on paper and key it in later. The records then name the wrong person and come late, which defeats the audit trail of AC-05.3, and a mistyped id can send or ship the wrong item.

The owner has no business of their own. The product is made for small businesses like the one in section 1, and the repository is public as a portfolio piece. A browser front end is what makes the product usable by the people it is for.

**Primary user:** the warehouse worker, who several times a day records raw material arriving, sends material for a production order and ships customers' orders, and who should not need to know what an HTTP request is.

**Other users:** the production line worker, who completes orders and asks for extra material; the production and inventory manager, who plans and cancels orders, approves extra material, returns leftover and reads notifications; the seller, who keeps the customer list and takes and cancels sales orders. While the product is localhost only (Q105), all four take turns at the one computer that runs the API.

## 15. Goal for version 1.2

Each of the four roles can do their daily tasks in a web browser on the owner's machine, signed in as themselves, without PowerShell, and a Manager can set up units, items, recipes and staff accounts there too, while the existing API still enforces every rule. The screens work from phone width up, ready for the cloud step that will let phones reach them.

## 16. Scope

In version 1.2 (owner, 2026-11-27: Q99 and Q100):

- One responsive layout for every screen, from 360 pixels wide (a phone) up to a desktop (Q102, Q109, section 18).
- Sign-in, sign-out, change own password, and the end of a session by expiry, inactivity or deactivation (US-34, US-35).
- A menu and screens per role (17.2) that never offer more than the role table of section 4.2 allows (US-36).
- The front-end security rules of SEC-10 and SEC-27 (US-37), and the changes to the API's web server they need (section 19.1).
- Stock with open demand, for every role (US-38).
- Recording a receipt (US-39).
- Production orders: list, detail with estimate and variances, create, send, complete, cancel (US-40 to US-44).
- Complementary orders: request, list, approve, reject, send (US-45 to US-47).
- Leftover returns: list, instruct, receive, cancel (US-48). They are in version 1.2 because a Pending return blocks a send (AC-15.11), so the Warehouse must be able to record receiving it on screen.
- Notifications for Managers, with an unread count (US-49).
- Customers: list, detail, add, edit, deactivate, reactivate, delete (US-50).
- Sales orders: list, detail, create, ship, cancel (US-51 to US-54).
- Writing a shipment's movements in one batch, which the owner placed before the web front end (SEC-24, decision #175) (US-55). Scheduling note (delivery manager): this runs as its own small task before the front-end build; it stays in version 1.2 scope.
- Units and items: list and detail for every role; add, rename or edit, and delete for Managers (US-56, US-57) (Q100d).
- Recipes: view for Warehouse, Production and Manager; edit for Managers (US-58) (Q100d).
- User management for Managers: list, add, change roles, deactivate, reactivate, set a password (US-59) (Q100e).
- Escaping HTML characters in the API's JSON output (SEC-27 server hardening, AC-37.9, A-5) (Q108).

Movement history, the shipped-totals reports and adjustments are not in version 1.2 (Q100a to c). They stay API-only, done as today with PowerShell or the sample requests, and are on the Later list.

Out of scope for version 1.2 (non-goals):

- Any change to a business rule, endpoint, request or response shape, status code, message, role or limit of versions 1 and 1.1. The only API-side changes are those in section 19.1.
- A screen for any task not listed above, in particular movement history, the shipped-totals reports and adjustments (Q100).
- Access from any other machine: LAN, internet or cloud. HTTPS, the sign-in rate limit and the cloud path stay on the Later list, the cloud path as the next step (Q105, SEC-03, SEC-05).
- Using the front end from a real phone or tablet. A phone cannot reach `localhost`, so that comes with the cloud step; in version 1.2 the phone-width layout is tested in the browser's device preview (Q102, Q109).
- Any language other than English, and translating the API's messages (Q103).
- Staying signed in across a page reload, a new tab or a browser restart; "remember me"; signing out other sessions (Q106).
- Cookies, antiforgery tokens and emailed tokens. Because there are none, this version does not need the explicit DataProtection configuration on the Later list (section 19.1).
- Printing, PDF and exports (pick lists, delivery notes, CSV).
- Dashboards, charts and a per-role "work to do" home screen.
- Live updates pushed by the server, and alerts outside the screen (sound, desktop pop-ups, email). Only the notification count refreshes by itself (AC-49.5).
- Offline use, and installing the front end as an app.
- Sign-up and password reset by email (as in version 1).
- Editing anything the API cannot edit: production orders, movements, sales orders.
- A name search in the API. Pickers filter in the browser (F-13).
- Saved user preferences: themes, remembered filters, column choices.
- Demo data for people evaluating the repository.

Later: the follow-on ideas are on the Later list in section 3, marked "(v1.2)".

## 17. User stories and acceptance criteria

### 17.1 Conventions used in every front-end criterion

These are part of the acceptance criteria of US-34 to US-59. The API conventions of section 4.1 still apply to every request the front end sends.

- **F-1 Refusals.** When the API refuses a request, the screen shows the `detail` text of the response word for word (`docs/02-design.md` section 6) in a message area at the top of the form or screen that sent it. When the response has an `errors` entry for a field the form shows, that message is also shown next to the field (for a line, next to that line's row). Everything the user typed stays in the form, except password fields, which are emptied. Nothing is shown as saved.
- **F-2 No answer.** When the API does not answer (it is not running, or 30 seconds pass without an answer), the screen shows "Cannot reach the API. Check that it is running, then try again." and keeps the typed input. A request that changes data is never re-sent automatically.
- **F-3 One click, one request.** While a request that changes data is running, the control that sent it cannot be used again and shows that it is busy. Pressing it twice, or pressing Enter twice, sends one request.
- **F-4 Success.** After a successful change, the screen shows a short confirmation naming the record and its new state (for example "Order #12 is now InProduction.") and shows the record as the API returned it. The wording may differ; QA checks that the record number and state are named.
- **F-5 Confirm before final actions.** Before an action that changes a record's status, deletes a record, or changes a user's roles or password, a dialog names the record and the action and says whether it can be undone, for example "Ship sales order #2 for Cafe Sur (#7)? This cannot be undone." Only the dialog's confirm control sends the request; its cancel control, the Escape key and closing the dialog send nothing. Recording a receipt, creating a record, editing a customer, renaming a unit, editing an item, saving a recipe and marking notifications read have no dialog.
- **F-6 Lists.** A list shows one API page at a time, in the API's order (newest first), 50 rows per page, with the total count (for example "132 production orders") and next and previous page controls, each hidden when there is no such page. The filters offered are the API's filters for that list. Changing a filter shows page 1. When nothing matches, the list shows a sentence instead of an empty table, for example "No production orders match these filters." Selecting a row opens that record.
- **F-7 Numbers.** Quantities are shown as the API writes them in messages (design 6.1): comma thousands separator, dot for decimals, at most 3 decimals with trailing zeros removed, the unit after a space, for example "1,015 kg" and "0.6 kg". Percentages have a sign and 2 decimals: "+2.90%", "-2.50%". A quantity or amount field accepts digits with at most one dot; anything else, such as "1,5", "1 000" or "-5", is refused on the screen with "Enter a number such as 995 or 995.5." and nothing is sent. Every other rule on numbers (decimal places, zero, maximums, ranges) is left to the API and shown by F-1. The front end calculates no business figure; it shows the figures the API returns (design section 1).
- **F-8 Times.** Times from the API are shown in the computer's local time zone as "2026-11-26 08:05", and the footer of every screen says "Times are local (UTC-06:00)." with the computer's actual offset (Q107). A date the user enters (the receipt date) is sent as entered.
- **F-9 People** are shown by the email the API returns.
- **F-10 Ids next to names.** A customer is shown as its name followed by its id, "Deli Norte (#4)", everywhere a customer appears: lists, details, pickers and dialogs. When the customer is deactivated, "(inactive)" follows. An item is shown with its id, "Cheese (#3)", in every picker, on the Stock screen and on the Items screens (SEC-09, SEC-25, SEC-27).
- **F-11 Only possible actions are offered.** A control for an action that none of the user's roles allows (section 4.2) is not shown. A control for an action the record's current status does not allow is not shown either, for example no Ship control on a Shipped sales order. The API still checks every request; a refusal, for example because another user changed the record in the meantime, is shown by F-1.
- **F-12 Records that do not exist.** Opening the address of a record that does not exist shows the API's `404` message and a link to the list of that kind of record.
- **F-13 Pickers.** Where a form asks for an item, a customer or a unit, the user chooses from a list of every record of the allowed kind (all pages of the API's list), shown as in F-10, with the unit for items. Typing narrows the list to the records whose name contains the typed text, ignoring letter case, or whose id equals the typed number. With 500 records, the narrowed list appears within 1 second of typing. The user cannot send an id that was not chosen from the list.
- **F-14 Refresh.** A screen shows the data as it was when the screen opened. Each list and record screen has a Refresh control that reloads it. Only the notification count refreshes by itself (AC-49.5).
- **F-15 Every width from 360 pixels.** Every screen, form, dialog and the menu work in a browser window from 360 pixels wide up to a desktop (Q102, Q109). At any width in that range: no text or control is cut off or overlaps another; the page itself never needs sideways scrolling, except that a wide table may scroll sideways inside its own area; the menu can be reached from every screen; and every control can be used by touch, with a target of at least 24 by 24 CSS pixels. QA checks each story at 360 by 640, 768 by 1024 and 1280 by 720, using the device preview of Edge or Chrome (row 74).

### 17.2 Screens and actions by role

The front end offers each role the screens and actions below, and never more than section 4.2 allows. A user with several roles gets everything any of them allows (AC-05.2). Section 4.2 actions with no screen in version 1.2 (movement history, the shipped-totals reports, adjustments) stay available through the API.

| Screen or action | Warehouse | Production | Manager | Seller | Story |
|------------------|:---------:|:----------:|:-------:|:------:|-------|
| Sign in, sign out, change own password | yes | yes | yes | yes | US-34, US-35 |
| Stock, with open demand | yes | yes | yes | yes | US-38 |
| Record a receipt | yes | | yes | | US-39 |
| Production orders: list and detail | yes | yes | yes | | US-40 |
| Create or cancel a production order | | | yes | | US-41, US-44 |
| Send materials for an order | yes | | yes | | US-42 |
| Complete an order | | yes | yes | | US-43 |
| Request a complementary order | | yes | yes | | US-45 |
| Complementary orders: list and detail | yes | yes | yes | | US-46 |
| Approve or reject a complementary order | | | yes | | US-46 |
| Send a complementary order | yes | | yes | | US-47 |
| Returns: list and detail | yes | yes | yes | | US-48 |
| Instruct or cancel a return | | | yes | | US-48 |
| Record receiving a return | yes | | yes | | US-48 |
| Notifications | | | yes | | US-49 |
| Customers: list and detail | yes | | yes | yes | US-50 |
| Add, edit, deactivate, reactivate or delete a customer | | | yes | yes | US-50 |
| Sales orders: list and detail | yes | | yes | yes | US-51 |
| Create or cancel a sales order | | | yes | yes | US-52, US-54 |
| Ship a sales order | yes | | yes | | US-53 |
| Units: list and detail | yes | yes | yes | yes | US-56 |
| Add, rename or delete a unit | | | yes | | US-56 |
| Items: list and detail | yes | yes | yes | yes | US-57 |
| Add, edit or delete an item | | | yes | | US-57 |
| View a recipe | yes | yes | yes | | US-58 |
| Edit a recipe | | | yes | | US-58 |
| Users: list, detail, add, change roles, deactivate, reactivate, set a password | | | yes | | US-59 |

Not in version 1.2 (Q100, Later list): movement history, the shipped-totals reports and adjustments.

### US-34: Sign in, sign out and the end of a session

As a staff member, I want to sign in on the browser with my email and password so that I can do my work under my own name without PowerShell.

- **AC-34.1** Opening the front end's address when not signed in shows the sign-in screen: an email field, a password field and a Sign in control. No other screen and no API data can be seen. Opening the address of any other screen when not signed in shows the sign-in screen, and after signing in, that screen opens.
- **AC-34.2** Signing in with the correct email and password of an active user opens the Stock screen. The header shows the user's email and roles, for example "maria.lopez@example.com (Manager)", a Change password control and a Sign out control.
- **AC-34.3** A wrong password, an unknown email or a deactivated user: the screen shows "Invalid email or password.", keeps the email and empties the password (AC-01.3). A locked account shows "Account temporarily locked. Try again later." (AC-01.4). A blank email or password shows "Email is required." or "Password is required." (design 7.1); the front end may show these without sending a request.
- **AC-34.4** Sign out shows the sign-in screen and discards the token. Afterwards, the browser's Back control shows the sign-in screen, not the earlier screens or their data.
- **AC-34.5** The token is held only in the page's memory (SEC-10). After signing in, the browser's local storage, session storage, IndexedDB, cookies and cache storage for the front end's address hold no token and no API data, and the token never appears in the address bar, on the page or in the browser console. Reloading the page, opening the front end in a second tab, or closing and reopening the browser shows the sign-in screen (Q106).
- **AC-34.6** When any request is answered `401` (the 8-hour token has expired, the user was deactivated, or the user changed their password elsewhere), the sign-in screen opens with "Your sign-in is missing or has expired. Sign in again." After signing in, the screen that was open opens again. What was typed in a form is not kept.
- **AC-34.7** After 15 minutes with no keyboard, mouse or touch input in the front end, the user is signed out as in AC-34.4, and the sign-in screen says "You were signed out after 15 minutes without activity." (Q106, because staff share one computer).
- **AC-34.8** When the API is not running, signing in shows the F-2 message.

### US-35: Change own password

As a staff member, I want to change my own password on screen so that only I know it.

- **AC-35.1** Change password opens a form with three fields: current password, new password, and new password again. When the two new passwords match and the API accepts the change, the sign-in screen opens with "Password changed. Sign in with your new password.", because the change ends every session of the user (AC-04.1).
- **AC-35.2** When the two new passwords differ, the form shows "The new passwords do not match." and sends nothing.
- **AC-35.3** A wrong current password shows "Current password is incorrect." next to the current password field (AC-04.2). A new password that fails the policy shows the policy message next to the new password field. A locked account shows the lock-out message. In each case all three fields are emptied and the password is unchanged.

### US-36: Screens by role

As a staff member, I want to see only the screens and actions my roles allow so that I am never offered something I cannot do.

- **AC-36.1** The menu shows exactly the screens of the 17.2 table that at least one of the user's roles can open. Warehouse only: Stock, Record receipt, Production orders, Complementary orders, Returns, Customers, Sales orders, Items, Units. Production only: Stock, Production orders, Complementary orders, Returns, Items, Units. Seller only: Stock, Customers, Sales orders, Items, Units. Manager: Stock, Record receipt, Production orders, Complementary orders, Returns, Notifications, Customers, Sales orders, Items, Units, Users. Production and Seller: Stock, Production orders, Complementary orders, Returns, Customers, Sales orders, Items, Units.
- **AC-36.2** Opening the address of a screen that none of the user's roles can open (for example a Seller-only user opening a production order's address) shows "Your roles do not allow this screen." with a link to Stock, and none of that screen's data.
- **AC-36.3** Every screen and every record (production order, complementary order, return, customer, sales order, unit, item, user) has its own address. Opening that address in the same tab, or after signing in (AC-34.1), shows it. The browser's Back and Forward controls move between the screens visited.
- **AC-36.4** The menu follows the roles returned at sign-in. When a Manager changes the user's roles in the meantime, the old roles stay in force until the user signs in again (AC-03.5); an action the API now refuses shows the API's `403` message (F-1). After the next sign-in, the menu matches the new roles.
- **AC-36.5** A reference to a record the user cannot open is shown as plain text, not as a link. Example: on the Stock screen, a Seller-only user sees "order #12" next to committed material as text (AC-30.7), while Warehouse, Production and Manager users get a link to order #12.

### US-37: Safe display of stored text (SEC-10, SEC-27)

As the owner, I want text typed by any user to be shown to others only as text so that no user can run a script in another user's browser and act under their sign-in.

- **AC-37.1** Every text from the API is shown as plain text: names, descriptions, notes, reasons, customer references, shipment notes, notification messages, emails and error messages. Test data: an item named `<img src=x onerror=alert(1)>`, a customer named `</script><script>alert(1)</script>`, customer notes `<b>bold</b> & 'quote'`, and a completion reason `<a href="https://example.com">click</a>`. On every screen where they appear, the characters show exactly as typed, no dialog opens, nothing is bold, nothing is a link, and the browser makes no request to any other address.
- **AC-37.2** The front end never inserts API data into the page as HTML and never places API data inside a script block. `code-reviewer` checks that no raw-HTML insertion is used anywhere in the front end's code (for example `innerHTML`, `outerHTML`, `insertAdjacentHTML`, `document.write`, `v-html`, `dangerouslySetInnerHTML`, `[innerHTML]`).
- **AC-37.3** Bidirectional control characters in any text from the API (U+061C, U+200E, U+200F, U+202A to U+202E, U+2066 to U+2069) are shown as a visible marker naming the code point, for example "[U+202E]", and do not reorder the text around them. Example: an item named "Milk" + U+202E + "gnp.exe" is shown as "Milk[U+202E]gnp.exe", never as "Milkexe.png". The stored text is not changed: an edit field (customer name or notes, item name or description, unit name) holds the stored text exactly, and below it the form says "This text contains hidden direction characters: U+202E."
- **AC-37.4** Customers and items are shown with their ids as F-10 says. Example: customers "Deli Norte" (#4) and "Deli Nоrte" with a Cyrillic "о" (#9) both appear in the sales order's customer picker with their ids, and the sales order detail and list show the id of the customer chosen.
- **AC-37.5** Every response that delivers the front end's pages carries a `Content-Security-Policy` header that contains at least `default-src 'self'`, `script-src 'self'`, `object-src 'none'`, `base-uri 'none'`, `frame-ancestors 'none'` and `form-action 'self'`, and `connect-src 'self'` (the API is the same origin, Q104). No directive contains `*`, `http:`, `https:` or `data:` as a script source, and `script-src` contains neither `'unsafe-inline'` nor `'unsafe-eval'`. `style-src` may contain `'unsafe-inline'` only if the tech lead records the need in `docs/decisions.md` and `security-reviewer` accepts it. During the acceptance tests the browser reports no Content-Security-Policy violation.
- **AC-37.6** Every response, pages and API alike, carries `X-Content-Type-Options: nosniff` and `Referrer-Policy: no-referrer`, and no `Server` header. Every response under `/api/` carries `Cache-Control: no-store` (SEC-11).
- **AC-37.7** Cross-origin access (SEC-10). The front end is served from the API's own address (Q104), so the API has no CORS policy: it sends no `Access-Control-Allow-Origin` header to any origin, and a page from another origin cannot read API responses. Test: a request to `/api/v1/units` with an `Origin: http://localhost:5999` header gets no `Access-Control-Allow-Origin` header, and a preflight `OPTIONS` request from that origin gets none either.
- **AC-37.8** The front end loads nothing from any other address (no scripts, styles, fonts or images from a CDN) and sends no analytics or error reports anywhere. During the acceptance tests, the browser's network log shows requests to the front end's own address only.
- **AC-37.9** (Q108.) The API's JSON output escapes `<`, `>`, `&` and `'` inside strings, written as a Unicode escape (a backslash, the letter `u` and four hex digits; for `<` the digits are `003C`). Accented letters stay readable as they are. Example: the raw bytes of `GET /customers/{id}` for the customer named `</script><script>alert(1)</script>` contain no `<` character, and the name after parsing is unchanged. Every client, the PowerShell helper and the sample requests included, reads the same strings as before; only the raw bytes change.

### US-38: Stock

As any staff member, I want to see stock on screen so that I know what is in the warehouse and in production storage, and what customers are waiting for.

- **AC-38.1** The Stock screen lists one row per item, from the stock view (US-10, US-31): item name with id, type, unit, Warehouse, Production storage and Open demand. For a raw material it also shows Committed (with the order number it is committed to), Pending returns and Free leftover. For a finished product those three cells show "-". A value of 0 is shown as "0".
- **AC-38.2** The list can be filtered by type: all items, raw materials or finished products. Paging follows F-6.
- **AC-38.3** Example (design 5.5): Cheese shows Warehouse 1,200 kg, Production storage 1,020 kg, Open demand 120 kg, Committed 1,020 kg (order #12), Pending returns 0 kg, Free leftover 0 kg. Shredded cheese 1 kg bag (AC-28.4) shows Warehouse 1,001 Bag, Production storage 0 Bag, Open demand 1,200 Bag, and "-" for the raw-material cells.
- **AC-38.4** Every role sees the same columns (AC-31.4). The committed order number follows AC-36.5.
- **AC-38.5** With no items, the screen says "No items yet. A Manager adds them."

### US-39: Record a receipt

As a warehouse user, I want to record raw material arriving on screen so that Warehouse stock is correct the moment it arrives.

- **AC-39.1** A Warehouse or Manager user opens Record receipt: a raw material picker (raw materials only, F-13), a quantity field with the chosen item's unit shown beside it, a receipt date (today's local date to start with) and an optional note. Saving records the receipt (US-09).
- **AC-39.2** On success the screen says, for example, "Received 1,000 kg of Cheese." and empties the item, quantity and note fields, keeping the date. The Stock screen then shows the new Warehouse figure (AC-09.4: 200 kg becomes 1,200 kg).
- **AC-39.3** Refusals are shown by F-1 with the input kept, for example a receipt date later than today (UTC), a quantity of 0, more than 3 decimal places, or a note over 500 characters (AC-09.2).
- **AC-39.4** With no raw materials, the picker says "No raw materials yet. A Manager adds them." and nothing can be saved.

### US-40: Production orders, list and detail

As a warehouse, production or manager user, I want to see production orders and everything recorded on them so that I know what to send, what to produce and what to investigate.

- **AC-40.1** The list shows, per order: number, finished product, ordered quantity with unit, status, created time, time of the latest status change and produced quantity. It can be filtered by status (all, Created, InProduction, Completed, Cancelled). Paging follows F-6.
- **AC-40.2** The detail shows every value of AC-14.2: number, finished product, ordered quantity, status, produced quantity, output variance and its percentage, the tolerance in force at completion, and the completion or cancellation reason; per line: material, unit, amount per unit, planned amount, leftover applied, warehouse send, complementary sends, carried leftover, leftover entered, ActualUsage, RequiredForActualOutput, usage variance and its percentage; the order's complementary orders (number as a link, status, requested time); and the history (status, time, user's email, note). A value not yet known is shown as "-".
- **AC-40.3** While the order is Created, each line also shows a section headed "Estimate (recalculated each time the order is opened)" with the estimated leftover applied, the nominal send and the accepted send range, for example "975 to 1,015 kg" (AC-13.2, AC-15.3).
- **AC-40.4** On a completed line whose usage variance is above tolerance, the words "Above tolerance" appear beside the percentage, in text and not by colour only. When the percentage is empty (decision #49), the cell shows "n/a" and the line still shows "Above tolerance" when the API flags it.
- **AC-40.5** Example A (AC-20.5): completed order #12 shows Cheese ActualUsage 1,016 kg, RequiredForActualOutput 1,001 kg, usage variance +15 kg and +1.50% with no "Above tolerance"; produced 1,001 Bag, output variance +1 Bag and +0.10%. Example B (AC-20.6) shows +29 kg, +2.90% and "Above tolerance".
- **AC-40.6** Following F-11, the detail offers: on a Created order, Send materials (Warehouse, Manager) and Cancel order (Manager); on an InProduction order, Complete order and Request extra material (Production, Manager) and Cancel order (Manager); on a Completed or Cancelled order, no action.
- **AC-40.7** An unknown order number follows F-12.

### US-41: Create a production order

As a Manager, I want to create a production order on screen so that the warehouse sees what to send and I see the leftover estimate at once.

- **AC-41.1** A Manager opens New production order from the production order list: a finished product picker (finished products only, F-13) and an ordered quantity with the product's unit beside it. On success the new order's detail opens, Created, with its estimate (AC-40.3).
- **AC-41.2** Refusals follow F-1, for example a finished product with no recipe, or an ordered quantity of 0 or with more than 3 decimal places (AC-13.3).
- **AC-41.3** With no finished products, the picker says "No finished products yet." and nothing can be saved.

### US-42: Send materials for an order

As a warehouse user, I want to enter what I weighed out for each material, with the accepted range in front of me, so that the send is right the first time.

- **AC-42.1** On a Created order, Send materials opens a form with one row per order line: material, unit, planned amount, estimated leftover applied, nominal send, accepted range, the material's current Warehouse stock, and an empty quantity field. Every row must be filled, 0 included; an empty row shows "Enter a quantity for Cheese (0 if none is sent)." and nothing is sent.
- **AC-42.2** Sending asks for confirmation (F-5) with each material and quantity listed. Confirming sends one request (US-15). On success the order detail shows InProduction, with leftover applied, warehouse send and carried leftover filled in.
- **AC-42.3** Example (AC-15.3): with 5 kg of free leftover, a send of 995 kg is accepted, and the Stock screen then shows 1,000 kg of Cheese in production storage. A send of 1,015.001 kg is refused: "Cheese: send must be between 975 and 1,015 kg (planned 1,000 kg, leftover applied 5 kg)." appears at the top and next to the Cheese row, and nothing changes.
- **AC-42.4** The other refusals follow F-1 with the input kept: another order in production (AC-15.10), a Pending return (AC-15.11; the message is followed by a link to the Returns list filtered to Pending), not enough Warehouse stock (AC-15.7), and an order no longer Created because someone else sent or cancelled it (AC-15.9).
- **AC-42.5** The ranges on the form are the estimate when the form opened. If leftover changed in the meantime, the API's message states the current range, and the detail after a successful send shows the leftover actually applied (D-6).

### US-43: Complete an order

As a production user, I want to enter the output and the leftover on screen so that finished goods enter the warehouse and the usage is checked.

- **AC-43.1** On an InProduction order, Complete order opens a form: the produced quantity with the product's unit, and per line the leftover with the material's current production storage stock beside it ("Production storage now: 1,020 kg"), plus a reason. The form explains: "Enter the total quantity of each material physically left in production storage, including leftover from earlier orders." (row 1). The reason is labelled "Required when any leftover is above 0".
- **AC-43.2** An empty leftover row shows "Enter the leftover for Cheese (0 if none)." and nothing is sent.
- **AC-43.3** Completing asks for confirmation (F-5) with the produced quantity. On success the detail shows Completed with every variance (AC-40.5).
- **AC-43.4** Refusals follow F-1, for example "A reason is required when leftover is recorded." and "Leftover for Cheese cannot exceed the 1,020 kg in production storage." (AC-19.4).

### US-44: Cancel a production order

As a Manager, I want to cancel an order on screen, with a reason, so that it no longer holds leftover or blocks the line.

- **AC-44.1** On a Created or InProduction order, Cancel order opens the F-5 dialog with a required reason field. A blank reason shows "Reason is required." in the dialog and sends nothing. On success the detail shows Cancelled with the reason.
- **AC-44.2** For an InProduction order, the dialog also says "Material already sent stays in production storage as leftover." (AC-21.3).
- **AC-44.3** Refusals follow F-1, for example an order completed by someone else in the meantime (AC-21.5).

### US-45: Request a complementary order

As a production user, I want to ask for extra material on screen, with a reason, so that the line does not stop and the extra use is on record.

- **AC-45.1** On an InProduction order, Request extra material opens a form with one row per material on the order (only those, AC-16.3) with an empty requested amount, and a required reason. Rows left empty are not requested. On success the screen says "Complementary order #3 is waiting for approval." and the order detail lists #3 as PendingApproval.
- **AC-45.2** With every row empty, the form shows "Enter an amount for at least one material." and sends nothing.
- **AC-45.3** Refusals follow F-1, for example an order that is no longer InProduction (AC-16.2).

### US-46: Complementary orders, list, approve and reject

As a Manager, I want to see the requests for extra material and decide them on screen so that no extra material leaves the warehouse without my decision.

- **AC-46.1** The list shows, per complementary order: number, parent order number (a link), status, requested by, requested time and its materials with requested amounts. It can be filtered by status and by parent order number. Paging follows F-6. The detail adds the reason, the accepted send range per line while PendingApproval or Approved, the sent quantity once Sent, the rejection or cancellation note, and the history.
- **AC-46.2** On a PendingApproval complementary order, a Manager sees Approve and Reject. Reject opens the F-5 dialog with an optional note. On success the detail shows Approved, or Rejected with the note.
- **AC-46.3** A complementary order already decided or cancelled in the meantime follows F-1 (AC-17.3).

### US-47: Send a complementary order

As a warehouse user, I want to send approved extra material on screen so that production can continue.

- **AC-47.1** On an Approved complementary order, Send material opens a form with one row per line: material, unit, requested amount, accepted range, current Warehouse stock and an empty quantity field. Every row must be filled. Sending asks for confirmation (F-5). On success the complementary order shows Sent, and its parent order's complementary sends include the quantity.
- **AC-47.2** Example (AC-18.2): requested 20 kg; 20.4 kg is accepted; 20.401 kg is refused with the API's range message next to the row.
- **AC-47.3** Other refusals follow F-1, for example not enough Warehouse stock (AC-18.4) or a status other than Approved (AC-18.3).

### US-48: Leftover returns

As a Manager, I want to instruct returns of leftover, and as a warehouse user I want to record receiving them, on screen, so that leftover goes back to the warehouse and sends are not blocked.

- **AC-48.1** The Returns list (Warehouse, Production, Manager) shows, per return: number, material, quantity with unit, reason, linked order number (a link) if any, status, and who created it and when. It can be filtered by status and by material. Paging follows F-6. The detail adds the history.
- **AC-48.2** A Manager opens New return: a raw material picker, the material's current free leftover beside it ("Free leftover now: 20 kg"), a quantity, a required reason and an optional order number. On success the return is Pending, and the screen says, for example, "Return #5 of 20 kg of Cheese is waiting for the warehouse."
- **AC-48.3** On a Pending return, Warehouse and Manager users see Receive; Manager users also see Cancel return. Each asks for confirmation (F-5). Example (AC-22.4): after receiving a 20 kg Cheese return, the Stock screen shows production storage 0 kg and Warehouse 20 kg more than before.
- **AC-48.4** Refusals follow F-1, for example a quantity above the free leftover (AC-22.2), a material on the InProduction order (AC-22.3), or a return that is no longer Pending (AC-22.5).

### US-49: Notifications

As a Manager, I want to see events that need my attention and the number I have not read yet, on every screen, so that nothing is missed.

- **AC-49.1** The Notifications screen lists, per notification: type, order number (a link) and complementary order number (a link) where present, time, message (as text, US-37), and "Unread" or "Read" in words. It can be filtered to unread only. Paging follows F-6.
- **AC-49.2** Each unread notification has Mark read; the screen also has Mark all read. After either, the rows show "Read" and the unread count goes down. Opening the list marks nothing read by itself.
- **AC-49.3** A Manager's header shows "Notifications (3)" with the number of the Manager's unread notifications, or "Notifications" when there are none. Users without the Manager role see no notification control.
- **AC-49.4** Example (AC-20.5): after order #12 is completed as in worked example A, the Manager's unread count rises by 2 and the list shows one OutputVariance and one LeftoverAtCompletion notification for order #12.
- **AC-49.5** The count is refreshed whenever the Manager opens a screen and every 60 seconds while signed in. A notification created by another user appears in the count within 60 seconds without any action. This refresh does not count as activity for AC-34.7.

### US-50: Customers

As a seller, I want to keep the customer list on screen so that every sales order names its customer the same way.

- **AC-50.1** The Customers list (Warehouse, Manager, Seller) shows, per customer: name with id (F-10), "Active" or "Inactive" in words, created by and created time. It can be filtered by active, inactive or all (all to start with). Paging follows F-6. The detail adds the notes, shown as text with their line breaks kept.
- **AC-50.2** Seller and Manager users see Add customer: a required name and optional notes. Below the notes the form says "Only the business contact details needed to deliver. Warehouse, Manager and Seller users can read these notes." (SEC-28). On success the new customer's detail opens.
- **AC-50.3** On a customer's detail, Seller and Manager users see Edit, with the stored name and notes filled in; saving sends both. Example (AC-29.3): after "Deli Norte" is renamed "Deli Norte SA", sales order #1 shows "Deli Norte SA (#4)".
- **AC-50.4** Seller and Manager users see Deactivate on an active customer and Reactivate on an inactive one, each with the F-5 dialog. A deactivated customer is not offered in the sales order customer picker (AC-52.1).
- **AC-50.5** Seller and Manager users see Delete, with the F-5 dialog. On success the list no longer shows the customer. A customer with sales orders is refused with "Deli Norte has sales orders and cannot be deleted. Deactivate it instead." (AC-29.5).
- **AC-50.6** Other refusals follow F-1, for example "A customer named 'Deli Norte' already exists." (AC-29.2). Warehouse users see no Add, Edit, Deactivate, Reactivate or Delete control.

### US-51: Sales orders, list and detail

As a seller or warehouse user, I want to see sales orders on screen so that I know what to ship and what has been shipped.

- **AC-51.1** The list (Warehouse, Manager, Seller) shows, per sales order: sales order number, customer (F-10), customer reference, status, created time, time of the latest status change and number of lines. It can be filtered by status, customer (picker) and item (picker). Filters combine. Paging follows F-6.
- **AC-51.2** The detail shows every value of AC-25.2: number, customer with id and active status, customer reference, status, shipment note, cancellation reason, per line the item, item type, unit and quantity in the order entered, and the history (status, time, user's email, note).
- **AC-51.3** Following F-11, a Created sales order offers Ship (Warehouse, Manager) and Cancel (Seller, Manager). Shipped and Cancelled sales orders offer no action. An unknown number follows F-12.

### US-52: Create a sales order

As a seller, I want to enter a customer's order on screen so that the warehouse knows what to ship.

- **AC-52.1** Seller and Manager users open New sales order: a customer picker offering active customers only (F-10, F-13), an optional customer reference, and lines. Each line has an item picker (any item, raw material or finished product, AC-24.8) and a quantity with the item's unit beside it. Lines can be added and removed; the form starts with one line. On success the new sales order's detail opens, Created.
- **AC-52.2** A line with no item or no quantity shows "Choose an item and enter a quantity, or remove the line." and nothing is sent.
- **AC-52.3** Refusals follow F-1, for example the same item on two lines (AC-24.3), or a customer deactivated by someone else in the meantime: "Customer Deli Norte is deactivated. Reactivate the customer or choose another one." (AC-24.4).
- **AC-52.4** The form checks no stock: a sales order for 600 Bag with 0 Bag in the Warehouse is created (AC-24.2).

### US-53: Ship a sales order

As a warehouse user, I want to record on screen that a customer's order has left so that Warehouse stock goes down and the shipment is on record.

- **AC-53.1** On a Created sales order, Ship opens the F-5 dialog listing each line with its quantity and the item's current Warehouse stock, and an optional shipment note. Confirming ships every line in full (US-26). On success the detail shows Shipped, with who shipped it, when, and the note.
- **AC-53.2** Example (AC-26.3): a refused shipment shows "Not enough stock: Shredded cheese 1 kg bag in Warehouse has 401 Bag; sales order #2 needs 500 Bag." and the sales order stays Created.
- **AC-53.3** Other refusals follow F-1, for example a sales order shipped or cancelled by someone else in the meantime (AC-26.7).

### US-54: Cancel a sales order

As a seller, I want to cancel a sales order on screen, with a reason, so that it no longer counts as work to do or as open demand.

- **AC-54.1** On a Created sales order, Cancel opens the F-5 dialog with a required reason. A blank reason shows "Reason is required." in the dialog and sends nothing. On success the detail shows Cancelled with the reason.
- **AC-54.2** Refusals follow F-1, for example "This sales order has been shipped and cannot be cancelled." (AC-27.2).

### US-55: Write a shipment in one batch (SEC-24)

As a warehouse user, I want a large shipment recorded quickly so that nobody else's save waits for it, now that several people use the product at once in browsers.

- **AC-55.1** With the section 5 data set on the owner's machine, shipping a Created sales order of 500 lines whose stock covers every line answers `200` within 1 second.
- **AC-55.2** While that shipment runs, a receipt recorded by another user at the same moment answers within 1 second.
- **AC-55.3** Every rule of US-26 and US-28 is unchanged: one ShippedToCustomer movement per line, all or nothing, the same messages, and every race in AC-26.10. The existing tests still pass.

### US-56: Units

As a Manager, I want to keep the list of units on screen so that a new installation can be set up without PowerShell and every item is measured consistently.

- **AC-56.1** The Units screen (every role) lists every unit with its name and id, newest first, paged by F-6. With no units it says "No units yet. A Manager adds them."
- **AC-56.2** A Manager sees Add unit: one name field. On success the list shows the new unit (US-06). Refusals follow F-1, for example "Name is required.", "Name must be 50 characters or fewer.", "Name contains a character that is not allowed." and "A unit named 'kg' already exists." (AC-06.2).
- **AC-56.3** A Manager sees Rename on each unit, with the stored name filled in. On success the unit shows its new name, and every item using it shows that name too (the item list reads it from the API). Refusals follow AC-56.2.
- **AC-56.4** A Manager sees Delete on each unit, with the F-5 dialog. On success the unit is no longer listed. A unit used by an item is refused with "Unit is used by one or more items." (AC-06.4), and the unit stays.
- **AC-56.5** Warehouse, Production and Seller users see the list only, with no Add, Rename or Delete control (F-11).

### US-57: Items

As a Manager, I want to keep raw materials and finished products on screen so that orders, recipes, stock and sales orders refer to the same items.

- **AC-57.1** The Items screen (every role) lists, per item: name with id (F-10), type in words ("Raw material" or "Finished product"), unit and description. It can be filtered by type. Paging follows F-6. With no items it says "No items yet. A Manager adds them." Selecting an item opens its detail: the same fields, its creation time and, for a finished product, its recipe (US-58).
- **AC-57.2** A Manager sees Add item: name (required), description (optional), type (raw material or finished product) and unit (a unit picker, F-13). On success the new item's detail opens (US-07). With no units, the unit picker says "No units yet. Add a unit first." and nothing can be saved.
- **AC-57.3** On an item's detail a Manager sees Edit, with the stored name, description, type and unit filled in; saving sends all four (a full replacement, design 5.4). Example (AC-07.3): for Cheese, which has movements, a change of name or description is accepted, and a change of unit from kg to Bag is refused with "Type and unit cannot change once the item is in use."; the form keeps the input.
- **AC-57.4** On an item's detail a Manager sees Delete, with the F-5 dialog. On success the item is no longer listed. An item in use is refused with the API's message, for example "Cheese has movements, recipe lines, order lines or sales order lines and cannot be deleted." (AC-07.4, AC-28.5).
- **AC-57.5** Other refusals follow F-1, for example "An item named 'Cheese' already exists." (AC-07.2), "Name must be 100 characters or fewer." and "Description must be 500 characters or fewer."
- **AC-57.6** Warehouse, Production and Seller users see the list and the detail only, with no Add, Edit or Delete control (F-11).

### US-58: Recipes

As a Manager, I want to set the recipe of a finished product on screen so that production orders are planned from it; and as a warehouse or production user, I want to read it.

- **AC-58.1** A finished product's detail shows its recipe to Warehouse, Production and Manager users: per line, the raw material with id, the amount per unit of finished product and the material's unit, in the stored order, and the time the recipe was last saved. A finished product with no recipe shows "Shredded cheese 1 kg bag has no recipe." (design 5.4) instead of the table. A raw material's detail has no recipe section.
- **AC-58.2** A Seller-only user sees no recipe section on any item, and the front end does not request the recipe for that user (AC-30.6).
- **AC-58.3** On a finished product, a Manager sees Edit recipe (or Set recipe when there is none). The form shows the current lines, or one empty line, each with a raw material picker (raw materials only, F-13) and an amount per unit with the material's unit beside it. Lines can be added and removed. The form says "Saving replaces the whole recipe. Orders already created keep the recipe they were created with." (AC-08.1, AC-08.4).
- **AC-58.4** A line with no material or no amount shows "Choose a material and enter an amount, or remove the line." and nothing is sent. A form with no lines shows "A recipe needs at least one line." and nothing is sent.
- **AC-58.5** Refusals follow F-1 with the input kept, shown next to the line where the API names one, for example "Cheese appears on more than one line.", "Amount per unit must be greater than 0." and "Amount per unit can have at most 6 decimal places." (AC-08.2).
- **AC-58.6** Example (AC-08.3, AC-08.4): the recipe of Shredded cheese 1 kg bag is saved as 1 kg of Cheese per Bag; a new production order for 1,000 Bag then shows 1,000 kg of Cheese planned. The recipe is changed to 1.01 kg; the earlier order still shows 1,000 kg, and a new order for 1,000 Bag shows 1,010 kg.

### US-59: User management

As a Manager, I want to manage staff accounts on screen so that each person can sign in as themselves and do exactly their job.

- **AC-59.1** The Users screen (Manager only) lists, per user: email, roles, "Active" or "Inactive" in words, and creation time, newest first, paged by F-6. Selecting a user opens their detail with the same fields. No screen ever shows a password or password hash (AC-03.4).
- **AC-59.2** Add user takes an email, an initial password entered twice, and one or more of the four roles, chosen with one checkbox per role (Warehouse, Production, Manager, Seller). When the two passwords differ the form shows "The passwords do not match." and sends nothing; with no role chosen it shows "At least one role is required." and sends nothing. On success the new user's detail opens, active, with exactly the roles chosen (AC-03.1).
- **AC-59.3** Refusals on Add user follow F-1, for example "A user with this email already exists." (AC-03.2), "Email is not a valid email address." and the password policy message (AC-03.3).
- **AC-59.4** On a user's detail, Change roles shows the four checkboxes with the current roles ticked. Saving asks for confirmation (F-5). On success the screen says "The new roles apply from this user's next sign-in. To remove access now, deactivate the user." (AC-03.5, SEC-07 runbook).
- **AC-59.5** On a user's detail, Deactivate (for an active user) and Reactivate (for an inactive one) ask for confirmation (F-5). On success the status shows the change (AC-03.6). When a Manager deactivates their own account, the dialog also says "You will be signed out at once."; after confirming, the next request is answered `401` and AC-34.6 applies.
- **AC-59.6** Removing the Manager role from, or deactivating, the last active Manager is refused with "At least one active Manager is required." (AC-03.8), and nothing changes.
- **AC-59.7** On another user's detail, Set password takes a new password entered twice and asks for confirmation (F-5). Different entries show "The passwords do not match." and send nothing; a password that fails the policy shows the policy message. On success the screen says "Password set. Tell the user to sign in with it and change it." (AC-03.7). On the Manager's own detail, Set password is not offered; the Manager uses Change password (US-35), which asks for the current password (SEC-15).
- **AC-59.8** Users without the Manager role see no Users entry in the menu, and opening a user's address follows AC-36.2.

## 18. Non-functional requirements

Section 5 still applies to the API. For the front end:

- **Where it runs:** on the owner's Windows 11 machine, reachable at `localhost` only, like the API (Q105, row 63; row 22). The owner starts it with the same `scripts\run.ps1 -Published` command as today, which then also prints the front end's address. The running product needs no new software on the owner's machine. Building it may need a new tool (row 77). A phone or tablet cannot reach `localhost`, so real phone use waits for the cloud step, the next step after version 1.2 (Q105, Q109).
- **Browsers:** the current versions of Microsoft Edge and Google Chrome on Windows 11 (row 74). QA runs the acceptance tests in both, at the three window sizes of F-15, using the browsers' device preview for the phone and tablet sizes. Phone browsers themselves are not tested in version 1.2.
- **Screen sizes:** one responsive layout for every screen, from a browser window 360 pixels wide (a phone) up to a desktop (Q102, Q109). F-15 states what QA checks. At 200% browser zoom on a 1280 by 720 window every text and every control can still be reached; wide tables may scroll sideways inside their own area.
- **Accessibility:** for every screen in scope, at every width of F-15: every action can be done with the keyboard alone, in a logical order, with the focus always visible; every field has a visible label tied to it; F-1 messages are announced to screen readers and tied to their fields; status, read state and "Above tolerance" are given in words, never by colour alone; text contrast is at least 4.5 to 1; touch targets are at least 24 by 24 CSS pixels; each screen has its own page title; the page declares English as its language. An automated accessibility checker chosen by the tech lead reports no critical or serious problem on any screen (row 75).
- **Performance:** with the section 5 data set (100,000 movements, 1,000 production orders, 500 items, 500 customers, 5,000 sales orders) on the owner's machine: the sign-in screen appears within 3 seconds of opening the address; 95% of screens show their data within 2 seconds of being opened; pickers follow F-13.
- **Language:** English only (Q103). The API's messages are shown word for word.
- **Data in the browser:** the front end stores nothing in the browser: no token, no API data, no preferences (AC-34.5). Everything it shows comes from the API and disappears with the tab.
- **Compatibility:** every existing API client keeps working unchanged: the PowerShell helper of the user guide, the sample requests and every existing test. The base path stays `/api/v1`.
- **Security:** US-37 and section 19.1. The security review is re-opened for this version (phase 6), with the front end in its scope.
- **Technology:** chosen by the tech lead in phase 3. The owner prefers TypeScript or JavaScript for front ends, with mainstream, well-documented tools (CLAUDE.md).

## 19. Changes to the API and to the version 1 and 1.1 parts

### 19.1 Changes to the API and its web server

The front end uses the existing 62 endpoints as they are. It needs no new endpoint and no change to any request, response, status code, message or role. These are the only API-side changes in version 1.2:

| # | Change | Why | Story |
|---|--------|-----|-------|
| A-1 | The API's web server also serves the front end's files, at the root of the same address and port (Q104). Any path outside `/api/`, `/health` and `/openapi/` that is not a file returns the front end's page, so a screen's address can be opened directly (AC-36.3). Every path under `/api/` answers as today, including the `404` ProblemDetails for an unknown path. | One address and one start command for the owner; same origin, so no CORS. | US-34, US-36 |
| A-2 | Security headers: the Content-Security-Policy on the pages (AC-37.5); `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer` and no `Server` header on every response; `Cache-Control: no-store` on `/api/` (AC-37.6). | SEC-10, SEC-11, SEC-27. | US-37 |
| A-3 | No CORS policy: the front end is served from the API's own address (Q104, AC-37.7). | SEC-10. | US-37 |
| A-4 | A shipment's movements are written in one batch. Runs as its own small task before the front-end build (delivery manager's scheduling). | SEC-24, decision #175 (before the web front end). | US-55 |
| A-5 | The JSON output escapes `<`, `>`, `&` and `'` in strings (Q108, AC-37.9). | SEC-27 server hardening. | US-37 |

Not changed, on purpose:

- Authentication stays a bearer token in the `Authorization` header. The front end sets no cookie and the API issues none, so there is no CSRF risk to handle and no antiforgery token. For the same reason the explicit DataProtection configuration on the Later list is not required by version 1.2: the owner chose a token in memory, with no cookie session (Q106).
- The sign-in lock-out (AC-01.4), the 8-hour token with no refresh (AC-01.2), the request limits (row 21) and the role rules (section 4.2).
- Localhost only. Nothing in this version makes the API or the front end reachable from another machine (Q105). The cloud path is the next step after version 1.2.
- Units, items, recipes and users (US-56 to US-59) use the existing endpoints of US-03 and US-06 to US-08 unchanged.

### 19.2 Changes to the version 1 and 1.1 parts

Every change version 1.2 makes to sections 1 to 13. Each is also marked "(v1.2)" in place.

| # | Where | Change |
|---|-------|--------|
| 1 | Status line and introduction at the top | The document now has a version 1.2 part (sections 14 to 20), its questions settled on 2026-11-27 and approved by the owner on 2026-11-27, including assumption rows 92 and 93. |
| 2 | Section 3, Later | "A web front end." is marked as moved into sections 14 to 20. |
| 3 | Section 3, Later, (v1.1) sub-list | The SEC-24 batching item is marked as part of version 1.2 (US-55). The SEC-27 escaping item is marked as part of version 1.2: the front end's rules in US-37, the server escaping in AC-37.9 (Q108). The DataProtection item is marked as not needed by version 1.2, which adds no cookies (19.1, Q106). |
| 4 | Section 3, Later | A "(v1.2)" sub-list of follow-on ideas is added. |
| 5 | Section 5, Platforms and Accessibility | A pointer to section 18 for the web front end. |

Deliberately not changed: section 3 "Out of scope: Any user interface" and section 9's "Any user interface", which describe versions 1 and 1.1 as released; sections 4.1 to 4.3, every user story US-01 to US-33 and every acceptance criterion in them; section 11. The front end follows them all and changes none.

Not part of this document, but affected, for the roles that own them: `docs/02-design.md` gains the screens (phase 2, `ux-designer`, with mockups); `docs/03-architecture.md` gains the front end and the A-1 to A-5 changes; `docs/07-release.md`, `README.md` and `docs/08-user-guide.md` describe the browser way of working; `docs/06-security-review.md` is re-opened.

## 20. Assumptions and open questions for version 1.2

Rows 56 to 66 and row 91 were questions for the owner, Q98 to Q109; the owner settled all of them in a grilling session on 2026-11-27. Rows 67 to 90 and 92 to 97 are assumptions made on the owner's behalf; each can be overruled. No question is open.

| # | Assumption or question | Decided by | Answer |
|---|------------------------|------------|--------|
| 56 | Q98. Version number: v1.2 or v2.0? | Owner | Decided 2026-11-27 (owner): **v1.2**. The API stays backwards compatible at `/api/v1`. |
| 57 | Q99. Which roles and tasks come first? | Owner | Decided 2026-11-27 (owner): **the daily tasks of all four roles**, US-34 to US-55, as proposed (section 16). |
| 58 | Q100. Which further screens join version 1.2: (a) movement history, (b) shipped-totals reports, (c) adjustments, (d) units, items and recipes, (e) user management? | Owner | Decided 2026-11-27 (owner): **(d) and (e) are in version 1.2**, as full stories: units US-56, items US-57, recipes US-58, user management US-59. **(a), (b) and (c) go to the (v1.2) Later sub-list** in section 3 and stay API-only. |
| 59 | Q101. Should the first version be read-only for some roles? | Owner | Decided 2026-11-27 (owner): **no**. Every role gets its write tasks on screen. |
| 60 | Q102. Desktop only, or phone-friendly too? | Owner | Decided 2026-11-27 (owner): **phone-friendly**: one responsive layout from 360 pixels wide up to a desktop (F-15, section 18). How it is tested is row 91 (Q109). |
| 61 | Q103. English only, or Spanish too? | Owner | Decided 2026-11-27 (owner): **English only**. Spanish is on the (v1.2) Later sub-list. |
| 62 | Q104. Is the front end served by the API, or does it run separately? | Owner | Decided 2026-11-27 (owner): **served by the API, at the same address and port** (A-1). No CORS policy (A-3, AC-37.7). |
| 63 | Q105. Localhost only, or reachable on the local network? | Owner | Decided 2026-11-27 (owner): **localhost only**. The owner added "later on will be on cloud": the cloud path (PostgreSQL, HTTPS with HSTS, sign-in rate limit, CD) stays on the Later list as the next step after version 1.2. |
| 64 | Q106. How long does a browser sign-in last? | Owner | Decided 2026-11-27 (owner): **the token lives in memory only**; a reload or a new tab means signing in again (AC-34.5); **automatic sign-out after 15 minutes without activity** (AC-34.7). No cookies, so no DataProtection work in version 1.2 (19.1). |
| 65 | Q107. Are times shown in UTC or in local time? | Owner | Decided 2026-11-27 (owner): **local time on screen, with the offset in the footer** (F-8). Date filters stay whole UTC days and are labelled so; no screen in version 1.2 has one, so this applies when the Later screens (movement history, reports) come. |
| 66 | Q108. Should the API also escape HTML characters in its JSON output in version 1.2 (SEC-27 server hardening)? | Owner | Decided 2026-11-27 (owner): **yes**. A-5 is in version 1.2 (AC-37.9). |
| 67 | The home screen after sign-in is Stock for every role, because it is the one screen every role can open. | PM | Assumed |
| 68 | Status changes, deletes, and changes to a user's roles or password ask for confirmation (F-5); receipts, new records, customer edits, unit renames, item edits, recipe saves and marking read do not, because they are frequent or can be corrected. | PM | Assumed |
| 69 | Send, complementary send and completion fields start empty, with the nominal send or the production storage figure shown beside them, so the user enters what was really weighed or counted rather than confirming a pre-filled figure. | PM | Assumed |
| 70 | The front end calculates no business figure (ranges, estimates, variances, shortfalls); it shows what the API returns. Its own checks are limited to required fields, number format (F-7), at least one role or recipe line, and passwords entered twice matching. | PM | Assumed |
| 71 | Numbers are shown in the API's message format (comma thousands, dot decimals) and entered with a dot for decimals and no thousands separator (F-7). | PM | Assumed |
| 72 | Customer ids are shown everywhere a customer appears; item ids in pickers, on the Stock screen and on the Items screens (F-10). Not on every order line, to keep tables readable. | PM | Assumed |
| 73 | Bidirectional control characters are shown as visible markers, not stripped (AC-37.3), so a disguised name is seen for what it is. Other invisible characters allowed by the name rules (ZWJ and ZWNJ between letters, #171, #181) are shown as they are, because marking them would break the scripts and emoji that need them. | PM | Assumed |
| 74 | Supported browsers are current Microsoft Edge (installed with Windows 11) and Google Chrome, on Windows 11; the phone and tablet widths are checked with their device preview (F-15). Firefox, Safari and real phone browsers are not tested in version 1.2. | PM | Assumed |
| 75 | Accessibility is the concrete list in section 18, taken from WCAG 2.2 level AA, rather than a full WCAG audit. | PM | Assumed |
| 76 | The notification count refreshes every 60 seconds and on every screen change (AC-49.5); nothing else refreshes by itself (F-14). | PM | Assumed |
| 77 | Building the front end may need a tool the owner does not have yet (for example Node.js), on the owner's machine and in CI. The tech lead names it in phase 3; installing it is the owner's call. The front end's tests run in CI with the rest. | PM | Assumed |
| 78 | After a `401`, the user returns to the screen they were on, but typed input is lost (AC-34.6). Keeping it would mean storing it in the browser, which AC-34.5 rules out. | PM | Assumed |
| 79 | Customer delete is on screen for Sellers and Managers (AC-50.5), although rare, because it completes the customer list task of US-29; the API refuses it for any customer with a sales order. | PM | Assumed |
| 80 | Returns are in the first slice (US-48), because a Pending return blocks a send (AC-15.11) and the Warehouse must be able to clear it on screen. | PM | Assumed |
| 81 | Pickers read every page of the item, customer or unit list and filter in the browser (F-13). With 500 records that is 5 requests of 100; no API search endpoint is needed in this version. | PM | Assumed |
| 82 | The SEC-24 batching target is 1 second for a 500-line shipment at the section 5 volume (AC-55.1), in line with section 5's 1-second target. The tech lead may propose another figure, with a measurement. | PM | Assumed |
| 83 | The browser's password manager may fill in the sign-in form. Remembering a password is the user's own choice in their browser. | PM | Assumed |
| 84 | Password fields are emptied after any refusal (F-1, AC-34.3, AC-35.3). | PM | Assumed |
| 85 | The receipt date starts at today's local date (AC-39.1). East of UTC, just after local midnight, that date can be later than today in UTC; the API then refuses it (AC-09.2) and the user picks the day before. The API's rule is not changed. | PM | Assumed |
| 86 | Lists keep the API's page size of 50; there is no page-size control. | PM | Assumed |
| 87 | The customer list starts with all customers, active and inactive, as the API's default (AC-29.6). | PM | Assumed |
| 88 | A front-end path never starts with `/api/`, `/health` or `/openapi/`, so screens and API paths cannot collide (A-1). | PM | Assumed |
| 89 | Superseded by Q100 (row 58), 2026-11-27. The draft's five candidate stories were renumbered: units, items and recipes became US-56 to US-58 and user management US-59; movement history, the shipped-totals reports and adjustments were removed and are on the (v1.2) Later sub-list. | Owner | Superseded 2026-11-27 |
| 90 | Movement history, the shipped-totals reports and adjustments stay possible through the API as today. The user guide keeps its PowerShell sections for them. | PM | Assumed |
| 91 | Q109, raised in the grilling session. What does "phone-friendly" mean while a phone cannot reach `localhost`, and how is it tested? | Owner | Decided 2026-11-27 (owner): **one responsive layout from 360 pixels wide up to a desktop, tested in the browser's device or phone preview** (F-15, section 18). Real phone use comes with the cloud step (HTTPS), because a phone cannot reach `localhost`. Accessibility at 200% zoom still applies. |
| 92 | Units and Items screens can be opened by every role, and a recipe by Warehouse, Production and Manager, because section 4.2 lets those roles read them; only Managers change them (US-56 to US-58). | PM | Assumed |
| 93 | On their own user detail, a Manager is not offered Set password; they use Change password, which asks for the current password (AC-59.7). The API allows a Manager to reset their own password without it (SEC-15); the screen does not invite it. | PM | Assumed |
| 94 | A Manager may deactivate their own account on screen, as the API allows, with a warning that they will be signed out at once (AC-59.5); the last-Manager rule still applies (AC-03.8). | PM | Assumed |
| 95 | Roles are chosen with one checkbox per role (AC-59.2, AC-59.4), so an unknown role or more than 10 roles cannot be sent from the screen. | PM | Assumed |
| 96 | Saving a recipe has no confirmation dialog; the form states that it replaces the whole recipe and that existing orders keep theirs (AC-58.3). | PM | Assumed |
| 97 | On narrow screens a wide table may scroll sideways inside its own area rather than the page (F-15). How each screen rearranges at phone width is for `ux-designer` to design. | PM | Assumed |
