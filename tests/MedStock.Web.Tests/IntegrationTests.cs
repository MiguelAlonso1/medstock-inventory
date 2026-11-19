using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using MedStock.Domain;
using MedStock.Web.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MedStock.Web.Tests;

/// <summary>Boots the real app against a throw-away SQLite file, simulator off.</summary>
public sealed class MedStockFactory : WebApplicationFactory<Program>
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"medstock-test-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = $"Data Source={_db}",
            ["Simulation:Enabled"] = "false"
        }));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _db, _db + "-wal", _db + "-shm" })
            try { File.Delete(f); } catch (IOException) { }
    }

    public HttpClient NewClient() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>Signs in through the real one-click demo form post (antiforgery token + cookie included).</summary>
    public async Task<HttpClient> LoginAsync(string role)
    {
        var client = NewClient();
        var html = await client.GetStringAsync("/login");
        var token = Regex.Match(html, "__RequestVerificationToken[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(token));
        var res = await client.PostAsync("/account/demo", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["role"] = role, ["returnUrl"] = "/", ["__RequestVerificationToken"] = token
        }));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        return client;
    }

    public async Task<T> QueryAsync<T>(Func<MedStockDb, Task<T>> q)
    {
        await using var scope = Services.CreateAsyncScope();
        var f = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MedStockDb>>();
        await using var db = await f.CreateDbContextAsync();
        return await q(db);
    }
}

[CollectionDefinition("app")]
public class AppCollection : ICollectionFixture<MedStockFactory>;

[Collection("app")]
public class AuthTests(MedStockFactory app)
{
    [Fact]
    public async Task Health_IsPublic() =>
        Assert.Equal(HttpStatusCode.OK, (await app.NewClient().GetAsync("/health")).StatusCode);

    [Fact]
    public async Task LoginPage_IsPublic_AndOffersAllDemoRoles()
    {
        var html = await app.NewClient().GetStringAsync("/login");
        foreach (var r in Roles.All) Assert.Contains($"value=\"{r}\"", html);
    }

    [Fact]
    public async Task Overview_RedirectsAnonymousToLogin()
    {
        var res = await app.NewClient().GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Contains("/login", res.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Api_Returns401WhenAnonymous() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.NewClient().GetAsync("/api/orders/1")).StatusCode);

    [Fact]
    public async Task DemoPost_WithoutAntiforgeryToken_IsRejected()
    {
        var res = await app.NewClient().PostAsync("/account/demo", new FormUrlEncodedContent(new Dictionary<string, string> { ["role"] = "Admin" }));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Warehouse Manager")]
    [InlineData("Picker")]
    [InlineData("Viewer")]
    public async Task DemoLogin_ForEveryRole_OpensTheOverview(string role)
    {
        var client = await app.LoginAsync(role);
        var res = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("Overview", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PasswordLogin_Works_AndBadPasswordIsRejected()
    {
        var client = app.NewClient();
        string Token(string html) => Regex.Match(html, "__RequestVerificationToken[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var token = Token(await client.GetStringAsync("/login"));
        var bad = await client.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["email"] = "viewer@medstock.demo", ["password"] = "nope", ["__RequestVerificationToken"] = token }));
        Assert.Contains("error=1", bad.Headers.Location?.OriginalString);
        var good = await client.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["email"] = "viewer@medstock.demo", ["password"] = DemoUsers.Password, ["returnUrl"] = "//evil.example", ["__RequestVerificationToken"] = token }));
        Assert.Equal("/", good.Headers.Location?.OriginalString);   // open redirect neutralised
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/inventory")).StatusCode);
    }

    [Fact]
    public async Task Admin_Page_IsForbiddenForNonAdmins()
    {
        var manager = await app.LoginAsync(Roles.WarehouseManager);
        var res = await manager.GetAsync("/admin");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Contains("access-denied", res.Headers.Location?.OriginalString);
        var admin = await app.LoginAsync(Roles.Admin);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/admin")).StatusCode);
    }

    [Fact]
    public async Task Logout_EndsTheSession()
    {
        var client = await app.LoginAsync(Roles.Viewer);
        await client.PostAsync("/account/logout", null);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task ThemeEndpoint_SetsCookie_AndRejectsOpenRedirect()
    {
        var res = await app.NewClient().GetAsync("/theme/light?returnUrl=//evil.example");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("/", res.Headers.Location?.OriginalString);
        Assert.Contains(res.Headers.GetValues("Set-Cookie"), c => c.StartsWith("theme=light"));
    }

    [Fact]
    public async Task ThemeToggle_IsPlainLinkOutsideBlazorNavigation()
    {
        var html = await (await app.NewClient().GetAsync("/login")).Content.ReadAsStringAsync();
        var i = html.IndexOf("theme/", StringComparison.Ordinal);
        Assert.True(i > 0);
        var tag = html[html.LastIndexOf('<', i)..html.IndexOf('>', i)];
        Assert.Contains("target=\"_top\"", tag);
    }
}

