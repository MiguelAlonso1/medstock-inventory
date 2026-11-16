using MedStock.Domain;
using MedStock.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace MedStock.Web.Services;

public sealed record ProductInput(int Id, string Sku, string Name, string NdcCode, string Category, string Unit, bool ColdChain, double UnitCost,
    int MinLevel, int MaxLevel, int PackSize, int ShelfLifeMonths, double AvgDailyDemand, int SupplierId);
public sealed record WarehouseInput(int Id, string Code, string Name, string City, bool ColdChainCapable);
public sealed record SupplierInput(int Id, string Name, string Country, int LeadTimeDays);
public sealed record CustomerInput(int Id, string Name, string City);

public enum MasterKind { Product, Warehouse, Supplier, Customer }

/// <summary>
/// CRUD for master data (products, warehouses, suppliers, customers). Every mutation checks the caller's role here, validates with the
/// domain rules, refuses to hard-delete referenced records (the caller is told to deactivate instead) and publishes an event.
/// </summary>
public sealed class MasterDataService(IDbContextFactory<MedStockDb> factory, WriteGate gate, InventoryEventHub hub)
{
    // ---- reads (anyone signed in may read; pages restrict the UI) ----
    public async Task<IReadOnlyList<Product>> ListProductsAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Products.AsNoTracking().Include(p => p.Supplier).OrderBy(p => p.Name).ToListAsync();
    }

    public async Task<IReadOnlyList<Warehouse>> ListWarehousesAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Warehouses.AsNoTracking().OrderBy(w => w.Code).ToListAsync();
    }

    public async Task<IReadOnlyList<Supplier>> ListSuppliersAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Suppliers.AsNoTracking().OrderBy(s => s.Name).ToListAsync();
    }

    public async Task<IReadOnlyList<Customer>> ListCustomersAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Customers.AsNoTracking().OrderBy(c => c.Name).ToListAsync();
    }

    // ---- products ----
    public Task<OpResult<int>> SaveProductAsync(ProductInput i, Caller caller) => gate.RunAsync(async () =>
    {
        if (!caller.CanManage) return OpResult<int>.Fail(Caller.DeniedManage);
        var errors = MasterDataRules.ValidateProduct(i.Sku, i.Name, i.Category, i.Unit, i.UnitCost, i.MinLevel, i.MaxLevel, i.PackSize, i.ShelfLifeMonths, i.AvgDailyDemand);
        if (errors.Count > 0) return OpResult<int>.Fail(errors[0]);
        await using var db = await factory.CreateDbContextAsync();
        var sku = i.Sku.Trim();
        var supplier = await db.Suppliers.FindAsync(i.SupplierId);
        if (supplier is null) return OpResult<int>.Fail("Choose a supplier.");
        Product? p = null;
        if (i.Id != 0)
        {
            p = await db.Products.FindAsync(i.Id);
            if (p is null) return OpResult<int>.Fail("Product not found.");
        }
        if (!supplier.IsActive && (p is null || p.SupplierId != supplier.Id)) return OpResult<int>.Fail($"{supplier.Name} is inactive.");
        if (await db.Products.AnyAsync(x => x.Sku == sku && x.Id != i.Id)) return OpResult<int>.Fail($"SKU {sku} is already used by another product.");
        if (i.ColdChain && p is { ColdChain: false } &&
            await db.StockLevels.AnyAsync(s => s.ProductId == p.Id && s.Quantity > 0 && !s.Warehouse!.ColdChainCapable))
            return OpResult<int>.Fail("This product has stock at warehouses that are not cold-chain capable, so it cannot be flagged cold-chain.");

        var isNew = p is null;
        p ??= new Product();
        p.Sku = sku; p.Name = i.Name.Trim(); p.NdcCode = (i.NdcCode ?? "").Trim(); p.Category = i.Category.Trim(); p.Unit = i.Unit.Trim();
        p.ColdChain = i.ColdChain; p.UnitCost = Math.Round(i.UnitCost, 2); p.MinLevel = i.MinLevel; p.MaxLevel = i.MaxLevel; p.PackSize = i.PackSize;
        p.ShelfLifeMonths = i.ShelfLifeMonths; p.AvgDailyDemand = i.AvgDailyDemand; p.SupplierId = supplier.Id;
        if (isNew) db.Products.Add(p);
        await db.SaveChangesAsync();
        hub.Publish(ActivityKind.System, $"Product {p.Sku} {(isNew ? "created" : "updated")} by {caller.Name}", Topic.Master | Topic.Stock | Topic.Alerts);
        return OpResult<int>.Success(p.Id);
    });

    // ---- warehouses ----
    public Task<OpResult<int>> SaveWarehouseAsync(WarehouseInput i, Caller caller) => gate.RunAsync(async () =>
    {
        if (!caller.CanManage) return OpResult<int>.Fail(Caller.DeniedManage);
        var errors = MasterDataRules.ValidateWarehouse(i.Code, i.Name, i.City);
        if (errors.Count > 0) return OpResult<int>.Fail(errors[0]);
        await using var db = await factory.CreateDbContextAsync();
        var code = i.Code.Trim().ToUpperInvariant();
        Warehouse? w = null;
        if (i.Id != 0)
        {
            w = await db.Warehouses.FindAsync(i.Id);
            if (w is null) return OpResult<int>.Fail("Warehouse not found.");
        }
        if (await db.Warehouses.AnyAsync(x => x.Code == code && x.Id != i.Id)) return OpResult<int>.Fail($"Code {code} is already used by another warehouse.");
        if (!i.ColdChainCapable && w is { ColdChainCapable: true } &&
            await db.StockLevels.AnyAsync(s => s.WarehouseId == w.Id && s.Quantity > 0 && s.Product!.ColdChain))
            return OpResult<int>.Fail("This warehouse holds cold-chain stock, so it cannot be switched to non cold-chain.");

        var isNew = w is null;
        w ??= new Warehouse();
        w.Code = code; w.Name = i.Name.Trim(); w.City = i.City.Trim(); w.ColdChainCapable = i.ColdChainCapable;
        if (isNew) db.Warehouses.Add(w);
        await db.SaveChangesAsync();
        hub.Publish(ActivityKind.System, $"Warehouse {w.Code} {(isNew ? "created" : "updated")} by {caller.Name}", Topic.Master | Topic.Stock);
        return OpResult<int>.Success(w.Id);
    });

    // ---- suppliers ----
    public Task<OpResult<int>> SaveSupplierAsync(SupplierInput i, Caller caller) => gate.RunAsync(async () =>
    {
        if (!caller.CanManage) return OpResult<int>.Fail(Caller.DeniedManage);
        var errors = MasterDataRules.ValidateSupplier(i.Name, i.Country, i.LeadTimeDays);
        if (errors.Count > 0) return OpResult<int>.Fail(errors[0]);
        await using var db = await factory.CreateDbContextAsync();
        var name = i.Name.Trim();
        Supplier? s = null;
        if (i.Id != 0)
        {
            s = await db.Suppliers.FindAsync(i.Id);
            if (s is null) return OpResult<int>.Fail("Supplier not found.");
        }
        if (await db.Suppliers.AnyAsync(x => x.Name == name && x.Id != i.Id)) return OpResult<int>.Fail($"A supplier named {name} already exists.");
        var isNew = s is null;
        s ??= new Supplier();
        s.Name = name; s.Country = i.Country.Trim(); s.LeadTimeDays = i.LeadTimeDays;
        if (isNew) db.Suppliers.Add(s);
        await db.SaveChangesAsync();
        hub.Publish(ActivityKind.System, $"Supplier {s.Name} {(isNew ? "created" : "updated")} by {caller.Name}", Topic.Master | Topic.Purchasing);
        return OpResult<int>.Success(s.Id);
    });

    // ---- customers ----
    public Task<OpResult<int>> SaveCustomerAsync(CustomerInput i, Caller caller) => gate.RunAsync(async () =>
    {
        if (!caller.CanManage) return OpResult<int>.Fail(Caller.DeniedManage);
        var errors = MasterDataRules.ValidateCustomer(i.Name, i.City);
        if (errors.Count > 0) return OpResult<int>.Fail(errors[0]);
        await using var db = await factory.CreateDbContextAsync();
        var name = i.Name.Trim();
        Customer? c = null;
        if (i.Id != 0)
        {
            c = await db.Customers.FindAsync(i.Id);
            if (c is null) return OpResult<int>.Fail("Customer not found.");
        }
        if (await db.Customers.AnyAsync(x => x.Name == name && x.Id != i.Id)) return OpResult<int>.Fail($"A customer named {name} already exists.");
        var isNew = c is null;
        c ??= new Customer();
        c.Name = name; c.City = i.City.Trim();
        if (isNew) db.Customers.Add(c);
        await db.SaveChangesAsync();
        hub.Publish(ActivityKind.System, $"Customer {c.Name} {(isNew ? "created" : "updated")} by {caller.Name}", Topic.Master | Topic.Orders);
        return OpResult<int>.Success(c.Id);
    });

    // ---- reference checks, delete, deactivate ----
    private static async Task<(string What, ReferenceCounts Refs)?> ReferencesAsync(MedStockDb db, MasterKind kind, int id)
    {
        switch (kind)
        {
            case MasterKind.Product:
                var p = await db.Products.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
                if (p is null) return null;
                return ($"Product {p.Sku}", ReferenceCounts.Of(
                    ("stock level(s)", await db.StockLevels.CountAsync(x => x.ProductId == id)),
                    ("lot(s)", await db.Lots.CountAsync(x => x.ProductId == id)),
                    ("order line(s)", await db.OrderLines.CountAsync(x => x.ProductId == id)),
                    ("purchase order line(s)", await db.PurchaseOrderLines.CountAsync(x => x.ProductId == id)),
                    ("stock movement(s)", await db.Movements.CountAsync(x => x.ProductId == id)),
                    ("transfer(s)", await db.Transfers.CountAsync(x => x.ProductId == id))));
            case MasterKind.Warehouse:
                var w = await db.Warehouses.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
                if (w is null) return null;
                return ($"Warehouse {w.Code}", ReferenceCounts.Of(
                    ("stock level(s)", await db.StockLevels.CountAsync(x => x.WarehouseId == id)),
                    ("stock movement(s)", await db.Movements.CountAsync(x => x.WarehouseId == id)),
                    ("customer order(s)", await db.Orders.CountAsync(x => x.WarehouseId == id)),
                    ("purchase order(s)", await db.PurchaseOrders.CountAsync(x => x.WarehouseId == id)),
                    ("transfer(s)", await db.Transfers.CountAsync(x => x.FromWarehouseId == id || x.ToWarehouseId == id)),
                    ("temperature record(s)", await db.Excursions.CountAsync(x => x.WarehouseId == id))));
            case MasterKind.Supplier:
                var s = await db.Suppliers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
                if (s is null) return null;
                return ($"Supplier {s.Name}", ReferenceCounts.Of(
                    ("product(s)", await db.Products.CountAsync(x => x.SupplierId == id)),
                    ("purchase order(s)", await db.PurchaseOrders.CountAsync(x => x.SupplierId == id))));
            default:
                var c = await db.Customers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
                if (c is null) return null;
                return ($"Customer {c.Name}", ReferenceCounts.Of(("customer order(s)", await db.Orders.CountAsync(x => x.CustomerId == id))));
        }
    }

    public async Task<ReferenceCounts?> GetReferencesAsync(MasterKind kind, int id)
    {
        await using var db = await factory.CreateDbContextAsync();
        return (await ReferencesAsync(db, kind, id))?.Refs;
    }

    /// <summary>Deletes a record nothing refers to. Referenced records come back as Blocked (the UI then offers Deactivate).</summary>
    public Task<DeleteResult> DeleteAsync(MasterKind kind, int id, Caller caller) => gate.RunAsync(async () =>
    {
        if (!caller.CanManage) return DeleteResult.Fail(Caller.DeniedManage);
        await using var db = await factory.CreateDbContextAsync();
        var info = await ReferencesAsync(db, kind, id);
        if (info is null) return DeleteResult.Fail("Record not found.");
        var (what, refs) = info.Value;
        if (!DeletePolicy.CanDelete(refs)) return DeleteResult.Block(DeletePolicy.BlockedMessage(what, refs));
        switch (kind)
        {
            case MasterKind.Product: db.Products.Remove((await db.Products.FindAsync(id))!); break;
            case MasterKind.Warehouse: db.Warehouses.Remove((await db.Warehouses.FindAsync(id))!); break;
            case MasterKind.Supplier: db.Suppliers.Remove((await db.Suppliers.FindAsync(id))!); break;
            default: db.Customers.Remove((await db.Customers.FindAsync(id))!); break;
        }
        await db.SaveChangesAsync();
        hub.Publish(ActivityKind.System, $"{what} deleted by {caller.Name}", Topic.Master | Topic.Stock | Topic.Orders | Topic.Purchasing, "warning");
        return DeleteResult.Done();
    });

    /// <summary>Deactivate hides the record from pickers, suggestions and the simulator but keeps all history readable.</summary>
    public Task<OpResult> SetActiveAsync(MasterKind kind, int id, bool active, Caller caller) => gate.RunAsync(async () =>
    {
        if (!caller.CanManage) return OpResult.Fail(Caller.DeniedManage);
        await using var db = await factory.CreateDbContextAsync();
        string label;
        switch (kind)
        {
            case MasterKind.Product:
                var p = await db.Products.FindAsync(id);
                if (p is null) return OpResult.Fail("Record not found.");
                p.IsActive = active; label = $"Product {p.Sku}";
                break;
            case MasterKind.Warehouse:
                var w = await db.Warehouses.FindAsync(id);
                if (w is null) return OpResult.Fail("Record not found.");
                if (!active)
                {
                    if (await db.StockLevels.AnyAsync(s => s.WarehouseId == id && (s.Quantity > 0 || s.Allocated > 0)))
                        return OpResult.Fail($"{w.Code} still holds stock. Transfer it out before deactivating the warehouse.");
                    if (await db.Orders.AnyAsync(o => o.WarehouseId == id && (o.Status == OrderStatus.Pending || o.Status == OrderStatus.Allocated || o.Status == OrderStatus.Picking)) ||
                        await db.PurchaseOrders.AnyAsync(o => o.WarehouseId == id && (o.Status == PurchaseOrderStatus.Ordered || o.Status == PurchaseOrderStatus.PartiallyReceived)))
                        return OpResult.Fail($"{w.Code} has open orders or purchase orders. Finish or cancel them first.");
                }
                w.IsActive = active; label = $"Warehouse {w.Code}";
                break;
            case MasterKind.Supplier:
                var s2 = await db.Suppliers.FindAsync(id);
                if (s2 is null) return OpResult.Fail("Record not found.");
                s2.IsActive = active; label = $"Supplier {s2.Name}";
                break;
            default:
                var c = await db.Customers.FindAsync(id);
                if (c is null) return OpResult.Fail("Record not found.");
                c.IsActive = active; label = $"Customer {c.Name}";
                break;
        }
        await db.SaveChangesAsync();
        hub.Publish(ActivityKind.System, $"{label} {(active ? "reactivated" : "deactivated")} by {caller.Name}", Topic.Master | Topic.Stock | Topic.Alerts);
        return OpResult.Success();
    });
}
