using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace TradingBot.MarketDataWorker;

internal sealed class KrakenFuturesLiquidationWorker(
    MarketDataWorkerConfiguration config,
    IMarketDataStore store,
    IUniverseProvider futuresUniverseProvider,
    IClock? clock = null)
{
    private readonly IClock _clock = clock ?? SystemClock.Instance;
    private readonly SentryFailureGate _failureGate = new();

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!config.Ingestion.FuturesLiquidationsEnabled || config.Ingestion.StrategyPairs.Count == 0)
        {
            return;
        }

        store.EnsureSchema();
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunConnectionAsync(cancellationToken);
                _failureGate.Recovered();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                var now = _clock.UtcNow;
                store.SetStreamState(MarketDataVenue.Futures, "trade", false, now, recordGap: true);
                Console.WriteLine($"market-data liquidation stream FAILED: {ex.Message}");
                _failureGate.Report("liquidation-stream", ex);
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
    }

    private async Task RunConnectionAsync(CancellationToken cancellationToken)
    {
        var universe = await futuresUniverseProvider.GetUniverseAsync(cancellationToken);
        var requested = config.Ingestion.StrategyPairs.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var instruments = universe.Instruments
            .Where(instrument => instrument.Enabled && requested.Contains(instrument.Pair))
            .ToList();
        if (instruments.Count == 0)
        {
            throw new InvalidOperationException("No configured BYKO v2 instruments are available for the liquidation stream.");
        }

        var symbolToPair = instruments.ToDictionary(
            instrument => instrument.KrakenPair,
            instrument => instrument.Pair,
            StringComparer.OrdinalIgnoreCase);
        var connectStarted = _clock.UtcNow;
        store.SetStreamState(MarketDataVenue.Futures, "trade", false, connectStarted, recordGap: true);

        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        await socket.ConnectAsync(new Uri(config.Ingestion.FuturesWebSocketUrl), cancellationToken);

        var subscribe = JsonSerializer.SerializeToUtf8Bytes(new
        {
            @event = "subscribe",
            feed = "trade",
            product_ids = symbolToPair.Keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray()
        });
        await socket.SendAsync(subscribe, WebSocketMessageType.Text, true, cancellationToken);
        var buffer = new byte[64 * 1024];
        var subscribed = false;
        var lastLivenessWrite = DateTimeOffset.MinValue;
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new WebSocketException($"Kraken closed trade stream: {result.CloseStatus} {result.CloseStatusDescription}");
                }

                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text)
            {
                continue;
            }

            var receivedAt = _clock.UtcNow;
            if (KrakenFuturesLiquidationParser.IsSubscriptionError(message.ToArray(), out var subscriptionError))
            {
                throw new InvalidOperationException($"Kraken trade subscription rejected: {subscriptionError}");
            }

            if (!subscribed && KrakenFuturesLiquidationParser.IsSubscribed(message.ToArray()))
            {
                subscribed = true;
                store.SetStreamState(MarketDataVenue.Futures, "trade", true, receivedAt, recordGap: false);
                lastLivenessWrite = receivedAt;
                Console.WriteLine($"market-data liquidation stream subscribed products={symbolToPair.Count}");
            }

            if (!subscribed)
            {
                continue;
            }

            if (receivedAt - lastLivenessWrite >= TimeSpan.FromSeconds(1))
            {
                store.SetStreamState(MarketDataVenue.Futures, "trade", true, receivedAt, recordGap: false);
                lastLivenessWrite = receivedAt;
            }

            var records = KrakenFuturesLiquidationParser.Parse(
                message.ToArray(),
                symbolToPair,
                receivedAt);
            store.AppendLiquidations(records);
        }

        throw new WebSocketException("Kraken trade stream left the open state.");
    }
}

internal static class KrakenFuturesLiquidationParser
{
    public static bool IsSubscribed(ReadOnlySpan<byte> json)
    {
        using var document = JsonDocument.Parse(json.ToArray());
        var root = document.RootElement;
        return root.ValueKind == JsonValueKind.Object
            && string.Equals(GetString(root, "event"), "subscribed", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(GetString(root, "feed"), "trade", StringComparison.OrdinalIgnoreCase)
                || string.Equals(GetString(root, "feed"), "trade_snapshot", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsSubscriptionError(ReadOnlySpan<byte> json, out string message)
    {
        using var document = JsonDocument.Parse(json.ToArray());
        var root = document.RootElement;
        var eventName = root.ValueKind == JsonValueKind.Object ? GetString(root, "event") : null;
        var failed = string.Equals(eventName, "subscribed_failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(eventName, "error", StringComparison.OrdinalIgnoreCase);
        message = failed
            ? GetString(root, "message") ?? GetString(root, "error") ?? eventName ?? "unknown error"
            : string.Empty;
        return failed;
    }

    public static IReadOnlyList<SharedLiquidationRecord> Parse(
        ReadOnlySpan<byte> json,
        IReadOnlyDictionary<string, string> symbolToPair,
        DateTimeOffset receivedAt)
    {
        using var document = JsonDocument.Parse(json.ToArray());
        var root = document.RootElement;
        var candidates = new List<JsonElement>();
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("trades", out var trades)
            && trades.ValueKind == JsonValueKind.Array)
        {
            candidates.AddRange(trades.EnumerateArray());
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            candidates.AddRange(root.EnumerateArray());
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            candidates.Add(root);
        }

        var records = new List<SharedLiquidationRecord>();
        foreach (var item in candidates)
        {
            var type = GetString(item, "type");
            if (!string.Equals(type, "liquidation", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(type, "partialLiquidation", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var symbol = GetString(item, "product_id") ?? GetString(item, "productId");
            var side = GetString(item, "side")?.ToLowerInvariant();
            var price = GetDecimal(item, "price");
            var quantity = GetDecimal(item, "qty", "quantity");
            if (string.IsNullOrWhiteSpace(symbol)
                || !symbolToPair.TryGetValue(symbol, out var pair)
                || side is not ("buy" or "sell")
                || price <= 0m
                || quantity <= 0m)
            {
                continue;
            }

            var eventTime = GetTime(item, receivedAt);
            var id = GetScalarString(item, "uid")
                ?? GetScalarString(item, "trade_id")
                ?? $"{symbol}:{GetScalarString(item, "seq") ?? "noseq"}:{eventTime.ToUnixTimeMilliseconds()}:{side}:{price}:{quantity}";
            records.Add(new SharedLiquidationRecord(
                MarketDataVenue.Futures,
                pair,
                id,
                eventTime,
                side,
                type!,
                price,
                quantity,
                price * quantity,
                receivedAt));
        }

        return records;
    }

    private static DateTimeOffset GetTime(JsonElement item, DateTimeOffset fallback)
    {
        if (!item.TryGetProperty("time", out var value))
        {
            return fallback;
        }

        if (value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed.ToUniversalTime();
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var integer))
        {
            return integer > 10_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(integer)
                : DateTimeOffset.FromUnixTimeSeconds(integer);
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var seconds))
        {
            return DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000d));
        }

        return fallback;
    }

    private static string? GetString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? GetScalarString(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static decimal GetDecimal(JsonElement item, string name, string? fallbackName = null)
    {
        if (!item.TryGetProperty(name, out var value)
            && (fallbackName is null || !item.TryGetProperty(fallbackName, out value)))
        {
            return 0m;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
            JsonValueKind.String when decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var number) => number,
            _ => 0m
        };
    }
}
