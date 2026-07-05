# Worker changelog

Latest entry must be first. The first `## <id>` heading is used as `worker.changeSet`.

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
