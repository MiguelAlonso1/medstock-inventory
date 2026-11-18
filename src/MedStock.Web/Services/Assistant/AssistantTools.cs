using System.Text.Json;
using System.Text.Json.Nodes;
using MedStock.Domain;

namespace MedStock.Web.Services.Assistant;

public sealed record ToolResult(string Json, bool IsError);

/// <summary>
/// Read-only tools the assistant may call. This class only depends on query methods of the existing services
/// (no write method is referenced anywhere), validates every input and caps every result.
/// </summary>
public sealed class AssistantTools(InventoryQueries inventory, OrderService orders, PurchasingService purchasing, TimeProvider clock)
{
    public const int MaxRows = 25;
    private static readonly JsonSerializerOptions Out = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public static readonly IReadOnlyList<string> Names =
        ["search_products", "get_stock_by_product", "list_expiring_lots", "list_low_stock", "get_order", "list_open_orders", "list_reorder_suggestions", "list_alerts", "get_kpis"];

    /// <summary>Tool definitions in the Messages API format. Order is fixed (prompt caching needs a stable prefix).</summary>
    public static JsonArray Definitions()
    {
        static JsonObject Tool(string name, string desc, JsonObject props, params string[] required) => new()
        {
            ["name"] = name,
            ["description"] = desc,
            ["input_schema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = props,
                ["required"] = new JsonArray(required.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
                ["additionalProperties"] = false
            }
        };
        static JsonObject Str(string d) => new() { ["type"] = "string", ["description"] = d };
        static JsonObject Int(string d, int min, int max) => new() { ["type"] = "integer", ["description"] = d, ["minimum"] = min, ["maximum"] = max };
        static JsonObject Bool(string d) => new() { ["type"] = "boolean", ["description"] = d };

        return new JsonArray
        {
            Tool("search_products", "Find products by name, SKU or NDC fragment. Returns id, sku, name, cold_chain. Use it to resolve a product before get_stock_by_product.",
                new JsonObject { ["query"] = Str("Name, SKU or NDC fragment (2-60 chars)"), ["limit"] = Int("Max rows (default 10)", 1, MaxRows) }, "query"),
            Tool("get_stock_by_product", "Stock of one product: totals, per-warehouse on hand/allocated, lots with expiry, quantity on order and the latest movements.",
                new JsonObject { ["product_id"] = Int("Product id", 1, int.MaxValue), ["sku"] = Str("SKU (alternative to product_id)") }),
            Tool("list_expiring_lots", "Lots with stock on hand that expire within N days (already expired ones included), soonest first.",
                new JsonObject
                {
                    ["days"] = Int("Horizon in days from today (default 30)", 1, 90),
                    ["warehouse"] = Str("Warehouse code filter, e.g. MIA (optional)"),
                    ["cold_chain_only"] = Bool("Only cold-chain products"),
                    ["limit"] = Int("Max rows (default 15)", 1, MaxRows)
                }),
            Tool("list_low_stock", "Products whose available stock (on hand minus allocated) is at or below the minimum level, most critical first.",
                new JsonObject { ["cold_chain_only"] = Bool("Only cold-chain products"), ["limit"] = Int("Max rows (default 15)", 1, MaxRows) }),
            Tool("get_order", "One customer order with its lines, allocated lots and shortfall per line. Accepts the order number (SO-001042) or numeric id.",
                new JsonObject { ["order"] = Str("Order number such as SO-001042, or numeric id") }, "order"),
            Tool("list_open_orders", "Recent customer orders. Defaults to open ones (Pending, Allocated, Picking).",
                new JsonObject
                {
                    ["status"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("open", "pending", "allocated", "picking", "shipped", "cancelled") },
                    ["shortfall_only"] = Bool("Only orders where allocated is below ordered"),
                    ["limit"] = Int("Max rows (default 15)", 1, MaxRows)
                }),
            Tool("list_reorder_suggestions", "Purchasing suggestions (min/max logic, quantities already on order included) with supplier, suggested quantity and cost, most urgent first.",
                new JsonObject { ["cold_chain_only"] = Bool("Only cold-chain products"), ["limit"] = Int("Max rows (default 15)", 1, MaxRows) }),
            Tool("list_alerts", "Alert summary counts plus the most recent cold-chain temperature excursions (with acknowledgement state).",
                new JsonObject { ["limit"] = Int("Max excursions (default 10)", 1, MaxRows) }),
            Tool("get_kpis", "Network KPIs: inventory value, units, SKUs, low-stock SKUs, lots expiring in 30/60/90 days, expired lots, open POs and orders, 30-day fill rate, open excursions.",
                new JsonObject())
        };
    }

    public async Task<ToolResult> ExecuteAsync(string name, JsonElement input, CancellationToken ct = default)
    {
        try
        {
            if (input.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined or JsonValueKind.Null))
                return Err("Input must be a JSON object.");
            ct.ThrowIfCancellationRequested();
            object result = name switch
            {
                "search_products" => await SearchProducts(input),
                "get_stock_by_product" => await GetStock(input),
                "list_expiring_lots" => await ExpiringLots(input),
                "list_low_stock" => await LowStock(input),
                "get_order" => await GetOrder(input),
                "list_open_orders" => await OpenOrders(input),
                "list_reorder_suggestions" => await Reorder(input),
                "list_alerts" => await Alerts(input),
                "get_kpis" => await Kpis(),
                _ => throw new ToolInputException($"Unknown tool '{name}'.")
            };
            return new ToolResult(JsonSerializer.Serialize(result, Out), false);
        }
        catch (ToolInputException ex) { return Err(ex.Message); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return Err("The data source failed. Try again."); }
    }

