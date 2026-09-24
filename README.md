# Nexus Promotions (APE) for SAP Business One

Advanced promotions engine for SAP B1: one engine, called by the B1 client add-on (Mode A, before save),
the queue worker (Mode B, after save), POS and e-commerce. Requirements: the PRD
"Advanced Promotions Engine for SAP Business One" (sections 3–9; trigger modes in 6.5).

## Status

| Part | State |
| --- | --- |
| `src/Nexus.Promotions.Engine` | Done: P01–P06, P08/P09, P13 audience rules, stacking, caps, floor price, near-miss, hash |
| `src/Nexus.Promotions.Api` | Done: REST API; rules from JSON files or the `@APE_PROMO` UDO (`Promotions:Source = ServiceLayer`); `/documents/evaluate` for the add-on and worker |
| `src/Nexus.Promotions.B1` | Done: Service Layer client, UDO → promotion mapping, item/customer lookup, document re-evaluation and write-back plan, Mode B processor |
| `src/Nexus.Promotions.Worker` | Done: Mode B worker (Windows service, or `once`); verified on SBODemoHO |
| `src/Nexus.Promotions.AddOn` | Done, used in the B1 client on SBODemoHO: Mode A add-on (.NET Framework 4.8 x64, UI API); one exe that is also its own installer/uninstaller, ready for SAP registration (`.ard`) |
| `tests/` | 65 tests: golden scenario per mechanic, rules, API, SQL against a compatibility-level-100 database |
| `sql/mssql` | Done: queue and settings tables, hash, validation (TransactionNotification), Mode B enqueue (PostTransactionNotice) |
| `src/Nexus.Promotions.Setup` | Done: creates UDTs/UDFs/UDOs through the Service Layer, installs the SQL, hooks the notification procedures; installed and verified on SBODemoHO |
| HANA version of the SQL | Next |
| Usage ledger (`@APE_LEDGER`) and budget consumption | Next |

## Run

```bash
dotnet test
dotnet run --project src/Nexus.Promotions.Api
```

The API listens on http://localhost:5190 and loads `samples/promotions.json` and `samples/items.json`.
It reloads rules every 60 s, or on `POST /api/v1/promotions/reload`.

| Endpoint | Purpose |
| --- | --- |
| `POST /api/v1/evaluate` | Basket in, adjusted lines out (FR-10) |
| `POST /api/v1/simulate` | Draft promotions against a basket, with or without the active ones (FR-13) |
| `POST /api/v1/hash` | Recompute the result hash for a set of lines (FR-45) |
| `GET /api/v1/promotions` | Rules currently in the cache |
| `GET /health` | Liveness and rule count (no API key) |

## Running against B1

Set the Service Layer password once for your Windows user (new terminals pick it up):

```bash
setx APE_ServiceLayer__Password "..."
```

API reading promotions from the `@APE_PROMO` UDO (company and user in `src/Nexus.Promotions.Api/appsettings.json`):

```bash
dotnet run --project src/Nexus.Promotions.Api -- --Promotions:Source=ServiceLayer
```

Mode B worker: `once` processes the queue and exits; without it, it runs as a service and polls every 2 s.
It evaluates in-process by default, or through the API when `Engine:Url` is set.

```bash
dotnet run --project src/Nexus.Promotions.Worker -- once
```

Mode A add-on: start the B1 client, log in, then run `src/Nexus.Promotions.AddOn/bin/Debug/net48/NexusPromotionsAddOn.exe`
(the API must be running; its URL is in `NexusPromotionsAddOn.json`). Sales quotations, orders, deliveries and A/R
invoices get an **Apply Promotions** button, and Add/Update applies promotions and shows a Save / Review summary.

## Deploying the add-on to every workstation (SAP registration, `.ard`)

B1 installs and starts add-ons itself once they are registered from an `.ard` file, so nothing is run by hand
per machine. Three things make that work:

1. **One exe does everything.** B1 distributes only the files an `.ard` names, so `NexusPromotionsAddOn.exe` is the
   add-on, the installer and the uninstaller in one file, with its dependencies embedded (Costura.Fody).
   B1 runs the installer as `exe "installFolder|...\AddOnInstallAPI.dll"`; the exe copies itself into that folder and
   calls `EndInstall()` (B1 hands over the path of the 32-bit `AddOnInstallAPI.dll` even to this 64-bit exe, so it
   loads the `_x64` sibling first). B1 runs the uninstaller as `exe /U`. Details: `InstallerMode.cs`. An installer run by B1
   has no window, so it logs to `%TEMP%\NexusPromotionsAddOn.install.log`.
