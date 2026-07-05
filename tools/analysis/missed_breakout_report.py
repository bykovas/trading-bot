#!/usr/bin/env python3
"""
Build a replay-style report for near-threshold breakout candidates.

Input is the combined API export from:
  /api/export/cycles-and-snapshots.csv

The report intentionally does not optimize strategy constants. It finds classes
of rejected candidates and measures what happened after the rejection.
"""

from __future__ import annotations

import argparse
import csv
import json
import math
import re
from bisect import bisect_left
from collections import Counter, defaultdict
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from statistics import mean, median
from typing import Any


LT_OFFSET_HOURS = 3
FORWARD_MINUTES = (15, 30, 60, 120)
EMA_GAP_RE = re.compile(r"(?:gap|by)\s+(-?\d+(?:\.\d+)?)%", re.IGNORECASE)


@dataclass(frozen=True)
class Snapshot:
    utc: datetime
    pair: str
    bid: float | None
    ask: float | None
    last: float
    volume24h: float | None
    change_percent: float | None


@dataclass
class Candidate:
    utc: datetime
    cycle_id: str
    pair: str
    price: float
    score: float
    desired_position: str
    action: str
    rejection: str
    spread_percent: float | None
    price_action_direction: str | None
    price_action_trend_percent: float | None
    fast_ema: float | None
    slow_ema: float | None
    ema_gap_percent: float | None
    ema_confirmed: bool
    contributions: dict[str, float]
    contribution_reasons: dict[str, str]
    future_returns: dict[int, float | None]
    max_up_120m: float | None
    max_down_120m: float | None

    @property
    def volatility_penalty_active(self) -> bool:
        return self.contributions.get("Volatility", 0.0) < 0

    @property
    def volume_weak_or_missing(self) -> bool:
        volume = self.contributions.get("Volume")
        cap = self.contributions.get("VolumeCap", 0.0)
        reason = self.contribution_reasons.get("Volume", "").lower()
        return volume is None or volume <= 0 or cap < 0 or "no volume confirmation" in reason

    @property
    def momentum_positive(self) -> bool:
        return self.contributions.get("Momentum", 0.0) > 0

    @property
    def trend_positive(self) -> bool:
        return self.contributions.get("Trend", 0.0) > 0

    @property
    def rsi_positive(self) -> bool:
        return self.contributions.get("RSI", 0.0) > 0

    @property
    def score_without_volatility_penalty(self) -> float:
        volatility = self.contributions.get("Volatility", 0.0)
        return self.score - volatility if volatility < 0 else self.score


def parse_utc(value: str) -> datetime:
    normalized = value.strip().replace("Z", "+00:00")
    # Normalize .NET fractional seconds to Python's expected 6-digit microseconds.
    match = re.match(r"^(.*?\.)(\d+)([+-]\d\d:\d\d)$", normalized)
    if match:
        normalized = f"{match.group(1)}{match.group(2)[:6].ljust(6, '0')}{match.group(3)}"
    parsed = datetime.fromisoformat(normalized)
    if parsed.tzinfo is None:
        parsed = parsed.replace(tzinfo=timezone.utc)
    return parsed.astimezone(timezone.utc)


def to_lt(value: datetime) -> str:
    # The analyzed July data is in Lithuania summer time (UTC+03).
    local_ts = value.timestamp() + LT_OFFSET_HOURS * 3600
    return datetime.fromtimestamp(local_ts, tz=timezone.utc).strftime("%Y-%m-%d %H:%M:%S")


def parse_float(value: Any) -> float | None:
    if value is None:
        return None
    if isinstance(value, (int, float)):
        result = float(value)
    else:
        text = str(value).strip()
        if not text:
            return None
        result = float(text)
    return result if math.isfinite(result) else None


