-- 001_init.sql
-- Applied automatically at startup by Data/Db.cs (Migrator) if not already recorded
-- in the __migrations table.

CREATE TABLE IF NOT EXISTS __migrations (
    filename    text PRIMARY KEY,
    applied_at  timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS trades (
    id            bigserial PRIMARY KEY,
    opened_at     timestamptz NOT NULL,
    closed_at     timestamptz,
    pair          text NOT NULL,
    side          text NOT NULL,
    size          numeric NOT NULL,
    entry_price   numeric NOT NULL,
    exit_price    numeric,
    stop_price    numeric NOT NULL,
    pnl           numeric,
    fee           numeric,
    close_reason  text
);

CREATE INDEX IF NOT EXISTS ix_trades_opened_at ON trades (opened_at DESC);

CREATE TABLE IF NOT EXISTS candles (
    pair        text NOT NULL,
    timeframe   text NOT NULL,
    open_time   timestamptz NOT NULL,
    o           numeric NOT NULL,
    h           numeric NOT NULL,
    l           numeric NOT NULL,
    c           numeric NOT NULL,
    volume      numeric NOT NULL,
    PRIMARY KEY (pair, timeframe, open_time)
);

CREATE TABLE IF NOT EXISTS bot_events (
    id      bigserial PRIMARY KEY,
    ts      timestamptz NOT NULL DEFAULT now(),
    level   text NOT NULL,
    message text NOT NULL,
    data    jsonb
);

CREATE INDEX IF NOT EXISTS ix_bot_events_ts ON bot_events (ts DESC);

CREATE TABLE IF NOT EXISTS app_state (
    key   text PRIMARY KEY,
    value text NOT NULL
);

INSERT INTO app_state (key, value) VALUES ('paused', 'false')
ON CONFLICT (key) DO NOTHING;
