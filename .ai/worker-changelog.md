# Worker changelog

Latest entry must be first. The first `## <id>` heading is used as `worker.changeSet`.

## 2026-07-07-futures-atr-funding-liquidity-risk

- Futures dry-run new entries are now fail-closed on missing ATR, funding, quote volume, executable exit depth, maker queue/fill data, BTC regime, or projected open-risk capacity. Existing reduce-only exits, TP/SL, max-hold, and the dry-run-only execution boundary are preserved.
- Replaced fixed futures entry TP/SL modeling with ATR-derived distances: stop distance uses at least `Exits.MinStopAtrFloor` x ATR, take-profit is lifted to at least `Exits.MinTpVsCostMult` x estimated round-trip cost, and entry diagnostics record ATR percent, stop/take-profit distance, round-trip cost, and expected funding.
- Funding gating now uses Kraken Futures ticker `fundingRate` with documented directional semantics: positive funding is adverse to longs, negative funding is adverse to shorts. Missing funding blocks new entries.
- Added futures liquidity gates: minimum 24h quote volume, reduce-only taker exit depth within configured impact, and maker queue-ahead based dry-run fill/miss/partial-fill diagnostics. Virtual positions, fees, risk, and TP/SL levels are based only on filled notional.
- Added BTC regime gates for futures: new longs are blocked below/crashing BTC regime; shorts require a falling BTC MA, no crash-chase extension, bearish pair signal, elevated score threshold, and non-adverse funding.
- Added futures guard defaults for cooldown after close, stop-loss cooldown, minimum hold, entry blackout, max concurrent open risk, fees, and max hold. Futures execution remains dry-run only; no live order path was added.

## 2026-07-07-live-entry-parity-with-virtual

- Restored live/virtual parity for configured spot entry channels: live mode no longer force-disables `Strategy.ExploratoryEntriesEnabled` or `Strategy.EarlyEntryEnabled` during config normalization.
- Set committed spot defaults back to `ExploratoryAllowedInLive=true` and `EarlyEntryAllowedInLive=true`, so live uses the same signal-channel configuration as virtual.
- Safety remains enforced by hard fail-closed entry gates before any real order: valid ATR, bid/ask, volume, depth, emergency exit depth, BTC regime, open-risk cap, cooldowns/blackout/correlation, kill switch, and post-only maker execution still apply.

## 2026-07-07-spot-maker-atr-liquidity-risk

- Spot live entries now submit only Kraken post-only limit buys at or below best bid. Unfilled maker orders are cancelled after `Entry.MakerFillTimeoutSec` (default 60s), at most `Entry.MakerRepegs` (default 1) re-price is allowed per cycle, and partial fills commit only the exchange-reported filled volume/cost/fee. Missing fill confirmation is fail-closed and no modeled live buy position is created.
- New spot positions require fresh ATR(14), valid bid/ask, 24h quote volume, depth, emergency exit depth, open-risk capacity, and BTC regime data. Missing/stale/invalid inputs block only new entries; held-position exits, saved stops, trailing, max-hold, cooldowns, blackout, and kill switch continue to run.
- ATR is now the entry-time source for new stop/take-profit distances: stop distance uses at least `PositionExit.MinStopAtrFloor` (1.5x ATR), take-profit distance is lifted to at least `PositionExit.MinTpVsCostMult` (3x) estimated round-trip cost. Existing operational/protective exit rules remain in place.
- Added absolute liquidity/risk gates: `Filters.MinQuoteVolume24h=50000` EUR (tune later), depth multiple 5x order size, emergency bid-side depth within `Filters.MaxExitImpactPct`, and `Risk.MaxConcurrentOpenRisk=1.5` EUR including emergency exit cost; positions without valid stops fail unsafe and block new entries.
- Replaced the old breadth/24h BTC regime with BTC/EUR 15m regime: block new longs on fast crash (`Regime.BtcCrashLookback=4`, `Regime.BtcCrashPct=2.0`) or close below falling MA (`Regime.BtcTrendMa=50`). Missing/stale BTC candles fail closed for new entries.
- Config now carries real Kraken fee defaults (`Fees.MakerPct=0.25`, `Fees.TakerPct=0.40`) and diagnostics include maker execution mode, maker fill rate, spread/volume/depth pass counts, open risk, BTC regime state, queue-ahead/fill timing/repeg counts, and per-trade round-trip cost estimates.
- Live early/exploratory weak-score entries are force-disabled regardless of copied config; scores below the firm `Strategy.MinimumLongScore` do not open live spot positions.

