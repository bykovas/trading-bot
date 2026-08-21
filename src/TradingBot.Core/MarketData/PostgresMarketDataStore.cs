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

            create table if not exists portfolio_position_state (
                bot_instance_id text not null,
                updated_at timestamptz not null,
                position_index integer not null,
                pair text not null,
                side text not null,
                quantity numeric not null,
                entry_price numeric not null,
                entry_notional_eur numeric not null,
                last_price numeric not null,
                market_value_eur numeric not null,
                unrealized_pnl_eur numeric not null,
                unrealized_pnl_percent numeric not null,
                opened_at_utc timestamptz,
                last_action_at_utc timestamptz,
                peak_pnl_percent numeric,
                entry_score numeric,
                exit_mode text,
                entry_atr numeric,
                stop_loss_price numeric,
                take_profit_price numeric,
                round_trip_cost_estimate_pct numeric,
                expected_funding_pct numeric,
                atr_pct numeric,
                stop_distance_pct numeric,
                take_profit_distance_pct numeric,
                exchange_stop_loss_price numeric,
                exchange_take_profit_price numeric,
                exchange_protection_multiplier_percent numeric,
                trailing_stop_state text,
                trailing_stop_percent numeric,
                trailing_stop_order_id text,
                trailing_activated_at_utc timestamptz,
                low_score_cycles integer not null default 0,
                leverage numeric,
                initial_margin_eur numeric,
                mark_price numeric,
                liquidation_price numeric,
                liquidation_distance_percent numeric,
                funding_paid_eur numeric,
                tp_order_state text,
                sl_order_state text,
                origin text,
                entry_channel text,
                primary key (bot_instance_id, position_index)
            );

            create index if not exists ix_portfolio_position_state_pair on portfolio_position_state (bot_instance_id, pair);
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
        // One transaction for the whole batch. Each statement outside one is its own
        // implicit transaction and pays a WAL flush; at ~3.5ms a row that is what made
        // these sweeps slow.
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
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
                connection,
                transaction);
            command.Parameters.AddWithValue("venue", instrument.Venue);
            command.Parameters.AddWithValue("pair", instrument.Pair);
            command.Parameters.AddWithValue("kraken_symbol", instrument.KrakenSymbol);
            command.Parameters.AddWithValue("enabled", instrument.Enabled);
            command.Parameters.AddWithValue("quantity_decimals", (object?)instrument.QuantityDecimals ?? DBNull.Value);
            command.Parameters.AddWithValue("price_decimals", (object?)instrument.PriceDecimals ?? DBNull.Value);
            command.Parameters.AddWithValue("updated_at", instrument.UpdatedAt.UtcDateTime);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void UpsertQuotes(IReadOnlyList<SharedMarketQuoteRecord> quotes)
    {
        if (quotes.Count == 0)
        {
            return;
        }

        EnsureSchema();
        // One transaction for the whole batch. Each statement outside one is its own
        // implicit transaction and pays a WAL flush; at ~3.5ms a row that is what made
        // these sweeps slow.
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
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
                connection,
                transaction);
            BindQuote(command, quote);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    // A sweep brings back ~11k futures candles (and used to bring 51k for spot). Writing
    // them one statement at a time meant that many SQL parses, round trips and - because
    // each ran outside a transaction - that many WAL flushes: measured at roughly 3.5ms
    // per row, which was ~39 of the 55 seconds a futures sweep took. The HTTP calls it
    // was blamed on account for 14: 93 pairs x 2 requests at ~75ms each.
    //
    // Binary COPY into a temp table, then one upsert out of it. The same idiom the
    // snapshot writer has used all along.
    public void UpsertCandles(IReadOnlyList<SharedMarketCandleRecord> candles, int timeframeMinutes)
    {
        if (candles.Count == 0)
        {
            return;
        }

        EnsureSchema();
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var create = new NpgsqlCommand(
            """
            create temp table tmp_market_candles (
                venue text, pair text, timeframe_minutes int, open_time timestamptz,
                open numeric, high numeric, low numeric, close numeric, volume numeric
            ) on commit drop
            """,
            connection,
            transaction))
        {
            create.ExecuteNonQuery();
        }

        using (var writer = connection.BeginBinaryImport(
            """
            copy tmp_market_candles (
                venue, pair, timeframe_minutes, open_time, open, high, low, close, volume)
            from stdin (format binary)
            """))
        {
            foreach (var candle in candles)
            {
                writer.StartRow();
                writer.Write(candle.Venue, NpgsqlDbType.Text);
                writer.Write(candle.Pair, NpgsqlDbType.Text);
                writer.Write(timeframeMinutes, NpgsqlDbType.Integer);
                writer.Write(candle.OpenTime.UtcDateTime, NpgsqlDbType.TimestampTz);
                writer.Write(candle.Open, NpgsqlDbType.Numeric);
                writer.Write(candle.High, NpgsqlDbType.Numeric);
                writer.Write(candle.Low, NpgsqlDbType.Numeric);
                writer.Write(candle.Close, NpgsqlDbType.Numeric);
                writer.Write(candle.Volume, NpgsqlDbType.Numeric);
            }

            writer.Complete();
        }

        // `is distinct from` so a re-fetched candle that has not moved is left alone:
        // rewriting it with identical values still costs a new row version and a dead
        // tuple, and a closed candle never changes.
        using (var merge = new NpgsqlCommand(
            """
            insert into market_candles (
                venue, pair, timeframe_minutes, open_time, open, high, low, close, volume)
            select distinct on (venue, pair, timeframe_minutes, open_time)
                venue, pair, timeframe_minutes, open_time, open, high, low, close, volume
            from tmp_market_candles
            order by venue, pair, timeframe_minutes, open_time
            on conflict (venue, pair, timeframe_minutes, open_time) do update set
                open = excluded.open,
                high = excluded.high,
                low = excluded.low,
                close = excluded.close,
                volume = excluded.volume
            where market_candles.open is distinct from excluded.open
               or market_candles.high is distinct from excluded.high
               or market_candles.low is distinct from excluded.low
               or market_candles.close is distinct from excluded.close
               or market_candles.volume is distinct from excluded.volume
            """,
            connection,
            transaction))
        {
            merge.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void UpsertOrderBooks(IReadOnlyList<SharedMarketOrderBookRecord> orderBooks)
    {
        if (orderBooks.Count == 0)
        {
            return;
        }

        EnsureSchema();
        // One transaction for the whole batch. Each statement outside one is its own
        // implicit transaction and pays a WAL flush; at ~3.5ms a row that is what made
        // these sweeps slow.
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
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
                connection,
                transaction);
            command.Parameters.AddWithValue("venue", orderBook.Venue);
            command.Parameters.AddWithValue("pair", orderBook.Pair);
            command.Parameters.AddWithValue("utc", orderBook.Utc.UtcDateTime);
            command.Parameters.Add("bids_json", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(orderBook.Bids, JsonOptions);
            command.Parameters.Add("asks_json", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(orderBook.Asks, JsonOptions);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
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
            select distinct pair
            from portfolio_position_state
            where bot_instance_id like @prefix
              and coalesce(pair, '') <> ''
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
