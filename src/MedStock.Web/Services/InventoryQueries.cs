using MedStock.Domain;
using MedStock.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace MedStock.Web.Services;

public enum InventoryFilter { All, LowStock, Expiring, ColdChain }

public sealed record InventoryQuery(string? Search, int? WarehouseId, string? Category, InventoryFilter Filter, string? SortBy, bool Descending, int Page, int PageSize);
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total);
public sealed record InventoryRow(int Id, string Sku, string Name, string Ndc, string Category, bool ColdChain, int OnHand, int Allocated,
    int Min, int Max, double UnitCost, DateOnly? NextExpiry, bool Low)
{
    public int Available => OnHand - Allocated;
    public double Value => OnHand * UnitCost;
}

public sealed record Kpis(double InventoryValue, long TotalUnits, int SkuCount, int LowStockSkus, int Expiring30, int Expiring60, int Expiring90,
    int ExpiredLots, int OpenPurchaseOrders, int OpenOrders, double FillRatePct, int OpenExcursions);
public sealed record LabelValue(string Label, double Value);
public sealed record DailyMovement(DateOnly Day, int Inbound, int Outbound);

public sealed record LocationStock(int WarehouseId, string Code, string Name, int OnHand, int Allocated);
public sealed record LotRow(int LotId, string LotNumber, DateOnly Expiry, ExpiryClass Class, int DaysLeft, string Warehouse, int OnHand, int Allocated);
public sealed record MovementRow(long Id, DateTime Utc, MovementType Type, int Quantity, string Warehouse, string LotNumber, string Reference, string Actor);
public sealed record ProductDetail(Product Product, string Supplier, int OnHand, int Allocated, int OnOrder,
    IReadOnlyList<LocationStock> Locations, IReadOnlyList<LotRow> Lots, IReadOnlyList<MovementRow> Movements);

public sealed record LowStockAlert(int ProductId, string Sku, string Name, bool ColdChain, int Available, int Min, int Max, int OnOrder);
public sealed record ExpiryAlert(int ProductId, string Sku, string Name, string LotNumber, DateOnly Expiry, int DaysLeft, ExpiryClass Class, string Warehouse, int Quantity, bool ColdChain);
public sealed record ExcursionRow(int Id, string Warehouse, string Zone, double ReadingC, DateTime RecordedUtc, string? AcknowledgedBy, DateTime? AcknowledgedUtc);
public sealed record AlertSummary(int LowStock, int Expiring, int Expired, int ColdChain);

public sealed record Lookups(IReadOnlyList<Warehouse> Warehouses, IReadOnlyList<Customer> Customers, IReadOnlyList<string> Categories, IReadOnlyList<Supplier> Suppliers);
public sealed record ProductOption(int Id, string Sku, string Name, bool ColdChain)
{
    public override string ToString() => $"{Name} ({Sku})";
}