## 2026-07-07-spot-futures-cycle-cadence

- Tuned default cycle cadence by venue: spot workers now run every 240 seconds (4 minutes) instead of 180 seconds, while futures workers run every 120 seconds (2 minutes) instead of 180 seconds.
- Spot gets lower polling pressure and fewer repeated evaluations between 15m candle closes; futures gets faster virtual TP/SL, mark-price, and reversal checks while evaluating the expanded futures universe.
- No scoring thresholds, universe membership, risk limits, order sizing, live execution, leverage, or TP/SL levels changed.

## 2026-07-07-futures-spot-sized-universe

- Expanded the futures dry-run candidate universe from the initial 5 majors to the Kraken Futures tradeable USD perpetual subset matching the spot worker asset universe: 48 `PF_*USD` contracts are now eligible.
- Raised futures `Trading.MaxActiveInstruments` from 5 to 20 so each cycle evaluates the top 20 enabled futures candidates by held-position priority and 24h quote activity.
- Kept non-existent/non-tradeable futures contracts out of config instead of adding dead symbols for spot-only assets; live futures execution remains unavailable and all existing leverage, slot, funding, margin, TP/SL, and no-flip rails are unchanged.
- Expected effect: futures virtual diagnostics cover the same broad opportunity surface as spot where Kraken Futures has matching perpetuals, while still avoiding noisy unusable market-data rows.

## 2026-07-07-live-fills-entry-economics-regime-filter

- Live fill reconciliation: after `LIVE_SUBMITTED` the spot worker reads the real fill back via Kraken `QueryOrders` (average price, executed volume, quote cost, fee) and commits THOSE numbers to the portfolio instead of the ask+slippage model; a failed query falls back to the modeled fill. Every BUY/SELL record now carries `fillSource` (`REAL`/`MODELED`) plus `modeledFillPrice`/`modeledFeeEur`, exposed in `dry_run_decisions` as `fill_source`/`modeled_fill_price`/`modeled_fee_eur` for model-vs-exchange drift analysis. Live startup warns when Kraken EUR balance drifts >1 EUR from virtual cash.
- Trade-economics entry gate: friction filters ENTRIES instead of widening stops. A BUY is blocked (`FRICTION_BLOCK`/`REJECT_FRICTION_TOO_HIGH`) when the ATR take-profit distance is below `Strategy.MinTakeProfitToFrictionRatio` (3.0) x round-trip friction (taker fees both ways + live spread + slippage both ways). Stops stay pure 2xATR. Rationale: the 9h forensic window traded ATR~0.3% setups whose terminal break-even win rate was ~88%.
- Trailing stop enabled (activation 0.8%, distance 0.5%) so favorable excursions get banked; observed MFE profile (+0.24/+0.64/+1.25%) never reached the 3xATR TP. Entry spread gates tightened to 0.15% (0.12% exploratory).
- Early-entry channel: a forming EMA cross (gap >= 0.10% but below full confirmation) that is still widening (gap velocity > 0), RSI in the ideal band, price action tolerating a -0.2% pullback, may enter at the BASE order size (10 EUR), top-1 ranked candidate per cycle, gated in live by explicit `EarlyEntryAllowedInLive`. Replaces the early-structure exploratory path that required a +0.5% rising series and forced chasing.
- Anti-extension guards: entries more than 0.6% above their own fast EMA or after a >2.5% snapshot run-up are rejected (`REJECT_PRICE_EXTENDED`). The negative-price-action score penalty now only applies below a -0.2% trend.
- Market-regime filter: NEW long entries are blocked when XBT/EUR 24h change <= -1% AND positive-change breadth < 40% (`MARKET_REGIME`/`REJECT_MARKET_REGIME`); exits unaffected. In the forensic window 55/60 pairs were falling and all 8 closed longs lost.
- Defensive exits calmed: score-decay defensive window 2 -> 5 cycles (~15 min), immediate-exit score 0.40 -> 0.30, confirmed-bearish-flip loss floor -1.2% -> -2.0%. Exploratory sampling is no longer allowed in live (`ExploratoryAllowedInLive=false`); it stays a virtual-instance tool.
- Expected effect: live records become reconcilable against the exchange, sub-economic and chased entries disappear (visible via the new rejection codes), winners get banked by trailing instead of round-tripping to stops, and long entries pause during market-wide drawdowns.

