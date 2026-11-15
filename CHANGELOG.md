# Changelog

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
