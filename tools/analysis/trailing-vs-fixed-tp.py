#!/usr/bin/env python3
"""Does handing a winning trade to a trailing stop beat just taking the profit?

The bot arms a Kraken trailing stop when an open position reaches its working
take profit instead of closing at it. The exit is then decided entirely by the
highest price reached afterwards: the stop sits TRAIL below the running peak,
so the trade closes at peak * (1 - TRAIL). It therefore beats the fixed target
only when the peak goes past (1 + TP) / (1 - TRAIL) - with TP 4% and TRAIL 2%
that is +6.12%, not +4%. Everything between +4% and +6.12% is a worse exit than
simply taking the target, and the floor once armed is +1.92%.

This walks real Kraken candles to measure how often that happens.

Assumptions, stated because they change the answer:
  - Entry at a candle close, sampled every SAMPLE_EVERY candles so that
    overlapping windows do not count the same move many times.
  - Within one candle the adverse extreme is assumed to come first. A candle
    that could both arm the stop and trigger it is resolved against us.
  - No fees, no funding, no slippage. Both strategies pay the same round trip
    (~0.1% of notional), so the comparison between them is unaffected; the
    absolute numbers are that much optimistic.
  - Price returns, not margin. At 10x leverage every figure multiplies by ten.
"""

import os
import subprocess
import sys
from collections import defaultdict

TP = 0.04          # working take profit: where the trailing stop is armed
TRAIL = 0.02       # trailing distance below the running peak
SL = 0.02          # working stop loss
HORIZON = 192      # 48 hours of 15m candles
SAMPLE_EVERY = 96  # one hypothetical entry per day per pair
SINCE = "2025-01-01"

BREAK_EVEN = (1 + TP) / (1 - TRAIL) - 1  # peak needed for trailing to win


def psql(query):
    out = subprocess.run(
        ["docker", "exec", "-i", "trading-bot-db", "psql", "-U", "tradingbot",
         "-d", "tradingbot_research", "-At", "-F", "\t", "-c", query],
        capture_output=True, text=True)
    if out.returncode != 0:
        raise RuntimeError(out.stderr.strip())
    return [line.split("\t") for line in out.stdout.splitlines() if line]


def simulate(highs, lows, closes, start, direction):
    """Walk one hypothetical trade forward. Returns (outcome, return, peak)."""
    entry = closes[start]
    if entry <= 0:
        return None

    if direction > 0:
        stop_loss, arm_at = entry * (1 - SL), entry * (1 + TP)
    else:
        stop_loss, arm_at = entry * (1 + SL), entry * (1 - TP)

    armed = False
    peak = entry

    for i in range(start + 1, min(start + 1 + HORIZON, len(closes))):
        high, low = highs[i], lows[i]

        if armed:
            # Adverse extreme first: a candle that both extends the peak and
            # takes out the stop is resolved as a stop-out at the old peak.
            trail_at = peak * (1 - TRAIL) if direction > 0 else peak * (1 + TRAIL)
            if (direction > 0 and low <= trail_at) or (direction < 0 and high >= trail_at):
                return ("TRAILED", (trail_at / entry - 1) * direction, (peak / entry - 1) * direction)
            peak = max(peak, high) if direction > 0 else min(peak, low)
            continue

        if (direction > 0 and low <= stop_loss) or (direction < 0 and high >= stop_loss):
            return ("STOPPED", -SL, 0.0)

        if (direction > 0 and high >= arm_at) or (direction < 0 and low <= arm_at):
            armed = True
            peak = max(entry * (1 + TP), high) if direction > 0 else min(entry * (1 - TP), low)
            trail_at = peak * (1 - TRAIL) if direction > 0 else peak * (1 + TRAIL)
            if (direction > 0 and low <= trail_at) or (direction < 0 and high >= trail_at):
                return ("TRAILED", (trail_at / entry - 1) * direction, (peak / entry - 1) * direction)

    last = closes[min(start + HORIZON, len(closes) - 1)]
    return ("OPEN" if not armed else "OPEN_ARMED", (last / entry - 1) * direction,
            (peak / entry - 1) * direction)


def main():
    direction = -1 if "--short" in sys.argv else 1
    symbols = [r[0] for r in psql(
        "select distinct symbol from kraken_candles where resolution='15m' order by symbol")]
    print(f"{len(symbols)} symbols, direction={'SHORT' if direction < 0 else 'LONG'}, "
          f"TP {TP:.0%} / trail {TRAIL:.0%} / SL {SL:.0%}, horizon {HORIZON} candles, "
          f"entry every {SAMPLE_EVERY}", flush=True)
    print(f"trailing beats the fixed target only above a peak of {BREAK_EVEN:+.2%}", flush=True)

    counts = defaultdict(int)
    trailed_returns, fixed_returns, all_trailing, all_fixed = [], [], [], []
    beat, tied, lost = 0, 0, 0

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
            result = simulate(highs, lows, closes, start, direction)
            if result is None:
                continue
            outcome, ret, peak = result
            counts[outcome] += 1

            # What the fixed target would have done on the same path.
            fixed = TP if outcome in ("TRAILED", "OPEN_ARMED") else ret
            all_trailing.append(ret)
            all_fixed.append(fixed)

            if outcome in ("TRAILED", "OPEN_ARMED"):
                trailed_returns.append(ret)
                fixed_returns.append(TP)
                if ret > TP + 1e-9:
                    beat += 1
                elif ret < TP - 1e-9:
                    lost += 1
                else:
                    tied += 1

        if n % 50 == 0:
            print(f"  {n}/{len(symbols)} symbols, {sum(counts.values())} entries", flush=True)

    total = sum(counts.values())
    armed = len(trailed_returns)

    def mean(xs):
        return sum(xs) / len(xs) if xs else 0.0

    def median(xs):
        if not xs:
            return 0.0
        s = sorted(xs)
        m = len(s) // 2
        return s[m] if len(s) % 2 else (s[m - 1] + s[m]) / 2

    print("\n=== entries ===")
    for key in ("STOPPED", "TRAILED", "OPEN_ARMED", "OPEN"):
        print(f"  {key:<12} {counts[key]:>8}  {counts[key] / total:6.2%}")
    print(f"  {'TOTAL':<12} {total:>8}")

    print(f"\n=== of the {armed} that reached +{TP:.0%} and armed the trail ===")
    print(f"  trailing exit BEAT the fixed target : {beat:>8}  {beat / armed:6.2%}")
    print(f"  trailing exit LOST to it            : {lost:>8}  {lost / armed:6.2%}")
    print(f"  identical                           : {tied:>8}  {tied / armed:6.2%}")
    print(f"  mean exit, trailing : {mean(trailed_returns):+.3%}   vs fixed {TP:+.2%}")
    print(f"  median exit, trailing: {median(trailed_returns):+.3%}")

    print("\n=== expectancy over every entry, both strategies on the same paths ===")
    print(f"  trailing : mean {mean(all_trailing):+.4%}  median {median(all_trailing):+.4%}")
    print(f"  fixed TP : mean {mean(all_fixed):+.4%}  median {median(all_fixed):+.4%}")
    edge = mean(all_trailing) - mean(all_fixed)
    print(f"  edge of trailing: {edge:+.4%} per trade in price "
          f"({edge * 10:+.3%} on margin at 10x)")


if __name__ == "__main__":
    main()