## 2026-07-06-futures-entry-threshold-tuning

- Tuned futures dry-run entry thresholds from live diagnostics: `Strategy.MinimumLongScore` is now 0.85 and `Strategy.MinimumEmaGapPercent` is now 0.30, so fully confirmed 0.85-score setups and near-threshold EMA gaps can enter instead of staying flat.
- Kept futures safety rails unchanged: maximum 3 open positions, default 1x leverage, 2x leverage cap, no flips, shorts disabled by default, fixed 2.0% stop-loss and 3.0% take-profit.
- Expected effect: futures virtual should start taking the cleaner XBT/ETH/SOL-style long candidates seen in recent database cycles while preserving the same margin and TP/SL controls.

## 2026-07-06-futures-data-diagnostics-shorts

- Added real read-only Kraken Futures public market data wiring: instruments, tickers, and mark-price candles are parsed from `futures.kraken.com`; live execution remains impossible and the broker adapter still throws.
- Updated the futures scaffold universe from non-existent `PF_*EUR` contracts to live Kraken Futures USD perpetual symbols (`PF_XBTUSD`, `PF_ETHUSD`, `PF_SOLUSD`, `PF_XRPUSD`, `PF_ADAUSD`) so `MarketDataMode=kraken-futures` produces usable dry-run data.
- Futures dry-run now persists entry diagnostics instead of null, including top candidates, risk/no-signal rejection counts, excluded pairs, and spread context.
- Futures risk now fails closed when a funding-rate cap is configured but funding is unavailable; funding from public futures tickers is passed into the margin gate.
- Added nullable futures observability fields to portfolio/action API surfaces (`leverage`, initial margin, mark/liquidation price, liquidation distance, TP/SL order state, reduce-only, exit trigger source) without changing spot rows.
- Added a conservative Core `ShortCandidate` intent path for futures only: bearish EMA plus downside confirmation can map to short exposure when `Futures.AllowShorts=true`; spot continues to ignore short intent.
- Raised the futures dry-run open-position cap to 3 and allowed a cycle to open up to the remaining free futures slots; leverage and no-flip limits stay unchanged.
- Aligned futures fixed TP/SL with the spot ATR risk/reward profile: stop-loss is now 2.0% and take-profit remains 3.0%, triggered on mark price and simulated reduce-only.
- Futures no-entry diagnostics now explain the actual blocker (score below threshold, EMA gap below the configured minimum, short gate not confirmed, or slots exhausted) instead of the generic "no exposure requested".
- Futures deploy defaults now use `TRADINGBOT_MARKET_DATA_MODE=kraken-futures` for newly created live envs and every refreshed virtual env; existing live env/appsettings remain operator-owned and are not overwritten.
- CI changelog enforcement now covers `src/TradingBot.FuturesWorker` in addition to Core and SpotWorker.

## 2026-07-06-futures-worker-scaffold

- Added `TradingBot.FuturesWorker` (blueprint phase 4): dry-run-only long/short worker with a virtual margin ledger (`FuturesVirtualPortfolio`), margin/leverage pre-trade gates (`MarginRiskManager`), simulated reduce-only TP/SL orders (`TpSlOrchestrator`), and a slim cycle loop that persists cycle records and market snapshots to the shared database under the `futures-live`/`futures-virtual` instance ids.
- Safety per blueprint: no live order path exists in the binary (no live env override either); leverage clamped to 2x, one position max, flips refused, reduce-only orders can never open exposure, shorts gated by `Futures.AllowShorts`.
- `KrakenFuturesBroker` and `KrakenFuturesMarketDataSource` are documented stubs (futures.kraken.com auth scheme noted); the worker runs on Core's sample market data until the real adapter lands with its safety tests. Short-candidate scoring is a TODO - Core's scorer emits long intents only.
- Journal DTOs extended additively (nullable `Leverage`, `InitialMarginEur`, `MarkPrice`, `LiquidationPrice`, `LiquidationDistancePercent`, `FundingPaidEur`, `TpOrderState`, `SlOrderState` on positions; `Side`, `ReduceOnly`, `Leverage`, `ExitTriggerSource` on actions) - spot rows are unchanged.
- New `TradingBot.FuturesWorker.Tests` covers the blueprint safety cases: shorts only when allowed, reduce-only SELL never opens short, reduce-only BUY never opens long, no flip, liquidation-distance math, margin cap blocks entries, TP/SL triggers are reduce-only and cancel the sibling order.
- Deploy: new `futures-worker-live`/`futures-worker-virtual` compose services with per-instance dirs under `/opt/trading-bot/futures/`, new `ghcr.io/bykovas/trading-bot-futures-worker` image in CI with its own strategy-version hash over Core+FuturesWorker.

