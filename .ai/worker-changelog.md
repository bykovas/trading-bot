## 2026-08-23-legend-on-one-line-and-a-key-behind-a-question-mark

- The legend is one pill with four groups and hairlines between them, not two framed rows. Two frames were the right grouping and the wrong object: stacked above the chart they read as two boxes arguing with the thing they describe. One object with internal divisions reads as a caption.
- The owner names that headed the rows, `BlynAI:` and `Rankiniai:`, are gone from the line. `BlynAI` stays on the first group; the blue already says a person moved that money, and the full account of who owns what is now a sentence away.
- Squares first, then what they mean - every group, the same way the dialog rows read. `BlynAI` was standing in front of its squares while the other three groups stood behind theirs, and one group reading the other way was the only thing to notice about the line. The name is now part of its group's label, `[up][down] BlynAI istorija`, joined by a word space rather than the wider gap that separates a swatch from its words.
- A `?` at the end of the pill opens a dialog naming every square in plain Lithuanian - what it is, why it has a direction or does not, and why a transfer is kept out of the result. Same native `<dialog>` and `initModal` as the disclaimer, so Esc, the backdrop and focus return come for free. Its hit area reaches past the pill, because a 26px bar cannot give a thumb 44px and the glyph should not have to grow to 44 either.
- The divider is a border on each group rather than a span between them. A separate span would leave a doubled hairline wherever `startas` is absent - which is any account funded before it was listed.
- "No data" moved inside the BlynAI group, next to the up and down squares. It is an outcome of a day, not a category of its own, and as a sixth segment it was the difference between fitting a 375px phone and not.
- On a phone three of the seven words go rather than shorten into stubs: `istorija` is what the name and a pair of arrows already say, and the grey square is the one entry nobody reads in a hurry. Measured 324 of 343 available, 337 in the rare state where a missing day is also on the chart.
- The headline block now gives up width before the legend does. One line is wider than the two rows it replaced, and left to their content widths the two of them wrapped the legend under the number on a 1280px screen; a 360px basis lets the sentence under the number take a second line instead. Scoped to the wide layout: the narrow one runs the same flex in a column, where that basis is 360px of empty height under the number - which is exactly what it drew before the rule was moved.
- Page only. No worker behaviour touched.

## 2026-08-23-legend-frames-and-the-base-under-the-first-candle

- Legend regrouped into two named frames, one per row: `BlynAI: istorija · šiandien` and `Rankiniai: įnešimai · startas`. What the bot does now sits under the bot's name and what a hand does sits apart, so the grouping reads before the labels do. Squeezed onto one line the two frames left no margin at all - 337 of 337 on a 375px phone - and the words had to be cut to fit; stacked, they keep the words that say what they mean.
- The name carries the topbar's split wherever it appears in text, "Blyn" in cream and "AI" in gold, at whatever size it sits in.
- `startas` now means everything the account held when the series starts, drawn from zero as the base the first candle stands on - not just transfers that landed shortly before the first cycle. futures-live was funded long before the launch date, so it had no such transfers and its first candle floated at 51.57 with nothing underneath while the legend still offered to explain a bar that was not there. Whether the money arrived minutes or months before makes no difference to a reader: it is what there was.
- It keeps its own darker blue and its own name, so it never reads as a deposit; the headline names the same figure, `(+ 51,57 $ pradinio kapitalo)`.
- The entry hides itself on an account that genuinely has no base to draw, the way the missing-data entry already does.
- Page only. No worker behaviour touched.

## 2026-08-23-outlier-band-deleted-a-real-deposit

- The daily rollup keeps only values within a third and triple of the day's median, to stop one nonsense cycle setting a high or a low. It cannot tell a nonsense cycle from a real jump: 562 USD arrived on futures-live at 22:16 local on 2026-08-22, the day's median was about 40, and every cycle after the deposit sat above median x 3 and was thrown away. The day closed at 38.35 as though the money had never come, and its observed window ended at 22:14 - two minutes before the transfer.
- That second effect is what reached the page. Movements are attributed to a day only when they land inside its observed window, so the transfer fell outside the truncated window, was never counted as manual, and the next day opened from 38.35 against a live 600: the bot was credited with +561.71, "+1 464,61 % šiandien".
- The band is now widened by the money that actually moved that day, from `portfolio_cash_events`. Noise is still caught - a 704 USD reading on an account around 52 with no transfers is as far outside as it ever was - while a deposit no longer deletes the balance it created. Rollup revision bumped so stored days rebuild; 08-19 through 08-21 were checked to come back byte-identical before shipping.
- The same band guards the drawdown scan and inherits the fix.
- Page and API only; no worker behaviour touched.

## 2026-08-22-hypothetical-scales-from-capital-used

- The "if only we had known" card multiplied the whole portfolio by ten: 60 dollars became 600 and the best day's 18.54 became 185. That credited the result to money that was never in a position. On that day the bot held at most 44.98 of margin, and the 18.54 belongs to those 44.98.
- It now answers a question that has an answer: the same day traded with 500 dollars of capital. Straight ratio, because position size is proportional to margin and the result is proportional to position size - 18.54 / 44.98 x 500 = 206. futures-lukas-live reads `500 $ → 706 $`, futures-live `500 $ → 582 $` off its own best day.
- 500 is fixed rather than tied to the live balance, so the card says the same thing tomorrow as today and is not quietly restated every time money moves.
- Losing best day keeps its own wording: a bigger stake on a day that lost is not a missed opportunity.
- Page only. No worker behaviour touched.

## 2026-08-22-chart-legend-and-starting-capital

- The chart's first blue bar was the starting capital and the first day's transfers in one body, while the headline counted only the transfers - the page showed 62.44 drawn and 2.44 written for futures-lukas-live, both correct answers to different questions. The capital now has its own darker blue and its own legend entry, and the headline names it: `rankiniai koregavimai +2,44 $ (+ 60,00 $ pradinio kapitalo)`.
- `Per N dienas botas` is measured against the live total, which already holds anything moved today, so subtracting only the closed days' adjustments credited the bot with a deposit the moment it landed: futures-live read `+534,36 botas (+1 036,2 %)` an hour after 560 dollars went in. Today's transfers are subtracted now too.
- Legend entries renamed and cut to fit four on one row of a 375px phone: `Algo`, `Įnašai`, `Šiandien`, `Startas`. Spelled out the four need 358px against 343 available, so the third keeps its long form `Algo šiandien` where there is room and shortens on a phone - on a desktop the entry says which candle it names, on a phone the gold already says it.
- Page only. No worker behaviour touched.

## 2026-08-22-futures-live-stakes-ten-times

- futures-live trades 150 USD of margin per position instead of 15. Leverage stays 10x and the position count stays 3, so a full book is 450 USD of margin against 1500 USD of notional per position. futures-lukas-live is unchanged at 15.
- Every money knob moved together, in both sections that hold them: `TargetMarginUsd` and `MaxMarginPerPositionUsd` 15 to 150, `MaxNotionalUsd` 150 to 1500, `MaxTotalNotionalUsd` 450 to 4500, `Risk.TargetRiskUsd` 4.5 to 45 and `Risk.MaxConcurrentOpenRiskUsd` 13.5 to 135. Raising the margin caps alone would have left the risk-based sizer working to the old budget and quietly ignoring the new limit.
- Which binds is unchanged: at a 2% stop the risk budget wants 2250 USD of notional and the notional cap holds it to 1500, exactly as 45 was held to 150 before. The account behaves the same, ten times larger.
- The guard test's allow-list now spans both sections. It listed only the `Futures` keys, so the first real resize failed the build on `Risk.TargetRiskUsd` - the two are one setting split across two places.
- No change to signal scoring, entry gates, exits, leverage or the mirror.

## 2026-08-22-immediate-ledger-read-on-cash-jump