2. **The `.ard` carries the MD5 and SHA-256 of the exe.** B1 rejects an add-on whose exe changed after the `.ard`
   was made, so build both together, every time:

   ```powershell
   scripts\build-addon.ps1 -Version 1.2      # -> dist\addon\v1.2\NexusPromotionsAddOn.exe and .ard
                                             #    (one folder per version: a running exe is locked by Windows)
   ```

   `New-AddOnArd.ps1` writes the same file as SAP's *Add-On Registration Data Generator* (verified byte for byte
   against a file made with that tool), so the GUI is optional. To use the GUI instead, fill it with:

   | Field | Value |
   | --- | --- |
   | Partner name / Namespace / Contact | Nexus / Nex / Nexus |
   | Add-On name / Version | NexusPromotion / 1.0 (raise it for every release) |
   | Add-On executable, Installer exe, Uninstaller exe | `dist\addon\v<version>\NexusPromotionsAddOn.exe` (all three) |
   | x64, Supported client type | ticked, Both |
   | Installer command line | empty |
   | Uninstaller command line arguments | `/U` |
   | Estimated install / uninstall time | 30 seconds each |

3. **No per-workstation settings.** The add-on reads the promotion API's address from the company database, so
   after registration every workstation finds it on its own:

   ```bash
   dotnet run --project src/Nexus.Promotions.Setup -- setting ApiUrl http://promo-server:5190
   dotnet run --project src/Nexus.Promotions.Setup -- setting ApiKey <key>      # only if Promotions:ApiKeys is set
   ```

   Precedence: `NexusPromotionsAddOn.json` next to the exe (a deliberate local override), then `dbo.APE_Settings`,
   then `http://localhost:5190`. The API must listen on an address the workstations can reach
   (`"Urls": "http://0.0.0.0:5190"` in its `appsettings.json`, and the firewall open on that port).