## 2026-07-06-decision-model-split

- Extracted pure technical scoring from `TechnicalDecisionEngine` into `TradingBot.Core.Scoring.SignalScorer` and introduced the venue-neutral `SignalIntent` enum (`None`/`LongCandidate`/`ShortCandidate`). Core never emits BUY/SELL; the spot worker translates `LongCandidate` into its persisted `LONG_MICRO` desired position, so journal/DB contracts are byte-identical.
- Introduced `ISpotBroker` and switched `DecisionWorker` from the concrete `KrakenBroker` to the interface; behavior unchanged.
- The CSV replay harness and the full test suite pass unchanged, proving no scoring or decisioning drift.

## 2026-07-06-extract-core-library

- Extracted the venue-neutral half of the spot worker into a new `TradingBot.Core` class library: indicators (EMA/RSI/ATR), Kraken/sample market data sources, price action, entry gate, AI/heuristic watchlist advisors, entry ranking, correlation risk, cycle diagnostics, file logging, clock, shared option classes, journal DTOs (`PortfolioState`, `DryRunCycleRecord`, ...), and the file/Postgres portfolio stores that own the DB schema.
- The spot worker keeps everything venue-specific: decisioning, cycle orchestration, dry-run portfolio engine, Kraken spot broker, exit policy and exit levels, spot option classes, and the composition root. Persisted JSON contracts and env variable names are unchanged.
- Core types are public; workers keep internal types. New `TradingBot.Core.Tests` project holds the pure Core tests; the CSV replay harness stays in `TradingBot.SpotWorker.Tests` and passes unchanged, proving no behavior drift.
- CI strategy-version hash and the changelog gate now cover `src/TradingBot.Core` in addition to `src/TradingBot.SpotWorker`.

## 2026-07-06-rename-worker-to-spotworker

- Renamed the worker project `TradingBot.Worker` -> `TradingBot.SpotWorker` (namespace, csproj, sln, Dockerfile, CI paths, changelog gate, strategy-version hash input) ahead of the Core/SpotWorker/FuturesWorker split. Pure rename: no behavior change, full test suite unchanged and green.
- Renamed the worker image `ghcr.io/bykovas/trading-bot-worker` -> `ghcr.io/bykovas/trading-bot-spot-worker` and the deploy env contract `WORKER_IMAGE_NAME`/`WORKER_IMAGE_TAG` -> `SPOT_WORKER_IMAGE_NAME`/`SPOT_WORKER_IMAGE_TAG`.
- `strategy_version` values in `dry_run_cycles` reset at this commit because the hash input path changed from `src/TradingBot.Worker` to `src/TradingBot.SpotWorker`.

## 2026-07-06-market-prefixed-instance-ids

- Renamed bot instance ids to the market-prefixed scheme: `live` -> `spot-live`, `virtual` -> `spot-virtual` (the upcoming futures worker will use `futures-live` / `futures-virtual`). Ids come from `TRADINGBOT_BOT_INSTANCE_ID`; `deploy.sh` templates updated and the operator-owned live `.env` is upgraded in place once.
- Clean-slate policy for the rename: `EnsureSchema` now deletes rows persisted under the retired `live`, `virtual`, and `default` instance ids from `portfolio_state`, `dry_run_cycles`, and `market_snapshots` instead of migrating them. Virtual portfolio state restarts from `StartingCashEur`; live positions must be reconciled manually if any are open at rollout.
- UI pages (dashboard, trades, cycles, market snapshots) now target `spot-live` / `spot-virtual` and transparently map legacy `live` / `virtual` values from localStorage and shared URLs.
- Expected effect: instance ids unambiguously identify market and mode ahead of the futures worker rollout; no behavior change in decisioning.