- Transfers were read from the futures account log every thirty minutes. That is fine for the daily figures but not for the live chart: a 560 USD deposit into futures-live landed at 19:16 and the page drew it as bot profit - "+1179% today" - because the split between the bot's work and a transfer can only be made once the ledger names the movement. The money itself was never missing; the balance had it immediately. What was missing was the record saying whose it was.
- The reconciliation now re-reads the ledger on the spot when available collateral moves by 5 USD or more without a position closing in the same cycle. Cash that moves without a trade to explain it is a transfer by definition, and a jump that size is rare enough that the extra history call costs nothing. The routine half-hourly sync is unchanged.
- Threshold picked to sit above any fee, funding payment or reconciliation rounding the worker produces on its own, and well below any transfer a person would actually make.
- No change to signal scoring, entry gates, sizing, leverage, exits or the mirror.

## 2026-08-22-futures-live-mirror-only

- New `Futures.OwnSignalEntriesEnabled`, off on futures-live. That account now opens nothing from its own signals and takes every entry from the mirror. It was the other way round before: of its 82 entries only 9 came from the mirror and 73 from its own scoring.
- The gate skips a pair the account does not already hold, before any indicator or risk work is done on it. Held pairs still fall all the way through to the exit logic - trailing, stop loss, max hold, score decay all keep working. An account that could not close what it opened would be a trap, and gating entries at the candidate stage rather than at the order stage keeps that impossible by construction.
- Visible in two places: the startup line now reads `ownSignalEntries=off (mirror only)`, and the per-cycle decision count drops from about seventy to however many pairs are held, because the account genuinely stops considering the rest.
- The staking knobs are now allowed to differ between the two accounts - `TargetMarginUsd`, `MaxMarginPerPositionUsd`, `MaxNotionalUsd`, `MaxTotalNotionalUsd`, `MaxPositions`, `MaxConcurrentOpenRiskUsd` - since the point of a mirror on a separate account is to size the same trade differently. Everything else must still match: exits, entry gates, leverage, cooldowns, blackout. The guard test enforces exactly that split, so a real drift still fails the build while a deliberate sizing change does not.
- No change to signal scoring, exits, risk caps or the mirror protocol itself.

## 2026-08-22-end-the-flip-experiment

- futures-live stops inverting. Two independent settings did it and both are now off: `Futures.FlipLongEntries`, which turned its own approved LONG signals into SHORTs, and `EntryMirror.InvertSide`, which flipped the entries mirrored from futures-lukas-live. Turning off only the first would have left the mirror inverted, which is most of what that account trades - 8 of its 9 mirrored entries were SHORT.
- `InvertSide` had to change on both accounts, not just the follower. The publisher uses it to decide the side it writes into the command and the follower uses it to decide the side it expects; had they disagreed, every command would have been rejected with `MIRROR_SIDE_MISMATCH` and the mirror would have gone quiet rather than loud.
- Checked what else had drifted while the two accounts were run differently: of 175 configuration keys they differed in five, and four of those are identity and mirror role. Take profit, stop loss, trailing distance, leverage, position count, margin per position, entry gates, cooldowns and the blackout window were already identical, on both the repository files and the deployed copies, with environment overrides carrying nothing but the instance id.
- Positions already open keep the behaviour they were opened with. futures-live is holding an XBT/USD LONG opened through the mirror against lukas's SHORT on the same pair four seconds earlier; it stays flagged as a flipped entry and exits on the flipped levels, 1.5% take profit and 0.75% trailing. New entries follow lukas directly.
- No change to signal scoring, sizing, risk caps or exits.

## 2026-08-22-trailing-stop-075

- `TpSl.TrailingStopPercent` goes from 2% to 0.75% on both live futures accounts. `FlippedTrailingStopPercent` was already 0.75, so on futures-live this only brings the non-flipped entries into line with what its flipped ones have been doing since the flip experiment started; on futures-lukas-live, where `FlipLongEntries` is false, it changes every entry.
- Measured on 162 965 hypothetical entries across 316 perps (research database, 15m candles from 2025-01-01, arm at +4%, SL 2%). The exit equals `peak x (1 - trail)`, so trailing only beats the fixed target above a peak of `(1 + tp) / (1 - trail)`: +6.12% at a 2% trail, +4.79% at 0.75%. The floor once armed rises from +1.92% to +3.22%, so the worst case costs 0.78 percentage points against the target instead of 2.08.
- Edge per entry, in price: 2% gave +0.168%, 0.75% gives +0.258%, and it is monotonic across 0.5%-3% in both directions. Long and short agree.
- The reason to narrow it is not only the mean. At a 2% trail the advantage is carried by a minority of long runs and is invisible at our trade count: after 5 armed trades it is ahead 56.5% of the time with a 5th percentile of -8.90 USD, and it takes about 100 trades for that percentile to clear zero. At 0.75% the per-trade loss is capped at 0.78 points while the upside is not, so the spread collapses - 10 trades put the 5th percentile in profit. With `MaxPositions` 3 and roughly one close a day, that difference decides whether the setting can ever be judged from live results.
- Known limit of the measurement: 15m candles cannot see a sub-percent dip that happens and recovers inside one candle, so the model flatters tight trails, and the finer the trail the larger the error. 0.75% was chosen over the 0.5% the data prefers for exactly that reason - it keeps most of the improvement while staying further from the granularity floor. Validation on 1m data for a subset of pairs is still outstanding.
- No change to signal scoring, entry gates, sizing, leverage or the mirror. Take profit stays 4%, stop loss 2%, exchange protection 200% of both.

## 2026-08-21-exchange-close-realised-result

- A close performed by the exchange was reported on the page as "realised 0.00". The dashboard builds a trade's result from the difference between `portfolio_value_before_eur` and `portfolio_value_after_eur` on the action row, not from the reason text, and the exchange-closure recorder left both at zero: it deliberately does not route through `FuturesVirtualPortfolio.Apply`/`Close`, so nothing ever set them. On futures-lukas-live the +3.59 trailing-stop close on HBAR/USD at 12:03 on 2026-08-21 made the whole day read 0.00 realised.
- Both recorders now set the pair so the difference is exactly the realised figure Kraken reported on the fill. The live one bases it on the portfolio before the close, which it already has; the backfill one leaves the absolutes at zero, because the only total available at repair time belongs to today rather than to the day the closure happened, and a plausible wrong number is worse than none.
- The reason text also gains the realised percentage in the same shape `FuturesVirtualPortfolio` writes it, `realized PnL USD 3.5934 (2.4324%)`, because the page reads that back with a regex. Without it an exchange close showed no percentage while a bot close did.
- Percentage is the price move signed for the side, identical to the bot's own close, so both kinds of exit read the same way.
- No change to signal scoring, entry gates, sizing, leverage, exits, execution or the mirror. Journal fields only.

## 2026-08-21-unsettled-valuations

- A cycle in which a position leaves the account records a total the account never held. Cash and open positions come from two Kraken reads that settle independently: the position is already gone from one while its proceeds have not yet arrived in the other. Lukas read 74.39 at 12:03 on 2026-08-21 and 92.91 two minutes later with no trade in between, and that single reading was being reported on the page as a -22.6% max drawdown.
- `ReconcileWithKrakenAsync` now returns whether a position vanished this cycle, and the cycle is journalled with `valuation_unsettled`. The cycle is still written in full - decisions, actions, everything - it is only kept out of the equity series and the drawdown scan. The flag also covers the other shape of the same fault: on 2026-08-19 at 18:28 a freshly opened position dropped out of Kraken's position read for exactly one cycle, showing 69.07 between two readings of ~80 with cash unchanged at 14.77.
- Cost is one equity sample per close, out of roughly 490 a day. Trading is untouched: the worker still sizes and decides on the same reconciled state it always did, which stays conservative while the credit is in flight.
- Max drawdown was also being measured over 30 days while the rest of the page starts at 2026-08-19, so futures-live reported -65.1% off a peak the chart never draws. It now starts at the same date. With both fixes the figures become -20.7% for futures-live and -7.5% for futures-lukas-live, computed against production data before shipping.
- Five readings already in the database predate the flag (futures-live 07-29 03:17, 08-17 21:39, 08-17 22:37; futures-lukas-live 08-19 18:28, 08-21 12:03). They need a one-off update to be marked; until then they remain in the series.
- No change to signal scoring, entry gates, sizing, leverage, exits, execution or the mirror.