**Speed and diagnostics.** B1 waits for the add-on on every event, and every call from the add-on into B1 crosses a
process boundary (about 15 ms for `Items`, `Columns` and `Cells`; 0.25 ms for `DBDataSource.GetValue`). A Sales Order
has about 800 items, so never loop over `form.Items` (an earlier version did, and held every form open for 14–18 s).
`NexusPromotionsAddOn.exe --bench` (run from a folder you can write to, with a B1 client open) times these calls on the
open sales forms without changing anything. The add-on log is per user, `%LOCALAPPDATA%\Nexus\PromotionsAddOn\`, and
records the time of every save (read / engine / write) and any event that took over 300 ms.

Turn automatic apply-on-save off or on for every workstation at once with the switch at the top of the admin app
(`dbo.APE_Settings` `ModeA`); the **Apply Promotions** button always still works.

Then, in the B1 client, register the `.ard` (Administration → Add-Ons → Add-On Administration; menu names vary a
little between B1 versions) and assign it to the companies and users that should have it.

## Several companies (also on the same workstation)

One promotion server serves all companies. Add them in the admin app under **Settings > Companies** (saved to `companies.json`, applied on the next API restart; passwords are never shown back). Promotions are defined once, in a **master company**, and each promotion ticks the companies it applies to (step "Companies" in the admin app; none ticked = master only). Every company is evaluated with its own items, customers and price lists.

```json
"Companies": [
  { "Db": "SBODemoHO",  "Name": "Head Office", "Master": true, "SqlConnectionString": "..." },
  { "Db": "SBODemoBr1", "Name": "Branch 1",    "SqlConnectionString": "..." }
]
```

- Each add-on sends its company in `X-Company-Db`; the server answers with that company's promotions and refuses a company it does not serve.
- In every non-master company run `setting CentralPromotions Y` (promotions are not read from that company's own tables) and give its Mode B worker `Engine:Url` plus its own `ServiceLayer:CompanyDb`. Each company keeps its own hash key.
- The admin app shows per-company Auto-apply switches (`/mode-a` with `company`), and the Simulator/Try it has a company selector.
- Item-group, manufacturer and item-property numbers are per company. Saving a promotion that ticks other companies runs a check and lists items, groups and customer groups that do not match there; it is a warning, not a block.
- Two companies on one workstation: the add-on picks the server by company, so both work side by side; a second Mode B worker needs its own `Worker:ServiceName`.

## MS SQL install (per company database)

Requires SQL Server 2019+ (the hash uses a UTF-8 collation). Works at compatibility level 100, which most
B1 company databases still use.

The setup tool does steps 1–3; set the company in `src/Nexus.Promotions.Setup/appsettings.json` and the
password in the `APE_ServiceLayer__Password` environment variable (never in the repository):

```bash
dotnet run --project src/Nexus.Promotions.Setup -- plan
dotnet run --project src/Nexus.Promotions.Setup -- install
dotnet run --project src/Nexus.Promotions.Setup -- verify
```

`install` is safe to re-run: existing objects are skipped, and a procedure that already has the APE block is left alone.
`verify` posts two test sales orders (C20000 / A00001), checks acceptance, tampering (71004), Mode B queueing and
the delivery block (71001), then cancels them.

By hand, the steps are:
1. Create the UDOs and `U_APE_*` fields (the setup tool's `Schema.cs` lists them).
2. Run `sql/mssql/01` to `04` in order.
3. Paste the two blocks from `05_TransactionNotification_snippet.sql` into `SBO_SP_TransactionNotification`
   and `SBO_SP_PostTransactionNotice`.
4. Set `dbo.APE_Settings`: `HashKey` (same as the engine's `Promotions:HashKey`), `ModeB`, `TechUserId`
   (the worker's B1 user), `DiscTolerance`.

| Error | Meaning |
| --- | --- |
| 71001 | Delivery or invoice copied from an order still waiting in the Mode B queue |
| 71002 | Promotion lines changed after promotions were applied, or promotions without a valid hash |
| 71003 | Free line without 100% discount |
| 71004 | `DiscPrcnt` does not give the `U_APE_DiscAmt` amount |
| 71005 | Unknown promotion code |
| 71006 | Copied line whose promotion or discount differs from its base line |

Rules that need care:
- The hash sorts the rows before hashing, so line order does not matter: free lines may be appended anywhere.
- Copied lines (delivery from order, partial quantities) keep their promotion without a new hash, as long as
  code, free flag and `DiscPrcnt` match the base line (FR-18).
- On an order or quotation, clearing `U_APE_Hash` asks Mode B to re-apply promotions.
- Validation of a 500-line order takes about 20 ms.

SQL tests: `tests/Nexus.Promotions.Sql.Tests` rebuilds a database `APE_SqlTest` (compatibility level 100, B1
table stubs) on `APE_SQL_SERVER` (default `localhost`), and skips when no SQL Server is reachable.

## How the engine works

1. **Eligibility**: dates, weekdays, happy hour, channel, customer, group, branch, price list, coupon, budget.
2. **Phase 1, unit price** (P01, P02, P03): per line, each exclusive promotion competes with the stack of
   stackable ones. `ConflictMode` picks the best deal for the customer, or the highest priority.
   `StackingMode` compounds or adds the stacked percentages.
3. **Phase 2, quantity** (P04, P05, P06): one application at a time, in priority order. A unit used by one
   promotion cannot trigger another. Free units are split into their own 100%-discount line; `AddNew` adds reward lines.
4. **Phase 3, basket** (P08/P09): the discount is spread over the lines in proportion to their value. The
   rounding residue goes to the largest line, so the lines always add up to the total.
5. **Final pass**: caps on phase 1 promotions, minimum-price floor, rounding to the currency's decimals, hash.

The output is the complete new line set. Each line has `DiscountPercent` (for B1 `DiscPrcnt`),
`DiscountAmount` (`U_APE_DiscAmt`), `PromotionCodes` (`U_APE_Promo`), `Group` (`U_APE_Group`) and `IsFree` (`U_APE_Free`).

Performance: 500 lines against 500 active promotions takes about 130 ms in Release on a quiet machine
(NFR-01 target: 300 ms). The unit test is a 1 s regression guard, because wall-clock time on a busy machine varies 2–3×.

## Known limits (to address)

- An exclusive quantity or basket promotion only sees lines no earlier-phase promotion touched. There is no
  "best deal" comparison across phases yet.
- `MixAndMatch` with `Cheapest` selection stops at the first set that saves nothing.
- Promotion amounts are in document currency; there is no conversion yet.
