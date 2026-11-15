using MedStock.Domain;
using MedStock.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace MedStock.Web.Services;

public sealed record ReorderSuggestion(int ProductId, string Sku, string Name, bool ColdChain, int SupplierId, string Supplier,
    int OnHand, int Allocated, int OnOrder, int Min, int Max, int SuggestedQty, double UnitCost)
{
    public double Cost => SuggestedQty * UnitCost;
}

public sealed record PoLineView(int LineId, string Sku, string Name, int Ordered, int Received, double UnitCost);
public sealed record PurchaseOrderView(int Id, string Number, string Supplier, string Warehouse, PurchaseOrderStatus Status,
    DateTime CreatedUtc, DateOnly Expected, int Units, double Value, IReadOnlyList<PoLineView> Lines);

public sealed class PurchasingService(IDbContextFactory<MedStockDb> factory, WriteGate gate, InventoryEventHub hub, TimeProvider clock)
{
    private DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    public async Task<IReadOnlyList<ReorderSuggestion>> GetSuggestionsAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        var stock = await db.StockLevels.GroupBy(s => s.ProductId)
            .Select(g => new { g.Key, OnHand = g.Sum(s => s.Quantity), Alloc = g.Sum(s => s.Allocated) }).ToDictionaryAsync(x => x.Key);
        var onOrder = await db.PurchaseOrderLines
            .Where(l => l.PurchaseOrder!.Status == PurchaseOrderStatus.Ordered || l.PurchaseOrder.Status == PurchaseOrderStatus.PartiallyReceived)
            .GroupBy(l => l.ProductId).Select(g => new { g.Key, Qty = g.Sum(l => l.QtyOrdered - l.QtyReceived) }).ToDictionaryAsync(x => x.Key, x => x.Qty);
        var products = await db.Products.AsNoTracking().Include(p => p.Supplier).ToListAsync();

