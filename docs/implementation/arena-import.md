# BYKO → Arena import (issue #22)

Ordered delivery: platform#584/PR#588 is deployed; this is Part B. Part C is trading-bot#23. Do not run a send without Denis present and approving the full preview and capital. Do not merge/deploy a trading worker as part of the import. No manual SQL writes and no DELETE /v1/me.

## Read-only evidence capture

The separate executable starts no worker and has no order method. It uses only GET to Kraken's fixed origin and refuses redirects. The default `capture` requires general=READ_ONLY and transfer=NO_ACCESS. Owner decision (22 October): `capture-existing-key-readonly` may instead read using the existing working key, through the same GET-only allowlist. It omits the optional Futures Nonce header so it cannot advance the live bot's nonce; it never changes or revokes that key. See [Kraken Futures authentication](https://docs.kraken.com/exchange/guides/futures/rest).

Credentials are read only from `TRADINGBOT_KRAKEN_FUTURES_API_KEY` / `TRADINGBOT_KRAKEN_FUTURES_API_SECRET` in the protected runtime environment. Arena's key is `DEBYKO_ARENA_KEY`; none is a CLI argument, URL, committed file or log entry.

Only positions opened **and** closed by BYKO are selected. The journal provides proof of order ownership and annotations, not prices, quantities, fees or funding. Those facts come from Kraken fills/position events and the account log. Manual positions and cycles with any unproven/manual leg are excluded and reported; a manually closed bot position is not silently treated as a bot close. Matching symbols, times or P&L alone is not ownership proof.

```sh
# Restore/build on the developer machine; the capture is a manual live operation, never an automated test.
dotnet build tools/TradingBot.ArenaImport -c Release
dotnet run --no-build -c Release --project tools/TradingBot.ArenaImport -- capture "$HOME/.local/share/byko-arena-import/capture.json"
```

The output directory must be outside a Git worktree and mode0700; each new evidence file is mode0600 and cannot overwrite an earlier one. A complete capture is an intermediate input, never an approved import. Position events preserve venue old/new state; the complete cycle must reconcile. Current open-position receipt time is recorded separately from the historical cutoff; prepare/send must reconcile them and require fresh open evidence.

Pagination: executions use the opaque continuation token; account log uses inclusive `to=min_id-1` (not `before`, which is milliseconds). Empty/malformed/error arrays, repeated IDs/tokens, account changes, HTTP errors or safety bounds fail the whole capture. Nothing becomes an empty or partially complete history. Requests are paced at12s, transient429/5xx retries are bounded, and Retry-After wins. Byte/page safety bounds fail explicitly; they never shorten an import window.

Primary contracts (Kraken moved its documentation URLs): [execution events](https://docs.kraken.com/api-reference/account-history/get-execution-events), [account log](https://docs.kraken.com/api-reference/account-history/get-account-log), [key permissions](https://docs.kraken.com/api-reference/api-keys/check-v3-api-key). No external address is called by the tests; HTTP and waits are faked. Local net8 tests can use DOTNET_ROLL_FORWARD=Major when only the net10 runtime is installed; CI runs net8.

## Remaining before any send

Position reconstruction and fees/funding reconciliation; journal-only firing/label/exit annotations; complete OpenAPI/400 preflight; capital/effective_from and preview; digest/checkpoints/batches≤100 and64KiB; owner approval; fresh-open-position recheck; profile→trades→positions; counts and public percentage curve/tape/signal report. No import has been executed.
