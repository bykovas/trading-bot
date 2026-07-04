# 2026-07-04 Early Entry Gap Analysis

This note is an analysis artifact, not a worker behavior change.

It references the active worker changelog entry in [`.ai/worker-changelog.md`](../../.ai/worker-changelog.md):

```text
2026-07-04-price-action-warmup-and-precise-rejections
```

The latest persisted cycles in PostgreSQL were produced by:

```text
worker_version / commit / image_tag: 0c5e5875086ffefa8539c4841565c831e3f58043
worker_build_utc: 2026-07-04T12:28:07Z
strategy_version: ee6193ce6fbc23e8f5752b01d6a8737f590641ea58ec47e89c729c5695811477
change_set: 2026-07-04-price-action-warmup-and-precise-rejections
```

## Context

Manual Trading 212 observation:

- XRP/EUR was bought manually around `1.0098` average price.
- The position was up roughly `+0.12 EUR` on about `40 EUR`, around `+0.29%`.
- The user question: why did the bot not take a similar virtual entry while cash was idle?

Bot database observation:

- The bot did open one virtual position, but in `XPL/EUR`, not `XRP/EUR`.
- Current virtual state at the time of analysis:
  - `cash_eur = 65`
  - `open_positions = 1`
  - `XPL/EUR` position around `10 EUR`
  - unrealized PnL fluctuated around `+0.10%` to `+0.20%`
- XRP/EUR stayed `NO_ORDER` in all inspected cycles.

## First Observation

The issue is not a cash/risk/cooldown block. The dominant rejection reason is:

```text
REJECT_NO_BULLISH_SIGNAL
```

Across the inspected cycles, most decisions were rejected for this reason:

```text
NO_ORDER / REJECT_NO_BULLISH_SIGNAL: 251
WOULD_BUY: 1
```

For XRP/EUR specifically:

```text
12:29 UTC price 0.99979 score 0.50 -> NO_ORDER
13:05 UTC price 1.00335 score 0.50 -> NO_ORDER
13:20 UTC price 1.00691 score 0.50 -> NO_ORDER
13:31 UTC price 1.00812 score 0.40 -> NO_ORDER
```

The latest XRP/EUR contribution breakdown:

```text
EMA:        0.00  EMA crossover ignored because gap 0.298% < configured minimum 0.350%
RSI:       0.05  RSI 62.79 is acceptable but outside the ideal band
Volatility:0.05  short-term volatility 0.41% is controlled
```

Earlier XRP/EUR had a stronger RSI contribution, but still failed:

```text
EMA gap around 0.253% < required 0.350%
RSI ideal contribution around +0.15
Volatility contribution around +0.05
Final score around 0.50
```

The practical interpretation: the bot recognized that XRP/EUR was not bad, but it did not score it as an entry. The current entry logic requires stronger confirmation than a rising short-term chart.

## GPT Review Notes

The important nuance: the problem is not that the bot missed XRP specifically. The broader issue is that the bot has very little early-entry machinery.

`EMA gap = 0.253%` does not mean the trend is absent. It means:

- fast EMA is already above slow EMA;
- the spread between them is only `0.253%`;
- the config requires at least `0.350%`.

So the trend has started, but the bot says: wait until it becomes more obvious.

That is the cost of conservatism. When the EMA gap finally reaches `0.350%`, price may already be another `0.3%`, `0.5%`, or even `1%` higher. This is not inherently wrong, but it is a strategy philosophy: buy later after confirmation, not earlier during formation.

However, lowering `MinimumEmaGapPercent` globally from `0.35` to `0.20` is probably too blunt. It would affect every pair and likely cause:

- more entries;
- more false EMA crossovers;
- more fees;
- more stops;
- more churn outside XRP-like moves.

The better direction is a separate scoring path:

```text
EMA confirmed
OR
strong price action / breakout confirmed
```

For example, keep EMA scoring intact, but add a separate route where strong price action can contribute enough to produce an exploratory entry:

```text
PriceAction = RISING
trend over recent snapshots > configured threshold
recent highs are being updated
volume / liquidity confirmation is acceptable
spread is clean
```

This would let the bot buy early momentum even when EMA has not yet widened enough.

## Current Structural Weakness

The current behavior is close to:

```text
EMA confirmation first,
then other signals help.
```

The more flexible model would be:

