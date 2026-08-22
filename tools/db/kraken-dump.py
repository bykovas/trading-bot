#!/usr/bin/env python3
"""Dump Kraken Futures history into the research database.

Standard library only, on purpose: the VPS has python3 and psql and nothing else
needs installing. Rows reach Postgres through `psql \\copy` from stdin rather than
one insert per candle - the live market-data writer was rebuilt for exactly that
reason after per-row inserts turned a 14-second sweep into 55 seconds.

Resumable. Every page advances a cursor in kraken.dump_progress, so a run that is
killed, times out or hits a bad symbol continues where it stopped instead of
replaying 22 000 requests.

  ./kraken-dump.py --database tradingbot_research --resolution 15m
  ./kraken-dump.py --database tradingbot_research --resolution 15m --symbols PF_XBTUSD,PF_ETHUSD
  ./kraken-dump.py --database tradingbot_research --plan
"""

import argparse
import concurrent.futures
import io
import json
import os
import threading
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


def load_delisted_from_file(path, live_symbols):
    """Same recovery as the registry read, for a machine that has no trading
    database next to it. The list is a snapshot, so it ages: refresh it from the
    registry when a run should pick up perps delisted since."""
    symbols = [line.strip().upper() for line in io.open(path, encoding="utf-8")
               if line.strip().startswith("PF_")]
    return [
        {"symbol": symbol, "pair": symbol, "type": "flexible_futures",
         "tradeable": False, "openingDate": None, "delisted": True}
        for symbol in symbols if symbol not in live_symbols
    ]


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
        # Dated futures carry expiry and basis behaviour that does not belong in a
        # backtest of a perpetual strategy. Type alone does not separate them:
        # Kraken labels eight FF_ contracts "flexible_futures" too, so they came
        # through the type filter and sat in the dataset looking like perps. The
        # PF_ prefix is what actually marks a perpetual.
        if contract_type != "all":
            if item.get("type") != contract_type:
                continue
            if contract_type == "flexible_futures" and not item["symbol"].startswith("PF_"):
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
            insert into kraken.instruments
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


def free_gigabytes(path):
    """Checked against where the database files actually are, which is not
    necessarily the root filesystem: the local cluster lives on an external disk."""
    return shutil.disk_usage(path).free / 1024 ** 3


