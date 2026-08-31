#!/usr/bin/env python3
"""Idempotent Kraken Futures history loader for the live trading database.

Public endpoints only. First run bulk-inserts; later runs insert only new rows
via ON CONFLICT DO NOTHING. Existing application tables are never updated or
deleted; market_candles is insert-only (conflict does nothing).

Typical run, on the VPS next to Postgres:

    python3 tools/db/kraken-history-backfill.py --docker-container trading-bot-db

Or with a connection string (VPN / workstation):

    set -a; source .ai/private/database.env; set +a
    python3 tools/db/kraken-history-backfill.py
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import time
import urllib.error
import urllib.request
from datetime import datetime, timezone
from typing import Any

FUTURES = "https://futures.kraken.com"
VENUE_FUNDING = "kraken-futures"
VENUE_DEPTH = "kraken-futures"
VENUE_CANDLES = "futures"
TIMEFRAME_MINUTES = 15
CANDLE_STEP_SECONDS = TIMEFRAME_MINUTES * 60
WINDOW_START = "2026-07-08T00:00:00+00:00"
WINDOW_END = "2026-08-21T23:59:59+00:00"


def log(message: str) -> None:
    print(f"{datetime.now(timezone.utc):%Y-%m-%dT%H:%M:%SZ} {message}", flush=True)


def fetch(url: str, attempts: int = 5) -> Any:
    delay = 1.0
    last_error: Exception | None = None
    for attempt in range(1, attempts + 1):
        try:
            request = urllib.request.Request(url, headers={"User-Agent": "trading-bot-history/1.0"})
            with urllib.request.urlopen(request, timeout=60) as response:
                return json.load(response)
        except (urllib.error.URLError, urllib.error.HTTPError, TimeoutError, json.JSONDecodeError) as error:
            last_error = error
            status = getattr(error, "code", None)
            if attempt == attempts:
                break
            if status not in (None, 429, 500, 502, 503, 504) and not isinstance(error, TimeoutError):
                # Still retry a couple of times: Kraken occasionally resets public calls.
                if attempt >= 2 and status not in (429, 500, 502, 503, 504):
                    break
            log(f"  retry {attempt}/{attempts - 1} {url.split('?')[0]} after {error}")
            time.sleep(delay)
            delay = min(delay * 2, 30)
    raise RuntimeError(f"GET {url} failed: {last_error}")


def quote(value: Any) -> str:
    if value is None:
        return "null"
    return "'" + str(value).replace("'", "''") + "'"


def copy_cell(value: Any) -> str:
    if value is None:
        return r"\N"
    text = str(value)
    return text.replace("\\", "\\\\").replace("\t", r"\t").replace("\n", r"\n")


def normalize_pair(symbol: str, pair: str | None = None) -> str:
    if pair and "/" in pair:
        return pair
    compact = symbol[3:] if symbol.upper().startswith("PF_") else symbol
    if compact.upper().endswith("USD"):
        return f"{compact[:-3]}/USD"
    return compact


def infer_symbol(pair: str) -> str:
    compact = pair.replace("/", "")
    return compact if compact.upper().startswith("PF_") else f"PF_{compact}"


def parse_ado_connection_string(value: str) -> dict[str, str]:
    parsed: dict[str, str] = {}
    for item in value.split(";"):
        if "=" not in item:
            continue
        key, raw = item.split("=", 1)
        parsed[key.strip().lower()] = raw.strip()
    return parsed


def parse_timestamp(value: Any) -> datetime:
    if isinstance(value, (int, float)):
        seconds = float(value)
        if seconds > 1e12:
            seconds /= 1000.0
        return datetime.fromtimestamp(seconds, timezone.utc)
    text = str(value).strip()
    if text.endswith("Z"):
        text = text[:-1] + "+00:00"
    parsed = datetime.fromisoformat(text)
    if parsed.tzinfo is None:
        parsed = parsed.replace(tzinfo=timezone.utc)
    return parsed.astimezone(timezone.utc)


def cumulative_notional(levels: list[list[Any]], mid: float, side: str, bps: float) -> float:
    """USD notional inside `bps` of mid. Size is base; price * size is quote USD."""
    fraction = bps / 10_000.0
    if side == "bid":
        floor = mid * (1.0 - fraction)
        return sum(float(price) * float(size) for price, size in levels if float(price) >= floor)
    ceiling = mid * (1.0 + fraction)
    return sum(float(price) * float(size) for price, size in levels if float(price) <= ceiling)


class Database:
    """psycopg if a connection string is reachable, otherwise docker exec psql."""

    def __init__(self, container: str, user: str, database: str, connection_string: str | None):
        self.container = container
        self.user = user
        self.database = database
        self.connection_string = connection_string
        self.mode = "docker"
        self._psycopg = None
        self._connect_kwargs: dict[str, Any] = {}

    def connect(self) -> None:
        if self.connection_string:
            try:
                self._try_psycopg()
                self.mode = "psycopg"
                log(f"database via psycopg ({self._connect_kwargs.get('host')}:{self._connect_kwargs.get('port')}/{self._connect_kwargs.get('dbname')})")
                return
            except Exception as error:
                log(f"psycopg path unavailable ({error}); trying docker exec")
        self._probe_docker()
        self.mode = "docker"
        log(f"database via docker exec {self.container}")

    def _try_psycopg(self) -> None:
        try:
            import psycopg  # type: ignore
        except ImportError:
            import psycopg2 as psycopg  # type: ignore
        parsed = parse_ado_connection_string(self.connection_string or "")
        kwargs = {
            "host": parsed.get("host", "10.8.0.1"),
            "port": int(parsed.get("port", "5432")),
            "dbname": parsed.get("database", self.database),
            "user": parsed.get("username") or parsed.get("user") or self.user,
            "password": parsed.get("password", ""),
            "connect_timeout": 8,
        }
        conn = psycopg.connect(**kwargs)
        conn.close()
        self._psycopg = psycopg
        self._connect_kwargs = kwargs

    def _probe_docker(self) -> None:
        result = subprocess.run(
            ["docker", "exec", self.container, "pg_isready", "-U", self.user, "-d", self.database],
            capture_output=True, text=True,
        )
        if result.returncode != 0:
            raise RuntimeError(
                f"cannot reach Postgres (psycopg and docker exec {self.container} both failed): "
                f"{result.stderr.strip() or result.stdout.strip() or 'pg_isready failed'}"
            )

    def run(self, sql: str) -> str:
        if self.mode == "psycopg":
            conn = self._psycopg.connect(**self._connect_kwargs)
            try:
                conn.autocommit = True
                with conn.cursor() as cursor:
                    cursor.execute(sql)
                    if cursor.description is None:
                        return ""
                    rows = cursor.fetchall()
                    return "\n".join("|".join("" if col is None else str(col) for col in row) for row in rows)
            finally:
                conn.close()
        result = subprocess.run(
            ["docker", "exec", "-i", self.container, "psql",
             "-U", self.user, "-d", self.database, "-v", "ON_ERROR_STOP=1", "-At", "-F", "|", "-q"],
            input=sql, capture_output=True, text=True,
        )
        if result.returncode != 0:
            raise RuntimeError(f"psql failed: {result.stderr.strip() or result.stdout.strip()}")
        return result.stdout

    def query(self, sql: str) -> list[list[str]]:
        output = self.run(sql).strip()
        if not output:
            return []
        return [line.split("|") for line in output.splitlines() if line]

    def scalar(self, sql: str) -> str | None:
        rows = self.query(sql)
        return rows[0][0] if rows else None

    def copy_upsert(self, table: str, columns: list[str], rows: list[list[Any]]) -> int:
        """COPY into a temp table, INSERT ... ON CONFLICT DO NOTHING, return inserted count."""
        if not rows:
            return 0
        if self.mode == "psycopg":
            return self._copy_upsert_psycopg(table, columns, rows)
        return self._copy_upsert_psql(table, columns, rows)

    def _copy_upsert_psql(self, table: str, columns: list[str], rows: list[list[Any]]) -> int:
        payload = "\n".join("\t".join(copy_cell(value) for value in row) for row in rows)
        col_sql = ", ".join(columns)
        sql = f"""