def read_export(path: Path) -> tuple[list[dict[str, Any]], dict[str, list[Snapshot]]]:
    cycles: list[dict[str, Any]] = []
    snapshots_by_pair: dict[str, list[Snapshot]] = defaultdict(list)

    with path.open("r", encoding="utf-8-sig", newline="") as handle:
        reader = csv.DictReader(handle)
        for row in reader:
            record_type = (row.get("record_type") or "").strip().strip('"')
            if record_type in {"snapshot", "market_snapshot"}:
                last = parse_float(row.get("last"))
                if last is None:
                    continue
                snapshot = Snapshot(
                    utc=parse_utc(row["utc"]),
                    pair=row["pair"],
                    bid=parse_float(row.get("bid")),
                    ask=parse_float(row.get("ask")),
                    last=last,
                    volume24h=parse_float(row.get("volume24h")),
                    change_percent=parse_float(row.get("change_percent")),
                )
                snapshots_by_pair[snapshot.pair].append(snapshot)
            elif record_type == "cycle":
                record_json = row.get("record_json")
                if not record_json:
                    continue
                try:
                    cycle = json.loads(record_json)
                except json.JSONDecodeError:
                    continue
                cycles.append(cycle)

    for snapshots in snapshots_by_pair.values():
        snapshots.sort(key=lambda item: item.utc)

    return cycles, snapshots_by_pair


def contribution_maps(decision: dict[str, Any]) -> tuple[dict[str, float], dict[str, str]]:
    values: dict[str, float] = {}
    reasons: dict[str, str] = {}
    for item in decision.get("contributions") or []:
        name = str(item.get("name") or "")
        if not name:
            continue
        value = parse_float(item.get("value"))
        if value is not None:
            values[name] = value
        reasons[name] = str(item.get("reason") or "")
    return values, reasons


def extract_ema_gap(decision: dict[str, Any], contributions: dict[str, float], reasons: dict[str, str]) -> tuple[float | None, bool]:
    fast = parse_float(decision.get("fastEma"))
    slow = parse_float(decision.get("slowEma"))
    if fast is not None and slow is not None and slow > 0:
        gap = ((fast - slow) / slow) * 100.0
        return gap, contributions.get("EMA", 0.0) > 0

    reason = reasons.get("EMA", "")
    match = EMA_GAP_RE.search(reason)
    if match:
        gap = parse_float(match.group(1))
        return gap, contributions.get("EMA", 0.0) > 0

    return None, contributions.get("EMA", 0.0) > 0


def find_price_at_or_after(snapshots: list[Snapshot], utc: datetime, minutes: int) -> float | None:
    target_ts = utc.timestamp() + minutes * 60
    timestamps = [item.utc.timestamp() for item in snapshots]
    index = bisect_left(timestamps, target_ts)
    if index >= len(snapshots):
        return None
    return snapshots[index].last


def future_range(snapshots: list[Snapshot], utc: datetime, minutes: int) -> list[float]:
    start_ts = utc.timestamp()
    end_ts = start_ts + minutes * 60
    return [item.last for item in snapshots if start_ts < item.utc.timestamp() <= end_ts]


def pct_return(start: float, end: float | None) -> float | None:
    if end is None or start <= 0:
        return None
    return ((end - start) / start) * 100.0


def build_candidates(cycles: list[dict[str, Any]], snapshots_by_pair: dict[str, list[Snapshot]]) -> list[Candidate]:
    candidates: list[Candidate] = []
    for cycle in cycles:
        cycle_id = str(cycle.get("cycleId") or "")
        utc = parse_utc(str(cycle.get("utc") or ""))
        for decision in cycle.get("decisions") or []:
            dry_run_action = decision.get("dryRunAction") or {}
            action = str(dry_run_action.get("action") or "")
            rejection = str(decision.get("entryRejectionReason") or "")
            if action not in {"NO_ORDER", "WOULD_BUY_BLOCKED"}:
                continue
            if not rejection:
                continue

            price = parse_float(decision.get("price"))
            score = parse_float(decision.get("score"))
            pair = str(decision.get("pair") or "")
            if price is None or score is None or not pair:
                continue

            contributions, reasons = contribution_maps(decision)
            ema_gap, ema_confirmed = extract_ema_gap(decision, contributions, reasons)
            snapshots = snapshots_by_pair.get(pair, [])
            returns = {
                minutes: pct_return(price, find_price_at_or_after(snapshots, utc, minutes))
                for minutes in FORWARD_MINUTES
            }
            future_prices = future_range(snapshots, utc, 120)
            max_up = pct_return(price, max(future_prices)) if future_prices else None
            max_down = pct_return(price, min(future_prices)) if future_prices else None

            candidates.append(
                Candidate(
                    utc=utc,
                    cycle_id=cycle_id,
                    pair=pair,
                    price=price,
                    score=score,
                    desired_position=str(decision.get("desiredPosition") or ""),
                    action=action,
                    rejection=rejection,
                    spread_percent=parse_float(decision.get("spreadPercent")),
                    price_action_direction=decision.get("priceActionDirection"),
                    price_action_trend_percent=parse_float(decision.get("priceActionTrendPercent")),
                    fast_ema=parse_float(decision.get("fastEma")),
                    slow_ema=parse_float(decision.get("slowEma")),
                    ema_gap_percent=ema_gap,
                    ema_confirmed=ema_confirmed,
                    contributions=contributions,
                    contribution_reasons=reasons,
                    future_returns=returns,
                    max_up_120m=max_up,
                    max_down_120m=max_down,
                )
            )
    return candidates


