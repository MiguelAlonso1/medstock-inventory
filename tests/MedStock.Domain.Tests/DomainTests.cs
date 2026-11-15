using MedStock.Domain;

namespace MedStock.Domain.Tests;

public class ExpiryClassifierTests
{
    private static readonly DateOnly Today = new(2026, 11, 15);

    [Theory]
    [InlineData(-1, ExpiryClass.Expired)]
    [InlineData(0, ExpiryClass.Within30)]
    [InlineData(30, ExpiryClass.Within30)]
    [InlineData(31, ExpiryClass.Within60)]
    [InlineData(60, ExpiryClass.Within60)]
    [InlineData(61, ExpiryClass.Within90)]
    [InlineData(90, ExpiryClass.Within90)]
    [InlineData(91, ExpiryClass.Ok)]
    [InlineData(700, ExpiryClass.Ok)]
    public void Classify_BucketsByDaysRemaining(int days, ExpiryClass expected) =>
        Assert.Equal(expected, ExpiryClassifier.Classify(Today.AddDays(days), Today));

    [Fact]
    public void IsExpiringSoon_ExcludesExpiredAndOk()
    {
        Assert.False(ExpiryClassifier.IsExpiringSoon(ExpiryClass.Expired));
        Assert.False(ExpiryClassifier.IsExpiringSoon(ExpiryClass.Ok));
        Assert.True(ExpiryClassifier.IsExpiringSoon(ExpiryClass.Within60));
    }
}

public class FefoAllocatorTests
{
    private static readonly DateOnly Today = new(2026, 11, 15);

    private static LotStock Lot(int id, int days, int available) => new(id, id * 10, Today.AddDays(days), available);

    [Fact]
    public void Allocates_EarliestExpiryFirst()
    {
        var r = FefoAllocator.Allocate([Lot(1, 200, 50), Lot(2, 20, 30), Lot(3, 90, 40)], 60, Today);
        Assert.True(r.IsComplete);
        Assert.Equal([(2, 30), (3, 30)], r.Allocations.Select(a => (a.LotId, a.Quantity)));
    }

    [Fact]
    public void SkipsExpiredLots()
    {
        var r = FefoAllocator.Allocate([Lot(1, -5, 100), Lot(2, 10, 5)], 20, Today);
        Assert.Equal(15, r.Shortfall);
        Assert.Single(r.Allocations);
        Assert.Equal(2, r.Allocations[0].LotId);
    }

    [Fact]
    public void ReportsShortfall_WhenStockInsufficient()
    {
        var r = FefoAllocator.Allocate([Lot(1, 50, 10), Lot(2, 60, 5)], 40, Today);
        Assert.Equal(15, r.Allocated);
        Assert.Equal(25, r.Shortfall);
        Assert.False(r.IsComplete);
    }

    [Fact]
    public void RespectsMinimumShelfLife()
    {
        var r = FefoAllocator.Allocate([Lot(1, 10, 100), Lot(2, 120, 100)], 50, Today, minShelfLifeDays: 30);
        Assert.Equal(2, r.Allocations.Single().LotId);
    }

