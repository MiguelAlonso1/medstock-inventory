using MedStock.Domain;
using MedStock.Web.Data;
using MedStock.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MedStock.Web.Tests;

/// <summary>Service-level tests for the CRUD features. Own app instance (and database) so tests may mutate freely.</summary>
public class CrudServiceTests(MedStockFactory app) : IClassFixture<MedStockFactory>
{
    private static readonly Caller Admin = Caller.ForRole(Roles.Admin, "Admin Tester");
    private static readonly Caller Manager = Caller.ForRole(Roles.WarehouseManager, "Manager Tester");
    private static readonly Caller Picker = Caller.ForRole(Roles.Picker, "Picker Tester");
    private static readonly Caller Viewer = Caller.ForRole(Roles.Viewer, "Viewer Tester");

    private T Svc<T>() where T : notnull => app.Services.GetRequiredService<T>();
    private MasterDataService Master => Svc<MasterDataService>();
    private OrderService Orders => Svc<OrderService>();
    private PurchasingService Purchasing => Svc<PurchasingService>();
    private TransferService Transfers => Svc<TransferService>();
    private static string Uniq() => Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private static ProductInput NewProduct(int supplierId, string? sku = null, bool cold = false) =>
        new(0, sku ?? "T-" + Uniq(), "Test product " + Uniq(), "", "Test category", "box", cold, 12.5, 10, 50, 1, 24, 1, supplierId);

    private Task<int> AnySupplierAsync() => app.QueryAsync(db => db.Suppliers.Where(s => s.IsActive).Select(s => s.Id).FirstAsync());
    private Task<int> AnyCustomerAsync() => app.QueryAsync(db => db.Customers.Where(c => c.IsActive).Select(c => c.Id).FirstAsync());

    private sealed record Fixture(int WarehouseId, int Warehouse2Id, int ProductId, int SupplierId, int LotA, int LotB, int LevelA, int LevelB);

    /// <summary>Own warehouses/product with two lots (A expires sooner, 10 units each) plus matching receipt movements so the ledger stays consistent.</summary>
    private async Task<Fixture> SeedStockAsync(bool cold = false)
    {
        var supplierId = await AnySupplierAsync();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return await app.QueryAsync(async db =>
        {
            var w1 = new Warehouse { Code = "W" + Uniq()[..5], Name = "Test WH 1", City = "X", ColdChainCapable = true };
            var w2 = new Warehouse { Code = "W" + Uniq()[..5], Name = "Test WH 2", City = "X", ColdChainCapable = true };
            var p = new Product { Sku = "F-" + Uniq(), Name = "Fixture " + Uniq(), Category = "Test", Unit = "box", ColdChain = cold, UnitCost = 2, MinLevel = 1, MaxLevel = 5, SupplierId = supplierId };
            db.AddRange(w1, w2, p);
            await db.SaveChangesAsync();
            var a = new Lot { ProductId = p.Id, LotNumber = "LA" + Uniq(), ExpirationDate = today.AddDays(30) };
            var b = new Lot { ProductId = p.Id, LotNumber = "LB" + Uniq(), ExpirationDate = today.AddDays(60) };
            db.AddRange(a, b);
            await db.SaveChangesAsync();
            var la = new StockLevel { WarehouseId = w1.Id, ProductId = p.Id, LotId = a.Id, Quantity = 10 };
            var lb = new StockLevel { WarehouseId = w1.Id, ProductId = p.Id, LotId = b.Id, Quantity = 10 };
            db.AddRange(la, lb);
            foreach (var l in new[] { la, lb })
                db.Movements.Add(new StockMovement { TimestampUtc = DateTime.UtcNow, Type = MovementType.Receipt, ProductId = p.Id, LotId = l.LotId, WarehouseId = w1.Id, Quantity = 10, Reference = "TEST", Actor = "test" });
            await db.SaveChangesAsync();
            return new Fixture(w1.Id, w2.Id, p.Id, supplierId, a.Id, b.Id, la.Id, lb.Id);
        });
    }

    private Task<(int Qty, int Alloc)> LevelAsync(int levelId) =>
        app.QueryAsync(async db => { var l = await db.StockLevels.AsNoTracking().FirstAsync(x => x.Id == levelId); return (l.Quantity, l.Allocated); });

    private async Task AssertLedgerCleanAsync()
    {
        var (_, violations, mismatches) = await Svc<InventoryQueries>().VerifyLedgerAsync();
        Assert.Equal(0, violations);
        Assert.Equal(0, mismatches);
    }

    // ---------------------------------------------------------------- products

    [Fact]
    public async Task Product_Create_Edit_Delete()
    {
        var sup = await AnySupplierAsync();
        var created = await Master.SaveProductAsync(NewProduct(sup), Manager);
        Assert.True(created.Ok, created.Error);
        var p = (await Master.ListProductsAsync()).Single(x => x.Id == created.Value);
        Assert.True(p.IsActive);

        var edited = await Master.SaveProductAsync(NewProduct(sup, p.Sku) with { Id = p.Id, Name = "Renamed", UnitCost = 99.99 }, Manager);
        Assert.True(edited.Ok, edited.Error);
        var after = (await Master.ListProductsAsync()).Single(x => x.Id == p.Id);
        Assert.Equal("Renamed", after.Name);
        Assert.Equal(99.99, after.UnitCost);

        var del = await Master.DeleteAsync(MasterKind.Product, p.Id, Manager);
        Assert.True(del.Deleted);
        Assert.DoesNotContain(await Master.ListProductsAsync(), x => x.Id == p.Id);
    }

