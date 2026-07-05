# Missed Breakout Candidate Report

This is a replay/regression analysis artifact. It does not change trading behavior and should not be used to tune one coin or one day.

## Scope

- source CSV: `/private/tmp/trading-bot-latest-cycles-snapshots.csv`
- cycles: `222`
- rejected/blocked candidate rows inspected: `4146`
- LT time range: `2026-07-04 15:29:00 -> 2026-07-05 10:14:47`

## Candidate Definitions

Strict near-breakout candidate:

```text
0.75 <= final score <= 0.85
EMA confirmed
RSI contribution > 0
Momentum contribution > 0
Trend contribution > 0
Volatility contribution < 0
Volume weak/missing or VolumeCap active
```

Loose candidate:

```text
0.70 <= final score <= 0.85
at least 3 of: EMA confirmed, RSI positive, Momentum positive, Trend positive, PA RISING
```

## Headline Counts

- strict near-breakout candidates: `5`
- loose candidates: `482`
- top strict pairs: `M/EUR x5`
- top loose pairs: `ALGO/EUR x60, HBAR/EUR x58, SUI/EUR x51, XLM/EUR x38, LINK/EUR x30, AVAX/EUR x25, OP/EUR x24, ONDO/EUR x24`

Top rejection reasons among inspected rows:

```text
REJECT_NO_BULLISH_SIGNAL: 3445
REJECT_SCORE_BELOW_THRESHOLD: 563
REJECT_CORRELATION_LIMIT: 58
REJECT_EXPLORATORY_REQUIRES_POSITIVE_PRICE_ACTION: 38
REJECT_SPREAD_TOO_WIDE: 15
REJECT_CYCLE_POSITION_LIMIT: 13
REJECT_EXPLORATORY_RANK: 9
REJECT_INSUFFICIENT_PRICE_HISTORY: 4
REJECT_COOLDOWN: 1
```

## Initial Read

- Strict near-breakouts are all `M/EUR` in this export, so this is still a single-setup sample.
- Strict candidates looked strongest at 30m (median `2.170%`) but faded by 120m (median `0.508%`).
- Worst 120m path drawdown among strict candidates was `-1.033%` before fees/spread.
- Loose candidates are not an obvious edge: 120m median `-0.124%`, average `-0.099%`.
- Current evidence supports more diagnostics, not a live threshold/spread loosening.

## Live Observation: ADA/EUR Early Structure

Observed after the CSV export window through `/api/cycles`.

```text
cycle: 20260705080130
LT time: 2026-07-05 11:01:30
pair: ADA/EUR
action: NO_ORDER
rejection: REJECT_NO_BULLISH_SIGNAL
price: 0.168767
score: 0.74
spread: 0.065%
PA: RISING +0.368%
hasBullishStructure: true
emaFullyConfirmed: false
EMA gap: 0.214%
EMA gap velocity: +0.117%
earlyEntryEligible: false
earlyEntryReason: diagnostic score 0.74 below 0.85
earlyEntrySuggestedNotionalEur: 5
```

Contribution breakdown:

```text
EMA:        +0.14  fast EMA above slow EMA by 0.214%, below full 0.350% minimum
RSI:        +0.05  RSI 62.68 acceptable, outside ideal band
Volatility: +0.05  short-term volatility 0.50% controlled
Momentum:   +0.10  price up 1.58% over last 4 candles
Volume:     +0.05  last candle volume above 1.2x recent average
Trend:      +0.05  price above 50-period trend filter
PriceAction:+0.00  PA rising, diagnostic/context only here
```

Interpretation:

- ADA was not blocked by cash, spread, max positions, or correlation.
- It was blocked because the EMA gap had early bullish structure but was not fully confirmed.
- This is exactly the class of setup the early-entry diagnostics were meant to observe before enabling real buys.
- Re-check this candidate after `30m`, `60m`, and `120m` from `2026-07-05 11:01:30 LT`.

## Forward Return Summary

Strict near-breakouts:

```text
15m: n=5, win=80.0%, >=+1%=2, <=-1%=0, avg=0.806%, med=0.877%
30m: n=5, win=100.0%, >=+1%=5, <=-1%=0, avg=2.024%, med=2.170%
60m: n=5, win=60.0%, >=+1%=1, <=-1%=1, avg=0.219%, med=0.314%
120m: n=5, win=60.0%, >=+1%=0, <=-1%=0, avg=0.163%, med=0.508%
```

Loose candidates:

```text
15m: n=479, win=48.6%, >=+1%=19, <=-1%=16, avg=0.008%, med=0.000%
30m: n=473, win=45.7%, >=+1%=34, <=-1%=29, avg=0.001%, med=-0.032%
60m: n=470, win=39.6%, >=+1%=32, <=-1%=39, avg=-0.081%, med=-0.202%
120m: n=470, win=42.3%, >=+1%=47, <=-1%=81, avg=-0.099%, med=-0.124%
```

