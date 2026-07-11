# Centralized Market Data Worker Blueprint

## Problem

Today each decision instance (spot-live, spot-virtual, futures-live, futures-virtual — more later) independently:

1. Discovers Kraken universe (`AssetPairs` / `instruments`)
2. Fetches light tickers for the full universe
3. Selects an active subset (~20 pairs per instance)
4. Fetches OHLC + order book per active pair (spot: **500ms delay** between OHLC calls — `MarketData.cs:23`)

API load scales **O(instances)**. All instances share one server IP → Kraken public ~1 req/s budget is exhausted; cycles slip, live/virtual snapshots diverge (observed ~23s skew), and price-action history is duplicated per `bot_instance_id`.

## Goal

One `TradingBot.MarketDataWorker` polls Kraken once, writes shared market state to Postgres, and decision workers read from DB instead of hitting Kraken directly.

Decision logic stays in spot/futures workers. The data worker **collects only** — it does not score, gate, or trade.

## Design Principles

| Principle | Rule |
|-----------|------|
| Cheap data → full universe | Light tickers for **all** discovered pairs (spot `Ticker` without pair filter is one call) |
| Expensive data → curated set | OHLC + order book only for ~40 pairs per venue per cycle |
| Held pairs never dropped | Union must include open positions from **all** instances (`portfolio_state`) |
| Instances keep strategy | Data worker selects fetch set; instances still choose `MaxActiveInstruments`, held forcing, regime anchors |
| Fail-safe consumption | `MarketDataMode=database` with staleness gates; `kraken` / `kraken-futures` fallback if data worker is down |
| Venue tagging | Every row tagged `venue` = `spot` \| `futures` |

## Architecture

```text
                    ┌─────────────────────────────┐
                    │  TradingBot.MarketDataWorker │
                    │  (single container)          │
                    └──────────────┬──────────────┘
                                   │ write
                                   ▼
                    ┌─────────────────────────────┐
                    │  PostgreSQL                  │
                    │  instrument_registry           │
                    │  market_quotes               │
                    │  market_candles              │
                    │  market_orderbooks (optional)│
                    └──────────────┬──────────────┘
           read                    │                    read
    ┌──────────────┐    ┌─────────┴─────────┐    ┌──────────────┐
    │ spot-live    │    │ spot-virtual      │    │ futures-*    │
    │ DecisionWorker│   │ DecisionWorker    │    │ FuturesDec.. │
    └──────────────┘    └───────────────────┘    └──────────────┘
```

Reuse existing seam: `IMarketDataSource` + `IUniverseProvider`. Add `DatabaseMarketDataSource` and `DatabaseUniverseProvider` in Core; wire via `MarketDataMode=database`.

## Data Worker Loop (per venue)

Cadence: **30s** light poll, **full candle refresh on loop interval** (configurable, default 120s). Spot and futures run in one process, two inner loops or interleaved with shared rate-limit budget.

### Step 1 — Universe (cached)

- Spot: `GET /0/public/AssetPairs` → EUR pairs, online (existing `KrakenSpotUniverseProvider` logic)
- Futures: `GET /derivatives/api/v3/instruments` → `PF_*USD` tradeable (existing `KrakenFuturesUniverseProvider` logic)
- Upsert `instrument_registry` (pair, kraken_symbol, venue, enabled, precision rules)

### Step 2 — Light quotes (full universe)

- Spot: `GET /0/public/Ticker` (all pairs, one request) or batched if needed
- Futures: `GET /derivatives/api/v3/tickers` batched by symbol list
- Upsert `market_quotes` (venue, pair, bid, ask, last, volume24h, change_pct, funding/mark fields, `utc`)

### Step 3 — Candle fetch set (~40 per venue)

Union of:

1. **Top-N by quote volume** (default N=30) from step 2
2. **All held pairs** — `SELECT DISTINCT pair FROM portfolio_state JOIN positions` across all `bot_instance_id` values for that venue (critical: instance with open position must not go blind on exit)
3. **Strong movers** — `abs(change_pct) >= StrongMoverThreshold` (default 3%, align with spot `StrongMoverBackfill` threshold)
4. **Force-include** — config CSV (`TRADINGBOT_MARKET_DATA_FORCE_INCLUDE`)
5. Minus **blacklist** config CSV

Cap at `MaxCandlePairs` (default 40). Log selection diagnostics each cycle.

### Step 4 — Full market data (selected pairs only)

For each selected pair (sequential, 500ms OHLC delay on spot):

- Spot: `OHLC` + `Depth` (existing `KrakenMarketDataSource.GetFullMarketStatesAsync` logic)
- Futures: charts + orderbook (existing `KrakenFuturesMarketDataSource` logic)

Write:

- `market_candles` — append or upsert latest window (venue, pair, timeframe_minutes, open_time, o,h,l,c,v)
- `market_orderbooks` — latest snapshot per pair (or embed top-of-book in quotes if we want to defer)

