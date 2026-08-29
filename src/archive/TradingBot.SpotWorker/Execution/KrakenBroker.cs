using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TradingBot.SpotWorker;

/// <summary>
/// Minimal Kraken private-API adapter: HMAC-SHA512 auth, Balance, and AddOrder with the
/// <c>validate</c> flag. It never enables live execution by itself — the caller decides the
/// <c>validate</c> value from the risk/live gate.
/// </summary>
internal sealed class KrakenBroker(HttpClient httpClient, KrakenOptions options) : ISpotBroker
{
    private static long _lastNonce;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(options.ApiKey) && !string.IsNullOrWhiteSpace(options.ApiSecret);

    public async Task<IReadOnlyDictionary<string, decimal>> GetBalanceAsync(CancellationToken cancellationToken)
    {
        using var doc = await PostPrivateAsync("/0/private/Balance", new Dictionary<string, string>(), cancellationToken);
        var root = doc.RootElement;
        var errors = ReadErrors(root);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"Kraken Balance error: {string.Join(", ", errors)}");
        }

        var balances = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in result.EnumerateObject())
            {
                if (decimal.TryParse(property.Value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
                {
                    balances[property.Name] = value;
                }
            }
        }

        return balances;
    }

    public async Task<BrokerOrderResult> AddOrderAsync(
        string krakenPair,
        string side,
        decimal volume,
        bool validate,
        CancellationToken cancellationToken)
    {
        var fields = new Dictionary<string, string>
        {
            ["pair"] = krakenPair,
            ["type"] = side,
            ["ordertype"] = "market",
            ["volume"] = volume.ToString(CultureInfo.InvariantCulture)
        };

        if (validate)
        {
            fields["validate"] = "true";
        }

        JsonDocument doc;
        try
        {
            doc = await PostPrivateAsync("/0/private/AddOrder", fields, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException)
        {
            return BrokerOrderResult.Failed(ex.Message);
        }

        using (doc)
        {
            var root = doc.RootElement;
            var errors = ReadErrors(root);
            if (errors.Count > 0)
            {
                return BrokerOrderResult.Rejected(string.Join(", ", errors));
            }

            string? description = null;
            var txIds = new List<string>();
            if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object)
            {
                if (result.TryGetProperty("descr", out var descr) && descr.TryGetProperty("order", out var order))
                {
                    description = order.GetString();
                }

                if (result.TryGetProperty("txid", out var txid) && txid.ValueKind == JsonValueKind.Array)
                {
                    txIds.AddRange(txid.EnumerateArray()
                        .Select(item => item.GetString())
                        .Where(item => !string.IsNullOrWhiteSpace(item))
                        .Cast<string>());
                }
            }

            return BrokerOrderResult.Ok(validate, description, txIds);
        }
    }

    public Task<BrokerOrderResult> AddLimitPostOnlyOrderAsync(
        string krakenPair,
        string side,
        decimal volume,
        decimal price,
        bool validate,
        CancellationToken cancellationToken)
    {
        var fields = new Dictionary<string, string>
        {
            ["pair"] = krakenPair,
            ["type"] = side,
            ["ordertype"] = "limit",
            ["oflags"] = "post",
            ["volume"] = volume.ToString(CultureInfo.InvariantCulture),
            ["price"] = price.ToString(CultureInfo.InvariantCulture)
        };

        return AddOrderCoreAsync(fields, validate, cancellationToken);
    }

    public Task<BrokerOrderResult> AddLimitIocOrderAsync(
        string krakenPair,
        string side,
        decimal volume,
        decimal price,
        bool validate,
        CancellationToken cancellationToken)
    {
        var fields = new Dictionary<string, string>
        {
            ["pair"] = krakenPair,
            ["type"] = side,
            ["ordertype"] = "limit",
            ["timeinforce"] = "IOC",
            ["volume"] = volume.ToString(CultureInfo.InvariantCulture),
            ["price"] = price.ToString(CultureInfo.InvariantCulture)
        };

        return AddOrderCoreAsync(fields, validate, cancellationToken);
    }

    public async Task<BrokerOrderResult> CancelOrderAsync(string txid, CancellationToken cancellationToken)
    {
        JsonDocument doc;
        try
        {
            doc = await PostPrivateAsync(
                "/0/private/CancelOrder",
                new Dictionary<string, string> { ["txid"] = txid },
                cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException)
        {
            return BrokerOrderResult.Failed(ex.Message);
        }

        using (doc)
        {
            var root = doc.RootElement;
            var errors = ReadErrors(root);
            return errors.Count > 0
                ? BrokerOrderResult.Rejected(string.Join(", ", errors))
                : BrokerOrderResult.Ok(false, "cancel accepted", Array.Empty<string>());
        }
    }

    private async Task<BrokerOrderResult> AddOrderCoreAsync(
        Dictionary<string, string> fields,
        bool validate,
        CancellationToken cancellationToken)
    {
        if (validate)
        {
            fields["validate"] = "true";
        }

        JsonDocument doc;
        try
        {
            doc = await PostPrivateAsync("/0/private/AddOrder", fields, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException)
        {
            return BrokerOrderResult.Failed(ex.Message);
        }

        using (doc)
        {
            var root = doc.RootElement;
            var errors = ReadErrors(root);
            if (errors.Count > 0)
            {
                return BrokerOrderResult.Rejected(string.Join(", ", errors));
            }

            string? description = null;
            var txIds = new List<string>();
            if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object)
            {
                if (result.TryGetProperty("descr", out var descr) && descr.TryGetProperty("order", out var order))
                {
                    description = order.GetString();
                }

                if (result.TryGetProperty("txid", out var txid) && txid.ValueKind == JsonValueKind.Array)
                {
                    txIds.AddRange(txid.EnumerateArray()
                        .Select(item => item.GetString())
                        .Where(item => !string.IsNullOrWhiteSpace(item))
                        .Cast<string>());
                }
            }

            return BrokerOrderResult.Ok(validate, description, txIds);
        }
    }

    public async Task<BrokerOrderQuery?> QueryOrderAsync(string txid, CancellationToken cancellationToken)
    {
        JsonDocument doc;
        try
        {
            doc = await PostPrivateAsync(
                "/0/private/QueryOrders",
                new Dictionary<string, string> { ["txid"] = txid },
                cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException)
        {
            Console.WriteLine($"broker-query-order: {txid} failed ({ex.Message})");
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            var errors = ReadErrors(root);
            if (errors.Count > 0)
            {
                Console.WriteLine($"broker-query-order: {txid} rejected ({string.Join(", ", errors)})");
                return null;
            }

            if (!root.TryGetProperty("result", out var result)
                || result.ValueKind != JsonValueKind.Object
                || !result.TryGetProperty(txid, out var order)
                || order.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return new BrokerOrderQuery(
                order.TryGetProperty("status", out var status) ? status.GetString() ?? string.Empty : string.Empty,
                ReadDecimal(order, "vol_exec"),
                ReadDecimal(order, "price"),
                ReadDecimal(order, "cost"),
                ReadDecimal(order, "fee"));
        }
    }

    public async Task<IReadOnlyList<SpotTradeHistoryEntry>> GetTradeHistoryAsync(CancellationToken cancellationToken)
    {
        JsonDocument doc;
        try
        {
            doc = await PostPrivateAsync("/0/private/TradesHistory", new Dictionary<string, string>(), cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException)
        {
            Console.WriteLine($"broker-trade-history: failed ({ex.Message})");
            return Array.Empty<SpotTradeHistoryEntry>();
        }

        using (doc)
        {
            var root = doc.RootElement;
            var errors = ReadErrors(root);
            if (errors.Count > 0)
            {
                Console.WriteLine($"broker-trade-history: rejected ({string.Join(", ", errors)})");
                return Array.Empty<SpotTradeHistoryEntry>();
            }

            if (!root.TryGetProperty("result", out var result)
                || result.ValueKind != JsonValueKind.Object
                || !result.TryGetProperty("trades", out var trades)
                || trades.ValueKind != JsonValueKind.Object)
            {
                return Array.Empty<SpotTradeHistoryEntry>();
            }

            var entries = new List<SpotTradeHistoryEntry>();
            foreach (var trade in trades.EnumerateObject())
            {
                var value = trade.Value;
                var seconds = ReadDouble(value, "time");
                entries.Add(new SpotTradeHistoryEntry(
                    value.TryGetProperty("ordertxid", out var ordertxid) ? ordertxid.GetString() ?? string.Empty : string.Empty,
                    value.TryGetProperty("pair", out var pair) ? pair.GetString() ?? string.Empty : string.Empty,
                    value.TryGetProperty("type", out var type) ? type.GetString() ?? string.Empty : string.Empty,
                    ReadDecimal(value, "price"),
                    ReadDecimal(value, "vol"),
                    ReadDecimal(value, "cost"),
                    ReadDecimal(value, "fee"),
                    seconds > 0d ? DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000d)) : DateTimeOffset.MinValue));
            }

            return entries;
        }
    }


    // Deposits, withdrawals and transfers straight from the Kraken ledger. This is
    // the only trustworthy source for them: cash on the account also moves when the
    // exchange settles or releases funds, so comparing balances between cycles
    // cannot tell a deposit apart from ordinary account activity.
    //
    // The ledger returns 50 entries per page and mixes trades in with money movement,
    // so an active account needs paging to reach anything but the last few hours.
    public async Task<IReadOnlyList<PortfolioCashEvent>> GetCashEventsAsync(
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        const int pageSize = 50;
        const int maxPages = 40;

        var events = new List<PortfolioCashEvent>();

        for (var page = 0; page < maxPages; page++)
        {
            JsonDocument doc;
            try
            {
                // Kraken takes a single ledger type per call, so ask for everything
                // and keep the three entry kinds that move money in or out.
                doc = await PostPrivateAsync(
                    "/0/private/Ledgers",
                    new Dictionary<string, string>
                    {
                        ["type"] = "all",
                        ["start"] = since.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                        ["ofs"] = (page * pageSize).ToString(CultureInfo.InvariantCulture)
                    },
                    cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException)
            {
                Console.WriteLine($"broker-ledger: failed on page {page} ({ex.Message})");
                break;
            }

            var rows = 0;

            using (doc)
            {
                var root = doc.RootElement;
                var errors = ReadErrors(root);
                if (errors.Count > 0)
                {
                    Console.WriteLine($"broker-ledger: rejected ({string.Join(", ", errors)})");
                    break;
                }

                if (!root.TryGetProperty("result", out var result)
                    || result.ValueKind != JsonValueKind.Object
                    || !result.TryGetProperty("ledger", out var ledger)
                    || ledger.ValueKind != JsonValueKind.Object)
                {
                    break;
                }

                foreach (var entry in ledger.EnumerateObject())
                {
                    rows++;

                    var value = entry.Value;
                    var type = (value.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null)
                        ?? string.Empty;
                    if (!IsCashMovement(type))
                    {
                        continue;
                    }

                    var seconds = ReadDouble(value, "time");
                    events.Add(new PortfolioCashEvent(
                        entry.Name,
                        seconds > 0d
                            ? DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000d))
                            : DateTimeOffset.UtcNow,
                        type.ToLowerInvariant(),
                        ReadDecimal(value, "amount"),
                        value.TryGetProperty("asset", out var asset) ? asset.GetString() : null,
                        "kraken-spot-ledger"));
                }
            }

            if (rows < pageSize)
            {
                break;
            }
        }

        return events;
    }

    private static bool IsCashMovement(string type) =>
        type.Equals("deposit", StringComparison.OrdinalIgnoreCase)
        || type.Equals("withdrawal", StringComparison.OrdinalIgnoreCase)
        || type.Equals("transfer", StringComparison.OrdinalIgnoreCase);

    private static double ReadDouble(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var parsed)
            ? parsed
            : 0d;

    private static decimal ReadDecimal(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
        && decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0m;

    private async Task<JsonDocument> PostPrivateAsync(
        string path,
        Dictionary<string, string> fields,
        CancellationToken cancellationToken)
    {
        var nonce = NextNonce();
        fields["nonce"] = nonce;
        var postData = string.Join("&", fields.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        var signature = Sign(path, nonce, postData, options.ApiSecret);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{options.BaseUrl.TrimEnd('/')}{path}");
        request.Headers.Add("API-Key", options.ApiKey);
        request.Headers.Add("API-Sign", signature);
        request.Content = new StringContent(postData, Encoding.UTF8, "application/x-www-form-urlencoded");

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Kraken HTTP {(int)response.StatusCode}: {Trim(body)}");
        }

        return JsonDocument.Parse(body);
    }

    private static string Sign(string path, string nonce, string postData, string base64Secret)
    {
        using var sha256 = SHA256.Create();
        var nonceAndData = sha256.ComputeHash(Encoding.UTF8.GetBytes(nonce + postData));

        var pathBytes = Encoding.UTF8.GetBytes(path);
        var buffer = new byte[pathBytes.Length + nonceAndData.Length];
        Buffer.BlockCopy(pathBytes, 0, buffer, 0, pathBytes.Length);
        Buffer.BlockCopy(nonceAndData, 0, buffer, pathBytes.Length, nonceAndData.Length);

        using var hmac = new HMACSHA512(Convert.FromBase64String(base64Secret));
        return Convert.ToBase64String(hmac.ComputeHash(buffer));
    }

    private static string NextNonce()
    {
        var candidate = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        while (true)
        {
            var last = Interlocked.Read(ref _lastNonce);
            var nonce = candidate > last ? candidate : last + 1;
            if (Interlocked.CompareExchange(ref _lastNonce, nonce, last) == last)
            {
                return nonce.ToString(CultureInfo.InvariantCulture);
            }
        }
    }

    private static IReadOnlyList<string> ReadErrors(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return error.EnumerateArray()
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .ToList();
    }

    private static string Trim(string value) => value.Length <= 300 ? value : value[..300];
}

internal sealed record BrokerOrderResult(
    bool Success,
    bool Validated,
    string? Description,
    IReadOnlyList<string> TxIds,
    string? Error)
{
    public static BrokerOrderResult Ok(bool validated, string? description, IReadOnlyList<string> txIds) =>
        new(true, validated, description, txIds, null);

    public static BrokerOrderResult Rejected(string error) =>
        new(false, false, null, Array.Empty<string>(), error);

    public static BrokerOrderResult Failed(string error) =>
        new(false, false, null, Array.Empty<string>(), error);
}