begin;
create temp table stage (
    {", ".join(f"{name} text" for name in columns)}
) on commit drop;
\\copy stage from stdin
{payload}
\\.
with inserted as (
    insert into {table} ({col_sql})
    select {col_sql} from stage
    on conflict do nothing
    returning 1
)
select count(*) from inserted;
commit;
"""
        output = self.run(sql).strip().splitlines()
        for line in reversed(output):
            line = line.strip()
            if line.isdigit():
                return int(line)
        return 0

    def _copy_upsert_psycopg(self, table: str, columns: list[str], rows: list[list[Any]]) -> int:
        from io import StringIO

        payload = "\n".join("\t".join(copy_cell(value) for value in row) for row in rows)
        col_sql = ", ".join(columns)
        stage_cols = ", ".join(f"{name} text" for name in columns)
        conn = self._psycopg.connect(**self._connect_kwargs)
        try:
            with conn:
                with conn.cursor() as cursor:
                    cursor.execute(f"create temp table stage ({stage_cols}) on commit drop")
                    buffer = StringIO(payload + "\n")
                    if hasattr(cursor, "copy"):
                        with cursor.copy(f"COPY stage ({col_sql}) FROM STDIN") as copy:
                            copy.write(buffer.getvalue())
                    else:
                        cursor.copy_expert(f"COPY stage ({col_sql}) FROM STDIN", buffer)
                    cursor.execute(
                        f"""
                        with inserted as (
                            insert into {table} ({col_sql})
                            select {col_sql} from stage
                            on conflict do nothing
                            returning 1
                        )
                        select count(*) from inserted
                        """
                    )
                    count = cursor.fetchone()[0]
            return int(count)
        finally:
            conn.close()


def ensure_schema(database: Database) -> None:
    database.run(
        """
        create table if not exists market_funding_history (
            venue text not null,
            pair text not null,
            utc timestamptz not null,
            funding_rate numeric,
            relative_funding_rate numeric,
            primary key (venue, pair, utc)
        )
        """
    )
    database.run(
        """
        create table if not exists market_depth_history (
            venue text not null,
            pair text not null,
            utc timestamptz not null,
            mid numeric,
            spread_percent numeric,
            bid_usd_25bps numeric,
            ask_usd_25bps numeric,
            bid_usd_50bps numeric,
            ask_usd_50bps numeric,
            primary key (venue, pair, utc)
        )
        """
    )


def load_perpetuals() -> list[dict[str, Any]]:
    payload = fetch(f"{FUTURES}/derivatives/api/v3/instruments")
    rows = []
    for item in payload.get("instruments") or []:
        symbol = str(item.get("symbol") or "")
        if not symbol.startswith("PF_"):
            continue
        if not item.get("tradeable") or item.get("isExpired") or item.get("postOnly"):
            continue
        rows.append(item)
    rows.sort(key=lambda item: str(item.get("symbol") or ""))
    return rows


def load_funding(database: Database, instruments: list[dict[str, Any]], sleep_s: float, limit: int) -> dict[str, Any]:
    before = int(database.scalar("select count(*) from market_funding_history") or "0")
    inserted = 0
    processed = 0
    errors: list[str] = []
    targets = instruments[:limit] if limit else instruments
    for index, item in enumerate(targets, 1):
        symbol = item["symbol"]
        pair = normalize_pair(symbol, item.get("pair"))
        try:
            payload = fetch(f"{FUTURES}/derivatives/api/v4/historicalfundingrates?symbol={urllib.request.quote(symbol)}")
            rates = payload.get("rates") or []
            rows = []
            for rate in rates:
                utc = parse_timestamp(rate.get("timestamp")).isoformat()
                rows.append([
                    VENUE_FUNDING,
                    pair,
                    utc,
                    rate.get("fundingRate"),
                    rate.get("relativeFundingRate"),
                ])
            added = database.copy_upsert(
                "market_funding_history",
                ["venue", "pair", "utc", "funding_rate", "relative_funding_rate"],
                rows,
            )
            inserted += added
            processed += 1
            log(f"funding [{index}/{len(targets)}] {symbol} {pair}: {len(rows)} fetched +{added}")
        except Exception as error:
            errors.append(f"{symbol}: {error}")
            log(f"funding [{index}/{len(targets)}] {symbol}: FAILED {error}")
        if index < len(targets) and sleep_s > 0:
            time.sleep(sleep_s)
    after = int(database.scalar("select count(*) from market_funding_history") or "0")
    return {
        "before": before,
        "after": after,
        "inserted": inserted,
        "processed": processed,
        "errors": errors,
        "first_mass_run": before == 0 and inserted > 0,
        "symbols": len(targets),
    }


def load_candles(database: Database, sleep_s: float, limit: int) -> dict[str, Any]:
    before = int(database.scalar(
        f"select count(*) from market_candles where venue = {quote(VENUE_CANDLES)} and timeframe_minutes = {TIMEFRAME_MINUTES}"
    ) or "0")
    registry_rows = database.query(
        f"select pair, kraken_symbol from instrument_registry where venue = {quote(VENUE_CANDLES)}"
    )
    registry = {pair: symbol for pair, symbol in registry_rows}
    tails = database.query(
        f"""
        select pair, extract(epoch from max(open_time))::bigint
        from market_candles
        where venue = {quote(VENUE_CANDLES)} and timeframe_minutes = {TIMEFRAME_MINUTES}
        group by pair
        order by pair
        """
    )
    if limit:
        tails = tails[:limit]
    inserted = 0
    processed = 0
    errors: list[str] = []
    now = time.time()
    to_unix = int(now)
    for index, (pair, max_epoch) in enumerate(tails, 1):
        symbol = registry.get(pair) or infer_symbol(pair)
        start = int(max_epoch)
        try:
            fetched = 0
            added_total = 0
            while start < to_unix:
                url = (
                    f"{FUTURES}/api/charts/v1/mark/{urllib.request.quote(symbol)}/"
                    f"15m?from={start}&to={to_unix}"
                )
                payload = fetch(url)
                candles = payload.get("candles") or []
                more = bool(payload.get("more_candles"))
                closed = []
                last_open_s = None
                for candle in candles:
                    open_s = int(candle["time"]) / 1000.0
                    if open_s + CANDLE_STEP_SECONDS > now:
                        continue
                    last_open_s = open_s
                    closed.append([
                        VENUE_CANDLES,
                        pair,
                        TIMEFRAME_MINUTES,
                        datetime.fromtimestamp(open_s, timezone.utc).isoformat(),
                        candle.get("open"),
                        candle.get("high"),
                        candle.get("low"),
                        candle.get("close"),
                        candle.get("volume") or 0,
                    ])
                if not closed or last_open_s is None:
                    break
                added = database.copy_upsert(
                    "market_candles",
                    ["venue", "pair", "timeframe_minutes", "open_time", "open", "high", "low", "close", "volume"],
                    closed,
                )
                inserted += added
                added_total += added
                fetched += len(closed)
                next_start = int(last_open_s) + CANDLE_STEP_SECONDS
                if next_start <= start:
                    break
                start = next_start
                if not more:
                    break
                if sleep_s > 0:
                    time.sleep(sleep_s)
            processed += 1
            log(f"candles [{index}/{len(tails)}] {symbol} {pair}: fetched {fetched} +{added_total}")
        except Exception as error:
            errors.append(f"{symbol}/{pair}: {error}")
            log(f"candles [{index}/{len(tails)}] {symbol} {pair}: FAILED {error}")
        if index < len(tails) and sleep_s > 0:
            time.sleep(sleep_s)
    after = int(database.scalar(
        f"select count(*) from market_candles where venue = {quote(VENUE_CANDLES)} and timeframe_minutes = {TIMEFRAME_MINUTES}"
    ) or "0")
    return {
        "before": before,
        "after": after,
        "inserted": after - before,
        "fetched_inserted_reported": inserted,
        "processed": processed,
        "pairs": len(tails),
        "errors": errors,
        "first_mass_run": before == 0 and after > 0,
    }


def load_depth(database: Database, instruments: list[dict[str, Any]], sleep_s: float, top_n: int) -> dict[str, Any]:
    before = int(database.scalar("select count(*) from market_depth_history") or "0")
    payload = fetch(f"{FUTURES}/derivatives/api/v3/tickers")
    live = {item["symbol"] for item in instruments}
    tickers = [
        item for item in (payload.get("tickers") or [])
        if str(item.get("symbol") or "").startswith("PF_") and item.get("symbol") in live
    ]
    # ticker.vol24h is base units (PEPE/SHIB dominate); volumeQuote is USD 24h volume.
    tickers.sort(key=lambda item: float(item.get("volumeQuote") or 0), reverse=True)
    selected = tickers[:top_n]
    inserted = 0
    processed = 0
    errors: list[str] = []
    snapshot_utc = datetime.now(timezone.utc).replace(microsecond=0).isoformat()
    for index, ticker in enumerate(selected, 1):
        symbol = ticker["symbol"]
        pair = normalize_pair(symbol, ticker.get("pair"))
        try:
            book = fetch(f"{FUTURES}/derivatives/api/v3/orderbook?symbol={urllib.request.quote(symbol)}")
            order_book = book.get("orderBook") or {}
            bids = [[float(level[0]), float(level[1])] for level in (order_book.get("bids") or []) if len(level) >= 2]
            asks = [[float(level[0]), float(level[1])] for level in (order_book.get("asks") or []) if len(level) >= 2]
            if not bids or not asks:
                raise RuntimeError("empty order book")
            best_bid = max(price for price, _ in bids)
            best_ask = min(price for price, _ in asks)
            if best_bid <= 0 or best_ask <= 0 or best_ask < best_bid:
                raise RuntimeError(f"bad top of book bid={best_bid} ask={best_ask}")
            mid = (best_bid + best_ask) / 2.0
            spread_percent = (best_ask - best_bid) / mid * 100.0
            row = [[
                VENUE_DEPTH,
                pair,
                snapshot_utc,
                mid,
                spread_percent,
                cumulative_notional(bids, mid, "bid", 25),
                cumulative_notional(asks, mid, "ask", 25),
                cumulative_notional(bids, mid, "bid", 50),
                cumulative_notional(asks, mid, "ask", 50),
            ]]
            added = database.copy_upsert(
                "market_depth_history",
                [
                    "venue", "pair", "utc", "mid", "spread_percent",
                    "bid_usd_25bps", "ask_usd_25bps", "bid_usd_50bps", "ask_usd_50bps",
                ],
                row,
            )
            inserted += added
            processed += 1
            log(f"depth [{index}/{len(selected)}] {symbol} {pair}: mid={mid:.6g} spread={spread_percent:.4f}% +{added}")
        except Exception as error:
            errors.append(f"{symbol}: {error}")
            log(f"depth [{index}/{len(selected)}] {symbol}: FAILED {error}")
        if index < len(selected) and sleep_s > 0:
            time.sleep(sleep_s)
    after = int(database.scalar("select count(*) from market_depth_history") or "0")
    return {
        "before": before,
        "after": after,
        "inserted": inserted,
        "processed": processed,
        "errors": errors,
        "first_mass_run": before == 0 and inserted > 0,
        "top_n": len(selected),
    }


def funding_report(database: Database) -> dict[str, Any]:
    stats = database.query(
        f"""
        select
            coalesce(min(utc)::text, ''),
            coalesce(max(utc)::text, ''),
            count(*)::text,
            count(distinct pair)::text
        from market_funding_history
        where venue = {quote(VENUE_FUNDING)}
        """
    )
    coverage = database.query(
        f"""
        with per_pair as (
            select
                pair,
                min(utc) as mn,
                max(utc) as mx,
                count(*) filter (
                    where utc >= timestamptz {quote(WINDOW_START)}
                      and utc <= timestamptz {quote(WINDOW_END)}
                ) as in_window
            from market_funding_history
            where venue = {quote(VENUE_FUNDING)}
            group by pair
        )
        select
            count(*)::text,
            count(*) filter (
                where mn <= timestamptz {quote(WINDOW_START)}
                  and mx >= timestamptz {quote('2026-08-21T00:00:00+00:00')}
            )::text,
            count(*) filter (where in_window > 0)::text
        from per_pair
        """
    )
    min_utc, max_utc, count_rows, distinct_pairs = stats[0] if stats else ["", "", "0", "0"]
    total_pairs, covering, any_in_window = coverage[0] if coverage else ["0", "0", "0"]
    covering_n = int(covering)
    window_covered = covering_n > 0
    return {
        "min_utc": min_utc,
        "max_utc": max_utc,
        "count": int(count_rows),
        "distinct_pairs": int(distinct_pairs),
        "window": "2026-07-08 .. 2026-08-21",
        "window_covered": window_covered,
        "pairs_covering_window": covering_n,
        "pairs_with_any_row_in_window": int(any_in_window),
        "pairs_total": int(total_pairs),
    }


def load_connection_string() -> str | None:
    env_path = os.path.join(os.path.dirname(__file__), "..", "..", ".ai", "private", "database.env")
    env_path = os.path.abspath(env_path)
    if os.path.isfile(env_path):
        with open(env_path, encoding="utf-8") as handle:
            for line in handle:
                line = line.strip()
                if not line or line.startswith("#") or "=" not in line:
                    continue
                key, value = line.split("=", 1)
                os.environ.setdefault(key.strip(), value.strip().strip("'").strip('"'))
    return os.environ.get("TRADINGBOT_DATABASE_CONNECTION_STRING")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--docker-container", default="trading-bot-db")
    parser.add_argument("--user", default="tradingbot")
    parser.add_argument("--database", default="tradingbot")
    parser.add_argument("--sleep-funding", type=float, default=0.75)
    parser.add_argument("--sleep-candles", type=float, default=0.15)
    parser.add_argument("--sleep-depth", type=float, default=1.0)
    parser.add_argument("--top-depth", type=int, default=100)
    parser.add_argument("--limit-symbols", type=int, default=0, help="cap symbols per section, for a smoke test")
    parser.add_argument("--skip-funding", action="store_true")
    parser.add_argument("--skip-candles", action="store_true")
    parser.add_argument("--skip-depth", action="store_true")
    args = parser.parse_args()

    database = Database(args.docker_container, args.user, args.database, load_connection_string())
    database.connect()
    ensure_schema(database)

    instruments = load_perpetuals()
    log(f"{len(instruments)} tradeable PF_ perpetuals")

    funding = {"inserted": 0, "processed": 0, "errors": [], "first_mass_run": False, "symbols": 0, "before": 0, "after": 0}
    candles = {"inserted": 0, "processed": 0, "errors": [], "first_mass_run": False, "pairs": 0, "before": 0, "after": 0}
    depth = {"inserted": 0, "processed": 0, "errors": [], "first_mass_run": False, "top_n": 0, "before": 0, "after": 0}

    if not args.skip_funding:
        funding = load_funding(database, instruments, args.sleep_funding, args.limit_symbols)
    if not args.skip_candles:
        candles = load_candles(database, args.sleep_candles, args.limit_symbols)
    if not args.skip_depth:
        depth = load_depth(database, instruments, args.sleep_depth, args.top_depth if not args.limit_symbols else min(args.top_depth, args.limit_symbols))

    coverage = funding_report(database)
    errors = (
        [f"funding {item}" for item in funding.get("errors") or []]
        + [f"candles {item}" for item in candles.get("errors") or []]
        + [f"depth {item}" for item in depth.get("errors") or []]
    )
    report = {
        "funding": {
            "inserted": funding.get("inserted"),
            "before": funding.get("before"),
            "after": funding.get("after"),
            "first_mass_run": funding.get("first_mass_run"),
            "processed": funding.get("processed"),
            "errors": len(funding.get("errors") or []),
            "min_utc": coverage["min_utc"],
            "max_utc": coverage["max_utc"],
            "count": coverage["count"],
            "distinct_pairs": coverage["distinct_pairs"],
            "window_covered": coverage["window_covered"],
            "pairs_covering_window": coverage["pairs_covering_window"],
            "pairs_with_any_row_in_window": coverage["pairs_with_any_row_in_window"],
        },
        "candles": {
            "inserted": candles.get("inserted"),
            "before": candles.get("before"),
            "after": candles.get("after"),
            "first_mass_run": candles.get("first_mass_run"),
            "processed": candles.get("processed"),
            "pairs": candles.get("pairs"),
            "errors": len(candles.get("errors") or []),
        },
        "depth": {
            "inserted": depth.get("inserted"),
            "before": depth.get("before"),
            "after": depth.get("after"),
            "first_mass_run": depth.get("first_mass_run"),
            "processed": depth.get("processed"),
            "top_n": depth.get("top_n"),
            "errors": len(depth.get("errors") or []),
        },
        "symbols_processed": (funding.get("processed") or 0) + (candles.get("processed") or 0) + (depth.get("processed") or 0),
        "error_count": len(errors),
        "errors": errors[:40],
    }
    print("REPORT_JSON_BEGIN")
    print(json.dumps(report, indent=2))
    print("REPORT_JSON_END")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except KeyboardInterrupt:
        log("interrupted")
        raise SystemExit(130)
