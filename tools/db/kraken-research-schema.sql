-- Historical Kraken Futures data for research: funnel width experiments, backtests,
-- and anything that needs more past than the live bot keeps.
--
-- It lives in a plain `research` database under its own `kraken` schema, so the
-- database stays generic and the next dataset gets a schema beside this one rather
-- than a database of its own. Everything here can be dropped with one statement:
--
--   drop schema kraken cascade;
--
-- It is deliberately not in the trading database. That one is 18 GB of live journal
-- driving real money; this is bulk import with completely different retention and
-- vacuum needs that has to be rebuildable without anyone thinking twice.

create schema if not exists kraken;

create table if not exists kraken.instruments (
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

create table if not exists kraken.candles (
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
create index if not exists ix_candles_slice
    on kraken.candles (feed, resolution, open_time, symbol);

-- One row per series being dumped, so a run that dies halfway resumes instead of
-- starting over. 285 symbols x ~77 pages is not something to repeat by accident.
create table if not exists kraken.dump_progress (
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
create or replace view kraken.dump_coverage as
select
    progress.resolution,
    progress.feed,
    count(*) filter (where progress.complete) as complete_symbols,
    count(*) filter (where not progress.complete) as pending_symbols,
    sum(progress.candle_count) as candles,
    min(progress.earliest_open_time) as earliest,
    max(progress.latest_open_time) as latest
from kraken.dump_progress progress
group by progress.resolution, progress.feed;
