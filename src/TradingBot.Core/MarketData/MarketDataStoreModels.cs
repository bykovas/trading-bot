namespace TradingBot.Core.MarketData;

public sealed record InstrumentRegistryRecord(
    string Venue,
    string Pair,
    string KrakenSymbol,
    bool Enabled,
    DateTimeOffset UpdatedAt,
    int? QuantityDecimals = null,
    int? PriceDecimals = null);

public sealed record SharedMarketQuoteRecord(
    string Venue,
    string Pair,
    DateTimeOffset Utc,
    decimal Bid,
    decimal Ask,
    decimal Last,
    decimal Volume24h,
    decimal ChangePercent,
    decimal? FundingRatePercent = null,
    decimal? MarkPrice = null,
    decimal? IndexPrice = null);

public sealed record SharedMarketCandleRecord(
    string Venue,
    string Pair,
    int TimeframeMinutes,
    DateTimeOffset OpenTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume);

public sealed record SharedMarketOrderBookRecord(
    string Venue,
    string Pair,
    DateTimeOffset Utc,
    IReadOnlyList<OrderBookLevel> Bids,
    IReadOnlyList<OrderBookLevel> Asks);

public sealed record MarketDataCycleRecord(
    string CycleId,
    string Venue,
    DateTimeOffset Utc,
    int UniverseCount,
    int QuoteCount,
    int CandlePairCount,
    int DurationMs,
    string? Warnings);

public sealed record SharedStrategyCandleRecord(
    string Venue,
    string Pair,
    int TimeframeMinutes,
    DateTimeOffset OpenTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume);

public sealed record SharedLiquidationRecord(
    string Venue,
    string Pair,
    string EventId,
    DateTimeOffset EventTime,
    string Side,
    string Type,
    decimal Price,
    decimal Quantity,
    decimal ValueUsd,
    DateTimeOffset ReceivedAt);

public sealed record SharedLiquidationWindow(
    decimal LongValueUsd,
    decimal ShortValueUsd,
    bool Complete,
    DateTimeOffset? LastGapAt);
