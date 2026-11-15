using Microsoft.AspNetCore.Identity;
using MedStock.Domain;

namespace MedStock.Web.Data;

public class AppUser : IdentityUser
{
    public string DisplayName { get; set; } = "";
}

public static class Roles
{
    public const string Admin = "Admin";
    public const string WarehouseManager = "Warehouse Manager";
    public const string Picker = "Picker";
    public const string Viewer = "Viewer";
    public static readonly string[] All = [Admin, WarehouseManager, Picker, Viewer];

    public const string ManageRoles = Admin + "," + WarehouseManager;
    public const string FulfillRoles = Admin + "," + WarehouseManager + "," + Picker;
}

public class Warehouse
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string City { get; set; } = "";
    public bool ColdChainCapable { get; set; }
}

public class Supplier
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Country { get; set; } = "";
    public int LeadTimeDays { get; set; }
}

public class Customer
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string City { get; set; } = "";
}

public class Product
{
    public int Id { get; set; }
    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
    public string NdcCode { get; set; } = "";
    public string Category { get; set; } = "";
    public string Unit { get; set; } = "each";
    public bool ColdChain { get; set; }
    public double UnitCost { get; set; }
    public int MinLevel { get; set; }
    public int MaxLevel { get; set; }
    public int PackSize { get; set; } = 1;
    public int ShelfLifeMonths { get; set; } = 24;
    public double AvgDailyDemand { get; set; }
    public int SupplierId { get; set; }
    public Supplier? Supplier { get; set; }
}

public class Lot
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public string LotNumber { get; set; } = "";
    public DateOnly ExpirationDate { get; set; }
    public Product? Product { get; set; }
}

public class StockLevel
{
    public int Id { get; set; }
    public int WarehouseId { get; set; }
    public int ProductId { get; set; }
    public int LotId { get; set; }
    public int Quantity { get; set; }
    public int Allocated { get; set; }
    public Warehouse? Warehouse { get; set; }
    public Product? Product { get; set; }
    public Lot? Lot { get; set; }
    public int Available => Quantity - Allocated;
}

public class StockMovement
{
    public long Id { get; set; }
    public DateTime TimestampUtc { get; set; }
    public MovementType Type { get; set; }
    public int ProductId { get; set; }
    public int LotId { get; set; }
    public int WarehouseId { get; set; }
    public int Quantity { get; set; }
    public string Reference { get; set; } = "";
    public string Actor { get; set; } = "";
    public Product? Product { get; set; }
    public Lot? Lot { get; set; }
    public Warehouse? Warehouse { get; set; }
}

public class CustomerOrder
{
    public int Id { get; set; }
    public string Number { get; set; } = "";
    public int CustomerId { get; set; }
    public int WarehouseId { get; set; }
    public OrderStatus Status { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public string CreatedBy { get; set; } = "";
    public Customer? Customer { get; set; }
    public Warehouse? Warehouse { get; set; }
    public List<OrderLine> Lines { get; set; } = [];
}

public class OrderLine
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public int ProductId { get; set; }
    public int QtyOrdered { get; set; }
    public int QtyAllocated { get; set; }
    public int QtyShipped { get; set; }
    public CustomerOrder? Order { get; set; }
    public Product? Product { get; set; }
    public List<OrderAllocation> Allocations { get; set; } = [];
}

public class OrderAllocation
{
    public int Id { get; set; }
    public int OrderLineId { get; set; }
    public int StockLevelId { get; set; }
    public int LotId { get; set; }
    public int Quantity { get; set; }
    public Lot? Lot { get; set; }
    public StockLevel? StockLevel { get; set; }
}

public class PurchaseOrder
{
    public int Id { get; set; }
    public string Number { get; set; } = "";
    public int SupplierId { get; set; }
    public int WarehouseId { get; set; }
    public PurchaseOrderStatus Status { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateOnly ExpectedDate { get; set; }
    public DateTime? ReceivedUtc { get; set; }
    public string CreatedBy { get; set; } = "";
    public Supplier? Supplier { get; set; }
    public Warehouse? Warehouse { get; set; }
    public List<PurchaseOrderLine> Lines { get; set; } = [];
}

public class PurchaseOrderLine
{
    public int Id { get; set; }
    public int PurchaseOrderId { get; set; }
    public int ProductId { get; set; }
    public int QtyOrdered { get; set; }
    public int QtyReceived { get; set; }
    public double UnitCost { get; set; }
    public PurchaseOrder? PurchaseOrder { get; set; }
    public Product? Product { get; set; }
}

public class Transfer
{
    public int Id { get; set; }
    public string Number { get; set; } = "";
    public int ProductId { get; set; }
    public int LotId { get; set; }
    public int FromWarehouseId { get; set; }
    public int ToWarehouseId { get; set; }
    public int Quantity { get; set; }
    public DateTime CreatedUtc { get; set; }
    public string CreatedBy { get; set; } = "";
    public Product? Product { get; set; }
    public Lot? Lot { get; set; }
    public Warehouse? FromWarehouse { get; set; }
    public Warehouse? ToWarehouse { get; set; }
}

public class TempExcursion
{
    public int Id { get; set; }
    public int WarehouseId { get; set; }
    public string Zone { get; set; } = "";
    public double ReadingC { get; set; }
    public DateTime RecordedUtc { get; set; }
    public string? AcknowledgedBy { get; set; }
    public DateTime? AcknowledgedUtc { get; set; }
    public Warehouse? Warehouse { get; set; }
}
