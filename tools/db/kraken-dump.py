#!/usr/bin/env python3
"""Dump Kraken Futures history into the research database.

Standard library only, on purpose: the VPS has python3 and psql and nothing else
needs installing. Rows reach Postgres through `psql \\copy` from stdin rather than
one insert per candle - the live market-data writer was rebuilt for exactly that
reason after per-row inserts turned a 14-second sweep into 55 seconds.

Resumable. Every page advances a cursor in kraken_dump_progress, so a run that is
killed, times out or hits a bad symbol continues where it stopped instead of
replaying 22 000 requests.

  ./kraken-dump.py --database tradingbot_research --resolution 15m
  ./kraken-dump.py --database tradingbot_research --resolution 15m --symbols PF_XBTUSD,PF_ETHUSD
  ./kraken-dump.py --database tradingbot_research --plan
"""

import argparse
import json
import os
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request
from datetime import datetime, timezone

CHARTS = "https://futures.kraken.com/api/charts/v1"
INSTRUMENTS = "https://futures.kraken.com/derivatives/api/v3/instruments"
PAGE = 2000  # what the API returns per request; not configurable on their side

# Seconds per candle, used to step the cursor past a page and to tell whether the
# last candle of a page is still forming.
RESOLUTION_SECONDS = {
    "1m": 60, "5m": 300, "15m": 900, "30m": 1800,
    "1h": 3600, "4h": 14400, "12h": 43200, "1d": 86400, "1w": 604800,
}


def log(message):
    print(f"{datetime.now(timezone.utc):%H:%M:%S} {message}", flush=True)


def fetch(url, attempts=5):
    """GET with backoff. Kraken answers in ~0.12s; a stall is worth waiting out
    rather than abandoning a symbol halfway and leaving a hole in the series."""
    delay = 1.0
    for attempt in range(1, attempts + 1):
        try:
            request = urllib.request.Request(url, headers={"User-Agent": "trading-bot-research/1.0"})
            with urllib.request.urlopen(request, timeout=30) as response:
                return json.load(response)
        except (urllib.error.URLError, urllib.error.HTTPError, TimeoutError, json.JSONDecodeError) as error:
            if attempt == attempts:
                raise
            log(f"  retry {attempt}/{attempts - 1} after {error}")
            time.sleep(delay)
            delay = min(delay * 2, 30)


class Database:
    """Thin psql wrapper. One process per statement batch, fed through stdin so a
    page of candles and the upsert that consumes it share a single transaction."""

    def __init__(self, container, user, database):
        self.base = ["docker", "exec", "-i", container, "psql",
                     "-U", user, "-d", database, "-v", "ON_ERROR_STOP=1"]

    def run(self, sql, quiet=True):
        result = subprocess.run(self.base + (["-q"] if quiet else []),
                                input=sql, capture_output=True, text=True)
        if result.returncode != 0:
            raise RuntimeError(f"psql failed: {result.stderr.strip()}")
        return result.stdout

    def query(self, sql):
        result = subprocess.run(self.base + ["-At"], input=sql, capture_output=True, text=True)
        if result.returncode != 0:
            raise RuntimeError(f"psql failed: {result.stderr.strip()}")
        return [line for line in result.stdout.splitlines() if line]


def quote(value):
    if value is None:
        return "null"
    return "'" + str(value).replace("'", "''") + "'"


def load_delisted(container, user, registry_database, live_symbols):
    """Symbols the bot's own registry remembers but Kraken no longer lists.

    Kraken's instruments endpoint returns only what trades today, so a dump built
    from it alone is a survivorship-biased dataset: the perps that were delisted are
    disproportionately the ones that collapsed, which is exactly what a strategy has
    to survive. The registry has seen 308 futures symbols against the 285 live now,
    and Kraken still serves full history for every one of the missing 31."""
    registry = Database(container, user, registry_database)
    try:
        rows = registry.query(
            "select kraken_symbol from instrument_registry "
            "where kraken_symbol like 'PF\\_%' order by kraken_symbol;")
    except RuntimeError as error:
        log(f"registry unavailable, dumping live symbols only ({error})")
        return []
    return [
        {"symbol": symbol, "pair": symbol, "type": "flexible_futures",
         "tradeable": False, "openingDate": None, "delisted": True}
        for symbol in rows if symbol not in live_symbols
    ]