## Strict Candidates With Largest 120m Upside

|LT time|pair|score|score-no-vol-penalty|reject|spread|PA|EMA gap|vol|volume|r30|r60|r120|max up|max down|
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
|2026-07-05 06:47:18|M/EUR|0.75|0.85|SPREAD_TOO_WIDE|0.520|FALLING -1.098|0.431|-0.10|0.00|1.598|1.375|0.508|3.227|-0.166|
|2026-07-05 06:52:29|M/EUR|0.75|0.85|SPREAD_TOO_WIDE|0.509|FALLING -0.765|0.431|-0.10|0.00|2.170|0.821|0.508|3.227|-0.166|
|2026-07-05 06:57:40|M/EUR|0.75|0.85|SPREAD_TOO_WIDE|0.504|FALLING -0.372|0.431|-0.10|0.00|2.545|0.314|0.508|3.227|-0.166|
|2026-07-05 07:02:51|M/EUR|0.80|0.90|SCORE_BELOW_THRESHOLD|0.309|RISING 1.481|0.627|-0.10|0.00|2.329|-1.033|-0.091|2.329|-1.033|
|2026-07-05 07:08:02|M/EUR|0.75|0.85|SCORE_BELOW_THRESHOLD|0.344|FALLING -0.015|0.627|-0.10|0.00|1.475|-0.382|-0.619|2.329|-1.033|

## Strict Candidates With Worst 120m Drawdown

|LT time|pair|score|score-no-vol-penalty|reject|spread|PA|EMA gap|vol|volume|r30|r60|r120|max up|max down|
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
|2026-07-05 07:02:51|M/EUR|0.80|0.90|SCORE_BELOW_THRESHOLD|0.309|RISING 1.481|0.627|-0.10|0.00|2.329|-1.033|-0.091|2.329|-1.033|
|2026-07-05 07:08:02|M/EUR|0.75|0.85|SCORE_BELOW_THRESHOLD|0.344|FALLING -0.015|0.627|-0.10|0.00|1.475|-0.382|-0.619|2.329|-1.033|
|2026-07-05 06:47:18|M/EUR|0.75|0.85|SPREAD_TOO_WIDE|0.520|FALLING -1.098|0.431|-0.10|0.00|1.598|1.375|0.508|3.227|-0.166|
|2026-07-05 06:52:29|M/EUR|0.75|0.85|SPREAD_TOO_WIDE|0.509|FALLING -0.765|0.431|-0.10|0.00|2.170|0.821|0.508|3.227|-0.166|
|2026-07-05 06:57:40|M/EUR|0.75|0.85|SPREAD_TOO_WIDE|0.504|FALLING -0.372|0.431|-0.10|0.00|2.545|0.314|0.508|3.227|-0.166|

## Loose Candidates With Largest 120m Upside

