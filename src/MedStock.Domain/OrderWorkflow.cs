namespace MedStock.Domain;

/// <summary>Customer order lifecycle: Pending -> Allocated -> Picking -> Shipped, cancellable until shipped.</summary>
public static class OrderWorkflow
{
    public static bool CanTransition(OrderStatus from, OrderStatus to) => (from, to) switch
    {
        (OrderStatus.Pending, OrderStatus.Allocated) => true,
        (OrderStatus.Pending, OrderStatus.Cancelled) => true,
        (OrderStatus.Allocated, OrderStatus.Picking) => true,
        (OrderStatus.Allocated, OrderStatus.Cancelled) => true,
        (OrderStatus.Picking, OrderStatus.Shipped) => true,
        (OrderStatus.Picking, OrderStatus.Cancelled) => true,
        _ => false
    };

    public static void EnsureTransition(OrderStatus from, OrderStatus to)
    {
        if (!CanTransition(from, to)) throw new DomainException($"An order cannot move from {from} to {to}.");
    }

    public static bool IsOpen(OrderStatus s) => s is OrderStatus.Pending or OrderStatus.Allocated or OrderStatus.Picking;

    public static OrderStatus? Next(OrderStatus s) => s switch
    {
        OrderStatus.Allocated => OrderStatus.Picking,
        OrderStatus.Picking => OrderStatus.Shipped,
        _ => null
    };
}