public sealed class InventoryQueries(IDbContextFactory<MedStockDb> factory, WriteGate gate, InventoryEventHub hub, TimeProvider clock)
{
    private DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    public async Task<Lookups> GetLookupsAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        return new Lookups(
            await db.Warehouses.AsNoTracking().Where(w => w.IsActive).OrderBy(w => w.Code).ToListAsync(),
            await db.Customers.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Name).ToListAsync(),
            await db.Products.Select(p => p.Category).Distinct().OrderBy(c => c).ToListAsync(),
            await db.Suppliers.AsNoTracking().Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync());
    }

    public async Task<IReadOnlyList<ProductOption>> SearchProductsAsync(string? term, int take = 25, int? supplierId = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        var q = db.Products.AsNoTracking().Where(p => p.IsActive);
        if (supplierId is { } sid) q = q.Where(p => p.SupplierId == sid);
        if (!string.IsNullOrWhiteSpace(term)) q = q.Where(p => p.Name.Contains(term) || p.Sku.Contains(term));
        return await q.OrderBy(p => p.Name).Take(take).Select(p => new ProductOption(p.Id, p.Sku, p.Name, p.ColdChain)).ToListAsync();
    }

    public async Task<Kpis> GetKpisAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        var today = Today;
        var value = await db.StockLevels.SumAsync(s => (double?)(s.Quantity * s.Product!.UnitCost)) ?? 0;
        var units = await db.StockLevels.SumAsync(s => (long?)s.Quantity) ?? 0;
        var skus = await db.Products.CountAsync(p => p.IsActive);

        var avail = await db.StockLevels.GroupBy(s => s.ProductId).Select(g => new { g.Key, A = g.Sum(s => s.Quantity - s.Allocated) }).ToDictionaryAsync(x => x.Key, x => x.A);
        var mins = await db.Products.Where(p => p.IsActive).Select(p => new { p.Id, p.MinLevel }).ToListAsync();
        var low = mins.Count(m => avail.GetValueOrDefault(m.Id) <= m.MinLevel);

        async Task<int> Within(int days)
        {
            var to = today.AddDays(days);
            return await db.StockLevels.Where(s => s.Quantity > 0 && s.Lot!.ExpirationDate >= today && s.Lot.ExpirationDate <= to).Select(s => s.LotId).Distinct().CountAsync();
        }
        var e30 = await Within(30); var e60 = await Within(60); var e90 = await Within(90);
        var expired = await db.StockLevels.Where(s => s.Quantity > 0 && s.Lot!.ExpirationDate < today).Select(s => s.LotId).Distinct().CountAsync();
        var openPo = await db.PurchaseOrders.CountAsync(p => p.Status == PurchaseOrderStatus.Ordered || p.Status == PurchaseOrderStatus.PartiallyReceived);
        var openOrders = await db.Orders.CountAsync(o => o.Status == OrderStatus.Pending || o.Status == OrderStatus.Allocated || o.Status == OrderStatus.Picking);

        var since = clock.GetUtcNow().UtcDateTime.AddDays(-30);
        var ordered = await db.OrderLines.Where(l => l.Order!.CreatedUtc >= since && l.Order.Status != OrderStatus.Cancelled).SumAsync(l => (long?)l.QtyOrdered) ?? 0;
        var allocated = await db.OrderLines.Where(l => l.Order!.CreatedUtc >= since && l.Order.Status != OrderStatus.Cancelled).SumAsync(l => (long?)l.QtyAllocated) ?? 0;
        var fill = ordered == 0 ? 100 : 100.0 * allocated / ordered;
        var exc = await db.Excursions.CountAsync(x => x.AcknowledgedUtc == null);
        return new Kpis(value, units, skus, low, e30, e60, e90, expired, openPo, openOrders, fill, exc);
    }

    public async Task<IReadOnlyList<LabelValue>> ValueByWarehouseAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        var rows = await db.StockLevels.GroupBy(s => s.Warehouse!.Code).Select(g => new { g.Key, V = g.Sum(s => s.Quantity * s.Product!.UnitCost) }).ToListAsync();
        return rows.OrderBy(r => r.Key).Select(r => new LabelValue(r.Key, Math.Round(r.V, 0))).ToList();
    }

    public async Task<IReadOnlyList<DailyMovement>> MovementsPerDayAsync(int days = 14)
    {
        await using var db = await factory.CreateDbContextAsync();
        var start = clock.GetUtcNow().UtcDateTime.Date.AddDays(-(days - 1));
        var rows = await db.Movements.AsNoTracking().Where(m => m.TimestampUtc >= start && (m.Type == MovementType.Receipt || m.Type == MovementType.Shipment))
            .Select(m => new { m.TimestampUtc, m.Quantity }).ToListAsync();
        var today = Today;
        return Enumerable.Range(0, days).Select(i =>
        {
            var d = today.AddDays(i - (days - 1));
            var day = rows.Where(r => DateOnly.FromDateTime(r.TimestampUtc) == d).ToList();
            return new DailyMovement(d, day.Where(r => r.Quantity > 0).Sum(r => r.Quantity), -day.Where(r => r.Quantity < 0).Sum(r => r.Quantity));
        }).ToList();
    }

    public async Task<IReadOnlyList<LabelValue>> TopProductsAsync(int take = 6)
    {
        await using var db = await factory.CreateDbContextAsync();
        var since = clock.GetUtcNow().UtcDateTime.AddDays(-30);
        var top = await db.Movements.Where(m => m.Type == MovementType.Shipment && m.TimestampUtc >= since)
            .GroupBy(m => m.ProductId).Select(g => new { g.Key, Units = -g.Sum(m => m.Quantity) })
            .OrderByDescending(x => x.Units).Take(take).ToListAsync();
        var ids = top.Select(t => t.Key).ToList();
        var names = await db.Products.Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name);
        return top.Select(t => new LabelValue(names[t.Key], t.Units)).ToList();
    }

    private sealed class Row
    {
        public Product P { get; set; } = null!;
        public int OnHand { get; set; }
        public int Allocated { get; set; }
        public int NetAvail { get; set; }
        public DateOnly? NextExpiry { get; set; }
    }

    public async Task<PagedResult<InventoryRow>> GetInventoryAsync(InventoryQuery qy)
    {
        await using var db = await factory.CreateDbContextAsync();
        var soon = Today.AddDays(90);
        var wh = qy.WarehouseId;
        var q = db.Products.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(qy.Search))
        {
            var s = qy.Search.Trim();
            q = q.Where(p => p.Name.Contains(s) || p.Sku.Contains(s) || p.NdcCode.Contains(s));
        }
        if (!string.IsNullOrEmpty(qy.Category)) q = q.Where(p => p.Category == qy.Category);
        if (qy.Filter == InventoryFilter.ColdChain) q = q.Where(p => p.ColdChain);

        var rows = q.Select(p => new Row
        {
            P = p,
            OnHand = db.StockLevels.Where(s => s.ProductId == p.Id && (wh == null || s.WarehouseId == wh)).Sum(s => (int?)s.Quantity) ?? 0,
            Allocated = db.StockLevels.Where(s => s.ProductId == p.Id && (wh == null || s.WarehouseId == wh)).Sum(s => (int?)s.Allocated) ?? 0,
            NetAvail = db.StockLevels.Where(s => s.ProductId == p.Id).Sum(s => (int?)(s.Quantity - s.Allocated)) ?? 0,
            NextExpiry = db.StockLevels.Where(s => s.ProductId == p.Id && s.Quantity > 0 && (wh == null || s.WarehouseId == wh)).Min(s => (DateOnly?)s.Lot!.ExpirationDate)
        });
        if (qy.Filter == InventoryFilter.LowStock) rows = rows.Where(r => r.NetAvail <= r.P.MinLevel);
        if (qy.Filter == InventoryFilter.Expiring) rows = rows.Where(r => r.NextExpiry != null && r.NextExpiry <= soon);

        var total = await rows.CountAsync();
        var d = qy.Descending;
        rows = (qy.SortBy?.ToLowerInvariant()) switch
        {
            "sku" => d ? rows.OrderByDescending(r => r.P.Sku) : rows.OrderBy(r => r.P.Sku),
            "category" => d ? rows.OrderByDescending(r => r.P.Category) : rows.OrderBy(r => r.P.Category),
            "onhand" => d ? rows.OrderByDescending(r => r.OnHand) : rows.OrderBy(r => r.OnHand),
            "cost" => d ? rows.OrderByDescending(r => r.P.UnitCost) : rows.OrderBy(r => r.P.UnitCost),
            "value" => d ? rows.OrderByDescending(r => r.OnHand * r.P.UnitCost) : rows.OrderBy(r => r.OnHand * r.P.UnitCost),
            "expiry" => d ? rows.OrderByDescending(r => r.NextExpiry) : rows.OrderBy(r => r.NextExpiry == null).ThenBy(r => r.NextExpiry),
            _ => d ? rows.OrderByDescending(r => r.P.Name) : rows.OrderBy(r => r.P.Name)
        };
        var page = await rows.Skip(qy.Page * qy.PageSize).Take(qy.PageSize).ToListAsync();
        return new PagedResult<InventoryRow>(
            page.Select(r => new InventoryRow(r.P.Id, r.P.Sku, r.P.Name, r.P.NdcCode, r.P.Category, r.P.ColdChain, r.OnHand, r.Allocated,
                r.P.MinLevel, r.P.MaxLevel, r.P.UnitCost, r.NextExpiry, r.NetAvail <= r.P.MinLevel)).ToList(), total);
    }

    public async Task<IReadOnlyList<LotRow>> GetLotsAsync(int productId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var today = Today;
        var list = await db.StockLevels.AsNoTracking().Where(s => s.ProductId == productId && (s.Quantity > 0 || s.Allocated > 0))
            .OrderBy(s => s.Lot!.ExpirationDate).ThenBy(s => s.Warehouse!.Code)
            .Select(s => new { s.LotId, s.Lot!.LotNumber, s.Lot.ExpirationDate, W = s.Warehouse!.Code, s.Quantity, s.Allocated }).ToListAsync();
        return list.Select(s => new LotRow(s.LotId, s.LotNumber, s.ExpirationDate, ExpiryClassifier.Classify(s.ExpirationDate, today),
            ExpiryClassifier.DaysToExpiry(s.ExpirationDate, today), s.W, s.Quantity, s.Allocated)).ToList();
    }

    public async Task<IReadOnlyDictionary<int, IReadOnlyList<LotRow>>> GetLotsAsync(IReadOnlyCollection<int> productIds)
    {
        await using var db = await factory.CreateDbContextAsync();
        var today = Today;
        var list = await db.StockLevels.AsNoTracking().Where(s => productIds.Contains(s.ProductId) && (s.Quantity > 0 || s.Allocated > 0))
            .OrderBy(s => s.Lot!.ExpirationDate).ThenBy(s => s.Warehouse!.Code)
            .Select(s => new { s.ProductId, s.LotId, s.Lot!.LotNumber, s.Lot.ExpirationDate, W = s.Warehouse!.Code, s.Quantity, s.Allocated }).ToListAsync();
        return list.GroupBy(s => s.ProductId).ToDictionary(g => g.Key, g => (IReadOnlyList<LotRow>)g.Select(s => new LotRow(s.LotId, s.LotNumber, s.ExpirationDate,
            ExpiryClassifier.Classify(s.ExpirationDate, today), ExpiryClassifier.DaysToExpiry(s.ExpirationDate, today), s.W, s.Quantity, s.Allocated)).ToList());
    }

    public async Task<ProductDetail?> GetProductAsync(int id)
    {
        await using var db = await factory.CreateDbContextAsync();
        var p = await db.Products.AsNoTracking().Include(x => x.Supplier).FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return null;
        var locs = await db.Warehouses.AsNoTracking().OrderBy(w => w.Code).Select(w => new LocationStock(w.Id, w.Code, w.Name,
            db.StockLevels.Where(s => s.ProductId == id && s.WarehouseId == w.Id).Sum(s => (int?)s.Quantity) ?? 0,
            db.StockLevels.Where(s => s.ProductId == id && s.WarehouseId == w.Id).Sum(s => (int?)s.Allocated) ?? 0)).ToListAsync();
        var onOrder = await db.PurchaseOrderLines.Where(l => l.ProductId == id && (l.PurchaseOrder!.Status == PurchaseOrderStatus.Ordered || l.PurchaseOrder.Status == PurchaseOrderStatus.PartiallyReceived))
            .SumAsync(l => (int?)(l.QtyOrdered - l.QtyReceived)) ?? 0;
        var moves = await db.Movements.AsNoTracking().Where(m => m.ProductId == id).OrderByDescending(m => m.TimestampUtc).ThenByDescending(m => m.Id).Take(40)
            .Select(m => new MovementRow(m.Id, m.TimestampUtc, m.Type, m.Quantity, m.Warehouse!.Code, m.Lot!.LotNumber, m.Reference, m.Actor)).ToListAsync();
        return new ProductDetail(p, p.Supplier!.Name, locs.Sum(l => l.OnHand), locs.Sum(l => l.Allocated), onOrder, locs, await GetLotsAsync(id), moves);
    }

    public async Task<IReadOnlyList<LowStockAlert>> GetLowStockAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        var avail = await db.StockLevels.GroupBy(s => s.ProductId).Select(g => new { g.Key, A = g.Sum(s => s.Quantity - s.Allocated) }).ToDictionaryAsync(x => x.Key, x => x.A);
        var onOrder = await db.PurchaseOrderLines.Where(l => l.PurchaseOrder!.Status == PurchaseOrderStatus.Ordered || l.PurchaseOrder.Status == PurchaseOrderStatus.PartiallyReceived)
            .GroupBy(l => l.ProductId).Select(g => new { g.Key, Q = g.Sum(l => l.QtyOrdered - l.QtyReceived) }).ToDictionaryAsync(x => x.Key, x => x.Q);
        var products = await db.Products.AsNoTracking().Where(p => p.IsActive).ToListAsync();
        return products.Where(p => avail.GetValueOrDefault(p.Id) <= p.MinLevel)
            .Select(p => new LowStockAlert(p.Id, p.Sku, p.Name, p.ColdChain, avail.GetValueOrDefault(p.Id), p.MinLevel, p.MaxLevel, onOrder.GetValueOrDefault(p.Id)))
            .OrderBy(a => a.Available / (double)Math.Max(1, a.Min)).Take(300).ToList();
    }

    public async Task<IReadOnlyList<ExpiryAlert>> GetExpiryAlertsAsync(bool coldChainOnly = false)
    {
        await using var db = await factory.CreateDbContextAsync();
        var today = Today;
        var limit = today.AddDays(90);
        var q = db.StockLevels.AsNoTracking().Where(s => s.Quantity > 0 && s.Lot!.ExpirationDate <= limit);
        if (coldChainOnly) q = q.Where(s => s.Product!.ColdChain);
        var list = await q.OrderBy(s => s.Lot!.ExpirationDate).Take(400)
            .Select(s => new { s.ProductId, s.Product!.Sku, s.Product.Name, s.Product.ColdChain, s.Lot!.LotNumber, s.Lot.ExpirationDate, W = s.Warehouse!.Code, s.Quantity }).ToListAsync();
        return list.Select(s => new ExpiryAlert(s.ProductId, s.Sku, s.Name, s.LotNumber, s.ExpirationDate, ExpiryClassifier.DaysToExpiry(s.ExpirationDate, today),
            ExpiryClassifier.Classify(s.ExpirationDate, today), s.W, s.Quantity, s.ColdChain)).ToList();
    }

    public async Task<IReadOnlyList<ExcursionRow>> GetExcursionsAsync(int take = 30)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Excursions.AsNoTracking().OrderByDescending(e => e.RecordedUtc).Take(take)
            .Select(e => new ExcursionRow(e.Id, e.Warehouse!.Code, e.Zone, e.ReadingC, e.RecordedUtc, e.AcknowledgedBy, e.AcknowledgedUtc)).ToListAsync();
    }

    public Task<OpResult> AcknowledgeExcursionAsync(int id, string actor) => gate.RunAsync(async () =>
    {
        await using var db = await factory.CreateDbContextAsync();
        var e = await db.Excursions.Include(x => x.Warehouse).FirstOrDefaultAsync(x => x.Id == id);
        if (e is null) return OpResult.Fail("Excursion not found.");
        if (e.AcknowledgedUtc is not null) return OpResult.Fail("Already acknowledged.");
        e.AcknowledgedBy = actor;
        e.AcknowledgedUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync();
        hub.Publish(ActivityKind.Alert, $"Cold-chain excursion at {e.Warehouse!.Code} ({e.Zone}) acknowledged by {actor}", Topic.Alerts, "info");
        return OpResult.Success();
    });

    public async Task<AlertSummary> GetAlertSummaryAsync()
    {
        var k = await GetKpisAsync();
        return new AlertSummary(k.LowStockSkus, k.Expiring30 + (k.Expiring60 - k.Expiring30) + (k.Expiring90 - k.Expiring60), k.ExpiredLots, k.OpenExcursions);
    }

    /// <summary>Verifies the movement history replays without ever driving a (warehouse, lot) balance negative and matches current stock.</summary>
    public async Task<(int Movements, int Violations, int Mismatches)> VerifyLedgerAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        var entries = await db.Movements.AsNoTracking().OrderBy(m => m.Id)
            .Select(m => new LedgerEntry(m.Id, m.Type, m.ProductId, m.LotId, m.WarehouseId, m.Quantity)).ToListAsync();
        var (ledger, violations) = StockLedger.Verify(entries);
        var levels = await db.StockLevels.AsNoTracking().Select(s => new { s.WarehouseId, s.LotId, s.Quantity }).ToListAsync();
        var mismatches = levels.Count(l => ledger.Balance(l.WarehouseId, l.LotId) != l.Quantity);
        return (entries.Count, violations.Count, mismatches);
    }
}