## 2026-08-21-backfill-dedupe-by-pair-and-time

- Fixes a defect in yesterday's repair that I found only after running it on production. It skipped a fill when the fill's `order_id` was already in the journal, which is exactly wrong for the closes the bot performs itself: those are written by `FuturesVirtualPortfolio` and carry no `exchange_order_id` at all, so the guard matched none of them and recorded every one a second time. The first run added 29 entries on futures-live and 15 on futures-lukas-live, of which 18 were duplicates of closes already there - e.g. `HBAR/USD 08-20 21:10` appearing as both `SELL_STOP_LOSS -3.274` and `EXCHANGE_CLOSE_BACKFILLED -3.262`.
- The guard now also skips a fill when a `WOULD_CLOSE` already sits on the same pair within 15 minutes of the fill time, whoever wrote it, via the new `IDryRunPortfolioStore.LoadRecordedCloseTimes`. Fifteen minutes is the cycle period: two genuine closes on one pair inside a single cycle are not reachable, since a pair holds at most one position and reopening requires the next cycle.
- The 18 duplicate cycles were deleted from production - `dry_run_cycles` and its dependants - leaving futures-live at 72 closes and futures-lukas-live at 15, of which 13 each are genuine recoveries. No orphan rows remain in `dry_run_actions`, `dry_run_decision_facts` or `dry_run_cycle_facts`.
- No change to signal scoring, entry gates, sizing, leverage, exits, execution or the mirror. The repair still only appends journal rows.
- Follow-up found on production: the repair's cycle rows are not observations of the account. Each is stamped with the fill's own time but carries the portfolio as it stood when the repair ran, so 13 rows on futures-live all read 49.49 and 13 on futures-lukas-live all read 94.70, back-dated across three days. Two of them sat below futures-live's real 08-20 low. The equity rollup and the drawdown scan now skip `-backfill` cycles; every already-stored day rebuilds byte-identical, checked against 08-17 through 08-20 on both accounts.

## 2026-08-21-backfill-missed-closures

- New one-shot repair, off unless `TRADINGBOT_FUTURES_BACKFILL_CLOSURE_DAYS` is set to a number of days. On startup the worker walks Kraken's fills over that window and writes journal entries for closures it never recorded, then continues normally. Running it twice is harmless: order ids already present in the journal are skipped, via the new `IDryRunPortfolioStore.LoadRecordedExchangeOrderIds`.
- Backfilled entries are stamped with the fill's own time, not the moment of the repair, so they land on the day they happened and the daily figures they exist to fix actually change.
- They deliberately do not claim which protection fired. The live path identifies a trailing stop exactly by matching the fill's order id against the one stored on the position, but for history the position is long gone and that id no longer exists anywhere. These carry the real price, time and realized PnL under a plain `EXCHANGE_CLOSE_BACKFILLED` reason rather than a guess.
- Known gap on futures-lukas-live before the repair: a +3.59 close on 2026-08-21 and a +4.88 close on 2026-08-20, neither in the journal.
- No change to signal scoring, entry gates, sizing, leverage, exits, execution or the mirror. The repair only appends journal rows; it never touches portfolio state.

## 2026-08-21-record-exchange-closures

- The reconciliation now notices positions that left the account without the worker closing them and writes a `WOULD_CLOSE` journal entry for each, built from Kraken's own fills: real fill price, real `realized_pnl`, real fill time, and the closing order id.
- Attribution: a trailing stop is identified exactly, by matching the fill's `order_id` against the stored `trailing_stop_order_id`; `fillType` containing "liquidation" is reported as such; otherwise take profit and stop loss are told apart by which level the fill price landed nearer, their levels being 4% and 2% from entry. Reasons are prefixed `EXCHANGE_` so a close the exchange performed is never confused with one the bot decided.
- Verified against live data before shipping: on futures-lukas-live the fill `PF_HBARUSD sell 0.07579 pnl=+3.59341 order=a28de0e3` matches the trailing order id the worker logged when it armed the stop, so the identification works on a real closure rather than only in theory.
- Deliberately does not route through `FuturesVirtualPortfolio.Apply`/`Close`. Those derive a fill price from a slippage model and move `state.CashEur`, but the real price is known here and cash has already been rebuilt from Kraken a few lines above; reusing them would replace a real number with a modelled one and count the money twice. The portfolio is left untouched - only the journal gains the entry.
- Failure to read fills is logged and the cycle continues; the closure stays unrecorded rather than blocking trading.
- No change to signal scoring, entry gates, sizing, leverage, exits, execution or the mirror.

## 2026-08-21-futures-fills-reader

- `IFuturesBroker` gains `GetFillsAsync(sinceUtc)` and `KrakenFuturesBroker` implements it against `/derivatives/api/v3/fills`, walked newest-first in pages of 100 via `lastFillTime` until the window is covered - the same paging shape as the account-log reader, and for the same reason.
- Why: a position closed by a protection order never passes through this worker. On futures-lukas-live the trailing stop armed at 11:54 on 2026-08-21 and closed PF_HBARUSD around 12:02 for roughly +3.5 USD; the bot only saw `remotePositions=0`, dropped the position from its state and recorded nothing, so the day read `opened 1, closed 0, realised 0.00` while the account had actually gained. Fills carry `order_id`, `price`, `fillTime` and `realized_pnl` on position-closing fills, which is everything the journal was missing.
- Nothing calls the new method yet: this commit is additive only and cannot change trading behaviour. The consumer is the next step, and it must build the close record without routing through `FuturesVirtualPortfolio.Apply`/`Close` - those derive a fill price from a slippage model and move `state.CashEur`, but the reconciliation has already rebuilt cash from Kraken, so reusing them would replace a real fill price with a modelled one and count the money twice.
- Which protection fired is identified by matching the fill's `order_id` against the stored `trailing_stop_order_id`; take profit and stop loss are told apart by fill price, since their levels sit 4% and 2% from entry and the order ids are not persisted.
- No change to signal scoring, entry gates, sizing, leverage, exits, execution or the mirror.

## 2026-08-21-batch-market-data-writes

- `UpsertCandles` in `PostgresMarketDataStore` now streams the batch into a temp table with binary COPY and merges it with one upsert, inside a single transaction. It previously issued one insert per candle: ~11k statements per futures sweep, each parsed separately and each its own implicit transaction with its own WAL flush. Measured at roughly 3.5ms a row, that was ~39 of the 55 seconds a sweep took.
- The HTTP calls the sweep was blamed for account for about 14 seconds: probed from the production host itself, Kraken Futures answers candles in 74ms and the order book in 77ms, and the sweep makes 93 x 2 of them. The bottleneck was never the exchange.
- The merge also skips rows whose values have not moved (`where ... is distinct from ...`). A closed candle never changes, but the sweep refetches the full window every run, so the old unconditional `do update` rewrote every row it touched - a new row version and a dead tuple each time, for data identical to what was already stored.
- `UpsertQuotes`, `UpsertOrderBooks` and `UpsertInstruments` keep their per-row statements but now run inside one transaction each, which removes the per-statement flush from those paths too.
- No change to signal scoring, entry gates, sizing, leverage, exits, execution or the mirror. Consumers read the same tables with the same shape; only the write path changed.

## 2026-08-21-freeze-spot-collection