def dump_series(database, symbol, opening_date, feed, resolution, args, state):
    """Walk one symbol's series forward, a page at a time, from wherever the last
    run stopped."""
    step = RESOLUTION_SECONDS[resolution]

    cursor = database.query(f"""
        select coalesce(extract(epoch from cursor_time)::bigint, 0), complete
        from kraken.dump_progress
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
        # A floor only ever moves the start forward. A symbol listed after it keeps
        # its own listing date, so no request is spent on years that never existed.
        if args.since:
            start = max(start, args.since)

    added = 0
    buffer, buffered = [], 0
    while True:
        with state["lock"]:
            state["requests"] += 1
        if state["requests"] >= args.max_requests:
            log(f"  stopping: request cap {args.max_requests} reached")
            return added
        if free_gigabytes(args.disk_path) < args.min_free_gb:
            raise SystemExit(f"aborting: less than {args.min_free_gb} GB free on the VPS; "
                             f"the live bot shares this disk")

        payload = fetch(f"{CHARTS}/{feed}/{symbol}/{resolution}?from={start}")
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

        # Buffer pages and write them together. A psql process per page cost about
        # 0.65s against 0.12-0.8s of actual network, so on the minute series the
        # writer, not Kraken, was three quarters of the wall clock.
        buffer.append(rows)
        buffered += len(closed)
        added += len(closed)
        start = int(last_open + step)
        if buffered >= FLUSH_ROWS or not more:
            flush(database, symbol, feed, resolution, buffer, start, state)
            buffer, buffered = [], 0
        if not more:
            break
        time.sleep(args.sleep)

    if buffer:
        flush(database, symbol, feed, resolution, buffer, start, state)

    database.run(f"""
        update kraken.dump_progress set complete = true, cursor_time = null, updated_at = now()
        where symbol = {quote(symbol)} and feed = {quote(feed)} and resolution = {quote(resolution)};
    """)
    return added


FLUSH_ROWS = 200000


def flush(database, symbol, feed, resolution, buffer, cursor_epoch, state):
    rows = "\n".join(buffer)
    with state["lock"]:
        database.run(f"""
            begin;
            create temp table stage (
                symbol text, feed text, resolution text, open_time timestamptz,
                open numeric, high numeric, low numeric, close numeric, volume numeric
            ) on commit drop;
            \\copy stage from stdin
{rows}
\\.
            insert into kraken.candles
                (symbol, feed, resolution, open_time, open, high, low, close, volume)
            select symbol, feed, resolution, open_time, open, high, low, close, volume
            from stage
            on conflict (symbol, feed, resolution, open_time) do nothing;

            insert into kraken.dump_progress
                (symbol, feed, resolution, cursor_time, earliest_open_time,
                 latest_open_time, candle_count, request_count, complete, updated_at)
            select {quote(symbol)}, {quote(feed)}, {quote(resolution)},
                   to_timestamp({cursor_epoch}),
                   min(open_time), max(open_time), count(*), 1, false, now()
            from kraken.candles
            where symbol = {quote(symbol)} and feed = {quote(feed)} and resolution = {quote(resolution)}
            on conflict (symbol, feed, resolution) do update set
                cursor_time = excluded.cursor_time,
                earliest_open_time = excluded.earliest_open_time,
                latest_open_time = excluded.latest_open_time,
                candle_count = excluded.candle_count,
                request_count = kraken.dump_progress.request_count + 1,
                complete = false,
                last_error = null,
                updated_at = now();
            commit;
        """)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--container", default="trading-bot-db")
    parser.add_argument("--user", default="tradingbot")
    parser.add_argument("--database", default="research")
    parser.add_argument("--feed", default="trade", choices=["trade", "mark"])
    parser.add_argument("--resolution", default="15m", choices=sorted(RESOLUTION_SECONDS))
    parser.add_argument("--symbols", help="comma separated, default every tradeable perp")
    parser.add_argument("--from", dest="since_date", metavar="YYYY-MM-DD",
                        help="do not fetch candles before this date; a symbol listed later "
                             "still starts at its own listing date")
    parser.add_argument("--symbols-file", metavar="PATH",
                        help="file of PF_ symbols to recover delisted ones from, for a "
                             "machine with no trading database; replaces the registry read")
    parser.add_argument("--registry-database", default="tradingbot",
                        help="database holding instrument_registry, read to recover delisted symbols")
    parser.add_argument("--skip-delisted", action="store_true",
                        help="live symbols only; leaves a survivorship-biased dataset")
    parser.add_argument("--type", default="flexible_futures",
                        choices=["flexible_futures", "futures_inverse", "all"],
                        help="contract type; the default is the perpetuals the bot trades")
    parser.add_argument("--limit-symbols", type=int, default=0, help="stop after N symbols, for a smoke test")
    parser.add_argument("--sleep", type=float, default=0.15, help="pause between pages")
    parser.add_argument("--workers", type=int, default=1,
                        help="symbols fetched in parallel. Paging within one symbol stays "
                             "sequential; this only overlaps different symbols. Kraken's "
                             "chart endpoint is public and IP-limited, and this machine is "
                             "not the one the bot trades from, so a throttle here cannot "
                             "reach production.")
    # A runaway backstop, not a budget. The minute series across every perpetual
    # needs about 133 000 requests, and the old default of 100 000 silently cut a
    # finished-looking run 82 symbols short.
    parser.add_argument("--max-requests", type=int, default=2000000)
    parser.add_argument("--disk-path", default="/",
                        help="filesystem the database files live on, for the free-space guard")
    parser.add_argument("--min-free-gb", type=float, default=15.0,
                        help="abort below this; the live trading database shares the disk")
    parser.add_argument("--refresh", action="store_true", help="re-walk series already marked complete")
    parser.add_argument("--plan", action="store_true", help="print what would be fetched and exit")
    args = parser.parse_args()
    args.since = (int(datetime.fromisoformat(args.since_date + "T00:00:00+00:00").timestamp())
                  if args.since_date else None)

    database = Database(args.container, args.user, args.database)

    if not args.plan:
        schema = os.path.join(os.path.dirname(os.path.abspath(__file__)), "kraken-research-schema.sql")
        database.run(open(schema).read())
        log(f"schema ready in {args.database}")

    instruments = load_instruments(database, args.plan, args.type)
    if not args.skip_delisted and args.type in ("flexible_futures", "all"):
        live = {item["symbol"] for item in instruments}
        delisted = (load_delisted_from_file(args.symbols_file, live) if args.symbols_file
                    else load_delisted(args.container, args.user, args.registry_database, live))
        if delisted:
            log(f"{len(delisted)} delisted symbol(s) recovered from the registry")
            # They need their own row too. Without it the candles are there but
            # nothing says which symbols are no longer listed, and a query that
            # joins instruments silently drops them - which is the survivorship
            # bias this whole path exists to avoid.
            values = ",".join(
                "({},{},null,null,{},false,null,null,null,'delisted')".format(
                    quote(item["symbol"]), quote(item["symbol"]), quote(item["type"]))
                for item in delisted)
            database.run(f"""
                insert into kraken.instruments
                    (symbol, pair, base, quote, type, tradeable, opening_date,
                     contract_size, tick_size, tags)
                values {values}
                on conflict (symbol) do update set
                    tradeable = false, tags = 'delisted', fetched_at = now();
            """)
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
            listed = datetime.fromisoformat(opened.replace("Z", "+00:00")).timestamp()
            seconds = time.time() - max(listed, args.since or 0)
            total += max(1, int(seconds / step / PAGE) + 1)
        log(f"{len(instruments)} symbols, {args.feed}/{args.resolution}: "
            f"~{total} requests, ~{total * PAGE / 1e6:.1f}M candles, "
            f"~{total * (0.12 + args.sleep) / 60:.0f} min at {args.sleep}s spacing")
        return

    log(f"{len(instruments)} symbols, feed={args.feed} resolution={args.resolution}, "
        f"{free_gigabytes(args.disk_path):.0f} GB free")

    state = {"requests": 0, "lock": threading.Lock(), "done": 0, "total": 0}

    def work(index_item):
        index, item = index_item
        symbol = item["symbol"]
        try:
            added = dump_series(database, symbol, item.get("openingDate"), args.feed,
                                args.resolution, args, state)
            with state["lock"]:
                state["done"] += 1
                state["total"] += added
                done, total, requests = state["done"], state["total"], state["requests"]
            log(f"[{done}/{len(instruments)}] {symbol}: +{added} candles "
                f"({requests} requests, {total} total)")
        except SystemExit:
            raise
        except Exception as error:  # one bad symbol must not end the run
            with state["lock"]:
                state["done"] += 1
                done = state["done"]
            log(f"[{done}/{len(instruments)}] {symbol}: FAILED {error}")
            database.run(f"""
                insert into kraken.dump_progress (symbol, feed, resolution, last_error)
                values ({quote(symbol)}, {quote(args.feed)}, {quote(args.resolution)}, {quote(str(error)[:400])})
                on conflict (symbol, feed, resolution) do update set
                    last_error = excluded.last_error, updated_at = now();
            """)

    if args.workers > 1:
        with concurrent.futures.ThreadPoolExecutor(max_workers=args.workers) as pool:
            list(pool.map(work, enumerate(instruments, 1)))
    else:
        for pair in enumerate(instruments, 1):
            work(pair)

    log(f"done: {state['total']} candles added, {state['requests']} requests")


if __name__ == "__main__":
    main()