### Step 5 — Cycle metadata

Write `market_data_cycles` row: cycle_id, venue, utc, universe_count, quote_count, candle_pair_count, duration_ms, warnings.

## Database Schema (new tables)

Scoped by `venue`, **not** `bot_instance_id`. Decision workers read shared rows.

```sql
create table if not exists instrument_registry (
    venue text not null,           -- 'spot' | 'futures'
    pair text not null,
    kraken_symbol text not null,
    enabled boolean not null default true,
    updated_at timestamptz not null,
    primary key (venue, pair)
);

create table if not exists market_quotes (
    venue text not null,
    pair text not null,
    utc timestamptz not null,
    bid numeric not null,
    ask numeric not null,
    last numeric not null,
    volume24h numeric not null,
    change_percent numeric not null,
    -- futures extras (nullable): mark, index, funding_rate
    primary key (venue, pair)      -- latest quote per pair; upsert on write
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
```

Keep existing `market_snapshots` (per `bot_instance_id`) during migration for price-action hydration; later instances can hydrate from `market_quotes` instead.

## Consumer Side (`DatabaseMarketDataSource`)

New implementation in `TradingBot.Core/MarketData/`:

```csharp
public sealed class DatabaseMarketDataSource(IMarketDataStore store, MarketDataConsumerOptions options) : IMarketDataSource
{
    // GetLightMarketStatesAsync: read all quotes for venue where utc >= now - MaxQuoteAge
    // GetFullMarketStatesAsync: read candles + orderbook for requested instruments;
    //   reject/stale-mark pairs where newest candle age > MaxCandleAgeMinutes
}
```

### Staleness gates (mandatory)

| Data | Default max age | On stale |
|------|-----------------|----------|
| Quotes (light) | 2 min | `DataWarning` on state; price-action assess → `Stale=true` |
| Candles (full) | 1 × timeframe (15m) | Block **entries** (`ENTRY_CANDLE_STALE` / futures quality gate); exits allowed on last-known |
| Universe registry | 1 h | Fall back to configured `CandidateUniverse` |

Reuse existing patterns:

- `PriceActionMaxSampleAgeMinutes` (30) — already abstains on stale snapshots
- `EvaluateBtcRegime` candle staleness check in both workers
- API `isStale` if last cycle > 10 min

### `MarketDataMode` values

| Mode | Behavior |
|------|----------|
| `database` | Read shared store; optional `kraken` fallback on stale if `MarketDataFallbackEnabled=true` |
| `kraken` | Current spot direct fetch (unchanged) |
| `kraken-futures` | Current futures direct fetch (unchanged) |
| `sample` | Tests / offline |

Env: `TRADINGBOT_MARKET_DATA_MODE=database`, `TRADINGBOT_MARKET_DATA_FALLBACK_ENABLED=true`.

## What Stays in Decision Workers

- `BuildActiveInstruments` / futures active selection (held first, volume rank, BTC anchor, strong-mover backfill)
- Scoring, risk, execution, portfolio
- Per-instance `market_snapshots` append (optional during migration; can stop once shared quotes are trusted)

Data worker does **not** replicate instance-specific watchlist or exploratory ranking.

## Migration Plan (safe, incremental)

### Phase 0 — Blueprint + schema (this doc)

No behavior change.

### Phase 1 — Schema + data worker writes, instances unchanged

- Add tables to `EnsureSchema`
- New `TradingBot.MarketDataWorker` project
- Deploy as 5th compose service; `MarketDataMode` still `kraken` on instances
- Verify: quotes and candles land in DB; compare against direct fetch in logs

### Phase 2 — `DatabaseMarketDataSource` + one instance on database mode

- Switch `spot-virtual` first (lowest blast radius)
- `MarketDataFallbackEnabled=true` — direct Kraken if DB stale
- Compare cycle decisions live vs virtual for 24–48h

### Phase 3 — Roll remaining instances

- spot-live, futures-virtual, futures-live one at a time
- Remove duplicate universe discovery from instances on database mode (use `DatabaseUniverseProvider`)

### Phase 4 — Cleanup

- Drop per-instance light polling (`MarketSnapshotIntervalSeconds`) on database mode — data worker owns 30s cadence
- Retire duplicate Kraken calls; tune `MaxCandlePairs` from observed rate limits
- Optional: stop writing per-instance `market_snapshots` for light data

## New Project Layout

```text
src/TradingBot.MarketDataWorker/
  Program.cs
  MarketDataWorkerConfiguration.cs
  MarketDataIngestionWorker.cs      # main loop
  CandlePairSelector.cs             # top-N + held + movers union
  HeldPairsReader.cs                # cross-instance portfolio_state query
  Dockerfile

src/TradingBot.Core/MarketData/
  DatabaseMarketDataSource.cs       # consumer
  DatabaseUniverseProvider.cs
  IMarketDataStore.cs
  PostgresMarketDataStore.cs

tests/TradingBot.MarketDataWorker.Tests/
tests/TradingBot.Core.Tests/DatabaseMarketDataSourceTests.cs
```