|LT time|pair|score|score-no-vol-penalty|reject|spread|PA|EMA gap|vol|volume|r30|r60|r120|max up|max down|
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
|2026-07-04 15:39:26|XPL/EUR|0.85|0.85|INSUFFICIENT_PRICE_HISTORY|0.212|UNKNOWN n/a|0.370|0.05|0.00|1.786|2.731|6.197|6.723|-0.945|
|2026-07-04 15:44:37|XPL/EUR|0.85|0.85|EXPLORATORY_REQUIRES_POSITIVE_PRICE_ACTION|0.106|FALLING -0.945|0.370|0.05|0.00|1.471|3.256|6.828|6.723|-0.945|
|2026-07-04 15:34:15|XPL/EUR|0.85|0.85|INSUFFICIENT_PRICE_HISTORY|0.104|UNKNOWN n/a|0.370|0.05|0.00|-0.105|3.151|6.723|6.513|-0.945|
|2026-07-05 06:47:18|M/EUR|0.75|0.85|SPREAD_TOO_WIDE|0.520|FALLING -1.098|0.431|-0.10|0.00|1.598|1.375|0.508|3.227|-0.166|
|2026-07-05 06:52:29|M/EUR|0.75|0.85|SPREAD_TOO_WIDE|0.509|FALLING -0.765|0.431|-0.10|0.00|2.170|0.821|0.508|3.227|-0.166|
|2026-07-05 06:57:40|M/EUR|0.75|0.85|SPREAD_TOO_WIDE|0.504|FALLING -0.372|0.431|-0.10|0.00|2.545|0.314|0.508|3.227|-0.166|
|2026-07-05 07:02:51|M/EUR|0.80|0.90|SCORE_BELOW_THRESHOLD|0.309|RISING 1.481|0.627|-0.10|0.00|2.329|-1.033|-0.091|2.329|-1.033|
|2026-07-05 07:08:02|M/EUR|0.75|0.85|SCORE_BELOW_THRESHOLD|0.344|FALLING -0.015|0.627|-0.10|0.00|1.475|-0.382|-0.619|2.329|-1.033|
|2026-07-04 23:09:56|HBAR/EUR|0.74|0.74|NO_BULLISH_SIGNAL|0.015|RISING 0.092|0.259|0.05|0.00|-0.123|0.781|1.747|2.329|-0.123|
|2026-07-05 05:55:25|BCH/EUR|0.75|0.75|NO_BULLISH_SIGNAL|0.088|FALLING -1.220|0.267|0.05|0.05|0.418|0.753|1.632|2.211|-0.491|
|2026-07-04 23:15:08|HBAR/EUR|0.70|0.70|NO_BULLISH_SIGNAL|0.015|RISING 0.122|0.226|0.05|0.00|-0.413|0.718|1.100|2.032|-0.413|
|2026-07-04 23:25:31|HBAR/EUR|0.70|0.70|NO_BULLISH_SIGNAL|0.015|RISING 0.031|0.226|0.05|0.00|1.008|1.497|1.314|2.032|-0.413|
|2026-07-04 22:59:34|HBAR/EUR|0.79|0.79|NO_BULLISH_SIGNAL|0.015|FALLING -0.168|0.341|0.05|0.00|-0.290|0.657|2.001|2.001|-0.443|
|2026-07-05 00:03:33|HBAR/EUR|0.73|0.73|NO_BULLISH_SIGNAL|0.015|RISING 0.950|0.206|0.05|0.05|0.818|0.606|1.499|1.984|-0.409|
|2026-07-05 00:07:53|HBAR/EUR|0.73|0.73|NO_BULLISH_SIGNAL|0.015|RISING 0.921|0.206|0.05|0.05|0.666|0.545|1.666|1.984|-0.409|
|2026-07-05 00:13:08|HBAR/EUR|0.73|0.73|NO_BULLISH_SIGNAL|0.061|RISING 0.905|0.206|0.05|0.05|1.075|0.197|0.772|1.984|-0.182|
|2026-07-04 22:49:12|HBAR/EUR|0.79|0.79|NO_BULLISH_SIGNAL|0.015|FALLING -0.229|0.341|0.05|0.00|-0.290|-0.107|1.818|1.955|-0.443|
|2026-07-04 22:54:23|HBAR/EUR|0.79|0.79|NO_BULLISH_SIGNAL|0.015|FALLING -0.229|0.341|0.05|0.00|-0.290|0.978|2.001|1.955|-0.443|
|2026-07-04 19:17:03|XLM/EUR|0.85|0.85|CORRELATION_LIMIT|0.063|RISING 1.267|0.679|0.05|0.05|0.875|0.194|0.956|1.908|-0.158|
|2026-07-04 19:22:17|XLM/EUR|0.85|0.85|CORRELATION_LIMIT|0.097|RISING 1.989|0.679|0.05|0.05|0.491|0.335|0.523|1.908|-0.158|
|2026-07-04 19:27:28|XLM/EUR|0.85|0.85|CORRELATION_LIMIT|0.086|RISING 1.750|0.679|0.05|0.05|0.639|0.468|0.220|1.908|-0.158|
|2026-07-04 19:43:01|AVAX/EUR|0.70|0.70|SCORE_BELOW_THRESHOLD|0.016|RISING 0.016|0.468|0.05|0.00|0.295|0.968|1.050|1.870|-0.049|
|2026-07-04 19:48:13|AVAX/EUR|0.70|0.70|SCORE_BELOW_THRESHOLD|0.016|RISING 0.230|0.453|0.05|0.00|0.459|1.017|1.033|1.870|-0.049|
|2026-07-04 19:53:24|AVAX/EUR|0.70|0.70|SCORE_BELOW_THRESHOLD|0.016|RISING 0.328|0.453|0.05|0.00|0.427|1.870|1.033|1.870|-0.049|
|2026-07-04 19:54:53|AVAX/EUR|0.70|0.70|SCORE_BELOW_THRESHOLD|0.016|RISING 0.328|0.453|0.05|0.00|0.427|1.624|1.033|1.870|-0.049|

## Interpretation Rules

- A later price rise is not proof that the bot should have bought; spread, fees, slippage, and stop path still matter.
- If strict candidates show positive median/average forward returns across several days, investigate a conditional volatility-penalty softening.
- If strict candidates are mixed or negative, keep the current conservative gate.
- Do not lower `MinimumLongScore` globally from this report alone.
- Do not raise global spread limits from this report alone.

## Next Checks

1. Run this report on several daily exports, not only the M/EUR day.
2. Compare strict candidates against actual `WOULD_BUY` outcomes and stopped positions.
3. Add order-book depth/slippage diagnostics before relaxing spread handling.
