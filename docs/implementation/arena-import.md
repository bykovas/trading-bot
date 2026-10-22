# BYKO → Arena import (issue #22)

Ordered delivery: platform#584/PR#588 is deployed; this is Part B. Part C is trading-bot#23. Do not run a send without Denis present and approving the full preview and capital. Do not merge/deploy a trading worker as part of the import. No manual SQL writes and no DELETE /v1/me.

## Read-only evidence capture

The separate executable starts no worker and has no order method. It uses only GET to Kraken's fixed origin, refuses redirects, requires general=READ_ONLY and transfer=NO_ACCESS, and reads the complete execution history and account log (including opens before 2026-10-11). It never uses the live worker's credentials. Old account-log readers in the worker are not the import source: a best-effort response or a page limit cannot prove completeness.

Supply a separate temporary read-only Kraken key through `TRADINGBOT_KRAKEN_FUTURES_API_KEY` and `TRADINGBOT_KRAKEN_FUTURES_API_SECRET` in a protected environment. Arena's own key is `DEBYKO_ARENA_KEY`; none is a CLI argument, URL, committed file or log entry. Revoke the temporary key after the owner-supervised import. Do not copy production trading credentials into the import environment.

```sh
# Restore/build on the developer machine; the capture is a manual live operation, never an automated test.
dotnet build tools/TradingBot.ArenaImport -c Release
dotnet run --no-build -c Release --project tools/TradingBot.ArenaImport -- capture "$HOME/.local/share/byko-arena-import/capture.json"
```

The output directory must be outside a Git worktree and mode0700; each new evidence file is mode0600 and cannot overwrite an earlier one. A complete capture is an intermediate input, never an approved import. Current open-position receipt time is recorded separately from the historical cutoff; prepare/send must reconcile them and require fresh open evidence.

Pagination: executions use the opaque continuation token; account log uses inclusive `to=min_id-1` (not `before`, which is milliseconds). Empty/malformed/error arrays, repeated IDs/tokens, account changes, HTTP errors or safety bounds fail the whole capture. Nothing becomes an empty or partially complete history. Requests are paced at12s, transient429/5xx retries are bounded, and Retry-After wins. Byte/page safety bounds fail explicitly; they never shorten an import window.

Primary contracts (Kraken moved its documentation URLs): [execution events](https://docs.kraken.com/api-reference/account-history/get-execution-events), [account log](https://docs.kraken.com/api-reference/account-history/get-account-log), [key permissions](https://docs.kraken.com/api-reference/api-keys/check-v3-api-key). No external address is called by the tests; HTTP and waits are faked. Local net8 tests can use DOTNET_ROLL_FORWARD=Major when only the net10 runtime is installed; CI runs net8.

## Remaining before any send

Position reconstruction and fees/funding reconciliation; journal-only firing/label/exit annotations; complete OpenAPI/400 preflight; capital/effective_from and preview; digest/checkpoints/batches≤100 and64KiB; owner approval; fresh-open-position recheck; profile→trades→positions; counts and public percentage curve/tape/signal report. No import has been executed.
