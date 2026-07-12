namespace TradingBot.Core.Common;

public static class BotInstanceId
{
    public static string Normalize(string? value)
    {
        value = string.IsNullOrWhiteSpace(value) ? "default" : value.Trim().ToLowerInvariant();
        var chars = value
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-')
            .ToArray();
        var normalized = new string(chars).Trim('-', '_');
        return string.IsNullOrWhiteSpace(normalized) ? "default" : normalized;
    }
}

public sealed class BotInstanceOptions
{
    public string Id { get; set; } = "default";
    public string Name { get; set; } = "default";
}

public sealed class WorkerOptions
{
    public bool RunOnce { get; set; } = true;
    public int LoopIntervalSeconds { get; set; } = 300;

    // Optional light-market polling cadence between full decision cycles. When set
    // below LoopIntervalSeconds, the spot worker persists extra ticker snapshots and
    // feeds the price-action guard without running full candle/scoring/order logic.
    // 0 disables the extra polling path.
    public int MarketSnapshotIntervalSeconds { get; set; } = 0;
}

public sealed class HttpOptions
{
    public int TimeoutSeconds { get; set; } = 20;
}

public sealed class KrakenOptions
{
    public string MarketDataMode { get; set; } = "sample";
    public string BaseUrl { get; set; } = "https://api.kraken.com";
    public string ApiKey { get; set; } = string.Empty;
    public string ApiSecret { get; set; } = string.Empty;
}

public sealed class UniverseDiscoveryOptions
{
    public bool Enabled { get; set; } = true;
    public int RefreshSeconds { get; set; } = 3600;
    public bool IncludeConfiguredUniverse { get; set; } = true;
    public List<string> ForceInclude { get; set; } = new();
    public List<string> Blacklist { get; set; } = new();
}

public sealed class AiOptions
{
    public string Provider { get; set; } = "none";
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public int MaxRecommendations { get; set; } = 2;
    public int WatchlistRefreshSeconds { get; set; } = 3600;
    public bool UseJsonResponseFormat { get; set; } = true;
}

public sealed class LoggingOptions
{
    public string Directory { get; set; } = "logs";
}

public sealed class TradingOptions
{
    public bool LiveTradingEnabled { get; set; } = false;
    public int TimeframeMinutes { get; set; } = 5;
    public int MaxActiveInstruments { get; set; } = 2;
    public decimal TargetOrderEur { get; set; } = 3m;

    // Dry-run deterministic backfill for pairs the AI/volume watchlist did not
    // select but whose current ticker is already moving cleanly. This only broadens
    // virtual evaluation; live mode keeps the advisor-selected universe.
    public bool StrongMoverBackfillEnabled { get; set; } = false;
    public decimal StrongMoverMinChangePercent { get; set; } = 4m;
    public decimal StrongMoverMaxSpreadPercent { get; set; } = 0.35m;
    public decimal StrongMoverMinDailyVolumeEur { get; set; } = 100_000m;
    public int StrongMoverMaxBackfillPairs { get; set; } = 5;
}

public sealed class StrategyOptions
{
    public int FastEmaPeriod { get; set; } = 9;
    public int SlowEmaPeriod { get; set; } = 21;
    public int RsiPeriod { get; set; } = 14;
    public decimal MinimumEmaGapPercent { get; set; } = 0.05m;
    public decimal MinimumLongScore { get; set; } = 0.55m;
    public decimal RsiIdealMin { get; set; } = 45m;
    public decimal RsiIdealMax { get; set; } = 62m;
    public int MomentumLookbackBars { get; set; } = 4;
    public decimal MomentumMinPercent { get; set; } = 0.2m;
    public int TrendFilterMaPeriod { get; set; } = 50;
    public decimal VolumeConfirmationMultiple { get; set; } = 1.2m;
    public decimal MaxEntrySpreadPercent { get; set; } = 0.5m;

