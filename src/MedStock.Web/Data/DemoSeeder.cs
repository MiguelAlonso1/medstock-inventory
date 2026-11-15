using MedStock.Domain;
using MedStock.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MedStock.Web.Data;

public static class DemoUsers
{
    public const string Password = "MedStock-Demo-2026!";
    public sealed record Demo(string Role, string Email, string Name, string Blurb);

    public static readonly Demo[] All =
    [
        new(Roles.Admin, "admin@medstock.demo", "Dana Whitfield", "Everything, including reset demo data and the simulator switch."),
        new(Roles.WarehouseManager, "manager@medstock.demo", "Marcus Chen", "Create orders and POs, receive stock, transfer between locations."),
        new(Roles.Picker, "picker@medstock.demo", "Priya Natarajan", "Pick and ship allocated orders. Read-only elsewhere."),
        new(Roles.Viewer, "viewer@medstock.demo", "Sam Okafor", "Read-only access to dashboards, inventory and alerts.")
    ];
}

/// <summary>Creates the SQLite schema and fills it with deterministic synthetic data (no real products, companies or people).</summary>
public sealed class DemoSeeder(IDbContextFactory<MedStockDb> factory, IServiceProvider sp, WriteGate gate, InventoryEventHub hub, SimulationControl sim, TimeProvider clock)
{
    private sealed record Base(string Name, string Category, bool Cold, double CostLo, double CostHi, string[] Strengths, string[] Forms, string Unit, int Pack, int Shelf);