[Collection("app")]
public class OrderAllocationTests(MedStockFactory app)
{
    private sealed record AllocDto(string LotNumber, DateOnly Expiry, int Quantity);
    private sealed record LineDto(int ProductId, int Ordered, int Allocated, List<AllocDto> Lots);
    private sealed record OrderDto(int Id, string Number, string Status, int TotalOrdered, int TotalAllocated, List<LineDto> Lines);
    private sealed record StockPick(int WarehouseId, int ProductId, int FirstLotAvailable);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    /// <summary>A product with at least two allocatable lots at one non-cold warehouse.</summary>
    private async Task<StockPick> FindMultiLotProductAsync()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return await app.QueryAsync(async db =>
        {
            var groups = await db.StockLevels.AsNoTracking()
                .Where(s => s.Quantity - s.Allocated > 5 && s.Lot!.ExpirationDate > today && !s.Product!.ColdChain)
                .Select(s => new { s.WarehouseId, s.ProductId, s.Lot!.ExpirationDate, A = s.Quantity - s.Allocated }).ToListAsync();
            var g = groups.GroupBy(x => (x.WarehouseId, x.ProductId)).First(x => x.Count() >= 2);
            return new StockPick(g.Key.WarehouseId, g.Key.ProductId, g.OrderBy(x => x.ExpirationDate).First().A);
        });
    }

    private async Task<int> CustomerIdAsync() => await app.QueryAsync(db => db.Customers.Select(c => c.Id).FirstAsync());

    [Fact]
    public async Task CreateOrder_AllocatesEarliestExpiryFirst_AcrossLots()
    {
        var pick = await FindMultiLotProductAsync();
        var client = await app.LoginAsync(Roles.WarehouseManager);
        var res = await client.PostAsJsonAsync("/api/orders", new
        {
            customerId = await CustomerIdAsync(), warehouseId = pick.WarehouseId,
            lines = new[] { new { productId = pick.ProductId, quantity = pick.FirstLotAvailable + 1 } }
        });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var order = (await res.Content.ReadFromJsonAsync<OrderDto>(Json))!;
        var line = Assert.Single(order.Lines);
        Assert.Equal(line.Ordered, line.Allocated);
        Assert.True(line.Lots.Count >= 2, "order should span at least two lots");
        Assert.Equal(line.Lots.OrderBy(l => l.Expiry).Select(l => l.LotNumber), line.Lots.Select(l => l.LotNumber));
        Assert.Equal(pick.FirstLotAvailable, line.Lots[0].Quantity);   // first (earliest) lot is drained completely first
        Assert.Equal("Allocated", order.Status);
    }

    [Fact]
    public async Task CreateOrder_ReportsShortfall_WhenStockInsufficient()
    {
        var pick = await FindMultiLotProductAsync();
        var client = await app.LoginAsync(Roles.WarehouseManager);
        var res = await client.PostAsJsonAsync("/api/orders", new
        {
            customerId = await CustomerIdAsync(), warehouseId = pick.WarehouseId,
            lines = new[] { new { productId = pick.ProductId, quantity = 10_000_000 } }
        });
        var order = (await res.Content.ReadFromJsonAsync<OrderDto>(Json))!;
        Assert.True(order.TotalAllocated < order.TotalOrdered);
    }

    [Fact]
    public async Task CreateOrder_ValidatesInput()
    {
        var client = await app.LoginAsync(Roles.WarehouseManager);
        var res = await client.PostAsJsonAsync("/api/orders", new { customerId = 0, warehouseId = 0, lines = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Theory]
    [InlineData("Viewer")]
    [InlineData("Picker")]
    public async Task CreateOrder_IsForbiddenForReadOnlyAndPickerRoles(string role)
    {
        var client = await app.LoginAsync(role);
        var res = await client.PostAsJsonAsync("/api/orders", new { customerId = 1, warehouseId = 1, lines = new[] { new { productId = 1, quantity = 1 } } });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task PickerCanPickAndShip_StockDrops_AndLedgerStaysConsistent()
    {
        var pick = await FindMultiLotProductAsync();
        var manager = await app.LoginAsync(Roles.WarehouseManager);
        const int qty = 3;
        Task<int> OnHand() => app.QueryAsync(db => db.StockLevels.Where(s => s.WarehouseId == pick.WarehouseId && s.ProductId == pick.ProductId).SumAsync(s => s.Quantity));
        var before = await OnHand();
        var created = await manager.PostAsJsonAsync("/api/orders", new
        {
            customerId = await CustomerIdAsync(), warehouseId = pick.WarehouseId, lines = new[] { new { productId = pick.ProductId, quantity = qty } }
        });
        var order = (await created.Content.ReadFromJsonAsync<OrderDto>(Json))!;
        Assert.Equal(before, await OnHand());   // reserved, not yet shipped

        var picker = await app.LoginAsync(Roles.Picker);
        var picking = (await (await picker.PostAsync($"/api/orders/{order.Id}/advance", null)).Content.ReadFromJsonAsync<OrderDto>(Json))!;
        Assert.Equal("Picking", picking.Status);
        var shipped = (await (await picker.PostAsync($"/api/orders/{order.Id}/advance", null)).Content.ReadFromJsonAsync<OrderDto>(Json))!;
        Assert.Equal("Shipped", shipped.Status);
        Assert.Equal(before - qty, await OnHand());
        Assert.Equal(HttpStatusCode.BadRequest, (await picker.PostAsync($"/api/orders/{order.Id}/advance", null)).StatusCode);

        var ledger = await (await manager.GetAsync("/api/ledger/verify")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, ledger.GetProperty("violations").GetInt32());
        Assert.Equal(0, ledger.GetProperty("mismatches").GetInt32());
    }
}

[Collection("app")]
public class PurchaseOrderReceiveTests(MedStockFactory app)
{
    [Fact]
    public async Task ReceivePo_AddsLotsAndStock_AndClosesThePo()
    {
        var po = await app.QueryAsync(db => db.PurchaseOrders.Include(p => p.Lines).AsNoTracking().FirstAsync(p => p.Status == PurchaseOrderStatus.Ordered));
        var line = po.Lines[0];
        Task<int> OnHand() => app.QueryAsync(db => db.StockLevels.Where(s => s.WarehouseId == po.WarehouseId && s.ProductId == line.ProductId).SumAsync(s => s.Quantity));
        var before = await OnHand();

        var client = await app.LoginAsync(Roles.WarehouseManager);
        var res = await client.PostAsJsonAsync($"/api/purchase-orders/{po.Id}/receive", new { });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(before + line.QtyOrdered, await OnHand());
        var reloaded = await app.QueryAsync(db => db.PurchaseOrders.AsNoTracking().FirstAsync(p => p.Id == po.Id));
        Assert.Equal(PurchaseOrderStatus.Received, reloaded.Status);
        var movement = await app.QueryAsync(db => db.Movements.AsNoTracking().FirstAsync(m => m.Reference == po.Number && m.ProductId == line.ProductId));
        Assert.Equal(MovementType.Receipt, movement.Type);

        // A closed PO cannot be received twice.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/purchase-orders/{po.Id}/receive", new { })).StatusCode);
    }

    [Fact]
    public async Task ReceivePo_PartialThenRemainder_ReachesReceived()
    {
        var po = await app.QueryAsync(db => db.PurchaseOrders.Include(p => p.Lines).AsNoTracking().FirstAsync(p => p.Status == PurchaseOrderStatus.Ordered && p.Lines.Any(l => l.QtyOrdered >= 2)));
        var line = po.Lines.First(l => l.QtyOrdered >= 2);
        var client = await app.LoginAsync(Roles.WarehouseManager);
        var half = line.QtyOrdered / 2;
        var r1 = await client.PostAsJsonAsync($"/api/purchase-orders/{po.Id}/receive", new { lines = new[] { new { lineId = line.Id, quantity = half } } });
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        Assert.Equal(PurchaseOrderStatus.PartiallyReceived, (await app.QueryAsync(db => db.PurchaseOrders.AsNoTracking().FirstAsync(p => p.Id == po.Id))).Status);
        // over-receipt on the same line is rejected
        var over = await client.PostAsJsonAsync($"/api/purchase-orders/{po.Id}/receive", new { lines = new[] { new { lineId = line.Id, quantity = line.QtyOrdered } } });
        Assert.Equal(HttpStatusCode.BadRequest, over.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/purchase-orders/{po.Id}/receive", new { })).StatusCode);
        Assert.Equal(PurchaseOrderStatus.Received, (await app.QueryAsync(db => db.PurchaseOrders.AsNoTracking().FirstAsync(p => p.Id == po.Id))).Status);
    }

    [Theory]
    [InlineData("Viewer")]
    [InlineData("Picker")]
    public async Task ReceivePo_IsForbiddenWithoutManagerRole(string role)
    {
        var client = await app.LoginAsync(role);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/purchase-orders/1/receive", new { })).StatusCode);
    }
}

[Collection("app")]
public class SeedAndAdminTests(MedStockFactory app)
{
    [Fact]
    public async Task Seed_ProducesCatalogueAndConsistentLedger()
    {
        Assert.Equal(300, await app.QueryAsync(db => db.Products.CountAsync()));
        Assert.Equal(4, await app.QueryAsync(db => db.Warehouses.CountAsync()));
        Assert.True(await app.QueryAsync(db => db.Movements.CountAsync()) > 1000);
        Assert.True(await app.QueryAsync(db => db.StockLevels.AllAsync(s => s.Quantity >= 0 && s.Allocated >= 0 && s.Allocated <= s.Quantity)));
        var client = await app.LoginAsync(Roles.Viewer);
        var ledger = await (await client.GetAsync("/api/ledger/verify")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, ledger.GetProperty("violations").GetInt32());
        Assert.Equal(0, ledger.GetProperty("mismatches").GetInt32());
    }

    [Fact]
    public async Task ColdChainOrders_AreRejectedAtNonColdWarehouses()
    {
        var (pid, wid, cid) = await app.QueryAsync(async db => (
            (await db.Products.FirstAsync(p => p.ColdChain)).Id,
            (await db.Warehouses.FirstAsync(w => !w.ColdChainCapable)).Id,
            (await db.Customers.FirstAsync()).Id));
        var client = await app.LoginAsync(Roles.WarehouseManager);
        var res = await client.PostAsJsonAsync("/api/orders", new { customerId = cid, warehouseId = wid, lines = new[] { new { productId = pid, quantity = 1 } } });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("cold-chain", await res.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("Viewer")]
    [InlineData("Warehouse Manager")]
    public async Task ResetDemoData_IsAdminOnly(string role)
    {
        var client = await app.LoginAsync(role);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/admin/reset-demo", null)).StatusCode);
    }

    [Fact]
    public async Task ResetDemoData_AsAdmin_RestoresTheSeed()
    {
        var admin = await app.LoginAsync(Roles.Admin);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync("/api/admin/reset-demo", null)).StatusCode);
        Assert.Equal(300, await app.QueryAsync(db => db.Products.CountAsync()));
        Assert.True(await app.QueryAsync(db => db.PurchaseOrders.AnyAsync(p => p.Status == PurchaseOrderStatus.Ordered)));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/inventory")).StatusCode);   // users survive the reset
    }
}

public class HttpsRedirectTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private sealed class LoopbackFilter : IStartupFilter
    {
        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) => app =>
        {
            app.Use((ctx, n) => { ctx.Connection.RemoteIpAddress = IPAddress.Loopback; return n(); });
            next(app);
        };
    }

    private HttpClient Client(bool enforce = true) => factory
        .WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = $"Data Source={Path.Combine(Path.GetTempPath(), "medstock-https-test.db")}",
                ["Simulation:Enabled"] = "false",
                ["Https:Enforce"] = enforce ? "true" : "false"
            }));
            b.ConfigureServices(s => s.AddTransient<IStartupFilter, LoopbackFilter>());
        })
        .CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("http://inventory.alonsomiguel.com") });

    [Fact]
    public async Task Http_RedirectsToHttps_PreservingPathAndQuery()
    {
        var res = await Client().GetAsync("/inventory?x=1");
        Assert.Equal(HttpStatusCode.MovedPermanently, res.StatusCode);
        Assert.Equal("https://inventory.alonsomiguel.com/inventory?x=1", res.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Http_IsServedWhenEnforcementIsOff()
    {
        var res = await Client(enforce: false).GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task ForwardedProtoHttps_IsNotRedirected()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/health");
        req.Headers.Add("X-Forwarded-Proto", "https");
        Assert.Equal(HttpStatusCode.OK, (await Client().SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task AcmeChallengeOverHttp_IsNotRedirected()
    {
        var res = await Client().GetAsync("/.well-known/acme-challenge/x");
        Assert.Null(res.Headers.Location);
    }
}