- The market data collector gains per-venue switches, `TRADINGBOT_MARKET_DATA_SPOT_ENABLED` and `TRADINGBOT_MARKET_DATA_FUTURES_ENABLED` (both default true), and `deploy.sh` sets spot to false. Spot light quotes and candles are no longer fetched; the rows already in `market_quotes`, `market_candles` and `market_snapshots` stay untouched and simply stop being refreshed.
- Reason: nothing trades spot. `spot-worker-live` never placed a real order in 53,535 recorded actions and both spot workers are now retired, yet the spot candle sweep was the single slowest thing on the box - 71 pairs at two calls each, 204s after the `OhlcDelay` fix and 419s before it, against a 120s schedule. It never finished before the next one started, so it ran continuously and starved the light-quote step that the futures workers depend on.
- Effect on the venue that matters: futures keeps its full sweep, and the per-IP Kraken budget it shares stops being spent on a frozen venue. Quote freshness had already recovered from 444s to 7s after the delay change; this removes the remaining competitor.
- No change to signal scoring, entry gates, sizing, leverage, exits, execution or the mirror. Re-enabling spot is one environment variable.

## 2026-08-21-market-data-throughput-and-paper-workers

- `OhlcDelay` in `KrakenMarketDataSource` drops from 500ms to 20ms. The old value was derived from Kraken's documented ~1 req/s and sized in its own comment for "a 22-pair active set"; the live sets are now 75 spot and 94 futures pairs, so the pause alone accounted for ~169s of every sweep. Measured against the live API from an unrelated address: 150 back-to-back calls (OHLC + Depth across 75 pairs) finished in 15s with zero rate-limit responses, and 40 consecutive calls on a single pair (~21/s) were clean too. `RateLimitBackoffs` is unchanged and still absorbs a 429.
- Consequence for the stack: the spot candle sweep was taking 144-420s against a 120s schedule, so it ran continuously and starved the light-quote step. `market_quotes` therefore sat minutes stale, every decision worker tripped its staleness gate and fell back to fetching Kraken directly, and six processes competed for one IP's budget - which kept the sweep slow. This breaks that loop.
- `spot-worker-live`, `spot-worker-virtual` and `futures-worker-virtual` move behind a `paper` compose profile that nothing enables, and `deploy.sh` removes their containers and no longer health-checks them. None has ever placed a real order - `exchange_order_id` is null across all 53,535 / 52,581 / 1,158,911 recorded actions and live trading is off for each - while each still polled Kraken and wrote its own duplicate copy of every market snapshot.
- No change to signal scoring, entry gates, sizing, leverage, TP/SL/trailing, execution or the mirror. The two workers that handle money, `futures-worker-live` and `futures-worker-lukas-live`, are untouched and still deployed and health-checked.

## 2026-08-20-futures-wallet-diagnostics

- Kraken returns every wallet on the futures account (11 on the live accounts) and `SumFuturesAvailableCollateralUsd` adds up `availableMargin` across all of them, so the portfolio value is an all-wallet total.
- That hides internal moves: on futures-live 2026-08-19 two real `Transfer to futures` entries of 10.61 and 3.88 USD took the futures wallet 49.376 -> 59.986 and 56.1066 -> 59.9866, while the all-wallet total moved only 59.73 -> 60.00. The transfer is still written to `portfolio_cash_events` and then subtracted from the bot's result, so the bot is charged for money that only changed pockets.
- `FuturesAccountBalance` now carries Kraken's wallet key (optional field, existing call sites unchanged) and every sync logs one `futures-kraken-wallet:` line per wallet with its currency, margin balance and available margin.
- Diagnostics only. No change to sizing, entries, exits or the collateral sum; the next change will narrow the basis to the futures wallet, and that one WILL change live position sizes.

## 2026-08-20-kraken-ledger-pagination

- Both ledger readers now page through the exchange history instead of taking whatever the first response contained. The futures account log is walked newest-first with `count=500` and a `before` cursor; the spot ledger pages through `ofs`. Both stop at the first short page, once entries fall outside the window, or after a hard page cap.
- Without this, only the first page of the window was ever read. The futures log interleaves money movement with every funding-rate change and fill, so on a busy account that page never reached the present: `futures-live` recorded no deposit or withdrawal after 2026-07-15 even though the account kept moving. `futures-lukas-live` was unaffected only because its log is sparse enough that one page still reached the current day.
- Not changed: signal scoring, entry guards, sizing or leverage, TP/SL/trailing, execution, the mirror flow, dead-man switch, or cash and position reconciliation. The sync remains best-effort and throttled to once per 30 minutes; a ledger outage still logs and lets the cycle continue.

## 2026-08-20-kraken-cash-event-ledger

- Both live workers now read deposits, withdrawals and transfers straight from the exchange ledger and persist them to a new `portfolio_cash_events` table, keyed on the exchange's own entry id so re-reading an overlapping window is idempotent. Spot uses `/0/private/Ledgers` (single call with `type=all`, filtered to the three money-movement kinds); futures uses `/api/history/v3/account-log`, filtered on the `info` field. Kraken retains this history far longer than the bot retains cycles, so the first sync backfills movements that never existed in the database.
- The sync runs inside the existing Kraken reconciliation, throttled to once every 30 minutes over a 45-day lookback window, and is fully wrapped: a ledger outage logs `cash-events: sync failed` and the trading cycle continues untouched. `FileDryRunPortfolioStore` implements the new store method as an explicit no-op because local dry runs have no dashboard behind them.
- This exists because manual money movement cannot be inferred from balances. On `futures-live`, cash between consecutive cycles jumps by 15-45 USD while total portfolio value stays flat, since the exchange releases and re-commits margin outside the bot's action log; treating those gaps as deposits produced fictional 45-55 USD "deposits" on days with none. The cumulative `ExternalPnlEur` drift counter is unchanged and remains spot-only.
- Not changed: signal scoring, entry guards, sizing or leverage, TP/SL/trailing rules, execution or order types, the mirror flow, dead-man switch, reconciliation of cash and positions, or any strategy threshold. The new table is read-only from the worker's perspective after insert and is consumed only by the dashboard API.

## 2026-08-19-deterministic-opposite-account-entry-mirror

- Replaced the two live workers' independent entry decisions with one deterministic entry source: `futures-lukas-live` keeps the normal strategy and publishes each confirmed live fill, while `futures-live` suppresses independent entries and follows the same pair with the opposite side (`LONG -> SHORT`, `SHORT -> LONG`). Mirror commands use a normalized Postgres table, are idempotently claimed, expire after 60 seconds, retry transient execution failures up to three times, and record explicit mirror decisions and failure reasons.
- The follower uses the source's confirmed filled notional capped by the source entry's sized target, plus the same leverage, then manages its own opposite position through the existing working TP/SL, exchange protection, and trailing flow. The cap prevents fill-price drift from exceeding the configured per-position limit on the follower. Local strategy-reversal signals cannot close mirror-owned positions. Existing positions are not changed or retroactively mirrored.
- Futures live entries now use Kraken `fok` limit orders instead of `ioc`: a complete fill is required before a position or mirror command is committed. A defensive partial-FOK response is immediately unwound reduce-only; this prevents small residual positions such as the 1-contract MORPHO fill from being treated as the intended 150 USD entry.
- Repository appsettings remain the strategy source: both workers retain 15 USD target margin, 10x leverage, and 150 USD per-position notional. No mirror or sizing setting was added to environment variables. Not changed: signal scoring, entry guards on the Lukas source, TP/SL/trailing thresholds, Kraken reconciliation, dead-man switch, virtual futures entry decisions or sizing, or spot workers.

## 2026-08-19-lukas-independent-futures-live-instance