    [Fact]
    public void IgnoresEmptyLots_AndBreaksTiesByLotId()
    {
        var r = FefoAllocator.Allocate([Lot(5, 30, 10), Lot(3, 30, 10), Lot(4, 10, 0)], 15, Today);
        Assert.Equal([(3, 10), (5, 5)], r.Allocations.Select(a => (a.LotId, a.Quantity)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void RejectsNonPositiveQuantity(int qty) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => FefoAllocator.Allocate([Lot(1, 10, 10)], qty, Today));
}

public class ReorderCalculatorTests
{
    [Fact]
    public void ReorderPoint_IsLeadTimeDemandPlusSafetyStock() =>
        Assert.Equal(48, ReorderCalculator.ReorderPoint(3.2, 12, 9));

    [Fact]
    public void NoSuggestion_WhenPositionAboveMin() =>
        Assert.Equal(0, ReorderCalculator.SuggestedQuantity(onHand: 120, allocated: 10, onOrder: 0, min: 50, max: 200));

    [Fact]
    public void SuggestsUpToMax_WhenAtOrBelowMin() =>
        Assert.Equal(180, ReorderCalculator.SuggestedQuantity(onHand: 25, allocated: 5, onOrder: 0, min: 50, max: 200));

    [Fact]
    public void OnOrderStockCountsTowardsPosition()
    {
        Assert.Equal(0, ReorderCalculator.SuggestedQuantity(10, 0, 100, 50, 200));
        Assert.Equal(90, ReorderCalculator.SuggestedQuantity(10, 0, 100, 110, 200));
    }

    [Fact]
    public void RoundsUpToPackSize() =>
        Assert.Equal(180, ReorderCalculator.SuggestedQuantity(onHand: 0, allocated: 0, onOrder: 0, min: 10, max: 175, packSize: 12));

    [Fact]
    public void RejectsMaxBelowMin() =>
        Assert.Throws<ArgumentException>(() => ReorderCalculator.SuggestedQuantity(0, 0, 0, 10, 5));
}

public class StockLedgerTests
{
    private static LedgerEntry E(long seq, MovementType t, int qty, int lot = 1, int wh = 1) => new(seq, t, 1, lot, wh, qty);

    [Fact]
    public void Apply_TracksBalancesPerWarehouseAndLot()
    {
        var l = new StockLedger();
        l.Apply(E(1, MovementType.Receipt, 100));
        l.Apply(E(2, MovementType.Shipment, -30));
        l.Apply(E(3, MovementType.TransferIn, 10, wh: 2));
        Assert.Equal(70, l.Balance(1, 1));
        Assert.Equal(10, l.Balance(2, 1));
    }

    [Fact]
    public void Apply_RejectsNegativeStock()
    {
        var l = new StockLedger();
        l.Apply(E(1, MovementType.Receipt, 10));
        Assert.Throws<InsufficientStockException>(() => l.Apply(E(2, MovementType.Shipment, -11)));
        Assert.Equal(10, l.Balance(1, 1));
    }

    [Fact]
    public void Apply_RejectsWrongSignAndZero()
    {
        var l = new StockLedger();
        Assert.Throws<DomainException>(() => l.Apply(E(1, MovementType.Receipt, -5)));
        Assert.Throws<DomainException>(() => l.Apply(E(2, MovementType.Shipment, 5)));
        Assert.Throws<DomainException>(() => l.Apply(E(3, MovementType.Adjustment, 0)));
    }

    [Fact]
    public void Verify_CleanHistoryHasNoViolations_RegardlessOfInputOrder()
    {
        var (ledger, violations) = StockLedger.Verify([E(3, MovementType.Shipment, -40), E(1, MovementType.Receipt, 100), E(2, MovementType.TransferOut, -20)]);
        Assert.Empty(violations);
        Assert.Equal(40, ledger.Balance(1, 1));
    }

    [Fact]
    public void Verify_FlagsEntriesThatGoNegative()
    {
        var (_, violations) = StockLedger.Verify([E(1, MovementType.Receipt, 5), E(2, MovementType.Shipment, -9)]);
        var v = Assert.Single(violations);
        Assert.Equal(2, v.Sequence);
        Assert.Equal(-4, v.BalanceAfter);
    }

    [Fact]
    public void ApplyDelta_GuardsNegative()
    {
        Assert.Equal(7, StockLedger.ApplyDelta(10, -3));
        Assert.Throws<InsufficientStockException>(() => StockLedger.ApplyDelta(2, -3));
    }
}

public class PurchaseOrderReceivingTests
{
    private static readonly PoLine[] Lines = [new(1, 100, 0), new(2, 50, 20)];

    [Fact]
    public void FullReceipt_MarksReceived()
    {
        var o = PurchaseOrderReceiving.Receive(PurchaseOrderStatus.Ordered, Lines, PurchaseOrderReceiving.ReceiveAllRemaining(Lines));
        Assert.Equal(PurchaseOrderStatus.Received, o.Status);
        Assert.All(o.Lines, l => Assert.Equal(0, l.Remaining));
    }

    [Fact]
    public void PartialReceipt_MarksPartiallyReceived()
    {
        var o = PurchaseOrderReceiving.Receive(PurchaseOrderStatus.Ordered, Lines, [new ReceiptLine(1, 40)]);
        Assert.Equal(PurchaseOrderStatus.PartiallyReceived, o.Status);
        Assert.Equal(40, o.Lines[0].Received);
        Assert.Equal(20, o.Lines[1].Received);
    }

    [Fact]
    public void OverReceipt_IsRejected() =>
        Assert.Throws<DomainException>(() => PurchaseOrderReceiving.Receive(PurchaseOrderStatus.Ordered, Lines, [new ReceiptLine(2, 31)]));

    [Theory]
    [InlineData(PurchaseOrderStatus.Received)]
    [InlineData(PurchaseOrderStatus.Cancelled)]
    public void ClosedPo_CannotReceive(PurchaseOrderStatus s) =>
        Assert.Throws<DomainException>(() => PurchaseOrderReceiving.Receive(s, Lines, [new ReceiptLine(1, 1)]));

    [Fact]
    public void UnknownLineOrNonPositiveQty_IsRejected()
    {
        Assert.Throws<DomainException>(() => PurchaseOrderReceiving.Receive(PurchaseOrderStatus.Ordered, Lines, [new ReceiptLine(9, 1)]));
        Assert.Throws<DomainException>(() => PurchaseOrderReceiving.Receive(PurchaseOrderStatus.Ordered, Lines, [new ReceiptLine(1, 0)]));
    }
}

public class OrderWorkflowTests
{
    [Theory]
    [InlineData(OrderStatus.Pending, OrderStatus.Allocated, true)]
    [InlineData(OrderStatus.Allocated, OrderStatus.Picking, true)]
    [InlineData(OrderStatus.Picking, OrderStatus.Shipped, true)]
    [InlineData(OrderStatus.Picking, OrderStatus.Cancelled, true)]
    [InlineData(OrderStatus.Allocated, OrderStatus.Shipped, false)]
    [InlineData(OrderStatus.Shipped, OrderStatus.Cancelled, false)]
    [InlineData(OrderStatus.Cancelled, OrderStatus.Allocated, false)]
    public void Transitions(OrderStatus from, OrderStatus to, bool allowed) =>
        Assert.Equal(allowed, OrderWorkflow.CanTransition(from, to));

    [Fact]
    public void EnsureTransition_ThrowsOnInvalid() =>
        Assert.Throws<DomainException>(() => OrderWorkflow.EnsureTransition(OrderStatus.Shipped, OrderStatus.Picking));

    [Fact]
    public void Next_FollowsHappyPath()
    {
        Assert.Equal(OrderStatus.Picking, OrderWorkflow.Next(OrderStatus.Allocated));
        Assert.Equal(OrderStatus.Shipped, OrderWorkflow.Next(OrderStatus.Picking));
        Assert.Null(OrderWorkflow.Next(OrderStatus.Shipped));
    }
}
