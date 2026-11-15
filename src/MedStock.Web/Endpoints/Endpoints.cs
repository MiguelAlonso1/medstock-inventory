using System.Security.Claims;
using MedStock.Domain;
using MedStock.Web.Data;
using MedStock.Web.Services;
using MedStock.Web.Theme;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace MedStock.Web.Endpoints;

public static class AppEndpoints
{
    public static string SafeLocal(string? url) =>
        !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//") && !url.StartsWith("/\\") ? url : "/";

    public static string Actor(ClaimsPrincipal u) => u.FindFirstValue("display_name") ?? u.Identity?.Name ?? "unknown";

    /// <summary>GET /theme/{mode}?returnUrl=/ stores the explicit choice in a cookie, then redirects (local URLs only). No JavaScript.</summary>
    public static void MapThemeEndpoint(this WebApplication app) =>
        app.MapGet("/theme/{mode}", (string mode, string? returnUrl, HttpContext ctx) =>
        {
            if (mode is "light" or "dark")
            {
                ctx.Response.Cookies.Append(AppTheme.CookieName, mode, new CookieOptions
                {
                    Expires = DateTimeOffset.UtcNow.AddYears(1), Path = "/", SameSite = SameSiteMode.Lax,
                    Secure = ctx.Request.IsHttps, IsEssential = true
                });
            }
            return Results.LocalRedirect(SafeLocal(returnUrl));
        }).ExcludeFromDescription();

    public static void MapAccountEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/account").ExcludeFromDescription();

        // Plain HTML form posts (antiforgery protected). Failed attempts go back to /login with an error flag.
        g.MapPost("/login", async ([FromForm] string? email, [FromForm] string? password, [FromForm] string? returnUrl,
            UserManager<AppUser> users, SignInManager<AppUser> signIn) =>
        {
            var user = string.IsNullOrWhiteSpace(email) ? null : await users.FindByEmailAsync(email.Trim());
            if (user is not null && !string.IsNullOrEmpty(password))
            {
                var res = await signIn.PasswordSignInAsync(user, password, isPersistent: false, lockoutOnFailure: true);
                if (res.Succeeded) return Results.LocalRedirect(SafeLocal(returnUrl));
            }
            return Results.LocalRedirect("/login?error=1");
        }).AllowAnonymous();

        g.MapPost("/demo", async ([FromForm] string? role, [FromForm] string? returnUrl, UserManager<AppUser> users, SignInManager<AppUser> signIn, IConfiguration cfg) =>
        {
            if (!cfg.GetValue("Demo:OneClickLogin", true)) return Results.NotFound();
            var demo = DemoUsers.All.FirstOrDefault(d => string.Equals(d.Role, role, StringComparison.OrdinalIgnoreCase));
            var user = demo is null ? null : await users.FindByEmailAsync(demo.Email);
            if (user is null) return Results.LocalRedirect("/login?error=1");
            await signIn.SignInAsync(user, isPersistent: false);
            return Results.LocalRedirect(SafeLocal(returnUrl));
        }).AllowAnonymous();

        g.MapPost("/logout", async (SignInManager<AppUser> signIn) =>
        {
            await signIn.SignOutAsync();
            return Results.LocalRedirect("/login");
        }).AllowAnonymous();
    }

    public sealed record OrderLineDto(int ProductId, int Quantity);
    public sealed record CreateOrderDto(int CustomerId, int WarehouseId, List<OrderLineDto> Lines);
    public sealed record ReceiveLineDto(int LineId, int Quantity);
    public sealed record ReceiveDto(List<ReceiveLineDto>? Lines);

    public static void MapApiEndpoints(this WebApplication app)
    {
        // JSON API: cookie is SameSite=Lax and bodies are JSON, so form-style CSRF does not apply; antiforgery covers the HTML form posts.
        var api = app.MapGroup("/api").ExcludeFromDescription().DisableAntiforgery();

        api.MapGet("/orders/{id:int}", [Authorize] async (int id, OrderService svc) =>
            await svc.GetAsync(id) is { } o ? Results.Ok(o) : Results.NotFound()).RequireAuthorization();

        // Creates the order and FEFO-allocates stock at the chosen warehouse.
        api.MapPost("/orders", async (CreateOrderDto dto, OrderService svc, ClaimsPrincipal user) =>
        {
            var res = await svc.CreateAsync(new NewOrderRequest(dto.CustomerId, dto.WarehouseId, dto.Lines.Select(l => new NewOrderLine(l.ProductId, l.Quantity)).ToList()), Actor(user));
            return res.Ok ? Results.Created($"/api/orders/{res.Value!.Id}", res.Value) : Results.BadRequest(new { error = res.Error });
        }).RequireAuthorization(p => p.RequireRole(Roles.Admin, Roles.WarehouseManager)).DisableAntiforgery();

        api.MapPost("/orders/{id:int}/advance", async (int id, OrderService svc, ClaimsPrincipal user) =>
        {
            var res = await svc.AdvanceAsync(id, Actor(user));
            return res.Ok ? Results.Ok(res.Value) : Results.BadRequest(new { error = res.Error });
        }).RequireAuthorization(p => p.RequireRole(Roles.Admin, Roles.WarehouseManager, Roles.Picker)).DisableAntiforgery();

        api.MapPost("/purchase-orders/{id:int}/receive", async (int id, ReceiveDto? dto, PurchasingService svc, ClaimsPrincipal user) =>
        {
            var lines = dto?.Lines is { Count: > 0 } l ? l.Select(x => new ReceiptLine(x.LineId, x.Quantity)).ToList() : null;
            var res = await svc.ReceiveAsync(id, Actor(user), lines);
            return res.Ok ? Results.Ok(res.Value) : Results.BadRequest(new { error = res.Error });
        }).RequireAuthorization(p => p.RequireRole(Roles.Admin, Roles.WarehouseManager)).DisableAntiforgery();

        api.MapGet("/ledger/verify", async (InventoryQueries q) =>
        {
            var (m, v, mm) = await q.VerifyLedgerAsync();
            return Results.Ok(new { movements = m, violations = v, mismatches = mm });
        }).RequireAuthorization();

        api.MapPost("/admin/reset-demo", async (DemoSeeder seeder, ClaimsPrincipal user) =>
        {
            await seeder.ResetAsync(Actor(user));
            return Results.NoContent();
        }).RequireAuthorization(p => p.RequireRole(Roles.Admin)).DisableAntiforgery();
    }
}
