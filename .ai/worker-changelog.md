## 2026-07-17-decisions-explainability-refactor

- Futures decision records now persist the actual SHORT-side diagnostics (`ShortScore`, bearish EMA gap/structure, allow verdict, configured score/EMA thresholds) instead of forcing the Decisions UI to mislabel the LONG score as the reason a SHORT was rejected.
- Base SHORT rejection now has an explicit machine-readable code and exact explanation: bearish EMA not confirmed, SHORT score below the signal threshold, or missing downside momentum/volume/trend confirmation. Trading decisions, thresholds, sizing, and execution behavior are unchanged.
- The Decisions page now resolves explicit gate codes before prose fallbacks and presents side-aware, human-readable verdicts while retaining raw technical evidence for audit.

## 2026-07-17-futures-fixed-working-exits-trailing-protection

- Bot-owned futures positions now use fixed working exit policy from `TpSl`: working TP `TakeProfitPercent` and working SL `StopLossPercent` are frozen from entry and no longer silently inherit ATR-derived `entryPlan` distances.
- Live bot-owned entries place farther exchange reduce-only protection using `ExchangeProtectionMultiplierPercent`: with TP=3, SL=1, multiplier=200, Kraken receives emergency TP +6% and emergency SL -2% while the bot monitors +3%/-1%.
- Working SL/TP checks use closeable live quotes when available: LONG uses bid and SHORT uses ask before falling back to configured mark/last. `KRAKEN_SYNC`/external positions are excluded from simulated/working TP/SL triggers and from new protection/trailing mutations.
- When a bot-owned live position reaches working TP, the worker cancels protective TP/SL and arms a reduce-only Kraken `trailing_stop` using `TrailingStopPercent`; if trailing creation fails after cancel, it immediately attempts to restore the protective SL.
- Kraken Futures broker support added for single-order cancel and percent trailing-stop orders; portfolio state now records separate working prices, exchange protection prices, and trailing-stop state.

## 2026-07-17-futures-external-position-soft-exit-guard

- Futures positions now carry `Origin`: worker-opened entries are tagged `BOT`, while live Kraken reconciliation tags newly adopted exchange positions as `KRAKEN_SYNC` and preserves any existing origin.
- `KRAKEN_SYNC` / external futures positions are no longer closed by soft strategy exits: signal-reversal closes are converted to HOLD with `EXTERNAL_SIGNAL_FLIP_BLOCK`, and max-hold stale exits are skipped for those positions. Hard TP/SL fallback triggers run only for protection legs that are not already represented by live Kraken `EXCHANGE_OPEN` reduce-only orders.
- HOLD actions now preserve an explicit reason when the desired side already matches the held position, so the UI/journal can explain that an adopted Kraken position ignored a reversal instead of showing a generic hold.

## 2026-07-16-futures-400-notional-live-caps

- Restored futures live sizing to the requested 40 EUR margin × 10x leverage target: `Futures.TargetMarginEur`=40 and `Risk.TargetRiskEur`=3 so a 0.75% floor-stop entry sizes to 400 EUR notional before ATR shrinkage.
- Raised the independent caps that were still clipping entries after the ATR risk-sizing change: `Futures.MaxNotionalEur` 150 → 400, `Futures.MaxMarginPerPositionEur` 20 → 40, aggregate `Futures.MaxTotalNotionalEur` 300 → 1200 for the three configured slots, and `CorrelationRisk.MaxExposureEurPerGroup` 150 → 400.
- Raised `Risk.MaxConcurrentOpenRisk` 3 → 9 so the configured three slots can each carry the 3 EUR stop-distance budget. Volatile instruments can still size below 400 when ATR requires a wider stop; the caps no longer force every calm/floor-stop setup down to ~150 notional.

## 2026-07-16-futures-short-entry-guard-mirror

- New authoritative SHORT entry gate `FuturesShortEntryGuard` — the mirror of the LONG range/freshness guards, evaluated on the EXECUTABLE BID (a short sells into the bid). It runs only when `desired == Short` and `Shorts.RangeGuardEnabled`. LONG is never touched by it; SHORT sizing/risk/cost/caps are unchanged (already symmetric).
- Mirror mapping (LONG → SHORT): rebound-from-24h-low → PULLBACK-from-24h-high; rising snapshots → FALLING snapshots; positive slope → NEGATIVE slope; fresh upward tape → fresh DOWNWARD tape (falling steps + slope ≤ −`FreshTapeMinSlopePct` + latest ≤ average, gated by non-positive `ContinuationCandleMomentum`); entry-near-local-high (block unless breakout) → entry-near-local-LOW (block unless confirmed BREAKDOWN); drift +x% above signal (chase up) → drift −x% below signal (chase the dump). Wick 24h position stays diagnostic only, so a mid-range reclaim-DOWN after wide spikes passes (mirror of the LONG reclaim).
- Ordered block reasons: `SHORT_RANGE_UNAVAILABLE`, `SHORT_PULLBACK_FROM_24H_HIGH_TOO_SMALL`, `SHORT_FALLING_SNAPSHOTS_NOT_CONFIRMED`, `SHORT_SLOPE_NOT_NEGATIVE`, `SHORT_FRESH_TAPE_NOT_CONFIRMED`, `SHORT_ENTRY_TOO_CLOSE_TO_LOCAL_LOW`, `SHORT_ENTRY_DRIFT_TOO_HIGH`. A confirmed breakdown exempts the local-low and drift checks (mirror of the breakout exemption). Degenerate/insufficient data fails safe (SHORT blocked).
- Gate precedence in the worker: LONG range guard → SHORT entry guard → freshness guard → quality gate. Short entry channels added: `ShortBreakdown` / `ShortContinuation` / `ShortReclaim` / `Standard` (mirror of Breakout / Continuation / Reclaim). Block reason wins the hold-reason code; side-agnostic diagnostics (range basis, close-percentile, recent-swing, range-blocked flag) are persisted and a full `SHORT_ENTRY` telemetry line is logged.
- Mechanical thresholds are SHARED with `Freshness` at the same magnitudes, mirrored in direction (tape counts, lookbacks, breakdown buffer, candle-momentum, local-low lookback, drift). New SHORT-specific structural config in `Shorts` (validated in Normalize): `RangeGuardEnabled`=true, `Min24hRangePositionForShort`=70 (diagnostic band, not a veto), `MinPullbackFrom24hHighPct`=0.20, `RequiredFallingSnapshotCount`=2, `RequireNegativeShortSlope`=true, `RequireFreshTapeForHighRangeShort`=true.
- Tests: `FuturesShortEntryGuardTests` (fresh down-tape passes, reclaim-down mid-range passes, near-local-low blocks, rising tape blocks, no-pullback-at-top blocks, not-evaluated for LONG/disabled). Full FuturesWorker suite: 108 passing.

## 2026-07-16-futures-atr-risk-sizing-and-reclaim