def is_near_breakout(candidate: Candidate) -> bool:
    return (
        0.75 <= candidate.score <= 0.85
        and candidate.ema_confirmed
        and candidate.rsi_positive
        and candidate.momentum_positive
        and candidate.trend_positive
        and candidate.volatility_penalty_active
        and candidate.volume_weak_or_missing
    )


def is_loose_breakout(candidate: Candidate) -> bool:
    positives = sum(
        [
            candidate.ema_confirmed,
            candidate.rsi_positive,
            candidate.momentum_positive,
            candidate.trend_positive,
            candidate.price_action_direction == "RISING",
        ]
    )
    return 0.70 <= candidate.score <= 0.85 and positives >= 3


def fmt(value: Any, digits: int = 3) -> str:
    if value is None:
        return "n/a"
    if isinstance(value, bool):
        return "yes" if value else "no"
    if isinstance(value, (int, float)):
        if not math.isfinite(value):
            return "n/a"
        return f"{value:.{digits}f}"
    return str(value)


def summarize_returns(candidates: list[Candidate], minutes: int) -> str:
    returns = [candidate.future_returns.get(minutes) for candidate in candidates]
    values = [value for value in returns if value is not None]
    if not values:
        return "n/a"
    winners = sum(1 for value in values if value > 0)
    strong = sum(1 for value in values if value >= 1.0)
    weak = sum(1 for value in values if value <= -1.0)
    return (
        f"n={len(values)}, win={winners / len(values) * 100:.1f}%, "
        f">=+1%={strong}, <=-1%={weak}, avg={mean(values):.3f}%, med={median(values):.3f}%"
    )


def markdown_table(candidates: list[Candidate], limit: int) -> str:
    headers = [
        "LT time",
        "pair",
        "score",
        "score-no-vol-penalty",
        "reject",
        "spread",
        "PA",
        "EMA gap",
        "vol",
        "volume",
        "r30",
        "r60",
        "r120",
        "max up",
        "max down",
    ]
    lines = ["|" + "|".join(headers) + "|", "|" + "|".join(["---"] * len(headers)) + "|"]
    for candidate in candidates[:limit]:
        row = [
            to_lt(candidate.utc),
            candidate.pair,
            fmt(candidate.score, 2),
            fmt(candidate.score_without_volatility_penalty, 2),
            candidate.rejection.replace("REJECT_", ""),
            fmt(candidate.spread_percent, 3),
            f"{candidate.price_action_direction or 'n/a'} {fmt(candidate.price_action_trend_percent, 3)}",
            fmt(candidate.ema_gap_percent, 3),
            fmt(candidate.contributions.get("Volatility"), 2),
            fmt(candidate.contributions.get("Volume"), 2),
            fmt(candidate.future_returns.get(30), 3),
            fmt(candidate.future_returns.get(60), 3),
            fmt(candidate.future_returns.get(120), 3),
            fmt(candidate.max_up_120m, 3),
            fmt(candidate.max_down_120m, 3),
        ]
        lines.append("|" + "|".join(row) + "|")
    return "\n".join(lines)