## Deploy Changes

`infra/docker-compose.prod.yml`:

```yaml
market-data-worker:
  container_name: trading-bot-market-data-worker
  image: ${MARKET_DATA_WORKER_IMAGE_NAME}:${MARKET_DATA_WORKER_IMAGE_TAG:-latest}
  restart: unless-stopped
  env_file:
    - /opt/trading-bot/market-data/.env
  depends_on:
    database:
      condition: service_healthy
```

`infra/deploy.sh`: seed `/opt/trading-bot/market-data/.env` with DB connection, no trading keys needed (public API only).

CI (`.github/workflows/static-site.yml`): build/push `ghcr.io/.../trading-bot-market-data-worker`.

## Rate Budget (single worker, both venues)

Approximate per 120s cycle:

| Call | Count | Notes |
|------|-------|-------|
| Spot AssetPairs | 1 | cached 1h |
| Spot Ticker (all) | 1 | full universe |
| Spot OHLC + Depth | 40 × 2 = 80 | 500ms spacing → ~40s |
| Futures instruments | 1 | cached 1h |
| Futures tickers | ~3 batches | 80 symbols each |
| Futures charts + book | 40 × 2 = 80 | sequential |

Total ~165 calls / 120s ≈ 1.4 req/s peak — fits one IP with spacing. Was **4×** with four instances.

Inter-cycle light poll (30s): spot all-ticker + futures tickers only (~4 calls / 30s).

## Config Knobs (env)

| Variable | Default | Purpose |
|----------|---------|---------|
| `TRADINGBOT_MARKET_DATA_MODE` | `database` (instances) / `kraken` (ingest worker always direct) | Consumer mode |
| `TRADINGBOT_MARKET_DATA_FALLBACK_ENABLED` | `true` | Direct Kraken if DB stale |
| `TRADINGBOT_MARKET_DATA_LIGHT_INTERVAL_SECONDS` | `30` | Ticker poll cadence |
| `TRADINGBOT_MARKET_DATA_CANDLE_INTERVAL_SECONDS` | `120` | OHLC refresh cadence |
| `TRADINGBOT_MARKET_DATA_MAX_CANDLE_PAIRS` | `40` | Cap per venue |
| `TRADINGBOT_MARKET_DATA_TOP_VOLUME_PAIRS` | `30` | Top-N leg of union |
| `TRADINGBOT_MARKET_DATA_STRONG_MOVER_PERCENT` | `3.0` | Mover threshold |
| `TRADINGBOT_MARKET_DATA_MAX_QUOTE_AGE_SECONDS` | `120` | Consumer staleness |
| `TRADINGBOT_MARKET_DATA_MAX_CANDLE_AGE_MINUTES` | `15` | Consumer staleness (= 1 bar) |

## Open Questions

1. **Order book storage** — full depth vs top-5 in `market_quotes`? Spot entry spread gate needs bid/ask (quotes suffice); depth used for exit impact — keep orderbook fetch for candle set only.
2. **Futures mark-price candles** — store as-is from charts API; document that indicators run on mark, not last.
3. **Historical backfill** — Phase 1 writes rolling window only (~120 bars); CSV bootstrap remains separate task per `solution-architecture.md`.
4. **Held-pairs query** — spot pairs `XBT/EUR` vs futures `PF_XBTUSD`: map via `instrument_registry.kraken_symbol` / `pair` per venue.

## Success Criteria

- [ ] Four instances on `database` mode; Kraken public call rate flat as instance count grows
- [ ] live/virtual decision inputs match within one data-worker cycle (no 23s ticker skew)
- [ ] Open position on any instance guarantees candle+quote freshness for that pair
- [ ] Stale DB → entries blocked, exits proceed, fallback or alert fires
- [ ] Data worker restart: instances survive on last-known + fallback until quotes refresh (< 2 min)

## References (current code)

- `IMarketDataSource` — `src/TradingBot.Core/MarketData/MarketData.cs`
- Spot OHLC rate limit — `MarketData.cs:23` (500ms)
- Universe providers — `KrakenSpotUniverseProvider.cs`, `KrakenFuturesUniverseProvider.cs`
- Active pair selection — `DecisionWorker.BuildActiveInstruments`, `FuturesDecisionWorker.RunCycleAsync`
- Snapshot persistence — `PostgresDryRunPortfolioStore`, table `market_snapshots`
- Price-action staleness — `SnapshotPriceHistory.Assess`, `PriceActionMaxSampleAgeMinutes`
- Deploy — `infra/docker-compose.prod.yml` (4 workers, no market-data service yet)
