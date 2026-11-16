# Changelog

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
