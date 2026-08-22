# Kraken research dump

Historical Kraken Futures candles for research the live bot cannot answer from its
own data: how a wider funnel would have behaved, whether a faster timeframe reacts
sooner, what a strategy change does over years rather than the 58 closed trades the
live accounts have produced so far.

## Where it lives

A local Postgres in Docker, `bykovas-pg`, with its files on the external disk:

    /Volumes/FileDepot/bykovas-pg-data     the cluster
    localhost:55432                        user bykovas, database bykovas-pg
    schema kraken                          candles, instruments, dump_progress

The database name is deliberately generic: the next dataset gets a schema beside
this one rather than a database of its own. `drop schema kraken cascade` removes
everything here.

It runs locally rather than on the VPS because the full set is 75 GB and the VPS
had 95 GB free with a live trading database sharing the disk. The external volume
has 646 GB free after the load.

An earlier 15m-only copy still sits in `tradingbot_research` on the VPS with the
tables unqualified (`kraken_candles` rather than `kraken.candles`); the analysis
scripts still point there.

## Usage

    tools/db/dump-all-resolutions.sh                 # 4h, 1h, 15m, 5m, 1m in order

    tools/db/kraken-dump.py --plan --resolution 15m  # what it would fetch, no writes
    tools/db/kraken-dump.py \
      --container bykovas-pg --user bykovas --database bykovas-pg \
      --resolution 15m --from 2025-01-01 --workers 6 \
      --disk-path /Volumes/FileDepot \
      --symbols-file tools/db/kraken-futures-registry-symbols.txt

`--symbols-file` replaces the registry read for a machine with no trading database
next to it; the snapshot in this directory ages, so refresh it from
`instrument_registry` when a run should pick up perps delisted since.

`--workers` overlaps different symbols. Paging within one symbol stays sequential
because each request's cursor comes from the previous page.

Resumable: every page advances a cursor in `kraken_dump_progress`, so a run that is
killed continues where it stopped. Re-running a finished series costs zero requests.

Guards: `--min-free-gb` (default 15) aborts rather than filling the disk the live
trading database shares, and `--max-requests` caps a single run.

## Scale, measured

The full local load, 308 perpetuals from 2025-01-01, six workers:

| resolution | candles | wall clock |
|---|---|---|
| 4h | 981 256 | 55 s |
| 1h | 3 916 561 | 3 min |
| 15m | 15 630 938 | 22 min |
| 5m | 46 655 099 | 1 h 43 |
| 1m | 232 754 490 | 8 h |

75 GB on disk for all five. Kraken serves every resolution back to 2022-03-23, so
a deeper window is available at proportional cost.

Two things made this feasible. Writes are batched per 200 000 rows instead of one
`psql` process per page — that process cost about 0.65 s against 0.12-0.8 s of
actual network, so the writer, not Kraken, was most of the wall clock. And symbols
are fetched in parallel, which took the rate from 1 page/s to 15.6.

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

Dated futures are excluded, but type alone does not identify them: Kraken labels
eight `FF_` contracts `flexible_futures` exactly as it labels the perpetuals, so
they came through the type filter and sat in the dataset looking like perps until
the pair counts were reconciled. The `PF_` prefix is what actually marks one. The
eight already loaded are kept but relabelled `fixed_futures`, so a query joining
`kraken.instruments` filters them out.

`trade` is the feed that actually printed. `mark` is a separate series for the same
symbol, priced for liquidations; pass `--feed mark` if that is ever needed, and it
lands beside the trade series rather than overwriting it.
