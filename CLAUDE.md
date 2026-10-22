# Cobalt B1 Promotions (repo folder still named Promotion / Nexus.Promotions.*)

Session handoff. Read this first instead of replaying history. Last updated 2026-10-22.
Full product docs: `README.md` (run, deploy add-on, multi-company, SQL install, engine rules).
Portal/e-commerce contract: `docs/portal-integration.md`.

## Where things are
- Local: `C:\Claude\Promotion` (git, branch `main`, remote `https://github.com/tonymatar-max/Prromotion.git`; the repo name has two r's).
- Pushed to GitHub: only up to `c12a7c0` (the NIVEA15 fix). **Later commits are local only** (rebrand + deploy scripts, test isolation, portal API, MixAndMatch fix, README, docs). Push when Tony says so.
- Projects (.NET 8, namespaces stay `Nexus.Promotions.*`): `Engine`, `B1` (Service Layer, UDO mapping, `PromotionStore`, `PortalOrder`), `Api` (REST + admin app in `wwwroot/app.js`), `Worker` (Mode B), `AddOn` (Mode A, net48 x64), `Setup` (UDT/UDF/UDO + SQL installer). Tests in `tests/` (all pass: Api 20, B1 18, Engine 28, Sql 20).
- Run: `dotnet run --project src/Nexus.Promotions.Api` -> http://localhost:5190. Default `Promotions:Source = File` (reads `samples/promotions.json`), so the admin app is read-only.

## Done (2026-10)
- **NIVEA15 404 fix** (pushed): in File mode `GET /admin/promotions/{code}` had no fallback; `AdminPromotion.FromEngine` added. Test in `ApiTests`.
- **Rebrand to Cobalt, user-facing only**: admin title/header, add-on messages, README title. Deliberately NOT changed: namespaces, project/file names, `.ard` identity (`NexusPromotion`, partner `Nexus`), registry key/log paths, SQL `-- Nexus Promotions (APE) — BEGIN/END` markers (the installer detects installed blocks by them; renaming would double-install), `APE_` prefixes.
- **Windows service "Cobalt B1 Promotion"**: `deploy/publish.ps1`, `install-service.ps1` (-Worker, -Urls, -ServiceLayerPassword), `uninstall-service.ps1`. Service id `CobaltB1Promotion`, worker `CobaltB1PromotionWorker`, folder `C:\CobaltB1Promotion`, port 5190. Publish keeps `appsettings.json`, `companies.json`, `data\`. Tested: publish to a temp folder and run the exe in console mode. **Not installed** as a service (needs an elevated shell): run `deploy\publish.ps1` then `deploy\install-service.ps1` elevated.
  - Deviations from the Cobalt service standard (decide with Tony): default bind is `0.0.0.0:5190` (add-ons on every workstation must reach it; standard says localhost); no in-app Settings for SQL/Service Layer yet (config is `appsettings.json` + env `APE_ServiceLayer__Password`).
  - **Admin sign-in done** (`AdminAuth.cs`, roles Admin/Viewer, `data\users.json`, first admin from localhost, cookie `cobalt_promo_auth`; `Admin:RequireLogin=false` turns it off). `/admin/info` stays open for the add-on (it reads Mode A state with its API key). Tests: `AdminAuthTests`; other tests run with login off via `TestIsolation`. Still missing from the standard: in-app Settings for SQL/Service Layer with Test buttons.
- **Tests**: the 4 old `MultiCompanyApiTests` failures were the developer's gitignored `companies.json` leaking into tests; `CompaniesFile` setting + `TestIsolation.cs` fix it.
- **Portal API** `POST /api/v1/portal/orders/evaluate` (`B1/PortalOrder.cs`, endpoint in `Api/Program.cs`): order lines in (price before promotions), discounted lines + free/added lines + hash + exact Service Layer fields out. Test recomputes the hash from only the documented fields.
- **Engine**: `MixAndMatch` + `FixedPrice` + `Cheapest` no longer stops at a cheap set that saves nothing (`UnitPool.Skip`). Two tests, both fail without the fix.
- Repo cleanup: nothing needed (`backups/`, `dist/`, logs already gitignored).

## Customer Portal wiring (other repo)
`C:\Claude\Cobalt Customer Portal`, branch **`promotions-integration`** (not merged, not pushed). Settings > Promotions tab (EN/AR), `PromotionsClient`, `OrderService.PostAsync` applies promotions at SAP posting time (skips accepted quotes, never blocks the order if the server fails, writes `order.promotions*` to the audit trail), `tests/section-promotions.sh` + `tests/fake_promo.py`. Run only that section: `ONLY=promotions bash tests/e2e.sh`. See that repo's notes for status.
Design choices to confirm with Tony: lines carry the portal's price (the one the customer saw) when promotions apply; cart preview of discounts is not built (only applied at posting); fail-open when the promotion server is down.

## Not done (needs input)
- **HANA version of the SQL**: cannot be verified without a HANA system; validation runs inside the customer's SAP DB, so do not ship untested.
- **Usage ledger (`@APE_LEDGER`) and budget consumption**: design first (when is budget used, does a cancelled order give it back, per promotion vs campaign).
- **Engine limits**: exclusive quantity/basket promotions only see untouched lines (no cross-phase best deal); no currency conversion.
- **Rename the GitHub repo** (`Prromotion` typo): needs the new name; GitHub redirects the old URL.
- In-app Settings + login for the service (see deviations above).

## Gotchas
- A running API locks its DLLs: stop it before `dotnet build` (kill by port, never by image name).
- Hash key (`Promotions:HashKey`) must match `dbo.APE_Settings.HashKey` or SAP validation raises 71002. A channel posting a promoted order must send the lines exactly as returned (the hash covers item, quantity, unit price, discount amount, promotion codes, free flag).
- `api.out.log` / `api.err.log` are runtime logs (ignored).
- Do not name a config key under `Companies:` (the list binder treats it as an entry); use a top-level key like `CompaniesFile`.