- Added an independently gated `futures-lukas-live` worker using the shared futures image and market-data database but isolated Kraken credentials, portfolio state, exchange reconciliation, protective orders, trailing management, logs, and runtime directories. Its Docker container is `trading-bot-lukas-futures-worker-live` and it is enabled only by `TRADINGBOT_LUKAS_FUTURES_LIVE_TRADING_ENABLED=true`.
- Added a repository-owned Lukas futures profile that is identical to the primary futures profile except for instance identity and `Futures.FlipLongEntries=false`. A regression test compares the complete JSON profiles so future strategy, sizing, risk, and exit tuning cannot drift silently between accounts.
- Added CI secret wiring for the existing Lukas Kraken Futures key pair, isolated deployment mapping into the worker's standard credential names, fail-closed live-instance coverage, and UI selectors for the new instance. The startup limits log now reports the configured flip policy instead of the stale `flip=forbidden` text.
- Not changed: signal scoring, entry guards, native SHORT behavior, the primary account's conditional flipped-entry policy, sizing/leverage, TP/SL/trailing rules, IOC execution, Kraken reconciliation semantics, dead-man switch behavior, spot workers, or shared market-data collection.

## 2026-08-17-futures-flip-regime-gate

- The flipped-entry experiment no longer turns every approved LONG into a SHORT. It now flips only when the latest complete closed-candle 24h return is at or below `Futures.FlipMaxPair24hRisePercent` for the pair (default 3%) and at or below `Futures.FlipMaxBtc24hRisePercent` for BTC (default 0%). If either series is unavailable or exceeds its limit, the already-approved original LONG is preserved instead of suppressing the trade.
- The worker calculates both returns from one complete 24h window of closed candles at the configured timeframe and emits a `FLIP_REGIME` line with the pair/BTC values, thresholds, executed side, verdict, and reason. The action reason also records whether the flip was applied or skipped, making live outcomes distinguishable in the decision journal.
- No strategy setting was added to environment variables; both thresholds live in appsettings and invalid values reset to 3% / 0% during normalization. Not changed: entry scoring and guards, native SHORT decisions, trade capacity, sizing/leverage, TP/SL/trailing, IOC execution, reconciliation, or spot behavior.

## 2026-08-15-futures-usd-15x10-sizing

- Futures entries now target 15 USD initial margin at 10x leverage, producing a 150 USD notional position when sufficient collateral is available. Per-position, aggregate, and correlation-group notional caps are aligned at 150 / 450 / 150 USD so downstream guards do not silently retain the previous 60 USD ceiling.
- The stop-distance risk budget is aligned to the configured 3% maximum accepted stop: `Risk.TargetRiskUsd` is 4.5 and `Risk.MaxConcurrentOpenRiskUsd` is 13.5 for three slots. This preserves the full 150 USD notional across accepted 2-3% stop distances; positions can still shrink when free collateral is below 15 USD.
- `Margin.MinLiquidationDistancePercent` is 5%, matching the modeled 5% liquidation distance at 10x with the configured 5% maintenance-margin rate. The working 2% stop floor, 3% maximum accepted stop, Kraken protection, TP/trailing policy, entry scoring/guards, flipped execution direction, and 60 USD account balance are unchanged.

## 2026-08-14-futures-usd-native-15x4-sizing

- Futures accounting is now USD-native end to end: Kraken USD/USDC/USDT available collateral is kept in USD, position quantity is calculated directly from USD notional and the fresh executable bid/ask, and the previous static EUR/USD multiplier plus live FX lookup were removed. Non-USD collateral is excluded rather than mixed into USD buying power.
- Shipped futures settings now use 15 USD target/max margin at 4x leverage for a 60 USD per-position notional cap. The three-position caps are 180 USD aggregate notional and 5.4 USD concurrent stop heat; virtual futures starts with 60 USD, while live cash continues to reconcile from Kraken.
- Futures dashboard, trades, cycle diagnostics, snapshots, simulation, and server-rendered views now display futures values as USD while spot remains EUR. Generic persisted fields with legacy `*Eur` names remain schema-compatible, but their futures values are USD and no conversion is applied.
- No new sizing or risk values are sourced from environment variables; appsettings remains the source of truth. Not changed: entry scoring/guards, flipped-entry direction, max positions, TP/SL/trailing policy, IOC semantics, dead-man switch, Kraken protection, reconciliation ownership, or spot accounting.

## 2026-08-14-futures-flipped-price-owned-exits

- Flipped LONG-to-SHORT positions no longer close on `ShortCandidate`. A bearish signal confirms the executed SHORT side rather than reversing it, so flipped entries are now managed exclusively by working SL, working-TP handoff, exchange trailing protection, exchange emergency protection, or manual close.
- Any position with `TrailingStopState=EXCHANGE_OPEN` now ignores strategy reversal exits. This prevents a full decision cycle from market-closing a winner after Kraken trailing protection has already been armed.
- Not changed: entry selection/scoring, trade frequency, 5 EUR margin, 2x leverage, 10 EUR notional, working SL 2%, flipped trailing activation 1.5%, flipped trailing distance 0.75%, normal reversal behavior before trailing activation, external/manual position ownership, execution, reconciliation, or portfolio caps.

## 2026-08-12-futures-flipped-exit-calibration

- Bot-owned futures positions opened by the LONG-to-SHORT flipped experiment now use a dedicated working profit handoff: take-profit activation at 1.5% and a 0.75% reduce-only Kraken trailing stop. Their working stop-loss remains 2%, and new exchange protection remains twice the working distances (3% TP / 4% SL).
- `MAX_HOLD` is disabled for flipped entries, so the six-hour stale-loss timer can no longer close this experiment before its price-based exit policy resolves. Signal reversal, working stop-loss, trailing stop, exchange protection, and manual close remain active.
- New appsettings-only controls: `TpSl.FlippedTakeProfitPercent` (1.5), `TpSl.FlippedTrailingStopPercent` (0.75), and `Exits.MaxHoldForFlippedEntriesEnabled` (false). Kraken reconciliation applies the new working handoff to existing bot-owned flipped positions while preserving any real protective orders already open on the exchange until handoff.
- Not changed: 5 EUR target margin, 2x leverage, 10 EUR notional cap, normal futures exits, externally opened/manual positions, entry scoring/guards, IOC execution, dead-man switch, or portfolio capacity.

## 2026-08-12-futures-maintenance-margin-model

- Corrected the isolated-margin liquidation estimate to subtract maintenance margin as a fraction of position notional from initial margin (`1 / leverage - maintenance rate`). The previous implementation multiplied initial margin by the remaining percentage and materially overstated liquidation distance.
- Updated `Margin.MaintenanceMarginRatePercent` from 0.5% to Kraken EEA retail's 5% first-tier rate. With the existing 8% minimum liquidation-distance gate, a 10x entry now estimates 5% adverse room and is rejected, while the configured 2x entries estimate 45% and remain eligible.
- Invalid maintenance-margin settings (`<= 0` or `> 50`) now reset to the safe 5% default with a validation message. Added exact 2x/10x liquidation and risk-gate regression coverage.
- Not changed: configured 2x leverage, 5 EUR target margin, 10 EUR notional cap, entry scoring/guards, TP/SL/trailing, Kraken execution, reconciliation, or portfolio capacity.

## 2026-08-07-futures-flipped-entry-inverted-reversal-exit

- Fixes the flipped-logic experiment's exit hole observed live (SPCXX/USD): a flipped short opened FROM a LONG signal was immediately closed by `DecideHeld` as "signal reversal close", because for a normal short a persisting LongCandidate IS the reversal. The position survived only the minimum-hold window and closed at +0.13%. The signal that opens a flipped position can never be the signal that closes it.
- New `PortfolioPosition.FlippedEntry` (persisted as `portfolio_position_state.flipped_entry boolean not null default false` with an `add column if not exists` migration; carried through Clone, live Kraken reconciliation, and restarts). The futures worker stamps it alongside `EntryChannel` when a flip executes.
- `LongShortStrategy.DecideHeld` inverts the reversal exit for flipped shorts: hold while the long thesis persists, close when a ShortCandidate appears — exactly when the original long would have closed. Normal shorts and all longs are untouched; spot ignores the field entirely.
- Everything price-based was already correct for the real short side and is unchanged: working TP 4% / SL 2% tracked by the worker, exchange protective TP/SL orders at 2x distance (8% / 4%) via `ExchangeProtectionMultiplierPercent=200`, and the working-TP -> trailing-stop (2%) handover.
- The reversal close reason for a flipped position now reads `signal reversal close; flipped logic applied`.
- Not changed: entry gates, sizing/leverage, TP/SL percentages, trailing, max-hold exit, dead-man switch, universe selection, or portfolio caps.

