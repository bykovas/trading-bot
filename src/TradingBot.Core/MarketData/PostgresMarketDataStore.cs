using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace TradingBot.Core.MarketData;

public sealed class PostgresMarketDataStore(string connectionString) : IMarketDataStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private bool _schemaReady;

    public void EnsureSchema()
    {
        if (_schemaReady)
        {
            return;
        }

        using var connection = OpenConnection();
        using var command = new NpgsqlCommand(
            """
            create table if not exists instrument_registry (
                venue text not null,
                pair text not null,
                kraken_symbol text not null,
                enabled boolean not null default true,
                quantity_decimals int,
                price_decimals int,
                updated_at timestamptz not null,
                primary key (venue, pair)
            );
            alter table instrument_registry add column if not exists quantity_decimals int;
            alter table instrument_registry add column if not exists price_decimals int;

            create table if not exists market_quotes (
                venue text not null,
                pair text not null,
                utc timestamptz not null,
                bid numeric not null,
                ask numeric not null,
                last numeric not null,
                volume24h numeric not null,
                change_percent numeric not null,
                funding_rate_percent numeric,
                mark_price numeric,
                index_price numeric,
                primary key (venue, pair)
            );

            create table if not exists market_candles (
                venue text not null,
                pair text not null,
                timeframe_minutes int not null,
                open_time timestamptz not null,
                open numeric not null,
                high numeric not null,
                low numeric not null,
                close numeric not null,
                volume numeric not null,
                primary key (venue, pair, timeframe_minutes, open_time)
            );

            create index if not exists ix_market_candles_lookup
                on market_candles (venue, pair, timeframe_minutes, open_time desc);

            create table if not exists market_orderbooks (
                venue text not null,
                pair text not null,
                utc timestamptz not null,
                bids_json jsonb not null,
                asks_json jsonb not null,
                primary key (venue, pair)
            );

            create table if not exists market_data_cycles (
                cycle_id text primary key,
                venue text not null,
                utc timestamptz not null,
                universe_count int not null,
                quote_count int not null,
                candle_pair_count int not null,
                duration_ms int not null,
                warnings text
            );

            create index if not exists ix_market_data_cycles_venue_utc
                on market_data_cycles (venue, utc desc);
            """,
            connection);
        command.ExecuteNonQuery();
        _schemaReady = true;
    }

    public void UpsertInstruments(IReadOnlyList<InstrumentRegistryRecord> instruments)
    {
        if (instruments.Count == 0)
        {
            return;
        }

        EnsureSchema();
        using var connection = OpenConnection();
        foreach (var instrument in instruments)
        {
            using var command = new NpgsqlCommand(
                """
                insert into instrument_registry (venue, pair, kraken_symbol, enabled, quantity_decimals, price_decimals, updated_at)
                values (@venue, @pair, @kraken_symbol, @enabled, @quantity_decimals, @price_decimals, @updated_at)
                on conflict (venue, pair) do update set
                    kraken_symbol = excluded.kraken_symbol,
                    enabled = excluded.enabled,
                    quantity_decimals = excluded.quantity_decimals,
                    price_decimals = excluded.price_decimals,
                    updated_at = excluded.updated_at
                """,
                connection);
            command.Parameters.AddWithValue("venue", instrument.Venue);
            command.Parameters.AddWithValue("pair", instrument.Pair);
            command.Parameters.AddWithValue("kraken_symbol", instrument.KrakenSymbol);
            command.Parameters.AddWithValue("enabled", instrument.Enabled);
            command.Parameters.AddWithValue("quantity_decimals", (object?)instrument.QuantityDecimals ?? DBNull.Value);
            command.Parameters.AddWithValue("price_decimals", (object?)instrument.PriceDecimals ?? DBNull.Value);
            command.Parameters.AddWithValue("updated_at", instrument.UpdatedAt.UtcDateTime);
            command.ExecuteNonQuery();
        }
    }

    public void UpsertQuotes(IReadOnlyList<SharedMarketQuoteRecord> quotes)
    {
        if (quotes.Count == 0)
        {
            return;
        }

        EnsureSchema();
        using var connection = OpenConnection();
        foreach (var quote in quotes)
        {
            using var command = new NpgsqlCommand(
                """
                insert into market_quotes (
                    venue, pair, utc, bid, ask, last, volume24h, change_percent,
                    funding_rate_percent, mark_price, index_price)
                values (
                    @venue, @pair, @utc, @bid, @ask, @last, @volume24h, @change_percent,
                    @funding_rate_percent, @mark_price, @index_price)
                on conflict (venue, pair) do update set
                    utc = excluded.utc,
                    bid = excluded.bid,
                    ask = excluded.ask,
                    last = excluded.last,
                    volume24h = excluded.volume24h,
                    change_percent = excluded.change_percent,
                    funding_rate_percent = excluded.funding_rate_percent,
                    mark_price = excluded.mark_price,
                    index_price = excluded.index_price
                """,
                connection);
            BindQuote(command, quote);
            command.ExecuteNonQuery();
        }
    }

    public void UpsertCandles(IReadOnlyList<SharedMarketCandleRecord> candles, int timeframeMinutes)
    {
        if (candles.Count == 0)
        {
            return;
        }

        EnsureSchema();
        using var connection = OpenConnection();
        foreach (var candle in candles)
        {
            using var command = new NpgsqlCommand(
                """
                insert into market_candles (
                    venue, pair, timeframe_minutes, open_time, open, high, low, close, volume)
                values (
                    @venue, @pair, @timeframe_minutes, @open_time, @open, @high, @low, @close, @volume)
                on conflict (venue, pair, timeframe_minutes, open_time) do update set
                    open = excluded.open,
                    high = excluded.high,
                    low = excluded.low,
                    close = excluded.close,
                    volume = excluded.volume
                """,
                connection);
            command.Parameters.AddWithValue("venue", candle.Venue);
            command.Parameters.AddWithValue("pair", candle.Pair);
            command.Parameters.AddWithValue("timeframe_minutes", timeframeMinutes);
            command.Parameters.AddWithValue("open_time", candle.OpenTime.UtcDateTime);
            command.Parameters.AddWithValue("open", candle.Open);
            command.Parameters.AddWithValue("high", candle.High);
            command.Parameters.AddWithValue("low", candle.Low);
            command.Parameters.AddWithValue("close", candle.Close);
            command.Parameters.AddWithValue("volume", candle.Volume);
            command.ExecuteNonQuery();
        }
    }

    public void UpsertOrderBooks(IReadOnlyList<SharedMarketOrderBookRecord> orderBooks)
    {
        if (orderBooks.Count == 0)
        {
            return;
        }

        EnsureSchema();
        using var connection = OpenConnection();
        foreach (var orderBook in orderBooks)
        {
            using var command = new NpgsqlCommand(
                """
                insert into market_orderbooks (venue, pair, utc, bids_json, asks_json)
                values (@venue, @pair, @utc, @bids_json, @asks_json)
                on conflict (venue, pair) do update set
                    utc = excluded.utc,
                    bids_json = excluded.bids_json,
                    asks_json = excluded.asks_json
                """,
                connection);
            command.Parameters.AddWithValue("venue", orderBook.Venue);
            command.Parameters.AddWithValue("pair", orderBook.Pair);
            command.Parameters.AddWithValue("utc", orderBook.Utc.UtcDateTime);
            command.Parameters.Add("bids_json", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(orderBook.Bids, JsonOptions);
            command.Parameters.Add("asks_json", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(orderBook.Asks, JsonOptions);
            command.ExecuteNonQuery();
        }
    }

    public void AppendCycle(MarketDataCycleRecord cycle)
    {
        EnsureSchema();
        using var connection = OpenConnection();
        using var command = new NpgsqlCommand(
            """
            insert into market_data_cycles (
                cycle_id, venue, utc, universe_count, quote_count, candle_pair_count, duration_ms, warnings)
            values (
                @cycle_id, @venue, @utc, @universe_count, @quote_count, @candle_pair_count, @duration_ms, @warnings)
            on conflict (cycle_id) do update set
                venue = excluded.venue,
                utc = excluded.utc,
                universe_count = excluded.universe_count,
                quote_count = excluded.quote_count,
                candle_pair_count = excluded.candle_pair_count,
                duration_ms = excluded.duration_ms,
                warnings = excluded.warnings
            """,
            connection);
        command.Parameters.AddWithValue("cycle_id", cycle.CycleId);
        command.Parameters.AddWithValue("venue", cycle.Venue);
        command.Parameters.AddWithValue("utc", cycle.Utc.UtcDateTime);
        command.Parameters.AddWithValue("universe_count", cycle.UniverseCount);
        command.Parameters.AddWithValue("quote_count", cycle.QuoteCount);
        command.Parameters.AddWithValue("candle_pair_count", cycle.CandlePairCount);
        command.Parameters.AddWithValue("duration_ms", cycle.DurationMs);
        command.Parameters.AddWithValue("warnings", (object?)cycle.Warnings ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<InstrumentRegistryRecord> LoadInstruments(string venue)
    {
        EnsureSchema();
        using var connection = OpenConnection();
        using var command = new NpgsqlCommand(
            """
            select venue, pair, kraken_symbol, enabled, updated_at, quantity_decimals, price_decimals
            from instrument_registry
            where venue = @venue and enabled = true
            order by pair
            """,
            connection);
        command.Parameters.AddWithValue("venue", venue);
        return ReadInstruments(command);
    }

    public IReadOnlyList<SharedMarketQuoteRecord> LoadQuotes(string venue, DateTimeOffset minUtc)
    {
        EnsureSchema();
        using var connection = OpenConnection();
        using var command = new NpgsqlCommand(
            """
            select venue, pair, utc, bid, ask, last, volume24h, change_percent,
                   funding_rate_percent, mark_price, index_price
            from market_quotes
            where venue = @venue and utc >= @min_utc
            order by pair
            """,
            connection);
        command.Parameters.AddWithValue("venue", venue);
        command.Parameters.AddWithValue("min_utc", minUtc.UtcDateTime);
        return ReadQuotes(command);
    }

    public IReadOnlyList<SharedMarketCandleRecord> LoadCandles(
        string venue,
        string pair,
        int timeframeMinutes,
        int maxBars)
    {
        EnsureSchema();
        using var connection = OpenConnection();
        using var command = new NpgsqlCommand(
            """
            select venue, pair, timeframe_minutes, open_time, open, high, low, close, volume
            from market_candles
            where venue = @venue and pair = @pair and timeframe_minutes = @timeframe_minutes
            order by open_time desc
            limit @max_bars
            """,
            connection);
        command.Parameters.AddWithValue("venue", venue);
        command.Parameters.AddWithValue("pair", pair);
        command.Parameters.AddWithValue("timeframe_minutes", timeframeMinutes);
        command.Parameters.AddWithValue("max_bars", Math.Max(1, maxBars));
        var candles = ReadCandles(command);
        return candles.OrderBy(candle => candle.OpenTime).ToList();
    }

    public SharedMarketOrderBookRecord? LoadOrderBook(string venue, string pair)
    {
        EnsureSchema();
        using var connection = OpenConnection();
        using var command = new NpgsqlCommand(
            """
            select venue, pair, utc, bids_json, asks_json
            from market_orderbooks
            where venue = @venue and pair = @pair
            """,
            connection);
        command.Parameters.AddWithValue("venue", venue);
        command.Parameters.AddWithValue("pair", pair);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new SharedMarketOrderBookRecord(
            reader.GetString(0),
            reader.GetString(1),
            new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc)),
            JsonSerializer.Deserialize<IReadOnlyList<OrderBookLevel>>(reader.GetString(3), JsonOptions) ?? Array.Empty<OrderBookLevel>(),
            JsonSerializer.Deserialize<IReadOnlyList<OrderBookLevel>>(reader.GetString(4), JsonOptions) ?? Array.Empty<OrderBookLevel>());
    }

    public IReadOnlyList<string> LoadHeldPairs(string venue)
    {
        EnsureSchema();
        var prefix = MarketDataVenue.BotInstancePrefix(venue);
        using var connection = OpenConnection();
        using var command = new NpgsqlCommand(
            """
            select distinct position ->> 'pair' as pair
            from portfolio_state
            cross join lateral jsonb_array_elements(coalesce(state_json -> 'positions', '[]'::jsonb)) as position
            where bot_instance_id like @prefix
              and coalesce(position ->> 'pair', '') <> ''
            """,
            connection);
        command.Parameters.AddWithValue("prefix", $"{prefix}%");
        var pairs = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            pairs.Add(reader.GetString(0));
        }

        return pairs;
    }

    public DateTimeOffset? LoadLatestQuoteUtc(string venue)
    {
        EnsureSchema();
        using var connection = OpenConnection();
        using var command = new NpgsqlCommand(
            """
            select max(utc)
            from market_quotes
            where venue = @venue
            """,
            connection);
        command.Parameters.AddWithValue("venue", venue);
        var value = command.ExecuteScalar();
        return value is DateTime utc
            ? new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc))
            : null;
    }

    private static void BindQuote(NpgsqlCommand command, SharedMarketQuoteRecord quote)
    {
        command.Parameters.AddWithValue("venue", quote.Venue);
        command.Parameters.AddWithValue("pair", quote.Pair);
        command.Parameters.AddWithValue("utc", quote.Utc.UtcDateTime);
        command.Parameters.AddWithValue("bid", quote.Bid);
        command.Parameters.AddWithValue("ask", quote.Ask);
        command.Parameters.AddWithValue("last", quote.Last);
        command.Parameters.AddWithValue("volume24h", quote.Volume24h);
        command.Parameters.AddWithValue("change_percent", quote.ChangePercent);
        command.Parameters.AddWithValue("funding_rate_percent", (object?)quote.FundingRatePercent ?? DBNull.Value);
        command.Parameters.AddWithValue("mark_price", (object?)quote.MarkPrice ?? DBNull.Value);
        command.Parameters.AddWithValue("index_price", (object?)quote.IndexPrice ?? DBNull.Value);
    }

    private static List<InstrumentRegistryRecord> ReadInstruments(NpgsqlCommand command)
    {
        var results = new List<InstrumentRegistryRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new InstrumentRegistryRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetBoolean(3),
                new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc)),
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6)));
        }

        return results;
    }

    private static List<SharedMarketQuoteRecord> ReadQuotes(NpgsqlCommand command)
    {
        var results = new List<SharedMarketQuoteRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new SharedMarketQuoteRecord(
                reader.GetString(0),
                reader.GetString(1),
                new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc)),
                reader.GetDecimal(3),
                reader.GetDecimal(4),
                reader.GetDecimal(5),
                reader.GetDecimal(6),
                reader.GetDecimal(7),
                reader.IsDBNull(8) ? null : reader.GetDecimal(8),
                reader.IsDBNull(9) ? null : reader.GetDecimal(9),
                reader.IsDBNull(10) ? null : reader.GetDecimal(10)));
        }

        return results;
    }

    private static List<SharedMarketCandleRecord> ReadCandles(NpgsqlCommand command)
    {
        var results = new List<SharedMarketCandleRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new SharedMarketCandleRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc)),
                reader.GetDecimal(4),
                reader.GetDecimal(5),
                reader.GetDecimal(6),
                reader.GetDecimal(7),
                reader.GetDecimal(8)));
        }

        return results;
    }

    private NpgsqlConnection OpenConnection()
    {
        var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        return connection;
    }
}
