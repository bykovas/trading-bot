-- Historical Kraken Futures data for research: funnel width experiments, backtests,
-- and anything that needs more past than the live bot keeps.
--
-- This belongs in its own database, not in `tradingbot`. The trading database is
-- 18 GB of live journal that drives real money; this is a few gigabytes of bulk
-- import with completely different retention, vacuum and backup needs, and it must
-- be droppable and rebuildable without anyone thinking twice. Same Postgres
-- instance, so there is nothing extra to run, monitor or back up:
--
--   docker exec trading-bot-db psql -U tradingbot -d postgres \
--     -c "create database tradingbot_research"
--
-- Reaching across to the live journal later is still possible with postgres_fdw or
-- a dump; the separation costs nothing and buys a clean blast radius.

create table if not exists kraken_instruments (
    symbol text primary key,
    pair text not null,
    base text,
    quote text,
    type text not null,
    tradeable boolean not null,
    -- How far back this symbol can possibly go. Bounds the walk per symbol instead
    -- of hammering the API for years that never existed.
    opening_date timestamptz,
    contract_size numeric,
    tick_size numeric,
    tags text,
    fetched_at timestamptz not null default now()
);

create table if not exists kraken_candles (
    symbol text not null,
    -- Kraken serves three separate series per symbol. `trade` is what actually
    -- printed, `mark` is what liquidations are priced off. They differ, and a
    -- backtest that mixes them silently is wrong, so the feed is part of the key.
    feed text not null,
    resolution text not null,
    open_time timestamptz not null,
    open numeric not null,
    high numeric not null,
    low numeric not null,
    close numeric not null,
    volume numeric not null,
    primary key (symbol, feed, resolution, open_time)
);

-- Cross-symbol scans by time are the whole point of this table: "what did all 285
-- perps do in this window". The primary key leads with symbol and cannot serve that.
create index if not exists ix_kraken_candles_slice
    on kraken_candles (feed, resolution, open_time, symbol);

-- One row per series being dumped, so a run that dies halfway resumes instead of
-- starting over. 285 symbols x ~77 pages is not something to repeat by accident.
create table if not exists kraken_dump_progress (
    symbol text not null,
    feed text not null,
    resolution text not null,
    -- Next `from` to request. Null once the series is complete.
    cursor_time timestamptz,
    earliest_open_time timestamptz,
    latest_open_time timestamptz,
    candle_count bigint not null default 0,
    request_count bigint not null default 0,
    complete boolean not null default false,
    last_error text,
    updated_at timestamptz not null default now(),
    primary key (symbol, feed, resolution)
);

-- Coverage at a glance: what is loaded, how deep, and what is still missing.
create or replace view kraken_dump_coverage as
select
    progress.resolution,
    progress.feed,
    count(*) filter (where progress.complete) as complete_symbols,
    count(*) filter (where not progress.complete) as pending_symbols,
    sum(progress.candle_count) as candles,
    min(progress.earliest_open_time) as earliest,
    max(progress.latest_open_time) as latest
from kraken_dump_progress progress
group by progress.resolution, progress.feed;