## 2026-08-06-futures-flip-long-entries-experiment

- Contrarian experiment, operator-requested: new `Futures.FlipLongEntries` (env `TRADINGBOT_FUTURES_FLIP_LONG_ENTRIES`, appsettings ships true) executes a fully approved LONG entry as a SHORT. The entire long pipeline runs UNCHANGED — scoring, dip-bounce admission, quality gate, freshness, 24h-range guard, follow-through, portfolio guards, and margin risk all still evaluate the long thesis — and only the submitted order side inverts at the execution step. Rationale: the recent live long cohort has been persistently negative; this mirrors those exact entries to measure whether the inverse of the signal carries edge, without perturbing any gate statistics.
- The opened position is a REAL short everywhere downstream: IOC side `sell`, limit-price and pre-submit deviation checks use the short branch, ledger side SHORT, TP below entry / SL above, liquidation estimate, exchange protection orders, trailing handoff, signal-reversal close, and Kraken reconciliation are all standard short behavior. The decision/ledger reason keeps its normal text and appends `flipped logic applied`; a `FLIPPED_LOGIC pair=... approved=LONG executed=SHORT` console line is emitted per flip.
- Guard rails: `Normalize()` disables the flip with a warning when `Futures.AllowShorts` is false (the portfolio layer would refuse the short and the bot would silently stop entering), and logs a loud config-warning at startup while the flip is active. SHORT candidates, held-position management, and all exits are untouched. New tests pin default-off, survive-normalize, and shorts-disabled behavior.
- Not changed: any entry gate or threshold, sizing/leverage, TP/SL percentages, trailing, Kraken execution mechanics, reconciliation, dead-man switch, universe selection, or portfolio caps.

## 2026-08-05-futures-mid-range-chop-filter

- MID-zone non-breakout futures LONG entries now measure directional efficiency over the latest 96 closed 15-minute candles and reject entries below `Freshness.MinMidRangeDirectionalEfficiencyPct` (default 5%). This separates a sustained move from a short micro-rally inside a range: the live DOGE entry had moved only -0.083% net while travelling 15.80% candle-to-candle, for 0.53% efficiency.
- New appsettings-only controls: `Freshness.DirectionalEfficiencyLookbackCandles` (default 96, valid 8-120) and `Freshness.MinMidRangeDirectionalEfficiencyPct` (default 5%, valid 0-100). Invalid values reset to safe defaults during `Normalize()`; no strategy environment override was added.
- Not changed: LOW-zone rebounds, confirmed UPPER breakouts, SHORT entries, score thresholds, sizing/leverage, TP/SL/trailing, Kraken execution, reconciliation, dead-man switch, universe selection, or portfolio caps.

## 2026-08-05-futures-long-follow-through-gate

- Futures LONG entries now require setup-specific follow-through after scoring, market-quality, freshness, and range checks have passed. UPPER-zone breakouts must show either recent candle momentum or snapshot price-action trend at least `Freshness.UpperBreakoutMinFollowThroughPct` (default 0.60%), so a mere touch above the local high no longer spends a slot when the tape is weak.
- MID-zone non-breakout LONG reclaims/continuations now need snapshot price-action trend at least `Freshness.MidRangeReclaimMinPriceActionTrendPct` (default 0.50%). The recent live loss cluster was concentrated in MID reclaims with score-heavy bullish structure but weak immediate continuation; this shifts capacity toward LOW-zone rebounds, strong breakouts, and shorts instead of reducing the global score funnel.
- New env overrides: `TRADINGBOT_FUTURES_UPPER_BREAKOUT_MIN_FOLLOW_THROUGH_PERCENT` and `TRADINGBOT_FUTURES_MID_RANGE_RECLAIM_MIN_PRICE_ACTION_TREND_PERCENT`. Invalid values reset to safe defaults during `Normalize()`.
- Not changed: LONG/SHORT score thresholds, LOW-zone rebound rules, SHORT entry logic, sizing/leverage, TP/SL/trailing, Kraken execution, reconciliation, dead-man switch, universe selection, or portfolio caps.

## 2026-08-02-futures-small-2x-sizing-and-portfolio-api-rounding

- Futures appsettings now target small entries: `Futures.TargetMarginEur`=5, `Futures.DefaultLeverage`/`MaxLeverage`=2x, `Futures.MaxNotionalEur`=10, `Futures.MaxTotalNotionalEur`=30, `Futures.MaxMarginPerPositionEur`=5, and `CorrelationRisk.MaxExposureEurPerGroup`=10. `Risk.TargetRiskEur`=0.3 / `MaxConcurrentOpenRisk`=0.9 keep accepted stops from shrinking below the 10 EUR notional cap while still bounding three configured slots.
- Portfolio API/Web summary reads now round display numerics to 8 decimal places in SQL before Npgsql maps them to C# decimals. This prevents long-scale Postgres numeric values from making `/api/portfolio` return 500 while leaving stored ledger precision unchanged.
- Kraken Futures reconciliation now preserves the exchange-reported leverage for existing remote positions instead of clamping it to the new-entry `Futures.MaxLeverage` cap. Lowering the entry cap to 2x therefore cannot inflate already-open 10x positions in the UI ledger.
- Not changed: entry scoring, LONG/SHORT guards, TP/SL percentages, trailing-stop handoff, universe selection, or dead-man switch behavior.

## 2026-07-31-futures-force-include-active-universe

- Futures active selection now treats `UniverseDiscovery.ForceInclude` as a true detailed-evaluation guarantee, not only as a discovery fallback. The worker still selects the normal `Trading.MaxActiveInstruments` set first using the existing held-position / strong-mover / absolute-change / volume ranking, then appends any missing force-included pairs. Core pairs such as ETH therefore cannot disappear from `cycle-decisions` just because their 24h move is quiet, and force-included pairs do not crowd out the normal top movers.
- Futures and market-data appsettings now share the same 40-pair Kraken Futures force list: XBT, ETH, SOL, XRP, HYPE, DOGE, ADA, SUI, ZEC, LINK, AVAX, NEAR, XLM, BNB, LTC, BCH, AAVE, TAO, INJ, ENA, ONDO, UNI, TRX, DOT, ATOM, FIL, ARB, OP, POL, CRV, HBAR, LDO, XMR, PEPE, WIF, SHIB, BONK, PENGU, APT, and ALGO. The names were verified against the live futures `instrument_registry`.
- Not changed: scoring thresholds, LONG/SHORT entry guards, anti-chase logic, sizing, leverage, TP/SL, Kraken execution, reconciliation, or dead-man switch settings.

## 2026-07-31-futures-trailing-reconciliation-and-db-first-deploy

- Futures live fast-exit checks now persist non-closing TP events such as `TRAILING_ACTIVATED` / `TRAILING_ACTIVATION_FAILED` into the cycle journal. A bot-owned live position that reaches working TP and arms a Kraken trailing stop is now visible in the decisions/trades UI instead of disappearing because the position stayed open.
- Kraken Futures reconciliation now recognizes existing reduce-only `trailing_stop` open orders as active trailing protection. After a restart or sync, the worker preserves `TrailingStopState=EXCHANGE_OPEN` and does not recreate the old TP/SL pair over an already-armed trailing stop.
- Trailing activation failures now restore the previous local TP/SL states, so a failed cancel/order path does not leave the ledger marked as triggered/cancelled while exchange protection remains unchanged.
- Production deploy now starts Postgres first, verifies the `database` DNS alias over the compose network with `pg_isready`, and only then starts API/web/workers. App services depend on a `db-ready` one-shot service instead of Docker's in-container Postgres healthcheck, and the Postgres host bind defaults to localhost so stack startup does not depend on a late VPN interface.
- Not changed: entry scoring, sizing, leverage, working TP/SL percentages, Kraken IOC execution, dead-man switch settings, or virtual TP close behavior.