def write_candidates_csv(path: Path, candidates: list[Candidate]) -> None:
    with path.open("w", encoding="utf-8", newline="") as handle:
        writer = csv.writer(handle)
        writer.writerow(
            [
                "time_lt",
                "time_utc",
                "cycle_id",
                "pair",
                "price",
                "score",
                "score_without_volatility_penalty",
                "action",
                "rejection",
                "spread_percent",
                "price_action_direction",
                "price_action_trend_percent",
                "ema_confirmed",
                "ema_gap_percent",
                "rsi",
                "ema",
                "momentum",
                "trend",
                "volume",
                "volatility",
                "volume_cap",
                "return_15m",
                "return_30m",
                "return_60m",
                "return_120m",
                "max_up_120m",
                "max_down_120m",
            ]
        )
        for candidate in candidates:
            writer.writerow(
                [
                    to_lt(candidate.utc),
                    candidate.utc.isoformat(),
                    candidate.cycle_id,
                    candidate.pair,
                    candidate.price,
                    candidate.score,
                    candidate.score_without_volatility_penalty,
                    candidate.action,
                    candidate.rejection,
                    candidate.spread_percent,
                    candidate.price_action_direction,
                    candidate.price_action_trend_percent,
                    candidate.ema_confirmed,
                    candidate.ema_gap_percent,
                    candidate.contributions.get("RSI"),
                    candidate.contributions.get("EMA"),
                    candidate.contributions.get("Momentum"),
                    candidate.contributions.get("Trend"),
                    candidate.contributions.get("Volume"),
                    candidate.contributions.get("Volatility"),
                    candidate.contributions.get("VolumeCap"),
                    candidate.future_returns.get(15),
                    candidate.future_returns.get(30),
                    candidate.future_returns.get(60),
                    candidate.future_returns.get(120),
                    candidate.max_up_120m,
                    candidate.max_down_120m,
                ]
            )


def write_report(path: Path, export_path: Path, cycles: list[dict[str, Any]], candidates: list[Candidate], near: list[Candidate], loose: list[Candidate]) -> None:
    all_rejections = Counter(candidate.rejection for candidate in candidates)
    near_by_pair = Counter(candidate.pair for candidate in near)
    loose_by_pair = Counter(candidate.pair for candidate in loose)
    top_near_up = sorted(near, key=lambda item: item.max_up_120m if item.max_up_120m is not None else -999, reverse=True)
    top_near_down = sorted(near, key=lambda item: item.max_down_120m if item.max_down_120m is not None else 999)
    top_loose_up = sorted(loose, key=lambda item: item.max_up_120m if item.max_up_120m is not None else -999, reverse=True)

    times = [parse_utc(str(cycle.get("utc"))) for cycle in cycles if cycle.get("utc")]
    time_range = f"{to_lt(min(times))} -> {to_lt(max(times))}" if times else "n/a"

    lines = [
        "# Missed Breakout Candidate Report",
        "",
        "This is a replay/regression analysis artifact. It does not change trading behavior and should not be used to tune one coin or one day.",
        "",
        "## Scope",
        "",
        f"- source CSV: `{export_path}`",
        f"- cycles: `{len(cycles)}`",
        f"- rejected/blocked candidate rows inspected: `{len(candidates)}`",
        f"- LT time range: `{time_range}`",
        "",
        "## Candidate Definitions",
        "",
        "Strict near-breakout candidate:",
        "",
        "```text",
        "0.75 <= final score <= 0.85",
        "EMA confirmed",
        "RSI contribution > 0",
        "Momentum contribution > 0",
        "Trend contribution > 0",
        "Volatility contribution < 0",
        "Volume weak/missing or VolumeCap active",
        "```",
        "",
        "Loose candidate:",
        "",
        "```text",
        "0.70 <= final score <= 0.85",
        "at least 3 of: EMA confirmed, RSI positive, Momentum positive, Trend positive, PA RISING",
        "```",
        "",
        "## Headline Counts",
        "",
        f"- strict near-breakout candidates: `{len(near)}`",
        f"- loose candidates: `{len(loose)}`",
        f"- top strict pairs: `{', '.join(f'{pair} x{count}' for pair, count in near_by_pair.most_common(8)) or 'n/a'}`",
        f"- top loose pairs: `{', '.join(f'{pair} x{count}' for pair, count in loose_by_pair.most_common(8)) or 'n/a'}`",
        "",
        "Top rejection reasons among inspected rows:",
        "",
        "```text",
        *[f"{reason}: {count}" for reason, count in all_rejections.most_common(12)],
        "```",
        "",
        "## Initial Read",
        "",
        initial_read(near, loose),
        "",
        "## Forward Return Summary",
        "",
        "Strict near-breakouts:",
        "",
        "```text",
        *[f"{minutes}m: {summarize_returns(near, minutes)}" for minutes in FORWARD_MINUTES],
        "```",
        "",
        "Loose candidates:",
        "",
        "```text",
        *[f"{minutes}m: {summarize_returns(loose, minutes)}" for minutes in FORWARD_MINUTES],
        "```",
        "",
        "## Strict Candidates With Largest 120m Upside",
        "",
        markdown_table(top_near_up, 20) if top_near_up else "No strict candidates found.",
        "",
        "## Strict Candidates With Worst 120m Drawdown",
        "",
        markdown_table(top_near_down, 20) if top_near_down else "No strict candidates found.",
        "",
        "## Loose Candidates With Largest 120m Upside",
        "",
        markdown_table(top_loose_up, 25) if top_loose_up else "No loose candidates found.",
        "",
        "## Interpretation Rules",
        "",
        "- A later price rise is not proof that the bot should have bought; spread, fees, slippage, and stop path still matter.",
        "- If strict candidates show positive median/average forward returns across several days, investigate a conditional volatility-penalty softening.",
        "- If strict candidates are mixed or negative, keep the current conservative gate.",
        "- Do not lower `MinimumLongScore` globally from this report alone.",
        "- Do not raise global spread limits from this report alone.",
        "",
        "## Next Checks",
        "",
        "1. Run this report on several daily exports, not only the M/EUR day.",
        "2. Compare strict candidates against actual `WOULD_BUY` outcomes and stopped positions.",
        "3. Add order-book depth/slippage diagnostics before relaxing spread handling.",
    ]
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def initial_read(near: list[Candidate], loose: list[Candidate]) -> str:
    lines: list[str] = []
    if near:
        pair_count = Counter(candidate.pair for candidate in near)
        if len(pair_count) == 1:
            pair = next(iter(pair_count))
            lines.append(f"- Strict near-breakouts are all `{pair}` in this export, so this is still a single-setup sample.")
        r30 = [candidate.future_returns.get(30) for candidate in near if candidate.future_returns.get(30) is not None]
        r120 = [candidate.future_returns.get(120) for candidate in near if candidate.future_returns.get(120) is not None]
        max_down = [candidate.max_down_120m for candidate in near if candidate.max_down_120m is not None]
        if r30 and r120:
            lines.append(
                f"- Strict candidates looked strongest at 30m (median `{median(r30):.3f}%`) "
                f"but faded by 120m (median `{median(r120):.3f}%`)."
            )
        if max_down:
            lines.append(f"- Worst 120m path drawdown among strict candidates was `{min(max_down):.3f}%` before fees/spread.")
    else:
        lines.append("- No strict near-breakout candidates were found.")

    loose_120 = [candidate.future_returns.get(120) for candidate in loose if candidate.future_returns.get(120) is not None]
    if loose_120:
        lines.append(
            f"- Loose candidates are not an obvious edge: 120m median `{median(loose_120):.3f}%`, "
            f"average `{mean(loose_120):.3f}%`."
        )
    lines.append("- Current evidence supports more diagnostics, not a live threshold/spread loosening.")
    return "\n".join(lines)


