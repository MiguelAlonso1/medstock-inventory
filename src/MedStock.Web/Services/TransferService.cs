using MedStock.Domain;
using MedStock.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace MedStock.Web.Services;

public sealed record TransferRow(int Id, string Number, string Sku, string Product, string LotNumber, string From, string To, int Quantity, DateTime CreatedUtc, string CreatedBy, bool Reversed);
public sealed record TransferableLot(int LotId, string LotNumber, DateOnly Expiry, int Available);

public sealed class TransferService(IDbContextFactory<MedStockDb> factory, WriteGate gate, InventoryEventHub hub, TimeProvider clock)
{
    public async Task<IReadOnlyList<TransferableLot>> GetLotsAsync(int productId, int warehouseId)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.StockLevels.AsNoTracking()
            .Where(s => s.ProductId == productId && s.WarehouseId == warehouseId && s.Quantity - s.Allocated > 0)
            .OrderBy(s => s.Lot!.ExpirationDate)
            .Select(s => new TransferableLot(s.LotId, s.Lot!.LotNumber, s.Lot.ExpirationDate, s.Quantity - s.Allocated)).ToListAsync();
    }

    public Task<OpResult<string>> TransferAsync(int productId, int lotId, int fromId, int toId, int quantity, Caller caller) => gate.RunAsync(async () =>
    {
        if (!caller.CanManage) return OpResult<string>.Fail(Caller.DeniedManage);
        var actor = caller.Name;
        try
        {
            if (fromId == toId) return OpResult<string>.Fail("Source and destination must differ.");
            if (quantity <= 0) return OpResult<string>.Fail("Quantity must be positive.");
            await using var db = await factory.CreateDbContextAsync();
            var from = await db.Warehouses.FindAsync(fromId);
            var to = await db.Warehouses.FindAsync(toId);
            var product = await db.Products.FindAsync(productId);
            var lot = await db.Lots.FindAsync(lotId);
            if (from is null || to is null || product is null || lot is null || lot.ProductId != productId)
                return OpResult<string>.Fail("Choose a product, lot and two warehouses.");
            if (!product.IsActive) return OpResult<string>.Fail($"{product.Name} is inactive.");
            if (!from.IsActive || !to.IsActive) return OpResult<string>.Fail("Both warehouses must be active.");
            if (product.ColdChain && !to.ColdChainCapable)
                return OpResult<string>.Fail($"{product.Name} is cold-chain; {to.Name} cannot receive it.");

            var src = await db.StockLevels.FirstOrDefaultAsync(s => s.WarehouseId == fromId && s.LotId == lotId);
            if (src is null || src.Available < quantity)
                return OpResult<string>.Fail($"Only {src?.Available ?? 0} units are available to transfer (reserved stock cannot move).");

            src.Quantity = StockLedger.ApplyDelta(src.Quantity, -quantity);
            var dst = await db.StockLevels.FirstOrDefaultAsync(s => s.WarehouseId == toId && s.LotId == lotId);
            if (dst is null) db.StockLevels.Add(new StockLevel { WarehouseId = toId, ProductId = productId, LotId = lotId, Quantity = quantity });
            else dst.Quantity += quantity;

            var now = clock.GetUtcNow().UtcDateTime;
            var t = new Transfer
            {
                Number = "PENDING", ProductId = productId, LotId = lotId, FromWarehouseId = fromId, ToWarehouseId = toId,
                Quantity = quantity, CreatedUtc = now, CreatedBy = actor
            };
            db.Transfers.Add(t);
            await db.SaveChangesAsync();
            t.Number = $"TRF-{t.Id + 100:D5}";
            db.Movements.Add(new StockMovement { TimestampUtc = now, Type = MovementType.TransferOut, ProductId = productId, LotId = lotId, WarehouseId = fromId, Quantity = -quantity, Reference = t.Number, Actor = actor });
            db.Movements.Add(new StockMovement { TimestampUtc = now, Type = MovementType.TransferIn, ProductId = productId, LotId = lotId, WarehouseId = toId, Quantity = quantity, Reference = t.Number, Actor = actor });
            await db.SaveChangesAsync();

            hub.Publish(ActivityKind.Transfer, $"{t.Number}: {quantity} x {product.Name} (lot {lot.LotNumber}) moved {from.Code} to {to.Code}", Topic.Stock | Topic.Alerts);
            return OpResult<string>.Success(t.Number);
        }
        catch (DomainException ex) { return OpResult<string>.Fail(ex.Message); }
    });

    /// <summary>
    /// Reverses a transfer with compensating movements (TransferOut at the destination, TransferIn at the source, reference TRF-xxxxx-REV).
    /// The original ledger rows are never touched; the transfer is only flagged as reversed.
    /// </summary>
    public Task<OpResult<string>> ReverseAsync(int transferId, Caller caller) => gate.RunAsync(async () =>
    {
        if (!caller.CanManage) return OpResult<string>.Fail(Caller.DeniedManage);
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var t = await db.Transfers.Include(x => x.Product).Include(x => x.Lot).Include(x => x.FromWarehouse).Include(x => x.ToWarehouse)
                .FirstOrDefaultAsync(x => x.Id == transferId);
            if (t is null) return OpResult<string>.Fail("Transfer not found.");
            var dst = await db.StockLevels.FirstOrDefaultAsync(s => s.WarehouseId == t.ToWarehouseId && s.LotId == t.LotId);
            var reason = TransferRules.CannotReverseReason(t.ReversedUtc is not null, dst?.Quantity ?? 0, dst?.Allocated ?? 0, t.Quantity);
            if (reason is not null) return OpResult<string>.Fail(reason);

            dst!.Quantity = StockLedger.ApplyDelta(dst.Quantity, -t.Quantity);
            var src = await db.StockLevels.FirstOrDefaultAsync(s => s.WarehouseId == t.FromWarehouseId && s.LotId == t.LotId);
            if (src is null) db.StockLevels.Add(new StockLevel { WarehouseId = t.FromWarehouseId, ProductId = t.ProductId, LotId = t.LotId, Quantity = t.Quantity });
            else src.Quantity += t.Quantity;

            var now = clock.GetUtcNow().UtcDateTime;
            var reference = $"{t.Number}-REV";
            db.Movements.Add(new StockMovement { TimestampUtc = now, Type = MovementType.TransferOut, ProductId = t.ProductId, LotId = t.LotId, WarehouseId = t.ToWarehouseId, Quantity = -t.Quantity, Reference = reference, Actor = caller.Name });
            db.Movements.Add(new StockMovement { TimestampUtc = now, Type = MovementType.TransferIn, ProductId = t.ProductId, LotId = t.LotId, WarehouseId = t.FromWarehouseId, Quantity = t.Quantity, Reference = reference, Actor = caller.Name });
            t.ReversedUtc = now;
            t.ReversedBy = caller.Name;
            await db.SaveChangesAsync();
            hub.Publish(ActivityKind.Transfer, $"{t.Number} reversed by {caller.Name}: {t.Quantity} x {t.Product!.Name} returned {t.ToWarehouse!.Code} to {t.FromWarehouse!.Code}", Topic.Stock | Topic.Alerts, "warning");
            return OpResult<string>.Success(reference);
        }
        catch (DomainException ex) { return OpResult<string>.Fail(ex.Message); }
    });

    public async Task<IReadOnlyList<TransferRow>> RecentAsync(int take = 50)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Transfers.AsNoTracking().OrderByDescending(t => t.CreatedUtc).ThenByDescending(t => t.Id).Take(take)
            .Select(t => new TransferRow(t.Id, t.Number, t.Product!.Sku, t.Product.Name, t.Lot!.LotNumber, t.FromWarehouse!.Code, t.ToWarehouse!.Code, t.Quantity, t.CreatedUtc, t.CreatedBy, t.ReversedUtc != null))
            .ToListAsync();
    }
}
