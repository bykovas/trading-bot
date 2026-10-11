using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TradingBot.FuturesWorker;

internal interface IDebykoMarketClient
{
    Task<DebykoBarResult> GetResultAsync(Guid ruleId, string instrument, DateTimeOffset barEnd, CancellationToken cancellationToken);
    Task<DebykoFrameRead> GetFrameAsync(string pair, DateTimeOffset barEnd, DateTimeOffset dataAsOf, CancellationToken cancellationToken,
        BykoV2Rule? rule = null, bool btcReference = false);
}

internal sealed record DebykoHttpAudit(string Path, int StatusCode, string? RequestBody, string RawBody, string BodySha256,
    string? ReceiptId, string? ItemsSha256, IReadOnlyDictionary<string, string[]> ReceiptHeaders);

internal sealed record DebykoBarResult(Guid RuleId, string? Version, string Instrument,
    DateTimeOffset BarEnd, DateTimeOffset? DataAsOf, int Attempt, bool? Verdict,
    string? Reason, bool Final, string? ReceiptId, IReadOnlyList<DebykoHttpAudit> Responses)
{
    public string RawBody => Responses[0].RawBody;
}

internal sealed record DebykoFrameRead(BykoV2MarketFrame? Frame, string? Reason,
    IReadOnlyList<DebykoHttpAudit> Responses)
{
    public IReadOnlyList<string> RawBodies => Responses.Select(response => JsonSerializer.Serialize(response)).ToArray();
}

internal sealed class DebykoMarketClient : IDebykoMarketClient
{
    private readonly HttpClient http;
    private readonly string apiKey;
    private const string Venue = "KRAKEN-FUTURES";
    private const string LiquidationBlock = "Flush blocked: liquidation as_of request rejected or window unavailable";

    public DebykoMarketClient(HttpClient http, string apiKey)
    {
        this.http = http ?? throw new ArgumentNullException(nameof(http));
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        this.apiKey = apiKey;
    }