    private static ToolResult Err(string m) => new(JsonSerializer.Serialize(new { error = m }, Out), true);

    // ---- input helpers (strict: wrong types are rejected, ranges are enforced) ----
    private static JsonElement? Get(JsonElement input, string key) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;

    private static string? OptStr(JsonElement input, string key, int min, int max)
    {
        if (Get(input, key) is not { } v) return null;
        if (v.ValueKind != JsonValueKind.String) throw new ToolInputException($"'{key}' must be a string.");
        var s = v.GetString()!.Trim();
        if (s.Length == 0) return null;
        if (s.Length < min || s.Length > max) throw new ToolInputException($"'{key}' must be {min}-{max} characters.");
        return s;
    }

    private static int? OptInt(JsonElement input, string key, int min, int max)
    {
        if (Get(input, key) is not { } v) return null;
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var n)) throw new ToolInputException($"'{key}' must be an integer.");
        if (n < min || n > max) throw new ToolInputException($"'{key}' must be between {min} and {max}.");
        return n;
    }

    private static bool OptBool(JsonElement input, string key)
    {
        if (Get(input, key) is not { } v) return false;
        if (v.ValueKind is JsonValueKind.True) return true;
        if (v.ValueKind is JsonValueKind.False) return false;
        throw new ToolInputException($"'{key}' must be a boolean.");
    }

    private static int Limit(JsonElement input, int def) => OptInt(input, "limit", 1, MaxRows) ?? def;

    // ---- tools ----
    private async Task<object> SearchProducts(JsonElement i)
    {
        var q = OptStr(i, "query", 2, 60) ?? throw new ToolInputException("'query' is required.");
        var rows = await inventory.SearchProductsAsync(q, Limit(i, 10));
        return new { returned = rows.Count, products = rows.Select(p => new { id = p.Id, sku = p.Sku, name = p.Name, cold_chain = p.ColdChain }) };
    }

    private async Task<object> GetStock(JsonElement i)
    {
        var id = OptInt(i, "product_id", 1, int.MaxValue);
        var sku = OptStr(i, "sku", 1, 40);
        if (id is null && sku is null) throw new ToolInputException("Provide product_id or sku.");
        if (id is null)
        {
            var hits = await inventory.SearchProductsAsync(sku, 25);
            var hit = hits.FirstOrDefault(h => string.Equals(h.Sku, sku, StringComparison.OrdinalIgnoreCase));
            id = hit?.Id ?? throw new ToolInputException($"No product with SKU '{sku}'. Use search_products.");
        }
        var d = await inventory.GetProductAsync(id.Value) ?? throw new ToolInputException($"Product {id} not found.");
        return new
        {
            id = d.Product.Id, sku = d.Product.Sku, name = d.Product.Name, cold_chain = d.Product.ColdChain, supplier = d.Supplier,
            min_level = d.Product.MinLevel, max_level = d.Product.MaxLevel, on_hand = d.OnHand, allocated = d.Allocated, available = d.OnHand - d.Allocated, on_order = d.OnOrder,
            warehouses = d.Locations.Where(l => l.OnHand > 0 || l.Allocated > 0).Select(l => new { code = l.Code, on_hand = l.OnHand, allocated = l.Allocated }),
            lots = d.Lots.Take(MaxRows).Select(l => new { lot = l.LotNumber, expiry = l.Expiry.ToString("yyyy-MM-dd"), days_left = l.DaysLeft, state = l.Class, warehouse = l.Warehouse, on_hand = l.OnHand, allocated = l.Allocated }),
            lots_total = d.Lots.Count,
            recent_movements = d.Movements.Take(8).Select(m => new { utc = m.Utc.ToString("yyyy-MM-dd HH:mm"), type = m.Type, qty = m.Quantity, warehouse = m.Warehouse, reference = m.Reference })
        };
    }

    private async Task<object> ExpiringLots(JsonElement i)
    {
        var days = OptInt(i, "days", 1, 90) ?? 30;
        var wh = OptStr(i, "warehouse", 1, 10);
        var cold = OptBool(i, "cold_chain_only");
        var limit = Limit(i, 15);
        var rows = (await inventory.GetExpiryAlertsAsync(cold)).Where(a => a.DaysLeft <= days)
            .Where(a => wh is null || string.Equals(a.Warehouse, wh, StringComparison.OrdinalIgnoreCase)).ToList();
        return new
        {
            as_of = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).ToString("yyyy-MM-dd"), horizon_days = days, total_matching = rows.Count, units_total = rows.Sum(r => r.Quantity),
            lots = rows.Take(limit).Select(a => new { sku = a.Sku, name = a.Name, lot = a.LotNumber, expiry = a.Expiry.ToString("yyyy-MM-dd"), days_left = a.DaysLeft, warehouse = a.Warehouse, quantity = a.Quantity, cold_chain = a.ColdChain })
        };
    }

    private async Task<object> LowStock(JsonElement i)
    {
        var cold = OptBool(i, "cold_chain_only");
        var limit = Limit(i, 15);
        var rows = (await inventory.GetLowStockAsync()).Where(a => !cold || a.ColdChain).ToList();
        return new
        {
            total_matching = rows.Count,
            products = rows.Take(limit).Select(a => new { id = a.ProductId, sku = a.Sku, name = a.Name, cold_chain = a.ColdChain, available = a.Available, min = a.Min, max = a.Max, on_order = a.OnOrder })
        };
    }

    private async Task<object> GetOrder(JsonElement i)
    {
        var key = OptStr(i, "order", 1, 20) ?? throw new ToolInputException("'order' is required.");
        int? id = null;
        if (int.TryParse(key, out var n)) id = n;
        else if (key.StartsWith("SO-", StringComparison.OrdinalIgnoreCase) && int.TryParse(key.AsSpan(3), out var num)) id = num - 1000;
        if (id is null or < 1) throw new ToolInputException("Order must look like SO-001042 or be a numeric id.");
        var o = await orders.GetAsync(id.Value) ?? throw new ToolInputException($"Order '{key}' not found.");
        return new
        {
            number = o.Number, status = o.Status, customer = o.Customer, warehouse = o.Warehouse, created_utc = o.CreatedUtc.ToString("yyyy-MM-dd HH:mm"),
            total_ordered = o.TotalOrdered, total_allocated = o.TotalAllocated, has_shortfall = o.HasShortfall,
            lines = o.Lines.Take(MaxRows).Select(l => new
            {
                sku = l.Sku, name = l.Name, ordered = l.Ordered, allocated = l.Allocated, shipped = l.Shipped, short_by = Math.Max(0, l.Ordered - l.Allocated),
                lots = l.Lots.Select(a => new { lot = a.LotNumber, expiry = a.Expiry.ToString("yyyy-MM-dd"), qty = a.Quantity })
            })
        };
    }

    private async Task<object> OpenOrders(JsonElement i)
    {
        var status = OptStr(i, "status", 1, 12)?.ToLowerInvariant() ?? "open";
        OrderStatus? st = status switch
        {
            "open" => null, "pending" => OrderStatus.Pending, "allocated" => OrderStatus.Allocated, "picking" => OrderStatus.Picking,
            "shipped" => OrderStatus.Shipped, "cancelled" => OrderStatus.Cancelled,
            _ => throw new ToolInputException("status must be one of open, pending, allocated, picking, shipped, cancelled.")
        };
        var shortOnly = OptBool(i, "shortfall_only");
        var limit = Limit(i, 15);
        var rows = (await orders.ListAsync(st, openOnly: st is null, take: 200)).Where(o => !shortOnly || o.TotalAllocated < o.TotalOrdered).ToList();
        return new
        {
            total_matching = rows.Count,
            orders = rows.Take(limit).Select(o => new { number = o.Number, status = o.Status, customer = o.Customer, warehouse = o.Warehouse, created_utc = o.CreatedUtc.ToString("yyyy-MM-dd"), lines = o.LineCount, ordered = o.TotalOrdered, allocated = o.TotalAllocated, short_by = Math.Max(0, o.TotalOrdered - o.TotalAllocated) })
        };
    }

    private async Task<object> Reorder(JsonElement i)
    {
        var cold = OptBool(i, "cold_chain_only");
        var limit = Limit(i, 15);
        var rows = (await purchasing.GetSuggestionsAsync()).Where(r => !cold || r.ColdChain).ToList();
        return new
        {
            total_matching = rows.Count, total_cost_all = Math.Round(rows.Sum(r => r.Cost), 2),
            suggestions = rows.Take(limit).Select(r => new { sku = r.Sku, name = r.Name, cold_chain = r.ColdChain, supplier = r.Supplier, on_hand = r.OnHand, allocated = r.Allocated, on_order = r.OnOrder, min = r.Min, max = r.Max, suggested_qty = r.SuggestedQty, cost = Math.Round(r.Cost, 2) })
        };
    }

    private async Task<object> Alerts(JsonElement i)
    {
        var s = await inventory.GetAlertSummaryAsync();
        var ex = await inventory.GetExcursionsAsync(Limit(i, 10));
        return new
        {
            summary = new { low_stock_skus = s.LowStock, expiring_lots_90d = s.Expiring, expired_lots = s.Expired, open_cold_chain_excursions = s.ColdChain },
            excursions = ex.Select(e => new { warehouse = e.Warehouse, zone = e.Zone, reading_c = e.ReadingC, recorded_utc = e.RecordedUtc.ToString("yyyy-MM-dd HH:mm"), acknowledged = e.AcknowledgedUtc is not null, acknowledged_by = e.AcknowledgedBy })
        };
    }

    private async Task<object> Kpis()
    {
        var k = await inventory.GetKpisAsync();
        return new
        {
            inventory_value_usd = Math.Round(k.InventoryValue, 0), total_units = k.TotalUnits, sku_count = k.SkuCount, low_stock_skus = k.LowStockSkus,
            expiring_lots_30d = k.Expiring30, expiring_lots_60d = k.Expiring60, expiring_lots_90d = k.Expiring90, expired_lots_on_hand = k.ExpiredLots,
            open_purchase_orders = k.OpenPurchaseOrders, open_orders = k.OpenOrders, fill_rate_30d_pct = Math.Round(k.FillRatePct, 1), open_cold_chain_excursions = k.OpenExcursions
        };
    }

    private sealed class ToolInputException(string message) : Exception(message);
}
