using MedStock.Domain;
using MedStock.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace MedStock.Web.Services;

public sealed record NewOrderLine(int ProductId, int Quantity);
public sealed record NewOrderRequest(int CustomerId, int WarehouseId, IReadOnlyList<NewOrderLine> Lines);
public sealed record AllocationDetail(string LotNumber, DateOnly Expiry, int Quantity);
public sealed record OrderLineResult(int ProductId, string Sku, string Name, int Ordered, int Allocated, int Shipped, IReadOnlyList<AllocationDetail> Lots);
public sealed record OrderResult(int Id, string Number, OrderStatus Status, string Customer, string Warehouse, DateTime CreatedUtc,
    int TotalOrdered, int TotalAllocated, IReadOnlyList<OrderLineResult> Lines)
{
    public bool HasShortfall => TotalAllocated < TotalOrdered;
}
public sealed record OrderRow(int Id, string Number, OrderStatus Status, string Customer, string Warehouse, DateTime CreatedUtc,
    int LineCount, int TotalOrdered, int TotalAllocated);

public sealed class OrderService(IDbContextFactory<MedStockDb> factory, WriteGate gate, InventoryEventHub hub, TimeProvider clock)
{
    private DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    public Task<OpResult<OrderResult>> CreateAsync(NewOrderRequest req, string actor) => gate.RunAsync(async () =>
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var customer = await db.Customers.FindAsync(req.CustomerId);
            var warehouse = await db.Warehouses.FindAsync(req.WarehouseId);
            if (customer is null) return OpResult<OrderResult>.Fail("Choose a customer.");
            if (warehouse is null) return OpResult<OrderResult>.Fail("Choose a fulfilling warehouse.");
            if (req.Lines.Count == 0) return OpResult<OrderResult>.Fail("Add at least one line.");
            if (req.Lines.Any(l => l.Quantity <= 0)) return OpResult<OrderResult>.Fail("Quantities must be positive.");

            var merged = req.Lines.GroupBy(l => l.ProductId).Select(g => new NewOrderLine(g.Key, g.Sum(x => x.Quantity))).ToList();
            var ids = merged.Select(m => m.ProductId).ToList();
            var products = await db.Products.Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
            if (products.Count != merged.Count) return OpResult<OrderResult>.Fail("One or more products do not exist.");
            var cold = products.Values.FirstOrDefault(p => p.ColdChain);
            if (cold is not null && !warehouse.ColdChainCapable)
                return OpResult<OrderResult>.Fail($"{cold.Name} is cold-chain; {warehouse.Name} cannot handle cold-chain stock.");

            var now = clock.GetUtcNow().UtcDateTime;
            var order = new CustomerOrder
            {
                Number = "PENDING", CustomerId = customer.Id, WarehouseId = warehouse.Id, Status = OrderStatus.Pending,
                CreatedUtc = now, UpdatedUtc = now, CreatedBy = actor,
                Lines = merged.Select(m => new OrderLine { ProductId = m.ProductId, QtyOrdered = m.Quantity }).ToList()
            };
            db.Orders.Add(order);
            await db.SaveChangesAsync();
            order.Number = $"SO-{order.Id + 1000:D6}";

            await AllocateAsync(db, order);
            await db.SaveChangesAsync();

            var result = await LoadResultAsync(db, order.Id);
            var pct = result!.TotalOrdered == 0 ? 0 : 100 * result.TotalAllocated / result.TotalOrdered;
            hub.Publish(ActivityKind.Order,
                $"{order.Number} created for {customer.Name}: {result.Lines.Count} line(s), {pct}% allocated at {warehouse.Code}",
                Topic.Orders | Topic.Stock | Topic.Alerts, result.HasShortfall ? "warning" : "info");
            return OpResult<OrderResult>.Success(result);
        }
        catch (DomainException ex) { return OpResult<OrderResult>.Fail(ex.Message); }
    });

    /// <summary>FEFO-allocates whatever each line is still missing at the order's warehouse.</summary>
    private async Task AllocateAsync(MedStockDb db, CustomerOrder order)
    {
        var today = Today;
        foreach (var line in order.Lines)
        {
            var missing = line.QtyOrdered - line.QtyAllocated;
            if (missing <= 0) continue;
            var levels = await db.StockLevels.Include(s => s.Lot)
                .Where(s => s.WarehouseId == order.WarehouseId && s.ProductId == line.ProductId && s.Quantity - s.Allocated > 0)
                .ToListAsync();
            var res = FefoAllocator.Allocate(levels.Select(s => new LotStock(s.LotId, s.Id, s.Lot!.ExpirationDate, s.Available)), missing, today);
            foreach (var a in res.Allocations)
            {
                var level = levels.First(s => s.Id == a.StockLevelId);
                level.Allocated += a.Quantity;
                line.Allocations.Add(new OrderAllocation { StockLevelId = level.Id, LotId = a.LotId, Quantity = a.Quantity });
            }
            line.QtyAllocated += res.Allocated;
        }
        if (order.Status == OrderStatus.Pending && order.Lines.Any(l => l.QtyAllocated > 0))
            order.Status = OrderStatus.Allocated;
        order.UpdatedUtc = clock.GetUtcNow().UtcDateTime;
    }

    /// <summary>Retries allocation for lines with a shortfall (e.g. after a PO receipt).</summary>
    public Task<OpResult<OrderResult>> ReallocateAsync(int orderId, string actor) => gate.RunAsync(async () =>
    {
        await using var db = await factory.CreateDbContextAsync();
        var order = await db.Orders.Include(o => o.Lines).ThenInclude(l => l.Allocations).FirstOrDefaultAsync(o => o.Id == orderId);
        if (order is null) return OpResult<OrderResult>.Fail("Order not found.");
        if (order.Status is not (OrderStatus.Pending or OrderStatus.Allocated))
            return OpResult<OrderResult>.Fail($"A {order.Status} order cannot be re-allocated.");
        await AllocateAsync(db, order);
        await db.SaveChangesAsync();
        var result = (await LoadResultAsync(db, orderId))!;
        hub.Publish(ActivityKind.Order, $"{order.Number} re-allocated ({result.TotalAllocated}/{result.TotalOrdered} units)", Topic.Orders | Topic.Stock | Topic.Alerts);
        return OpResult<OrderResult>.Success(result);
    });

    /// <summary>Allocated -> Picking -> Shipped. Shipping decrements stock and writes ledger movements.</summary>
    public Task<OpResult<OrderResult>> AdvanceAsync(int orderId, string actor) => gate.RunAsync(async () =>
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var order = await db.Orders.Include(o => o.Customer)
                .Include(o => o.Lines).ThenInclude(l => l.Allocations).ThenInclude(a => a.StockLevel)
                .FirstOrDefaultAsync(o => o.Id == orderId);
            if (order is null) return OpResult<OrderResult>.Fail("Order not found.");
            var next = OrderWorkflow.Next(order.Status);
            if (next is null) return OpResult<OrderResult>.Fail($"A {order.Status} order cannot advance.");
            if (order.Status == OrderStatus.Pending) return OpResult<OrderResult>.Fail("Nothing is allocated yet.");
            OrderWorkflow.EnsureTransition(order.Status, next.Value);

            var now = clock.GetUtcNow().UtcDateTime;
            if (next == OrderStatus.Shipped)
            {
                foreach (var line in order.Lines)
                {
                    foreach (var a in line.Allocations)
                    {
                        var level = a.StockLevel!;
                        level.Quantity = StockLedger.ApplyDelta(level.Quantity, -a.Quantity);
                        level.Allocated = StockLedger.ApplyDelta(level.Allocated, -a.Quantity);
                        db.Movements.Add(new StockMovement
                        {
                            TimestampUtc = now, Type = MovementType.Shipment, ProductId = line.ProductId, LotId = a.LotId,
                            WarehouseId = level.WarehouseId, Quantity = -a.Quantity, Reference = order.Number, Actor = actor
                        });
                    }
                    line.QtyShipped = line.QtyAllocated;
                }
            }
            order.Status = next.Value;
            order.UpdatedUtc = now;
            await db.SaveChangesAsync();

            var result = (await LoadResultAsync(db, orderId))!;
            hub.Publish(next == OrderStatus.Shipped ? ActivityKind.Shipment : ActivityKind.Order,
                next == OrderStatus.Shipped
                    ? $"{order.Number} shipped to {order.Customer!.Name} ({result.TotalAllocated} units)"
                    : $"{order.Number} is being picked",
                Topic.Orders | (next == OrderStatus.Shipped ? Topic.Stock | Topic.Alerts : Topic.None),
                next == OrderStatus.Shipped ? "success" : "info");
            return OpResult<OrderResult>.Success(result);
        }
        catch (DomainException ex) { return OpResult<OrderResult>.Fail(ex.Message); }
    });

    public Task<OpResult> CancelAsync(int orderId, string actor) => gate.RunAsync(async () =>
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var order = await db.Orders
                .Include(o => o.Lines).ThenInclude(l => l.Allocations).ThenInclude(a => a.StockLevel)
                .FirstOrDefaultAsync(o => o.Id == orderId);
            if (order is null) return OpResult.Fail("Order not found.");
            OrderWorkflow.EnsureTransition(order.Status, OrderStatus.Cancelled);
            foreach (var line in order.Lines)
            {
                foreach (var a in line.Allocations)
                    a.StockLevel!.Allocated = StockLedger.ApplyDelta(a.StockLevel.Allocated, -a.Quantity);
                db.OrderAllocations.RemoveRange(line.Allocations);
                line.QtyAllocated = 0;
            }
            order.Status = OrderStatus.Cancelled;
            order.UpdatedUtc = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync();
            hub.Publish(ActivityKind.Order, $"{order.Number} cancelled, reserved stock released", Topic.Orders | Topic.Stock | Topic.Alerts, "warning");
            return OpResult.Success();
        }
        catch (DomainException ex) { return OpResult.Fail(ex.Message); }
    });

    public async Task<OrderResult?> GetAsync(int id)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await LoadResultAsync(db, id);
    }

    public async Task<IReadOnlyList<OrderRow>> ListAsync(OrderStatus? status, bool openOnly = false, int take = 200)
    {
        await using var db = await factory.CreateDbContextAsync();
        var q = db.Orders.AsNoTracking().AsQueryable();
        if (status is not null) q = q.Where(o => o.Status == status);
        else if (openOnly) q = q.Where(o => o.Status == OrderStatus.Pending || o.Status == OrderStatus.Allocated || o.Status == OrderStatus.Picking);
        return await q.OrderByDescending(o => o.CreatedUtc).ThenByDescending(o => o.Id).Take(take)
            .Select(o => new OrderRow(o.Id, o.Number, o.Status, o.Customer!.Name, o.Warehouse!.Code, o.CreatedUtc,
                o.Lines.Count, o.Lines.Sum(l => l.QtyOrdered), o.Lines.Sum(l => l.QtyAllocated)))
            .ToListAsync();
    }

    private static async Task<OrderResult?> LoadResultAsync(MedStockDb db, int id)
    {
        var o = await db.Orders.AsNoTracking().AsSplitQuery()
            .Include(x => x.Customer).Include(x => x.Warehouse)
            .Include(x => x.Lines).ThenInclude(l => l.Product)
            .Include(x => x.Lines).ThenInclude(l => l.Allocations).ThenInclude(a => a.Lot)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (o is null) return null;
        var lines = o.Lines.OrderBy(l => l.Id).Select(l => new OrderLineResult(l.ProductId, l.Product!.Sku, l.Product.Name, l.QtyOrdered, l.QtyAllocated, l.QtyShipped,
            l.Allocations.OrderBy(a => a.Lot!.ExpirationDate).Select(a => new AllocationDetail(a.Lot!.LotNumber, a.Lot.ExpirationDate, a.Quantity)).ToList())).ToList();
        return new OrderResult(o.Id, o.Number, o.Status, o.Customer!.Name, o.Warehouse!.Name, o.CreatedUtc,
            lines.Sum(l => l.Ordered), lines.Sum(l => l.Allocated), lines);
    }
}
