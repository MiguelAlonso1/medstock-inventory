# Changelog

## 1.0.0 - 2026-11-15

Initial release.

- Blazor Web App (Interactive Server) on .NET 11 with MudBlazor 9.13; dark-first navy/indigo/cyan theme, light/dark toggle via cookie (no JavaScript).
- EF Core + SQLite (`App_Data/medstock.db`), created and seeded on startup: 4 distribution centers, 300 products (SKU, NDC-style code, lots, expiry, cold-chain flag, unit cost), suppliers, customers, stock levels per location and lot, purchase orders, customer orders and a replayable stock movement ledger. All data is synthetic.
- Pages: Overview (KPIs and MudChart charts), Inventory (server-side MudDataGrid, lot drill-down), Product detail, Orders (create with MudForm validation, FEFO allocation, pick/ship flow), Purchase orders (reorder suggestions, receiving), Transfers, Alerts (low stock, expiring, cold-chain excursions), Live activity.
- Real-time: singleton event hub plus a background simulator that creates orders, ships, receives POs, transfers and raises alerts; every connected circuit updates live.
- ASP.NET Core Identity with four seeded roles and one-click demo logins (plain form posts); role-based authorization; Admin-only reset of demo data.
- `MedStock.Domain` class library (FEFO allocator, reorder calculator, expiry classifier, stock ledger, PO receiving, order workflow) with unit tests; WebApplicationFactory integration tests.
- Deployment: self-contained win-x86 publish, `web.config` (out-of-process, WebSockets), in-app HTTPS redirect and HSTS, GitHub Actions CI/CD with FTPS deploy gated by `DEPLOY_ENABLED`.
