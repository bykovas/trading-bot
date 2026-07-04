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