    [Fact]
    public async Task Product_Sku_MustBeUnique_AndFieldsValidated()
    {
        var sup = await AnySupplierAsync();
        var first = await Master.SaveProductAsync(NewProduct(sup), Admin);
        var sku = (await Master.ListProductsAsync()).Single(x => x.Id == first.Value).Sku;
        var dup = await Master.SaveProductAsync(NewProduct(sup, sku), Admin);
        Assert.False(dup.Ok);
        Assert.Contains("SKU", dup.Error);
        Assert.False((await Master.SaveProductAsync(NewProduct(sup) with { MinLevel = 20, MaxLevel = 5 }, Admin)).Ok);
        Assert.False((await Master.SaveProductAsync(NewProduct(sup) with { Name = " " }, Admin)).Ok);
        Assert.False((await Master.SaveProductAsync(NewProduct(0), Admin)).Ok);
    }

    [Fact]
    public async Task Product_WithHistory_CannotBeDeleted_ButCanBeDeactivated_AndIsHiddenFromPickers()
    {
        var f = await SeedStockAsync();
        var blocked = await Master.DeleteAsync(MasterKind.Product, f.ProductId, Manager);
        Assert.True(blocked.Blocked);
        Assert.False(blocked.Deleted);
        Assert.Contains("Deactivate", blocked.Message);
        Assert.True(await app.QueryAsync(db => db.Products.AnyAsync(p => p.Id == f.ProductId)));

        var queries = Svc<InventoryQueries>();
        var name = await app.QueryAsync(db => db.Products.Where(p => p.Id == f.ProductId).Select(p => p.Sku).FirstAsync());
        Assert.Contains(await queries.SearchProductsAsync(name), o => o.Id == f.ProductId);

        Assert.True((await Master.SetActiveAsync(MasterKind.Product, f.ProductId, false, Manager)).Ok);
        Assert.DoesNotContain(await queries.SearchProductsAsync(name), o => o.Id == f.ProductId);
        Assert.Contains(await Master.ListProductsAsync(), p => p.Id == f.ProductId && !p.IsActive);   // still readable
        Assert.NotNull(await queries.GetProductAsync(f.ProductId));                                   // history stays readable
        Assert.DoesNotContain(await queries.GetLowStockAsync(), a => a.ProductId == f.ProductId);

        var cust = await AnyCustomerAsync();
        var order = await Orders.CreateAsync(new NewOrderRequest(cust, f.WarehouseId, [new NewOrderLine(f.ProductId, 1)]), Manager);
        Assert.False(order.Ok);
        Assert.Contains("inactive", order.Error);

        Assert.True((await Master.SetActiveAsync(MasterKind.Product, f.ProductId, true, Manager)).Ok);
        Assert.Contains(await queries.SearchProductsAsync(name), o => o.Id == f.ProductId);
    }

    [Fact]
    public async Task Product_ColdChainFlag_CannotBeSetWhenStockIsAtNonColdWarehouse()
    {
        var f = await SeedStockAsync();
        await app.QueryAsync(async db => { await db.Warehouses.Where(w => w.Id == f.WarehouseId).ExecuteUpdateAsync(s => s.SetProperty(w => w.ColdChainCapable, false)); return 0; });
        var p = (await Master.ListProductsAsync()).Single(x => x.Id == f.ProductId);
        var r = await Master.SaveProductAsync(new ProductInput(p.Id, p.Sku, p.Name, "", p.Category, p.Unit, true, p.UnitCost, p.MinLevel, p.MaxLevel, 1, 24, 0, p.SupplierId), Manager);
        Assert.False(r.Ok);
        Assert.Contains("cold-chain", r.Error);
    }

    // ------------------------------------------------- warehouses / suppliers / customers

    [Fact]
    public async Task Warehouse_Crud_CodeUnique_DeleteBlockedWhenReferenced_DeactivateRules()
    {
        var code = "N" + Uniq()[..5];
        var created = await Master.SaveWarehouseAsync(new WarehouseInput(0, code.ToLowerInvariant(), "New WH", "Town", false), Manager);
        Assert.True(created.Ok, created.Error);
        var wh = (await Master.ListWarehousesAsync()).Single(w => w.Id == created.Value);
        Assert.Equal(code, wh.Code);   // normalized to upper case

        Assert.False((await Master.SaveWarehouseAsync(new WarehouseInput(0, code, "Other", "Town", false), Manager)).Ok);
        Assert.True((await Master.SaveWarehouseAsync(new WarehouseInput(wh.Id, code, "Renamed WH", "City", true), Manager)).Ok);
        Assert.Equal("Renamed WH", (await Master.ListWarehousesAsync()).Single(w => w.Id == wh.Id).Name);

        // an empty warehouse can be deactivated (hidden from pickers) and reactivated, then deleted
        Assert.Contains((await Svc<InventoryQueries>().GetLookupsAsync()).Warehouses, w => w.Id == wh.Id);
        Assert.True((await Master.SetActiveAsync(MasterKind.Warehouse, wh.Id, false, Manager)).Ok);
        Assert.DoesNotContain((await Svc<InventoryQueries>().GetLookupsAsync()).Warehouses, w => w.Id == wh.Id);
        Assert.True((await Master.DeleteAsync(MasterKind.Warehouse, wh.Id, Manager)).Deleted);

        // a warehouse with stock: delete blocked, deactivate refused until emptied
        var f = await SeedStockAsync();
        Assert.True((await Master.DeleteAsync(MasterKind.Warehouse, f.WarehouseId, Manager)).Blocked);
        var deact = await Master.SetActiveAsync(MasterKind.Warehouse, f.WarehouseId, false, Manager);
        Assert.False(deact.Ok);
        Assert.Contains("stock", deact.Error);
    }