    internal static string ToInstrument(string pair)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pair);
        var text = pair.Trim().ToUpperInvariant();
        var asset = text.EndsWith("/USD", StringComparison.Ordinal) ? text[..^4]
            : text.EndsWith("-PERP", StringComparison.Ordinal) ? text[..^5] : "";
        if (asset.Length == 0 || asset.Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw new ArgumentException("Expected a USD pair or perpetual instrument", nameof(pair));
        return (asset == "XBT" ? "BTC" : asset) + "-PERP";
    }

    public async Task<DebykoBarResult> GetResultAsync(Guid ruleId, string instrument,
        DateTimeOffset barEnd, CancellationToken cancellationToken)
    {
        instrument = ToInstrument(instrument);
        var path = $"/v2/agent/result?rule_id={ruleId:D}&instrument={Uri.EscapeDataString(instrument)}&bar_end={Uri.EscapeDataString(Utc(barEnd))}";
        var audit = await SendAsync(path, null, cancellationToken);
        using var document = JsonDocument.Parse(audit.RawBody);
        var root = document.RootElement;
        if (root.GetProperty("rule_id").GetGuid() != ruleId || Text(root, "instrument") != instrument
            || Instant(root, "bar_end") != barEnd.ToUniversalTime())
            throw new InvalidDataException("DEBYKO result identity mismatch");
        bool? verdict = Text(root, "verdict") switch
        {
            "true" => true, "false" => false, "unknown" => null,
            _ => throw new InvalidDataException("Invalid DEBYKO verdict")
        };
        return new(ruleId, Text(root, "version"), instrument, Instant(root, "bar_end"),
            root.TryGetProperty("data_as_of", out var cut) && cut.ValueKind != JsonValueKind.Null ? Instant(root, "data_as_of") : null,
            root.TryGetProperty("attempt", out var attempt) && attempt.ValueKind != JsonValueKind.Null ? attempt.GetInt32() : 0,
            verdict, Text(root, "reason"), root.GetProperty("final").GetBoolean(), Text(root, "receipt_id"), [audit]);
    }

    public async Task<DebykoFrameRead> GetFrameAsync(string pair, DateTimeOffset barEnd,
        DateTimeOffset dataAsOf, CancellationToken cancellationToken, BykoV2Rule? rule = null, bool btcReference = false)
    {
        var instrument = ToInstrument(pair);
        var audits = new List<DebykoHttpAudit>();
        if (dataAsOf < barEnd) return new(null, "data_as_of precedes bar_end", audits);
        var where = $"venue = {Venue} and instrument = {instrument} and quote = USD";
        try
        {
            var flush = rule is BykoV2Rule.FlushLong or BykoV2Rule.FlushShort;
            var requests = rule is null
                ? new[] { ("1m", 1, 80), ("5m", 5, 20), ("15m", 15, 80), ("1h", 60, 240) }
                : flush ? btcReference ? new[] { ("5m", 5, 13) } : new[] { ("1m", 1, 57) }
                : new[] { ("15m", 15, btcReference ? 1 : 57), ("1h", 60, 203) };
            var series = new Dictionary<string, IReadOnlyList<Candle>>
                { ["1m"] = [], ["5m"] = [], ["15m"] = [], ["1h"] = [] };
            var gapFree = new Dictionary<string, bool?>
                { ["1m"] = null, ["5m"] = null, ["15m"] = null, ["1h"] = null };
            foreach (var (timeframe, minutes, count) in requests)
            {
                var step = TimeSpan.FromMinutes(minutes);
                var end = new DateTimeOffset(barEnd.UtcTicks - barEnd.UtcTicks % step.Ticks, TimeSpan.Zero);
                var start = end - step * count;
                var audit = await SendAsync("/v2/candles", new
                {
                    where, timeframe, from = Utc(start), to = Utc(end), as_of = Utc(dataAsOf), limit = count,
                    units = "published", number_format = "string", price_type = "trade", closed_only = true, include_gaps = true
                }, cancellationToken);
                audits.Add(audit);
                using var document = JsonDocument.Parse(audit.RawBody);
                var root = document.RootElement;
                Require(Instant(root, "data_as_of") == dataAsOf.ToUniversalTime()
                    && Instant(root, "from") == start && Instant(root, "to") == end
                    && Text(root, "timeframe") == timeframe && Text(root, "price_type") == "trade"
                    && Text(root, "units") == "published", "candle response bounds or units mismatch");
                var item = Listing(root, instrument);
                var rows = item.GetProperty("candles");
                var complete = Text(item, "status") == "present" && rows.GetArrayLength() == count;
                var recordedGap = Text(item, "reason") == "collector_gap" || Text(item, "gap_cause") is not null;
                var candles = new List<Candle>();
                DateTimeOffset? previousOpen = null;
                foreach (var row in rows.EnumerateArray())
                {
                    var open = Instant(row, "open_time");
                    Require(open >= start && open < end && (open - start).Ticks % step.Ticks == 0
                        && (previousOpen is null || open > previousOpen) && Instant(row, "close_time") == open + step,
                        "candle row bounds or ordering mismatch");
                    if (open != (previousOpen is null ? start : previousOpen.Value + step)) complete = false;
                    previousOpen = open;
                    if (Text(row, "status") != "present")
                    {
                        complete = false;
                        recordedGap |= Text(row, "reason") == "collector_gap" || Text(row, "gap_cause") is not null;
                        continue;
                    }
                    Require(Instant(row, "received_at") <= dataAsOf, "invisible candle");
                    var candle = new Candle(open, Number(row, "open"), Number(row, "high"), Number(row, "low"),
                        Number(row, "close"), Number(row, "volume"),
                        // Candle has no nullable count; -1 denotes unpublished, never a fabricated zero.
                        row.GetProperty("trade_count").ValueKind == JsonValueKind.Null ? -1 : row.GetProperty("trade_count").GetInt32());
                    Require(candle.Low > 0 && candle.High >= candle.Low && candle.Open >= candle.Low && candle.Open <= candle.High
                        && candle.Close >= candle.Low && candle.Close <= candle.High && candle.Volume >= 0, "invalid candle values");
                    candles.Add(candle);
                }
                series[timeframe] = candles;
                gapFree[timeframe] = recordedGap ? false : complete && candles.Count == count ? true : null;
            }
            if (btcReference)
                return new(new(pair, series["1m"], series["5m"], series["15m"], series["1h"], null, null, null, null, null, gapFree), null, audits);
            var requestedLayers = rule is null ? new[] { "ticker", "funding", "stats", "book" }
                : flush ? new[] { "book", "stats" } : new[] { "book", "funding" };
            var snapshot = await SendAsync("/v2/snapshots/history", new
            {
                where, at = new[] { Utc(dataAsOf) }, layers = requestedLayers,
                units = "published", number_format = "string", include_gaps = true, allow_stale = false
            }, cancellationToken);
            audits.Add(snapshot);
            using var history = JsonDocument.Parse(snapshot.RawBody);
            Require(Text(history.RootElement, "mode") == "at" && Text(history.RootElement, "units") == "published", "snapshot mode or units mismatch");
            var layers = Listing(history.RootElement, instrument).GetProperty("layers");
            var values = new Dictionary<string, JsonElement>();
            foreach (var name in requestedLayers)
            {
                if (!layers.TryGetProperty(name, out var layer) || !layer.TryGetProperty("rows", out var rows)
                    || rows.GetArrayLength() == 0) continue;
                Require(rows.GetArrayLength() == 1, "snapshot row bounds mismatch");
                var row = rows[0];
                Require(Instant(row, "at") == dataAsOf.ToUniversalTime(), "snapshot row bounds mismatch");
                if (Text(row, "status") != "present") continue;
                Require(Instant(row, "received_at") <= dataAsOf && Instant(row, "venue_ts") <= dataAsOf,
                    "invisible snapshot");
                if (row.TryGetProperty("values", out var layerValues)) values[name] = layerValues;
            }
            decimal? bid = values.TryGetValue("book", out var book) ? OptionalNumber(book, "bid") : null;
            decimal? ask = values.TryGetValue("book", out book) ? OptionalNumber(book, "ask") : null;
            decimal? turnover = values.TryGetValue("stats", out var stats) ? OptionalNumber(stats, "turnover_24h") : null;
            decimal? funding8h = null;
            if (values.TryGetValue("funding", out var funding))
            {
                try { funding8h = Funding8h(funding); }
                catch (Exception ex) when (ex is InvalidDataException or KeyNotFoundException or OverflowException) { }
            }
            if (bid is <= 0) bid = null;
            if (ask is <= 0) ask = null;
            if (ask < bid) { bid = null; ask = null; }
            if (turnover is <= 0) turnover = null;
            BykoV2LiquidationWindow? liquidations = null;
            string? reason = rule is null || flush ? LiquidationBlock : null;
            if (rule is null || flush)
            {
            try
            {
                var liquidation = await SendAsync("/v2/liquidations", new
                {
                    where, from = Utc(barEnd.AddMinutes(-4)), to = Utc(barEnd), as_of = Utc(dataAsOf),
                    units = "published", number_format = "string", include_gaps = true, limit = 1000
                }, cancellationToken, captureBadRequest: true);
                audits.Add(liquidation);
                Require(liquidation.StatusCode == 200, "liquidation as_of request rejected");
                using var liquidDocument = JsonDocument.Parse(liquidation.RawBody);
                var root = liquidDocument.RootElement;
                Require(Instant(root, "data_as_of") == dataAsOf.ToUniversalTime()
                    && Instant(root, "from") == barEnd.AddMinutes(-4).ToUniversalTime()
                    && Instant(root, "to") == barEnd.ToUniversalTime() && Text(root, "units") == "published", "liquidation bounds mismatch");
                var item = Listing(root, instrument);
                Require(Text(item, "status") == "present" && !item.GetProperty("truncated").GetBoolean()
                    && item.GetProperty("gaps").GetArrayLength() == 0, "incomplete liquidation window");
                if (item.TryGetProperty("retention_from", out var retained) && retained.ValueKind != JsonValueKind.Null)
                    Require(Instant(item, "retention_from") <= barEnd.AddMinutes(-4), "liquidation window not retained");
                // Published contract quantities are not necessarily base quantities, including quiet windows.
                Require(Text(item.GetProperty("units"), "size") == "base"
                    && Text(item.GetProperty("units"), "price") == "quote", "liquidation size cannot be valued as USD");
                decimal longs = 0, shorts = 0;
                foreach (var row in item.GetProperty("events").EnumerateArray())
                {
                    var time = Instant(row, "event_time");
                    Require(time > barEnd.AddMinutes(-4) && time <= barEnd && Instant(row, "received_at") <= dataAsOf, "invisible liquidation");
                    var price = Number(row, "price");
                    var size = Number(row, "size");
                    Require(price > 0 && size > 0, "invalid liquidation value");
                    if (Text(row, "side") == "sell") longs += price * size;
                    else if (Text(row, "side") == "buy") shorts += price * size;
                    else throw new InvalidDataException("unknown liquidation side");
                }
                liquidations = new(longs, shorts, true);
                reason = null;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                reason = LiquidationBlock;
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or KeyNotFoundException or FormatException or InvalidOperationException or OverflowException)
            {
                if (ex is InvalidDataException && ex.Message is "liquidation bounds mismatch" or "listing mismatch") throw;
                reason = "Flush blocked: liquidation window missing, holed, truncated or not reproducible at data_as_of";
            }
            }
            return new(new(pair, series["1m"], series["5m"], series["15m"], series["1h"], bid, ask,
                turnover, funding8h, liquidations, gapFree), reason, audits);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or KeyNotFoundException or FormatException or InvalidOperationException or OverflowException)
        {
            return new(null, ex is InvalidDataException ? "DEBYKO: " + ex.Message : "DEBYKO market data missing, stale, holed or invalid", audits);
        }
    }

    private async Task<DebykoHttpAudit> SendAsync(string path, object? body, CancellationToken cancellationToken, bool captureBadRequest = false)
    {
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, cancellationToken);
        // Never expose error bodies or response headers, including authentication challenges.
        if (!response.IsSuccessStatusCode && !(captureBadRequest && response.StatusCode == System.Net.HttpStatusCode.BadRequest))
            throw new HttpRequestException($"DEBYKO HTTP {(int)response.StatusCode}", null, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (raw.Contains(apiKey, StringComparison.Ordinal))
            throw new InvalidDataException("DEBYKO response contained credential material");
        string? receiptId = null, itemsSha256 = null;
        try
        {
            using var document = JsonDocument.Parse(raw);
            receiptId = Text(document.RootElement, "receipt_id");
            itemsSha256 = Text(document.RootElement, "items_sha256");
        }
        catch (JsonException) { }
        var headers = response.Headers.Where(h => h.Key.Equals("X-Receipt-Id", StringComparison.OrdinalIgnoreCase)
                || h.Key.Equals("X-Debyko-Receipt-Id", StringComparison.OrdinalIgnoreCase))
            .Where(h => h.Value.All(v => !v.Contains(apiKey, StringComparison.Ordinal)))
            .ToDictionary(h => h.Key, h => h.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        return new(path, (int)response.StatusCode, body is null ? null : JsonSerializer.Serialize(body), raw,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant(), receiptId, itemsSha256, headers);
    }

    private static JsonElement Listing(JsonElement root, string instrument)
    {
        var items = root.GetProperty("items");
        Require(items.GetArrayLength() == 1, "missing or ambiguous listing");
        var item = items[0];
        Require(Text(item, "instrument") == instrument && Text(item, "venue") == Venue && Text(item, "quote") == "USD", "listing mismatch");
        return item;
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new InvalidDataException(reason);
    }

    internal static string Utc(DateTimeOffset instant) => instant.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    private static string? Text(JsonElement element, string name) => element.TryGetProperty(name, out var value)
        && value.ValueKind != JsonValueKind.Null ? value.ToString() : null;
    private static DateTimeOffset Instant(JsonElement element, string name)
    {
        var value = element.GetProperty(name);
        Require(value.ValueKind == JsonValueKind.String, "missing timestamp");
        Require(DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant), "invalid timestamp");
        return instant.ToUniversalTime();
    }
    private static decimal Number(JsonElement element, string name)
    {
        var value = element.GetProperty(name);
        Require(value.ValueKind == JsonValueKind.String, "missing numeric field");
        Require(decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number), "invalid numeric field");
        return number;
    }

    private static decimal? OptionalNumber(JsonElement element, string name) =>
        !element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null ? null : Number(element, name);

    private static decimal Funding8h(JsonElement values)
    {
        var interval = Text(values, "funding_interval");
        Require(interval is not null, "funding interval unavailable");
        var digits = 0;
        while (digits < interval!.Length && interval[digits] is >= '0' and <= '9') digits++;
        var factor = interval[digits..] switch
        {
            "ms" => 1L, "s" => 1000L, "m" => 60000L, "h" => 3600000L, "d" => 86400000L, _ => 0L
        };
        Require(factor > 0 && long.TryParse(interval.AsSpan(0, digits), NumberStyles.None,
            CultureInfo.InvariantCulture, out _), "invalid funding interval");
        var amount = long.Parse(interval.AsSpan(0, digits), NumberStyles.None, CultureInfo.InvariantCulture);
        Require(amount > 0 && amount <= long.MaxValue / factor, "invalid funding interval");
        // VenueReader.Scaled multiplies first; the public funding_8h field is rounded for display.
        // The raw funding field is required: never reconstruct it from the derived field.
        return Number(values, "funding") * 28800000m / (amount * factor);
    }
}