## 2026-07-06-atr-position-exits

- Added manual ATR calculation using True Range and Wilder smoothing over closed candles only; the newest potentially open candle is excluded from ATR input.
- Added ATR-based entry-time exit levels. In `PositionExit.Mode=Atr`, new long positions save `ExitMode`, `EntryAtr`, `StopLossPrice`, and `TakeProfitPrice` when opened; those saved levels are used for hard stop-loss / take-profit instead of recalculating ATR later.
- Switched worker appsettings to ATR exits: `AtrPeriod=14`, `StopLossAtrMultiplier=2.0`, `TakeProfitAtrMultiplier=3.0`, with fixed-percent fallback levels `2.0%` stop-loss and `3.0%` take-profit.
- Kept legacy fixed-percent behavior through `FixedPercent` mode and legacy `StopLossPercent` / `TakeProfitPercent` aliases. If ATR history is insufficient at entry, the worker opens using fixed-percent saved levels and logs the fallback in the buy reason.
- ATR mode now fails startup when `TakeProfitAtrMultiplier < StopLossAtrMultiplier` to prevent accidental risk/reward below 1.
- Expected effect: exits adapt to each instrument's volatility while preserving deterministic entry-time SL/TP levels and fixed-percent fallback for cold starts / legacy positions.

## 2026-07-06-three-minute-loop

- Reduced the default worker loop interval from 300 seconds to 180 seconds so live and virtual workers evaluate entries/exits roughly every 3 minutes instead of every 5 minutes.
- Expected effect: faster reaction to early scalp entries and 2% take-profit exits, at the cost of more frequent Kraken/API/database polling.

## 2026-07-06-live-early-scalp-tuning

- Tuned exploratory/early-entry defaults from diagnostics: `ExploratoryMinimumLongScore=0.60`, `ExploratoryMinBullishEmaGapPercent=0.10`, `ExploratoryMinEmaGapVelocityPercent=0.00`, `ExploratoryMinPriceActionTrendPercent=0.50`, `MaxExploratorySpreadPercent=0.30`, and `PositionExit.TakeProfitPercent=2.0`.
- Added real gate enforcement for the new exploratory minimum EMA gap, EMA-gap velocity, and price-action trend settings. Early bullish EMA structure must now satisfy the configured score, gap, non-shrinking gap velocity, rising trend, spread, and momentum/volume confirmation before it can become an exploratory entry.
- Set `ExploratoryAllowedInLive=true` in worker appsettings so live mode no longer automatically suppresses the explicitly configured early/exploratory entry path. Live hard filters, pair metadata checks, bid/ask validation, price-action warm-up, ranking, portfolio risk, correlation, cooldown, blackout, and broker gates still apply.
- Added environment overrides for the new exploratory thresholds.
- Expected effect: allow the tested early scalp profile to participate in both virtual and live decisioning while keeping it constrained to clean rising price action and a 2% take-profit exit.

## 2026-07-06-live-virtual-instance-split

- Added `BotInstance.Id` / `BotInstance.Name` metadata. Cycle ids are now instance-prefixed, cycle JSON includes bot instance identity, and Postgres persistence separates portfolio state, cycles, decisions, diagnostics, and market snapshots by `bot_instance_id`.
- Prepared shared-DB multi-instance operation: one worker can run as `live`, another as `virtual`, without overwriting each other's portfolio state or mixing UI/API results.
- Added API/UI filtering by bot instance on portfolio dashboard, trades journal, cycles table, and market snapshots table. The UI selector exposes only the implemented `live` and `virtual` bot instances.
- Added deterministic strong-mover backfill to the active watchlist: clean high-change pairs can be evaluated even when the AI/volume top-N advisor did not select them. Defaults in `appsettings.json`: enabled, `StrongMoverMinChangePercent=4.0`, `StrongMoverMaxSpreadPercent=0.35`, `StrongMoverMinDailyVolumeEur=100000`, `StrongMoverMaxBackfillPairs=5`.
- The backfill only broadens indicator evaluation; normal spread, score, ranking, portfolio, correlation, cooldown, blackout, exposure, and broker gates still apply before any order.
- Exploratory entries may now admit early bullish EMA structure (`hasBullishStructure=true`, `emaFullyConfirmed=false`) when score, positive price action, clean spread, and momentum/volume confirmation are present. In live mode this still requires explicit `ExploratoryAllowedInLive=true`; otherwise `Normalize()` disables exploratory entries.
- Added worker tests for strong-mover backfill and early-structure exploratory admission.
- Expected effect: reduce missed opportunities from watchlist exclusion and the binary EMA gate while allowing live and virtual workers to run side by side against the same database.

