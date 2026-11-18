using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MedStock.Web.Data;
using MedStock.Web.Services;
using MedStock.Web.Services.Assistant;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MedStock.Web.Tests;

/// <summary>Scripted Messages API: returns queued SSE bodies and records every request. No live calls.</summary>
internal sealed class FakeClaude(Func<int, JsonObject, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<JsonObject> Requests { get; } = [];
    public List<HttpRequestMessage> Raw { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
        Requests.Add(body);
        Raw.Add(request);
        return respond(Requests.Count, body);
    }

    public static HttpResponseMessage Sse(string stop, params string[] blocks)
    {
        var sb = new StringBuilder();
        sb.Append("event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"usage\":{\"input_tokens\":10}}}\n\n");
        for (var i = 0; i < blocks.Length; i++) sb.Append(blocks[i].Replace("#", i.ToString()));
        sb.Append($"event: message_delta\ndata: {{\"type\":\"message_delta\",\"delta\":{{\"stop_reason\":\"{stop}\"}},\"usage\":{{\"output_tokens\":5}}}}\n\n");
        sb.Append("event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sb.ToString(), Encoding.UTF8, "text/event-stream") };
    }

    public static string Text(params string[] parts) =>
        "data: {\"type\":\"content_block_start\",\"index\":#,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n" +
        string.Concat(parts.Select(p => "data: {\"type\":\"content_block_delta\",\"index\":#,\"delta\":{\"type\":\"text_delta\",\"text\":" + JsonSerializer.Serialize(p) + "}}\n\n")) +
        "data: {\"type\":\"content_block_stop\",\"index\":#}\n\n";

    public static string ToolUse(string id, string name, params string[] jsonParts) =>
        "data: {\"type\":\"content_block_start\",\"index\":#,\"content_block\":{\"type\":\"tool_use\",\"id\":\"" + id + "\",\"name\":\"" + name + "\",\"input\":{}}}\n\n" +
        string.Concat(jsonParts.Select(p => "data: {\"type\":\"content_block_delta\",\"index\":#,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":" + JsonSerializer.Serialize(p) + "}}\n\n")) +
        "data: {\"type\":\"content_block_stop\",\"index\":#}\n\n";

    public static HttpResponseMessage Error(HttpStatusCode code) =>
        new(code) { Content = new StringContent("{\"type\":\"error\",\"error\":{\"type\":\"x\",\"message\":\"secret detail\"}}", Encoding.UTF8, "application/json") };
}

internal sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan t) => _now += t;
}

public class AssistantTests(MedStockFactory app) : IClassFixture<MedStockFactory>
{
    private static IConfiguration Cfg(string? key = "test-key", Dictionary<string, string?>? extra = null)
    {
        var d = new Dictionary<string, string?> { ["Claude:ApiKey"] = key };
        if (extra is not null) foreach (var kv in extra) d[kv.Key] = kv.Value;
        return new ConfigurationBuilder().AddInMemoryCollection(d).Build();
    }

    private (AssistantService Svc, FakeClaude Fake, AssistantLimiter Limiter) Make(Func<int, JsonObject, HttpResponseMessage> respond, IConfiguration? cfg = null, TimeProvider? clock = null)
    {
        cfg ??= Cfg();
        var fake = new FakeClaude(respond);
        var claude = new ClaudeClient(new HttpClient(fake), cfg);
        var limiter = new AssistantLimiter(clock ?? TimeProvider.System, cfg);
        var tools = new AssistantTools(app.Services.GetRequiredService<InventoryQueries>(), app.Services.GetRequiredService<OrderService>(),
            app.Services.GetRequiredService<PurchasingService>(), TimeProvider.System);
        return (new AssistantService(claude, tools, limiter, cfg), fake, limiter);
    }

    private static Task Ignore(AssistantEvent _) => Task.CompletedTask;
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();

    // ---------------------------------------------------------------- tool-use loop