## 2026-07-30-futures-enable-shorts-and-relative-strength

- Fixes an unreachable short entry gate. In a real downtrend the short score is structurally capped at 0.80: the bearish-EMA base (0.60) plus calm volatility (0.05), downside momentum (0.10) and price-below-trend (0.05) reach exactly 0.80, while BOTH RSI bonuses require an OVERHEATED RSI that a falling market never produces. `Shorts.MinShortScore` sat at 0.85, so the gate could only be cleared with volume confirmation, which almost never fires. Measured over 48h of futures-live: 65,932 decision rows, 37,023 with bearish structure, 14,464 admitted by the scorer, max short score 0.80, and ZERO shorts opened. `Shorts.MinShortScore` is now 0.80, which also matches the scorer's own admission bar (`Strategy.MinimumLongScore`) — the 0.85 value was leftover asymmetry inside one path, not a deliberate setting. Every other short protection is unchanged: bearish EMA plus a downside confirmation are still required, and `FuturesShortEntryGuard` (pullback from the 24h high, fresh downward tape, local-low anti-chase, breakdown confirmation) still applies on top.
- Enables the relative-strength gate shipped disabled in the previous entry (`Regime.RelativeStrengthGateEnabled` = true, `MinRelativeStrengthPct` = 0.5). The data now supports it: all 7 futures-live longs closed in the window were losses, every one of them a Reclaim/Standard/Continuation entry with `breakout=false`, and their excursion profile was systematically adverse (best favourable move +0.23%..+1.71%, worst adverse move -3.38%..-6.80%). Mirroring the same entries as shorts would have returned about +15 EUR net. The gate only applies to a LOW-zone long taken while the BTC regime blocks longs, and requires the pair to be rising on its own AND outperforming BTC; a pair whose momentum cannot be measured is never blocked.
- Not changed: TP/SL levels, the working-TP-to-trailing-stop handover, sizing, leverage, `Strategy.MinimumLongScore`, `Regime.ShortOverrideMinScore` (so a BTC crash beyond `Shorts.MaxChaseDrawdownPct` still refuses to chase shorts), or the signal-flip exit. Take-profit calibration was deliberately left alone: with the direction fix in place the excursion profile these trades produced is no longer representative.

## 2026-07-28-futures-relative-strength-measurement

