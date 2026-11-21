# Product requirements: Production & Inventory Tracker API

Status: approved by the owner on 2026-11-11. Written 2026-11-11 from the owner interview of the same day.

Source of truth: this document and the agreed design from the 2026-11-11 interview. Where `README.md` says something different (one warehouse, four movement types, no users, a single "read" flag on notifications, no Cancelled status), this document wins.

## 1. Problem and users

A small food manufacturer turns bulk raw materials into packaged products, for example 1 kg bags of shredded cheese made from cheese bought by the kilogram. The manager needs to know how much raw material each production order should use, how much was actually sent to the line and used, and where the numbers do not match. Today this is tracked by hand or in spreadsheets, so material left over in production storage is forgotten or counted twice, differences are found late, and waste goes unnoticed.

**Primary user:** the production and inventory manager, who plans production orders, approves extra material, and investigates every difference between what was planned, what was used and what was produced.

**Other users:** warehouse staff, who receive raw materials, send them to production and take back returned leftover; production line staff, who register the output, count the leftover and ask for extra material when it runs short.

## 2. Goal for version 1

A manager can run every production order through the API, from creation to registered output, with stock tracked in the warehouse and in production storage, leftover netted against the next order automatically, and every usage or output difference recorded on the order and raised as a notification.

## 3. Scope

In version 1:

- Sign-in with email and password (bearer token), three fixed roles, user administration by Managers, first Manager created from environment variables.
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

- Sales orders that remove finished goods from stock.
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

## 4. User stories and acceptance criteria

### 4.1 Conventions used in every criterion