    [Fact]
    public async Task ToolLoop_ToolUse_ToolResult_FinalText_Streams()
    {
        var (svc, fake, _) = Make((n, _) => n == 1
            ? FakeClaude.Sse("tool_use", FakeClaude.Text("Checking."), FakeClaude.ToolUse("toolu_1", "get_kpis", "{", "}"))
            : FakeClaude.Sse("end_turn", FakeClaude.Text("There are ", "12 SKUs.")));
        var events = new List<AssistantEvent>();
        var res = await svc.AskAsync("u1", [], "How many SKUs?", e => { events.Add(e); return Task.CompletedTask; });

        Assert.True(res.Ok);
        Assert.Equal("There are 12 SKUs.", res.Text);
        Assert.Equal(["get_kpis"], res.Tools);
        Assert.Equal(2, fake.Requests.Count);
        // streamed in pieces, narration before the tool call was discarded
        Assert.Contains(events, e => e is { Kind: AssistantEventKind.Reset });
        Assert.Contains(events, e => e is { Kind: AssistantEventKind.ToolStart, Text: "get_kpis" });
        Assert.Equal(["There are ", "12 SKUs."], events.Where(e => e.Kind == AssistantEventKind.Text).Skip(1).Select(e => e.Text));

        // request shape: model, headers, caching, tools, no thinking budget
        var r1 = fake.Requests[0];
        Assert.Equal("claude-haiku-5-5", r1["model"]!.GetValue<string>());
        Assert.True(r1["stream"]!.GetValue<bool>());
        Assert.Equal("ephemeral", r1["system"]![0]!["cache_control"]!["type"]!.GetValue<string>());
        Assert.Equal("ephemeral", r1["tools"]!.AsArray().Last()!["cache_control"]!["type"]!.GetValue<string>());
        Assert.Equal(AssistantTools.Names.Count, r1["tools"]!.AsArray().Count);
        Assert.Equal("test-key", fake.Raw[0].Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", fake.Raw[0].Headers.GetValues("anthropic-version").Single());

        // second request carries assistant tool_use + user tool_result with the matching id
        var msgs = fake.Requests[1]["messages"]!.AsArray();
        Assert.Equal("assistant", msgs[1]!["role"]!.GetValue<string>());
        Assert.Equal("tool_use", msgs[1]!["content"]!.AsArray().Last()!["type"]!.GetValue<string>());
        var tr = msgs[2]!["content"]![0]!;
        Assert.Equal("tool_result", tr["type"]!.GetValue<string>());
        Assert.Equal("toolu_1", tr["tool_use_id"]!.GetValue<string>());
        Assert.Contains("inventory_value_usd", tr["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task IterationCap_ForcesFinalAnswerWithoutTools()
    {
        var (svc, fake, _) = Make((_, body) => body["tool_choice"] is null
            ? FakeClaude.Sse("tool_use", FakeClaude.ToolUse("t", "get_kpis"))
            : FakeClaude.Sse("end_turn", FakeClaude.Text("Done.")));
        var res = await svc.AskAsync("u2", [], "loop forever", Ignore);
        Assert.True(res.Ok);
        Assert.Equal("Done.", res.Text);
        Assert.Equal(AssistantService.MaxToolIterations + 1, fake.Requests.Count);
        Assert.Equal("none", fake.Requests[^1]["tool_choice"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task InvalidToolJson_ReturnsErrorResult_AndLoopContinues()
    {
        var (svc, fake, _) = Make((n, _) => n == 1
            ? FakeClaude.Sse("tool_use", FakeClaude.ToolUse("t1", "list_low_stock", "{\"limit\":"))
            : FakeClaude.Sse("end_turn", FakeClaude.Text("Sorry.")));
        var res = await svc.AskAsync("u3", [], "low stock?", Ignore);
        Assert.True(res.Ok);
        var tr = fake.Requests[1]["messages"]![2]!["content"]![0]!;
        Assert.True(tr["is_error"]!.GetValue<bool>());
        Assert.Contains("INVALID_JSON", tr["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Briefing_UsesToolsAndStreams()
    {
        var (svc, fake, _) = Make((n, _) => n == 1
            ? FakeClaude.Sse("tool_use", FakeClaude.ToolUse("a", "get_kpis"), FakeClaude.ToolUse("b", "list_alerts"))
            : FakeClaude.Sse("end_turn", FakeClaude.Text("- All calm.")));
        var res = await svc.BriefAsync("u4", Ignore);
        Assert.True(res.Ok);
        Assert.Equal(["get_kpis", "list_alerts"], res.Tools);
        Assert.Contains("operations briefing", fake.Requests[0]["messages"]![0]!["content"]!.GetValue<string>());
        Assert.Equal(2, fake.Requests[1]["messages"]![2]!["content"]!.AsArray().Count); // both results in one user message
    }

    // ---------------------------------------------------------------- limits, disabled state, errors

    [Fact]
    public async Task Disabled_WithoutApiKey_NeverCallsApi()
    {
        var (svc, fake, _) = Make((_, _) => throw new InvalidOperationException("must not be called"), Cfg(null));
        Assert.False(svc.Enabled);
        var res = await svc.AskAsync("u", [], "hello", Ignore);
        Assert.False(res.Ok);
        Assert.Contains("API key", res.Error);
        Assert.Empty(fake.Requests);
        Assert.False((await svc.BriefAsync("u", Ignore)).Ok);
    }

    [Fact]
    public async Task QuestionLimits_AndAnonymousUser()
    {
        var (svc, fake, _) = Make((_, _) => FakeClaude.Sse("end_turn", FakeClaude.Text("ok")));
        Assert.False((await svc.AskAsync("u", [], new string('x', AssistantService.MaxQuestionChars + 1), Ignore)).Ok);
        Assert.False((await svc.AskAsync("u", [], "   ", Ignore)).Ok);
        Assert.False((await svc.AskAsync("", [], "hi", Ignore)).Ok);
        Assert.True((await svc.AskAsync("u", [], new string('x', AssistantService.MaxQuestionChars), Ignore)).Ok);
        Assert.Single(fake.Requests);
    }

    [Fact]
    public async Task HistoryIsLimitedToLastTenTurns_AndAlternates()
    {
        var (svc, fake, _) = Make((_, _) => FakeClaude.Sse("end_turn", FakeClaude.Text("ok")));
        var history = Enumerable.Range(0, 40).Select(i => new ChatTurn(i % 2 == 0 ? "user" : "assistant", "turn " + i)).ToList();
        await svc.AskAsync("u", history, "latest", Ignore);
        var msgs = fake.Requests[0]["messages"]!.AsArray();
        Assert.True(msgs.Count <= AssistantService.MaxHistoryTurns + 1);
        Assert.Equal("user", msgs[0]!["role"]!.GetValue<string>());
        Assert.Equal("latest", msgs[^1]!["content"]!.GetValue<string>());
        for (var i = 1; i < msgs.Count; i++) Assert.NotEqual(msgs[i - 1]!["role"]!.GetValue<string>(), msgs[i]!["role"]!.GetValue<string>());
    }

    [Fact]
    public void PerUserHourlyLimit_AndWindowSlides()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero));
        var lim = new AssistantLimiter(clock, Cfg(extra: new() { ["Claude:HourlyLimit"] = "3", ["Claude:DailyLimit"] = "100" }));
        for (var i = 0; i < 3; i++) Assert.Null(lim.TryAcquire("a"));
        Assert.NotNull(lim.TryAcquire("a"));
        Assert.Null(lim.TryAcquire("b")); // other users unaffected
        clock.Advance(TimeSpan.FromMinutes(61));
        Assert.Null(lim.TryAcquire("a"));
    }

    [Fact]
    public void GlobalDailyCap_ResetsNextDay_AndDefaultsTo200()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero));
        Assert.Equal(200, new AssistantLimiter(clock, Cfg()).DailyLimit);
        Assert.Equal(20, new AssistantLimiter(clock, Cfg()).HourlyLimit);
        var lim = new AssistantLimiter(clock, Cfg(extra: new() { ["Claude:DailyLimit"] = "2", ["Claude:HourlyLimit"] = "50" }));
        Assert.Null(lim.TryAcquire("a"));
        Assert.Null(lim.TryAcquire("b"));
        Assert.Contains("daily", lim.TryAcquire("c"), StringComparison.OrdinalIgnoreCase);
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Null(lim.TryAcquire("c"));
    }

    [Fact]
    public async Task RateLimit_AppliesInService_WithoutCallingApi()
    {
        var (svc, fake, _) = Make((_, _) => FakeClaude.Sse("end_turn", FakeClaude.Text("ok")), Cfg(extra: new() { ["Claude:HourlyLimit"] = "1" }));
        Assert.True((await svc.AskAsync("same", [], "one", Ignore)).Ok);
        var second = await svc.AskAsync("same", [], "two", Ignore);
        Assert.False(second.Ok);
        Assert.Single(fake.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task ApiErrors_AreFriendly_AndDoNotLeakDetails(HttpStatusCode code)
    {
        var (svc, _, _) = Make((_, _) => FakeClaude.Error(code));
        var res = await svc.AskAsync("u", [], "hi", Ignore);
        Assert.False(res.Ok);
        Assert.DoesNotContain("secret detail", res.Error);
        Assert.DoesNotContain("test-key", res.Error);
    }

    [Fact]
    public async Task ConnectionFailure_IsFriendly()
    {
        var (svc, _, _) = Make((_, _) => throw new HttpRequestException("boom"));
        var res = await svc.AskAsync("u", [], "hi", Ignore);
        Assert.False(res.Ok);
        Assert.DoesNotContain("boom", res.Error);
    }

    // ---------------------------------------------------------------- tools: read-only and validated

    [Fact]
    public async Task AllTools_AreReadOnly_LedgerAndTablesUnchanged()
    {
        var tools = new AssistantTools(app.Services.GetRequiredService<InventoryQueries>(), app.Services.GetRequiredService<OrderService>(),
            app.Services.GetRequiredService<PurchasingService>(), TimeProvider.System);
        var sku = await app.QueryAsync(db => db.Products.OrderBy(p => p.Id).Select(p => p.Sku).FirstAsync());
        var orderNo = await app.QueryAsync(db => db.Orders.OrderBy(o => o.Id).Select(o => o.Number).FirstAsync());

        async Task<string> SnapshotAsync() => await app.QueryAsync(async db => string.Join("|",
            await db.Movements.CountAsync(), await db.Movements.SumAsync(m => (long)m.Quantity),
            await db.StockLevels.SumAsync(s => (long)s.Quantity), await db.StockLevels.SumAsync(s => (long)s.Allocated),
            await db.Orders.CountAsync(), await db.Orders.SumAsync(o => (long)o.Status), await db.OrderLines.SumAsync(l => (long)l.QtyAllocated),
            await db.PurchaseOrders.CountAsync(), await db.PurchaseOrderLines.SumAsync(l => (long)l.QtyReceived),
            await db.Excursions.CountAsync(e => e.AcknowledgedUtc != null), await db.Products.CountAsync(), await db.Lots.CountAsync()));

        var before = await SnapshotAsync();
        var calls = new (string Name, string Input)[]
        {
            ("search_products", "{\"query\":\"" + sku[..2] + "\"}"),
            ("get_stock_by_product", "{\"sku\":\"" + sku + "\"}"),
            ("get_stock_by_product", "{\"product_id\":1}"),
            ("list_expiring_lots", "{\"days\":90,\"cold_chain_only\":true}"),
            ("list_expiring_lots", "{}"),
            ("list_low_stock", "{}"),
            ("get_order", "{\"order\":\"" + orderNo + "\"}"),
            ("list_open_orders", "{\"shortfall_only\":true}"),
            ("list_open_orders", "{\"status\":\"shipped\"}"),
            ("list_reorder_suggestions", "{\"limit\":5}"),
            ("list_alerts", "{}"),
            ("get_kpis", "{}"),
        };
        foreach (var name in AssistantTools.Names) Assert.Contains(calls, c => c.Name == name); // every tool is exercised
        foreach (var (name, input) in calls)
        {
            var r = await tools.ExecuteAsync(name, Json(input));
            Assert.False(r.IsError, $"{name}: {r.Json}");
            Assert.True(r.Json.Length < 20_000, $"{name} result too large ({r.Json.Length})");
        }
        Assert.Equal(before, await SnapshotAsync());
        var (_, violations, mismatches) = await app.Services.GetRequiredService<InventoryQueries>().VerifyLedgerAsync();
        Assert.Equal(0, violations);
        Assert.Equal(0, mismatches);
    }

    [Fact]
    public async Task Tools_DeclareNoMutatingNames_AndUnknownToolIsRejected()
    {
        Assert.All(AssistantTools.Names, n => Assert.DoesNotMatch("create|update|delete|cancel|receive|advance|allocate|transfer|ack", n));
        Assert.All(AssistantTools.Definitions().Select(d => d!["name"]!.GetValue<string>()), n => Assert.Contains(n, AssistantTools.Names));
        var tools = new AssistantTools(app.Services.GetRequiredService<InventoryQueries>(), app.Services.GetRequiredService<OrderService>(),
            app.Services.GetRequiredService<PurchasingService>(), TimeProvider.System);
        foreach (var bad in new[] { "cancel_order", "acknowledge_excursion", "create_purchase_order", "" })
            Assert.True((await tools.ExecuteAsync(bad, Json("{}"))).IsError);
    }

    [Theory]
    [InlineData("search_products", "{}")]
    [InlineData("search_products", "{\"query\":\"a\"}")]
    [InlineData("search_products", "{\"query\":5}")]
    [InlineData("search_products", "{\"query\":\"abc\",\"limit\":1000}")]
    [InlineData("search_products", "{\"query\":\"abc\",\"limit\":\"ten\"}")]
    [InlineData("get_stock_by_product", "{}")]
    [InlineData("get_stock_by_product", "{\"product_id\":0}")]
    [InlineData("get_stock_by_product", "{\"product_id\":999999}")]
    [InlineData("get_stock_by_product", "{\"sku\":\"NOPE-NOT-A-SKU\"}")]
    [InlineData("list_expiring_lots", "{\"days\":0}")]
    [InlineData("list_expiring_lots", "{\"days\":5000}")]
    [InlineData("list_expiring_lots", "{\"cold_chain_only\":\"yes\"}")]
    [InlineData("list_low_stock", "{\"limit\":-1}")]
    [InlineData("get_order", "{}")]
    [InlineData("get_order", "{\"order\":\"XX-1\"}")]
    [InlineData("get_order", "{\"order\":\"SO-999999\"}")]
    [InlineData("list_open_orders", "{\"status\":\"deleted\"}")]
    [InlineData("list_reorder_suggestions", "{\"limit\":26}")]
    [InlineData("list_alerts", "{\"limit\":0}")]
    [InlineData("get_kpis", "[1]")]
    public async Task InvalidInput_IsRejectedWithErrorResult(string tool, string input)
    {
        var tools = new AssistantTools(app.Services.GetRequiredService<InventoryQueries>(), app.Services.GetRequiredService<OrderService>(),
            app.Services.GetRequiredService<PurchasingService>(), TimeProvider.System);
        var r = await tools.ExecuteAsync(tool, Json(input));
        Assert.True(r.IsError, r.Json);
        Assert.Contains("error", r.Json);
    }

    [Fact]
    public async Task RowCaps_AreEnforced()
    {
        var tools = new AssistantTools(app.Services.GetRequiredService<InventoryQueries>(), app.Services.GetRequiredService<OrderService>(),
            app.Services.GetRequiredService<PurchasingService>(), TimeProvider.System);
        var r = await tools.ExecuteAsync("list_expiring_lots", Json("{\"days\":90,\"limit\":" + AssistantTools.MaxRows + "}"));
        var lots = JsonDocument.Parse(r.Json).RootElement.GetProperty("lots");
        Assert.True(lots.GetArrayLength() <= AssistantTools.MaxRows);
        var def = await tools.ExecuteAsync("list_low_stock", Json("{}"));
        Assert.True(JsonDocument.Parse(def.Json).RootElement.GetProperty("products").GetArrayLength() <= 15);
    }

    // ---------------------------------------------------------------- server-side access

    [Fact]
    public async Task AssistantPage_RequiresSignIn_AndAnyRoleSeesDisabledState()
    {
        var anon = await app.NewClient().GetAsync("/assistant");
        Assert.Equal(HttpStatusCode.Redirect, anon.StatusCode);
        Assert.Contains("/login", anon.Headers.Location!.ToString());

        var viewer = await app.LoginAsync(Roles.Viewer);
        var page = await viewer.GetAsync("/assistant");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("AI features need an API key", await page.Content.ReadAsStringAsync());

        var overview = await (await app.LoginAsync(Roles.Picker)).GetStringAsync("/");
        Assert.Contains("AI briefing", overview);
    }
}
