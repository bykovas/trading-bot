# PUKO position-management data

This change routes PUKO's **held positions only** through the same DEBYKO source
as BYKO. It does not change EntrySignalSource, entry-rule evaluation, instrument
selection, entry prices, sizing, strategy thresholds or LUKO. No deployment is
included. Existing positions use the new route after an approved deployment;
stored entry ATR, stops and position history are not reset.

## Shared contract

- Core owns `DebykoPositionMarketDataSource` and `PositionCandleWindow`.
- `PositionManagementData.CandlePriceType` is the single candle-type setting,
  default `trade`; optional environment override is
  `TRADINGBOT_POSITION_CANDLE_PRICE_TYPE`. Both instances must retain the same
  value. No deployment configuration override is added by this PR.
- Source is DEBYKO for both: `/v2/candles`, 120 bars, `closed_only=true`, published
  units. Existing BYKO interval selection is preserved: 1m when configured 1m,
  otherwise 15m. `to` is the latest UTC interval boundary; `from` is 120 intervals
  before it. `as_of` is floored to milliseconds. Exact response bounds, type and
  visibility validation remain in the client.
- BYKO's quote source stays DEBYKO `/v2/snapshots`. PUKO's mark/last/bid/ask,
  including fast-exit checks, retain their existing shared-data/fallback source.
  Only candle inputs change; candle type does not change TP/SL trigger prices.
- PUKO's non-held candidates retain the existing source. Closing a position
  removes it from the management route. There is no database/mark fallback when
  a held-position DEBYKO request fails; existing error handling applies.
- Entry DQL evaluation requests remain `trade`, even if the management setting
  is changed later. Changing to `mark` is not part of this rollout; API support
  and response availability must be checked before doing that.
- PUKO adds one DEBYKO candle request per held instrument per full cycle
  (currently every 120 seconds). Fast quote checks remain unchanged. This is
  additional API load, not a performance fix.

## Verification scope

Offline tests drive BYKO's direct management source and PUKO's held-only route
through a recording HTTP handler. They verify identical candle slices, trade
requests, 120 bars, closed 1m/15m boundary cases, millisecond precision, no fallback
on HTTP failure, unchanged quote/candidate routing and routing after a position closes.

Replay tests compare EMA/RSI/ATR, reversal decisions, TP/SL triggers and trailing
state at every step for long and short positions. These hold position state,
policy parameters and the execution model constant to isolate data parity. They
are not a claim that live Kraken execution and a paper simulator are identical.
Existing broker/protection tests cover the exchange submission path separately.

## Remaining differences (read-only audit, 2026-10-17)

Production profile values plus instance overrides were inspected. Effective
common settings include EMA 9/21, RSI 14, working TP/SL 4%/2%, trailing activation
1.5R, trailing distance 1.5 ATR and max-hold 360 minutes. Raw profile rows differ;
the instance overrides must be included when comparing them.

1. **Position state and fills:** BYKO reconciles actual Kraken positions and
   orders; PUKO simulates them. Entry timestamps, rounded quantities, entry prices,
   entry ATR, stored risk distances, peak history, order acceptance and fills may
   differ. Current holdings already differ, so this change cannot align their P&L.
2. **Quotes and observation time:** BYKO gets DEBYKO snapshots; PUKO keeps shared
   Kraken quotes with its existing fallback. Candle requests occur independently:
   they share a source and rounding rule but not an atomic `as_of` snapshot.
   A boundary crossing or a newly received candle can still produce different
   observations. BYKO also receives Kraken mark/account/order data during live
   reconciliation. This PR does not synchronize clocks or change reconciliation.
3. **Price-action history:** BYKO skips persisted snapshot hydration; PUKO hydrates
   it. Their in-memory sample histories can differ and feed signal scoring even
   with equal candles. PUKO's previously persisted mark-based samples are not
   deleted. This PR does not change scoring/hydration behavior.
4. **Trailing activation input:** the live regime-trail path checks its supplied
   mark price; paper trailing checks the closeable bid (long) or ask (short).
   Spreads can therefore shift activation even when the quote payload is equal.
5. **Trailing lifecycle:** `LongShortStrategy.DecideHeld` suppresses reversal
   exits for `EXCHANGE_OPEN` trailing, but not `SIMULATED_OPEN`. The live exchange
   tracks trailing continuously; paper checks sample prices and maintain their
   own peak. These are existing behavioral differences, deliberately unchanged.
6. **TP handoff / backup orders:** a bot-owned live working-TP hit can activate an
   exchange trailing stop; paper TP produces a close. Exchange backup protection
   uses the configured 200% protection multiplier, unlike the local working
   levels. The two order lifecycles and trigger sources must not be conflated.
7. **Capital, costs and execution metadata:** nominal position budgets differ
   (approximately $50 BYKO versus $500 PUKO). BYKO uses actual account/fill data;
   PUKO uses assumed public fee tiers and simulated fills. Funding, slippage,
   exchange precision/limits and external/adopted position flags also differ.
   These affect amounts and persisted state; none are changed here.

## Separate reference: Kraken trigger price

The production effective `TpSl.TriggerSource` is `mark` for both instances. BYKO
passes this setting to `SendTriggerOrderAsync` for `stp` and `take_profit`, and to
`SendTrailingStopOrderAsync`. `KrakenFuturesBroker` serializes it as
`triggerSignal=mark` on `/derivatives/api/v3/sendorder`, with `reduceOnly=true`.
This establishes what BYKO submits; no private exchange orders were read or
modified during this task.

Do not infer the exchange trigger from the indicator candle type: indicator
candles are currently trade, exchange protection triggers are mark, while local
working protection prefers bid/ask when available. Choosing mark candles would
not by itself remove the other differences listed above.
