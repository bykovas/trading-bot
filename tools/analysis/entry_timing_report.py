#!/usr/bin/env python3
"""
Analyze whether dry-run entries happen near local peaks.

Input is the combined API export from:
  /api/export/cycles-and-snapshots.csv

The report is diagnostic only. It uses persisted cycle decisions and ticker
snapshots; it does not change strategy settings.
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


FORWARD_MINUTES = (15, 30, 60, 120)
EARLY_LOOKBACK_MINUTES = 120
PEAK_WINDOW_MINUTES = 60


@dataclass(frozen=True)
class Snapshot:
    utc: datetime
    pair: str
    last: float


@dataclass(frozen=True)
class Decision:
    utc: datetime
    cycle_id: str
    pair: str
    action: str
    rejection: str | None
    price: float
    score: float
    desired_position: str | None
    spread_percent: float | None
    price_action_direction: str | None
    price_action_trend_percent: float | None
    has_bullish_structure: bool
    ema_fully_confirmed: bool
    bullish_ema_gap_percent: float | None
    ema_gap_velocity_percent: float | None
    early_entry_eligible: bool


def parse_utc(value: str) -> datetime:
    normalized = value.strip().replace("Z", "+00:00")
    match = re.match(r"^(.*?\.)(\d+)([+-]\d\d:\d\d)$", normalized)
    if match:
        normalized = f"{match.group(1)}{match.group(2)[:6].ljust(6, '0')}{match.group(3)}"
    parsed = datetime.fromisoformat(normalized)
    if parsed.tzinfo is None:
        parsed = parsed.replace(tzinfo=timezone.utc)
    return parsed.astimezone(timezone.utc)


def parse_float(value: Any) -> float | None:
    if value is None:
        return None
    if isinstance(value, (float, int)):
        result = float(value)
    else:
        text = str(value).strip()
        if not text:
            return None
        result = float(text)
    return result if math.isfinite(result) else None


def parse_bool(value: Any) -> bool:
    if isinstance(value, bool):
        return value
    return str(value or "").strip().lower() == "true"


def read_export(path: Path) -> tuple[list[Decision], dict[str, list[Snapshot]]]:
    decisions: list[Decision] = []
    snapshots_by_pair: dict[str, list[Snapshot]] = defaultdict(list)

    with path.open("r", encoding="utf-8-sig", newline="") as handle:
        reader = csv.DictReader(handle)
        for row in reader:
            record_type = (row.get("record_type") or "").strip().strip('"')
            if record_type in {"snapshot", "market_snapshot"}:
                last = parse_float(row.get("last"))
                pair = (row.get("pair") or "").strip()
                if last is None or not pair:
                    continue
                snapshots_by_pair[pair].append(Snapshot(parse_utc(row["utc"]), pair, last))
                continue

            if record_type != "cycle" or not row.get("record_json"):
                continue

            cycle = json.loads(row["record_json"])
            utc = parse_utc(str(cycle.get("utc") or row.get("utc") or ""))
            cycle_id = str(cycle.get("cycleId") or row.get("cycle_id") or "")
            for decision in cycle.get("decisions") or []:
                dry = decision.get("dryRunAction") or {}
                price = parse_float(decision.get("price"))
                score = parse_float(decision.get("score"))
                pair = str(decision.get("pair") or "")
                if not pair or price is None or score is None:
                    continue
                decisions.append(Decision(
                    utc=utc,
                    cycle_id=cycle_id,
                    pair=pair,
                    action=str(dry.get("action") or ""),
                    rejection=decision.get("entryRejectionReason") or dry.get("holdReasonCode"),
                    price=price,
                    score=score,
                    desired_position=decision.get("desiredPosition"),
                    spread_percent=parse_float(decision.get("spreadPercent")),
                    price_action_direction=decision.get("priceActionDirection"),
                    price_action_trend_percent=parse_float(decision.get("priceActionTrendPercent")),
                    has_bullish_structure=parse_bool(decision.get("hasBullishStructure")),
                    ema_fully_confirmed=parse_bool(decision.get("emaFullyConfirmed")),
                    bullish_ema_gap_percent=parse_float(decision.get("bullishEmaGapPercent")),
                    ema_gap_velocity_percent=parse_float(decision.get("emaGapVelocityPercent")),
                    early_entry_eligible=parse_bool(decision.get("earlyEntryEligible")),
                ))

    for snapshots in snapshots_by_pair.values():
        snapshots.sort(key=lambda item: item.utc)
    decisions.sort(key=lambda item: item.utc)
    return decisions, snapshots_by_pair


def prices_between(snapshots: list[Snapshot], start: datetime, end: datetime) -> list[Snapshot]:
    timestamps = [item.utc.timestamp() for item in snapshots]
    left = bisect_left(timestamps, start.timestamp())
    right = bisect_left(timestamps, end.timestamp())
    return snapshots[left:right]


def pct(start: float, end: float) -> float:
    return (end - start) / start * 100.0 if start > 0 else 0.0


def nearest_price_at_or_after(snapshots: list[Snapshot], utc: datetime) -> float | None:
    timestamps = [item.utc.timestamp() for item in snapshots]
    index = bisect_left(timestamps, utc.timestamp())
    return snapshots[index].last if index < len(snapshots) else None


def summarize(values: list[float]) -> str:
    if not values:
        return "n=0"
    return f"n={len(values)}, avg={mean(values):.3f}%, med={median(values):.3f}%, min={min(values):.3f}%, max={max(values):.3f}%"


def main() -> int:
    parser = argparse.ArgumentParser(description="Analyze dry-run entry timing from cycles/snapshots export.")
    parser.add_argument("csv", type=Path)
    parser.add_argument("--out", type=Path)
    args = parser.parse_args()

    decisions, snapshots_by_pair = read_export(args.csv)
    buys = [item for item in decisions if item.action in {"WOULD_BUY", "WOULD_OPEN_LONG"}]
    rejected = [
        item for item in decisions
        if item.action in {"NO_ORDER", "WOULD_BUY_BLOCKED"}
        and (
            item.desired_position in {"LONG_MICRO", "LONG"}
            or item.early_entry_eligible
            or (item.has_bullish_structure and item.score >= 0.70)
        )
    ]

    lines: list[str] = []
    lines.append("# Entry Timing Report")
    lines.append("")
    lines.append(f"- decisions: `{len(decisions)}`")
    lines.append(f"- market pairs with snapshots: `{len(snapshots_by_pair)}`")
    lines.append(f"- executed long entries: `{len(buys)}`")
    lines.append(f"- rejected/blocked plausible long candidates: `{len(rejected)}`")
    if decisions:
        lines.append(f"- UTC range: `{decisions[0].utc.isoformat()} -> {decisions[-1].utc.isoformat()}`")
    lines.append("")

    peak_distances: list[float] = []
    drawdowns_after: list[float] = []
    forward_returns: dict[int, list[float]] = defaultdict(list)
    top_entries: list[tuple[float, Decision, float, float]] = []
    for buy in buys:
        snapshots = snapshots_by_pair.get(buy.pair, [])
        if not snapshots:
            continue
        before = prices_between(
            snapshots,
            buy.utc.replace(tzinfo=timezone.utc),
            datetime.fromtimestamp(buy.utc.timestamp() + PEAK_WINDOW_MINUTES * 60, timezone.utc),
        )
        if before:
            high = max(item.last for item in before)
            low = min(item.last for item in before)
            peak_distance = pct(buy.price, high)
            max_drawdown = pct(buy.price, low)
            peak_distances.append(peak_distance)
            drawdowns_after.append(max_drawdown)
            top_entries.append((peak_distance, buy, high, low))
        for minutes in FORWARD_MINUTES:
            later = nearest_price_at_or_after(
                snapshots,
                datetime.fromtimestamp(buy.utc.timestamp() + minutes * 60, timezone.utc),
            )
            if later is not None:
                forward_returns[minutes].append(pct(buy.price, later))

    lines.append("## Actual Entries")
    lines.append("")
    lines.append(f"- return to best price in next {PEAK_WINDOW_MINUTES}m: `{summarize(peak_distances)}`")
    lines.append(f"- entries already above next {PEAK_WINDOW_MINUTES}m high: `{sum(1 for value in peak_distances if value < 0)} / {len(peak_distances)}`")
    lines.append(f"- worst path to next {PEAK_WINDOW_MINUTES}m low: `{summarize(drawdowns_after)}`")
    for minutes in FORWARD_MINUTES:
        lines.append(f"- return after {minutes}m: `{summarize(forward_returns[minutes])}`")
    lines.append("")
    lines.append("Top entries closest to local peak:")
    lines.append("")
    lines.append("|utc|pair|price|score|peak_dist_60m|max_down_60m|PA|EMA gap|action|")
    lines.append("|---|---|---:|---:|---:|---:|---|---:|---|")
    for peak_distance, buy, high, low in sorted(top_entries, key=lambda item: item[0])[:20]:
        lines.append(
            f"|{buy.utc.isoformat()}|{buy.pair}|{buy.price:.8g}|{buy.score:.2f}|"
            f"{peak_distance:.3f}|{pct(buy.price, low):.3f}|"
            f"{buy.price_action_direction or ''} {buy.price_action_trend_percent or ''}|"
            f"{buy.bullish_ema_gap_percent or 0:.3f}|{buy.action}|"
        )
    lines.append("")

    missed: list[tuple[float, Decision, float]] = []
    for candidate in rejected:
        snapshots = snapshots_by_pair.get(candidate.pair, [])
        if not snapshots:
            continue
        later_window = prices_between(
            snapshots,
            candidate.utc,
            datetime.fromtimestamp(candidate.utc.timestamp() + 120 * 60, timezone.utc),
        )
        if not later_window:
            continue
        max_up = max(pct(candidate.price, item.last) for item in later_window)
        if max_up >= 1.0:
            missed.append((max_up, candidate, min(pct(candidate.price, item.last) for item in later_window)))

    lines.append("## Missed / Blocked Opportunities")
    lines.append("")
    lines.append(f"- candidates with >= +1% upside in next 120m: `{len(missed)}`")
    lines.append("- rejection counts for those candidates:")
    for reason, count in Counter((item[1].rejection or "UNKNOWN") for item in missed).most_common(12):
        lines.append(f"  - `{reason}`: `{count}`")
    lines.append("")
    lines.append("|utc|pair|price|score|max_up_120m|max_down_120m|reject|PA|EMA gap|early|")
    lines.append("|---|---|---:|---:|---:|---:|---|---|---:|---|")
    for max_up, candidate, max_down in sorted(missed, key=lambda item: item[0], reverse=True)[:30]:
        lines.append(
            f"|{candidate.utc.isoformat()}|{candidate.pair}|{candidate.price:.8g}|{candidate.score:.2f}|"
            f"{max_up:.3f}|{max_down:.3f}|{candidate.rejection or ''}|"
            f"{candidate.price_action_direction or ''} {candidate.price_action_trend_percent or ''}|"
            f"{candidate.bullish_ema_gap_percent or 0:.3f}|{candidate.early_entry_eligible}|"
        )

    text = "\n".join(lines) + "\n"
    if args.out:
        args.out.write_text(text, encoding="utf-8")
    else:
        print(text)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
