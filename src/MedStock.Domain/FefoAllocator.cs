namespace MedStock.Domain;

public sealed record LotStock(int LotId, int StockLevelId, DateOnly Expiry, int Available);

public sealed record LotAllocation(int LotId, int StockLevelId, int Quantity);

public sealed record AllocationResult(IReadOnlyList<LotAllocation> Allocations, int Requested, int Shortfall)
{
    public int Allocated => Requested - Shortfall;
    public bool IsComplete => Shortfall == 0;
}

/// <summary>First-Expiry-First-Out allocation: consume the lots that expire soonest, skip expired ones.</summary>
public static class FefoAllocator
{
    /// <param name="minShelfLifeDays">Lots with fewer remaining days than this are not allocatable (0 = only skip expired).</param>
    public static AllocationResult Allocate(IEnumerable<LotStock> lots, int quantity, DateOnly today, int minShelfLifeDays = 0)
    {
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be positive.");
        var cutoff = today.AddDays(minShelfLifeDays);
        var remaining = quantity;
        var result = new List<LotAllocation>();

        foreach (var lot in lots
                     .Where(l => l.Available > 0 && l.Expiry >= cutoff)
                     .OrderBy(l => l.Expiry).ThenBy(l => l.LotId).ThenBy(l => l.StockLevelId))
        {
            if (remaining == 0) break;
            var take = Math.Min(lot.Available, remaining);
            result.Add(new LotAllocation(lot.LotId, lot.StockLevelId, take));
            remaining -= take;
        }
        return new AllocationResult(result, quantity, remaining);
    }
}
