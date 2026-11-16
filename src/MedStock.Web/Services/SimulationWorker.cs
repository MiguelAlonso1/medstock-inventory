using MedStock.Domain;
using MedStock.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace MedStock.Web.Services;

/// <summary>
/// Simulates a busy distribution network: incoming customer orders, pick/ship progress, PO receipts,
/// replenishment, transfers and the occasional cold-chain excursion. Every action goes through the real services,
/// so dashboards update exactly as they would for human activity. Toggle via <see cref="SimulationControl"/>.
/// </summary>
public sealed class SimulationWorker(IDbContextFactory<MedStockDb> factory, OrderService orders, PurchasingService purchasing, TransferService transfers,
    WriteGate gate, InventoryEventHub hub, DemoSeeder seeder, SimulationControl control, IConfiguration cfg, TimeProvider clock, ILogger<SimulationWorker> log) : BackgroundService
{
    private readonly Random _rng = new();
    private int _ticks;

    private async Task<bool> ExceedsCapAsync()
    {
        var cap = cfg.GetValue("Simulation:MaxMovements", 30000);
        await using var db = await factory.CreateDbContextAsync();
        return await db.Movements.CountAsync() > cap;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, cfg.GetValue("Simulation:IntervalSeconds", 10)));
        try { await Task.Delay(TimeSpan.FromSeconds(cfg.GetValue("Simulation:StartDelaySeconds", 5)), ct); } catch (OperationCanceledException) { return; }
        using var timer = new PeriodicTimer(interval);
        while (await WaitAsync(timer, ct))
        {
            if (!control.Active) continue;
            try
            {
                // Shared-host safety: the database may not grow without bound. Past the cap, reseed from scratch.
                if (++_ticks % 50 == 0 && await ExceedsCapAsync())
                {
                    log.LogInformation("Simulation data cap reached; resetting demo data");
                    await seeder.ResetAsync("Simulator (data cap)");
                    continue;
                }
                await TickAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Simulation tick failed"); }
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer t, CancellationToken ct)
    {
        try { return await t.WaitForNextTickAsync(ct); } catch (OperationCanceledException) { return false; }
    }

    public async Task TickAsync()
    {
        var roll = _rng.Next(100);
        // Order creation is balanced by fulfilment so open orders and daily inbound volume stay near the seeded baseline.
        if (roll < 28) await NewOrderAsync();
        else if (roll < 78) await AdvanceOrderAsync();
        else if (roll < 82) await ReceivePoAsync();
        else if (roll < 88) await ReplenishAsync();
        else if (roll < 94) await TransferAsync();
        else if (roll < 97) await ExcursionAsync();
        else await AdvanceOrderAsync();
    }

    private async Task NewOrderAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        var customers = await db.Customers.Where(c => c.IsActive).Select(c => c.Id).ToListAsync();
        var whs = await db.Warehouses.AsNoTracking().Where(w => w.IsActive).ToListAsync();
        if (customers.Count == 0 || whs.Count == 0) return;
        var wh = whs[_rng.Next(whs.Count)];
        var pool = await db.StockLevels.AsNoTracking().Where(s => s.WarehouseId == wh.Id && s.Product!.IsActive && s.Quantity - s.Allocated > 8)
            .OrderBy(s => s.Id).Select(s => new { s.ProductId, A = s.Quantity - s.Allocated }).Take(150).ToListAsync();
        if (pool.Count == 0) return;
        var lines = pool.OrderBy(_ => _rng.Next()).Take(_rng.Next(1, 4))
            .Select(p => new NewOrderLine(p.ProductId, _rng.Next(1, Math.Max(2, Math.Min(30, p.A / 3))))).ToList();
        await orders.CreateAsync(new NewOrderRequest(customers[_rng.Next(customers.Count)], wh.Id, lines), Caller.System("Simulator"));
    }

    private async Task AdvanceOrderAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        var ids = await db.Orders.Where(o => (o.Status == OrderStatus.Allocated || o.Status == OrderStatus.Picking) && (o.CreatedBy == "Simulator" || o.CreatedBy == "Seed"))
            .OrderBy(o => o.CreatedUtc).Take(12).Select(o => o.Id).ToListAsync();
        if (ids.Count == 0) { await NewOrderAsync(); return; }
        await orders.AdvanceAsync(ids[_rng.Next(Math.Min(ids.Count, 6))], Caller.System("Simulator"));
    }

    private async Task ReceivePoAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        var ids = await db.PurchaseOrders.Where(p => (p.Status == PurchaseOrderStatus.Ordered || p.Status == PurchaseOrderStatus.PartiallyReceived) && (p.CreatedBy == "Simulator" || p.CreatedBy == "Seed"))
            .OrderBy(p => p.CreatedUtc).Take(5).Select(p => p.Id).ToListAsync();
        if (ids.Count == 0) { await ReplenishAsync(); return; }
        await purchasing.ReceiveAsync(ids[_rng.Next(ids.Count)], Caller.System("Simulator"));
    }

    private async Task ReplenishAsync()
    {
        var open = await purchasing.ListAsync(50);
        if (open.Count(p => p.Status is PurchaseOrderStatus.Ordered or PurchaseOrderStatus.PartiallyReceived) >= 8) return;
        var s = await purchasing.GetSuggestionsAsync();
        if (s.Count == 0) return;
        var pick = s[_rng.Next(Math.Min(6, s.Count))];
        await using var db = await factory.CreateDbContextAsync();
        var wh = await db.Warehouses.Where(w => w.IsActive && (!pick.ColdChain || w.ColdChainCapable)).OrderBy(w => w.Id).ToListAsync();
        if (wh.Count == 0) return;
        await purchasing.CreatePurchaseOrdersAsync([new NewOrderLine(pick.ProductId, pick.SuggestedQty)], wh[_rng.Next(wh.Count)].Id, Caller.System("Simulator"));
    }

    private async Task TransferAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        var whs = await db.Warehouses.AsNoTracking().Where(w => w.IsActive).ToListAsync();
        if (whs.Count < 2) return;
        var from = whs[_rng.Next(whs.Count)];
        var levels = await db.StockLevels.AsNoTracking().Where(s => s.WarehouseId == from.Id && s.Product!.IsActive && s.Quantity - s.Allocated > 60)
            .OrderBy(s => s.Id).Select(s => new { s.ProductId, s.LotId, A = s.Quantity - s.Allocated, s.Product!.ColdChain, s.Lot!.ExpirationDate }).Take(60).ToListAsync();
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var cand = levels.Where(l => l.ExpirationDate >= today).OrderBy(_ => _rng.Next()).FirstOrDefault();
        if (cand is null) return;
        var targets = whs.Where(w => w.Id != from.Id && (!cand.ColdChain || w.ColdChainCapable)).ToList();
        if (targets.Count == 0) return;
        await transfers.TransferAsync(cand.ProductId, cand.LotId, from.Id, targets[_rng.Next(targets.Count)].Id, _rng.Next(10, Math.Min(60, cand.A / 2) + 1), Caller.System("Simulator"));
    }

    private async Task ExcursionAsync()
    {
        await gate.RunAsync(async () =>
        {
            await using var db = await factory.CreateDbContextAsync();
            var cold = await db.Warehouses.Where(w => w.IsActive && w.ColdChainCapable).ToListAsync();
            if (cold.Count == 0) return 0;
            if (await db.Excursions.CountAsync(e => e.AcknowledgedUtc == null) >= 3) return 0;
            var wh = cold[_rng.Next(cold.Count)];
            var zone = _rng.Next(2) == 0 ? $"Cold Room {_rng.Next(1, 4)}" : $"Vaccine Freezer {_rng.Next(1, 3)}";
            var reading = Math.Round(_rng.Next(2) == 0 ? 8.5 + _rng.NextDouble() * 4 : -12 - _rng.NextDouble() * 4, 1);
            db.Excursions.Add(new TempExcursion { WarehouseId = wh.Id, Zone = zone, ReadingC = reading, RecordedUtc = clock.GetUtcNow().UtcDateTime });
            await db.SaveChangesAsync();
            hub.Publish(ActivityKind.Alert, $"Cold-chain excursion at {wh.Code} ({zone}): {reading:0.0} C outside range", Topic.Alerts, "warning");
            return 0;
        });
    }
}
