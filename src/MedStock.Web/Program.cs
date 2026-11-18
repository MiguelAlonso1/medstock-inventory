using System.Security.Claims;
using MedStock.Web.Components;
using MedStock.Web.Data;
using MedStock.Web.Endpoints;
using MedStock.Web.Services;
using MedStock.Web.Services.Assistant;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddMudServices();
builder.Services.AddHealthChecks();
builder.Services.AddCascadingAuthenticationState();

// SQLite in App_Data (created + seeded on startup). The connection string is resolved lazily so tests can override it.
builder.Services.AddDbContextFactory<MedStockDb>((sp, o) =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var env = sp.GetRequiredService<IWebHostEnvironment>();
    var cs = cfg.GetConnectionString("Default");
    if (string.IsNullOrWhiteSpace(cs))
    {
        var dir = Path.Combine(env.ContentRootPath, "App_Data");
        Directory.CreateDirectory(dir);
        cs = $"Data Source={Path.Combine(dir, "medstock.db")};Default Timeout=30";
    }
    o.UseSqlite(cs, s => s.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery));
});
builder.Services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<MedStockDb>>().CreateDbContext());

builder.Services.AddIdentityCore<AppUser>(o =>
    {
        o.Password.RequiredLength = 8;
        o.Lockout.MaxFailedAccessAttempts = 8;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<MedStockDb>()
    .AddSignInManager()
    .AddClaimsPrincipalFactory<AppClaimsFactory>();
builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies(o => o.ApplicationCookie?.Configure(c =>
{
    c.LoginPath = "/login";
    c.AccessDeniedPath = "/access-denied";
    c.Cookie.Name = "medstock.auth";
    c.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    c.ExpireTimeSpan = TimeSpan.FromHours(8);
    // API callers get status codes, not redirects.
    c.Events.OnRedirectToLogin = ctx =>
    {
        if (ctx.Request.Path.StartsWithSegments("/api")) ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        else ctx.Response.Redirect(ctx.RedirectUri);
        return Task.CompletedTask;
    };
    c.Events.OnRedirectToAccessDenied = ctx =>
    {
        if (ctx.Request.Path.StartsWithSegments("/api")) ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        else ctx.Response.Redirect(ctx.RedirectUri);
        return Task.CompletedTask;
    };
}));
builder.Services.AddAuthorization();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<InventoryEventHub>();
builder.Services.AddSingleton<WriteGate>();
builder.Services.AddSingleton<SimulationControl>();
builder.Services.AddSingleton<OrderService>();
builder.Services.AddSingleton<PurchasingService>();
builder.Services.AddSingleton<TransferService>();
builder.Services.AddSingleton<MasterDataService>();
builder.Services.AddSingleton<InventoryQueries>();
builder.Services.AddSingleton<DemoSeeder>();
// "Ask MedStock": Claude over plain HttpClient. Without Claude:ApiKey the AI features show a disabled state.
builder.Services.AddSingleton(sp => new ClaudeClient(
    new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromSeconds(120) },
    sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<AssistantTools>();
builder.Services.AddSingleton<AssistantLimiter>();
builder.Services.AddSingleton<AssistantService>();
builder.Services.AddHostedService<SimulationWorker>();

// Behind IIS/ANCM (out-of-process) the original scheme arrives in X-Forwarded-Proto from 127.0.0.1.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
    o.KnownProxies.Add(System.Net.IPAddress.Loopback);
    o.KnownProxies.Add(System.Net.IPAddress.IPv6Loopback);
});
builder.Services.AddHsts(o =>
{
    o.MaxAge = TimeSpan.FromDays(30);
    o.IncludeSubDomains = false;
    o.Preload = false;
});

var app = builder.Build();

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    // HTTP -> HTTPS is done here (an IIS URL Rewrite rule is not reliable on shared hosting). ACME HTTP-01 stays on plain HTTP.
    // Opt-in (Https:Enforce=true) so the app also works on a plain-HTTP hostname until a certificate is installed.
    var enforceHttps = app.Configuration.GetValue("Https:Enforce", false);
    app.Use((ctx, next) =>
    {
        var req = ctx.Request;
        if (enforceHttps && !req.IsHttps && !req.Path.StartsWithSegments("/.well-known/acme-challenge", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.StatusCode = StatusCodes.Status301MovedPermanently;
            ctx.Response.Headers.Location = "https://" + req.Host + req.PathBase + req.Path + req.QueryString;
            return Task.CompletedTask;
        }
        return next();
    });
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    if (enforceHttps) app.UseHsts();
}
// Friendly 404 page for browsers; API callers keep the plain status code.
app.UseWhen(ctx => !ctx.Request.Path.StartsWithSegments("/api"),
    b => b.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true));
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapHealthChecks("/health").AllowAnonymous();
app.MapThemeEndpoint();
app.MapAccountEndpoints();
app.MapApiEndpoints();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

await app.Services.GetRequiredService<DemoSeeder>().InitializeAsync();

app.Run();

// Exposed for WebApplicationFactory in tests/MedStock.Web.Tests.
public partial class Program { }

/// <summary>Adds the display name as a claim so services can record who did what.</summary>
public sealed class AppClaimsFactory(UserManager<AppUser> um, RoleManager<IdentityRole> rm, Microsoft.Extensions.Options.IOptions<IdentityOptions> o)
    : UserClaimsPrincipalFactory<AppUser, IdentityRole>(um, rm, o)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AppUser user)
    {
        var id = await base.GenerateClaimsAsync(user);
        id.AddClaim(new Claim("display_name", string.IsNullOrEmpty(user.DisplayName) ? user.UserName ?? "user" : user.DisplayName));
        return id;
    }
}