- **Rejected** means: the API returns an error status, nothing is saved, no stock changes, and the response body contains a readable message stating the reason. Status codes: `400` invalid input, `401` missing, invalid or expired token, `403` the user's roles do not allow the action, `404` the record does not exist, `409` the request conflicts with the current state (order status, available stock, another order in production).
- **Unreadable bodies:** `408` and `413` come from the web server when a request body does not arrive in time or is too large (decision #124). See `docs/02-design.md` section 2.2.
- **Quantities** are decimals. Entered quantities have at most 3 decimal places; recipe amounts per unit have at most 6. Input with more decimal places is rejected with `400`. Computed quantities (planned amount, range limits, required amount) are rounded to 3 decimal places, half away from zero. Percentages are shown rounded to 2 decimal places, but threshold comparisons use the unrounded value.
- **Times** are stored and returned in UTC, in ISO 8601 format.
- **Lists** are returned newest first, in pages: default 50 records, maximum 100, with the total count.
- **t** is the tolerance setting (section 4.3). With the default, t = 2%.

### 4.2 Roles and permissions

A user holds one or more of three roles and may do everything any of their roles allows.

| Action | Warehouse | Production | Manager |
|--------|:---------:|:----------:|:-------:|
| Sign in, change own password | yes | yes | yes |
| View units, items, recipes, stock, movement history, orders, complementary orders, returns | yes | yes | yes |
| Record a stock receipt | yes | | yes |
| Send materials for an order or an approved complementary order | yes | | yes |
| Record receiving a leftover return | yes | | yes |
| Complete an order (output, leftover, reason) | | yes | yes |
| Request a complementary order | | yes | yes |
| Create or cancel an order | | | yes |
| Approve or reject a complementary order | | | yes |
| Instruct or cancel a leftover return | | | yes |
| Make an adjustment | | | yes |
| Manage units, items, recipes | | | yes |
| Read notifications and mark them read | | | yes |
| Manage users | | | yes |

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

  Every movement records its location (or both locations for a transfer), the user who performed it, the UTC time, and optionally the production order or complementary order it belongs to.
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

- **AC-03.1** A Manager creates a user with an email, an initial password and one or more roles from Warehouse, Production, Manager. The response is `201`; the new user can sign in and holds exactly those roles.
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
- **AC-05.3** Every movement, every order and complementary order status change, and every return status change records the user who did it and the UTC time. Reading the record returns both.

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
- **AC-07.3** Name and description can be changed at any time. Type and unit can be changed only while the item has no movements, recipe lines or order lines; otherwise the change is rejected with `409` "Type and unit cannot change once the item is in use."
- **AC-07.4** An item can be deleted only if it has no movements, recipe lines or order lines; otherwise the delete is rejected with `409`.
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

### US-11: Movement history

As a Manager, I want a history of every stock change so that I can audit what happened.

- **AC-11.1** Any signed-in user can list movements. Each shows item, location and signed quantity (both locations for a transfer), type, UTC time, user, linked order or complementary order if any, and note or reason if any.
- **AC-11.2** The list can be filtered by item, location, movement type, order, and a date range (from and to, both included). Filters combine. No matches returns `200` with an empty list.
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

- **AC-14.1** Any signed-in user can list orders, optionally filtered by status (Created, InProduction, Completed, Cancelled).
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
- **AC-22.6** Instructing a return (AC-22.1) and cancelling a return (AC-22.5) are Manager only; recording receipt of a return (AC-22.4) is Warehouse or Manager. A Production-only user gets `403` for instructing, receiving and cancelling; a Warehouse-only user gets `403` for instructing and cancelling. Listing and viewing returns are open to every signed-in role (section 4.2) and do not return `403` for any role.

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
- **Performance:** with 100,000 movements, 1,000 orders and 500 items in the database, 95% of requests answer within 1 second on the owner's machine.
- **Consistency under concurrent use:** when two requests that conflict arrive at the same time (two sends for the same order, sends for two different orders, two movements that together would take stock below zero), at most one succeeds and stock never goes below zero in either location. Every request that changes data either completes fully or saves nothing.
- **Security:** every endpoint except sign-in requires a valid token. Passwords are stored only as salted hashes. Password policy: at least 8 characters with an upper-case letter, a lower-case letter and a digit. The token signing key, `ADMIN_EMAIL` and `ADMIN_PASSWORD` come from environment variables, never from committed files.
- **Configuration:** the tolerance is a percentage in application settings, default 2. A value below 0 or above 100 stops the application at start-up with a message.
- **Accessibility:** not applicable in version 1 (no user interface).
- **Data:** stored in a SQL database (SQLite in version 1). Movements, orders, complementary orders, returns, notifications and users are kept indefinitely and never deleted (users are deactivated). Everything is visible to signed-in users according to section 4.2.
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
| 14 | All signed-in users can read master data, stock, movement history and orders. Only Managers read notifications. | PM | Assumed |
| 15 | Deactivation takes effect immediately; role changes take effect at the user's next sign-in. | PM | Assumed |
| 16 | A Manager added later sees all earlier notifications as unread. | PM | Assumed |
| 17 | The consumption movement type is named ConsumedInProduction. No movement is created for a zero quantity. | PM | Assumed |
| 18 | The application refuses to start if there are no users and `ADMIN_EMAIL` or `ADMIN_PASSWORD` is missing. | PM | Assumed |
| 19 | Owner decision during the build (2026-11-11): when RequiredForActualOutput rounds to 0, the completion succeeds; the usage variance percentage is empty (null) and the line is above tolerance whenever any of that material was used. The UsageVariance notification then shows the difference with "(percentage not applicable)" instead of a percentage. See decisions #49 and #57. | Owner | Decided 2026-11-11 |
| 20 | Owner decision during the build (2026-11-13): every entered quantity is at most 999,999,999.999 in size (so a negative adjustment is at least -999,999,999.999), and a recipe amount per unit is at most 999,999.999999. Above the limit is refused with a validation error. See decision #80. | Owner | Decided 2026-11-13 |
| 21 | Decision during the security review (2026-11-21): request limits. A request body is at most 1 MB. A recipe, and every body that carries lines (send, completion, complementary request, complementary send), has at most 500 lines. Creating a user or changing their roles takes at most 10 roles in the list. A request over any of these three limits is refused and nothing is saved. A `400` response lists at most 100 problems and then says how many more were not shown. Status codes, messages and the order of checks are in `docs/02-design.md` section 7.2. See decisions #122 to #125. | Delivery manager | Decided 2026-11-21 |
| 22 | Owner decision during the security review (2026-11-21): version 1 runs on localhost only, on the owner's machine, so plain HTTP is acceptable. HTTPS (with HSTS) is required before the API is reachable from any other machine. There is no sign-in rate limit; the lock-out of AC-01.4 stays. Both are on the Later list. See decisions #119 and #120. | Owner | Decided 2026-11-21 |
