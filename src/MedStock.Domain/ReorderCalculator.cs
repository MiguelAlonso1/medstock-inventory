namespace MedStock.Domain;

/// <summary>Min/max (order-up-to) replenishment math.</summary>
public static class ReorderCalculator
{
    /// <summary>Classic reorder point: demand during lead time plus safety stock.</summary>
    public static int ReorderPoint(double avgDailyDemand, int leadTimeDays, int safetyStock)
    {
        if (avgDailyDemand < 0 || leadTimeDays < 0 || safetyStock < 0) throw new ArgumentOutOfRangeException(nameof(avgDailyDemand));
        return (int)Math.Ceiling(avgDailyDemand * leadTimeDays) + safetyStock;
    }

    /// <summary>Inventory position = available now + already on order.</summary>
    public static int Position(int onHand, int allocated, int onOrder) => onHand - allocated + onOrder;

    public static bool NeedsReorder(int onHand, int allocated, int onOrder, int min) => Position(onHand, allocated, onOrder) <= min;

    /// <summary>Quantity that brings the position back up to max, rounded up to the pack size. 0 when above min.</summary>
    public static int SuggestedQuantity(int onHand, int allocated, int onOrder, int min, int max, int packSize = 1)
    {
        if (max < min) throw new ArgumentException("max must be >= min", nameof(max));
        if (packSize < 1) throw new ArgumentOutOfRangeException(nameof(packSize));
        var position = Position(onHand, allocated, onOrder);
        if (position > min) return 0;
        var qty = Math.Max(0, max - position);
        var rem = qty % packSize;
        return rem == 0 ? qty : qty + (packSize - rem);
    }
}
