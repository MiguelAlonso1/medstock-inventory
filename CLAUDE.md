# CLAUDE.md

# Hard rules

- MudBlazor-first, always latest: use the latest stable MudBlazor release and its native components/APIs for all UI (layout, navigation, theming, dark mode, cards, charts, timeline, chips, buttons, data grid, forms, typography, spacing utilities). Check https://mudblazor.com and the release notes before building UI; never hand-roll HTML/CSS/JS for something MudBlazor already provides. No deprecated/obsolete parameters (the MudBlazor analyzers run in the build). The build must have zero warnings (`TreatWarningsAsErrors` is on in `Directory.Build.props`).
- Icons: only MudBlazor's built-in icons (`Icons.Material.*` / `Icons.Custom.*`). No icon fonts, SVG packs or emoji. (`wwwroot/favicon.svg` is the only hand-made graphic.)
- No external libraries: no NuGet UI packages, CDN scripts/styles or third-party web fonts beyond MudBlazor and the .NET/ASP.NET Core/EF Core/Identity stack. Fonts are self-hosted (`wwwroot/fonts`, Inter).
- No custom JavaScript: no hand-written JS files, inline `<script>` blocks or `IJSRuntime` calls. Only `blazor.web.js` and MudBlazor's bundled `MudBlazor.min.js`. Theme toggle, login and logout are cookie/form-post based; real-time is Blazor Server's SignalR circuit.

## Overview
MedStock: fictional healthcare inventory and order management demo (synthetic data) for Miguel Alonso's portfolio. Design language matches alonsomiguel.com (dark-first navy, indigo/cyan, Inter).

## Stack
- .NET SDK 11.0.100, `net11.0`; Blazor Web App, Interactive Server (global); MudBlazor 9.13.0
- EF Core 11 + SQLite (`App_Data/medstock.db`), ASP.NET Core Identity (cookie auth)
- Hosting target: Windows/IIS (out-of-process) via FTPS from GitHub Actions

## Commands
- Run: `dotnet run --project src/MedStock.Web` (http://localhost:5120)
- Build: `dotnet build MedStock.slnx` (0 warnings, 0 errors)
- Test: `dotnet test MedStock.slnx` (domain unit tests + WebApplicationFactory integration tests; test packages are dev-only)
- Publish: `dotnet publish src/MedStock.Web/MedStock.Web.csproj -c Release -r win-x86 --self-contained true -o <dir outside the repo>`
- Health: `/health`
- Stop the running app before building/testing (the exe is locked on Windows).

## Structure
- `src/MedStock.Domain` - pure rules, no EF/ASP.NET: `FefoAllocator`, `ReorderCalculator`, `ExpiryClassifier`, `StockLedger` (never negative), `PurchaseOrderReceiving`, `OrderWorkflow`
- `src/MedStock.Web/Data` - entities, `MedStockDb` (Identity + business tables), `DemoSeeder` (deterministic synthetic data, demo users, admin reset)
- `src/MedStock.Web/Services` - `OrderService`, `PurchasingService`, `TransferService`, `InventoryQueries` (all singletons over `IDbContextFactory`), `WriteGate` (serializes writes), `InventoryEventHub` (publish/subscribe), `SimulationWorker` + `SimulationControl`
- `src/MedStock.Web/Endpoints` - theme, account (form posts), JSON API (`/api/orders`, `/api/purchase-orders/{id}/receive`, `/api/ledger/verify`, `/api/admin/reset-demo`)
- `src/MedStock.Web/Components` - `Pages`, `Layout` (MainLayout, AuthLayout for the static login page), `Shared` (`LiveComponentBase`, `KpiTile`, `Chips`, `ActivityFeed`)
- `src/MedStock.Web/Theme/AppTheme.cs` - MudTheme

## Conventions
- Every state change goes through a service that takes the `WriteGate`, saves, then publishes an event on `InventoryEventHub` with `Topic` flags. Pages inherit `LiveComponentBase`, override `Topics` and `LoadAsync`; the base class reloads (coalesced) on matching events and exposes `Actor`, `CanManage`, `CanFulfill`, `IsAdmin`.
- Stock may never go negative: use `StockLedger.ApplyDelta` for level changes and write a `StockMovement` for every quantity change. `/api/ledger/verify` replays history against current stock.
- Cold-chain products can only be stored/ordered/transferred at cold-chain capable warehouses.
- Roles: Admin, Warehouse Manager (`Roles.ManageRoles`), Picker (`Roles.FulfillRoles`), Viewer. Authorize pages with `[Authorize(Roles = ...)]`.
- The login page is static SSR (`[ExcludeFromInteractiveRouting]`, plain `<form method="post">` with `<AntiforgeryToken/>`). `/api/*` endpoints return 401/403 (no redirects, no status-page re-execute).
- Colors come from `AppTheme` palettes; use CSS variables in CSS. Tap targets >= 44px, no horizontal scroll at 390px.

## CI/CD and hosting
- `.github/workflows/ci-cd.yml`: build + test on every push/PR; publish artifact on main; `deploy` job only when repo variable `DEPLOY_ENABLED == 'true'`. Variables: `FTP_SERVER_DIR` (required, e.g. `inventory/`), `SITE_URL` (optional, default `https://inventory.alonsomiguel.com`), `DEPLOY_ENABLED`. Secrets: `FTP_SERVER`, `FTP_USERNAME`, `FTP_PASSWORD`. Same safety design as the personal site (folder validation, write probe, app_offline, mirror without --delete, always-run cleanup, smoke test, concurrency).
- Self-contained win-x86 publish; `web.config` is out-of-process with WebSockets on; `.pfx` blocked; `.well-known` passthrough. HTTP->HTTPS redirect and HSTS are in `Program.cs` but opt-in via config `Https:Enforce=true` (off until the subdomain has a certificate). Production slot: `pem-now/`; manual workflow run with `clean_target=true` wipes old files there (App_Data, .well-known, logs kept). Smoke test is skipped while `SITE_URL` is unset. Simulator: 10 s interval, auto-resets demo data past `Simulation:MaxMovements` (30000). stdout logging to `.\logs` is on in web.config.
- The database is created on the server on first start. `App_Data/*.db*` is excluded from the publish output and from the FTP mirror, so deployments never overwrite live data.

## Rules for future changes
- Keep build/test/publish green with zero warnings. Update CHANGELOG.md and `<Version>` in `MedStock.Web.csproj`.
- Test at 390px and desktop in light and dark before merging.
