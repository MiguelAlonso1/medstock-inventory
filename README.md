# MedStock

A healthcare-style **inventory and order management system** with real-time updates, built as a portfolio project. Everything is a demo with **synthetic data**: no real products, companies or patients.

> Blazor Web App (Interactive Server) on .NET 11, MudBlazor, EF Core + SQLite, ASP.NET Core Identity, SignalR live updates. No JavaScript written by hand, no CDNs, no external libraries beyond MudBlazor.

## Screenshots

| Overview (dark, desktop) | Inventory (light, desktop) |
| --- | --- |
| ![Overview, dark, 1440px](docs/screenshots/overview-dark-1440.png) | ![Inventory, light, 1440px](docs/screenshots/inventory-light-1440.png) |

| Product detail (dark, desktop) | Overview (dark, 390px phone) |
| --- | --- |
| ![Product detail, dark, 1440px](docs/screenshots/product-dark-1440.png) | ![Overview, dark, 390px](docs/screenshots/overview-dark-390.png) |

## Features

- **Overview**: KPI tiles (inventory value, low-stock SKUs, lots expiring within 30/60/90 days, open POs, fill rate, open orders), MudChart charts (movements per day, stock value by location), top products, and a live activity feed.
- **Inventory**: MudDataGrid with server-side paging, sorting and filtering (search, location, category, low stock, expiring, cold chain) and lot drill-down per row.
- **Product detail**: stock by location, lot and expiry table, movement timeline.
- **Orders**: create with MudForm validation, **FEFO** (first-expiry-first-out) allocation across lots, pick and ship flow, cancel and re-allocate. **Edit lines** (add, remove, change quantity) until picking starts: all reservations are released and FEFO runs again, so nothing stays reserved by accident. Admin-only delete of cancelled orders that never moved stock.
- **Purchase orders**: reorder suggestions from min/max levels (counting stock on order), auto-generated POs per supplier **and manual POs** (supplier, warehouse, lines), edit quantities (never below what was received), cancel before receipt, receive (full or partial) to book new lots.
- **Transfers** between locations (reserved stock cannot move; cold chain only to cold-chain sites) with **reversal by compensating movements**.
- **Master data (full CRUD)**: Products, Warehouses, Suppliers and Customers, each a MudDataGrid with search plus MudDialog create/edit forms with validation and a confirmed delete. Records that history refers to (stock, lots, orders, POs, movements) cannot be deleted; they are **deactivated** instead, which hides them from pickers, suggestions, alerts and the simulator while history stays readable.
- **Append-only ledger**: stock movements are never edited or deleted. Cancelling or reversing is always a new compensating movement, and `/api/ledger/verify` replays the whole history against current stock.
- **Role-based permissions, enforced in the services** (not only by page attributes): Admin (everything, plus order delete and demo reset), Warehouse Manager (all create/edit/delete/deactivate, cancel, receive, reverse), Picker (pick and ship), Viewer (read only).
- **Alerts**: low stock, expiring lots (30/60/90 days, expired), cold-chain temperature excursions.
- **Real time**: a background simulator creates orders, ships them, receives POs, transfers stock and raises alerts every few seconds. Every connected user sees dashboards, grids and the feed update live. Toggle it from the app bar or the Admin page.
- **Auth**: Identity with seeded roles (Admin, Warehouse Manager, Picker, Viewer), one-click "Try the demo" sign-in (plain form posts), role-based authorization, Admin-only "Reset demo data".
- Responsive, dark/light (cookie based), accessible (skip link, labels, focus states), zero build warnings.

## Architecture

```mermaid
flowchart LR
    subgraph Browser
        UI[MudBlazor UI<br/>rendered by Blazor Server]
    end
    subgraph Web["MedStock.Web (ASP.NET Core)"]
        Pages[Razor components<br/>LiveComponentBase] -->|queries/commands| Svc[Services<br/>Order / Purchasing / Transfer / Queries]
        Svc -->|publish| Hub[(InventoryEventHub<br/>singleton)]
        Hub -->|subscribe + InvokeAsync| Pages
        Sim[SimulationWorker<br/>BackgroundService] --> Svc
        Auth[Identity + cookie auth]
        API[/api endpoints/] --> Svc
    end
    Svc --> Domain[MedStock.Domain<br/>FEFO, reorder, expiry, ledger, PO receiving]
    Svc --> DB[(SQLite<br/>App_Data/medstock.db)]
    UI <-->|SignalR circuit| Pages
```

