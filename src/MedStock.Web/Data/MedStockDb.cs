using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace MedStock.Web.Data;

public class MedStockDb(DbContextOptions<MedStockDb> options) : IdentityDbContext<AppUser>(options)
{
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Lot> Lots => Set<Lot>();
    public DbSet<StockLevel> StockLevels => Set<StockLevel>();
    public DbSet<StockMovement> Movements => Set<StockMovement>();
    public DbSet<CustomerOrder> Orders => Set<CustomerOrder>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    public DbSet<OrderAllocation> OrderAllocations => Set<OrderAllocation>();
    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();
    public DbSet<PurchaseOrderLine> PurchaseOrderLines => Set<PurchaseOrderLine>();
    public DbSet<Transfer> Transfers => Set<Transfer>();
    public DbSet<TempExcursion> Excursions => Set<TempExcursion>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        b.Entity<Warehouse>().HasIndex(x => x.Code).IsUnique();
        b.Entity<Product>().HasIndex(x => x.Sku).IsUnique();
        b.Entity<Product>().HasIndex(x => x.Name);
        b.Entity<Lot>().HasIndex(x => new { x.ProductId, x.LotNumber }).IsUnique();
        b.Entity<StockLevel>().HasIndex(x => new { x.WarehouseId, x.LotId }).IsUnique();
        b.Entity<StockLevel>().HasIndex(x => x.ProductId);
        b.Entity<StockLevel>().Ignore(x => x.Available);
        b.Entity<StockMovement>().HasIndex(x => x.TimestampUtc);
        b.Entity<StockMovement>().HasIndex(x => x.ProductId);
        b.Entity<CustomerOrder>().HasIndex(x => x.Number).IsUnique();
        b.Entity<PurchaseOrder>().HasIndex(x => x.Number).IsUnique();
        b.Entity<OrderAllocation>().HasOne(x => x.StockLevel).WithMany().HasForeignKey(x => x.StockLevelId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<OrderAllocation>().HasOne(x => x.Lot).WithMany().HasForeignKey(x => x.LotId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Transfer>().HasOne(x => x.FromWarehouse).WithMany().HasForeignKey(x => x.FromWarehouseId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Transfer>().HasOne(x => x.ToWarehouse).WithMany().HasForeignKey(x => x.ToWarehouseId).OnDelete(DeleteBehavior.Restrict);
    }
}
