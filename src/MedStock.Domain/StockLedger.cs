namespace MedStock.Domain;

/// <param name="Quantity">Signed: receipts/transfer-in positive, shipments/transfer-out negative.</param>
public readonly record struct LedgerEntry(long Sequence, MovementType Type, int ProductId, int LotId, int WarehouseId, int Quantity);

public sealed record LedgerViolation(long Sequence, int LotId, int WarehouseId, int BalanceAfter, string Reason);

/// <summary>
/// Append-only stock movement ledger. Balances are per (warehouse, lot) and may never go negative.
/// The same rules guard live writes (<see cref="Apply"/>) and verify history (<see cref="Verify"/>).
/// </summary>
public sealed class StockLedger
{
    private readonly Dictionary<(int Warehouse, int Lot), int> _balances = new();

    public int Balance(int warehouseId, int lotId) => _balances.GetValueOrDefault((warehouseId, lotId));

    public IReadOnlyDictionary<(int Warehouse, int Lot), int> Balances => _balances;

    public static void ValidateSign(MovementType type, int quantity)
    {
        if (quantity == 0) throw new DomainException("A movement quantity cannot be zero.");
        var ok = type switch
        {
            MovementType.Receipt or MovementType.TransferIn => quantity > 0,
            MovementType.Shipment or MovementType.TransferOut => quantity < 0,
            _ => true
        };
        if (!ok) throw new DomainException($"{type} movements must be {(quantity > 0 ? "negative" : "positive")}.");
    }

    public void Apply(LedgerEntry e)
    {
        ValidateSign(e.Type, e.Quantity);
        var key = (e.WarehouseId, e.LotId);
        var after = _balances.GetValueOrDefault(key) + e.Quantity;
        if (after < 0)
            throw new InsufficientStockException($"Lot {e.LotId} at warehouse {e.WarehouseId} would go negative ({after}).");
        _balances[key] = after;
    }

    /// <summary>Replays entries in sequence order; reports every entry that is malformed or drives a balance below zero.</summary>
    public static (StockLedger Ledger, IReadOnlyList<LedgerViolation> Violations) Verify(IEnumerable<LedgerEntry> entries)
    {
        var ledger = new StockLedger();
        var violations = new List<LedgerViolation>();
        foreach (var e in entries.OrderBy(x => x.Sequence))
        {
            try { ledger.Apply(e); }
            catch (InsufficientStockException ex)
            {
                violations.Add(new LedgerViolation(e.Sequence, e.LotId, e.WarehouseId, ledger.Balance(e.WarehouseId, e.LotId) + e.Quantity, ex.Message));
            }
            catch (DomainException ex)
            {
                violations.Add(new LedgerViolation(e.Sequence, e.LotId, e.WarehouseId, ledger.Balance(e.WarehouseId, e.LotId), ex.Message));
            }
        }
        return (ledger, violations);
    }

    /// <summary>Guard used by services before mutating a stock level.</summary>
    public static int ApplyDelta(int current, int delta)
    {
        var after = current + delta;
        if (after < 0) throw new InsufficientStockException($"Stock would go negative ({after}).");
        return after;
    }
}