    private static readonly Base[] Catalogue =
    [
        new("Amoxicillin", "Antibiotics", false, 4, 18, ["250 mg", "500 mg", "875 mg"], ["capsule", "tablet", "oral suspension"], "bottle", 1, 30),
        new("Azithromycin", "Antibiotics", false, 6, 24, ["250 mg", "500 mg"], ["tablet", "oral suspension"], "pack", 1, 30),
        new("Ciprofloxacin", "Antibiotics", false, 5, 20, ["250 mg", "500 mg", "750 mg"], ["tablet"], "bottle", 1, 36),
        new("Doxycycline", "Antibiotics", false, 5, 22, ["50 mg", "100 mg"], ["capsule", "tablet"], "bottle", 1, 30),
        new("Cephalexin", "Antibiotics", false, 4, 16, ["250 mg", "500 mg"], ["capsule", "oral suspension"], "bottle", 1, 24),
        new("Clindamycin", "Antibiotics", false, 7, 28, ["150 mg", "300 mg"], ["capsule", "solution"], "bottle", 1, 30),
        new("Metronidazole", "Antibiotics", false, 3, 12, ["250 mg", "500 mg"], ["tablet"], "bottle", 1, 36),
        new("Lisinopril", "Cardiovascular", false, 2, 9, ["5 mg", "10 mg", "20 mg", "40 mg"], ["tablet"], "bottle", 1, 36),
        new("Atorvastatin", "Cardiovascular", false, 3, 15, ["10 mg", "20 mg", "40 mg", "80 mg"], ["tablet"], "bottle", 1, 36),
        new("Amlodipine", "Cardiovascular", false, 2, 10, ["2.5 mg", "5 mg", "10 mg"], ["tablet"], "bottle", 1, 36),
        new("Metoprolol", "Cardiovascular", false, 3, 11, ["25 mg", "50 mg", "100 mg"], ["tablet", "extended-release tablet"], "bottle", 1, 36),
        new("Losartan", "Cardiovascular", false, 3, 13, ["25 mg", "50 mg", "100 mg"], ["tablet"], "bottle", 1, 36),
        new("Warfarin", "Cardiovascular", false, 4, 14, ["1 mg", "2.5 mg", "5 mg"], ["tablet"], "bottle", 1, 36),
        new("Clopidogrel", "Cardiovascular", false, 4, 19, ["75 mg"], ["tablet"], "bottle", 1, 36),
        new("Furosemide", "Cardiovascular", false, 2, 8, ["20 mg", "40 mg"], ["tablet", "injection"], "bottle", 1, 36),
        new("Metformin", "Endocrine", false, 2, 9, ["500 mg", "850 mg", "1000 mg"], ["tablet", "extended-release tablet"], "bottle", 1, 36),
        new("Levothyroxine", "Endocrine", false, 4, 16, ["25 mcg", "50 mcg", "100 mcg", "150 mcg"], ["tablet"], "bottle", 1, 24),
        new("Insulin glargine", "Endocrine", true, 38, 96, ["100 units/mL"], ["pen", "vial"], "box", 5, 18),
        new("Insulin lispro", "Endocrine", true, 34, 90, ["100 units/mL"], ["pen", "vial"], "box", 5, 18),
        new("Semaglutide", "Endocrine", true, 210, 480, ["0.25 mg", "0.5 mg", "1 mg"], ["pen"], "box", 1, 18),
        new("Ibuprofen", "Pain & Inflammation", false, 2, 7, ["200 mg", "400 mg", "800 mg"], ["tablet"], "bottle", 1, 36),
        new("Acetaminophen", "Pain & Inflammation", false, 2, 6, ["325 mg", "500 mg"], ["tablet", "oral solution"], "bottle", 1, 36),
        new("Naproxen", "Pain & Inflammation", false, 3, 10, ["250 mg", "500 mg"], ["tablet"], "bottle", 1, 36),
        new("Gabapentin", "Neurology", false, 4, 17, ["100 mg", "300 mg", "600 mg"], ["capsule", "tablet"], "bottle", 1, 36),
        new("Levetiracetam", "Neurology", false, 5, 21, ["250 mg", "500 mg", "750 mg"], ["tablet"], "bottle", 1, 36),
        new("Lamotrigine", "Neurology", false, 5, 20, ["25 mg", "100 mg", "200 mg"], ["tablet"], "bottle", 1, 36),
        new("Albuterol", "Respiratory", false, 18, 52, ["90 mcg"], ["inhaler"], "each", 1, 24),
        new("Montelukast", "Respiratory", false, 5, 19, ["4 mg", "5 mg", "10 mg"], ["tablet", "chewable tablet"], "bottle", 1, 36),
        new("Fluticasone", "Respiratory", false, 14, 40, ["50 mcg", "110 mcg", "220 mcg"], ["inhaler", "nasal spray"], "each", 1, 24),
        new("Omeprazole", "Gastrointestinal", false, 3, 12, ["20 mg", "40 mg"], ["capsule"], "bottle", 1, 36),
        new("Pantoprazole", "Gastrointestinal", false, 3, 13, ["20 mg", "40 mg"], ["tablet", "injection"], "bottle", 1, 36),
        new("Ondansetron", "Gastrointestinal", false, 3, 14, ["4 mg", "8 mg"], ["tablet", "injection"], "pack", 1, 36),
        new("Sertraline", "Mental Health", false, 3, 13, ["25 mg", "50 mg", "100 mg"], ["tablet"], "bottle", 1, 36),
        new("Escitalopram", "Mental Health", false, 3, 14, ["5 mg", "10 mg", "20 mg"], ["tablet"], "bottle", 1, 36),
        new("Prednisone", "Endocrine", false, 2, 9, ["5 mg", "10 mg", "20 mg"], ["tablet"], "bottle", 1, 36),
        new("Cetirizine", "Allergy", false, 2, 7, ["5 mg", "10 mg"], ["tablet", "oral solution"], "bottle", 1, 36),
        new("Influenza vaccine", "Vaccines & Biologics", true, 14, 32, ["0.5 mL"], ["prefilled syringe", "multi-dose vial"], "box", 10, 12),
        new("Hepatitis B vaccine", "Vaccines & Biologics", true, 22, 58, ["10 mcg", "20 mcg"], ["prefilled syringe", "vial"], "box", 10, 24),
        new("Tdap vaccine", "Vaccines & Biologics", true, 28, 64, ["0.5 mL"], ["prefilled syringe"], "box", 10, 24),
        new("MMR vaccine", "Vaccines & Biologics", true, 60, 140, ["0.5 mL"], ["vial"], "box", 10, 24),
        new("Pneumococcal vaccine", "Vaccines & Biologics", true, 90, 210, ["0.5 mL"], ["prefilled syringe"], "box", 10, 24),
        new("Adalimumab", "Vaccines & Biologics", true, 480, 1250, ["40 mg/0.8 mL"], ["pen", "prefilled syringe"], "box", 2, 24),
        new("Etanercept", "Vaccines & Biologics", true, 420, 980, ["50 mg/mL"], ["pen", "prefilled syringe"], "box", 4, 24),
        new("Filgrastim", "Vaccines & Biologics", true, 150, 380, ["300 mcg", "480 mcg"], ["prefilled syringe"], "box", 5, 24),
        new("Epoetin alfa", "Vaccines & Biologics", true, 90, 260, ["4000 units/mL", "10000 units/mL"], ["vial"], "box", 6, 24),
        new("Enoxaparin", "Injectables", false, 12, 44, ["40 mg/0.4 mL", "60 mg/0.6 mL", "100 mg/mL"], ["prefilled syringe"], "box", 10, 24),
        new("Ceftriaxone", "Injectables", false, 6, 24, ["500 mg", "1 g", "2 g"], ["vial"], "box", 10, 36),
        new("Heparin", "Injectables", false, 8, 30, ["1000 units/mL", "5000 units/mL"], ["vial"], "box", 10, 24),
        new("Ketorolac", "Injectables", false, 4, 15, ["15 mg/mL", "30 mg/mL"], ["vial"], "box", 25, 24),
        new("Lidocaine", "Injectables", false, 5, 18, ["1%", "2%"], ["vial", "ampule"], "box", 25, 24),
        new("Dexamethasone", "Injectables", false, 4, 16, ["4 mg/mL", "10 mg/mL"], ["vial"], "box", 25, 24),
        new("Nitrile exam gloves", "Medical Supplies", false, 6, 14, ["Small", "Medium", "Large", "X-Large"], ["box of 100"], "case", 10, 60),
        new("Surgical mask", "Medical Supplies", false, 4, 11, ["Level 1", "Level 2", "Level 3"], ["box of 50"], "case", 10, 48),
        new("Syringe", "Medical Supplies", false, 5, 17, ["1 mL", "3 mL", "5 mL", "10 mL"], ["luer-lock box of 100"], "case", 10, 60),
        new("IV catheter", "Medical Supplies", false, 18, 52, ["18G", "20G", "22G", "24G"], ["box of 50"], "case", 5, 48),
        new("Sodium chloride 0.9% IV", "Medical Supplies", false, 1, 4, ["250 mL", "500 mL", "1000 mL"], ["bag"], "case", 12, 36),
        new("Gauze pad", "Medical Supplies", false, 2, 8, ["2x2 in", "4x4 in"], ["sterile box of 200"], "case", 10, 60),
        new("Alcohol prep pad", "Medical Supplies", false, 2, 6, ["Small", "Large"], ["box of 200"], "case", 20, 36),
        new("Blood glucose test strips", "Medical Supplies", false, 12, 38, ["50 ct", "100 ct"], ["box"], "box", 10, 18)
    ];