    [Fact]
    public async Task Warehouse_CannotLoseColdChain_WhileHoldingColdStock()
    {
        var f = await SeedStockAsync(cold: true);
        var wh = (await Master.ListWarehousesAsync()).Single(w => w.Id == f.WarehouseId);
        var r = await Master.SaveWarehouseAsync(new WarehouseInput(wh.Id, wh.Code, wh.Name, wh.City, false), Manager);
        Assert.False(r.Ok);
        Assert.Contains("cold-chain", r.Error);
    }

    [Fact]
    public async Task Supplier_Crud_AndDeleteRules()
    {
        var created = await Master.SaveSupplierAsync(new SupplierInput(0, "Test Supplier " + Uniq(), "US", 5), Manager);
        Assert.True(created.Ok, created.Error);
        Assert.True((await Master.SaveSupplierAsync(new SupplierInput(created.Value, "Edited Supplier " + Uniq(), "CA", 9), Manager)).Ok);
        var s = (await Master.ListSuppliersAsync()).Single(x => x.Id == created.Value);
        Assert.Equal(9, s.LeadTimeDays);
        Assert.False((await Master.SaveSupplierAsync(new SupplierInput(0, s.Name, "US", 5), Manager)).Ok);   // duplicate name
        Assert.False((await Master.SaveSupplierAsync(new SupplierInput(0, "Bad lead", "US", -3), Manager)).Ok);

        // referenced by a product -> blocked, deactivate works and hides it from the picker
        Assert.True((await Master.SaveProductAsync(NewProduct(s.Id), Manager)).Ok);
        var blocked = await Master.DeleteAsync(MasterKind.Supplier, s.Id, Manager);
        Assert.True(blocked.Blocked);
        Assert.True((await Master.SetActiveAsync(MasterKind.Supplier, s.Id, false, Manager)).Ok);
        Assert.DoesNotContain((await Svc<InventoryQueries>().GetLookupsAsync()).Suppliers, x => x.Id == s.Id);

        var lone = await Master.SaveSupplierAsync(new SupplierInput(0, "Lonely " + Uniq(), "US", 1), Manager);
        Assert.True((await Master.DeleteAsync(MasterKind.Supplier, lone.Value, Manager)).Deleted);
    }

    [Fact]
    public async Task Customer_Crud_AndDeleteRules()
    {
        var created = await Master.SaveCustomerAsync(new CustomerInput(0, "Test Clinic " + Uniq(), "Town"), Manager);
        Assert.True(created.Ok, created.Error);
        Assert.True((await Master.SaveCustomerAsync(new CustomerInput(created.Value, "Edited Clinic " + Uniq(), "City"), Manager)).Ok);
        Assert.Equal("City", (await Master.ListCustomersAsync()).Single(c => c.Id == created.Value).City);
        Assert.False((await Master.SaveCustomerAsync(new CustomerInput(0, "", "x"), Manager)).Ok);

        var f = await SeedStockAsync();
        var order = await Orders.CreateAsync(new NewOrderRequest(created.Value, f.WarehouseId, [new NewOrderLine(f.ProductId, 2)]), Manager);
        Assert.True(order.Ok, order.Error);
        Assert.True((await Master.DeleteAsync(MasterKind.Customer, created.Value, Manager)).Blocked);
        Assert.True((await Master.SetActiveAsync(MasterKind.Customer, created.Value, false, Manager)).Ok);
        Assert.DoesNotContain((await Svc<InventoryQueries>().GetLookupsAsync()).Customers, c => c.Id == created.Value);
        Assert.NotNull(await Orders.GetAsync(order.Value!.Id));   // history readable
        var again = await Orders.CreateAsync(new NewOrderRequest(created.Value, f.WarehouseId, [new NewOrderLine(f.ProductId, 1)]), Manager);
        Assert.False(again.Ok);

        var lone = await Master.SaveCustomerAsync(new CustomerInput(0, "Lonely " + Uniq(), "Town"), Manager);
        Assert.True((await Master.DeleteAsync(MasterKind.Customer, lone.Value, Manager)).Deleted);
    }

    // ---------------------------------------------------------------- role denials

    [Theory]
    [InlineData("Picker")]
    [InlineData("Viewer")]
    public async Task MasterData_Mutations_AreDeniedBelowManagerRole(string role)
    {
        var who = Caller.ForRole(role);
        var sup = await AnySupplierAsync();
        Assert.False((await Master.SaveProductAsync(NewProduct(sup), who)).Ok);
        Assert.False((await Master.SaveWarehouseAsync(new WarehouseInput(0, "ZZ" + Uniq()[..4], "n", "c", false), who)).Ok);
        Assert.False((await Master.SaveSupplierAsync(new SupplierInput(0, "x" + Uniq(), "US", 1), who)).Ok);
        Assert.False((await Master.SaveCustomerAsync(new CustomerInput(0, "x" + Uniq(), "c"), who)).Ok);
        var del = await Master.DeleteAsync(MasterKind.Product, 1, who);
        Assert.False(del.Deleted); Assert.False(del.Blocked);
        Assert.False((await Master.SetActiveAsync(MasterKind.Customer, 1, false, who)).Ok);
        Assert.True(await app.QueryAsync(db => db.Customers.AllAsync(c => c.IsActive)));   // nothing changed
    }

