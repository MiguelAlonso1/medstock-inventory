namespace MedStock.Domain;

/// <summary>How many records of each kind reference an entity (used to decide between delete and deactivate).</summary>
public sealed record ReferenceCounts(IReadOnlyDictionary<string, int> Counts)
{
    public static ReferenceCounts Of(params (string Name, int Count)[] items) =>
        new(items.Where(i => i.Count > 0).ToDictionary(i => i.Name, i => i.Count));

    public int Total => Counts.Values.Sum();
    public bool IsReferenced => Total > 0;
    public string Describe() => string.Join(", ", Counts.Select(c => $"{c.Value} {c.Key}"));
}

/// <summary>Hard delete is only allowed for records nothing refers to; otherwise the record is deactivated and history stays readable.</summary>
public static class DeletePolicy
{
    public static bool CanDelete(ReferenceCounts refs) => !refs.IsReferenced;

    public static string BlockedMessage(string what, ReferenceCounts refs) =>
        $"{what} cannot be deleted because it is referenced by {refs.Describe()}. Deactivate it instead: it disappears from pickers and the simulator, but its history stays.";
}

/// <summary>Pure input validation for master data. Each method returns the list of problems (empty = valid).</summary>
public static class MasterDataRules
{
    public const int MaxText = 120;

    public static IReadOnlyList<string> ValidateProduct(string? sku, string? name, string? category, string? unit, double unitCost,
        int minLevel, int maxLevel, int packSize, int shelfLifeMonths, double avgDailyDemand)
    {
        var e = new List<string>();
        Required(e, "SKU", sku, 40);
        Required(e, "Name", name, MaxText);
        Required(e, "Category", category, 60);
        Required(e, "Unit", unit, 30);
        if (unitCost < 0 || double.IsNaN(unitCost)) e.Add("Unit cost cannot be negative.");
        if (minLevel < 0) e.Add("Reorder point cannot be negative.");
        if (maxLevel < minLevel) e.Add("Reorder quantity (max level) must be at least the reorder point.");
        if (packSize < 1) e.Add("Pack size must be at least 1.");
        if (shelfLifeMonths < 1) e.Add("Shelf life must be at least 1 month.");
        if (avgDailyDemand < 0 || double.IsNaN(avgDailyDemand)) e.Add("Average daily demand cannot be negative.");
        return e;
    }

    public static IReadOnlyList<string> ValidateWarehouse(string? code, string? name, string? city)
    {
        var e = new List<string>();
        Required(e, "Code", code, 10);
        Required(e, "Name", name, MaxText);
        Required(e, "City", city, 80);
        return e;
    }

    public static IReadOnlyList<string> ValidateSupplier(string? name, string? country, int leadTimeDays)
    {
        var e = new List<string>();
        Required(e, "Name", name, MaxText);
        Required(e, "Country", country, 60);
        if (leadTimeDays is < 0 or > 365) e.Add("Lead time must be between 0 and 365 days.");
        return e;
    }

    public static IReadOnlyList<string> ValidateCustomer(string? name, string? city)
    {
        var e = new List<string>();
        Required(e, "Name", name, MaxText);
        Required(e, "City", city, 80);
        return e;
    }

    private static void Required(List<string> errors, string field, string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) errors.Add($"{field} is required.");
        else if (value.Trim().Length > max) errors.Add($"{field} must be at most {max} characters.");
    }
}

/// <summary>Rules for reversing a transfer with compensating ledger movements (rows are never edited or deleted).</summary>
public static class TransferRules
{
    /// <summary>Returns null when the transfer can be reversed, otherwise the reason it cannot.</summary>
    public static string? CannotReverseReason(bool alreadyReversed, int destinationQuantity, int destinationAllocated, int transferQuantity)
    {
        if (alreadyReversed) return "This transfer was already reversed.";
        var available = destinationQuantity - destinationAllocated;
        if (available < transferQuantity)
            return $"Only {Math.Max(0, available)} of the {transferQuantity} transferred units are still free at the destination (the rest was shipped or reserved).";
        return null;
    }
}

/// <summary>Rules for editing and removing customer orders.</summary>
public static class OrderEditRules
{
    /// <summary>Lines may change until picking starts.</summary>
    public static bool CanEditLines(OrderStatus s) => s is OrderStatus.Pending or OrderStatus.Allocated;

    /// <summary>Only a cancelled order (which never moved stock) may be hard-deleted.</summary>
    public static bool CanDelete(OrderStatus s, bool hasMovements) => s == OrderStatus.Cancelled && !hasMovements;

    public static void EnsureCanEditLines(OrderStatus s)
    {
        if (!CanEditLines(s)) throw new DomainException($"A {s} order can no longer be edited (lines can change only while Pending or Allocated).");
    }

    /// <summary>Merges duplicate products and validates quantities. Returns the merged (product, quantity) list.</summary>
    public static IReadOnlyList<(int ProductId, int Quantity)> MergeLines(IEnumerable<(int ProductId, int Quantity)> lines)
    {
        var list = lines.ToList();
        if (list.Count == 0) throw new DomainException("An order needs at least one line.");
        if (list.Any(l => l.Quantity <= 0)) throw new DomainException("Quantities must be positive.");
        return list.GroupBy(l => l.ProductId).Select(g => (g.Key, g.Sum(x => x.Quantity))).ToList();
    }
}

/// <summary>Cancel and edit rules for purchase orders.</summary>
public static class PurchaseOrderRules
{
    /// <summary>Only an untouched order can be cancelled; once stock is booked the received part cannot be un-received.</summary>
    public static bool CanCancel(PurchaseOrderStatus s) => s == PurchaseOrderStatus.Ordered;

    public static bool CanEdit(PurchaseOrderStatus s) => s is PurchaseOrderStatus.Ordered or PurchaseOrderStatus.PartiallyReceived;

    public static void EnsureCanCancel(PurchaseOrderStatus s)
    {
        if (!CanCancel(s)) throw new DomainException(s == PurchaseOrderStatus.PartiallyReceived
            ? "A partially received purchase order cannot be cancelled; receive or edit the remaining quantities instead."
            : $"A {s} purchase order cannot be cancelled.");
    }

    /// <summary>Applies new ordered quantities (never below what was already received) and returns the resulting lines and status.</summary>
    public static ReceivingOutcome ApplyEdit(PurchaseOrderStatus status, IReadOnlyList<PoLine> lines, IReadOnlyDictionary<int, int> newOrdered)
    {
        if (!CanEdit(status)) throw new DomainException($"A {status} purchase order can no longer be edited.");
        foreach (var id in newOrdered.Keys)
            if (lines.All(l => l.LineId != id)) throw new DomainException($"Unknown PO line {id}.");
        var updated = new List<PoLine>();
        foreach (var l in lines)
        {
            var qty = newOrdered.TryGetValue(l.LineId, out var q) ? q : l.Ordered;
            if (qty <= 0) throw new DomainException("Ordered quantities must be positive (cancel the order to drop it).");
            if (qty < l.Received) throw new DomainException($"Line {l.LineId}: {l.Received} units were already received, so the quantity cannot go below that.");
            updated.Add(l with { Ordered = qty });
        }
        var anyReceived = updated.Any(l => l.Received > 0);
        var newStatus = updated.All(l => l.Remaining == 0) ? PurchaseOrderStatus.Received
            : anyReceived ? PurchaseOrderStatus.PartiallyReceived : PurchaseOrderStatus.Ordered;
        return new ReceivingOutcome(newStatus, updated);
    }
}
