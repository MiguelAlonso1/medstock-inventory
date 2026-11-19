# Changelog

## 1.3.4 - 2026-11-19 - Stdout logging off

- web.config: `stdoutLogEnabled="false"` now that the app is healthy (it was only for first-deploy diagnosis and made log files accumulate on the shared host). To diagnose startup failures, temporarily set `stdoutLogEnabled="true"` and redeploy. Existing files in the server `logs\` folder are not removed and can be deleted later.

## 1.3.3 - 2026-11-19 - Test isolation from local AI keys

- Tests: the test host now overrides `Ai:Provider`, `Ai:Gemini:ApiKey`, `Ai:Anthropic:ApiKey` and `Claude:ApiKey` in memory so a developer's user-secrets or environment variables never leak into tests.

## 1.3.2 - 2026-11-19 - Node 24 Actions, theme toggle fix

- CI: updated GitHub Actions to Node 24 majors (`checkout` v7, `setup-dotnet` v6, `upload-artifact` v7, `download-artifact` v8). Deploy logic unchanged.
- Theme toggle (header and login page) now opens with `target="_top"` so Blazor no longer intercepts the click and performs a JS-interop forced navigation that raised `TaskCanceledException` in the circuit log; the browser follows the `/theme/{mode}` link normally.

## 1.3.1 - 2026-11-19 - User secrets for local AI keys

- Added a `UserSecretsId` to the web project so `dotnet user-secrets set` works (secrets load automatically in Development).
- README and Rider/DataGrip guide: corrected local AI-key instructions (exact config keys, user secrets, or an `Ai__Gemini__ApiKey` environment variable in a Rider run configuration).

## 1.3.0 - 2026-11-18

"Ask MedStock": a read-only agentic assistant on the Google Gemini free tier (or Claude).

- New `/assistant` page (nav entry "Ask MedStock"): chat with a tool-use loop (max 6 tool rounds), streamed answers and small chips showing which tools were used. Nine read-only tools over the existing query services (`search_products`, `get_stock_by_product`, `list_expiring_lots`, `list_low_stock`, `get_order`, `list_open_orders`, `list_reorder_suggestions`, `list_alerts`, `get_kpis`) with validated inputs and row caps.
- AI briefing card on the Overview: short daily operations briefing (expiring lots, low stock, cold-chain excursions, shortfalls).
- Provider abstraction `IAiProvider` selected by `Ai:Provider` (`Gemini` default, `Anthropic`): Gemini native function calling over `HttpClient` + `System.Text.Json` with SSE streaming (`Ai:Gemini:ApiKey`, `Ai:Gemini:Model`, free key from Google AI Studio); Claude Messages API with prompt caching (`Claude:ApiKey`, `Claude:Model`). No SDK, no new package. `Claude:DailyLimit` (200) and `Claude:HourlyLimit` (20 per user) apply to both.
- Limits: 500-character questions, last 10 turns, 90 s timeout, friendly errors. Without an API key the page and card show a disabled state; the rest of the app is unaffected.
- CI/CD: the deploy job (never the build job, whose artifact is public) injects `Ai__Gemini__ApiKey` (secret `GEMINI_API_KEY`), `Claude__ApiKey` (secret `ANTHROPIC_API_KEY`) and `Ai__Provider` (variable `AI_PROVIDER`) into `web.config`, each only when non-empty.
- Tests with fake `HttpMessageHandler`s for both providers (no live calls): tool-use loop, iteration cap, read-only guarantee (tables and ledger unchanged after running every tool), input validation, rate limit and daily cap, disabled state, page access.


## 1.2.0 - 2026-11-16

Full CRUD with role-based permissions and an append-only ledger.

- Master data pages (`/manage/products|warehouses|suppliers|customers`, Admin and Warehouse Manager): MudDataGrid with search, create/edit MudDialog forms with validation, delete with confirmation. Records referenced by stock, lots, orders, POs or movements cannot be deleted; the dialog offers Deactivate instead.
- New `IsActive` flag on products, warehouses, suppliers and customers. Inactive records are hidden from pickers, autocompletes, reorder suggestions, low-stock alerts and the simulator; history stays readable.
- Customer orders: edit lines (add, remove, quantity) while Pending or Allocated, with full release and FEFO re-allocation inside one transaction. Admin-only delete of cancelled orders without stock movements. Cancel now asks for confirmation.
- Purchase orders: manual PO creation (supplier, warehouse, lines), edit quantities (not below received), cancel before any receipt.
- Transfers: reverse with compensating TransferOut/TransferIn movements (`TRF-xxxxx-REV`); original ledger rows are untouched and `/api/ledger/verify` still passes.
- Services now take a `Caller` (name and role flags) and enforce permissions themselves; denied calls return an error and change nothing.
- Simulator ignores inactive products, warehouses and customers, no longer advances or receives user-created orders and POs, and tolerates empty user-created warehouses and products.
- Schema version check (`PRAGMA user_version` = 2): an older database is dropped, recreated and reseeded on startup with a clear log message, so the host's existing `App_Data` upgrades.
- Tests: domain rules, service tests for every create/edit/delete/deactivate path, order reallocation, PO cancel/edit/manual, transfer reversal ledger integrity, role denials and the new pages' access rules.

## 1.1.0 - 2026-11-16

Deployment to production slot.

- Deploys to the shared host folder `pem-now/` (replaces the retired app). Workflow gains a manual `clean_target` input that removes old files (guarded: only `pem-now/`, keeps App_Data, .well-known, logs) after the site is put offline.
- HTTPS redirect and HSTS are now opt-in (`Https:Enforce=true`), so the app runs over plain HTTP until a certificate exists for the subdomain.
- web.config: stdout logging to `.\logs` enabled for startup diagnostics; `.db`, `.db-wal`, `.db-shm` downloads blocked; App_Data and logs hidden.
- Simulator is host-friendly: default interval 10 s and a data cap (`Simulation:MaxMovements`, default 30000) after which demo data is reset automatically.
- Smoke test is skipped with a notice when the `SITE_URL` variable is unset.

## 1.0.1 - 2026-11-15

Polish pass.

- Charts: axis labels are readable at 390px (larger in-SVG font, fewer ticks, every third day label on phones); charts now size to their container, which removes the empty right margin on the movements chart.
- Inventory grid on phones: compact card per product (name, SKU, on-hand, expiry and Low/Cold chips); the empty "Flags" row is gone. Desktop filters laid out on two clean rows, headers no longer wrap.
- Lot tables fit 390px (expiry date and reserved columns hide on phones).
- New order: validation error no longer appears on the reset line after a successful create.
- Simulator rebalanced (fulfilment outpaces new orders, fewer PO receipts) so open orders and daily inbound volume stay realistic.
- README screenshots added.

## 1.0.0 - 2026-11-15

Initial release.

- Blazor Web App (Interactive Server) on .NET 11 with MudBlazor 9.13; dark-first navy/indigo/cyan theme, light/dark toggle via cookie (no JavaScript).
- EF Core + SQLite (`App_Data/medstock.db`), created and seeded on startup: 4 distribution centers, 300 products (SKU, NDC-style code, lots, expiry, cold-chain flag, unit cost), suppliers, customers, stock levels per location and lot, purchase orders, customer orders and a replayable stock movement ledger. All data is synthetic.
- Pages: Overview (KPIs and MudChart charts), Inventory (server-side MudDataGrid, lot drill-down), Product detail, Orders (create with MudForm validation, FEFO allocation, pick/ship flow), Purchase orders (reorder suggestions, receiving), Transfers, Alerts (low stock, expiring, cold-chain excursions), Live activity.
- Real-time: singleton event hub plus a background simulator that creates orders, ships, receives POs, transfers and raises alerts; every connected circuit updates live.
- ASP.NET Core Identity with four seeded roles and one-click demo logins (plain form posts); role-based authorization; Admin-only reset of demo data.
- `MedStock.Domain` class library (FEFO allocator, reorder calculator, expiry classifier, stock ledger, PO receiving, order workflow) with unit tests; WebApplicationFactory integration tests.
- Deployment: self-contained win-x86 publish, `web.config` (out-of-process, WebSockets), in-app HTTPS redirect and HSTS, GitHub Actions CI/CD with FTPS deploy gated by `DEPLOY_ENABLED`.
