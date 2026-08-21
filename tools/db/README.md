# Kraken research dump

Historical Kraken Futures candles for research the live bot cannot answer from its
own data: how a wider funnel would have behaved, whether a faster timeframe reacts
sooner, what a strategy change does over years rather than the 58 closed trades the
live accounts have produced so far.

## Where it lives

Its own database, `tradingbot_research`, on the same Postgres instance as the
trading database. The reasoning is in the header of `kraken-research-schema.sql`:
`tradingbot` is 18 GB of live journal driving real money, this is bulk import with
different retention and a need to be droppable. Same instance means nothing extra
to run or back up, and `postgres_fdw` can still bridge the two if a query ever
needs both.

On the VPS the scripts sit in `/opt/research/kraken-dump/`.

## Usage

    ./kraken-dump.py --plan --resolution 15m         # what it would fetch, no writes
    ./kraken-dump.py --resolution 15m                # every tradeable perpetual
    ./kraken-dump.py --resolution 1h --symbols PF_XBTUSD,PF_ETHUSD
    ./kraken-dump.py --resolution 5m --limit-symbols 5

Resumable: every page advances a cursor in `kraken_dump_progress`, so a run that is
killed continues where it stopped. Re-running a finished series costs zero requests.

Guards: `--min-free-gb` (default 15) aborts rather than filling the disk the live
trading database shares, and `--max-requests` caps a single run.

## Scale, measured

| resolution | requests | candles | wall clock | database |
|---|---|---|---|---|
| 1h | ~3 000 | ~6.0M | ~14 min | ~2 GB |
| 15m | ~11 600 | ~23.2M | ~52 min | ~8 GB |
| 5m | ~34 500 | ~69.0M | ~155 min | ~24 GB |

285 tradeable perpetuals, history back to 2022-03-23 for the oldest. Sizes are
extrapolated from a measured 349 bytes per candle including both indexes.

## What is dumped

Every perpetual Kraken has, not just the pairs the live universe scans - though in
practice those are the same thing: the worker reports `discovered=308 included=308
blacklisted=0`, so the universe is already everything its registry has seen. The
narrowing happens further down the funnel, at the active set, not here.

Delisted symbols are included, and they are the reason the registry is read at all.
Kraken's instruments endpoint returns only what trades today - 285 symbols - while
the registry remembers 316, and Kraken still serves full history for the missing 31
(PF_LUNA2USD-style casualties: PF_CATUSD, PF_VINEUSD, PF_ETHWUSD and so on). A dump
built from the live list alone is survivorship-biased, and the perps that vanished
are disproportionately the ones that collapsed - exactly the behaviour a strategy
has to survive. `--skip-delisted` opts out.

The 14 dated futures are excluded by default: they carry expiry and basis behaviour
that does not belong in a perpetual backtest.

`trade` is the feed that actually printed. `mark` is a separate series for the same
symbol, priced for liquidations; pass `--feed mark` if that is ever needed, and it
lands beside the trade series rather than overwriting it.
