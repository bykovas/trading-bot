#!/usr/bin/env python3
"""Sweep the trailing distance against the fixed target it replaces.

Same model and assumptions as trailing-vs-fixed-tp.py, but every variant is
simulated on the same paths in one pass, so the comparison between them carries
no sampling difference at all.

The break-even peak for a variant is (1 + tp) / (1 - trail) - 1. A tight trail
narrows the band where it loses to the target, but it also exits sooner: the
floor once armed is (1 + tp) * (1 - trail) - 1. The question is which of the two
effects the market actually pays for.
"""

import subprocess
import sys
from collections import defaultdict

TP = 0.04
SL = 0.02
HORIZON = 192
SAMPLE_EVERY = 96
SINCE = "2025-01-01"
TRAILS = [0.005, 0.0075, 0.01, 0.015, 0.02, 0.025, 0.03]


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

    armed = False
    peak = entry
    for i in range(start + 1, min(start + 1 + HORIZON, len(closes))):
        high, low = highs[i], lows[i]
        if armed:
            trail_at = peak * (1 - trail) if direction > 0 else peak * (1 + trail)
            if (direction > 0 and low <= trail_at) or (direction < 0 and high >= trail_at):
                return ("TRAILED", (trail_at / entry - 1) * direction)
            peak = max(peak, high) if direction > 0 else min(peak, low)
            continue
        if (direction > 0 and low <= stop_loss) or (direction < 0 and high >= stop_loss):
            return ("STOPPED", -SL)
        if (direction > 0 and high >= arm_at) or (direction < 0 and low <= arm_at):
            armed = True
            peak = max(arm_at, high) if direction > 0 else min(arm_at, low)
            trail_at = peak * (1 - trail) if direction > 0 else peak * (1 + trail)
            if (direction > 0 and low <= trail_at) or (direction < 0 and high >= trail_at):
                return ("TRAILED", (trail_at / entry - 1) * direction)
    last = closes[min(start + HORIZON, len(closes) - 1)]
    return ("OPEN_ARMED" if armed else "OPEN", (last / entry - 1) * direction)


def main():
    direction = -1 if "--short" in sys.argv else 1
    symbols = [r[0] for r in psql(
        "select distinct symbol from kraken_candles where resolution='15m' order by symbol")]
    print(f"{len(symbols)} symbols, {'SHORT' if direction < 0 else 'LONG'}, "
          f"arm at {TP:.0%}, SL {SL:.0%}, horizon {HORIZON}, entry every {SAMPLE_EVERY}", flush=True)

    stats = {t: defaultdict(float) for t in TRAILS}
    armed_returns = {t: [] for t in TRAILS}
    all_returns = {t: [] for t in TRAILS}
    fixed_returns = []
    entries = 0

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
            if closes[start] <= 0:
                continue
            entries += 1
            fixed_done = False
            for trail in TRAILS:
                outcome, ret = simulate(highs, lows, closes, start, direction, trail)
                stats[trail][outcome] += 1
                all_returns[trail].append(ret)
                if outcome in ("TRAILED", "OPEN_ARMED"):
                    armed_returns[trail].append(ret)
                    if not fixed_done:
                        fixed_returns.append(TP)
                        fixed_done = True
                elif not fixed_done and trail == TRAILS[-1]:
                    fixed_returns.append(ret)
        if n % 100 == 0:
            print(f"  {n}/{len(symbols)} symbols, {entries} entries", flush=True)

    def mean(xs):
        return sum(xs) / len(xs) if xs else 0.0

    def median(xs):
        if not xs:
            return 0.0
        s = sorted(xs)
        m = len(s) // 2
        return s[m] if len(s) % 2 else (s[m - 1] + s[m]) / 2

    print(f"\nentries: {entries}\n")
    header = ("trail  breakeven   floor    armed   beat_TP   mean_exit   median_exit"
              "   mean_all   edge_vs_fixed_TP")
    print(header)
    print("-" * len(header))
    base = mean(fixed_returns)
    for trail in TRAILS:
        armed = armed_returns[trail]
        beat = sum(1 for r in armed if r > TP + 1e-9)
        breakeven = (1 + TP) / (1 - trail) - 1
        floor = (1 + TP) * (1 - trail) - 1
        print(f"{trail:5.2%}  {breakeven:+8.2%}  {floor:+6.2%}  {len(armed):7d}"
              f"  {beat / len(armed):7.1%}  {mean(armed):+9.3%}  {median(armed):+11.3%}"
              f"  {mean(all_returns[trail]):+8.4%}  {mean(all_returns[trail]) - base:+8.4%}")
    print(f"\nfixed take profit at {TP:.0%} on the same paths: mean {base:+.4%}")
    print("edge is per entry, in price. At 10x leverage multiply by ten for margin.")


if __name__ == "__main__":
    main()