## 2026-07-05-conditional-max-hold

- Changed `PositionExit.MaxHoldMinutes` from an unconditional forced sell timer into a conditional stale-position exit.
- Positions older than `MaxHoldMinutes` now sell only when they are losing, no longer desired by the strategy, no longer score-confirmed, or no longer have bullish EMA structure.
- Old positions that are non-negative or temporarily unvalued and still have a confirming thesis now hold with `MAX_HOLD_HEALTHY_HOLD` instead of selling just because the age threshold was reached.
- Momentum weakness is diagnostic only for this age guard; it does not by itself trigger a max-hold sell.
- Expected effect: the bot stops closing neutral/profitable positions "from boredom" while cash and entry slots are available, but still clears stale losing or structurally broken positions.

## 2026-07-05-post-entry-adverse-structure

- Dry-run exit tuning: raised `PositionExit.PostEntryAdverseLossPercent` from 1.2% to 2.0% so the early adverse guard does not behave like a tight stop-loss for volatile altcoins.
- `SELL_POST_ENTRY_ADVERSE` now requires structural deterioration in addition to early loss and negative recent price action: final score must be below 0.85 and either EMA is no longer bullish or momentum is no longer positive.
- Hard stop-loss remains separate at `PositionExit.StopLossPercent=2.5`.
- Expected effect: PYTH/EUR-style positions with score still at 0.85 and only a temporary negative price-action wobble are held for normal stop/exit logic instead of being cut immediately.

## 2026-07-05-restore-stop-loss-buffer

- Dry-run exit tuning: restored `PositionExit.StopLossPercent` from 1.5% back to 2.5% after recent dry-run stops showed that the tighter stop was being hit by normal altcoin noise once bid/slippage/fees were included.
- Kept `PositionExit.TakeProfitPercent=4.0` and the shortened 01:00-07:00 Lithuania-time new-entry blackout unchanged.
- No entry score, pair universe, order-size, correlation, or live execution behavior changes.

## 2026-07-04-tight-stop-wide-take-profit

- Dry-run exit tuning: tightened `PositionExit.StopLossPercent` from 2.5% to 1.5% so weak entries are cut earlier before drifting toward larger losses.
- Raised `PositionExit.TakeProfitPercent` from 3.0% to 4.0% so stronger movers can run longer before the hard take-profit exit.
- Shortened the new-entry blackout from 10 hours to 6 hours: `EntryBlackoutUtcFromHour=22`, `EntryBlackoutMinutes=360`, which maps to roughly 01:00-07:00 Lithuania summer time. Existing positions can still exit during the blackout.
- No entry score, pair universe, order-size, correlation, or live execution behavior changes.

## 2026-07-04-add-kraken-eur-movers

- Added Kraken-online EUR pairs from the market mover list to the worker candidate universe: `VANRY/EUR`, `HMSTR/EUR`, `TLM/EUR`, `MIM/EUR`, `RPL/EUR`, `OGN/EUR`, `GLMR/EUR`, `LIT/EUR`, `IDEX/EUR`, and `MIRA/EUR`.
- Confirmed Kraken pair altnames through the public AssetPairs API: `VANRYEUR`, `HMSTREUR`, `TLMEUR`, `MIMEUR`, `RPLEUR`, `OGNEUR`, `GLMREUR`, `LITEUR`, `IDEXEUR`, and `MIRAEUR`.
- Assigned the new pairs to existing correlation groups instead of leaving them ungrouped: `VANRY/EUR` and `GLMR/EUR` to `L1_L2`; `MIM/EUR`, `RPL/EUR`, `OGN/EUR`, `LIT/EUR`, and `IDEX/EUR` to `DEFI`; `MIRA/EUR` to `AI_HIGH_BETA`; `HMSTR/EUR` and `TLM/EUR` to `MEME_HIGH_BETA`.
- No score, order-size, exit, entry-threshold, or risk-limit changes.