def load_instruments(database, plan_only, contract_type):
    payload = fetch(INSTRUMENTS)
    rows = []
    for item in payload.get("instruments", []):
        if not item.get("tradeable") or item.get("isExpired"):
            continue
        # 299 instruments are tradeable, but 285 of them are the perpetuals the bot
        # actually trades; the rest are dated futures with their own expiry and basis
        # behaviour. Mixing them into a backtest of a perp strategy is a silent error.
        if contract_type != "all" and item.get("type") != contract_type:
            continue
        rows.append(item)

    if not plan_only:
        values = ",".join(
            "({},{},{},{},{},{},{},{},{},{})".format(
                quote(item["symbol"]),
                quote(item.get("pair") or item["symbol"]),
                quote(item.get("base")),
                quote(item.get("quote")),
                quote(item.get("type")),
                "false" if item.get("delisted") else "true",
                quote(item.get("openingDate")),
                item.get("contractSize") if item.get("contractSize") is not None else "null",
                item.get("tickSize") if item.get("tickSize") is not None else "null",
                quote(",".join(item.get("tags") or [])),
            )
            for item in rows
        )
        database.run(f"""
            insert into kraken_instruments
                (symbol, pair, base, quote, type, tradeable, opening_date,
                 contract_size, tick_size, tags)
            values {values}
            on conflict (symbol) do update set
                pair = excluded.pair,
                tradeable = excluded.tradeable,
                opening_date = excluded.opening_date,
                tags = excluded.tags,
                fetched_at = now();
        """)
    return rows


def free_gigabytes(path="/"):
    return shutil.disk_usage(path).free / 1024 ** 3