Writes are serialized through a `WriteGate`, saved, then announced on the event hub with topic flags; components reload only when a relevant topic fires. The business rules live in `MedStock.Domain` (no EF or ASP.NET dependencies) and are unit-tested in isolation.

```
MedStock.slnx
  src/MedStock.Domain        pure rules (FEFO, reorder, expiry, ledger, PO receiving, order workflow)
  src/MedStock.Web           Blazor app, EF Core, Identity, services, simulator, endpoints
  tests/MedStock.Domain.Tests   xUnit unit tests
  tests/MedStock.Web.Tests      xUnit + WebApplicationFactory integration tests
```

## Tech stack

.NET 11, Blazor Web App (Interactive Server), MudBlazor 9.13, EF Core 11 + SQLite, ASP.NET Core Identity, SignalR (via Blazor Server), xUnit + `Microsoft.AspNetCore.Mvc.Testing`. Inter font self-hosted.

## Run locally

Requires the .NET SDK 11.

```
dotnet run --project src/MedStock.Web
# http://localhost:5120  (the SQLite database is created and seeded on first start)
dotnet test MedStock.slnx
```

Demo accounts (also available as one-click buttons on the sign-in page), password `MedStock-Demo-2026!`:
`admin@medstock.demo`, `manager@medstock.demo`, `picker@medstock.demo`, `viewer@medstock.demo`.

Master data pages live under `/manage/*` and are visible to Admin and Warehouse Manager.

Settings (`appsettings.json`): `Simulation:Enabled`, `Simulation:IntervalSeconds`, `Demo:OneClickLogin`, `ConnectionStrings:Default` (defaults to `App_Data/medstock.db`).

## Tests

- Domain: FEFO allocation, reorder point and min/max suggestions, expiry classification, ledger (no negative stock, replay verification), PO receiving (partial, over-receipt), order workflow, CRUD rules (delete vs deactivate, validation, order line edit and delete rules, PO edit/cancel rules, transfer reversal and compensating ledger entries).
- Service tests (real services over a throw-away SQLite file): create/edit/delete/deactivate for every master data type, delete blocking, hidden-when-inactive, order line edit FEFO reallocation without leaked reservations, manual/edit/cancel PO, transfer reversal ledger integrity, role denials for every mutation, simulator with inactive and user-created data.
- Integration (real app, throw-away SQLite file): authentication and antiforgery, every demo role, role restrictions (403/401), FEFO allocation through `POST /api/orders`, pick and ship by a Picker, ledger consistency, PO receive (full, partial, forbidden), cold-chain rules, Admin-only reset, HTTPS redirect.

## Deployment

Target: Windows/IIS shared hosting, deployed over FTPS by `.github/workflows/ci-cd.yml` (same safety design as the portfolio site: target-folder validation, write probe, `app_offline.htm`, `mirror -R` without `--delete`, always-run cleanup, smoke test, concurrency).

1. Repo **variables**: `FTP_SERVER_DIR` (site folder, e.g. `inventory/`), `SITE_URL` (optional, default `https://inventory.alonsomiguel.com`), `DEPLOY_ENABLED=true` to turn deploys on. **Secrets**: `FTP_SERVER`, `FTP_USERNAME`, `FTP_PASSWORD`.
2. The workflow publishes self-contained `win-x86` (the host may not offer .NET 11); `web.config` is out-of-process with WebSockets enabled. Enable the WebSocket Protocol feature in the hosting panel.
3. Smoke test: `https://inventory.alonsomiguel.com/health`.
4. **Database**: SQLite is created and seeded on first start in `App_Data/`. The folder must be writable by the app pool. `App_Data/*.db*` is excluded from the publish output and from the FTP mirror, so later deployments never overwrite live data. The schema is created with `EnsureCreated` (no migrations); its version is stored in SQLite's `user_version`. When a release changes the schema (`DemoSeeder.SchemaVersion`), the first start logs a warning, drops and recreates the demo database and reseeds it (demo users are recreated too), so an existing `App_Data` upgrades by itself.
5. Concurrent users share one simulator and one database; this is intentionally a single-instance demo.

## License

MIT. See [LICENSE](LICENSE).