- ATR stop + risk-based notional as one contract: `requiredStop = StopAtrMult * atrPct`; `stopPct = max(requiredStop, floor)`; `notional = TargetRiskEur / (stopPct/100)` then independent notional/margin caps. Leverage sets required margin only.
- ATR stop is NOT silently clamped from above. If `requiredStop > Exits.StopDistanceCapPct` the entry is BLOCKED with `STOP_DISTANCE_TOO_LARGE` (placing a stop inside the instrument's own volatility would guarantee churn). Only the floor is applied up.
- Safer live defaults for ~100 EUR equity: `Risk.TargetRiskEur` 3 → 1.00 (1% equity/position); `Risk.MaxConcurrentOpenRisk` = `TargetRiskEur * MaxPositions` = 3.00 (3% equity). Worked example @10x, 0.75% floor stop: notional ≈ 133 EUR, margin ≈ 13.3 EUR; three slots ≈ 40 EUR margin (40% util), 3 EUR stop heat.
- Independent portfolio caps (all enforced separately in `MarginRiskManager`): per-position notional `Futures.MaxNotionalEur`=150, aggregate notional `Futures.MaxTotalNotionalEur`=300, per-position margin `Futures.MaxMarginPerPositionEur`=20, total used margin via `Margin.MaxAccountMarginUtilizationPercent`=80%, concurrent stop heat `Risk.MaxConcurrentOpenRisk`=3, per correlation group `CorrelationRisk.MaxExposureEurPerGroup` 400 → 150. Open-risk cap does not replace notional/margin caps (gap/slippage/stop-failure/correlation tail).
- Concurrent open-risk cap = PURE stop-distance heat summed over positions (= budget). Realistic per-trade worst case (stop + round-trip taker + slippage + emergency) is computed in the sizer (`ProjectedOpenRiskEur`) and reported per trade; gap/slippage tail is bounded by the notional caps.
- TP always applies `max(MinRewardRiskMultiple × stop, MinTpVsCostMult × roundTrip, TP floor)` even when `TpSl.Enabled=true`.
- Legacy-parameter migration (documented): `TpSl.StopLossPercent` is now the MINIMUM stop floor (not a fixed stop) and `TpSl.TakeProfitPercent` the MINIMUM TP floor. They seed the decoupled `Exits.MinStopDistancePct` / `Exits.MinTakeProfitPct` when those are unset (0). Do not "fix" losses by widening the legacy stop against a fixed notional — the sizer shrinks notional for wider stops.
- Unified live/virtual cost model `taker_ioc_round_trip` (entry+exit taker + slippage + adverse funding). Virtual open AND close fees use `Fees.TakerPct`; removed `dry-run-maker-post-only` / `MODELED_MAKER` defaults.
- `FuturesLongRangeGuard` no longer hard-vetoes wick `position24h > 30%`. Close-percentile / recent-swing are the structural basis; wick 24h is diagnostic. Reclaim after wide spikes can pass; local-high / drift / falling-knife / freshness still block. Entry channels: DipBounce / Continuation / Breakout / Reclaim / Standard. Short logic unchanged.


- New authoritative LONG 24h-range gate (`FuturesLongRangeGuard`). A LONG is admitted only in the lower band of a ROBUST 24h range (percentile 5/95 over the last 96 closed 15m candles, absolute fallback with a source tag) AND with a confirmed upward reversal. The range position is measured on the EXECUTABLE ASK, not the candle close/last, so it reflects where the order would actually fill. Fresh tape can never bypass the range, local-high, or drift checks.
- Ordered checks with distinct block reasons: `LONG_24H_RANGE_UNAVAILABLE`, `LONG_24H_RANGE_TOO_NARROW`, `LONG_24H_RANGE_POSITION_TOO_HIGH` (> `Max24hRangePositionForLong`, default 30%), `LONG_REBOUND_FROM_24H_LOW_TOO_SMALL` (< `MinReboundFrom24hLowPct`), `LONG_RISING_SNAPSHOTS_NOT_CONFIRMED`, `LONG_SHORT_SLOPE_NOT_POSITIVE`, `LONG_FRESH_TAPE_NOT_CONFIRMED`, `LONG_ENTRY_TOO_CLOSE_TO_LOCAL_HIGH`, `LONG_ENTRY_DRIFT_TOO_HIGH`. A confirmed breakout exempts the local-high and drift checks but NEVER the range-position cap. Insufficient/degenerate ranges fail safe (LONG blocked, never a divide-by-zero or a silent admit).
- Reuses the freshness guard's existing signals (rising snapshots, short slope, fresh tape, local-high distance, entry drift, breakout) instead of duplicating them; adds only the robust range position and rebound-from-low confirmation. SHORT is never evaluated by this guard and is unchanged.
- New config in `Freshness` (all validated in Normalize): `LongRangeGuardEnabled`, `Max24hRangePositionForLong`=30, `MinReboundFrom24hLowPct`=0.20, `RequiredRisingSnapshotCount`=2, `RequirePositiveShortSlope`=true, `RequireFreshTapeForLowRangeLong`=true, `RobustRangeMinSampleCount`=20, `Min24hRangeWidthPct`=0.50, with `TRADINGBOT_FUTURES_LONG_*` env overrides. Diagnostics (entry price + source, absolute/robust 24h low/high, range source + sample count, raw/clamped position, rebound, rising count, block flag + reason) are persisted on the decision action and exposed on the `dry_run_decisions` view.
- Effect: continuation and breakout LONGs above the lower 30% of the range are now rejected — the primary long path becomes a confirmed reversal near the bottom of the daily range. This supersedes the earlier upper-range continuation rule as the binding constraint (that rule remains as a secondary layer).

## 2026-07-16-futures-upper-range-continuation-guard

- Root-cause of the VIRTUAL/USD peak buy (fill 0.62844 at pos24 ~86.7%, cycle futures-live-20260716033116): a fresh 3-snapshot micro-rebound (0.62519 → 0.62778 → 0.62783) off a pullback admitted a Continuation LONG while the last closed 15m candle was RED (O 0.630507 → C 0.627982) and the price sat in the top of the 24h range. The near-high block did not fire (86.7% < 88%), the local-high chase guard did not fire (0.47% below local high > 0.12%), and there was no breakout — so a fresh continuation tape carried the entry into the daily top.
- Fix: fresh continuation tape is no longer sufficient in the upper band of the 24h range. Above `Freshness.MaxContinuationRangePositionPct` (new, default 80%) a LONG requires a CONFIRMED breakout (price above the recent high, held over `BreakoutHoldSnapshotCount` snapshots); a fresh micro-tape alone is rejected with reason "entry upper-range continuation". Below the band, behavior is unchanged. Env override `TRADINGBOT_FUTURES_FRESHNESS_MAX_CONTINUATION_24H_RANGE_POSITION_PERCENT`; clamped to [50, 100].
- Note on the range-position basis: `CalculateRangePosition` still uses the last 15m candle close (not the executable ask). Recomputing VIRTUAL on the executable fill against the candle 24h range gives ~87.5%, still below the 88% near-high line, so switching the basis alone would not have blocked it and would have shifted every calibrated fixture; the upper-range rule (default 80%) closes the hole robustly regardless of the exact basis. The basis change remains available as a future refinement.
- Not changed: dip-bounce channel, near-high (>=88% and within 0.5% of recent high) and local-high/drift guards, sizing/leverage, TP/SL, scoring weights, short entries, or execution semantics.

## 2026-07-16-futures-dip-bounce-momentum-and-diagnostics

- Dip-bounce now demands a real up-tick, not merely a non-falling candle: promotion requires recent 15m candle momentum >= `Dip.MinCandleMomentumPct` (a small POSITIVE floor, default 0.10%) on top of the fresh upward tape, and momentum that cannot be computed no longer qualifies. Env override `TRADINGBOT_FUTURES_DIP_BOUNCE_MIN_CANDLE_MOMENTUM_PERCENT`; config/DB-tunable.
- Confirmed dip-bounce does not bypass any shared guard. A promoted entry still runs the full gauntlet: entry-quality gate (spread, anti-lag price action, warm-up, anti-extension, run-up), freshness guard, portfolio guards (pending-order dedupe, entry blackout, post-close and post-stop-loss cooldowns, correlation group/exposure caps), and the margin risk manager (liquidation distance, margin utilization, open-risk cap, funding, exit depth, BTC regime — a sub-0.85 dip score cannot trigger the long regime override).
- Decision rows now persist the dip-bounce admission context for post-hoc analysis: recent candle momentum (`entry_freshness_recent_candle_momentum_pct`) and the relaxed threshold that admitted the entry (`dip_bounce_min_score_applied`), alongside the already-recorded 24h range position, fresh-tape flag, entry channel, and score. Both new columns are exposed on the `dry_run_decisions` view.
- Not changed: dip-bounce near-low zone / min-score, sizing/leverage, TP/SL, local-high and drift guards, scoring weights, short entries, or execution semantics.

## 2026-07-15-futures-local-high-entry-guard

- Futures continuation LONG entries now evaluate the executable entry reference price before submit (ask for longs) against the local high of the last closed 15m candles. A fresh upward tape no longer bypasses this guard: if the entry price is within `Freshness.MaxEntryDistanceFromLocalHighPct` (default 0.12%) of the local high, the entry is rejected unless a breakout is confirmed.
- Breakout confirmation now requires more than touching the high: price must exceed the local high by `Freshness.BreakoutMinAboveRecentHighPct` and hold above that buffered level for `Freshness.BreakoutHoldSnapshotCount` recent snapshot observations. New local-high settings are env-tunable via `TRADINGBOT_FUTURES_FRESHNESS_LOCAL_HIGH_LOOKBACK_CLOSED_CANDLES`, `_MAX_ENTRY_DISTANCE_FROM_LOCAL_HIGH_PERCENT`, and `_BREAKOUT_HOLD_SNAPSHOT_COUNT`.
- Added an executable-price drift guard: entries are rejected when the live executable price has moved more than `Freshness.MaxEntryDriftFromSignalPct` (default 0.10%) from the candle signal close, unless the move is a confirmed breakout. This blocks chase entries like the INJ case where signal close was 5.140747 but executable/fill moved to ~5.1468.
- Entry diagnostics now persist local-high and drift telemetry on decision actions and SQL views: entry distance from local high, local high source, breakout buffer, live price vs signal close, plus post-fill local-high/drift measurements for accepted live orders.
- Not changed: scoring weights, dip-bounce promotion rules, short entries, margin sizing/leverage, TP/SL exchange order placement, funding/BTC-regime gates, or IOC execution semantics.

## 2026-07-15-futures-dip-bounce-and-40x10-sizing

- Sizing: futures now trades at margin 40 EUR × 10x leverage (`Futures.TargetMarginEur` 10 → 40; notional 400 EUR ≈ 432 USD per position). At `Portfolio.StartingCashEur` 100 EUR and the 80% `Margin.MaxAccountMarginUtilizationPercent` cap this leaves room for ~2 concurrent positions (40% equity each); `Risk.MaxConcurrentOpenRisk` and per-group exposure recompute from the new notional automatically.
- TP/SL: `TpSl.TakeProfitPercent` 3.0 → 1.5, `StopLossPercent` 2.0 → 0.75 (R:R 2:1; on 400 EUR notional ≈ +6 EUR / −3 EUR). Both stay above `Exits.MinTpVsCostMult` × round-trip friction. Note: 0.75% is a tight stop on 15m perps and will stop out more often on noise — this is an intended strategy choice.
- New dip-bounce entry channel: a LONG candidate whose score is below the firm `Strategy.MinimumLongScore` but at or above `Dip.MinScore` (0.70) is admitted when price sits in the lower `Dip.NearLowMax24hRangePositionPct` (25%) band of its 24h range AND a confirmed bounce is visible — a fresh upward snapshot tape plus non-negative 15m candle momentum (the exact freshness the continuation channel already demands). It never catches a falling knife: without the fresh tape+momentum the candidate stays flat, and every promoted entry still runs the full quality / freshness / margin / regime gauntlet. New `Dip` config (`Enabled`, `NearLowMax24hRangePositionPct`, `MinScore`) with env overrides `TRADINGBOT_FUTURES_DIP_BOUNCE_ENABLED` / `_NEAR_LOW_MAX_24H_RANGE_POSITION_PERCENT` / `_MIN_SCORE`; `MinScore` is config/DB-tunable without a recompile. Each promotion logs `DIP_BOUNCE_ENTRY`.
- Entry-channel attribution: every opened position is tagged Standard / Continuation / Breakout / DipBounce; the tag is stored on the open action, carried onto the position, and copied to the close action so realized PnL, win-rate and MFE/MAE can be compared per channel in SQL. New `entry_channel` column exposed on the `dry_run_decisions` and `portfolio_positions` views (and preserved across live Kraken reconciliation).
- Not changed: EMA/RSI scoring math, short entries, execution/price-deviation control, dead-man switch, liquidation-distance/funding gates, cooldowns/blackout, or fast-exit cadence.

## 2026-07-13-futures-freshness-candle-momentum

- Tightened the futures long-continuation freshness guard so a fresh micro-tape can no longer rescue an entry into a rolling-over 15m structure (the DOGE case: last 3 snapshots ticked up while the 4-candle change was ~-0.9% and price action was FALLING, yet the entry passed). In the continuation/near-high zone a tape now counts as fresh only when it is a fresh upward snapshot tape AND the recent candle momentum over `Freshness.ContinuationCandleMomentumLookback` (4) bars is at least `Freshness.MinContinuationCandleMomentumPct` (0 → non-negative). A genuine breakout above the recent high is unaffected; momentum that cannot be computed abstains (does not block).
- New config `Freshness.ContinuationCandleMomentumLookback` / `MinContinuationCandleMomentumPct` with env overrides `TRADINGBOT_FUTURES_FRESHNESS_CONTINUATION_CANDLE_MOMENTUM_LOOKBACK` / `_MIN_CONTINUATION_CANDLE_MOMENTUM_PERCENT`. Codex's continuation-zone default (`FreshContinuationMin24hRangePositionPct=50`) already generalized the near-high guard across the range; this adds the candle-momentum confirmation on top.
- Deploy: live worker appsettings.json is now refreshed from the repository on EVERY deploy (identical to virtual) for both spot and futures, instead of being create-once/operator-owned. Live and virtual now always run the same strategy config; operator-specific overrides stay in the preserved live .env files, which are unchanged.
- Not changed: sizing, leverage, execution/price-deviation, margin/funding/regime gates, TP/SL, dead-man switch, short entries.

## 2026-07-15-futures-margin-utilization-80

- Futures margin utilization cap default is raised from 50% to 80% (`Margin.MaxAccountMarginUtilizationPercent`) so one oversized imported/manual position does not block every additional 10 EUR margin entry while there is still account headroom.
- Not changed: target margin/leverage sizing, max positions, liquidation-distance gate, TP/SL exchange protection, freshness/entry quality gates, spread checks, or live execution semantics.

## 2026-07-15-futures-exchange-tpsl-reconciliation

- Futures live reconciliation now reads Kraken Futures open orders and treats existing reduce-only `stp` / `take_profit` orders on the closing side as the source of truth for a position's TP/SL. Existing exchange TP/SL orders are preserved: the worker does not cancel, edit, or replace them.
- When a live futures position has no exchange stop-loss or take-profit, the worker places only the missing reduce-only trigger order using the configured TP/SL distance and trigger source. Trigger prices are rounded to the instrument price precision before submit. New live IOC entries arm exchange TP/SL immediately after a known fill, using the real filled quantity and price.
- Added Kraken Futures `/openorders` support and trigger-order submit support. The futures dead-man switch is now opt-in via `Futures.DeadManSwitchEnabled` / `TRADINGBOT_FUTURES_DEAD_MAN_SWITCH_ENABLED` so persistent exchange TP/SL orders are not canceled just because the bot stops refreshing.
- Not changed: entry scoring, sizing/leverage, position sync, TP/SL percentages, fast-exit cadence, reduce-only close semantics, or virtual-mode simulated TP/SL behavior.

## 2026-07-15-futures-max-hold-stop-progress

- Futures max-hold stale-loss exits now require meaningful adverse progress toward the configured stop-loss, not merely a negative mark-to-market value. After `Exits.MaxHoldMinutes`, a losing position is held when its stop progress is below `Exits.MaxHoldMinStopProgressPct` (default 60%), so small losses far from SL can continue.
- Added `Exits.MaxHoldMinStopProgressPct` plus env override `TRADINGBOT_FUTURES_EXITS_MAX_HOLD_MIN_STOP_PROGRESS_PERCENT`. When stop-loss data is missing, stale losing positions still fail closed after max-hold.
- Not changed: TP/SL precedence, profitable max-hold holds, signal-reversal exits, entry behavior, sizing/leverage, IOC execution, or Kraken reconciliation.

## 2026-07-15-futures-conditional-max-hold

- Futures max-hold exits are now stale-loss exits instead of a hard age timer. After `Exits.MaxHoldMinutes`, a futures position is closed only when its marked unrealized PnL is negative; profitable or flat positions are held for TP/SL, trailing, or signal reversal handling instead of being closed solely because they are old.
- The fast 10-second exit check now uses the same conditional max-hold rule as the full cycle, preventing profitable imported/manual futures positions from being closed by the fast path just because they crossed 360 minutes.
- Updated the trades UI copy for futures closes so SHORT exits are described as closing a short, and max-hold text no longer claims every futures max-hold close was a stale long sell.
- Not changed: TP/SL precedence, signal-reversal exits, sizing/leverage, entry filters, funding/margin/BTC-regime gates, IOC execution, or Kraken reconciliation.

## 2026-07-14-futures-long-continuation-freshness

- Futures LONG entry freshness is now stricter above the lower half of the 24h range: once `FreshContinuationMin24hRangePositionPct` is reached (default 50%), bullish EMA/RSI structure is not enough by itself. A LONG must have either fresh upward tape or a valid breakout; otherwise it is rejected as `entry stale continuation`.
- Added env override `TRADINGBOT_FUTURES_FRESHNESS_FRESH_CONTINUATION_MIN_24H_RANGE_POSITION_PERCENT` and appsettings default `Freshness.FreshContinuationMin24hRangePositionPct=50` for live tuning. The existing near-high stale-entry guard remains in place for the stricter high-zone explanation.
- Not changed: scoring weights, volume cap, spread/funding/margin/BTC-regime gates, sizing/leverage, TP/SL, IOC execution, or Kraken reconciliation.

## 2026-07-14-futures-live-price-precision-imported-tpsl

- Futures live entries now round IOC limit prices to the instrument's Kraken Futures price precision before submit. The shared instrument registry stores `price_decimals` from Kraken `tickSize`; entries fall back to quote-derived precision when the registry has not been refreshed yet. This prevents marketable-limit orders such as `0.085044216800700` from being rejected by Kraken as `invalidPrice`.
- Futures Kraken reconciliation now arms imported/manual positions with simulated TP/SL levels when the position exists on Kraken but the local ledger has no saved levels. The levels use the configured fixed TP/SL percentages from entry price (`TpSl.TakeProfitPercent`, `TpSl.StopLossPercent`) and set both simulated order states to open, so fast/full exit checks can close externally opened positions at TP/SL.
- Not changed: entry scores, spread/freshness/BTC/funding/margin gates, target margin/leverage sizing, reduce-only close order type, or the 10-second fast-exit cadence.

## 2026-07-14-market-snapshot-query-indexes

- Added schema-managed indexes for high-volume history lookups: `market_snapshots(bot_instance_id, pair, utc desc, cycle_id desc)` and `dry_run_cycles(bot_instance_id, utc desc, cycle_id desc)`. Live `market_snapshots` has millions of rows; pair-specific UI/API reads previously scanned recent rows for all pairs before filtering.
- Not changed: trading decisions, scoring, risk, sizing, entry/exit behavior, live execution, portfolio reconciliation, or strategy metadata.

## 2026-07-14-futures-short-btc-regime-override

- Added a futures short BTC-regime override mirroring the existing long override. Strong confirmed short candidates can now pass when BTC itself is not in the bearish short regime if `ShortScore >= Regime.ShortOverrideMinScore` (default 0.85); weak shorts remain blocked by pair bearish confirmation, `Shorts.MinShortScore`, and the existing BTC gate.
- Added env override `TRADINGBOT_FUTURES_BTC_REGIME_SHORT_OVERRIDE_MIN_SCORE` and default appsettings value `Regime.ShortOverrideMinScore=0.85` for live tuning. Spread, maker-fill, price-action, freshness, funding, margin, TP/SL, and IOC execution gates are unchanged.

## 2026-07-14-futures-correlation-cap-100-notional

- Raised the futures default per-correlation-group exposure cap from EUR 10 to EUR 100 so the risk layer matches the current `TargetMarginEur=10` at `DefaultLeverage=10` sizing. With the old explicit cap, a normal 100 EUR notional entry was rejected as `correlation group ... exposure EUR 100 exceeds cap EUR 10`, blocking otherwise eligible live candidates before execution.
- Not changed: one open position per correlation group, leverage, target margin, TP/SL, entry freshness, spread/price-action quality gates, BTC regime, IOC execution, and Kraken reconciliation.

## 2026-07-13-futures-entry-freshness-ioc-fill-control

- Added a futures-only entry freshness guard before portfolio/risk/BTC-regime override checks. LONG entries near the recent high are now blocked when the 24h range position and recent-high distance show a late entry and the short live tape does not confirm fresh upside. The defaults (`NearHighMin24hRangePositionPct=88`, `NearHighMaxDistanceFromRecentHighPct=0.5`, 12-candle high lookback, 3-snapshot tape, `FreshTapeMinSlopePct=0.05`) are chosen from the RIVER-block / JUP-pass forensic cases and should be re-tuned on more live history.
- Added `SnapshotPriceHistory.RecentObservations` and persisted freshness diagnostics on decision actions: 24h range position, distance from recent high, latest snapshot step, short tape slope, positive steps, near-high/fresh-tape/breakout flags, and block reason. Freshness rejections map to `REJECT_ENTRY_STALE_NEAR_HIGH`.
- Live futures entries now refresh the Kraken Futures ticker immediately before submit and reject entries whose refreshed quote already exceeds `Entry.MaxEntryPriceDeviationPct` (default 0.35%) from the signal reference. Accepted entries are submitted as IOC marketable-limit orders at the allowed limit instead of uncontrolled market orders; reduce-only exits remain market orders.
- Kraken Futures order results now parse execution events into filled quantity, average fill price, fee when present, and fill timestamp. The local ledger commits only exchange-confirmed filled quantity at the real average fill price; missing fill readback becomes `FILL_RECONCILIATION_PENDING` and blocks duplicate same-symbol entries until Kraken reconciliation imports the position.
- Decision records now include execution telemetry (`SignalPrice`, `PreSubmitBid/Ask`, `SubmittedLimitPrice`, requested/filled quantity, average fill, deviation from signal/ask, exchange order id/fill timestamp). The SQL decisions view exposes the new freshness and execution fields.
- Not changed: margin-based sizing semantics from `TargetMarginEur * leverage`, TP/SL percentages, max leverage, funding/depth/margin gates, short scoring, market-data universe discovery, and reduce-only exit behavior.

## 2026-07-13-futures-margin-based-sizing

- Fixed futures position sizing to be margin-based. Previously `Futures.TargetNotionalEur=10` was treated as the position NOTIONAL, so at 10x leverage the bot posted only ~1 EUR margin (observed RIVER: 2.8 units, ~9.83 USD notional, ~0.98 USD margin). The business input is now `Futures.TargetMarginEur` (initial margin), and the position notional is derived ONCE as `TargetMarginEur * leverage` via `FuturesOptions.DerivedNotionalEur`. At `TargetMarginEur=10`, `DefaultLeverage=10` the bot now opens ~100 EUR notional and posts ~10 EUR margin; RIVER quantity becomes ~30.8 (≈11x the old 2.8). Nothing multiplies by leverage a second time.
- Added `Futures.UsdPerEur` (appsettings 1.08, code default 1.0) to convert the EUR notional into the instrument's USD quote currency for the contract quantity only; the EUR margin/notional books are unchanged by FX. Quantity = notionalEur * UsdPerEur / markPrice.
- Migration: the legacy `TargetNotionalEur` name (and `TRADINGBOT_FUTURES_TARGET_NOTIONAL_EUR`) is deprecated. When only the legacy value is set, Normalize migrates it to `TargetMarginEur = legacyNotional / leverage`, PRESERVING the old exposure (not silently 10x-ing it) and logging a one-time warning. New name/env: `TargetMarginEur` / `TRADINGBOT_FUTURES_TARGET_MARGIN_EUR`; FX env `TRADINGBOT_FUTURES_USD_PER_EUR`.
- Risk-limit semantics realigned to the notional (not the margin figure): per-group correlation exposure now defaults to one derived notional (margin*leverage), and `Risk.MaxConcurrentOpenRisk` is recomputed when a legacy value would block a normal entry (a stop-out loses ~notional*stopPct; the default now covers MaxPositions positions). These were recomputed, not arbitrarily changed.
- Decision records now carry `RequestedMarginEur`, `RequestedLeverage`, `RequestedNotionalEur`, `ActualInitialMarginEur`, `ActualEffectiveLeverage` alongside quantity/fill, and a structured `POSITION_SIZING` log line captures target margin, leverage, derived notional, FX, quantity, estimated margin and available collateral.
- Not changed in this commit: entry-freshness guard and execution price control / real-fill reconciliation (follow-up commits). Scoring, TP/SL, leverage cap, margin/funding/regime gates and the dead-man switch are unchanged.

## 2026-07-13-futures-btc-regime-long-override

- Futures BTC regime still blocks ordinary longs while BTC is below its trend filter or crashing, but high-confidence long candidates can now override that block when `signal.Score >= Regime.LongOverrideMinScore` (default 0.85). The override is recorded in the BTC regime diagnostic text passed into the risk/journal path.
- Added `Regime.LongOverrideMinScore` plus `TRADINGBOT_FUTURES_BTC_REGIME_LONG_OVERRIDE_MIN_SCORE` for live tuning without replacing operator-owned appsettings or secrets.
- Not changed: short regime, short score gate, spread/extension/price-action quality gates, funding/margin/depth gates, leverage, sizing, TP/SL, or Kraken execution.

## 2026-07-13-futures-short-score-gate

- Futures short entries now gate on the scorer's dedicated bearish `ShortScore` instead of the regular long-biased `Score`. Previously the diagnostics could show a valid short diagnostic score while `EvaluateShortGate` rejected the trade as `short score < threshold` using the unrelated long score, effectively suppressing legitimate short candidates.
- No change to long entries, leverage, sizing, TP/SL, BTC regime, spread/extension gates, or live Kraken execution. Shorts still require bearish EMA structure, downside confirmation, BTC short regime, and `Shorts.MinShortScore`.

## 2026-07-13-futures-entry-tuning-fixed-tpsl

- Tuned futures entry gates from live snapshot replay: `Strategy.MinimumLongScore` 0.85 -> 0.80, `Strategy.MinimumEmaGapPercent` 0.30 -> 0.20, `Strategy.MaxEntrySpreadPercent` 0.15 -> 0.25, `Strategy.MaxEntryExtensionPercent` 0.6 -> 1.0, and `Shorts.MinShortScore` 0.90 -> 0.85. Position size, leverage, max positions, and universe discovery are unchanged.
- Futures fixed TP/SL settings are now preserved as the execution source when `TpSl.Enabled=true`: `TpSl.TakeProfitPercent=3.0` and `TpSl.StopLossPercent=2.0` are no longer overwritten by ATR exit multipliers during config normalization, and entry plans use those fixed percentages for stop/take-profit distances.
- Added futures env overrides for the tuned strategy thresholds, short score, and fixed TP/SL percentages so operator-owned live appsettings/env files can be updated without replacing secrets or local-only server settings.

## 2026-07-13-futures-fast-exit-journal

- Futures fast-exit closes now persist a dedicated dry-run cycle event with the close decision, `WOULD_CLOSE` action, exit reason, ledger before/after, and worker metadata. Previously fast-exit updated `portfolio_state` and logs only, so UI/API trade journals could show the live entry without the matching close even though Kraken and portfolio sync were already flat.
- The dry-run cycle summary view now counts `WOULD_CLOSE` as a sell alongside spot-style `WOULD_SELL`, so futures close activity is included in sell aggregates.
- Not changed: entry scoring, order execution, TP/SL thresholds, sizing, leverage, Kraken reconciliation, or the fast-exit 10-second cadence.

## 2026-07-12-spot-live-balance-drift-cost-basis

- Fixed spot live Kraken reconciliation when an existing tracked position's exchange quantity changes outside the bot or after missed/partial live fills. Previously the worker updated `Quantity` to Kraken balance but left the old `EntryNotionalEur`, so a larger synced balance could later be sold against a too-small cost basis and show impossible realized P&L (observed `BILL/EUR` +393%).
- Quantity drift now rebases cost basis: increases add the extra quantity at the current market basis price, decreases reduce cost basis proportionally, and average `EntryPrice` is recomputed from the synced quantity/notional.
- Saved ATR exit levels, peak P&L, and round-trip cost estimate are cleared after a quantity rebasis so old TP/SL/trailing state from the pre-sync position cannot trigger exits for a materially different exchange balance. Fixed-percent exits still apply.

## 2026-07-12-futures-fast-exit-check

- Futures workers now run a fast held-position exit check between full decision cycles. The full futures decision loop remains at `Worker.LoopIntervalSeconds=120`, but open positions are checked every `Futures.FastExitCheckSeconds=10` seconds for TP/SL and max-hold exits.
- The fast path is reduce-only: it loads current portfolio state, fetches light/mark quotes only for held instruments, reconciles live Kraken Futures positions when live trading is enabled, and can only close existing exposure. It never evaluates new entries or strategy reversals.
- Deploy writes `TRADINGBOT_FUTURES_FAST_EXIT_CHECK_SECONDS=10` for futures live and virtual envs so preserved operator-owned live env files do not keep the old full-cycle-only exit cadence.
- Not changed: entry scoring, active universe selection, leverage, sizing, margin/funding/risk gates, and full-cycle signal reversal handling.

## 2026-07-11-futures-leverage-ceiling-10x

- Raised the futures leverage ceiling from 2x to 10x. `FuturesBotConfiguration.Normalize` previously hard-clamped `MaxLeverage` to [1, 2] (blueprint safety default), so any configured value above 2 was silently ignored; the clamp ceiling is now 10. Values above 10 still clamp to 10 (treated as a typo), and the per-symbol Kraken leverage preference plus the liquidation-distance gate still apply on top.
- appsettings now runs futures at 10x: `Futures.MaxLeverage=10`, `Futures.DefaultLeverage=10`, `TargetNotionalEur=10` (≈10 USD notional per the accepted USD-as-EUR behavior → ~1 USD margin at 10x).
- Deploy upgrades the operator-owned futures live env to `TRADINGBOT_FUTURES_MAX_LEVERAGE=10` and `TRADINGBOT_FUTURES_DEFAULT_LEVERAGE=10`, so existing live servers do not stay pinned to the old 2x env.
- Lowered `Margin.MinLiquidationDistancePercent` from 15 to 8. At 10x the liquidation distance is ~9.5% (1/leverage minus maintenance), so the old 15% floor would have rejected every 10x entry with `liquidation distance below minimum`; 8% leaves the gate active while permitting 10x.
- Risk note: at 10x the liquidation is ~9.5% from entry, so the 2xATR stop must clear before liquidation — high-ATR pairs are dangerous at this leverage, and a worker outage between cycles has only ~9.5% of adverse room. Margin-utilization cap (50%) and all other gates are unchanged.

## 2026-07-11-futures-live-leverage-and-size-fix

- Fixed live futures leverage: Kraken Futures leverage is a per-symbol margin preference, not an order field, and the worker never set it — so a live position inherited the exchange/account default (e.g. 10x on DYDX) and posted a fraction of the intended margin (observed: 4.94 USD notional at 0.494 USD margin = 10x instead of the configured 2x). `KrakenFuturesBroker.SetLeveragePreferenceAsync` now PUTs `/derivatives/api/v3/leveragepreferences` (symbol + maxLeverage) and the worker calls it, clamped to `Futures.MaxLeverage`, BEFORE every live entry; if it fails the entry is refused (`LIVE_LEVERAGE_SET_FAILED`) rather than opened at an unknown leverage.
- Fixed live futures order size: live entries were sized from `entryPlan.FilledNotionalEur`, which is the dry-run maker-fill SIMULATION (it returns exactly half the target when the simulated queue is in the partial band). A live `mkt` order is a taker that fills in full, so it is now sized from the real `Futures.TargetNotionalEur`. Combined with the leverage fix, a 10-notional / 2x config now opens ~10 USD notional at 2x margin instead of ~5 USD at 10x.
- The virtual ledger for a live entry now records the ACTUAL filled notional and the leverage set on the exchange, so virtual state mirrors the real position.
- Known/accepted: the target notional is still treated as USD for USD-quoted perps (no EUR->USD conversion) — deliberately left as-is per operator decision; a "10 EUR" target opens ~10 USD.
- Not changed: dry-run maker-fill simulation for virtual instances, margin/funding/BTC-regime/slot gates, TP/SL, dead-man switch.

## 2026-07-12-spot-live-discovered-balance-sync

- Spot live Kraken reconciliation now imports balances using the current discovered market universe from the cycle snapshot instead of only the static configured `CandidateUniverse`, so newly discovered holdings such as `BILL/EUR` are treated as existing bot-managed positions after portfolio sync.
- UI remains database-backed: Kraken is read by live workers only, then persisted to `portfolio_state`; portfolio/cycle/trade views continue to read the synced state from Postgres.

## 2026-07-12-futures-mover-coverage

- Market-data candle selection now has a separate `TopMoverPairs` quota and ranks strong 24h movers ahead of pure-volume leaders before applying `MaxCandlePairs`, so low-volume futures runners can receive candles/orderbooks instead of being crowded out by BTC/ETH-sized markets.
- Raised market-data defaults to `MaxCandlePairs=100`, `TopVolumePairs=40`, `TopMoverPairs=40`, with normalization allowing up to 120 candle pairs per venue; deploy writes the same values to market-data env so environment overrides do not shrink live coverage back to 40 pairs.
- Futures decision active selection now ranks held positions first, then strong movers with sufficient notional volume, then pure volume; futures default `MaxActiveInstruments` is raised from 20 to 50 while keeping score, spread, BTC-regime, margin, and live-size gates unchanged.
- Deploy upgrades the operator-owned futures live env with `TRADINGBOT_MAX_ACTIVE_INSTRUMENTS=50` and the strong-mover thresholds so the live worker receives the broadened active scan even though live appsettings are preserved across deploys.

## 2026-07-12-futures-live-size-precision

- Futures universe discovery now preserves Kraken Futures `contractValueTradePrecision` as instrument quantity precision in the shared `instrument_registry`; the market-data schema migrates with a nullable `quantity_decimals` column and database-backed workers receive it with their universe.
- Futures live entry execution now truncates order size down to Kraken's allowed quantity precision before `sendorder`, logs raw and adjusted size on rejects, and records the adjusted notional in the local margin ledger when an order is accepted.
- Entries whose requested EUR notional rounds to zero at the venue precision are skipped locally with `LIVE_ORDER_SIZE_TOO_SMALL` instead of repeatedly sending invalid live orders.

## 2026-07-11-futures-live-reject-diagnostics

- Fixed futures live rejected-entry diagnostics so a Kraken Futures order rejection preserves `live futures entry rejected: ...` in the persisted dry-run action instead of being overwritten by the generic flat/no-position message.
- Futures live now logs rejected entry attempts with pair, Kraken symbol, side, size, leverage, and rejection reason, making exchange-side failures distinguishable from strategy `NO_ORDER` decisions.
- No strategy threshold, sizing, risk, TP/SL, or Kraken order acceptance behavior changed; this only makes rejected real order attempts observable and auditable.

## 2026-07-11-centralized-market-data-worker

- Added `TradingBot.MarketDataWorker`: one process polls Kraken spot+futures once, writes shared `instrument_registry`, `market_quotes`, `market_candles`, and `market_orderbooks` to Postgres; candle fetch set is union of top-volume pairs, strong movers, force-include list, and held pairs from all `spot-*` / `futures-*` instances.
- Spot and futures decision workers now support `MarketDataMode=database` via `DatabaseMarketDataSource` + `DatabaseUniverseProvider`; stale shared data falls back to direct Kraken fetch when `TRADINGBOT_MARKET_DATA_FALLBACK_ENABLED=true` (default). Deploy defaults all instances to `database` mode and adds the `market-data-worker` compose service.
- Moved Kraken universe providers and futures public market-data adapter into `TradingBot.Core`; live Kraken reconciliation no longer depends on `MarketDataMode=kraken`.
- Expected effect: Kraken public API load stays O(1) as instance count grows; live/virtual/futures instances consume the same quote/candle snapshot instead of diverging by poll time.

## 2026-07-11-futures-dead-man-switch-cycle-refresh

- Fixed futures live dead-man switch refresh so it runs once at the start of every live cycle after Kraken reconciliation, instead of only when `ApplyOrExecuteLiveAsync` reaches an actual entry/exit order; holding positions no longer lets the 90s Kraken timer expire between 120s worker loops.
- `Normalize` now clamps `DeadManSwitchSeconds` to at least `2 * LoopIntervalSeconds` so configured timeouts cannot be shorter than the worker cadence; default appsettings raised from 90s to 180s (normalized to 240s with the 120s loop).
- Expected effect: Kraken `cancelallordersafter` stays armed during normal futures-live hold cycles and no longer mass-cancels resting orders ~30s before the next loop.

## 2026-07-11-futures-entry-quality-parity

- Futures entries now pass the same market-quality protections the spot worker already had, via a new pure `FuturesEntryQualityGate` evaluated BEFORE the portfolio guards and margin risk manager: entry spread limit (`Strategy.MaxEntrySpreadPercent` 0.15%), anti-lag price-action guard (longs reuse the shared `PriceActionGuard`; shorts get the mirror — a tape still rising by `PriceActionMaxDeclinePercent` blocks a short), price-action warm-up (`RequirePriceActionData`, forced on when `Futures.LiveTradingEnabled` like the spot live rule), and anti-extension (entry more than 0.6% beyond the fast EMA in the trade direction, or after a >2.5% lookback run-up/sell-off in the trade direction, is a chase and is rejected).
- The futures worker now feeds `SnapshotPriceHistory` from every light-state fetch and hydrates it on startup from persisted market snapshots (45 min window), so `SignalScorer` receives the price-action assessment (mild-pullback-tolerant negative-PA penalty included) and cycle diagnostics report a real `PriceActionReadyCount` instead of 0. Decision records now persist `priceActionDirection` / `priceActionTrendPercent` for futures like they do for spot.
- No change to margin/funding/BTC-regime/slot gates, TP/SL, leverage caps, or the live execution path; the quality gate only refuses entries whose microstructure or recent tape cannot pay the entry costs.
- Expected effect: futures instances stop opening into wide books, fading tapes, and extended prices; rejects are visible in `risk_reasons` as `entry quality gate: ...` lines.

## 2026-07-11-futures-auth-signing-path-fix

- Fixed Kraken Futures live private-API signing to use the documented authentication endpoint path (`/api/v3/...`) while still calling the `/derivatives/api/v3/...` URL, allowing live account/position sync and live orders to authenticate correctly.
- Added a regression test that verifies live futures order requests sign the `/api/v3/sendorder` path and still send reduce-only flags for exits.
- Position reconciliation behavior is unchanged: live futures imports exchange open positions before decisioning and then manages those positions as bot-owned exposure.

## 2026-07-11-futures-live-execution-gate

- Added an explicitly gated Kraken Futures live execution path behind `TRADINGBOT_FUTURES_LIVE_TRADING_ENABLED=true`; without that flag and configured Futures API keys the futures worker remains fail-closed/dry-run.
- Implemented minimal Kraken Futures private REST support for v3 auth, account/position reads, dead-man switch refresh, and market entry/exit orders; futures exits are sent reduce-only and accepted live orders are marked with `FillSource=REAL`.
- Futures live cycles now reconcile the local portfolio mirror from Kraken Futures accounts/open positions before decisioning, so live position and available-margin state is refreshed from the exchange similarly to spot reconciliation.
- Set futures defaults for requested live sizing to `TargetNotionalEur=10`, `MaxLeverage=2`, and `DefaultLeverage=2`; deploy writes separate futures live flags/API keys and keeps virtual futures forced non-live.

## 2026-07-10-spot-softer-exploratory-price-action

- Lowered the spot exploratory-entry price-action trend default from `0.50%` to `0.25%` so near-threshold candidates with confirmed positive recent movement are not rejected after the 30-second snapshot cadence shortened the effective price-action window.
- The exploratory path still requires a positive snapshot trend, the stricter exploratory spread cap, a top-`ExploratoryMaxRank` ranking slot, and normal risk/cash/exposure approval before any live order.
- Firm entry score threshold, early-entry rules, exit logic, order sizing, and automatic cycle metadata are unchanged.

## 2026-07-10-spot-wider-trailing-stop

- Raised spot trailing-stop defaults from activation `0.8%` / distance `0.5%` to activation `1.5%` / distance `1.0%` so live micro-positions do not exit on sub-fee/spread noise immediately after a small favorable move.
- Based on live `SELL_TRAILING_STOP` diagnostics: recent trailing exits preserved only near-zero realized PnL after real Kraken fills/fees, while one `BCH/EUR` exit sold before a later continuation.
- Stop-loss, take-profit, max-hold, score-decay, entry logic, and automatic cycle metadata are unchanged.

## 2026-07-10-spot-light-snapshot-polling

- Added optional spot-worker light market snapshot polling between full decision cycles via `Worker.MarketSnapshotIntervalSeconds`; the default spot config now polls Kraken light ticker snapshots every 30 seconds while keeping full decision/candle/order cycles at 240 seconds.
- Extra light polls persist `market_snapshots` and feed the rolling price-action guard only; they do not run watchlist selection, full candle fetches, scoring, ranking, risk checks, or orders, so open-position management and new entries remain on the existing full-cycle cadence.
- Re-tuned spot price-action defaults for the 30-second snapshot stream: lookback `6 -> 24` snapshots, minimum warm-up `4 -> 12`, and max non-rising snapshots `3 -> 8`, giving the anti-lag/anti-peak guard a faster ~12-minute recent window instead of relying on sparse 4-minute samples.

## 2026-07-09-kraken-sync-import-missing-positions

- Fixed live Kraken portfolio reconciliation to recognize exchange balance aliases such as `XXDG` for `XDG/EUR`, `XXBT` for `XBT/EUR`, and single- or double-`X` prefixed asset keys.
- Live `MarketDataMode=kraken` cycles now import missing real Kraken spot balances as bot positions when the state file lacks them, so `portfolioBefore` reflects actual held assets before decisions run.
- When a cycle imports missing Kraken positions, the accompanying EUR cash drift is synced but not booked as `ExternalPnlEur`; stale negative `ExternalPnlEur` is reset to 0 after import or after a later cycle confirms Kraken positions with zero current cash drift.

## 2026-07-09-market-buy-default-restored

- Restored spot BUY entries to market execution by default: `Entry.UseMarketBuy` now defaults to `true` in both code defaults and `appsettings.json`.
- The old maker post-only plus IOC fallback path remains available by explicitly setting `Entry.UseMarketBuy=false`.
- Added a live-order regression test that verifies market BUY submits a plain Kraken market order with base-asset volume and no post-only/IOC flags.

## 2026-07-09-external-pnl-cash-drift-only

- Fixed `ExternalPnlEur` computation: it now accumulates only the EUR cash drift (Kraken EUR balance vs bot's CashEur), not the Kraken-total-vs-bot-total difference. The old formula mixed valuation bases — Kraken assets at last price vs bot positions at conservative liquidation value (bid − slippage − taker fee) — so it reported a phantom "external P&L" (~0.5% of open notional) that drifted with prices even with zero external activity.
- Rationale: all spot trades are the bot's own (manual trading is futures-only), and live fills are committed with REAL exchange numbers, so any EUR cash drift is a deposit, withdrawal, or internal transfer — exactly what External P&L should capture.
- Position quantity sync and vanished-position removal are unchanged. Operator note: a previously persisted phantom `externalPnlEur` value should be reset to 0 in the state once this build is deployed.

## 2026-07-09-kraken-portfolio-sync

- Live mode now reconciles the bot's virtual portfolio with real Kraken balances at the start of each cycle (when `LiveTradingEnabled=true` and `MarketDataMode=kraken`). Cash is synced to the real Kraken EUR balance; position quantities are adjusted to match Kraken's reported asset holdings; positions with zero Kraken balance are removed (manual external sell detected).
- Added `ExternalPnlEur` to `PortfolioState` (TradingBot.Core): cumulative P&L from activity not attributable to the bot (manual trades, deposits, withdrawals). Computed each cycle as the difference between Kraken's total portfolio value and the bot's tracked total value.
- Added `ExternalPnlEur` to the `/api/portfolio` response (`PortfolioSummaryDto`) and a fifth "External P&L" metric tile on `dash.html`.
- Removed the old startup-only cash-drift warning from `PrintBrokerStartupAsync`; reconciliation replaces it with per-cycle sync.

## 2026-07-09-entry-market-buy-option

- Added `Entry.UseMarketBuy` config option (default `false`, env var `TRADINGBOT_ENTRY_USE_MARKET_BUY`). When `true`, spot BUY entries use a market order via `AddOrderAsync` instead of the maker post-only + IOC fallback flow. SELL execution is unchanged (already market).
- Added `UseMarketBuy` to `appsettings.json` under `Entry` section (default `false`).
- No behavioral change with the default setting; existing maker-then-IOC flow is preserved.

## 2026-07-08-core-exit-policy-for-simulation

- Extracted public copies of PositionExitPolicy, ExitEvaluation, ScoreDecaySnapshot, PositionExitLevelsSnapshot, ExecutionPolicyOptions, and PositionExitOptions into TradingBot.Core.Risk so the Api simulation engine can use the same tiered exit logic as the real spot bot.
- SpotWorker retains its own internal types unchanged (different namespace, no behavioral change).
- Api simulation for spot now runs the full tiered exit policy: stop-loss, take-profit, trailing stop, conditional max-hold, score-decay defensive exits, post-entry adverse guard, and signal-flip exits with min-hold / min-profit guards. Previously only checked fixed SL/TP levels.
- Api simulation for futures uses frozen SL/TP levels matching TpSlOrchestrator (unchanged behavior, now explicit).
- Added ProjectReference from TradingBot.Api to TradingBot.Core.
- No worker runtime behavior changed; this is a code-sharing extraction only.

## 2026-07-07-spot-ioc-buy-fallback

- Spot live BUY now has a guarded taker fallback after the maker phase. Phase 1 is unchanged in spirit (post-only limit at best bid) but the timeout default drops to `Entry.MakerFillTimeoutSec=25` and the single `Entry.MakerRepegs` now gets a real per-attempt window (the previous code only ever repegged after the whole timeout had already elapsed, so the repeg was effectively dead). Partial maker fills still commit only the exchange-reported filled volume and never fall back for the remainder.
- Phase 2 (IOC): only when the maker phase confirmed `executedVolume==0`, the worker submits one Immediate-Or-Cancel limit BUY at the fresh best ask — never an unrestricted market order. It first reconciles the final maker state (late fill after cancel commits the maker fill and suppresses the IOC; a non-final/ambiguous maker state suppresses the IOC to avoid a double position), re-fetches a fresh Kraken ticker quote, and re-checks execution/risk guards against the current portfolio without re-running scoring/EMA/price-action/regime/ranking/sizing.
- Hard slippage cap `Entry.MaxBuySlippagePercent=0.10` (interpreted as 0.10%, not 10%), referenced to the ORIGINAL maker bid: the IOC is allowed only when `currentAsk <= originalMakerBid * (1 + MaxBuySlippagePercent/100)`. Fallback also re-checks spread vs `Strategy.MaxEntrySpreadPercent`, quote validity, cash reserve, max exposure, max open positions, hourly entries, and Kraken volume/cost minimums. A per-pair in-flight lock backstops duplicate concurrent execution. A repeg is now placed only when the prior maker order is confirmed cancelled with zero fill, closing a double-maker/double-volume race.
- Positions are still created only from real Kraken `vol_exec`/price/fee. Virtual and validate-only BUYs are now tagged `fillSource=MODELED_MAKER_FILL` so a simulated instant-at-bid fill is never mistaken for a confirmed maker fill in cycle analysis.
- New per-entry diagnostics land in each BUY decision record under `entryExecution` (maker order id/price/volume/fee/wait/repegs/final-status; fallback bid/ask/spread/max-allowed-price/submitted-price/order-id/volume/fee/final-status; original-bid-to-fresh-bid movement and original-bid-to-fallback-ask displacement; reconciled final volume/price/fee; and `fillSource` of `MAKER`/`MAKER_PARTIAL`/`IOC_FALLBACK`/`MODELED_MAKER_FILL`/`NONE`). New reason codes for analysis: `LIVE_MAKER_FILLED`, `LIVE_MAKER_PARTIAL_FILLED`, `LIVE_FALLBACK_FILLED`, `LIVE_FALLBACK_REJECTED_{SLIPPAGE,SPREAD,STALE_QUOTE,RISK}`, `LIVE_FALLBACK_ORDER_FAILED`, `LIVE_FALLBACK_SKIPPED_{LATE_MAKER_FILL,UNKNOWN_MAKER_STATE}`.
- SELL execution is unchanged; the futures worker is untouched. `DryRunAction` gains only a nullable `EntryExecution` object (additive, backward compatible — old records deserialize with it null).

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

## 2026-07-11-kraken-auto-universe-discovery

- Spot and futures workers now auto-discover their tradable universe by default from Kraken public reference data instead of relying only on the hand-maintained `CandidateUniverse`.
- Spot discovery reads Kraken `AssetPairs`, includes online EUR spot pairs, keeps configured pairs as force/fallback overrides, and supports blacklist/force-include env overrides.
- Futures discovery reads Kraken Futures instruments, includes tradeable USD perpetual symbols, keeps configured pairs as force/fallback overrides, and supports blacklist/force-include env overrides.
- Light snapshots now run against the discovered universe while full candle/scoring evaluation remains capped by existing active-pair logic (`MaxActiveInstruments`, held-position forcing, regime anchors, and spot strong-mover backfill).
- Kraken spot and futures ticker/reference calls are batched so broad discovered universes do not exceed URL length limits.
- `futures-live` now refuses to run with `TRADINGBOT_FUTURES_LIVE_TRADING_ENABLED=false` instead of recording virtual fills under a live instance id.
- Futures live Kraken reconciliation now maps remote positions against the current discovered universe, not only the static fallback list.
- New env knobs: `TRADINGBOT_UNIVERSE_DISCOVERY_ENABLED`, `TRADINGBOT_UNIVERSE_DISCOVERY_REFRESH_SECONDS`, `TRADINGBOT_UNIVERSE_INCLUDE_CONFIGURED`, `TRADINGBOT_UNIVERSE_FORCE_INCLUDE`, and `TRADINGBOT_UNIVERSE_BLACKLIST`.
- Expected effect: new Kraken listings and fast movers can enter the snapshot/watchlist funnel automatically without manual JSON edits, while full evaluation and live risk limits remain bounded.

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