## 2026-07-04-high-beta-dry-run-cap-3

- Dry-run risk tuning: raised the high-beta portfolio cap from 2 positions / EUR 20 exposure to 3 positions / EUR 30 exposure.
- Per-correlation-group caps stay unchanged (`MaxOpenPositionsPerGroup=1`, `MaxExposureEurPerGroup=10`), so the bot may add a third high-beta position only from a different group, not double up on the same group.
- Order size remains capped at EUR 10; score thresholds, early-entry behavior, blackout, hourly/cycle entry limits, and exit logic are unchanged.
- Expected effect: validate whether a third distinct high-beta group improves dry-run opportunity capture without increasing per-group concentration.

## 2026-07-04-early-ema-diagnostics

- Entry scoring now separates full EMA confirmation from early bullish EMA structure: full entries still require the configured `MinimumEmaGapPercent`, but sub-threshold bullish EMA gaps can receive partial diagnostic score and allow momentum / volume / trend confirmations to be measured.
- Smooth EMA contribution is diagnostic-safe: the maximum +0.30 bonus is still reserved for the full configured EMA gap, while early gaps ramp up from a small floor instead of staying all-or-nothing at zero.
- No live-risk expansion: `EntryGate` still rejects `AllowsLong=false` signals as `REJECT_NO_BULLISH_SIGNAL`; early-entry mode is NOT used to open positions in this change.
- Added early-entry diagnostics on decisions, `dry_run_decisions`, and top candidates: `hasBullishStructure`, `emaFullyConfirmed`, `bullishEmaGapPercent`, `emaGapVelocityPercent`, `earlyEntryEligible`, `earlyEntryReason`, `earlyEntryDiagnosticScore`, and `earlyEntrySuggestedNotionalEur`.
- EMA gap velocity is diagnostic-only and does not affect score, ranking, or exits.
- Expected effect: the next dry-run/replay can quantify how many former binary EMA rejects become meaningful early candidates without making the bot buy them yet.

## 2026-07-04-price-action-warmup-and-precise-rejections

- Price-action warm-up is now visible and short: on startup the worker hydrates its rolling snapshot history from the market snapshots persisted in the last `PriceActionHydrationMinutes` (default 45), so the anti-lag guard is READY on the first cycle after a restart instead of being blind for 25-30 minutes. Only recent rows are loaded — a long downtime gap results in a normal warm-up, never a stitched-together fake trend.
- Explicit warm-up diagnostics: every candidate now carries `priceActionState` (`READY` / `WARMING_UP` / `STALE` / `INSUFFICIENT_DATA`), `priceActionSamplesAvailable` / `priceActionSamplesRequired`, and oldest/newest sample timestamps; cycle diagnostics carry `priceActionReadyCount` (also appended to the `dry_run_cycle_entry_diagnostics` view and `/api/entry-diagnostics`).
- Staleness guard: a series whose newest sample is older than `PriceActionMaxSampleAgeMinutes` (default 30) reads as STALE/UNKNOWN and is treated as insufficient.
- Safe warm-up rule: live mode still force-enables `RequirePriceActionData`; the only way out is the new explicit `AllowEntriesWithoutPriceActionInLive` override. Dry-run config now also sets `RequirePriceActionData=true` (hydration makes the cost near zero). Exploratory admission requires KNOWN positive price action — UNKNOWN never passes for safe.
- Precise rejection reasons for near-threshold (>= exploratory score) candidates instead of a generic `REJECT_SCORE_BELOW_THRESHOLD`: `REJECT_PRICE_ACTION_UNKNOWN`, `REJECT_EXPLORATORY_REQUIRES_POSITIVE_PRICE_ACTION`, `REJECT_NO_MOMENTUM_CONFIRMATION`, `REJECT_NO_VOLUME_CONFIRMATION`; secondary missing confirmations (including `PRICE_ACTION_UNKNOWN`) stay in `missingConfirmations`.
- New exploratory spread limit `MaxExploratorySpreadPercent` (default 0.30%): small sampling entries no longer pay 0.43%+ spreads that are technically below the hard 0.5% max.
- Active-pair exclusion diagnostics now say WHY: 24h EUR volume rank vs the advisor's top-N cut, estimated 24h volume, spread (flagged when above the entry max), and the advisor's own rank when it did recommend the pair; excluded rows carry `volumeRank` / `est24hVolumeEur` / `spreadPercent` / `advisorRank`.
- New `TRADINGBOT_STRATEGY_*` overrides: `MAX_EXPLORATORY_SPREAD_PERCENT`, `PRICE_ACTION_MAX_SAMPLE_AGE_MINUTES`, `PRICE_ACTION_HYDRATION_MINUTES`, `ALLOW_ENTRIES_WITHOUT_PRICE_ACTION_IN_LIVE`.
- Expected effect: no post-restart blind window for the anti-lag guard, HBAR-style 0.85 candidates get actionable rejection reasons, exploratory entries stop paying wide spreads, and excluded-pair logs explain themselves.