    [Fact]
    public async Task OrderPoTransfer_Mutations_AreDeniedWithoutTheRole()
    {
        var f = await SeedStockAsync();
        var cust = await AnyCustomerAsync();
        var order = (await Orders.CreateAsync(new NewOrderRequest(cust, f.WarehouseId, [new NewOrderLine(f.ProductId, 3)]), Manager)).Value!;
        var tr = await Transfers.TransferAsync(f.ProductId, f.LotA, f.WarehouseId, f.Warehouse2Id, 2, Manager);
        Assert.True(tr.Ok, tr.Error);
        var trId = await app.QueryAsync(db => db.Transfers.Where(t => t.Number == tr.Value).Select(t => t.Id).FirstAsync());
        var po = await Purchasing.CreateManualAsync(f.SupplierId, f.WarehouseId, null, [new NewOrderLine(f.ProductId, 4)], Manager);
        Assert.True(po.Ok, po.Error);
        var poId = await app.QueryAsync(db => db.PurchaseOrders.Where(p => p.Number == po.Value).Select(p => p.Id).FirstAsync());

        foreach (var who in new[] { Picker, Viewer })
        {
            Assert.False((await Orders.CreateAsync(new NewOrderRequest(cust, f.WarehouseId, [new NewOrderLine(f.ProductId, 1)]), who)).Ok);
            Assert.False((await Orders.UpdateLinesAsync(order.Id, [new NewOrderLine(f.ProductId, 1)], who)).Ok);
            Assert.False((await Orders.CancelAsync(order.Id, who)).Ok);
            Assert.False((await Orders.ReallocateAsync(order.Id, who)).Ok);
            Assert.False((await Orders.DeleteAsync(order.Id, who)).Ok);
            Assert.False((await Purchasing.CancelAsync(poId, who)).Ok);
            Assert.False((await Purchasing.UpdateQuantitiesAsync(poId, new Dictionary<int, int>(), null, who)).Ok);
            Assert.False((await Purchasing.CreateManualAsync(f.SupplierId, f.WarehouseId, null, [new NewOrderLine(f.ProductId, 4)], who)).Ok);
            Assert.False((await Purchasing.ReceiveAsync(poId, who)).Ok);
            Assert.False((await Transfers.ReverseAsync(trId, who)).Ok);
            Assert.False((await Transfers.TransferAsync(f.ProductId, f.LotA, f.WarehouseId, f.Warehouse2Id, 1, who)).Ok);
        }
        // pickers may fulfil, viewers may not
        Assert.False((await Orders.AdvanceAsync(order.Id, Viewer)).Ok);
        Assert.True((await Orders.AdvanceAsync(order.Id, Picker)).Ok);
        // managers cannot hard-delete orders, only admins can (and only cancelled ones)
        Assert.False((await Orders.DeleteAsync(order.Id, Manager)).Ok);
        Assert.False((await Orders.DeleteAsync(order.Id, Admin)).Ok);
        // nothing leaked through the denials
        Assert.Equal(OrderStatus.Picking, (await Orders.GetAsync(order.Id))!.Status);
        Assert.Equal(PurchaseOrderStatus.Ordered, (await Purchasing.GetAsync(poId))!.Status);
        await AssertLedgerCleanAsync();
    }

    // ---------------------------------------------------------------- order line edits

    [Fact]
    public async Task OrderEdit_ReallocatesFefo_ReleasesOldReservations_NoLeaks()
    {
        var f = await SeedStockAsync();
        var cust = await AnyCustomerAsync();
        var created = await Orders.CreateAsync(new NewOrderRequest(cust, f.WarehouseId, [new NewOrderLine(f.ProductId, 8)]), Manager);
        Assert.True(created.Ok, created.Error);
        var id = created.Value!.Id;
        Assert.Equal((10, 8), await LevelAsync(f.LevelA));   // lot A expires first
        Assert.Equal((10, 0), await LevelAsync(f.LevelB));

        // increase to 15: A fully used, B gets the rest, A is not double reserved
        var up = await Orders.UpdateLinesAsync(id, [new NewOrderLine(f.ProductId, 15)], Manager);
        Assert.True(up.Ok, up.Error);
        Assert.Equal(15, up.Value!.TotalAllocated);
        Assert.Equal((10, 10), await LevelAsync(f.LevelA));
        Assert.Equal((10, 5), await LevelAsync(f.LevelB));
        var lots = up.Value.Lines.Single().Lots;
        Assert.Equal([10, 5], lots.Select(l => l.Quantity).ToArray());
        Assert.True(lots[0].Expiry < lots[1].Expiry);

        // decrease to 3: everything comes from A, B is released
        var down = await Orders.UpdateLinesAsync(id, [new NewOrderLine(f.ProductId, 3)], Manager);
        Assert.True(down.Ok, down.Error);
        Assert.Equal((10, 3), await LevelAsync(f.LevelA));
        Assert.Equal((10, 0), await LevelAsync(f.LevelB));

        // over-ask: shortfall is reported, reservations never exceed stock
        var over = await Orders.UpdateLinesAsync(id, [new NewOrderLine(f.ProductId, 50)], Manager);
        Assert.True(over.Ok);
        Assert.Equal(20, over.Value!.TotalAllocated);
        Assert.True(over.Value.HasShortfall);
        Assert.Equal((10, 10), await LevelAsync(f.LevelA));
        Assert.Equal((10, 10), await LevelAsync(f.LevelB));

        // duplicate lines merge; invalid quantities are rejected and change nothing
        var merged = await Orders.UpdateLinesAsync(id, [new NewOrderLine(f.ProductId, 2), new NewOrderLine(f.ProductId, 2)], Manager);
        Assert.Equal(4, merged.Value!.TotalOrdered);
        Assert.False((await Orders.UpdateLinesAsync(id, [new NewOrderLine(f.ProductId, 0)], Manager)).Ok);
        Assert.False((await Orders.UpdateLinesAsync(id, [], Manager)).Ok);
        Assert.Equal((10, 4), await LevelAsync(f.LevelA));

        // cancelling after edits releases everything
        Assert.True((await Orders.CancelAsync(id, Manager)).Ok);
        Assert.Equal((10, 0), await LevelAsync(f.LevelA));
        Assert.Equal((10, 0), await LevelAsync(f.LevelB));
        await AssertLedgerCleanAsync();
    }