        var list = new List<ReorderSuggestion>();
        foreach (var p in products)
        {
            var onHand = stock.TryGetValue(p.Id, out var s) ? s.OnHand : 0;
            var alloc = s?.Alloc ?? 0;
            var oo = onOrder.GetValueOrDefault(p.Id);
            var qty = ReorderCalculator.SuggestedQuantity(onHand, alloc, oo, p.MinLevel, p.MaxLevel, p.PackSize);
            if (qty > 0)
                list.Add(new ReorderSuggestion(p.Id, p.Sku, p.Name, p.ColdChain, p.SupplierId, p.Supplier!.Name, onHand, alloc, oo, p.MinLevel, p.MaxLevel, qty, p.UnitCost));
        }
        return list.OrderBy(x => ReorderCalculator.Position(x.OnHand, x.Allocated, x.OnOrder) / (double)Math.Max(1, x.Min)).ThenBy(x => x.Name).ToList();
    }

    /// <summary>Creates one PO per supplier for the chosen lines, delivering to the given warehouse.</summary>
    public Task<OpResult<IReadOnlyList<string>>> CreatePurchaseOrdersAsync(IReadOnlyList<NewOrderLine> lines, int warehouseId, string actor) => gate.RunAsync(async () =>
    {
        await using var db = await factory.CreateDbContextAsync();
        var wh = await db.Warehouses.FindAsync(warehouseId);
        if (wh is null) return OpResult<IReadOnlyList<string>>.Fail("Choose a receiving warehouse.");
        if (lines.Count == 0 || lines.Any(l => l.Quantity <= 0)) return OpResult<IReadOnlyList<string>>.Fail("Select at least one line with a positive quantity.");
        var ids = lines.Select(l => l.ProductId).Distinct().ToList();
        var products = await db.Products.Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
        if (products.Count != ids.Count) return OpResult<IReadOnlyList<string>>.Fail("Unknown product in the selection.");
        var cold = products.Values.FirstOrDefault(p => p.ColdChain);
        if (cold is not null && !wh.ColdChainCapable)
            return OpResult<IReadOnlyList<string>>.Fail($"{cold.Name} is cold-chain; {wh.Name} cannot receive it.");

        var suppliers = await db.Suppliers.ToDictionaryAsync(s => s.Id);
        var now = clock.GetUtcNow().UtcDateTime;
        var created = new List<PurchaseOrder>();
        foreach (var grp in lines.GroupBy(l => products[l.ProductId].SupplierId))
        {
            var sup = suppliers[grp.Key];
            var po = new PurchaseOrder
            {
                Number = "PENDING", SupplierId = sup.Id, WarehouseId = wh.Id, Status = PurchaseOrderStatus.Ordered, CreatedUtc = now,
                ExpectedDate = Today.AddDays(sup.LeadTimeDays), CreatedBy = actor,
                Lines = grp.GroupBy(l => l.ProductId).Select(g => new PurchaseOrderLine
                {
                    ProductId = g.Key, QtyOrdered = g.Sum(x => x.Quantity), UnitCost = products[g.Key].UnitCost
                }).ToList()
            };
            db.PurchaseOrders.Add(po);
            created.Add(po);
        }
        await db.SaveChangesAsync();
        foreach (var po in created) po.Number = $"PO-{po.Id + 5000:D5}";
        await db.SaveChangesAsync();
        foreach (var po in created)
            hub.Publish(ActivityKind.Receipt, $"{po.Number} issued to {suppliers[po.SupplierId].Name} ({po.Lines.Sum(l => l.QtyOrdered)} units, due {po.ExpectedDate:MMM d})",
                Topic.Purchasing | Topic.Alerts);
        return OpResult<IReadOnlyList<string>>.Success(created.Select(p => p.Number).ToList());
    });

    public Task<OpResult<PurchaseOrderView>> ReceiveAsync(int poId, string actor, IReadOnlyList<ReceiptLine>? receipt = null) => gate.RunAsync(async () =>
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var po = await db.PurchaseOrders.Include(p => p.Warehouse).Include(p => p.Supplier)
                .Include(p => p.Lines).ThenInclude(l => l.Product)
                .FirstOrDefaultAsync(p => p.Id == poId);
            if (po is null) return OpResult<PurchaseOrderView>.Fail("Purchase order not found.");

            var domainLines = po.Lines.Select(l => new PoLine(l.Id, l.QtyOrdered, l.QtyReceived)).ToList();
            receipt ??= PurchaseOrderReceiving.ReceiveAllRemaining(domainLines);
            var outcome = PurchaseOrderReceiving.Receive(po.Status, domainLines, receipt);

            var now = clock.GetUtcNow().UtcDateTime;
            var today = Today;
            var delta = receipt.GroupBy(r => r.LineId).ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));
            var units = 0;
            foreach (var line in po.Lines.Where(l => delta.ContainsKey(l.Id)))
            {
                var qty = delta[line.Id];
                var p = line.Product!;
                // Each receipt is a new lot with the product's standard shelf life.
                var lotNumber = $"{po.Number[3..]}-{p.Id:D3}-{line.QtyReceived / Math.Max(1, qty) + 1}";
                lotNumber = await UniqueLotNumberAsync(db, p.Id, lotNumber);
                var lot = new Lot { ProductId = p.Id, LotNumber = lotNumber, ExpirationDate = today.AddMonths(p.ShelfLifeMonths) };
                db.Lots.Add(lot);
                var level = new StockLevel { Warehouse = po.Warehouse, ProductId = p.Id, Lot = lot, Quantity = qty };
                db.StockLevels.Add(level);
                db.Movements.Add(new StockMovement
                {
                    TimestampUtc = now, Type = MovementType.Receipt, ProductId = p.Id, Lot = lot, WarehouseId = po.WarehouseId,
                    Quantity = qty, Reference = po.Number, Actor = actor
                });
                line.QtyReceived += qty;
                units += qty;
            }
            po.Status = outcome.Status;
            if (outcome.Status == PurchaseOrderStatus.Received) po.ReceivedUtc = now;
            await db.SaveChangesAsync();

            hub.Publish(ActivityKind.Receipt,
                $"{po.Number} {(outcome.Status == PurchaseOrderStatus.Received ? "received in full" : "partially received")} at {po.Warehouse!.Code}: +{units} units from {po.Supplier!.Name}",
                Topic.Purchasing | Topic.Stock | Topic.Alerts, "success");
            return OpResult<PurchaseOrderView>.Success(ToView(po));
        }
        catch (DomainException ex) { return OpResult<PurchaseOrderView>.Fail(ex.Message); }
    });

    private static async Task<string> UniqueLotNumberAsync(MedStockDb db, int productId, string candidate)
    {
        var n = candidate;
        var i = 1;
        while (await db.Lots.AnyAsync(l => l.ProductId == productId && l.LotNumber == n) || db.Lots.Local.Any(l => l.ProductId == productId && l.LotNumber == n))
            n = $"{candidate}{(char)('A' + i++ % 26)}";
        return n;
    }

    public async Task<IReadOnlyList<PurchaseOrderView>> ListAsync(int take = 100)
    {
        await using var db = await factory.CreateDbContextAsync();
        var list = await db.PurchaseOrders.AsNoTracking().AsSplitQuery()
            .Include(p => p.Supplier).Include(p => p.Warehouse).Include(p => p.Lines).ThenInclude(l => l.Product)
            .OrderBy(p => p.Status == PurchaseOrderStatus.Received || p.Status == PurchaseOrderStatus.Cancelled ? 1 : 0)
            .ThenByDescending(p => p.CreatedUtc).ThenByDescending(p => p.Id).Take(take).ToListAsync();
        return list.Select(ToView).ToList();
    }

    public async Task<PurchaseOrderView?> GetAsync(int id)
    {
        await using var db = await factory.CreateDbContextAsync();
        var po = await db.PurchaseOrders.AsNoTracking().Include(p => p.Supplier).Include(p => p.Warehouse)
            .Include(p => p.Lines).ThenInclude(l => l.Product).FirstOrDefaultAsync(p => p.Id == id);
        return po is null ? null : ToView(po);
    }

    private static PurchaseOrderView ToView(PurchaseOrder p) => new(p.Id, p.Number, p.Supplier!.Name, p.Warehouse!.Code, p.Status, p.CreatedUtc, p.ExpectedDate,
        p.Lines.Sum(l => l.QtyOrdered), p.Lines.Sum(l => l.QtyOrdered * l.UnitCost),
        p.Lines.OrderBy(l => l.Id).Select(l => new PoLineView(l.Id, l.Product!.Sku, l.Product.Name, l.QtyOrdered, l.QtyReceived, l.UnitCost)).ToList());
}