## 2026-07-04-anti-lag-entry-gate-and-defensive-exits

- Added a snapshot-based anti-lag price-action layer: per-cycle light ticker snapshots feed a rolling in-memory history; LONG entries are rejected when recent real prices are falling/stalling even if candle indicators look bullish (this blocks the OP/EUR-style lagging EMA breakout buy).
- Scoring: missing volume confirmation now caps the final score at `Strategy.MissingVolumeScoreCap` (default 0.85, below the firm 0.90 entry bar); negative recent price action costs `NegativePriceActionPenalty` (default 0.05).
- Split NEW-entry decisioning into explicit layers (`EntryGate`): hard safety filters (pair tradability, max spread, min 24h volume) then quality filters (score threshold, anti-lag guard, volume rules); every rejected pair carries a compact `REJECT_*` reason.
- Live-mode hardening: entries with missing pair rules, any non-`online` status, or a missing/invalid bid-ask book are hard-rejected; `Normalize()` also forces `RequirePriceActionData` on in live mode, so after a restart the worker warms up (`PriceActionMinSnapshots` cycles) before it may open live positions.
- Live execution ordering fixed: the real order goes to the exchange FIRST (computed on a portfolio clone); the virtual portfolio commits only after `LIVE_SUBMITTED`. Failed/skipped live orders record `LIVE_ORDER_FAILED` and leave the portfolio untouched (no phantom positions/exits).
- Added TIER 2.5 defensive exits: score-decay (entry score >= 0.90 whose current score collapses to <= 0.40, or stays <= 0.50 for 2 cycles, exits losing positions before stop-loss) and a post-entry adverse-movement guard (first 30 minutes, down >= 1.2%, score no longer confirms, negative price action).
- Added dry-run exploratory entry mode: score >= 0.85 candidates may enter ONLY with positive recent price action, clean spread, and a top-2 ranking slot; force-disabled in live unless `ExploratoryAllowedInLive`.
- New-entry candidates are ranked (score, EMA gap, RSI quality) before consuming per-cycle/max-open limits.
- Every cycle now logs and persists entry-funnel diagnostics (`entryDiagnostics` in cycle records): score-bucket counters, hard-filter pass counts, top candidates with rejection reasons, excluded pairs, and an explicit chosen-pair or no-trade reason. New DB view `dry_run_cycle_entry_diagnostics`, extra columns on `dry_run_decisions`, new API endpoint `/api/entry-diagnostics`.
- All new knobs have `TRADINGBOT_*` environment overrides.
- Expected effect: fewer lagging-indicator entries, earlier exits on failed high-conviction entries, more (guarded) dry-run samples via exploratory mode, and zero silent no-trade cycles.

## 2026-07-04-cycle-worker-metadata-columns

- Added queryable worker metadata columns to `dry_run_cycles`.
- Added cycle API filters for worker commit/version, strategy version, change set, and latest strategy version.
- Expected effect: cycle analysis can isolate records produced by a specific worker logic version without parsing raw JSON.

## 2026-07-04-worker-version-metadata

- Added automatic worker build metadata for persisted cycle records.
- Added CI enforcement that worker logic changes must update this changelog.
- No trading strategy behavior change.