    [Fact]
    public async Task OrderEdit_AddAndRemoveLines_AcrossProducts()
    {
        var f = await SeedStockAsync();
        var g = await SeedStockAsync();   // a different product at a different warehouse
        var cust = await AnyCustomerAsync();
        var id = (await Orders.CreateAsync(new NewOrderRequest(cust, f.WarehouseId, [new NewOrderLine(f.ProductId, 4)]), Manager)).Value!.Id;

        // swap the product: the old reservation must be released, the new product has no stock at this warehouse -> nothing reserved
        var swapped = await Orders.UpdateLinesAsync(id, [new NewOrderLine(g.ProductId, 4)], Manager);
        Assert.True(swapped.Ok, swapped.Error);
        Assert.Equal(0, swapped.Value!.TotalAllocated);
        Assert.Equal(OrderStatus.Pending, swapped.Value.Status);
        Assert.Equal((10, 0), await LevelAsync(f.LevelA));
        Assert.Equal((10, 0), await LevelAsync(g.LevelA));

        // back to the original product plus a second line for it elsewhere is merged; status returns to Allocated
        var back = await Orders.UpdateLinesAsync(id, [new NewOrderLine(f.ProductId, 5)], Manager);
        Assert.Equal(OrderStatus.Allocated, back.Value!.Status);
        Assert.Equal((10, 5), await LevelAsync(f.LevelA));
        Assert.Single(await app.QueryAsync(db => db.OrderLines.Where(l => l.OrderId == id).ToListAsync()));
        // no orphan allocations
        Assert.Equal(5, await app.QueryAsync(db => db.OrderAllocations.Where(a => db.OrderLines.Any(l => l.Id == a.OrderLineId && l.OrderId == id)).SumAsync(a => a.Quantity)));
        // the total reserved across all stock levels of the product equals what the order holds
        Assert.Equal(5, await app.QueryAsync(db => db.StockLevels.Where(s => s.ProductId == f.ProductId).SumAsync(s => s.Allocated)));
    }

    [Fact]
    public async Task OrderEdit_RejectedOnceThePickingStarts_OrForInactiveAndColdMismatch()
    {
        var f = await SeedStockAsync();
        var cust = await AnyCustomerAsync();
        var id = (await Orders.CreateAsync(new NewOrderRequest(cust, f.WarehouseId, [new NewOrderLine(f.ProductId, 4)]), Manager)).Value!.Id;

        // cold-chain product cannot be added to an order fulfilled from a non cold-chain warehouse
        var coldSeed = await SeedStockAsync(cold: true);
        await app.QueryAsync(async db => { await db.Warehouses.Where(w => w.Id == f.WarehouseId).ExecuteUpdateAsync(s => s.SetProperty(w => w.ColdChainCapable, false)); return 0; });
        var cold = await Orders.UpdateLinesAsync(id, [new NewOrderLine(coldSeed.ProductId, 1)], Manager);
        Assert.False(cold.Ok);
        Assert.Contains("cold-chain", cold.Error);

        Assert.True((await Orders.AdvanceAsync(id, Manager)).Ok);   // Allocated -> Picking
        var late = await Orders.UpdateLinesAsync(id, [new NewOrderLine(f.ProductId, 1)], Manager);
        Assert.False(late.Ok);
        Assert.Contains("edited", late.Error);
        Assert.Equal((10, 4), await LevelAsync(f.LevelA));   // reservation untouched
    }