def dump_series(database, symbol, opening_date, feed, resolution, args, state):
    """Walk one symbol's series forward, a page at a time, from wherever the last
    run stopped."""
    step = RESOLUTION_SECONDS[resolution]

    cursor = database.query(f"""
        select coalesce(extract(epoch from cursor_time)::bigint, 0), complete
        from kraken_dump_progress
        where symbol = {quote(symbol)} and feed = {quote(feed)} and resolution = {quote(resolution)};
    """)
    if cursor:
        start, complete = cursor[0].split("|")
        if complete == "t" and not args.refresh:
            return 0
        start = int(start)
    else:
        start = 0

    if start == 0:
        # openingDate is when the contract listed; there is nothing before it.
        start = int(datetime.fromisoformat(opening_date.replace("Z", "+00:00")).timestamp()) if opening_date else 1640995200

    added = 0
    while True:
        if state["requests"] >= args.max_requests:
            log(f"  stopping: request cap {args.max_requests} reached")
            return added
        if free_gigabytes() < args.min_free_gb:
            raise SystemExit(f"aborting: less than {args.min_free_gb} GB free on the VPS; "
                             f"the live bot shares this disk")

        payload = fetch(f"{CHARTS}/{feed}/{symbol}/{resolution}?from={start}")
        state["requests"] += 1
        candles = payload.get("candles") or []
        more = bool(payload.get("more_candles"))

        # The newest candle of the final page is still forming. Storing it and then
        # never revisiting it - the upsert deliberately does nothing on conflict,
        # because a closed candle never changes - would freeze a partial bar into
        # the history forever.
        now = time.time()
        closed = [c for c in candles if int(c["time"]) / 1000 + step <= now]
        if not closed:
            break

        rows = "\n".join(
            "{}\t{}\t{}\t{}\t{}\t{}\t{}\t{}\t{}".format(
                symbol, feed, resolution,
                datetime.fromtimestamp(int(c["time"]) / 1000, timezone.utc).isoformat(),
                c["open"], c["high"], c["low"], c["close"], c["volume"])
            for c in closed
        )
        last_open = int(closed[-1]["time"]) / 1000

        database.run(f"""
            begin;
            create temp table stage (
                symbol text, feed text, resolution text, open_time timestamptz,
                open numeric, high numeric, low numeric, close numeric, volume numeric
            ) on commit drop;
            \\copy stage from stdin
{rows}
\\.
            insert into kraken_candles
                (symbol, feed, resolution, open_time, open, high, low, close, volume)
            select symbol, feed, resolution, open_time, open, high, low, close, volume
            from stage
            on conflict (symbol, feed, resolution, open_time) do nothing;

            insert into kraken_dump_progress
                (symbol, feed, resolution, cursor_time, earliest_open_time,
                 latest_open_time, candle_count, request_count, complete, updated_at)
            select {quote(symbol)}, {quote(feed)}, {quote(resolution)},
                   to_timestamp({last_open + step}),
                   min(open_time), max(open_time), count(*), 1, false, now()
            from kraken_candles
            where symbol = {quote(symbol)} and feed = {quote(feed)} and resolution = {quote(resolution)}
            on conflict (symbol, feed, resolution) do update set
                cursor_time = excluded.cursor_time,
                earliest_open_time = excluded.earliest_open_time,
                latest_open_time = excluded.latest_open_time,
                candle_count = excluded.candle_count,
                request_count = kraken_dump_progress.request_count + 1,
                complete = false,
                last_error = null,
                updated_at = now();
            commit;
        """)

        added += len(closed)
        start = int(last_open + step)
        if not more:
            break
        time.sleep(args.sleep)

    database.run(f"""
        update kraken_dump_progress set complete = true, cursor_time = null, updated_at = now()
        where symbol = {quote(symbol)} and feed = {quote(feed)} and resolution = {quote(resolution)};
    """)
    return added


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--container", default="trading-bot-db")
    parser.add_argument("--user", default="tradingbot")
    parser.add_argument("--database", default="tradingbot_research")
    parser.add_argument("--feed", default="trade", choices=["trade", "mark"])
    parser.add_argument("--resolution", default="15m", choices=sorted(RESOLUTION_SECONDS))
    parser.add_argument("--symbols", help="comma separated, default every tradeable perp")
    parser.add_argument("--registry-database", default="tradingbot",
                        help="database holding instrument_registry, read to recover delisted symbols")
    parser.add_argument("--skip-delisted", action="store_true",
                        help="live symbols only; leaves a survivorship-biased dataset")
    parser.add_argument("--type", default="flexible_futures",
                        choices=["flexible_futures", "futures_inverse", "all"],
                        help="contract type; the default is the perpetuals the bot trades")
    parser.add_argument("--limit-symbols", type=int, default=0, help="stop after N symbols, for a smoke test")
    parser.add_argument("--sleep", type=float, default=0.15, help="pause between pages")
    parser.add_argument("--max-requests", type=int, default=100000)
    parser.add_argument("--min-free-gb", type=float, default=15.0,
                        help="abort below this; the live trading database shares the disk")
    parser.add_argument("--refresh", action="store_true", help="re-walk series already marked complete")
    parser.add_argument("--plan", action="store_true", help="print what would be fetched and exit")
    args = parser.parse_args()

    database = Database(args.container, args.user, args.database)

    if not args.plan:
        schema = os.path.join(os.path.dirname(os.path.abspath(__file__)), "kraken-research-schema.sql")
        database.run(open(schema).read())
        log(f"schema ready in {args.database}")

    instruments = load_instruments(database, args.plan, args.type)
    if not args.skip_delisted and args.type in ("flexible_futures", "all"):
        delisted = load_delisted(args.container, args.user, args.registry_database,
                                 {item["symbol"] for item in instruments})
        if delisted:
            log(f"{len(delisted)} delisted symbol(s) recovered from the registry")
            instruments += delisted
    wanted = None
    if args.symbols:
        wanted = {s.strip().upper() for s in args.symbols.split(",")}
        instruments = [i for i in instruments if i["symbol"].upper() in wanted]
    if args.limit_symbols:
        instruments = instruments[:args.limit_symbols]

    if args.plan:
        step = RESOLUTION_SECONDS[args.resolution]
        total = 0
        for item in instruments:
            opened = item.get("openingDate") or "2022-01-01T00:00:00Z"
            seconds = time.time() - datetime.fromisoformat(opened.replace("Z", "+00:00")).timestamp()
            total += max(1, int(seconds / step / PAGE) + 1)
        log(f"{len(instruments)} symbols, {args.feed}/{args.resolution}: "
            f"~{total} requests, ~{total * PAGE / 1e6:.1f}M candles, "
            f"~{total * (0.12 + args.sleep) / 60:.0f} min at {args.sleep}s spacing")
        return

    log(f"{len(instruments)} symbols, feed={args.feed} resolution={args.resolution}, "
        f"{free_gigabytes():.0f} GB free")

    state = {"requests": 0}
    total = 0
    for index, item in enumerate(instruments, 1):
        symbol = item["symbol"]
        try:
            added = dump_series(database, symbol, item.get("openingDate"), args.feed,
                                args.resolution, args, state)
            total += added
            log(f"[{index}/{len(instruments)}] {symbol}: +{added} candles "
                f"({state['requests']} requests, {total} total)")
        except SystemExit:
            raise
        except Exception as error:  # one bad symbol must not end the run
            log(f"[{index}/{len(instruments)}] {symbol}: FAILED {error}")
            database.run(f"""
                insert into kraken_dump_progress (symbol, feed, resolution, last_error)
                values ({quote(symbol)}, {quote(args.feed)}, {quote(args.resolution)}, {quote(str(error)[:400])})
                on conflict (symbol, feed, resolution) do update set
                    last_error = excluded.last_error, updated_at = now();
            """)

    log(f"done: {total} candles added, {state['requests']} requests")


if __name__ == "__main__":
    main()
