# Plan 00 — Live Kraken Ordering (dry-run → validate → live)

> Status: 🚧 in progress. Goal: get from the current file-based dry-run to real micro-orders on Kraken, through a safe validate step, with a single deliberate switch to go live.
> Architecture: [solution-architecture.md](../architecture/solution-architecture.md) §9.4 (Kraken), §11 (Risk). Index: [README.md](README.md).

## The three stages

| Stage | Config | What the bot does with orders | Money at risk |
|---|---|---|---|
| **1. Dry-run** (tonight) | `LiveTradingEnabled=false`, no Kraken keys | Simulates fills in the virtual portfolio only. Prints `WOULD_BUY` / `WOULD_SELL`. | None |
| **2. Validate** (tomorrow) | `LiveTradingEnabled=false`, Kraken **read+trade** keys set | On every `WOULD_BUY`/`WOULD_SELL` it also sends the order to Kraken with `validate=true` — the exchange checks auth, pair, minimums, precision **without executing**. Prints `broker=VALIDATED_OK` / `VALIDATE_REJECTED`. | None |
| **3. Live** | `LiveTradingEnabled=true`, keys set | The same order is sent with `validate=false` → **a real market order executes**. Prints `broker=LIVE_SUBMITTED txid=...` and refreshes the real balance. | Real (micro, ~€2–5) |

The `validate` flag is **derived from config**, never edited by hand:

```
liveActive = LiveTradingEnabled AND kill-switch OFF AND Kraken keys configured AND market data = kraken
validate   = NOT liveActive
```

Default is always the safe path. Going live is one explicit flag (`TRADINGBOT_LIVE_TRADING_ENABLED=true`), guarded by the risk gate that already produced the `WOULD_BUY`/`WOULD_SELL`.

## Safety gates (all must hold before a real order)
1. `LiveTradingEnabled=true` (explicit).
2. Kill switch off (`Risk.KillSwitch=false`).
3. Decision passed the Risk Manager (order only sent on `WOULD_BUY`/`WOULD_SELL`).
4. Order notional ≤ `Risk.MaxOrderEur` (re-checked right before a live buy).
5. Volume ≥ pair `ordermin` and rounded to pair `lot_decimals` (from `AssetPairs`).
6. API key has **no withdrawal permission** (set on Kraken when creating the key).

## Configuration — one file
All worker config lives in a **single `appsettings.json`**: market data mode, loop interval, risk/strategy, `LiveTradingEnabled`, and **both API keys** (`Kraken.ApiKey` / `Kraken.ApiSecret` and `Ai.ApiKey`). Nothing is forced via environment variables in production.

- **On the server:** edit `/opt/trading-bot/appsettings.json` by hand (it is a host-mounted, git-ignored file — safe place for real keys) and restart the container:
  ```bash
  docker restart trading-bot-worker
  ```
- **In the repo:** the committed `src/TradingBot.Worker/appsettings.json` keeps the key fields **empty** — never commit real secrets. `deploy.sh` seeds the server file from it on the first deploy only, then your host edits persist.
- Create the Kraken API key with **Query Funds + Create/Modify Orders** only (**no withdrawal**).
- Env vars (`TRADINGBOT_*`) still work as optional local overrides, but are not required and not used on the server.

The relevant `appsettings.json` fields:
```json
"Kraken":  { "MarketDataMode": "kraken", "ApiKey": "", "ApiSecret": "" },
"Ai":      { "Provider": "none", "ApiKey": "", "Model": "" },
"Trading": { "LiveTradingEnabled": false }
```

## What is implemented in this iteration
- `KrakenBroker`: private-API auth (HMAC-SHA512 + monotonic nonce), `Balance`, `AddOrder` with `validate` flag.
- Startup: fetches and prints real EUR balance when keys are set (proves auth works); prints a loud warning when `LiveTradingEnabled=true`.
- Decision cycle: on `WOULD_BUY`/`WOULD_SELL`, sends the order to Kraken (validate or live per the gate) and prints the exchange verdict; the verdict is also written to the event journal.

## Known limitations (follow-ups → Plan 01)
- Nonce is process-monotonic (ms-based), not yet persisted across restarts — fine for a single worker; persist in Plan 01.
- In **live** mode the virtual dry-run portfolio still simulates alongside the real order (double bookkeeping). For the first live test, trust the printed real `Balance`; full position reconstruction from `Balance`/`TradesHistory` is Plan 01. Recommend `rm -rf data/dry-run` when first switching to live so virtual state doesn't confuse the picture.
- No 429/backoff taxonomy or auto kill-switch on repeated broker errors yet (Plan 01).

## Run recipes
Tonight — dry-run (unchanged):
```bash
TRADINGBOT_MARKET_DATA_MODE=kraken TRADINGBOT_RUN_ONCE=false TRADINGBOT_LOOP_INTERVAL_SECONDS=300 \
dotnet run --project src/TradingBot.Worker/TradingBot.Worker.csproj
```

Tomorrow — validate against the exchange (no execution):
```bash
TRADINGBOT_MARKET_DATA_MODE=kraken TRADINGBOT_RUN_ONCE=false \
TRADINGBOT_KRAKEN_API_KEY=... TRADINGBOT_KRAKEN_API_SECRET=... \
dotnet run --project src/TradingBot.Worker/TradingBot.Worker.csproj
```

Go live — real micro-orders (only when ready):
```bash
TRADINGBOT_MARKET_DATA_MODE=kraken TRADINGBOT_RUN_ONCE=false \
TRADINGBOT_KRAKEN_API_KEY=... TRADINGBOT_KRAKEN_API_SECRET=... \
TRADINGBOT_LIVE_TRADING_ENABLED=true \
dotnet run --project src/TradingBot.Worker/TradingBot.Worker.csproj
```
