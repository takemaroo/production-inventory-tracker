# Product requirements: Production & Inventory Tracker API

Status: version 1 (sections 1 to 6) approved by the owner on 2026-11-11, written 2026-11-11 from the owner interview of the same day. Version 1.1: Sales orders (sections 7 to 13): approved by the owner on 2026-11-23. Every question is settled by decisions #138 to #151 (section 13).

This document has two parts. Sections 1 to 6 are version 1. Sections 7 to 13 add version 1.1: Sales orders. Where version 1.1 had to change a version 1 section, the change is marked "(v1.1)" in place and listed in section 12. Everything else in sections 1 to 6 is unchanged.

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
- A web front end.
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
- Writing a shipment's movements in one batch, so a sales order with hundreds of lines does not hold the database for seconds (SEC-24, decision #175). Before the web front end.
- Tighter name rules after v1.1 (SEC-25, decision #176): refuse invisible characters at the start or end of a name, count blank-looking characters (Hangul fillers, U+2800) as blank, refuse noncharacters, NUL and tab; joining characters (ZWJ, ZWNJ) stay allowed between letters. A small task right after the v1.1 release, before the web front end.
- "Last changed by" columns on customers (SEC-26, decision #177). v1.1 records changes in the log only.
- A rule that the person who created a sales order cannot ship it (SEC-29, decision #178).
- Escaping HTML-sensitive characters in the API's JSON output and adding `nosniff` (SEC-27, SEC-11); the web front end's own rules (text binding only, strict Content-Security-Policy, ids shown next to customer names) go into its requirements when it is planned.
- The sign-in rate limit above (SEC-05) also covers sales order creation (SEC-30).
- A Development port for the sample requests (`InventoryTracker.Api.http`) that `run.ps1` never uses, so the walk-through cannot write to the real instance (SEC-31).
- A start-up check that refuses to start when the database has migrations this version of the code does not know (an older version started on a newer database), and optionally `run.ps1` refusing a pending migration without a fresh backup (v1.1 release rehearsal).

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

- **Platforms:** an HTTP API exchanging JSON, with no user interface. It must build and run on the owner's Windows 11 machine using the documented commands. It publishes an OpenAPI description listing every endpoint, at least in the development environment.
- **Performance:** with 100,000 movements, 1,000 orders and 500 items in the database, and (v1.1) 500 customers and 5,000 sales orders of 1 to 10 lines each, 95% of requests (the shipped-totals reports of US-32 and US-33 included) answer within 1 second on the owner's machine.
- **Consistency under concurrent use:** when two requests that conflict arrive at the same time (two sends for the same order, sends for two different orders, two movements that together would take stock below zero, and (v1.1) two shipments of the same sales order, a shipment and a cancellation of the same sales order, or deleting a customer while a sales order is created for it), at most one succeeds and stock never goes below zero in either location. Every request that changes data either completes fully or saves nothing.
- **Security:** every endpoint except sign-in requires a valid token. Passwords are stored only as salted hashes. Password policy: at least 8 characters with an upper-case letter, a lower-case letter and a digit. The token signing key, `ADMIN_EMAIL` and `ADMIN_PASSWORD` come from environment variables, never from committed files.
- **Configuration:** the tolerance is a percentage in application settings, default 2. A value below 0 or above 100 stops the application at start-up with a message.
- **Accessibility:** not applicable in version 1 (no user interface).
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
