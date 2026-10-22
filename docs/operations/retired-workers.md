# Production retirement: LUKO and CryptoSmith X

Owner decision, 22 October 2026: active trading workers are **BYKO** (`futures-live`) and **PUKO** (`futures-pukis-live`) only. Arena reporting is a separate non-trading process. Their database, market-data worker, API/UI, proxy and other required infrastructure remain operational.

## LUKO

- `trading-bot-lukas-futures-worker-live` was stopped and removed without deleting volumes or placing/cancelling any Kraken order.
- The production Compose service, optional live profile, deployment environment/credential bindings, configuration copy and health/startup paths have been removed. The old PROD enable variable was set false as immediate containment; even a stale true value cannot start a service that no longer exists.
- The deployment retains only an idempotent removal of a stray old container. CI renders the production topology with default and legacy flags and rejects any reintroduced LUKO service.
- Diagnostics cover BYKO and PUKO. Historical database records, the old sample profile and the original PUKO strategy-profile provenance are retained; none starts LUKO or changes PUKO's current selected profile.

## CryptoSmith X

All seven containers in the dedicated `cryptosmithx` Compose project were stopped and removed, including its private PostgreSQL and completed migrator. Its unused project network was removed; shared proxy/trading networks were preserved.

The CryptoSmith X repository's `deploy` workflow is `disabled_manually`. On the host, the dedicated forced-command deployment entry point now refuses deployment (exit78); its original script, Compose manifest and public proxy routing configuration were moved to a root-only retirement archive. It cannot be brought back by a normal push or a stale SSH deployment trigger. CI of that repository remains available.

Persistent database files, keys and historical logs were not erased. Retiring a container does **not** close exchange positions or cancel exchange orders. Resuming any retired service requires a new explicit owner decision and a reviewed restoration, not re-enabling an old flag.

## Acceptance

- No running or stopped LUKO/CryptoSmith containers remain.
- A negative topology test rejects the previous Compose; the updated default/legacy-flag configurations contain only BYKO/PUKO trading workers and the separate Arena reporter.
- Verify ongoing BYKO/PUKO cycles, Arena snapshot freshness and API health after the normal main deployment. Read production SQL only; do not alter the historical ledger or strategy settings to hide retirement.