    // Exit hysteresis: a held position keeps its LONG desire unless a CONFIRMED
    // bearish EMA cross appears (fast below slow by at least this gap). A merely
    // weak-but-not-bearish signal becomes a HOLD, not a flip. 0 restores the old
    // behavior (flip to NONE as soon as the bullish entry score is lost).
    public decimal ExitEmaGapPercent { get; set; } = 0.15m;

    // ---- Anti-lag recent price action (light-snapshot ticker series) ----
    // Number of most recent per-cycle snapshots the trend / rolling average is
    // computed over. At a 5-minute loop, 6 snapshots ~= the last 30 minutes.
    public int PriceActionLookbackSnapshots { get; set; } = 6;

    // Minimum snapshots required before the price-action guard may reject anything.
    // Below this the guard abstains (it blocks on evidence, never on missing data).
    public int PriceActionMinSnapshots { get; set; } = 4;

    // Hard anti-lag guard: reject a LONG entry when the last price declined at least
    // this percent over the lookback window. 0 disables the decline rule.
    public decimal PriceActionMaxDeclinePercent { get; set; } = 0.5m;

    // Reject a LONG entry when the last price failed to rise for this many
    // consecutive snapshots while the lookback trend is non-positive. 0 disables.
    public int PriceActionMaxNonRisingSnapshots { get; set; } = 3;

    // Score penalty applied to a bullish signal whose recent snapshot trend is
    // negative (lagging indicators vs falling real prices). 0 disables the penalty.
    public decimal NegativePriceActionPenalty { get; set; } = 0.05m;

    // ---- Volume confirmation weighting ----
    // Without volume confirmation the final score is capped at this value, keeping
    // "no volume" setups below a 0.90 firm entry bar. 0 disables the cap.
    public decimal MissingVolumeScoreCap { get; set; } = 0.85m;

    // ---- Liquidity hard filter ----
    // Minimum estimated 24h volume in EUR required to open a NEW entry. 0 disables.
    public decimal MinDailyVolumeEur { get; set; } = 0m;

    // ---- Exploratory entry mode (dry-run sampling) ----
    // When enabled, entries are also allowed at ExploratoryMinimumLongScore (below
    // MinimumLongScore) but ONLY with positive recent price action, a clean spread,
    // and a top-ExploratoryMaxRank slot in the cycle's candidate ranking. Meant for
    // dry-run sample collection; live mode force-disables it unless
    // ExploratoryAllowedInLive is explicitly set.
    public bool ExploratoryEntriesEnabled { get; set; } = false;
    public decimal ExploratoryMinimumLongScore { get; set; } = 0.85m;
    public int ExploratoryMaxRank { get; set; } = 2;
    public bool ExploratoryAllowedInLive { get; set; } = false;
    public decimal ExploratoryMinBullishEmaGapPercent { get; set; } = 0m;
    public decimal ExploratoryMinEmaGapVelocityPercent { get; set; } = 0m;
    public decimal ExploratoryMinPriceActionTrendPercent { get; set; } = 0m;

    // Stricter spread limit for exploratory entries: a small sampling position must
    // not pay a 0.4%+ spread even when it is below the hard MaxEntrySpreadPercent.
    // 0 disables the extra limit (the hard max still applies).
    public decimal MaxExploratorySpreadPercent { get; set; } = 0.30m;

    // ---- Trade-economics entry gate ----
    // A trade whose achievable win cannot comfortably pay its own costs must not be
    // opened at all (friction filters ENTRIES; it never widens the stop, which would
    // only grow the average loss). The calculated take-profit distance must be at
    // least this multiple of the round-trip friction (taker fees both ways + current
    // spread + slippage both ways). With TP=3xATR / SL=2xATR the terminal break-even
    // win rate is p* = (2*ATR + friction) / (5*ATR): at ATR 0.3% and friction 0.72%
    // p* is 88% (untradeable); requiring TP >= 3x friction caps p* near 60%.
    // 0 disables the gate.
    public decimal MinTakeProfitToFrictionRatio { get; set; } = 0m;

