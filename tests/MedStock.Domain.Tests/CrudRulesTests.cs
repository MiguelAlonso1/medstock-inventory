using MedStock.Domain;

namespace MedStock.Domain.Tests;

public class DeletePolicyTests
{
    [Fact]
    public void Unreferenced_CanBeDeleted() => Assert.True(DeletePolicy.CanDelete(ReferenceCounts.Of(("lot(s)", 0), ("order line(s)", 0))));

    [Fact]
    public void Referenced_IsBlocked_AndMessageOffersDeactivate()
    {
        var refs = ReferenceCounts.Of(("lot(s)", 3), ("order line(s)", 0), ("stock movement(s)", 12));
        Assert.False(DeletePolicy.CanDelete(refs));
        Assert.Equal(15, refs.Total);
        var msg = DeletePolicy.BlockedMessage("Product X", refs);
        Assert.Contains("3 lot(s)", msg);
        Assert.Contains("12 stock movement(s)", msg);
        Assert.DoesNotContain("order line", msg);
        Assert.Contains("Deactivate", msg);
    }
}

public class MasterDataRulesTests
{
    [Fact]
    public void ValidProduct_HasNoErrors() =>
        Assert.Empty(MasterDataRules.ValidateProduct("SKU-1", "Name", "Cat", "box", 1.5, 10, 50, 1, 24, 0));

    [Theory]
    [InlineData("", "n", "c", "u", 1.0, 1, 2, 1, 1, 0.0)]
    [InlineData("s", " ", "c", "u", 1.0, 1, 2, 1, 1, 0.0)]
    [InlineData("s", "n", "c", "u", -1.0, 1, 2, 1, 1, 0.0)]
    [InlineData("s", "n", "c", "u", 1.0, -1, 2, 1, 1, 0.0)]
    [InlineData("s", "n", "c", "u", 1.0, 10, 5, 1, 1, 0.0)]
    [InlineData("s", "n", "c", "u", 1.0, 1, 2, 0, 1, 0.0)]
    [InlineData("s", "n", "c", "u", 1.0, 1, 2, 1, 0, 0.0)]
    [InlineData("s", "n", "c", "u", 1.0, 1, 2, 1, 1, -0.5)]
    public void InvalidProduct_ReportsErrors(string sku, string name, string cat, string unit, double cost, int min, int max, int pack, int shelf, double demand) =>
        Assert.NotEmpty(MasterDataRules.ValidateProduct(sku, name, cat, unit, cost, min, max, pack, shelf, demand));

    [Fact]
    public void Warehouse_Supplier_Customer_Validation()
    {
        Assert.Empty(MasterDataRules.ValidateWarehouse("ATL", "Atlanta", "Atlanta, GA"));
        Assert.NotEmpty(MasterDataRules.ValidateWarehouse("", "Atlanta", "x"));
        Assert.NotEmpty(MasterDataRules.ValidateWarehouse("WAYTOOLONGCODE", "A", "x"));
        Assert.Empty(MasterDataRules.ValidateSupplier("Acme", "US", 7));
        Assert.NotEmpty(MasterDataRules.ValidateSupplier("Acme", "US", -1));
        Assert.NotEmpty(MasterDataRules.ValidateSupplier("Acme", "US", 400));
        Assert.Empty(MasterDataRules.ValidateCustomer("Clinic", "Town"));
        Assert.NotEmpty(MasterDataRules.ValidateCustomer(null, "Town"));
    }
}

public class OrderEditRulesTests
{
    [Theory]
    [InlineData(OrderStatus.Pending, true)]
    [InlineData(OrderStatus.Allocated, true)]
    [InlineData(OrderStatus.Picking, false)]
    [InlineData(OrderStatus.Shipped, false)]
    [InlineData(OrderStatus.Cancelled, false)]
    public void LinesEditableOnlyBeforePicking(OrderStatus s, bool expected) => Assert.Equal(expected, OrderEditRules.CanEditLines(s));

    [Fact]
    public void EnsureCanEditLines_ThrowsWhenPicking() => Assert.Throws<DomainException>(() => OrderEditRules.EnsureCanEditLines(OrderStatus.Picking));

    [Theory]
    [InlineData(OrderStatus.Cancelled, false, true)]
    [InlineData(OrderStatus.Cancelled, true, false)]
    [InlineData(OrderStatus.Shipped, false, false)]
    [InlineData(OrderStatus.Pending, false, false)]
    public void OnlyCancelledOrdersWithoutMovementsCanBeDeleted(OrderStatus s, bool movements, bool expected) =>
        Assert.Equal(expected, OrderEditRules.CanDelete(s, movements));

    [Fact]
    public void MergeLines_SumsDuplicates_AndRejectsBadInput()
    {
        var merged = OrderEditRules.MergeLines([(1, 2), (2, 5), (1, 3)]);
        Assert.Equal(2, merged.Count);
        Assert.Equal(5, merged.Single(m => m.ProductId == 1).Quantity);
        Assert.Throws<DomainException>(() => OrderEditRules.MergeLines([]));
        Assert.Throws<DomainException>(() => OrderEditRules.MergeLines([(1, 0)]));
    }
}

