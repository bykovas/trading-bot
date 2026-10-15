using System.Net;
using System.Text.Json;
using TradingBot.FuturesWorker;
using Xunit;

namespace TradingBot.FuturesWorker.Tests;

public sealed class DebykoMarketClientTests
{
    [Fact]
    public async Task ExitIndicatorsUse120ClosedBarsWithoutExtraMarketRequests()
    {
        var calls = 0;
        var client = Client(async request =>
        {
            calls++;
            Assert.Equal("/v2/candles", request.RequestUri!.AbsolutePath);
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var query = document.RootElement;
            Assert.Equal(120, query.GetProperty("limit").GetInt32());
            Assert.Equal("15m", query.GetProperty("timeframe").GetString());
            Assert.True(query.GetProperty("closed_only").GetBoolean());
            var from = query.GetProperty("from").GetDateTimeOffset();
            var to = query.GetProperty("to").GetDateTimeOffset();
            return Response(new { data_as_of = Cut, from, to, timeframe = "15m", price_type = "trade", units = "published",
                items = new[] { new { instrument = "BTC-PERP", venue = "KRAKEN-FUTURES", quote = "USD", status = "present",
                    candles = Enumerable.Range(0, 120).Select(i => new
                    {
                        open_time = from.AddMinutes(15 * i), close_time = from.AddMinutes(15 * (i + 1)), received_at = Cut,
                        status = "present", open = "100", high = "102", low = "99", close = "101", volume = "10", trade_count = 1
                    }) } } });
        });
        var read = await client.GetExitFrameAsync("XBT/USD", End, Cut, 15, 120, default);
        Assert.NotNull(read.Frame);
        Assert.Equal(120, read.Frame.FifteenMinute.Count);
        Assert.Equal(1, calls);
        Assert.Null(read.Reason);
    }

