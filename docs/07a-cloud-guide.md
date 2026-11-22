# Cloud guide: running the API on Azure App Service

This is written guidance only. Nobody has created an account, deployed anything or spent money on the owner's behalf, and this repository does not do it automatically. Every step marked **[ACCOUNT]** creates an account or identity, and every step marked **[COSTS MONEY]** starts a charge. Do those steps only when the owner has decided to go to the cloud.

Azure App Service is the default because the owner has not chosen a provider, the stack is .NET, and App Service runs .NET 10 without containers. The same shape works elsewhere; section 12 maps each piece to AWS, Google Cloud and a plain virtual server.

Prices are rough list prices for US and EU regions, checked against public price pages in late 2026 but not confirmed for your region or currency. Always check the [Azure pricing calculator](https://azure.microsoft.com/pricing/calculator/) before creating anything.

## 1. Stop: what must change in the product first

Version 1 is built and security-reviewed for **localhost only** (owner decision #119). The security review's "clear to release" verdict no longer holds once the API can be reached from another machine (`docs/06-security-review.md` section 7.1). Before any of the steps below go live, the delivery manager must route these to the developers and then re-open the security review:

| # | Change | Why | Who |
|---|--------|-----|-----|
| 1 | **Move from SQLite to Azure Database for PostgreSQL.** What the port involves is in `docs/03-architecture.md` section 5.2 ("PostgreSQL later": both transaction helpers, the WAL pragma) and section 9, risk 6 (new migrations for the provider, the SQLite-specific test seeds, order-number gaps). | See section 2 below. | `tech-lead`, `backend-developer` |
| 2 | **Add `app.UseHsts()` in Production** and make HTTPS the only way in (SEC-03). App Service supplies the certificate and the HTTPS-only redirect (step 6), but only the app can send the HSTS header. | Without HTTPS, passwords and 8-hour tokens travel readable. | `backend-developer` |
| 3 | **Add a sign-in rate limit** (SEC-05: ASP.NET Core's rate limiter on sign-in and change-password, partitioned by client address, answering `429`). Revisit the lock-out abuse (SEC-04). | Anyone on the internet can reach sign-in. | `backend-developer`, owner decision for SEC-04 |
| 4 | **Read the client address and scheme from App Service's forwarded headers** (`X-Forwarded-For`, `X-Forwarded-Proto`), trusting only App Service's front end. Step 8 turns on the built-in support; the rate limiter of item 3 must use the right address. | Behind App Service's front end the app sees the front end's address and plain HTTP. | `backend-developer` |
| 5 | **Keep exactly one instance.** The sign-in lock-out is kept in the API process (architecture section 9, risk 9). Do not scale out, and do not turn on autoscale, until the lock moves into the database. | Two instances could let simultaneous wrong passwords get past the 5-try lock-out. | owner (setting), `backend-developer` (later fix) |
| 6 | **Re-run the security review** against the deployed configuration. SEC-03 and SEC-05 become blocking (`docs/06-security-review.md` 7.1). | Release condition. | `security-reviewer` |

## 2. Why not SQLite in the cloud

SQLite is a single file on the local disk, opened directly by one process. That fits one machine and breaks on a cloud app host:

- **The disk can be temporary.** App Service may move your app to another machine (after maintenance, a restart or a scale operation). Anything written outside the persistent `/home` folder is lost when that happens. Containers on other platforms usually have no persistent disk at all.
- **The persistent disk is a network share.** App Service's `/home` is shared storage over the network. SQLite's file locking and write-ahead log need a real local disk; on a network share they are slow and can corrupt the database.
- **There can be several instances.** Scaling out, or a deployment slot swap, runs two copies of the app at once, each opening the same file.
- **SQLite has one writer at a time.** The app relies on that (`BEGIN IMMEDIATE`, architecture 5.2). It works for one process on one disk, not for several processes over a network.

Azure Database for PostgreSQL is a managed server: Azure runs it, patches it, backs it up every day and can restore it to any point in the backup window. The code was designed for this move (architecture section 1: "PostgreSQL later means changing the provider, not rewriting queries").

## 3. What you will create

| Piece | Azure service | Purpose |
|-------|---------------|---------|
| Resource group | Resource group (free) | One folder holding everything below, so it can all be deleted in one step (section 11). |
| Web server | App Service plan (Linux) and a Web App | Runs the published API on .NET 10, supplies HTTPS on `https://<app>.azurewebsites.net`. |
| Database | Azure Database for PostgreSQL flexible server | The data, with automatic backups. |
| Secrets | Azure Key Vault | Holds `JWT_SIGNING_KEY`, the first Manager's password and the database connection string. The Web App reads them with its own managed identity, so no secret is stored in App Service settings or in the repository. |
| Logs | App Service log stream (free); optionally Application Insights | Start-up messages, errors, sign-in records. |

## 4. Prerequisites on your machine

- An Azure account and subscription. **[ACCOUNT]** Creating one asks for a payment card even for the free offer. A new account normally includes a time-limited credit and 12 months of some services free, including a small PostgreSQL server; check the current terms on azure.microsoft.com/free.
- Azure CLI (free, from Microsoft: `winget install Microsoft.AzureCLI`). Installing it is the owner's call. Check with `az version`.
- Everything in `docs/07-release.md` section 1, and the product changes of section 1 above, merged and tested.

All commands below are for PowerShell on Windows, from the repository root. Replace `<unique>` with something only you would use (for example your initials and a number): app, vault and database names are global across Azure.

```powershell
$rg       = 'rg-inventory-tracker'
$location = 'westeurope'                    # pick the region nearest your users: az account list-locations -o table
$plan     = 'plan-inventory-tracker'
$app      = 'inventory-tracker-<unique>'     # becomes https://inventory-tracker-<unique>.azurewebsites.net
$vault    = 'kv-inventory-<unique>'          # 3 to 24 letters, digits and dashes
$pg       = 'pg-inventory-<unique>'
```

## 5. Step by step

### Step 1. Sign in and set a budget alert **[ACCOUNT]**

```powershell
az login
az account show --query "{subscription:name, id:id}" -o table
```

In the Azure portal, open **Cost Management > Budgets** and create a monthly budget (for example 40 USD) with an email alert at 80%. Budgets are free and are the simplest protection against surprises. An alert does not stop spending; tearing down (section 11) does.

### Step 2. Create the resource group (free)

```powershell
az group create --name $rg --location $location
```

### Step 3. Create the PostgreSQL server **[COSTS MONEY]** (about 15 to 20 USD a month, or free for 12 months on a new account)

Generate the database password locally, keep it in a variable only, and never paste it into a file:

```powershell
# 64 random characters from the signing-key script, keeping letters and digits only.
$pgPassword = (powershell -NoProfile -ExecutionPolicy Bypass -File scripts\new-signing-key.ps1) -replace '[^A-Za-z0-9]', ''

az postgres flexible-server create `
  --resource-group $rg --name $pg --location $location `
  --tier Burstable --sku-name Standard_B1ms --storage-size 32 --version 17 `
  --admin-user inventoryadmin --admin-password $pgPassword `
  --backup-retention 14 `
  --public-access 0.0.0.0

az postgres flexible-server db create --resource-group $rg --server-name $pg --database-name inventory
```

- `--public-access 0.0.0.0` allows connections from Azure services only (not from the internet at large). It is the simplest setup. The stronger option is private networking (a virtual network with App Service "VNet integration" and `--public-access None`), which keeps the database off public addresses entirely; it costs nothing extra on these tiers but adds several steps.
- Connections must use TLS; that is the server's default (`require_secure_transport`).
- `--backup-retention 14` keeps 14 days of automatic backups (7 to 35 allowed). Backup storage up to the size of the database is included.

### Step 4. Create the App Service plan and the Web App **[COSTS MONEY]** (B1: about 13 USD a month)

```powershell
az appservice plan create --resource-group $rg --name $plan --is-linux --sku B1 --number-of-workers 1
az webapp create --resource-group $rg --plan $plan --name $app --runtime "DOTNETCORE:10.0"
```

If `DOTNETCORE:10.0` is refused, list the exact names with `az webapp list-runtimes --os linux`. App Service installs and patches the .NET 10 runtime itself, which covers SEC-06 in the cloud; check the patch level later with the log stream (step 10).

`--sku F1` is free but limited (60 CPU minutes a day, no Always On, the app sleeps when idle). It is enough to try the steps, not to use.

### Step 5. Keep one instance

Leave the plan at one worker (`--number-of-workers 1`, above) and never turn on autoscale or "scale out" until risk 9 is fixed (section 1, item 5). Do not use deployment slots for swaps either: during a swap two copies run.

### Step 6. HTTPS only, modern TLS, no FTP (free)

```powershell
az webapp update --resource-group $rg --name $app --https-only true
az webapp config set --resource-group $rg --name $app `
  --min-tls-version 1.2 --ftps-state Disabled --http20-enabled true --always-on true `
  --startup-file "dotnet InventoryTracker.Api.dll"
```

- **HTTPS only** makes App Service's front end redirect every `http://` request to `https://` before it reaches the app. The certificate for `*.azurewebsites.net` is provided and renewed by Azure. A custom domain needs its own certificate (App Service can issue a free managed one) **[COSTS MONEY for the domain]**.
- **HSTS** tells browsers to use HTTPS from then on, without first trying HTTP. App Service does not add it; the app must (section 1, item 2). Check after deployment that responses carry `Strict-Transport-Security`.
- `--always-on` is not available on F1; drop it there.

### Step 7. Create the Key Vault and store the secrets **[COSTS MONEY]** (a few cents a month)

```powershell
az keyvault create --resource-group $rg --name $vault --location $location --enable-rbac-authorization true

# Allow yourself to write secrets into this vault (and nothing else).
$me      = az ad signed-in-user show --query id -o tsv
$vaultId = az keyvault show --name $vault --query id -o tsv
az role assignment create --role "Key Vault Secrets Officer" --assignee-object-id $me --assignee-principal-type User --scope $vaultId
```

Role assignments can take a minute or two to apply. Then store the three secrets:

```powershell
# A NEW signing key for this instance. Never reuse the key of your own machine or of a test instance (SEC-13).
$jwtKey = powershell -NoProfile -ExecutionPolicy Bypass -File scripts\new-signing-key.ps1
az keyvault secret set --vault-name $vault --name jwt-signing-key --value $jwtKey

# The first Manager's password: typed, so it is not stored in your command history. Read-Host does show it on
# screen while you type, so make sure nobody is watching, and run Clear-Host afterwards.
$adminPassword = Read-Host 'First Manager password (8+ characters, upper, lower, digit)'
az keyvault secret set --vault-name $vault --name admin-password --value $adminPassword

# The database connection string (the format of the Npgsql provider the PostgreSQL port will use).
$db = "Host=$pg.postgres.database.azure.com;Database=inventory;Username=inventoryadmin;Password=$pgPassword;SSL Mode=Require"
az keyvault secret set --vault-name $vault --name db-connection --value $db

Remove-Variable jwtKey, adminPassword, pgPassword, db
```

### Step 8. Give the app its identity and its settings **[ACCOUNT]** (a managed identity; free)

A managed identity is an Azure account for the app itself. The app uses it to read the vault, so no password for the vault exists anywhere.

```powershell
$appIdentity = az webapp identity assign --resource-group $rg --name $app --query principalId -o tsv
az role assignment create --role "Key Vault Secrets User" --assignee-object-id $appIdentity --assignee-principal-type ServicePrincipal --scope $vaultId
```

The settings go in through a small JSON file, because `az` on Windows mangles values that contain `;` or parentheses when they are passed on the command line. The file holds only references to the vault and ordinary values, no secrets; delete it afterwards anyway.

```powershell
$settings = @(
  @{ name = 'ASPNETCORE_ENVIRONMENT';               value = 'Production' }
  @{ name = 'ASPNETCORE_FORWARDEDHEADERS_ENABLED';  value = 'true' }
  @{ name = 'AllowedHosts';                         value = "$app.azurewebsites.net" }
  @{ name = 'ADMIN_EMAIL';                          value = 'you@yourcompany.example' }
  @{ name = 'ADMIN_PASSWORD';                       value = "@Microsoft.KeyVault(VaultName=$vault;SecretName=admin-password)" }
  @{ name = 'JWT_SIGNING_KEY';                      value = "@Microsoft.KeyVault(VaultName=$vault;SecretName=jwt-signing-key)" }
  @{ name = 'ConnectionStrings__Default';           value = "@Microsoft.KeyVault(VaultName=$vault;SecretName=db-connection)" }
)
$settingsFile = Join-Path $env:TEMP 'inventory-appsettings.json'
$settings | ForEach-Object { [pscustomobject]$_ } | ConvertTo-Json | Set-Content -Path $settingsFile -Encoding ASCII
az webapp config appsettings set --resource-group $rg --name $app --settings "@$settingsFile"
Remove-Item $settingsFile
```

- `@Microsoft.KeyVault(...)` is a **Key Vault reference**: App Service fetches the secret with the app's identity when the app starts and hands it to the app as an ordinary environment variable. In the portal, **Settings > Environment variables** shows a green tick next to each reference that resolved.
- `ASPNETCORE_FORWARDEDHEADERS_ENABLED` makes the app trust the `X-Forwarded-Proto` and `X-Forwarded-For` headers App Service adds, so it knows the request arrived over HTTPS. Do not set `HTTPS_PORT` or `ASPNETCORE_HTTPS_PORT`; with HTTPS-only already on, they only add a redirect that can loop.
- `AllowedHosts` limits which host names the app answers to (SEC-13).
- Do not set `ASPNETCORE_URLS` and do not use `scripts\run.ps1` here: App Service tells the app which port to listen on, and its front end is the only thing that can reach that port.
- The log level of decision #126 is already in `appsettings.Production.json`, which is part of the published app.

### Step 9. Publish and deploy (the deployment itself is free; it starts the app that costs money)

App Service supplies the runtime, so publish the framework-dependent folder and zip it. Use Windows' own `tar.exe` (built into Windows 10 and 11, called by its full path so that another `tar` on the PATH, such as Git's, is not picked up) rather than `Compress-Archive`: Windows PowerShell 5.1's `Compress-Archive` can write backslashes in the paths inside the zip, which Linux does not read as folders.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\publish.ps1 -FrameworkDependent
& "$env:SystemRoot\System32\tar.exe" -a -c -f publish\app.zip -C publish\framework-dependent .
az webapp deploy --resource-group $rg --name $app --src-path publish\app.zip --type zip
```

Keep each released `app.zip` (renamed with its version or date) somewhere safe: it is your rollback (section 10).

### Step 10. First start, first sign-in, then remove the first password

Watch the start-up in the log stream:

```powershell
az webapp log config --resource-group $rg --name $app --docker-container-logging filesystem
az webapp log tail --resource-group $rg --name $app
```

You should see the migrations being applied, `Created the first Manager (user ...)`, and the app listening. A `Start-up stopped: ...` line names the setting to fix (`docs/07-release.md` section 10).

In another PowerShell window. Each `Read-Host` below shows what you type on screen (it stays out of the command history, but it is not hidden), so make sure nobody is watching, and run `Clear-Host` when you are done:

```powershell
$base = "https://$app.azurewebsites.net"
Invoke-RestMethod "$base/health"                                   # -> Healthy
$cred = @{ email = 'you@yourcompany.example'; password = (Read-Host 'First Manager password') } | ConvertTo-Json
$token = (Invoke-RestMethod -Method Post "$base/api/v1/auth/sign-in" -ContentType 'application/json' -Body $cred).accessToken

# Change the first password straight away (it passed through your terminal and the vault).
$change = @{ currentPassword = (Read-Host 'Current password'); newPassword = (Read-Host 'New password') } | ConvertTo-Json
Invoke-RestMethod -Method Post "$base/api/v1/auth/change-password" -Headers @{ Authorization = "Bearer $token" } -ContentType 'application/json' -Body $change
```

Then remove the first-Manager settings and secret. They are never read again once a user exists (AC-02.2):

```powershell
az webapp config appsettings delete --resource-group $rg --name $app --setting-names ADMIN_PASSWORD ADMIN_EMAIL
az keyvault secret delete --vault-name $vault --name admin-password
```

Finally, create a second Manager account (the README explains why: keep at least two active Managers, SEC-04).

### Step 11. Logs

- **Log stream** (`az webapp log tail`, or **Log stream** in the portal): live console output. Free. Files are kept under `/home/LogFiles` with a size limit; App Service trims old ones.
- **Application Insights** (optional) **[COSTS MONEY]** beyond its free monthly data allowance: searchable logs and request charts. Turn it on from the Web App's **Application Insights** page if you need history beyond the log stream.
- What is logged and what never is (passwords, tokens, keys, request bodies): `docs/03-architecture.md` section 6, Logging.

## 6. Backups

- **Database:** Azure takes a full backup of the PostgreSQL server every day and keeps the transaction log, so you can restore to **any second** within the retention window (14 days, set in step 3). Nothing to schedule.
- **Restore** creates a **new** server **[COSTS MONEY while it exists]**; then you point the app at it:

  ```powershell
  az postgres flexible-server restore --resource-group $rg --name "$pg-restored" --source-server $pg --restore-time "2026-11-22T09:00:00Z"
  # Store a new db-connection secret with Host=<pg>-restored..., then restart the app:
  az webapp restart --resource-group $rg --name $app
  ```

  Delete whichever server you no longer need afterwards.
- **Your own copy (recommended monthly):** `pg_dump` from the PostgreSQL client tools makes a file you keep outside Azure. That file holds every user's email and password hash (SEC-16): store it encrypted, never in a shared folder.
- **The app itself** holds no data: any released `app.zip` can be redeployed.

## 7. Upgrading

1. Merge and test the new version locally: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test.ps1`
2. Note the time (UTC): it is your restore point if the upgrade goes wrong.
3. Publish, zip and deploy as in step 9. App Service restarts the app; it applies new migrations on start, before it answers requests (`Program.cs`, start-up step 3).
4. Watch the log stream and check `/health`.

## 8. Signing key

- One random key per instance, generated with `scripts\new-signing-key.ps1`, stored only in the vault.
- To replace it (for example if it may have leaked): store a new value under `jwt-signing-key` and restart the app (`az webapp restart`). Everybody is signed out; nothing else changes.

## 9. Rough monthly cost

| Tier | App Service | PostgreSQL | Other | Roughly |
|------|-------------|------------|-------|---------|
| Try it | F1, free | Burstable B1ms with 32 GB (free for 12 months on a new account, otherwise about 15 to 20 USD) | Key Vault: cents | 0 to 20 USD |
| Small company (recommended) | B1, about 13 USD | Burstable B1ms with 32 GB, about 15 to 20 USD | Key Vault: cents; logs: free tier | 30 to 35 USD |
| Business-critical | Premium P0v3 or P1v3, about 60 to 120 USD | General Purpose D2ds, about 130 USD, twice that with zone-redundant high availability | Application Insights: a few USD per GB | 200 to 400 USD |

Data leaving Azure (responses to users) is free for the first 100 GB a month, far more than this API uses.

## 10. Rolling back

- **Code:** redeploy the previous `app.zip` (step 9's `az webapp deploy` with the older file).
- **Data:** migrations only go forward. If the new version changed the schema, the old version may not work with it: restore the database to the time you noted before the upgrade (section 6), point the app at the restored server, then redeploy the old zip. Changes made after that time are lost, so write down what happened in between.

## 11. Tearing it all down (stops the charges)

Everything is in one resource group, so one command deletes it all:

```powershell
az group delete --name $rg --yes
```

Then:

1. **Key Vault** is kept "soft-deleted" for 90 days, which costs nothing but reserves its name. To remove it at once: `az keyvault list-deleted -o table`, then `az keyvault purge --name $vault`.
2. Check **Cost Management > Cost analysis** the next day: the charges should stop. Keep or delete the budget (free).
3. If you created the subscription only for this, you can cancel it in the portal (**Subscriptions > Cancel**).

Deleting the PostgreSQL server deletes its automatic backups too. Make a `pg_dump` first if you might want the data.

## 12. The same steps on other providers

| Piece | Azure (this guide) | AWS | Google Cloud | A virtual server (Hetzner, DigitalOcean, ...) |
|-------|--------------------|-----|--------------|---------------------------------------------|
| Run the API | App Service (Linux, .NET 10) | Elastic Beanstalk (.NET on Linux) or App Runner (container) | Cloud Run (container) | Ubuntu with the .NET 10 runtime and a systemd service |
| HTTPS | HTTPS-only + managed certificate | Load balancer + AWS Certificate Manager | Built in | Caddy or nginx with Let's Encrypt in front, app bound to `127.0.0.1` |
| Database | Azure Database for PostgreSQL | Amazon RDS for PostgreSQL | Cloud SQL for PostgreSQL | PostgreSQL on the same server (you run backups and updates) |
| Secrets | Key Vault + managed identity | Secrets Manager + IAM role | Secret Manager + service account | Environment file readable only by the service account |
| Logs | Log stream / Application Insights | CloudWatch | Cloud Logging | `journalctl` |
| One instance only | `--number-of-workers 1` | Single-instance environment | `--max-instances 1` | One server |

Cloud Run and App Runner run containers, so they need a `Dockerfile` (not in the repository yet; see `docs/07-release.md` section 5). Whatever the provider, section 1's product changes and the security re-review come first.