    // ---- Anti-extension entry guards ----
    // Reject a LONG entry whose price already ran away from its own fast EMA by
    // more than this percent: by the time the full confirmation stack agrees, an
    // extended price is a chase, not an entry. 0 disables.
    public decimal MaxEntryExtensionPercent { get; set; } = 0m;

    // Reject a LONG entry whose recent snapshot trend already gained more than
    // this percent over the price-action lookback window ("the move happened
    // without us"). 0 disables.
    public decimal MaxEntryRunupPercent { get; set; } = 0m;

    // The negative-price-action score penalty only applies once the snapshot trend
    // has fallen below MINUS this percent. A mild pullback (trend between
    // -threshold and 0) is a normal entry point, not weakness. 0 keeps the legacy
    // "any negative trend is penalized" behavior.
    public decimal NegativePriceActionPenaltyThresholdPercent { get; set; } = 0m;

    // ---- Market-regime entry filter ----
    // A long-only strategy must not open positions into a broadly falling market:
    // in the forensic window 55 of 60 pairs were down 1.5-5% and every long entry
    // lost. NEW entries are blocked when ALL enabled conditions read bad:
    //  - the reference pair's 24h change is at or below -RegimeFilterMaxBtcDeclinePercent;
    //  - the share of universe pairs with a positive 24h change is below
    //    RegimeFilterMinBreadthPercent.
    // A knob at 0 disables its condition; both at 0 disable the filter. Exits and
    // held-position management are never affected.
    public decimal RegimeFilterMaxBtcDeclinePercent { get; set; } = 0m;
    public decimal RegimeFilterMinBreadthPercent { get; set; } = 0m;
    public string RegimeFilterReferencePair { get; set; } = "XBT/EUR";

    // ---- Early-entry channel (forming EMA cross) ----
    // First-class entries taken BEFORE the EMA gap fully confirms: the gap must be
    // at least EarlyEntryMinEmaGapPercent, still WIDENING (gap velocity above
    // EarlyEntryMinGapVelocityPercent, strictly), RSI inside the ideal band, and
    // recent price action no worse than EarlyEntryMinPriceActionTrendPercent
    // (mildly negative = pullback is fine; a falling market is not). Ranked entries
    // are capped at EarlyEntryMaxRank per cycle. Unlike exploratory sampling this
    // channel is meant for live use; EarlyEntryAllowedInLive gates it there.
    public bool EarlyEntryEnabled { get; set; } = false;
    public bool EarlyEntryAllowedInLive { get; set; } = false;
    public decimal EarlyEntryMinScore { get; set; } = 0.60m;
    public decimal EarlyEntryMinEmaGapPercent { get; set; } = 0.10m;
    public decimal EarlyEntryMinGapVelocityPercent { get; set; } = 0m;
    public decimal EarlyEntryMinPriceActionTrendPercent { get; set; } = -0.2m;
    public int EarlyEntryMaxRank { get; set; } = 1;

    // ---- Price-action warm-up ----
    // When true, NEW entries are blocked until the pair has PriceActionMinSnapshots
    // observations, so the anti-lag guard never has to abstain for an actual entry.
    // Normalize() forces this to true in live mode: after a restart the worker must
    // warm up (hydration usually makes this instant) before it may open live
    // positions. AllowEntriesWithoutPriceActionInLive is the explicit, deliberate
    // override for operators who accept blind live entries; it is never implied.
    public bool RequirePriceActionData { get; set; } = false;
    public bool AllowEntriesWithoutPriceActionInLive { get; set; } = false;

    // A price-action series whose newest sample is older than this is STALE and
    // treated as insufficient (direction UNKNOWN, guard abstains, warm-up block
    // applies). Protects against trusting hydrated/frozen history. 0 disables.
    public int PriceActionMaxSampleAgeMinutes { get; set; } = 30;

