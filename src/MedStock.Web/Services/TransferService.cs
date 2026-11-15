using MedStock.Domain;
using MedStock.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace MedStock.Web.Services;

public sealed record TransferRow(int Id, string Number, string Sku, string Product, string LotNumber, string From, string To, int Quantity, DateTime CreatedUtc, string CreatedBy);
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

    public Task<OpResult<string>> TransferAsync(int productId, int lotId, int fromId, int toId, int quantity, string actor) => gate.RunAsync(async () =>
    {
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

    public async Task<IReadOnlyList<TransferRow>> RecentAsync(int take = 50)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Transfers.AsNoTracking().OrderByDescending(t => t.CreatedUtc).ThenByDescending(t => t.Id).Take(take)
            .Select(t => new TransferRow(t.Id, t.Number, t.Product!.Sku, t.Product.Name, t.Lot!.LotNumber, t.FromWarehouse!.Code, t.ToWarehouse!.Code, t.Quantity, t.CreatedUtc, t.CreatedBy))
            .ToListAsync();
    }
}