def main() -> int:
    parser = argparse.ArgumentParser(description="Build missed breakout candidate report from trading-bot CSV export.")
    parser.add_argument("csv", type=Path, help="Combined cycles/snapshots CSV export")
    parser.add_argument("--report", type=Path, default=Path("docs/analysis/2026-07-05-missed-breakout-candidates.md"))
    parser.add_argument("--candidates-csv", type=Path, default=Path("docs/analysis/2026-07-05-missed-breakout-candidates.csv"))
    args = parser.parse_args()

    cycles, snapshots_by_pair = read_export(args.csv)
    candidates = build_candidates(cycles, snapshots_by_pair)
    near = [candidate for candidate in candidates if is_near_breakout(candidate)]
    loose = [candidate for candidate in candidates if is_loose_breakout(candidate)]

    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.candidates_csv.parent.mkdir(parents=True, exist_ok=True)
    write_report(args.report, args.csv, cycles, candidates, near, loose)
    write_candidates_csv(args.candidates_csv, near)

    print(f"cycles: {len(cycles)}")
    print(f"inspected rejected/blocked rows: {len(candidates)}")
    print(f"strict near-breakout candidates: {len(near)}")
    print(f"loose candidates: {len(loose)}")
    print(f"report: {args.report}")
    print(f"strict candidates csv: {args.candidates_csv}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