    private static readonly string[] SupplierNames = ["Halcyon Pharmaceuticals", "Meridian Biologics", "Northfield Generics", "Atlas Medical Supply", "Brightwater Labs", "Corvane Healthcare", "Sundial Therapeutics", "Pinecrest Surgical"];
    private static readonly (string Country, int Lead)[] SupplierMeta = [("US", 7), ("US", 10), ("IN", 21), ("US", 5), ("DE", 18), ("CA", 9), ("US", 12), ("MX", 14)];
    private static readonly string[] CustomerNames =
    [
        "St. Marlow General Hospital", "Riverbend Medical Center", "Lakeshore Community Pharmacy", "Harborview Health System", "Summit Oncology Clinic",
        "Cedar Ridge Urgent Care", "Pioneer Valley Hospital", "Northgate Family Practice", "Bayside Children's Clinic", "Granite State Surgical Center",
        "Willow Creek Long-Term Care", "Eastbrook Specialty Pharmacy"
    ];
    private static readonly string[] Cities = ["Springfield", "Riverton", "Fairview", "Lakeside", "Oakdale", "Brookfield", "Maple Heights", "Clearwater", "Ashford", "Westbrook", "Kingsport", "Millbrook"];

    public async Task InitializeAsync()
    {
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        }
        await EnsureUsersAsync();
        await using (var db = await factory.CreateDbContextAsync())
            if (await db.Products.AnyAsync()) return;
        await SeedAsync();
    }

    private async Task EnsureUsersAsync()
    {
        using var scope = sp.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        foreach (var r in Roles.All)
            if (!await roles.RoleExistsAsync(r)) await roles.CreateAsync(new IdentityRole(r));
        foreach (var d in DemoUsers.All)
        {
            if (await users.FindByEmailAsync(d.Email) is not null) continue;
            var u = new AppUser { UserName = d.Email, Email = d.Email, EmailConfirmed = true, DisplayName = d.Name };
            var res = await users.CreateAsync(u, DemoUsers.Password);
            if (!res.Succeeded) throw new InvalidOperationException("Could not seed demo user: " + string.Join("; ", res.Errors.Select(e => e.Description)));
            await users.AddToRoleAsync(u, d.Role);
        }
    }

    /// <summary>Admin action: wipe business data and reseed. Users are kept. Simulation is paused meanwhile.</summary>
    public async Task ResetAsync(string actor)
    {
        using var _ = sim.Suspend();
        await gate.RunAsync(async () =>
        {
            await using var db = await factory.CreateDbContextAsync();
            await db.OrderAllocations.ExecuteDeleteAsync();
            await db.OrderLines.ExecuteDeleteAsync();
            await db.Orders.ExecuteDeleteAsync();
            await db.PurchaseOrderLines.ExecuteDeleteAsync();
            await db.PurchaseOrders.ExecuteDeleteAsync();
            await db.Transfers.ExecuteDeleteAsync();
            await db.Movements.ExecuteDeleteAsync();
            await db.StockLevels.ExecuteDeleteAsync();
            await db.Lots.ExecuteDeleteAsync();
            await db.Products.ExecuteDeleteAsync();
            await db.Suppliers.ExecuteDeleteAsync();
            await db.Customers.ExecuteDeleteAsync();
            await db.Excursions.ExecuteDeleteAsync();
            await db.Warehouses.ExecuteDeleteAsync();
            return 0;
        });
        hub.Clear();
        await SeedAsync();
        hub.Publish(ActivityKind.System, $"Demo data reset by {actor}", Topic.All, "warning");
    }

    private async Task SeedAsync()
    {
        var wasMuted = hub.Muted;
        hub.Muted = true;
        try
        {
            await SeedMasterAndStockAsync();
            await SeedOrdersAndPurchasingAsync();
        }
        finally { hub.Muted = wasMuted; }
        hub.Publish(ActivityKind.System, "MedStock demo data loaded (synthetic)", Topic.All);
    }

    // Retail bottles vs. hospital unit-dose packaging double the catalogue for oral and injectable products.
    private static IEnumerable<string> PackVariants(Base b) =>
        b.Category is "Medical Supplies" or "Vaccines & Biologics" ? [""] : ["", " (unit-dose pack)"];

    private async Task SeedMasterAndStockAsync()
    {
        var rng = new Random(20261115);
        var now = clock.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(now);
        await using var db = await factory.CreateDbContextAsync();

        var warehouses = new[]
        {
            new Warehouse { Code = "ATL", Name = "Atlanta Distribution Center", City = "Atlanta, GA", ColdChainCapable = true },
            new Warehouse { Code = "DAL", Name = "Dallas Distribution Center", City = "Dallas, TX", ColdChainCapable = false },
            new Warehouse { Code = "CMH", Name = "Columbus Distribution Center", City = "Columbus, OH", ColdChainCapable = true },
            new Warehouse { Code = "PHX", Name = "Phoenix Distribution Center", City = "Phoenix, AZ", ColdChainCapable = false }
        };
        db.Warehouses.AddRange(warehouses);
        var suppliers = SupplierNames.Select((n, i) => new Supplier { Name = n, Country = SupplierMeta[i].Country, LeadTimeDays = SupplierMeta[i].Lead }).ToArray();
        db.Suppliers.AddRange(suppliers);
        db.Customers.AddRange(CustomerNames.Select((n, i) => new Customer { Name = n, City = Cities[i] }));
        await db.SaveChangesAsync();

        // Products: expand the catalogue, shuffle deterministically, keep 300.
        var combos = Catalogue.SelectMany(b => b.Strengths.SelectMany(s => b.Forms.SelectMany(f => PackVariants(b).Select(v => (b, s, f + v))))).ToList();
        for (var i = combos.Count - 1; i > 0; i--) { var j = rng.Next(i + 1); (combos[i], combos[j]) = (combos[j], combos[i]); }
        var products = new List<Product>();
        foreach (var (b, s, f) in combos.Take(300).OrderBy(c => c.b.Category).ThenBy(c => c.b.Name))
        {
            var n = products.Count + 1;
            var cold = b.Cold;
            products.Add(new Product
            {
                Sku = $"MS-{new string(b.Category.Where(char.IsLetter).Take(3).ToArray()).ToUpperInvariant()}-{n:D4}",
                Name = $"{b.Name} {s} {f}",
                NdcCode = $"99{rng.Next(100, 999)}-{rng.Next(1000, 9999)}-{rng.Next(10, 99)}",
                Category = b.Category, Unit = b.Unit, ColdChain = cold, PackSize = b.Pack, ShelfLifeMonths = b.Shelf,
                UnitCost = Math.Round(b.CostLo + rng.NextDouble() * (b.CostHi - b.CostLo), 2),
                SupplierId = suppliers[cold ? (rng.Next(2) == 0 ? 1 : 6) : rng.Next(suppliers.Length)].Id
            });
        }
        db.Products.AddRange(products);
        await db.SaveChangesAsync();

        var lots = new List<Lot>();
        var levels = new List<StockLevel>();
        var lotWh = new Dictionary<Lot, List<(int Wh, int Qty)>>();
        foreach (var p in products)
        {
            var allowed = warehouses.Where(w => !p.ColdChain || w.ColdChainCapable).ToArray();
            var scale = p.Category == "Medical Supplies" ? 60 : p.ColdChain ? 25 : 40;
            var lotCount = rng.Next(2, 5);
            var total = 0;
            for (var li = 0; li < lotCount; li++)
            {
                var roll = rng.NextDouble();
                var days = roll switch
                {
                    < 0.03 => -rng.Next(3, 60),
                    < 0.09 => rng.Next(0, 31),
                    < 0.14 => rng.Next(31, 61),
                    < 0.19 => rng.Next(61, 91),
                    _ => rng.Next(91, p.ColdChain ? 420 : 900)
                };
                var lot = new Lot { ProductId = p.Id, LotNumber = $"{(char)('A' + rng.Next(26))}{today.Year % 100}{(char)('A' + li)}{rng.Next(10, 99)}", ExpirationDate = today.AddDays(days) };
                lots.Add(lot);
                var spread = allowed.OrderBy(_ => rng.Next()).Take(rng.Next(1, Math.Min(3, allowed.Length) + 1)).ToList();
                lotWh[lot] = spread.Select(w => (w.Id, rng.NextDouble() < 0.05 ? 0 : rng.Next(scale / 4, scale * 6))).ToList();
                total += lotWh[lot].Sum(x => x.Qty);
            }
            var lowForced = rng.NextDouble() < 0.12;
            var min = lowForced ? (int)(total * (1.1 + rng.NextDouble() * 0.5)) + 5 : (int)(total * (0.15 + rng.NextDouble() * 0.30));
            min = Math.Max(10, min);
            p.MinLevel = min;
            p.MaxLevel = (int)(min * (2.5 + rng.NextDouble()));
            p.AvgDailyDemand = Math.Round(p.MaxLevel / (double)rng.Next(30, 60), 1);
        }
        db.Lots.AddRange(lots);
        await db.SaveChangesAsync();
        foreach (var (lot, spread) in lotWh)
            foreach (var (wh, qty) in spread)
                levels.Add(new StockLevel { WarehouseId = wh, ProductId = lot.ProductId, LotId = lot.Id, Quantity = qty });
        db.StockLevels.AddRange(levels);

        // Movement history. Per stock level: one opening receipt covering all later shipments (so the ledger never dips below zero),
        // then recent receipts that sum to the final quantity. History therefore replays exactly to current stock.
        var moves = new List<StockMovement>();
        foreach (var l in levels)
        {
            var shipped = rng.Next(0, (int)(l.Quantity * 0.8) + 25);
            var actor = "System";
            if (shipped > 0)
            {
                moves.Add(M(now.AddDays(-rng.Next(38, 44)), MovementType.Receipt, l, shipped, $"PO-H{rng.Next(1000, 9999)}", actor));
                var parts = rng.Next(2, 6);
                var left = shipped;
                for (var i = 0; i < parts; i++)
                {
                    var q = i == parts - 1 ? left : Math.Min(left, Math.Max(1, shipped / parts + rng.Next(-2, 3)));
                    if (q <= 0) continue;
                    left -= q;
                    moves.Add(M(now.AddDays(-rng.NextDouble() * 29).AddMinutes(-rng.Next(0, 600)), MovementType.Shipment, l, -q, $"SO-H{rng.Next(1000, 9999)}", "System"));
                }
            }
            var rest = l.Quantity;
            while (rest > 0)
            {
                var q = rng.Next(0, 3) == 0 ? rest : Math.Max(1, rest / 2);
                rest -= q;
                moves.Add(M(now.AddDays(-rng.NextDouble() * 27), MovementType.Receipt, l, q, $"PO-H{rng.Next(1000, 9999)}", "System"));
            }
        }
        db.Movements.AddRange(moves.OrderBy(m => m.TimestampUtc));

        db.Excursions.AddRange(
            new TempExcursion { WarehouseId = warehouses[0].Id, Zone = "Cold Room 2", ReadingC = 9.4, RecordedUtc = now.AddDays(-3), AcknowledgedBy = "Marcus Chen", AcknowledgedUtc = now.AddDays(-3).AddMinutes(25) },
            new TempExcursion { WarehouseId = warehouses[2].Id, Zone = "Vaccine Freezer 1", ReadingC = -14.8, RecordedUtc = now.AddHours(-5) });
        await db.SaveChangesAsync();
    }

    private static StockMovement M(DateTime ts, MovementType type, StockLevel l, int qty, string reference, string actor) => new()
    {
        TimestampUtc = ts, Type = type, ProductId = l.ProductId, LotId = l.LotId, WarehouseId = l.WarehouseId, Quantity = qty, Reference = reference, Actor = actor
    };

    private async Task SeedOrdersAndPurchasingAsync()
    {
        var rng = new Random(77);
        var orders = sp.GetRequiredService<OrderService>();
        var purchasing = sp.GetRequiredService<PurchasingService>();
        var now = clock.GetUtcNow().UtcDateTime;

        await using var db = await factory.CreateDbContextAsync();
        var customers = await db.Customers.Select(c => c.Id).ToListAsync();
        var whs = await db.Warehouses.AsNoTracking().ToListAsync();
        var created = new List<(int Id, string Number)>();

        for (var i = 0; i < 36; i++)
        {
            var wh = whs[rng.Next(whs.Count)];
            var pool = await db.StockLevels.AsNoTracking()
                .Where(s => s.WarehouseId == wh.Id && s.Quantity - s.Allocated > 20 && s.Lot!.ExpirationDate > DateOnly.FromDateTime(now))
                .OrderBy(s => s.Id).Select(s => new { s.ProductId, A = s.Quantity - s.Allocated }).Take(120).ToListAsync();
            if (pool.Count < 4) continue;
            var lines = new List<NewOrderLine>();
            foreach (var p in pool.OrderBy(_ => rng.Next()).Take(rng.Next(1, 5)))
            {
                var qty = rng.Next(2, Math.Max(3, p.A / 4));
                if (rng.NextDouble() < 0.07)   // occasional order for more than the warehouse holds -> shortfall
                    qty = await db.StockLevels.Where(s => s.WarehouseId == wh.Id && s.ProductId == p.ProductId).SumAsync(s => s.Quantity - s.Allocated) + rng.Next(3, 30);
                lines.Add(new NewOrderLine(p.ProductId, qty));
            }
            var res = await orders.CreateAsync(new NewOrderRequest(customers[rng.Next(customers.Count)], wh.Id, lines), "Seed");
            if (res.Ok) created.Add((res.Value!.Id, res.Value.Number));
        }

        var shippedAges = new Dictionary<string, int>();
        for (var i = 0; i < created.Count; i++)
        {
            var (id, number) = created[i];
            if (i < 24)
            {
                await orders.AdvanceAsync(id, "Seed"); await orders.AdvanceAsync(id, "Seed");
                shippedAges[number] = rng.Next(1, 28);
            }
            else if (i < 30) await orders.AdvanceAsync(id, "Seed");
        }
        foreach (var (id, number) in created)
        {
            var age = shippedAges.TryGetValue(number, out var a) ? a : rng.Next(0, 3);
            var when = now.AddDays(-age).AddMinutes(-rng.Next(0, 720));
            await db.Orders.Where(o => o.Id == id).ExecuteUpdateAsync(s => s.SetProperty(o => o.CreatedUtc, when).SetProperty(o => o.UpdatedUtc, when.AddHours(3)));
            await db.Movements.Where(m => m.Reference == number).ExecuteUpdateAsync(s => s.SetProperty(m => m.TimestampUtc, when.AddHours(3)));
        }

        // Purchase orders from reorder suggestions, one per supplier.
        var suggestions = await purchasing.GetSuggestionsAsync();
        var coldWh = whs.First(w => w.ColdChainCapable).Id;
        var warm = whs.First(w => !w.ColdChainCapable).Id;
        var bySupplier = suggestions.GroupBy(s => s.SupplierId).Take(6).ToList();
        var poNumbers = new List<string>();
        foreach (var g in bySupplier)
        {
            var lines = g.Take(4).Select(s => new NewOrderLine(s.ProductId, s.SuggestedQty)).ToList();
            var dest = g.Any(s => s.ColdChain) ? coldWh : warm;
            var r = await purchasing.CreatePurchaseOrdersAsync(lines, dest, "Seed");
            if (r.Ok) poNumbers.AddRange(r.Value!);
        }
        var pos = await purchasing.ListAsync();
        if (pos.Count > 0) await purchasing.ReceiveAsync(pos[^1].Id, "Seed");
        if (pos.Count > 1)
        {
            var po = pos[^2];
            await purchasing.ReceiveAsync(po.Id, "Seed", [new ReceiptLine(po.Lines[0].LineId, Math.Max(1, po.Lines[0].Ordered / 2))]);
        }
        var i2 = 0;
        foreach (var po in await purchasing.ListAsync())
        {
            var when = now.AddDays(-(2 + i2++ * 2));
            var number = po.Number;
            await db.PurchaseOrders.Where(p => p.Id == po.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.CreatedUtc, when));
            await db.Movements.Where(m => m.Reference == number).ExecuteUpdateAsync(s => s.SetProperty(m => m.TimestampUtc, when.AddDays(1)));
        }
    }
}