public class PurchaseOrderRulesTests
{
    private static readonly IReadOnlyList<PoLine> Lines = [new(1, 10, 0), new(2, 20, 0)];

    [Theory]
    [InlineData(PurchaseOrderStatus.Ordered, true)]
    [InlineData(PurchaseOrderStatus.PartiallyReceived, false)]
    [InlineData(PurchaseOrderStatus.Received, false)]
    [InlineData(PurchaseOrderStatus.Cancelled, false)]
    public void OnlyUntouchedOrdersCanBeCancelled(PurchaseOrderStatus s, bool expected) => Assert.Equal(expected, PurchaseOrderRules.CanCancel(s));

    [Fact]
    public void Edit_ChangesQuantities_AndKeepsStatus()
    {
        var o = PurchaseOrderRules.ApplyEdit(PurchaseOrderStatus.Ordered, Lines, new Dictionary<int, int> { [1] = 15 });
        Assert.Equal(PurchaseOrderStatus.Ordered, o.Status);
        Assert.Equal(15, o.Lines.Single(l => l.LineId == 1).Ordered);
        Assert.Equal(20, o.Lines.Single(l => l.LineId == 2).Ordered);
    }

    [Fact]
    public void Edit_CannotGoBelowReceived_NorBeNonPositive()
    {
        IReadOnlyList<PoLine> partial = [new(1, 10, 6), new(2, 20, 0)];
        Assert.Throws<DomainException>(() => PurchaseOrderRules.ApplyEdit(PurchaseOrderStatus.PartiallyReceived, partial, new Dictionary<int, int> { [1] = 5 }));
        Assert.Throws<DomainException>(() => PurchaseOrderRules.ApplyEdit(PurchaseOrderStatus.Ordered, Lines, new Dictionary<int, int> { [1] = 0 }));
        Assert.Throws<DomainException>(() => PurchaseOrderRules.ApplyEdit(PurchaseOrderStatus.Ordered, Lines, new Dictionary<int, int> { [99] = 3 }));
    }

    [Fact]
    public void Edit_DownToReceivedQuantity_CompletesTheOrder()
    {
        IReadOnlyList<PoLine> partial = [new(1, 10, 6), new(2, 20, 20)];
        var o = PurchaseOrderRules.ApplyEdit(PurchaseOrderStatus.PartiallyReceived, partial, new Dictionary<int, int> { [1] = 6 });
        Assert.Equal(PurchaseOrderStatus.Received, o.Status);
    }

    [Fact]
    public void Edit_IsRejectedOnReceivedOrCancelled()
    {
        Assert.Throws<DomainException>(() => PurchaseOrderRules.ApplyEdit(PurchaseOrderStatus.Received, Lines, new Dictionary<int, int>()));
        Assert.Throws<DomainException>(() => PurchaseOrderRules.ApplyEdit(PurchaseOrderStatus.Cancelled, Lines, new Dictionary<int, int>()));
    }
}

public class TransferReversalTests
{
    [Fact]
    public void Reversal_RejectedWhenAlreadyReversedOrStockGone()
    {
        Assert.Null(TransferRules.CannotReverseReason(false, 50, 0, 30));
        Assert.NotNull(TransferRules.CannotReverseReason(true, 50, 0, 30));
        Assert.NotNull(TransferRules.CannotReverseReason(false, 50, 30, 30));   // reserved
        Assert.NotNull(TransferRules.CannotReverseReason(false, 10, 0, 30));    // shipped
    }

    [Fact]
    public void CompensatingMovements_RestoreBalances_AndLedgerStaysValid()
    {
        var entries = new List<LedgerEntry>
        {
            new(1, MovementType.Receipt, 1, 1, 1, 100),
            new(2, MovementType.TransferOut, 1, 1, 1, -40),
            new(3, MovementType.TransferIn, 1, 1, 2, 40),
            // compensating pair for the transfer above
            new(4, MovementType.TransferOut, 1, 1, 2, -40),
            new(5, MovementType.TransferIn, 1, 1, 1, 40)
        };
        var (ledger, violations) = StockLedger.Verify(entries);
        Assert.Empty(violations);
        Assert.Equal(100, ledger.Balance(1, 1));
        Assert.Equal(0, ledger.Balance(2, 1));
    }

    [Fact]
    public void ReversalAfterDestinationStockLeft_WouldBeALedgerViolation()
    {
        var entries = new List<LedgerEntry>
        {
            new(1, MovementType.Receipt, 1, 1, 1, 100),
            new(2, MovementType.TransferOut, 1, 1, 1, -40),
            new(3, MovementType.TransferIn, 1, 1, 2, 40),
            new(4, MovementType.Shipment, 1, 1, 2, -30),
            new(5, MovementType.TransferOut, 1, 1, 2, -40)
        };
        Assert.Single(StockLedger.Verify(entries).Violations);
    }
}