- Measures market-relative strength on every futures decision: the pair's own candle momentum minus BTC's over the same lookback (`Regime` now carries BTC's recent change). This is the difference between "this pair is genuinely flying while the market bleeds" (a scalp worth taking) and "this pair is merely drifting down with everything else".
- The veto that uses it SHIPS DISABLED (`Regime.RelativeStrengthGateEnabled` = false, `Regime.MinRelativeStrengthPct` = 0.5). Entry behaviour is unchanged: nothing is blocked that was not blocked before. When enabled, it only applies to a LOW-zone long taken while the BTC regime blocks longs, and requires the pair to be rising on its own AND outperforming BTC; an unmeasurable pair is never blocked on suspicion.
- Rationale for shipping it off: the evidence is genuinely split. Two low-zone longs (TIA, SUSHI) closed by signal flip at about -1.24% each, but two others opened in the same falling market (ADA, POL) are in profit, with ADA up 0.72% on a day the pair itself is down 3.5%. Tightening the filter on two losses while two winners are open would be fitting to four trades.
- Diagnostics persisted per decision (`btc_recent_change_pct`, `relative_strength_pct`) with an explicit `alter table ... add column if not exists` migration, so the question can be answered from history before any veto is switched on.
- Not changed: entry/exit thresholds, sizing, leverage, TP/SL, the signal-flip exit, or SHORT logic.

## 2026-07-28-futures-live-ledger-wording

- Wording only, no behaviour change: the futures virtual ledger backs BOTH modes — on a live instance it mirrors real, exchange-confirmed Kraken fills, on a dry-run instance it simulates them. Its open/close reason text hardcoded the word "virtual", so a real IOC fill was journalled as `live Kraken Futures IOC accepted id=... status=placed; open virtual long: notional EUR 149.91 ...`, which reads as if no money moved. The reason text now drops "virtual" on a live instance and keeps it on a dry-run one.
- The trades journal page derives the same wording from the committed fill source (`REAL*` → "реальный", otherwise "виртуальный") instead of hardcoding it.
- Not changed: sizing, leverage, TP/SL, entry guards, reconciliation, or any decision logic — only the human-readable reason strings.

## 2026-07-28-futures-strong-confirmation-and-short-zone-antichase

- Low-range LONG entries now require at least one STRUCTURAL confirmation on top of the count. The four low-range confirmations are not equally strong: a fresh upward tape and the multi-candle momentum are structural, while a single positive snapshot step and a single green candle are one-observation signals that a dead-cat bounce also produces. Without this rule the default `LowRangeMinConfirmations` (2) could be met by the two weakest signals alone while the tape was not fresh and multi-candle momentum was negative — exactly the falling-knife shape the dip channel was built to avoid. New `Freshness.LowRangeRequireStrongConfirmation` (default true), env `TRADINGBOT_FUTURES_LOW_RANGE_REQUIRE_STRONG_CONFIRMATION`, new block reason `LONG_LOW_RANGE_STRONG_CONFIRMATION_MISSING`.
- Removed a single point of failure: `Freshness.LongRangeGuardEnabled` and `Shorts.RangeGuardEnabled` now toggle only the zone-scoped RELAXATION, never the protective vetoes. Since the freshness guard stopped vetoing local-high/drift, those flags would otherwise have disabled every late-entry protection (including the upper-range breakout-only rule that prevents buying the daily peak) in one switch. With the relaxation off, a LOW zone behaves like MID: strict fresh tape plus anti-chase everywhere, i.e. the pre-relaxation behaviour.
- If the fresh-tape gate is switched off (`RequireFreshTapeForLowRangeLong` / `RequireFreshTapeForHighRangeShort` = false), the weaker vetoes it implies (rising/falling snapshot count, slope sign) now take over instead of leaving the tape completely unchecked.
- SHORT side (mirror of the LONG rework): the falling-snapshot-count and slope vetoes are deduplicated behind the fresh downward tape gate that already implies them — a rising tape is now reported once as `SHORT_FRESH_TAPE_NOT_CONFIRMED` instead of the earlier-in-chain code that was swallowing the rejection statistics. Anti-chase (`SHORT_ENTRY_TOO_CLOSE_TO_LOCAL_LOW`, `SHORT_ENTRY_DRIFT_TOO_HIGH`) is now scoped to the lower part of the range via new `Shorts.AntiChaseMaxRangePositionPct` (default 65, the mirror of the long threshold 35), env `TRADINGBOT_FUTURES_SHORT_ANTI_CHASE_MAX_RANGE_POSITION_PERCENT`: at the top of the range there is nothing to chase downwards, so requiring the entry to sit far above the local low there contradicted the fresh-downward-tape requirement.
- Not changed: scoring thresholds (`Strategy.MinimumLongScore`, `Shorts.MinShortScore`), rebound/pullback anti-knife minimums, the upper-range breakout-only rule, sizing, leverage, TP/SL, execution, or Kraken reconciliation.

## 2026-07-28-futures-long-entry-zone-antichase

- Futures LONG entry range guarding now separates low-range reclaim entries from mid/upper-range chase protection. In the low zone (`Freshness.AntiChaseMinRangePositionPct` default 35), local-high and signal-drift anti-chase diagnostics are still recorded but no longer veto the entry by themselves.
- Added low-zone confirmation quorum via `Freshness.LowRangeMinConfirmations` default 2: a low-range LONG needs at least 2 of fresh upward tape, sufficient recent candle momentum, positive last snapshot step, and a green last closed 15m candle. The existing `MinReboundFrom24hLowPct` falling-knife guard remains hard in every zone.
- Signal drift protection now scales with volatility: the effective drift cap is `max(MaxEntryDriftFromSignalPct, DriftAtrMultiple * atrPct)`, with `Freshness.DriftAtrMultiple` default 0.25 and ATR computed from the latest closed 15m candles.
- `RequiredRisingSnapshotCount` and `RequirePositiveShortSlope` remain diagnostic/backward-compatible settings but are no longer independent LONG vetoes, because fresh tape already strictly implies their intent.
- Added SQL/view diagnostics for LONG zone, anti-chase application, confirmation counts, effective drift cap, and ATR percent. SHORT logic, scoring thresholds, sizing, leverage, TP/SL, execution, reconciliation, and dead-man switch behavior are unchanged.

## 2026-07-27-futures-live-collateral-eur-display

- Futures live Kraken reconciliation now treats USD/USDC/USDT `availableMargin` as quote-currency collateral and converts it to EUR via Kraken `EUR/USD` before writing `PortfolioState.CashEur`. This stops the dashboard and EUR-denominated sizing from treating USD values as EUR.
- Portfolio summary persistence now stores optional `cash_quote_value` / `cash_quote_currency` alongside `cash_eur`, so UI/API can display the Kraken-like EUR amount with the original USD available amount in parentheses.
- If EUR/USD conversion is unavailable, the worker keeps the prior fallback (uses the numeric available value) and logs the conversion failure explicitly.

## 2026-07-27-futures-virtual-budget-125

- Futures virtual default starting collateral is now EUR 125 so a fresh virtual ledger restarts with the requested larger sandbox budget. Spot virtual remains EUR 75.
- Trading decisions, sizing, execution, TP/SL, and live bot behavior are unchanged.

## 2026-07-23-normalized-postgres-json-removal

- Postgres persistence now writes portfolio state and cycle forensic data only into normalized tables. The legacy `portfolio_state.state_json` and `dry_run_cycles.record_json` archive columns are no longer written and are dropped automatically during schema initialization.
- Removed the `dry_run_cycle_records` compatibility view and moved remaining API cycle/status/simulation paths to normalized cycle, decision, action, diagnostic, excluded-pair, and snapshot tables. The public cycle/trade pages still receive the expected `record.activePairs` and `record.decisions` shape, but it is assembled from normalized rows instead of database JSON.
- Added indexes for current cycle, decision, action, active-pair, excluded-pair, and market snapshot API filters so latest-meta views, trade-cycle scans, pair lookups, and hydrated cycle records do not fall back to broad table scans.
- Disabled the CSV export endpoints in API/Web while reporting moves to normalized tables, and removed obsolete JSON-export/backfill analysis scripts that depended on `record_json`.
- Trading decisions, sizing, execution, TP/SL, market snapshots, and live/virtual behavior are unchanged.

## 2026-07-18-spot-live-kraken-reconciliation-and-sell-fill

- Root cause of spot live diverging from the Kraken dashboard (while futures matches): the spot broker only exposed balances, so reconciliation could correct quantity and total cash but never the real cost basis, and a live SELL whose fill could not be confirmed by QueryOrders was committed at a MODELED price — recording a trade that never matched Kraken.
- Added `ISpotBroker.GetTradeHistoryAsync` (Kraken TradesHistory) and used it to: recover a live SELL fill by ordertxid when QueryOrders has not yet reflected it, and reconcile each holding's real average cost basis (buys add quote + fee, sells reduce pro-rata). Imported and existing positions now take their entry price from trade history instead of the current last price.
- Error-handling symmetry: a live SELL whose fill cannot be confirmed by either QueryOrders or trade history is no longer booked as a modeled close — like a BUY, it records "not executed" and lets the next balance/history reconcile correct the state, so the DB never shows a phantom or wrong-price sell.
- A held position whose pair is not in this cycle's discovery universe is no longer removed on sight: its balance is resolved directly, so a real Kraken holding is kept (and its cost basis reconciled) instead of being dropped and re-imported at a wrong price. Trade-history calls are best-effort — any failure degrades to the prior balance-only behaviour without blocking the cycle.
- Spot worker only; futures reconciliation, sizing, TP/SL, and execution are unchanged.

## 2026-07-18-futures-partial-collateral-entry-sizing

- Futures entry sizing now shrinks a valid entry plan to the remaining free collateral before portfolio/risk gates and live execution. If the normal target (for example 40 EUR margin × 10x) does not fit, the worker tries the largest smaller notional that fits `state.CashEur` including entry taker fee and the configured account margin-utilization cap.
- The reduced plan keeps the same signal, stop/TP distances, leverage, funding/depth checks, Kraken precision checks, and live price-deviation guard. If the leftover amount is too small for Kraken quantity precision or any other existing rule, the entry is still skipped with the existing reason.

## 2026-07-17-futures-tpsl-close-explainability

- Futures close actions now carry the frozen TP/SL distances, estimated open risk, and exact entry/fill realized percent from the closed position. Stop-loss/take-profit journal rows can show the actual working level that fired instead of losing that context on close.
- Futures stop-loss close text now includes entry price, fill price, realized percent, and the frozen working SL/TP level. This is an observability-only change; TP/SL trigger mechanics, live order execution, and sizing are unchanged.

## 2026-07-17-futures-external-trailing-profit-protection

- Futures live reconciliation can now manage KRAKEN_SYNC / external positions only after they already have both matching reduce-only Kraken TP and SL orders. When the closeable live price reaches `TpSl.ExternalTrailingActivationProgressPercent` (default 80%) of the way from entry to the existing Kraken TP, the worker cancels those TP/SL orders and places a reduce-only Kraken trailing stop using `TpSl.TrailingStopPercent` (default 2%).
- External trailing activation uses executable close prices (LONG bid, SHORT ask), validates that TP/SL are on the correct side of entry and match the position size, and skips positions that already have trailing active or lack either protective order.

## 2026-07-17-futures-preserve-frozen-tpsl-distances

- Futures live reconciliation now keeps frozen working TP/SL distances together with frozen working TP/SL prices for already-open bot-owned positions. Existing positions opened before a TP/SL config change no longer show the new percent while still closing on the old frozen price.
- Added regression coverage for the HYPE/USD case: a position opened with old 1%/3% working levels remains internally consistent after the default config is changed to 2%/4%; new positions still use the current config.

## 2026-07-17-decisions-explainability-refactor

- Futures decision records now persist the actual SHORT-side diagnostics (`ShortScore`, bearish EMA gap/structure, allow verdict, configured score/EMA thresholds) instead of forcing the Decisions UI to mislabel the LONG score as the reason a SHORT was rejected.
- Base SHORT rejection now has an explicit machine-readable code and exact explanation: bearish EMA not confirmed, SHORT score below the signal threshold, or missing downside momentum/volume/trend confirmation. Trading decisions, thresholds, sizing, and execution behavior are unchanged.
- The Decisions page now resolves explicit gate codes before prose fallbacks and presents side-aware, human-readable verdicts while retaining raw technical evidence for audit.

## 2026-07-17-futures-working-tpsl-4-2

- Futures fixed working exit defaults changed from TP 3% / SL 1% to TP 4% / SL 2% in `TpSl`; with the existing `ExchangeProtectionMultiplierPercent` 200, live Kraken protective orders are placed at TP +8% and SL -4%.
- Trailing stop behavior is unchanged: once the worker-owned position reaches the working TP, protective orders are replaced by the configured 2% reduce-only trailing stop.

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
