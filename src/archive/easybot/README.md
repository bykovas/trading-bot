# easybot

Single-project ASP.NET Core crypto trading bot: a Razor Pages dashboard and a
`TradingWorker` `BackgroundService` running in the same process, trading Kraken Futures
via [KrakenExchange.Net](https://www.nuget.org/packages/KrakenExchange.Net).

Demo mode is on by default (`Bot:DemoMode = true`), which points the exchange client at
Kraken's public Futures demo environment (`https://demo-futures.kraken.com`). Real trading
only happens if `Bot:DemoMode` is explicitly set to `false` in configuration.

## Running locally

1. Set Kraken Futures API credentials via user-secrets (never commit real keys):

   ```bash
   dotnet user-secrets init --project src/easybot
   dotnet user-secrets set "Kraken:FuturesApiKey" "your-demo-key" --project src/easybot
   dotnet user-secrets set "Kraken:FuturesApiSecret" "your-demo-secret" --project src/easybot
   ```

2. Point `ConnectionStrings:Postgres` at a running Postgres instance (via
   `appsettings.Development.json`, user-secrets, or the `ConnectionStrings__Postgres`
   environment variable). The schema is created automatically on startup from
   `Data/migrations/001_init.sql`.

3. Run it:

   ```bash
   dotnet run --project src/easybot
   ```

   The dashboard binds to `http://127.0.0.1:5080` only (localhost, no auth).

## Layout

- `Trading/` — `Strategy.cs` and `PositionSizer.cs` are pure, unit-tested functions;
  `TradingWorker.cs` is the candle-close-aligned trading loop; `ExchangeClient.cs` wraps
  KrakenExchange.Net's FuturesApi with retry/backoff; `BotState.cs` reconciles local state
  against the exchange (source of truth) on startup.
- `Data/` — Npgsql connection factory, a tiny SQL-file migrator, and Dapper repositories.
- `Pages/` — the single-page dashboard.
- `Logging/` — a Serilog sink that mirrors log events into the `bot_events` table.
- `Trading/Backtest/` — placeholder, not implemented yet.

## Tests

```bash
dotnet test src/easybot.Tests
```