    [Fact]
    public async Task OrderDelete_AdminOnly_CancelledOnly_NoMovements()
    {
        var f = await SeedStockAsync();
        var cust = await AnyCustomerAsync();
        var open = (await Orders.CreateAsync(new NewOrderRequest(cust, f.WarehouseId, [new NewOrderLine(f.ProductId, 2)]), Manager)).Value!;
        Assert.False((await Orders.DeleteAsync(open.Id, Admin)).Ok);   // not cancelled
        Assert.True((await Orders.CancelAsync(open.Id, Manager)).Ok);
        Assert.False((await Orders.DeleteAsync(open.Id, Manager)).Ok);  // manager denied
        Assert.True((await Orders.DeleteAsync(open.Id, Admin)).Ok);
        Assert.Null(await Orders.GetAsync(open.Id));
        Assert.False(await app.QueryAsync(db => db.OrderLines.AnyAsync(l => l.OrderId == open.Id)));

        // shipped orders (with movements) can never be deleted
        var shipped = (await Orders.CreateAsync(new NewOrderRequest(cust, f.WarehouseId, [new NewOrderLine(f.ProductId, 2)]), Manager)).Value!;
        await Orders.AdvanceAsync(shipped.Id, Picker); await Orders.AdvanceAsync(shipped.Id, Picker);
        Assert.False((await Orders.DeleteAsync(shipped.Id, Admin)).Ok);
        // and even a (hypothetical) cancelled order that has movements is protected
        await app.QueryAsync(async db => { await db.Orders.Where(o => o.Id == shipped.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatus.Cancelled)); return 0; });
        var blocked = await Orders.DeleteAsync(shipped.Id, Admin);
        Assert.False(blocked.Ok);
        Assert.Contains("movements", blocked.Error);
    }

    // ---------------------------------------------------------------- purchase orders

    [Fact]
    public async Task PurchaseOrder_ManualCreate_Validation_AndSuccess()
    {
        var f = await SeedStockAsync();
        var other = await Master.SaveSupplierAsync(new SupplierInput(0, "Other " + Uniq(), "US", 3), Manager);

        Assert.False((await Purchasing.CreateManualAsync(other.Value, f.WarehouseId, null, [new NewOrderLine(f.ProductId, 5)], Manager)).Ok);   // wrong supplier
        Assert.False((await Purchasing.CreateManualAsync(f.SupplierId, f.WarehouseId, null, [], Manager)).Ok);
        Assert.False((await Purchasing.CreateManualAsync(f.SupplierId, f.WarehouseId, null, [new NewOrderLine(f.ProductId, 0)], Manager)).Ok);
        Assert.False((await Purchasing.CreateManualAsync(f.SupplierId, f.WarehouseId, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-3), [new NewOrderLine(f.ProductId, 5)], Manager)).Ok);

        var cold = await SeedStockAsync(cold: true);
        await app.QueryAsync(async db => { await db.Warehouses.Where(w => w.Id == f.WarehouseId).ExecuteUpdateAsync(s => s.SetProperty(w => w.ColdChainCapable, false)); return 0; });
        var coldFail = await Purchasing.CreateManualAsync(cold.SupplierId, f.WarehouseId, null, [new NewOrderLine(cold.ProductId, 5)], Manager);
        Assert.False(coldFail.Ok);
        Assert.Contains("cold-chain", coldFail.Error);

        var ok = await Purchasing.CreateManualAsync(f.SupplierId, f.WarehouseId, null, [new NewOrderLine(f.ProductId, 5), new NewOrderLine(f.ProductId, 2)], Manager);
        Assert.True(ok.Ok, ok.Error);
        var po = (await Purchasing.ListAsync(500)).Single(p => p.Number == ok.Value);
        Assert.Equal(PurchaseOrderStatus.Ordered, po.Status);
        var line = Assert.Single(po.Lines);
        Assert.Equal(7, line.Ordered);
        Assert.Equal(2.0, line.UnitCost);

        // inactive supplier / warehouse / product are refused
        await Master.SetActiveAsync(MasterKind.Product, f.ProductId, false, Manager);
        Assert.False((await Purchasing.CreateManualAsync(f.SupplierId, f.WarehouseId, null, [new NewOrderLine(f.ProductId, 1)], Manager)).Ok);
    }

    [Fact]
    public async Task PurchaseOrder_Edit_Quantities_AndReceiveAfterwards()
    {
        var f = await SeedStockAsync();
        var created = await Purchasing.CreateManualAsync(f.SupplierId, f.WarehouseId, null, [new NewOrderLine(f.ProductId, 10)], Manager);
        var poId = await app.QueryAsync(db => db.PurchaseOrders.Where(p => p.Number == created.Value).Select(p => p.Id).FirstAsync());
        var lineId = (await Purchasing.GetAsync(poId))!.Lines.Single().LineId;

        var edited = await Purchasing.UpdateQuantitiesAsync(poId, new Dictionary<int, int> { [lineId] = 25 }, null, Manager);
        Assert.True(edited.Ok, edited.Error);
        Assert.Equal(25, edited.Value!.Units);
        Assert.False((await Purchasing.UpdateQuantitiesAsync(poId, new Dictionary<int, int> { [lineId] = 0 }, null, Manager)).Ok);

        // partially receive 10, then the quantity cannot drop below 10, and cancelling is refused
        Assert.True((await Purchasing.ReceiveAsync(poId, Manager, [new ReceiptLine(lineId, 10)])).Ok);
        Assert.False((await Purchasing.UpdateQuantitiesAsync(poId, new Dictionary<int, int> { [lineId] = 9 }, null, Manager)).Ok);
        Assert.False((await Purchasing.CancelAsync(poId, Manager)).Ok);
        // editing down to exactly what arrived completes the order
        var done = await Purchasing.UpdateQuantitiesAsync(poId, new Dictionary<int, int> { [lineId] = 10 }, null, Manager);
        Assert.True(done.Ok, done.Error);
        Assert.Equal(PurchaseOrderStatus.Received, done.Value!.Status);
        Assert.False((await Purchasing.UpdateQuantitiesAsync(poId, new Dictionary<int, int> { [lineId] = 12 }, null, Manager)).Ok);
        await AssertLedgerCleanAsync();
    }

    [Fact]
    public async Task PurchaseOrder_Cancel_OnlyBeforeReceipt_AndCancelledCannotBeReceived()
    {
        var f = await SeedStockAsync();
        var created = await Purchasing.CreateManualAsync(f.SupplierId, f.WarehouseId, null, [new NewOrderLine(f.ProductId, 6)], Manager);
        var poId = await app.QueryAsync(db => db.PurchaseOrders.Where(p => p.Number == created.Value).Select(p => p.Id).FirstAsync());
        var movementsBefore = await app.QueryAsync(db => db.Movements.CountAsync());
        var stockBefore = await app.QueryAsync(db => db.StockLevels.SumAsync(s => s.Quantity));

        Assert.True((await Purchasing.CancelAsync(poId, Manager)).Ok);
        Assert.Equal(PurchaseOrderStatus.Cancelled, (await Purchasing.GetAsync(poId))!.Status);
        Assert.False((await Purchasing.CancelAsync(poId, Manager)).Ok);
        Assert.False((await Purchasing.ReceiveAsync(poId, Manager)).Ok);
        Assert.False((await Purchasing.UpdateQuantitiesAsync(poId, new Dictionary<int, int>(), null, Manager)).Ok);
        Assert.Equal(movementsBefore, await app.QueryAsync(db => db.Movements.CountAsync()));
        Assert.Equal(stockBefore, await app.QueryAsync(db => db.StockLevels.SumAsync(s => s.Quantity)));
        // a cancelled PO no longer counts as stock on order
        Assert.DoesNotContain(await Purchasing.GetSuggestionsAsync(), s => s.ProductId == f.ProductId && s.OnOrder > 0);

        // fully received PO cannot be cancelled
        var c2 = await Purchasing.CreateManualAsync(f.SupplierId, f.WarehouseId, null, [new NewOrderLine(f.ProductId, 3)], Manager);
        var po2 = await app.QueryAsync(db => db.PurchaseOrders.Where(p => p.Number == c2.Value).Select(p => p.Id).FirstAsync());
        Assert.True((await Purchasing.ReceiveAsync(po2, Manager)).Ok);
        Assert.False((await Purchasing.CancelAsync(po2, Manager)).Ok);
    }

    // ---------------------------------------------------------------- transfers

    [Fact]
    public async Task Transfer_Reversal_UsesCompensatingMovements_AndKeepsLedgerAppendOnly()
    {
        var f = await SeedStockAsync();
        var tr = await Transfers.TransferAsync(f.ProductId, f.LotA, f.WarehouseId, f.Warehouse2Id, 6, Manager);
        Assert.True(tr.Ok, tr.Error);
        var t = await app.QueryAsync(db => db.Transfers.AsNoTracking().FirstAsync(x => x.Number == tr.Value));
        var originals = await app.QueryAsync(db => db.Movements.AsNoTracking().Where(m => m.Reference == t.Number).OrderBy(m => m.Id).ToListAsync());
        Assert.Equal(2, originals.Count);
        var countBefore = await app.QueryAsync(db => db.Movements.CountAsync());
        Assert.Equal((4, 0), await LevelAsync(f.LevelA));

        var rev = await Transfers.ReverseAsync(t.Id, Manager);
        Assert.True(rev.Ok, rev.Error);
        Assert.Equal(t.Number + "-REV", rev.Value);

        // balances restored
        Assert.Equal((10, 0), await LevelAsync(f.LevelA));
        var dest = await app.QueryAsync(db => db.StockLevels.AsNoTracking().FirstAsync(s => s.WarehouseId == f.Warehouse2Id && s.LotId == f.LotA));
        Assert.Equal(0, dest.Quantity);

        // original ledger rows untouched, exactly two new compensating rows appended
        var originalsAfter = await app.QueryAsync(db => db.Movements.AsNoTracking().Where(m => m.Reference == t.Number).OrderBy(m => m.Id).ToListAsync());
        Assert.Equal(originals.Select(m => (m.Id, m.Quantity, m.WarehouseId, m.Type, m.TimestampUtc)), originalsAfter.Select(m => (m.Id, m.Quantity, m.WarehouseId, m.Type, m.TimestampUtc)));
        Assert.Equal(countBefore + 2, await app.QueryAsync(db => db.Movements.CountAsync()));
        var comp = await app.QueryAsync(db => db.Movements.AsNoTracking().Where(m => m.Reference == rev.Value).OrderBy(m => m.Id).ToListAsync());
        Assert.Equal(2, comp.Count);
        Assert.Contains(comp, m => m.Type == MovementType.TransferOut && m.WarehouseId == f.Warehouse2Id && m.Quantity == -6);
        Assert.Contains(comp, m => m.Type == MovementType.TransferIn && m.WarehouseId == f.WarehouseId && m.Quantity == 6);
        Assert.All(comp, m => Assert.True(m.Id > originals.Max(o => o.Id)));
        Assert.Contains((await Transfers.RecentAsync(500)), r => r.Id == t.Id && r.Reversed);

        // can not reverse twice
        var twice = await Transfers.ReverseAsync(t.Id, Manager);
        Assert.False(twice.Ok);
        Assert.Contains("already", twice.Error);
        await AssertLedgerCleanAsync();
    }

    [Fact]
    public async Task Transfer_Reversal_IsRefused_WhenDestinationStockWasReserved_OrShipped()
    {
        var f = await SeedStockAsync();
        var cust = await AnyCustomerAsync();
        var tr = await Transfers.TransferAsync(f.ProductId, f.LotA, f.WarehouseId, f.Warehouse2Id, 6, Manager);
        var tId = await app.QueryAsync(db => db.Transfers.Where(x => x.Number == tr.Value).Select(x => x.Id).FirstAsync());

        // reserve part of the moved stock at the destination
        var order = await Orders.CreateAsync(new NewOrderRequest(cust, f.Warehouse2Id, [new NewOrderLine(f.ProductId, 4)]), Manager);
        Assert.True(order.Ok, order.Error);
        var refused = await Transfers.ReverseAsync(tId, Manager);
        Assert.False(refused.Ok);
        Assert.Contains("free at the destination", refused.Error);

        // release the reservation: reversal works again
        Assert.True((await Orders.CancelAsync(order.Value!.Id, Manager)).Ok);
        Assert.True((await Transfers.ReverseAsync(tId, Manager)).Ok);

        // a transfer whose stock has shipped can never be reversed
        var tr2 = await Transfers.TransferAsync(f.ProductId, f.LotA, f.WarehouseId, f.Warehouse2Id, 5, Manager);
        var t2Id = await app.QueryAsync(db => db.Transfers.Where(x => x.Number == tr2.Value).Select(x => x.Id).FirstAsync());
        var o2 = (await Orders.CreateAsync(new NewOrderRequest(cust, f.Warehouse2Id, [new NewOrderLine(f.ProductId, 5)]), Manager)).Value!;
        await Orders.AdvanceAsync(o2.Id, Picker); await Orders.AdvanceAsync(o2.Id, Picker);
        Assert.False((await Transfers.ReverseAsync(t2Id, Manager)).Ok);
        await AssertLedgerCleanAsync();
    }

    // ---------------------------------------------------------------- schema + simulator

    [Fact]
    public async Task Seeded_Database_CarriesTheCurrentSchemaVersion()
    {
        var v = await app.QueryAsync(async db =>
        {
            var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version";
            return Convert.ToInt32(await cmd.ExecuteScalarAsync());
        });
        Assert.Equal(DemoSeeder.SchemaVersion, v);
        Assert.True(await app.QueryAsync(db => db.Products.AllAsync(p => p.IsActive)));
    }

    [Fact]
    public async Task Simulator_IgnoresInactiveData_AndUserCreatedOrders()
    {
        var f = await SeedStockAsync();
        var cust = await AnyCustomerAsync();
        var mine = (await Orders.CreateAsync(new NewOrderRequest(cust, f.WarehouseId, [new NewOrderLine(f.ProductId, 3)]), Manager)).Value!;
        var myPo = await Purchasing.CreateManualAsync(f.SupplierId, f.WarehouseId, null, [new NewOrderLine(f.ProductId, 3)], Manager);
        var myPoId = await app.QueryAsync(db => db.PurchaseOrders.Where(p => p.Number == myPo.Value).Select(p => p.Id).FirstAsync());

        // everything inactive: the simulator must not create orders, POs or transfers (and must not throw)
        await app.QueryAsync(async db =>
        {
            await db.Customers.ExecuteUpdateAsync(s => s.SetProperty(c => c.IsActive, false));
            await db.Warehouses.ExecuteUpdateAsync(s => s.SetProperty(w => w.IsActive, false));
            await db.Products.ExecuteUpdateAsync(s => s.SetProperty(p => p.IsActive, false));
            return 0;
        });
        var ordersBefore = await app.QueryAsync(db => db.Orders.CountAsync());
        var posBefore = await app.QueryAsync(db => db.PurchaseOrders.CountAsync());
        var transfersBefore = await app.QueryAsync(db => db.Transfers.CountAsync());
        var worker = app.Services.GetServices<IHostedService>().OfType<SimulationWorker>().Single();
        for (var i = 0; i < 80; i++) await worker.TickAsync();
        Assert.Equal(ordersBefore, await app.QueryAsync(db => db.Orders.CountAsync()));
        Assert.Equal(posBefore, await app.QueryAsync(db => db.PurchaseOrders.CountAsync()));
        Assert.Equal(transfersBefore, await app.QueryAsync(db => db.Transfers.CountAsync()));

        // user-created order and PO are left alone for the user to edit
        Assert.Equal(OrderStatus.Allocated, (await Orders.GetAsync(mine.Id))!.Status);
        Assert.Equal(PurchaseOrderStatus.Ordered, (await Purchasing.GetAsync(myPoId))!.Status);

        // reactivate so the other tests are unaffected
        await app.QueryAsync(async db =>
        {
            await db.Customers.ExecuteUpdateAsync(s => s.SetProperty(c => c.IsActive, true));
            await db.Warehouses.ExecuteUpdateAsync(s => s.SetProperty(w => w.IsActive, true));
            await db.Products.ExecuteUpdateAsync(s => s.SetProperty(p => p.IsActive, true));
            return 0;
        });
        await AssertLedgerCleanAsync();
    }

    [Fact]
    public async Task Simulator_WithUserCreatedMasterData_KeepsWorking()
    {
        var sup = await Master.SaveSupplierAsync(new SupplierInput(0, "Sim Supplier " + Uniq(), "US", 4), Manager);
        await Master.SaveProductAsync(NewProduct(sup.Value), Manager);           // product without any stock
        await Master.SaveWarehouseAsync(new WarehouseInput(0, "S" + Uniq()[..5], "Empty WH", "Town", false), Manager);   // warehouse without stock
        await Master.SaveCustomerAsync(new CustomerInput(0, "Sim Customer " + Uniq(), "Town"), Manager);
        var worker = app.Services.GetServices<IHostedService>().OfType<SimulationWorker>().Single();
        for (var i = 0; i < 60; i++) await worker.TickAsync();
        await AssertLedgerCleanAsync();
    }
}

[Collection("app")]
public class ManagePageAccessTests(MedStockFactory app)
{
    [Theory]
    [InlineData("/manage/products")]
    [InlineData("/manage/warehouses")]
    [InlineData("/manage/suppliers")]
    [InlineData("/manage/customers")]
    public async Task ManagePages_OpenForManagers_AndAreDeniedToOthers(string path)
    {
        foreach (var role in new[] { Roles.Admin, Roles.WarehouseManager })
            Assert.Equal(System.Net.HttpStatusCode.OK, (await (await app.LoginAsync(role)).GetAsync(path)).StatusCode);
        foreach (var role in new[] { Roles.Picker, Roles.Viewer })
        {
            var res = await (await app.LoginAsync(role)).GetAsync(path);
            Assert.Equal(System.Net.HttpStatusCode.Redirect, res.StatusCode);
            Assert.Contains("access-denied", res.Headers.Location?.OriginalString);
        }
        Assert.Equal(System.Net.HttpStatusCode.Redirect, (await app.NewClient().GetAsync(path)).StatusCode);
    }
}