    [Theory]
    [InlineData("present", true)]
    [InlineData("stale", false)]
    public async Task LiveExecutionSnapshotsAreBatchedAndFailClosed(string status, bool available)
    {
        var client = Client(async request =>
        {
            Assert.Equal("/v2/snapshots", request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.False(body.RootElement.GetProperty("allow_stale").GetBoolean());
            Assert.Equal("venue = KRAKEN-FUTURES", body.RootElement.GetProperty("where").GetString());
            return Response(new { items = new[] { "BTC", "ETH" }.Select(asset => new
            {
                instrument = asset + "-PERP", venue = "KRAKEN-FUTURES", quote = "USD",
                layers = new
                {
                    mark = new { status, values = new { mark = "100" } },
                    ticker = new { status, values = new { last = "101" } },
                    book = new { status, values = new { bid = "99", ask = "102" } }
                }
            }).ToArray() });
        });
        var states = await client.GetLiveStatesAsync(new[]
        {
            new TradingBot.Core.Common.InstrumentOptions { Pair = "BTC/USD" },
            new TradingBot.Core.Common.InstrumentOptions { Pair = "ETH/USD" }
        }, default);
        Assert.Equal(2, states.Count);
        foreach (var state in states)
        {
            Assert.Equal(available, state.Quote is not null);
            Assert.Empty(state.Candles);
            if (available) Assert.Equal(100m, state.Quote!.MarkPrice);
            else Assert.NotNull(state.DataWarning);
        }
    }

    [Theory]
    [InlineData(41)]
    [InlineData(65)]
    public async Task LiveSnapshotsUseOneVenueRequestAndLocallySelectEveryRequestedInstrument(int count)
    {
        var instruments = Enumerable.Range(0, count).Select(i =>
            new TradingBot.Core.Common.InstrumentOptions { Pair = $"ASSET{i}/USD", Enabled = true }).ToArray();
        static object Listing(string instrument, string venue, string quote) => new
        {
            instrument, venue, quote,
            layers = new
            {
                mark = new { status = "present", values = new { mark = "100" } },
                ticker = new { status = "present", values = new { last = "101" } },
                book = new { status = "present", values = new { bid = "99", ask = "102" } }
            }
        };
        var items = instruments.Reverse()
            .Select(x => Listing(DebykoMarketClient.ToInstrument(x.Pair), "KRAKEN-FUTURES", "USD"))
            .Concat(new[]
            {
                Listing("UNREQUESTED-PERP", "KRAKEN-FUTURES", "USD"),
                Listing("ASSET0-PERP", "OTHER-VENUE", "USD"),
                Listing("ASSET0-PERP", "KRAKEN-FUTURES", "USDT")
            }).ToArray();
        var calls = 0;
        var client = Client(async request =>
        {
            calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v2/snapshots", request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("venue = KRAKEN-FUTURES", body.RootElement.GetProperty("where").GetString());
            Assert.Equal(new[] { "ticker", "mark", "book", "funding" },
                body.RootElement.GetProperty("layers").EnumerateArray().Select(x => x.GetString()));
            Assert.False(body.RootElement.GetProperty("allow_stale").GetBoolean());
            return Response(new { items });
        });

        var states = await client.GetLiveStatesAsync(instruments, default);

        Assert.Equal(1, calls);
        Assert.Equal(instruments.Select(x => x.Pair), states.Select(x => x.Instrument.Pair));
        Assert.All(states, state =>
        {
            Assert.NotNull(state.Quote);
            Assert.Equal(100m, state.Quote.MarkPrice);
            Assert.Null(state.DataWarning);
        });
    }

    private static readonly DateTimeOffset End = DateTimeOffset.Parse("2026-10-10T12:07:00Z");
    private static readonly DateTimeOffset Cut = End.AddSeconds(15);

    [Theory]
    [InlineData("XBT/USD", "BTC-PERP")]
    [InlineData("SOL/USD", "SOL-PERP")]
    [InlineData("BTC-PERP", "BTC-PERP")]
    public void ConvertsPairs(string pair, string expected) => Assert.Equal(expected, DebykoMarketClient.ToInstrument(pair));

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("unknown", null)]
    public async Task ReadsOfficialResult(string verdict, bool? expected)
    {
        var id = Guid.NewGuid();
        var client = Client(async request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Contains("/v2/agent/result?rule_id=" + id, request.RequestUri!.ToString());
            Assert.Contains("instrument=BTC-PERP", request.RequestUri.ToString());
            await Task.CompletedTask;
            return Response(new { rule_id = id, version = "abcd", instrument = "BTC-PERP", bar_end = End,
                data_as_of = Cut, attempt = 2, verdict, reason = "measured", final = true, receipt_id = "result-receipt" });
        });
        var result = await client.GetResultAsync(id, "XBT/USD", End.ToOffset(TimeSpan.FromHours(3)), default);
        Assert.Equal(expected, result.Verdict);
        Assert.Equal(2, result.Attempt);
        Assert.Equal(Cut, result.DataAsOf);
        Assert.Equal(TimeSpan.Zero, result.BarEnd.Offset);
        Assert.Contains("result-receipt", result.RawBody);
    }

    [Fact]
    public async Task MissingResultIsUnknownNotFalse()
    {
        var id = Guid.NewGuid();
        var client = Client(_ => Task.FromResult(Response(new { rule_id = id, instrument = "BTC-PERP", bar_end = End,
            verdict = "unknown", reason = "not_measured", final = false })));
        var result = await client.GetResultAsync(id, "BTC-PERP", End, default);
        Assert.Null(result.Verdict);
        Assert.Null(result.DataAsOf);
        Assert.Equal(0, result.Attempt);
        Assert.False(result.Final);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("liquidation", true)]
    [InlineData("truncated", true)]
    [InlineData("liquidation-gap", true)]
    [InlineData("liquidation-unknown", true)]
    [InlineData("liquidation-invisible", true)]
    [InlineData("liquidation-left-bound", true)]
    [InlineData("liquidation-wrong-cut", false)]
    [InlineData("missing-interval", true)]
    [InlineData("zero-interval", true)]
    [InlineData("four-hour-funding", true)]
    [InlineData("precise-funding", true)]
    [InlineData("missing-rate", true)]
    [InlineData("gap", true)]
    [InlineData("stale", true)]
    [InlineData("wrong-cut", false)]
    [InlineData("short", true)]
    [InlineData("null-value", false)]
    public async Task QueriesExactCutAndFailsClosed(string? defect, bool usable)
    {
        var requests = new List<string>();
        var client = Client(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            var raw = await request.Content!.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(raw);
            var query = doc.RootElement;
            var path = request.RequestUri!.AbsolutePath;
            requests.Add(path);
            Assert.Equal("published", query.GetProperty("units").GetString());
            Assert.Equal("string", query.GetProperty("number_format").GetString());
            Assert.True(query.GetProperty("include_gaps").GetBoolean());
            Assert.Equal("venue = KRAKEN-FUTURES and instrument = BTC-PERP and quote = USD", query.GetProperty("where").GetString());
            if (path == "/v2/candles")
            {
                Assert.Equal(Cut, query.GetProperty("as_of").GetDateTimeOffset());
                Assert.Equal("trade", query.GetProperty("price_type").GetString());
                Assert.True(query.GetProperty("closed_only").GetBoolean());
                var tf = query.GetProperty("timeframe").GetString()!;
                var (minutes, count) = tf switch { "1m" => (1, 80), "5m" => (5, 20), "15m" => (15, 80), "1h" => (60, 240), _ => throw new Exception() };
                var step = TimeSpan.FromMinutes(minutes);
                var to = new DateTimeOffset(End.UtcTicks - End.UtcTicks % step.Ticks, TimeSpan.Zero);
                var from = to - count * step;
                Assert.Equal(to, query.GetProperty("to").GetDateTimeOffset());
                Assert.Equal(from, query.GetProperty("from").GetDateTimeOffset());
                Assert.Equal(count, query.GetProperty("limit").GetInt32());
                var candles = Enumerable.Range(0, defect == "short" ? count - 1 : count).Select(i => new
                {
                    open_time = from + step * i, close_time = from + step * (i + 1),
                    status = defect == "gap" && i == count - 1 ? "gap" : "present",
                    reason = defect == "gap" && i == count - 1 ? "collector_gap" : null,
                    open = "100", high = "102", low = "99", close = "101", volume = defect == "null-value" ? null : "10", trade_count = (int?)null,
                    venue_ts = from + step * (i + 1), received_at = Cut
                });
                return Response(new { data_as_of = defect == "wrong-cut" ? Cut.AddSeconds(1) : Cut,
                    receipt_id = "candle-receipt", items_sha256 = "official-hash", units = "published", timeframe = tf,
                    price_type = "trade", from, to, items = new[] { new { instrument = "BTC-PERP", venue = "KRAKEN-FUTURES", quote = "USD", status = "present", candles } } });
            }
            if (path == "/v2/snapshots/history")
            {
                Assert.Equal(Cut, query.GetProperty("at")[0].GetDateTimeOffset());
                Assert.Equal(new[] { "ticker", "funding", "stats", "book" }, query.GetProperty("layers").EnumerateArray().Select(x => x.GetString()));
                Assert.False(query.GetProperty("allow_stale").GetBoolean());
                object Layer(object values) => new { rows = new[] { new { at = Cut, status = defect == "stale" ? "stale" : "present",
                    venue_ts = Cut.AddSeconds(-1), received_at = Cut.AddSeconds(-1), age_ms = 1000, values } } };
                var layers = new Dictionary<string, object>
                {
                    ["ticker"] = Layer(new { last = "101" }), ["book"] = Layer(new { bid = "100", ask = "101" }),
                    ["funding"] = Layer(new { funding = defect switch
                            { "missing-rate" => null, "precise-funding" => "0.00001234567890123456789", _ => "0.0000125" }, funding_interval = defect switch
                            { "missing-interval" => null, "zero-interval" => "0h", "four-hour-funding" => "4h", _ => "1h" },
                            funding_8h = defect switch { "four-hour-funding" => "0.000025", "precise-funding" => "0.000098765432", _ => "0.0001" } }),
                    ["stats"] = Layer(new { turnover_24h = "1000000" })
                };
                if (defect == "missing-book") layers.Remove("book");
                return Response(new { mode = "at", units = "published", receipt_id = "history-receipt", items = new[] { new
                {
                    instrument = "BTC-PERP", venue = "KRAKEN-FUTURES", quote = "USD", layers
                } } });
            }
            Assert.Equal("/v2/liquidations", path);
            Assert.Equal(Cut, query.GetProperty("as_of").GetDateTimeOffset());
            Assert.Equal(End.AddMinutes(-4), query.GetProperty("from").GetDateTimeOffset());
            Assert.Equal(End, query.GetProperty("to").GetDateTimeOffset());
            if (defect is "liquidation" or "truncated" || defect?.StartsWith("liquidation-", StringComparison.Ordinal) == true)
                return Response(new { data_as_of = defect == "liquidation-wrong-cut" ? Cut.AddSeconds(1) : Cut,
                    from = End.AddMinutes(-4), to = End, units = "published", items = new[] { new
                {
                    instrument = "BTC-PERP", venue = "KRAKEN-FUTURES", quote = "USD",
                    status = defect == "liquidation-unknown" ? "missing" : "present", truncated = defect == "truncated",
                    units = new { price = "quote", size = "base" }, gaps = defect == "liquidation-gap"
                        ? new object[] { new { from = End.AddMinutes(-2), to = End, reason = "collector_gap", gap_cause = "connection_lost" } } : Array.Empty<object>(),
                    events = new[] { new { event_time = defect == "liquidation-left-bound" ? End.AddMinutes(-4) : End.AddSeconds(-1),
                        received_at = defect == "liquidation-invisible" ? Cut.AddSeconds(1) : Cut, price = "100", size = "2", side = "sell" } }
                } } });
            return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("as_of not deployed") };
        });
        var read = await client.GetFrameAsync("XBT/USD", End, Cut, default);
        Assert.Equal(usable, read.Frame is not null);
        Assert.NotEmpty(read.RawBodies);
        Assert.All(read.RawBodies, raw => Assert.DoesNotContain("test-secret", raw));
        if (usable)
        {
            Assert.Equal(6, requests.Count);
            Assert.Equal(defect is "gap" or "short" ? 79 : 80, read.Frame!.OneMinute.Count);
            Assert.Equal(End.AddMinutes(defect is "gap" or "short" ? -2 : -1), read.Frame.OneMinute[^1].OpenTime);
            Assert.Equal(TimeSpan.Zero, read.Frame.OneMinute[^1].OpenTime.Offset);
            Assert.Equal(defect switch { "missing-rate" or "missing-interval" or "zero-interval" or "stale" => (decimal?)null,
                "four-hour-funding" => 0.000025m,
                "precise-funding" => 0.00001234567890123456789m * 28800000m / 3600000m,
                _ => 0.0001m }, read.Frame.Funding8hFraction);
            Assert.Equal(defect switch { "gap" => false, "short" => (bool?)null, _ => true }, read.Frame.CandleGapFree!["1m"]);
            if (defect == "missing-book")
            {
                Assert.Null(read.Frame.Bid);
                Assert.Null(read.Frame.Ask);
                Assert.Equal(1000000m, read.Frame.Turnover24hUsd);
                Assert.Equal(0.0001m, read.Frame.Funding8hFraction);
                Assert.Equal(240, read.Frame.OneHour.Count);
            }
            if (defect == "liquidation")
            {
                Assert.Equal(200m, read.Frame.Liquidations!.LongValueUsd);
                Assert.Equal(0m, read.Frame.Liquidations.ShortValueUsd);
                Assert.True(read.Frame.Liquidations.Complete);
                Assert.Null(read.Reason);
            }
            else
            {
                Assert.Null(read.Frame.Liquidations);
                Assert.Contains("Flush blocked", read.Reason);
            }
            Assert.Equal(64, read.Responses[0].BodySha256.Length);
            Assert.Equal("receipt-header", read.Responses[0].ReceiptHeaders["X-Receipt-Id"][0]);
            Assert.Equal(defect is "liquidation" or "truncated" || defect?.StartsWith("liquidation-", StringComparison.Ordinal) == true
                ? 200 : 400, read.Responses[^1].StatusCode);
        }
    }

    [Fact]
    public async Task RecordedCandleGapPreservesFrameAndKnownFalseWithoutFabricatedOhlc() =>
        await QueriesExactCutAndFailsClosed("gap", true);

    [Fact]
    public async Task MissingBookPreservesCandlesAndOtherSnapshotValues() =>
        await QueriesExactCutAndFailsClosed("missing-book", true);

    [Fact]
    public async Task RuleSpecificFramesDoNotRequireUnrelatedInputs()
    {
        foreach (var rule in new[] { BykoV2Rule.BreakoutLong, BykoV2Rule.BreakoutShort, BykoV2Rule.FlushLong, BykoV2Rule.FlushShort })
        foreach (var reference in new[] { false, true })
        {
            var flush = rule is BykoV2Rule.FlushLong or BykoV2Rule.FlushShort;
            var expected = flush ? reference ? new[] { ("5m", 13) } : new[] { ("1m", 57) }
                : new[] { ("15m", reference ? 1 : 57), ("1h", 203) };
            var candleQueries = new List<(string, int)>();
            var paths = new List<string>();
            var client = Client(async request =>
            {
                using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                var query = document.RootElement;
                var path = request.RequestUri!.AbsolutePath;
                paths.Add(path);
                if (path == "/v2/candles")
                {
                    var tf = query.GetProperty("timeframe").GetString()!;
                    var count = query.GetProperty("limit").GetInt32();
                    candleQueries.Add((tf, count));
                    var from = query.GetProperty("from").GetDateTimeOffset();
                    var to = query.GetProperty("to").GetDateTimeOffset();
                    var step = TimeSpan.FromTicks((to - from).Ticks / count);
                    return Response(new { data_as_of = Cut, from, to, timeframe = tf, price_type = "trade", units = "published",
                        items = new[] { new { instrument = "BTC-PERP", venue = "KRAKEN-FUTURES", quote = "USD", status = "present",
                            candles = Enumerable.Range(0, count).Select(i => new
                            {
                                open_time = from + i * step, close_time = from + (i + 1) * step, received_at = Cut,
                                status = "present", open = "100", high = "102", low = "99", close = "101", volume = "10", trade_count = 1
                            }) } } });
                }
                Assert.False(reference);
                if (path == "/v2/snapshots/history")
                {
                    var names = query.GetProperty("layers").EnumerateArray().Select(x => x.GetString()!).ToArray();
                    Assert.Equal(flush ? new[] { "book", "stats" } : new[] { "book", "funding" }, names);
                    var layers = names.ToDictionary(name => name, name => (object)new
                    {
                        rows = new[] { new { at = Cut, status = "present", venue_ts = Cut.AddSeconds(-1), received_at = Cut.AddSeconds(-1),
                            values = name switch
                            {
                                "book" => (object)new { bid = "100", ask = "101" },
                                "stats" => new { turnover_24h = "1000000" },
                                // A mismatched display field must not override raw-rate arithmetic.
                                _ => new { funding = "0.0000125", funding_8h = "0.00009", funding_interval = "1h" }
                            } } }
                    });
                    return Response(new { mode = "at", units = "published", items = new[] { new
                        { instrument = "BTC-PERP", venue = "KRAKEN-FUTURES", quote = "USD", layers } } });
                }
                Assert.True(flush);
                Assert.Equal("/v2/liquidations", path);
                return Response(new { data_as_of = Cut, from = End.AddMinutes(-4), to = End, units = "published", items = new[] { new
                    { instrument = "BTC-PERP", venue = "KRAKEN-FUTURES", quote = "USD", status = "present", truncated = false,
                        units = new { size = "base", price = "quote" }, gaps = Array.Empty<object>(), events = Array.Empty<object>() } } });
            });
            var read = await client.GetFrameAsync("XBT/USD", End, Cut, default, rule, reference);
            Assert.NotNull(read.Frame);
            Assert.Null(read.Reason);
            Assert.Equal(expected, candleQueries);
            Assert.Equal(expected.Length + (reference ? 0 : flush ? 2 : 1), paths.Count);
            Assert.Equal(flush && !reference ? 57 : 0, read.Frame.OneMinute.Count);
            Assert.Equal(flush && reference ? 13 : 0, read.Frame.FiveMinute.Count);
            Assert.Equal(flush ? 0 : reference ? 1 : 57, read.Frame.FifteenMinute.Count);
            Assert.Equal(flush ? 0 : 203, read.Frame.OneHour.Count);
            if (!flush && !reference) Assert.Equal(0.0001m, read.Frame.Funding8hFraction);
            if (reference)
            {
                Assert.Null(read.Frame.Bid);
                Assert.Null(read.Frame.Funding8hFraction);
                Assert.Null(read.Frame.Turnover24hUsd);
            }
        }
    }

    [Fact]
    public async Task UnauthorizedDoesNotExposeBodyOrHeaders()
    {
        var client = Client(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("test-secret") };
            response.Headers.TryAddWithoutValidation("WWW-Authenticate", "test-secret");
            return Task.FromResult(response);
        });
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetResultAsync(Guid.NewGuid(), "BTC-PERP", End, default));
        Assert.DoesNotContain("test-secret", error.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
    }

    private static DebykoMarketClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) =>
        new(new HttpClient(new Handler(handle)) { BaseAddress = new Uri("https://api.debyko.com") }, "test-secret");
    private static HttpResponseMessage Response(object value)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };
        response.Headers.Add("X-Receipt-Id", "receipt-header");
        return response;
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("test-secret", request.Headers.Authorization.Parameter);
            if (request.Content is { } content)
            {
                using var body = JsonDocument.Parse(await content.ReadAsStringAsync(cancellationToken));
                foreach (var property in body.RootElement.EnumerateObject())
                {
                    if (property.Name is "from" or "to" or "as_of") Assert.EndsWith("Z", property.Value.GetString());
                    if (property.Name == "at")
                        Assert.All(property.Value.EnumerateArray(), value => Assert.EndsWith("Z", value.GetString()));
                }
            }
            else
            {
                var query = Uri.UnescapeDataString(request.RequestUri!.Query);
                Assert.EndsWith("Z", query.Split("bar_end=", StringSplitOptions.None)[1]);
            }
            return await handle(request);
        }
    }
}
