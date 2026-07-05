# Trading Bot Database Access

Use this note when an AI agent needs read-only diagnostics against the trading bot PostgreSQL database.

## Connection

Inside Docker/networked deployment the app connection string uses:

```sh
Host=database;Port=5432;Database=tradingbot;Username=tradingbot;Password=<password>
```

From this local workstation, use the VPN/reachable host instead of the Docker service name:

```sh
Host=10.8.0.1;Port=5432;Database=tradingbot;Username=tradingbot;Password=<password>
```

Do not commit the real password. Pass it through `TRADINGBOT_DATABASE_CONNECTION_STRING` in the shell environment.

For this workstation, local-only secrets may be stored in ignored files under `.ai/private/`.
The usual local file is:

```sh
.ai/private/database.env
```

Load it before running database diagnostics:

```sh
set -a
source .ai/private/database.env
set +a
```

## Practical Notes

- `psql` may not be installed on the workstation.
- Existing diagnostics have used small temporary .NET/Npgsql console tools under `/private/tmp`.
- Keep diagnostics read-only:
  - open a transaction;
  - run `set transaction read only`;
  - do not write to application tables.
- Prefer querying persisted dry-run tables/views:
  - `dry_run_cycles`
  - `dry_run_decisions`
  - `dry_run_cycle_summary`
  - `dry_run_cycle_entry_diagnostics`
  - `market_snapshots`
  - `portfolio_summary`
  - `portfolio_positions`

## Example Command

```sh
TRADINGBOT_DATABASE_CONNECTION_STRING='Host=10.8.0.1;Port=5432;Database=tradingbot;Username=tradingbot;Password=<password>' \
dotnet run --project /private/tmp/tradingbot-dbcheck/tradingbot-dbcheck.csproj --no-restore
```

Or, when `.ai/private/database.env` exists:

```sh
set -a
source .ai/private/database.env
set +a
dotnet run --project /private/tmp/tradingbot-dbcheck/tradingbot-dbcheck.csproj --no-restore
```

If the tool does not exist yet, create it under `/private/tmp`, reference `Npgsql`, and keep it outside the repository.