    // On startup, snapshot history is hydrated from the persisted market snapshots
    // of the last N minutes so the guard is not blind for PriceActionMinSnapshots
    // cycles after every restart. Only recent rows are loaded on purpose: a long
    // downtime gap must not be bridged into a fake "recent" trend. 0 disables.
    public int PriceActionHydrationMinutes { get; set; } = 45;
}

public sealed class DryRunOptions
{
    public bool Enabled { get; set; } = true;
    public bool ApplyVirtualFills { get; set; } = true;
    public string OutputDirectory { get; set; } = "data/dry-run";
    public string StateFile { get; set; } = "portfolio-state.json";
    public string EventsFile { get; set; } = "events.jsonl";

    // Per-cycle light market snapshot (bid/ask/last/volume/change for every universe
    // pair). Written to this JSONL file when the database is disabled; the spread data
    // it captures cannot be reconstructed from candles later.
    public string MarketSnapshotsFile { get; set; } = "market-snapshots.jsonl";

    // Fee/slippage model for the dry-run simulation. These are meant to be an HONEST
    // estimate of real execution cost (26 bps = Kraken Pro starter taker tier, ~10 bps
    // slippage), not a padded worst case. Safety margin belongs in the risk limits
    // (order size, exposure, daily loss), NOT here: an over-penalized simulation can
    // reject a strategy that is actually profitable at real fees.
    public decimal TakerFeeBps { get; set; } = 26m;
    public decimal SlippageBps { get; set; } = 5m;
}

public sealed class DatabaseOptions
{
    public bool Enabled { get; set; } = false;
    public string ConnectionString { get; set; } = string.Empty;
}

public sealed class MarketDataConsumerOptions
{
    public string Venue { get; set; } = MarketDataVenue.Spot;
    public bool FallbackEnabled { get; set; } = true;
    public int MaxQuoteAgeSeconds { get; set; } = 120;
    public int MaxCandleAgeMinutes { get; set; } = 15;
    public int MaxCandleBars { get; set; } = 120;
}

public sealed class MarketDataIngestionOptions
{
    public int LightIntervalSeconds { get; set; } = 30;
    public int CandleIntervalSeconds { get; set; } = 120;
    public int TimeframeMinutes { get; set; } = 15;
    public int MaxCandlePairs { get; set; } = 40;
    public int TopVolumePairs { get; set; } = 30;
    public int TopMoverPairs { get; set; } = 40;
    public decimal StrongMoverPercent { get; set; } = 3m;
    public List<string> ForceInclude { get; set; } = new();
    public List<string> Blacklist { get; set; } = new();
}

public sealed class CorrelationRiskOptions
{
    // Per-group open-position and exposure caps. Every value uses the 0 = disabled
    // convention so a default-constructed (unconfigured) options object is inert.
    public int MaxOpenPositionsPerGroup { get; set; } = 0;
    public decimal MaxExposureEurPerGroup { get; set; } = 0m;

    // Aggregate caps across all high-beta groups (and ungrouped high-beta singletons).
    public int MaxHighBetaPositions { get; set; } = 0;
    public decimal MaxHighBetaExposureEur { get; set; } = 0m;

    // Group names considered high-beta. A pair absent from every group is an implicit
    // singleton group "UNGROUPED:<pair>" that is ALWAYS treated as high-beta.
    public List<string> HighBetaGroups { get; set; } = new();

    // Correlation group name -> member pairs (e.g. "L1_L2" -> ["SOL/EUR", ...]).
    public Dictionary<string, List<string>> Groups { get; set; } = new();
}

public sealed class InstrumentOptions
{
    public string Pair { get; set; } = string.Empty;
    public string KrakenPair { get; set; } = string.Empty;
    public string Venue { get; set; } = "Kraken";
    public bool Enabled { get; set; } = true;
    public int? QuantityDecimals { get; set; }
}