```text
EMA score
+ momentum score
+ trend score
+ breakout score
+ price-action score
```

as independent signal sources.

That matters because these two cases are not equivalent:

```text
Case A:
EMA gap = 0.36%
price action = flat

Case B:
EMA gap = 0.25%
price action = rising strongly
recent highs are updating
volume is acceptable
```

The current bot is more likely to approve Case A than Case B. The XRP example looks closer to Case B.

## Evidence Needed Before Changing Strategy

Do not tune this from one XRP example.

The next useful analysis is a rejected-signal replay over several days:

- find pairs rejected mainly because `MinimumEmaGapPercent` was not met;
- among those, identify cases where price action was already rising;
- measure future return after rejection over `2h`, `4h`, and `6h`;
- compare against fees and likely stop-loss behavior;
- count how many would have reached `+2%` versus how many would have stopped out.

If most such rejected setups later move profitably, add a momentum/breakout entry path. If outcomes are mixed, the current EMA filter may be saving more money than it loses in missed moves.

## Working Hypothesis

The bot is not broken. It is confirmation-heavy.

It is designed to avoid false positives more than to catch early momentum. That is why it missed XRP/EUR while a discretionary manual trade could capture a small move.

The likely improvement is not simply lowering EMA gap globally. It is adding an explicit early-entry or breakout score path that can coexist with the existing conservative EMA path.

## Follow-up Changes Applied

After this analysis, the worker config was tuned in change set:

```text
2026-07-04-tight-stop-wide-take-profit
```

The changes were intentionally narrow:

- `PositionExit.StopLossPercent`: `2.5 -> 1.5`
- `PositionExit.TakeProfitPercent`: `3.0 -> 4.0`
- `ExecutionPolicy.EntryBlackoutUtcFromHour`: `20 -> 22`
- `ExecutionPolicy.EntryBlackoutMinutes`: `600 -> 360`

In Lithuania summer time this changes the new-entry blackout from roughly:

```text
23:00-09:00 LT
```

to roughly:

```text
01:00-07:00 LT
```

Interpretation:

- weaker positions should be cut earlier instead of drifting toward a deeper stop;
- stronger movers have more room to reach a larger take-profit;
- the bot can still open positions during late evening and early morning liquidity if the normal entry filters pass;
- existing positions can still exit during blackout, because blackout only blocks new entries.

This was not intended to optimize to one CSV day. It should be reviewed over several dry-run days by comparing:

- number of `SELL_STOP_LOSS` events;
- average loss per stopped trade;
- number of trades that would have recovered after touching `-1.5%`;
- number of trades that reached `+4%` after previously hitting the old `+3%` take-profit;
- total realized PnL after fees and slippage, not raw price movement.

## Follow-up Idea: Separate Exit Cadence

Current discussion: the normal strategy cycle around every 5 minutes is acceptable for entries, because the entry signals are not intended to be one-minute scalps.

However, exits have a different job. Once a position is open, stop-loss, take-profit, and emergency exits may benefit from a faster check cadence.

Proposed experiment:

```text
entry scan cadence:      every 5 minutes
open-position exit scan: every 1 minute
```

The one-minute exit scan should be limited to already-open positions and should not evaluate new entries. It should only:

- refresh bid/ask for held pairs;
- compute conservative PnL using fee/slippage assumptions;
- trigger hard exits: stop-loss, take-profit, max-hold, kill/emergency;
- optionally run lightweight signal/score-decay checks only if the needed indicators are already fresh enough;
- persist clear diagnostics showing that this was an exit-only check.

Why this may help:

- reduce delay between crossing `-1.5%` and actually exiting;
- reduce delay between crossing `+4%` and locking profit;
- avoid increasing entry churn or buying one-minute noise;
- keep API/DB load modest because only held pairs are checked.

Risks / questions:

- repeated one-minute exit checks must not create duplicate sells;
- the worker must preserve ordering: exits first, entries later;
- missing/stale held-pair prices should fail conservative, but should not break the normal 5-minute cycle;
- if signal-based exits need full indicators, they may still belong in the 5-minute strategy cycle while hard price exits run every minute.

Suggested validation:

- add diagnostics first: `exitOnlyCheck=true`, held pair count, price age, PnL, would-exit reason;
- run for at least several days in dry-run;
- compare actual 5-minute exits vs simulated 1-minute hard exits on the same open positions.
