#!/usr/bin/env python3
"""How many trades before the trailing handoff is actually worth having?

The edge of a trailing stop over the fixed target it replaces is carried by a
minority of trades that run far. On a small number of trades that minority may
simply not show up, and the account sees only the give-back. This measures how
many armed trades it takes before trailing is more likely than not to be ahead,
and how wide the outcome still is at our real trade count.

Same simulation and assumptions as trailing-sweep.py; the armed returns it
produces are then bootstrapped.
"""

import random
import subprocess
import sys

TP = 0.04
SL = 0.02
HORIZON = 192
SAMPLE_EVERY = 96
SINCE = "2025-01-01"
TRAILS = [0.005, 0.02]
SAMPLE_SIZES = [5, 10, 25, 50, 100, 250, 500, 1000]
DRAWS = 20000
MARGIN_USD = 15.0   # MaxMarginPerPositionUsd
LEVERAGE = 10.0


def psql(query):
    out = subprocess.run(
        ["docker", "exec", "-i", "trading-bot-db", "psql", "-U", "tradingbot",
         "-d", "tradingbot_research", "-At", "-F", "\t", "-c", query],
        capture_output=True, text=True)
    if out.returncode != 0:
        raise RuntimeError(out.stderr.strip())
    return [line.split("\t") for line in out.stdout.splitlines() if line]


def simulate(highs, lows, closes, start, direction, trail):
    entry = closes[start]
    if entry <= 0:
        return None
    if direction > 0:
        stop_loss, arm_at = entry * (1 - SL), entry * (1 + TP)
    else:
        stop_loss, arm_at = entry * (1 + SL), entry * (1 - TP)
    armed, peak = False, entry
    for i in range(start + 1, min(start + 1 + HORIZON, len(closes))):
        high, low = highs[i], lows[i]
        if armed:
            trail_at = peak * (1 - trail) if direction > 0 else peak * (1 + trail)
            if (direction > 0 and low <= trail_at) or (direction < 0 and high >= trail_at):
                return (trail_at / entry - 1) * direction
            peak = max(peak, high) if direction > 0 else min(peak, low)
            continue
        if (direction > 0 and low <= stop_loss) or (direction < 0 and high >= stop_loss):
            return None
        if (direction > 0 and high >= arm_at) or (direction < 0 and low <= arm_at):
            armed = True
            peak = max(arm_at, high) if direction > 0 else min(arm_at, low)
            trail_at = peak * (1 - trail) if direction > 0 else peak * (1 + trail)
            if (direction > 0 and low <= trail_at) or (direction < 0 and high >= trail_at):
                return (trail_at / entry - 1) * direction
    if armed:
        last = closes[min(start + HORIZON, len(closes) - 1)]
        return (last / entry - 1) * direction
    return None


def main():
    direction = -1 if "--short" in sys.argv else 1
    symbols = [r[0] for r in psql(
        "select distinct symbol from kraken_candles where resolution='15m' order by symbol")]
    armed = {t: [] for t in TRAILS}

    for n, symbol in enumerate(symbols, 1):
        rows = psql(f"""select high, low, close from kraken_candles
                        where symbol='{symbol}' and resolution='15m'
                          and open_time >= '{SINCE}' order by open_time""")
        if len(rows) < HORIZON + 2:
            continue
        highs = [float(r[0]) for r in rows]
        lows = [float(r[1]) for r in rows]
        closes = [float(r[2]) for r in rows]
        for start in range(0, len(closes) - HORIZON - 1, SAMPLE_EVERY):
            for trail in TRAILS:
                r = simulate(highs, lows, closes, start, direction, trail)
                if r is not None:
                    armed[trail].append(r)
        if n % 100 == 0:
            print(f"  {n}/{len(symbols)} symbols", flush=True)

    random.seed(20260821)
    print(f"\n{'SHORT' if direction < 0 else 'LONG'} — armed trades simulated: "
          f"{len(armed[TRAILS[0]])}")
    print(f"one trade risks {MARGIN_USD:.0f} USD margin at {LEVERAGE:.0f}x, so "
          f"1 percentage point of price = {MARGIN_USD * LEVERAGE / 100:.2f} USD\n")

    for trail in TRAILS:
        pool = armed[trail]
        edge = sum(pool) / len(pool) - TP
        print(f"--- trail {trail:.2%}: mean exit {sum(pool)/len(pool):+.3%}, "
              f"edge over the fixed target {edge:+.3%} per armed trade "
              f"({edge * MARGIN_USD * LEVERAGE:+.3f} USD)")
        print(f"{'trades':>7}  {'P(trailing ahead)':>18}  {'median gain':>12}  "
              f"{'5th pct':>10}  {'95th pct':>10}")
        for n in SAMPLE_SIZES:
            totals = []
            for _ in range(DRAWS):
                s = sum(random.choice(pool) for _ in range(n))
                totals.append((s - TP * n) * MARGIN_USD * LEVERAGE)
            totals.sort()
            ahead = sum(1 for t in totals if t > 0) / DRAWS
            print(f"{n:>7}  {ahead:>17.1%}  {totals[DRAWS//2]:>+11.2f}$  "
                  f"{totals[int(DRAWS*0.05)]:>+9.2f}$  {totals[int(DRAWS*0.95)]:>+9.2f}$")
        print()


if __name__ == "__main__":
    main()
