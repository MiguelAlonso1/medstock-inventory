namespace MedStock.Domain;

public sealed record PoLine(int LineId, int Ordered, int Received)
{
    public int Remaining => Ordered - Received;
}

public sealed record ReceiptLine(int LineId, int Quantity);

public sealed record ReceivingOutcome(PurchaseOrderStatus Status, IReadOnlyList<PoLine> Lines);

public static class PurchaseOrderReceiving
{
    /// <summary>Validates a (possibly partial) receipt and returns the new line quantities and PO status. No over-receipt.</summary>
    public static ReceivingOutcome Receive(PurchaseOrderStatus status, IReadOnlyList<PoLine> lines, IReadOnlyList<ReceiptLine> receipt)
    {
        if (status is PurchaseOrderStatus.Received or PurchaseOrderStatus.Cancelled)
            throw new DomainException($"A {status} purchase order cannot receive stock.");
        if (receipt.Count == 0) throw new DomainException("Nothing to receive.");

        var byId = lines.ToDictionary(l => l.LineId);
        var add = new Dictionary<int, int>();
        foreach (var r in receipt)
        {
            if (!byId.ContainsKey(r.LineId)) throw new DomainException($"Unknown PO line {r.LineId}.");
            if (r.Quantity <= 0) throw new DomainException("Received quantity must be positive.");
            add[r.LineId] = add.GetValueOrDefault(r.LineId) + r.Quantity;
        }

        var updated = new List<PoLine>();
        foreach (var l in lines)
        {
            var received = l.Received + add.GetValueOrDefault(l.LineId);
            if (received > l.Ordered)
                throw new DomainException($"Line {l.LineId}: receiving {add[l.LineId]} exceeds the {l.Remaining} still open.");
            updated.Add(l with { Received = received });
        }

        var newStatus = updated.All(l => l.Remaining == 0) ? PurchaseOrderStatus.Received : PurchaseOrderStatus.PartiallyReceived;
        return new ReceivingOutcome(newStatus, updated);
    }

    public static IReadOnlyList<ReceiptLine> ReceiveAllRemaining(IReadOnlyList<PoLine> lines) =>
        lines.Where(l => l.Remaining > 0).Select(l => new ReceiptLine(l.LineId, l.Remaining)).ToList();
}
