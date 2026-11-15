namespace MedStock.Domain;

public enum ExpiryClass { Expired, Within30, Within60, Within90, Ok }

public enum OrderStatus { Pending = 0, Allocated = 1, Picking = 2, Shipped = 3, Cancelled = 4 }

public enum PurchaseOrderStatus { Ordered = 0, PartiallyReceived = 1, Received = 2, Cancelled = 3 }

public enum MovementType { Receipt = 0, Shipment = 1, TransferOut = 2, TransferIn = 3, Adjustment = 4 }

/// <summary>Raised when a domain rule is violated (negative stock, invalid transition, over-receipt...).</summary>
public class DomainException(string message) : Exception(message);

public sealed class InsufficientStockException(string message) : DomainException(message);
