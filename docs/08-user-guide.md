# User guide: Production & Inventory Tracker API, version 1

How each person does their daily work through the API, in the order a new installation needs it. The rules behind every step are in [`01-prd.md`](01-prd.md); every endpoint, field and message is in [`02-design.md`](02-design.md). To install and start the API, see the [README](../README.md).

Version 1 has no screens. Everything below is an HTTP request. The examples use PowerShell, which every Windows machine has, and a small helper defined in [section 0](#0-before-you-start).

## Who does what

A user holds one or more of three roles and may do everything any of their roles allows.

| Task | Warehouse | Production | Manager | Section |
|------|:---------:|:----------:|:-------:|---------|
| Sign in, change own password | yes | yes | yes | [1](#1-sign-in-and-change-your-password) |
| View units, items, recipes, stock, movements, orders, complementary orders, returns | yes | yes | yes | [5](#5-check-stock-and-movement-history) |
| Add users, set roles, deactivate, reset passwords | | | yes | [2](#2-add-people-and-give-them-roles), [13](#13-change-roles-reset-passwords-and-remove-access) |
| Set up units, items and recipes | | | yes | [3](#3-set-up-units-items-and-recipes) |
| Record a receipt of raw material | yes | | yes | [4](#4-receive-raw-materials) |
| Create or cancel a production order | | | yes | [6](#6-plan-a-production-order), [11](#11-cancel-an-order) |
| Send materials for an order or an approved complementary order | yes | | yes | [7](#7-send-materials-for-an-order), [8](#8-get-extra-material-complementary-orders) |
| Ask for extra material (complementary order) | | yes | yes | [8](#8-get-extra-material-complementary-orders) |
| Approve or reject extra material | | | yes | [8](#8-get-extra-material-complementary-orders) |
| Complete an order: output, leftover, reason | | yes | yes | [9](#9-complete-an-order) |
| Read notifications | | | yes | [10](#10-read-notifications) |
| Return leftover to the warehouse: instruct or cancel | | | yes | [12](#12-return-leftover-to-the-warehouse) |
| Return leftover to the warehouse: record receiving it | yes | | yes | [12](#12-return-leftover-to-the-warehouse) |
| Correct stock or write it off (adjustment) | | | yes | [14](#14-correct-stock-or-write-it-off) |

## Terms

- **Item**: a raw material (bought in, for example Cheese in kg) or a finished product (made here, for example "Shredded cheese 1 kg bag" in Bag). Each item has one unit.
- **Recipe**: how much of each raw material one unit of a finished product needs.
- **Warehouse** and **production storage**: the two stock locations. Stock in each is the sum of its **movements** (every receipt, send, consumption, output, return and adjustment). Neither location can go below zero. Movements are never edited or deleted; a mistake is corrected with a new adjustment.
- **Production order** ("order #12"): a plan to make a quantity of a finished product. Its statuses are Created, InProduction, Completed and Cancelled. Only one order can be InProduction at a time.
- **Send**: the one transfer of raw materials from the Warehouse to production storage for an order.
- **Leftover**: raw material left in production storage after an order. It stays there and is used first by the next order.
- **Complementary order**: a request for extra material for the order that is running. It needs a Manager's approval.
- **Return**: moving leftover from production storage back to the Warehouse.
- **Tolerance** (t): one percentage for the whole system, 2% unless the owner changed it. It sets the accepted send window and the usage-variance threshold.
- **Variance**: the difference between what an order should have used or produced and what it did.

## 0. Before you start

**Tools.**

- **PowerShell**, with the helper below. It works on any installation, Development or Production. Tested in Windows PowerShell 5.1; PowerShell 7 has not been tested.
- **The sample requests**, [`src/InventoryTracker.Api/InventoryTracker.Api.http`](../src/InventoryTracker.Api/InventoryTracker.Api.http), for VS Code's REST Client or Visual Studio 2022 17.12 or later: one request per endpoint, run top to bottom. They are for a **Development** instance with a fresh database (`dotnet run --project src/InventoryTracker.Api`). They read the first Manager's email and password, and the three passwords of the sample Warehouse user they create (`SAMPLE_USER_PASSWORD`, `SAMPLE_USER_RESET_PASSWORD`, `SAMPLE_USER_NEW_PASSWORD`), from `src/InventoryTracker.Api/.env`, which you create yourself ([README, Development](../README.md#development); the names are in `.env.example`). Never run them against your real database: they create fixed units, items and users, and nothing is ever deleted. Port 5133 is also the Production port, so check which API is running first.
- **The OpenAPI description** at `http://localhost:5133/openapi/v1.json` lists every endpoint with its fields and status codes. It exists in **Development only**; the Production instance answers `401`.

**The helper.** Paste this into the PowerShell window you work in. `Get-Token` signs in and asks for the password, so the password never lands in the command history. `Invoke-Api` sends a request with your token. When the API refuses either one, `Get-ApiMessage` shows the status code and the API's reason instead of a bare "(401) Unauthorized": Windows PowerShell 5.1 keeps the reason in the response body, so the helper reads it from there. It first tries `ErrorDetails`, where PowerShell 7 puts the reason, but only Windows PowerShell 5.1 has been tested.

```powershell
$api = 'http://localhost:5133/api/v1'

function Get-ApiMessage($Failure) {
    $response = $Failure.Exception.Response
    if ($null -eq $response) { throw $Failure }   # no answer at all: the API is not running, or the address is wrong
    $text = $Failure.ErrorDetails.Message
    if (-not $text) {
        # PowerShell has already read the body once while building the error, so rewind the stream to its start first.
        $stream = $response.GetResponseStream(); $stream.Position = 0
        $text = (New-Object System.IO.StreamReader($stream)).ReadToEnd()
    }
    "$([int]$response.StatusCode): $((ConvertFrom-Json $text).detail)"
}

function Get-Token([string] $Email) {
    $body = @{ email = $Email; password = (Read-Host "Password for $Email") } | ConvertTo-Json
    try { (Invoke-RestMethod -Method Post "$api/auth/sign-in" -ContentType 'application/json' -Body $body).accessToken }
    catch { Write-Warning (Get-ApiMessage $_) }
}

function Invoke-Api([string] $Method, [string] $Path, $Body) {
    $request = @{ Method = $Method; Uri = "$api$Path"; Headers = @{ Authorization = "Bearer $token" } }
    if ($null -ne $Body) {
        if ($Body -isnot [string]) { $Body = $Body | ConvertTo-Json -Depth 10 }
        $request.ContentType = 'application/json'; $request.Body = $Body
    }
    try { Invoke-RestMethod @request }
    catch { Write-Warning (Get-ApiMessage $_) }
}
```

**How every request works.**

- All paths start with `/api/v1`. Every request except sign-in carries the token (`Authorization: Bearer <token>`); the helper adds it from `$token`.
- Bodies are JSON. The helper turns a PowerShell hashtable such as `@{ name = 'kg' }` into JSON for you. Quantities are plain numbers (`995.5`, never `"995.5"`), with at most 3 decimal places; recipe amounts may have 6.
- Lists come in pages, newest first: `?page=1&pageSize=50` (at most 100). The answer has `items`, `page`, `pageSize` and `totalCount`. Show a list as a table with `(Invoke-Api Get '/units').items | Format-Table`.
- Times are UTC, for example `2026-11-22T01:13:45Z`.
- To see a whole nested answer, pipe it to `ConvertTo-Json -Depth 6`.
- The examples keep each answer in a variable (`$cheese`, `$order`) and use its `id` or `number` later. In your own window, look the number up instead, for example with `Invoke-Api Get '/production-orders?status=Created'`.
- Each person signs in as themselves, normally in their own window. The examples switch people by running `Get-Token` again.

## 1. Sign in and change your password

*Everyone.*

```powershell
$token = Get-Token 'you@yourcompany.example'
```

A token lasts 8 hours. After that every request answers `401` "Your sign-in is missing or has expired. Sign in again." and you run `Get-Token` again. There is no sign-out.

Change your own password (at least 8 characters, with an upper-case letter, a lower-case letter and a digit):

```powershell
$change = @{ currentPassword = (Read-Host 'Current password'); newPassword = (Read-Host 'New password') } | ConvertTo-Json
Invoke-Api Post '/auth/change-password' $change
$token = Get-Token 'you@yourcompany.example'
```

Success prints nothing (`204`). It ends every session of yours, so sign in again.

If sign-in fails, `Get-Token` prints a warning with the reason, and `$token` is left empty:

- `401: Invalid email or password.` The email is unknown, the password is wrong, or the account is deactivated. The message is the same for all three on purpose.
- `401: Account temporarily locked. Try again later.` Five wrong passwords in a row, at sign-in or at change-password, lock the account for 15 minutes, even for the right password. Stop retrying. Wait, or ask a Manager to reset your password, which also clears the lock.

For the first Manager, the email is exactly the `ADMIN_EMAIL` of the API's very first start (letter case does not matter). The account was created once, with the values set at that moment; changing `ADMIN_EMAIL` or `ADMIN_PASSWORD` later does nothing. The first Manager changes the first password at once, as the README says under [First password](../README.md#first-password).

## 2. Add people and give them roles

*Manager.*

Create each person with an email, an initial password and one or more roles (`Warehouse`, `Production`, `Manager`). Start with a **second Manager**: with only one, a locked or forgotten Manager password cannot be reset by anyone ([README, Keep two active Managers](../README.md#keep-two-active-managers)).

```powershell
$maria = Invoke-Api Post '/users' @{ email = 'maria.lopez@yourcompany.example'; password = (Read-Host 'Initial password for Maria'); roles = @('Manager') }
$ana   = Invoke-Api Post '/users' @{ email = 'ana.ruiz@yourcompany.example';    password = (Read-Host 'Initial password for Ana');   roles = @('Warehouse') }
$luis  = Invoke-Api Post '/users' @{ email = 'luis.mendez@yourcompany.example'; password = (Read-Host 'Initial password for Luis');  roles = @('Production') }
(Invoke-Api Get '/users').items | Format-Table id, email, roles, isActive
```

Give each person their initial password privately; they change it at their first sign-in (section 1). A user can hold up to 10 roles in the list (there are only three). Users are never deleted. To stop someone working, deactivate them (section 13).

Common refusals: `409` "A user with this email already exists." (emails are compared ignoring case); `400` "Password must be at least 8 characters and include an upper-case letter, a lower-case letter and a digit."; `400` "Unknown role 'Boss'. Roles are Warehouse, Production and Manager."

## 3. Set up units, items and recipes

*Manager. Everyone can read them.*

Units first, then items, then the recipe of each finished product.

```powershell
$kg  = Invoke-Api Post '/units' @{ name = 'kg' }
$bag = Invoke-Api Post '/units' @{ name = 'Bag' }

$cheese  = Invoke-Api Post '/items' @{ name = 'Cheese'; type = 'RawMaterial'; unitId = $kg.id }
$product = Invoke-Api Post '/items' @{ name = 'Shredded cheese 1 kg bag'; description = 'Retail pack'; type = 'FinishedProduct'; unitId = $bag.id }

Invoke-Api Put "/items/$($product.id)/recipe" @{ lines = @( @{ itemId = $cheese.id; amountPerUnit = 1 } ) }
```

The recipe says: one Bag needs 1 kg of Cheese, in the raw material's unit. Saving a recipe replaces the whole previous list. A recipe has 1 to 500 lines, each raw material once, each amount above zero with at most 6 decimal places.

Rules worth knowing:

- Unit names (1 to 50 characters) and item names (1 to 100) must be unique, ignoring case.
- An item's name and description can change at any time (`PUT /items/{id}` with all four fields). Its type and unit cannot change once it has movements, recipe lines or order lines: `409` "Type and unit cannot change once the item is in use."
- Only unused units and items can be deleted (`DELETE /units/{id}`, `DELETE /items/{id}`). A unit used by an item gives `409` "Unit is used by one or more items."
- Changing a recipe never changes existing orders. Each order keeps a copy of the recipe from the moment it was created.

## 4. Receive raw materials

*Warehouse or Manager.*

```powershell
$token = Get-Token 'ana.ruiz@yourcompany.example'
Invoke-Api Post '/receipts' @{ itemId = $cheese.id; quantity = 1200; receiptDate = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd'); note = 'Delivery note 4471' }
```

This records a Received movement that adds 1,200 kg to the Warehouse. `receiptDate` is the delivery date, `yyyy-MM-dd`, not later than today in UTC. The note is optional, up to 500 characters.

Refusals: `400` "Receipts are for raw materials only." (finished products enter stock only through order completion or an adjustment); `400` "Receipt date cannot be later than today (UTC)."; `400` "Quantity can have at most 3 decimal places."

## 5. Check stock and movement history

*Everyone.*

```powershell
Invoke-Api Get "/stock/$($cheese.id)"
(Invoke-Api Get '/stock?itemType=RawMaterial').items | Format-Table itemName, unit, warehouse, productionStorage, freeLeftover
(Invoke-Api Get "/movements?itemId=$($cheese.id)").items | Format-Table occurredAt, type, warehouseChange, productionStorageChange, orderNumber
```

For one raw material, `GET /stock/{itemId}` shows:

| Field | Meaning |
|-------|---------|
| `warehouse`, `productionStorage` | Stock in each location: the sum of its movements. |
| `committedToOrder`, `committedOrderNumber` | What the order now InProduction has received of this material (leftover applied plus all sends), and that order's number. |
| `pendingReturns` | Quantity in returns that a Manager has instructed and the Warehouse has not yet received. |
| `freeLeftover` | Production storage stock minus committed material minus pending returns: what the next order can use. |

For finished products the last four are `null`.

The movement list takes these filters, which combine: `itemId`, `location` (`Warehouse` or `ProductionStorage`), `type` (`Received`, `SentToProduction`, `ConsumedInProduction`, `ProducedOutput`, `ReturnedToWarehouse`, `Adjustment`), `orderNumber` (also matches that order's complementary orders), `complementaryOrderNumber`, `from` and `to` (dates, both included). Each movement shows the user and the UTC time. A transfer fills both `warehouseChange` and `productionStorageChange`.

## 6. Plan a production order

*Manager.*

```powershell
$token = Get-Token 'you@yourcompany.example'
$order = Invoke-Api Post '/production-orders' @{ finishedProductId = $product.id; orderedQuantity = 1000 }
$order.number
$order.lines | Format-List itemName, unit, plannedAmount, estimate
```

The order gets the next number, status Created, and one line per recipe line. **Planned amount** = amount per unit × ordered quantity: 1 kg × 1,000 bags = 1,000 kg. Creating an order moves no stock.

While the order is Created, each line shows an **estimate**, recalculated every time the order is read:

| Field | Meaning |
|-------|---------|
| `leftoverApplied` | Free leftover this order would use if it were sent now (section [How the numbers work](#how-the-numbers-work)). |
| `nominalSend` | Planned amount minus that leftover: what the Warehouse should weigh out. |
| `sendRangeMin`, `sendRangeMax` | The send the API will accept. |
| `tolerancePercent` | The tolerance in force. |

With no leftover, 1,000 kg planned gives a nominal send of 1,000 kg and a range of 980 to 1,020 kg.

An order cannot be edited. To change it, cancel it (section 11) and create a new one. Several orders can be Created at once, and an order can be created while another is InProduction.

Refusals: `400` "Orders are for finished products only. Cheese is a raw material."; `400` "... has no recipe. Set its recipe before creating an order."

## 7. Send materials for an order

*Warehouse or Manager.*

Read the order, weigh out each material within its range, and send all lines in one request. There is exactly one send per order.

```powershell
$token = Get-Token 'ana.ruiz@yourcompany.example'
(Invoke-Api Get "/production-orders/$($order.number)").lines | Format-Table itemName, plannedAmount, @{ n = 'min'; e = { $_.estimate.sendRangeMin } }, @{ n = 'max'; e = { $_.estimate.sendRangeMax } }
Invoke-Api Post "/production-orders/$($order.number)/send" @{ lines = @( @{ itemId = $cheese.id; quantity = 1000 } ) }
```

Every line of the order must appear once. A quantity of 0 is allowed where the range starts at 0 (the leftover already covers the need); no movement is made for it. On success the order becomes InProduction, the leftover applied is fixed on each line, and a SentToProduction movement moves each quantity from the Warehouse to production storage.

Refusals:

- `400` "Cheese: send must be between 980 and 1,020 kg (planned 1,000 kg, leftover applied 0 kg)." Adjust the quantity and send again. Every failing line is named.
- `409` "Not enough stock: Cheese in Warehouse has 183 kg." Record the missing receipt first (section 4).
- `409` "Order #N is in production. Only one order can be in production at a time." Wait until that order is completed or cancelled.
- `409` "Return #R of Cheese is waiting to be received by the warehouse. Record it first." Receive the return (section 12), then send.
- `409` "Materials have already been sent for this order. Raise a complementary order for more material." See section 8.
- `400` "Every order line needs a quantity. Missing: Cheese."

## 8. Get extra material: complementary orders

When the running order needs more material, Production asks, a Manager decides, and the Warehouse sends. Each request has its own number and is separate from the order's one send.

**Ask** (*Production or Manager*), while the order is InProduction, for materials on that order, with a reason of 1 to 500 characters:

```powershell
$token = Get-Token 'luis.mendez@yourcompany.example'
$extra = Invoke-Api Post "/production-orders/$($order.number)/complementary-orders" @{ reason = 'Hopper spill at start of run'; lines = @( @{ itemId = $cheese.id; requestedAmount = 20 } ) }
$extra.number
```

It starts as PendingApproval, and every Manager gets a ComplementaryAwaitingApproval notification.

**Approve or reject** (*Manager*):

```powershell
$token = Get-Token 'you@yourcompany.example'
(Invoke-Api Get '/complementary-orders?status=PendingApproval').items | Format-Table number, orderNumber, reason
Invoke-Api Post "/complementary-orders/$($extra.number)/approve" | Format-List number, status
```

To reject instead: `Invoke-Api Post "/complementary-orders/<number>/reject" @{ note = 'Use the spare from order #11' }` (the note is optional). A rejected request can never be sent. A Manager may approve a request they raised themselves.

**Send** (*Warehouse or Manager*), once, within requested amount ± tolerance. No leftover is netted here. For 20 kg the range is 19.6 to 20.4 kg.

```powershell
$token = Get-Token 'ana.ruiz@yourcompany.example'
(Invoke-Api Get '/complementary-orders?status=Approved').items | Format-Table number, orderNumber
Invoke-Api Post "/complementary-orders/$($extra.number)/send" @{ lines = @( @{ itemId = $cheese.id; quantity = 20 } ) } | Format-List number, status
```

The quantity is added to the order line's `complementarySends`. Requests still PendingApproval or Approved when the order is completed or cancelled become Cancelled by themselves, with the note "Parent order completed" or "Parent order cancelled". There is no other way to cancel one.

Refusals: `409` "Order #3 is Created. Complementary orders can be raised only while the order is in production."; `409` "Complementary order #2 is PendingApproval. Only an Approved complementary order can be sent."; `400` "Cheese: send must be between 19.6 and 20.4 kg (requested 20 kg)."

## 9. Complete an order

*Production or Manager.* Once, at the end of the run.

Count what is physically left of each material in production storage, **in total**, including any leftover that was already there before this order. Enter the produced quantity, that leftover for every line, and a reason if any leftover is above zero.

```powershell
$token = Get-Token 'luis.mendez@yourcompany.example'
$done = Invoke-Api Post "/production-orders/$($order.number)/complete" @{ producedQuantity = 1001; lines = @( @{ itemId = $cheese.id; leftover = 4 } ); reason = 'Last bag line jammed' }
$done | Format-List number, status, producedQuantity, outputVariance, outputVariancePercent, toleranceAtCompletionPercent
$done.lines | Format-List itemName, leftoverApplied, warehouseSend, complementarySends, leftoverEntered, actualUsage, requiredForActualOutput, usageVariance, usageVariancePercent, usageVarianceAboveTolerance
```

On success the order becomes Completed; a ProducedOutput movement adds 1,001 Bag to the Warehouse; a ConsumedInProduction movement removes what was used from production storage, leaving exactly the leftover you entered (4 kg). That leftover stays in production storage for the next order. The variances are recorded on the order and the notifications are raised (section 10).

For this example the order shows: actual usage 1,016 kg, required for actual output 1,001 kg, usage variance +15 kg (+1.50%, not above 2%), output variance +1 Bag (+0.10%). The next section explains these numbers.

Refusals: `400` "A reason is required when leftover is recorded."; `400` "Leftover for Cheese cannot exceed the 10 kg in production storage."; `409` "Order #3 has not been sent yet. Send its materials before completing it."; `409` "Output for order #1 has already been registered."

A produced quantity must be above zero. If a run produced nothing usable, a Manager cancels the order (section 11) and writes off the material (section 14).

## How the numbers work

**The tolerance window for a send.** The tolerance t is 2% unless the owner changed `Inventory__TolerancePercent`. Scales are not exact, so a send is accepted when *leftover applied + send* lies between *planned × (1 − t)* and *planned × (1 + t)*, limits included. The window's width always comes from the full planned amount, even when leftover covers part of it.

- Planned 1,000 kg, no leftover: send 980 to 1,020 kg.
- Planned 1,000 kg, 4 kg leftover applied: send 976 to 1,016 kg; nominal send 996 kg.
- Planned 30 kg, 50 kg of free leftover: leftover applied 30 kg, nominal send 0, range 0 to 0.6 kg. The 20 kg not needed stays where it is, for later orders.

A complementary send uses the same t around the requested amount, with no leftover: 20 kg requested gives 19.6 to 20.4 kg.

**The leftover rule.** Leftover stays in production storage. It is not sent back unless a Manager instructs a return.

- *Free leftover* of a material = production storage stock − what the order now InProduction has of it − pending returns. Never below zero.
- Free leftover goes to Created orders **oldest first**. Each order takes the smaller of its planned amount and what older Created orders have not already taken. Each kilogram goes to one order only.
- An order's leftover is fixed at the moment it is sent. Until then the estimate can change: cancelling an older order releases its share to the next one; a write-off or a return lowers it.
- A newer order may be sent before an older one, but it only gets leftover the older orders have not taken.
- *Carried leftover*: leftover that was in production storage when this order was sent but was not applied to it (more than it needed, or reserved for an older order). It is usually zero. At completion you enter the total you see; the API subtracts the carried leftover itself.

**Variance at completion.** For each material line:

- *Required for actual output* = amount per unit (from the order's copy of the recipe) × produced quantity. 1 kg × 1,001 = 1,001 kg.
- *Leftover remaining* = leftover entered − carried leftover. 4 − 0 = 4 kg.
- *Actual usage* = leftover applied + warehouse send + complementary sends − leftover remaining. 0 + 1,000 + 20 − 4 = 1,016 kg.
- *Usage variance* = actual usage − required = +15 kg, and as a percentage of required, +1.50%. Positive means more was used than the output needed.
- A **UsageVariance** notification is raised when the percentage is above t in either direction (over-use or under-use). Exactly 2.00% raises none; 2.0001% raises one, although it shows as 2.00%. The API decides this, in `usageVarianceAboveTolerance`; do not judge it from the rounded figure.

For the order as a whole:

- *Output variance* = produced − ordered = 1,001 − 1,000 = +1 Bag, +0.10%. Any difference other than zero raises an **OutputVariance** notification; the tolerance does not apply.

The tolerance in force is stored on each completed order (`toleranceAtCompletionPercent`), so changing it later changes nothing recorded. No variance ever changes stock by itself: a person decides whether an adjustment is needed (section 14).

Quantities are rounded to 3 decimal places and percentages are shown to 2, both half away from zero. When the required amount rounds to 0, the percentage is empty (`null`) and the line counts as above tolerance whenever any material was used.

## 10. Read notifications

*Manager only.* Other roles get `403`.

```powershell
$token = Get-Token 'you@yourcompany.example'
$unread = Invoke-Api Get '/notifications?unreadOnly=true'
$unread.totalCount
$unread.items | Format-List id, type, orderNumber, message
```

There are five types, each with its figures in `data`:

| Type | Raised when |
|------|-------------|
| `UsageVariance` | A completed line's usage variance is above the tolerance (one per material). |
| `OutputVariance` | A completed order produced more or less than ordered. |
| `LeftoverAtCompletion` | An order was completed with leftover above zero (one per completion, listing every material and the reason). |
| `ComplementaryAwaitingApproval` | Someone asked for extra material. |
| `OrderCancelledWithMaterial` | An InProduction order was cancelled while its materials were still in production storage. |

Example messages from a real run:

```
Order #1: produced 1,001 Bag, ordered 1,000 Bag. Difference +1 Bag (+0.10%).
Order #1 was completed with leftover in production storage: Cheese 4 kg. Reason: "Last bag line jammed"
```

Read status is per Manager: marking a notification read hides it from your unread list only.

```powershell
Invoke-Api Post "/notifications/$($unread.items[0].id)/read"
Invoke-Api Post '/notifications/read-all'
```

Both print nothing (`204`). Notifications are never deleted. A Manager added later sees every earlier notification as unread.

## 11. Cancel an order

*Manager.* A Created or InProduction order, with a reason of 1 to 500 characters.

```powershell
$second = Invoke-Api Post '/production-orders' @{ finishedProductId = $product.id; orderedQuantity = 50 }
Invoke-Api Post "/production-orders/$($second.number)/cancel" @{ reason = 'Customer order withdrawn' } | Format-List number, status, cancellationReason
```

Cancelling never moves stock.

- A **Created** order releases its share of leftover to the next oldest Created order.
- An **InProduction** order leaves everything it was sent in production storage, as free leftover, and another order can then be sent. If any of its materials has stock in production storage, Managers get an OrderCancelledWithMaterial notification. Then decide: leave the material for the next order, return it (section 12), or write it off (section 14).
- Its complementary orders still PendingApproval or Approved become Cancelled.

Refusals: `400` "Reason is required."; `409` "This order is cancelled."; `409` "Order #1 is completed and cannot be cancelled."

## 12. Return leftover to the warehouse

Two steps: a Manager instructs the return, then the Warehouse moves the material and records it.

**Instruct** (*Manager*). Check free leftover first; you can return at most that. A link to an order is optional.

```powershell
(Invoke-Api Get "/stock/$($cheese.id)").freeLeftover
$return = Invoke-Api Post '/returns' @{ itemId = $cheese.id; quantity = 4; reason = 'Not needed this week'; orderNumber = $order.number }
$return | Format-List number, status, quantity
```

The return is Pending. No stock moves yet, but the quantity stops counting as free leftover, and **no order using Cheese can be sent until the return is received or cancelled**.

**Receive** (*Warehouse or Manager*):

```powershell
$token = Get-Token 'ana.ruiz@yourcompany.example'
(Invoke-Api Get '/returns?status=Pending').items | Format-Table number, itemName, quantity, unit
Invoke-Api Post "/returns/$($return.number)/receive" | Format-List number, status
```

A ReturnedToWarehouse movement moves the quantity from production storage to the Warehouse.

**Cancel** a Pending return made by mistake (*Manager*): `Invoke-Api Post "/returns/<number>/cancel"`. The quantity is free leftover again.

Refusals: `409` "Not enough free leftover: Cheese has 0 kg free in production storage."; `409` "Cheese is in use by order #4, which is in production. Return it after the order is completed or cancelled."; `409` "Return #1 is Received. Only a Pending return can be received or cancelled."

## 13. Change roles, reset passwords and remove access

*Manager.*

```powershell
$token = Get-Token 'you@yourcompany.example'
Invoke-Api Put "/users/$($luis.id)/roles" @{ roles = @('Warehouse', 'Production') } | Format-List email, roles
Invoke-Api Put "/users/$($luis.id)/password" @{ newPassword = (Read-Host 'New password for Luis') }
```

- **New roles take effect at the user's next sign-in.** A token issued before the change keeps the old roles until it expires, up to 8 hours.
- A password reset prints nothing (`204`), ends that user's sessions and clears a lock-out.
- At least one active Manager must remain: removing the Manager role from, or deactivating, the last one gives `409` "At least one active Manager is required." Keep two (section 2).

### Remove someone's access

To remove someone's access **now**, deactivate them. Every token they hold stops working at once, and they cannot sign in. If they should continue with different roles, set the roles, then reactivate them; they sign in again with only the new roles.

```powershell
Invoke-Api Post "/users/$($luis.id)/deactivate" | Format-List email, isActive
Invoke-Api Put "/users/$($luis.id)/roles" @{ roles = @('Production') } | Format-List email, roles
Invoke-Api Post "/users/$($luis.id)/reactivate" | Format-List email, isActive
```

Users are never deleted, so their email stays on every movement and order they touched.

## 14. Correct stock or write it off

*Manager.* After a physical count, or for spoiled material: an adjustment changes one location by a signed quantity, with a reason of 1 to 500 characters.

```powershell
Invoke-Api Post '/adjustments' @{ itemId = $cheese.id; location = 'Warehouse'; quantity = -2; reason = 'Spoiled' }
```

`location` is `Warehouse` or `ProductionStorage`. A positive quantity adds, a negative one removes, zero is refused. A write-off in production storage also lowers the next order's leftover estimate.

Refusals: `409` "Not enough stock: Cheese in Production storage has 0 kg." (stock cannot go below zero); `409` "Cheese in production storage is in use by order #4. Adjust it after the order is completed or cancelled."; `400` "Finished products are adjusted in the Warehouse only."

## Common errors and what to do

Every refusal has a status code, a stable `code` and a readable `detail`, which the helper shows. Nothing is saved when a request is refused.

| Status | What it means | What to do |
|--------|---------------|------------|
| `400` | The request is wrong: a missing or invalid field, too many decimal places, a send outside its range. `detail` names each problem (at most 100, then "… more problems are not shown."). | Fix the fields named and send again. |
| `401` "Your sign-in is missing or has expired. Sign in again." | No token, an expired token (8 hours), a token from before a password change, a deactivation or a new signing key. | Run `Get-Token` again. |
| `401` "Invalid email or password." / "Account temporarily locked. Try again later." | Sign-in failed, or five failures locked the account. | Check the email and password; wait 15 minutes or ask a Manager for a reset. |
| `403` "Your roles do not allow this action. It needs the Manager role." | Your roles do not include the one named. | Ask someone with that role, or a Manager to change your roles (they apply at your next sign-in). |
| `404` "Order #999 not found." / "No endpoint matches this path." | The number or id does not exist, or the path is mistyped. | Check the number (list the records) or the path. |
| `408` "The request body arrived too slowly. Send it again." | The body did not arrive in time. | Send it again. |
| `409` | The request conflicts with the current state: status, stock, another order in production, a pending return, a duplicate name, the last Manager. `detail` says which. | Do the step it names first (receive stock, receive the return, wait for the other order), then try again. |
| `409` "The data changed while your request was being processed. Read it again and retry." | Someone else changed the same data at the same moment. | Read the record again and retry. |
| `413` "The request body is too large. The limit is 1 MB." | The body is over 1 MB. | Send less in one request. |
| `400` "At most 500 lines are allowed." / "At most 10 roles are allowed." | A recipe, send, completion or complementary request with more than 500 lines, or a user with more than 10 roles. | Split or shorten the request. |
| `500` "An unexpected error occurred." | A fault in the API. Details are in the API's console log, matched by `traceId`. | Report it with the time and the request. |

The complete list of messages is in [`02-design.md` section 7](02-design.md#7-messages).

## Limits

- Quantities: at most 3 decimal places and at most 999,999,999.999 (an adjustment at least -999,999,999.999). Recipe amounts per unit: at most 6 decimal places and at most 999,999.999999.
- Text: unit names 1 to 50 characters; item names 1 to 100; descriptions, reasons and notes up to 500; emails up to 256. An emoji counts as one character.
- A request body is at most 1 MB; a recipe, send, completion or complementary request at most 500 lines; a user's role list at most 10 entries.
- Lists: at most 100 per page.
- One order InProduction at a time; one send per order; output registered once.
- Nothing is ever deleted except unused units and items. Movements, orders, complementary orders, returns, notifications and users stay for ever.
