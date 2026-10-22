# BYKO → Arena import (issue #22)

Ordered delivery: platform#584/PR#588 is deployed; this is Part B. Part C is trading-bot#23. Do not run a send without Denis present and approving the full preview and capital. Do not merge/deploy a trading worker as part of the import. No manual SQL writes and no DELETE /v1/me.

## Read-only evidence capture

The separate executable starts no worker and has no order method. It uses only GET to Kraken's fixed origin and refuses redirects. The default `capture` requires general=READ_ONLY and transfer=NO_ACCESS. Owner decision (22 October): `capture-existing-key-readonly` may instead read using the existing working key, through the same GET-only allowlist. It omits the optional Futures Nonce header so it cannot advance the live bot's nonce; it never changes or revokes that key. See [Kraken Futures authentication](https://docs.kraken.com/exchange/guides/futures/rest).

Credentials are read only from `TRADINGBOT_KRAKEN_FUTURES_API_KEY` / `TRADINGBOT_KRAKEN_FUTURES_API_SECRET` in the protected runtime environment. Arena's key is `DEBYKO_ARENA_KEY`; none is a CLI argument, URL, committed file or log entry.

Only positions opened **and** closed by BYKO are selected. The journal provides proof of order ownership and annotations, not prices, quantities, fees or funding. Those facts come from Kraken fills/position events and the account log. Manual positions and cycles with any unproven/manual leg are excluded and reported; a manually closed bot position is not silently treated as a bot close. Matching symbols, times or P&L alone is not ownership proof.

```sh
# Restore/build on the developer machine; the capture is a manual live operation, never an automated test.
dotnet build tools/TradingBot.ArenaImport -c Release
dotnet run --no-build -c Release --project tools/TradingBot.ArenaImport -- capture "/tmp/byko-arena-import/capture.json"
```

The output directory must be outside a Git worktree and mode0700; each new evidence file is mode0600 and cannot overwrite an earlier one. A complete capture is an intermediate input, never an approved import. Position events preserve venue old/new state; the complete cycle must reconcile. Current open-position receipt time is recorded separately from the historical cutoff; prepare/send must reconcile them and require fresh open evidence.

Pagination: executions use the opaque continuation token; account log uses inclusive `to=min_id-1` (not `before`, which is milliseconds). Empty/malformed/error arrays, repeated IDs/tokens, account changes, HTTP errors or safety bounds fail the whole capture. Nothing becomes an empty or partially complete history. Requests are paced at12s, transient429/5xx retries are bounded, and Retry-After wins. Byte/page safety bounds fail explicitly; they never shorten an import window.

Primary contracts (Kraken moved its documentation URLs): [execution events](https://docs.kraken.com/api-reference/account-history/get-execution-events), [account log](https://docs.kraken.com/api-reference/account-history/get-account-log), [key permissions](https://docs.kraken.com/api-reference/api-keys/check-v3-api-key). No external address is called by the tests; HTTP and waits are faked. Local net8 tests can use DOTNET_ROLL_FORWARD=Major when only the net10 runtime is installed; CI runs net8.

## Reconstruct, preview, validate, then prepare

`preview <private-directory>` reads `capture.json`, `journal.json`, `protection-orders.log` and writes a digest-named private preview. The journal export is read-only and scoped to futures-live. Accepted-order records and exact placed protective-order IDs establish ownership; EXCHANGE_CLOSE and generic reconstructed closes do not. An expired trailing log needs both the exact trailing-stop closure journal and Kraken's bot client ID. Manual/mixed cycles are listed, not silently imported.

Native execution UID joins position changes; the position event's fillTime may refer to the original opening time, and its event timestamp can lag execution time. The full signed state must reconcile. Complete cycles reuse one reconstruction engine, including partial closes, adds and reversals. Costs for included holding periods come from USD account-log bookings, not rounded bot models or unrounded indicative position accruals. Cash and position ledger rows are not double-counted. No missing USD cost row becomes a guessed zero. Owner instruction22October: use the accepted-entry journal's historical leverage, falling back only to its recorded requested leverage, not today's config. All11selected trades and the DOT snapshot record10x; source is bot-declared leverage, not a claim that the venue reports historical margin.

Record the live public Arena OpenAPI as `openapi.json` and the complete normalized KRAKEN-FUTURES registry as `registry.json` (`ArenaRegistryListing[]`); keep the original public registry response too. No remote schema references are resolved. `check <private-preview.json>` runs the entire payload through schemas and semantic checks, plus <=100 and <=64KiB batching. Its in-memory 1 USD placeholder is explicitly NOT a capital declaration or a saved/sent profile.

After Denis confirms the table and capital: `prepare <private-preview.json> <confirmed-capital-USD>` produces the canonical profile/trades/positions plan and a SHA256 digest. The profile is omenas/live, USD, effective_from=2026-10-11T00:00:00Z, owner-approved bio. Preparation is not permission to send. A changed table/profile/contract requires new validation and approval. Retain original captures and earlier previews.

## Owner-supervised send and verification

1. With the owner present, record approval of the actual payload and capital/digest. No automatic import.
2. Obtain fresh `snapshot-existing-key-readonly <private-new-file.json>` in the same protected Kraken runtime. Copy it privately; match native direction/size/entry and the complete approved open scope. A changed position or age >60s blocks profile/trade writes; refresh is checked again before positions.
3. Supply DEBYKO_ARENA_KEY in the protected environment (never CLI/URL/log/repo), then `send <prepared-plan.json> <approved-sha256> <fresh-native-snapshot.json>`.
4. The executable validates the WHOLE plan before the first write and enforces profile -> trade batches -> positions. Every receipt is saved exclusively as a private checkpoint. 400/401/409 do not retry; transient429/5xx use bounded backoff/Retry-After with the same payload. A checkpoint or count error stops the next step. Re-running an approved identical trade ID/payload is a no-op; do not fabricate a new ID to bypass a failure/lock. Never DELETE/reset, especially now that Arena is public.
5. Check response totals and the public omenas record: first/last close times, every imported trade/label, tape vs self and verified signal counts, percentage curve/basis and late-report labels. An accepted HTTP response alone does not finish #22. A single open snapshot expires after180s until Part C is activated; do not claim it supplies a continuous heartbeat.

## Delivery plan and actual checkpoint (22 October)

- [x] A: platform#584 / PR#588 deployed; contract/docs and capital effective_from.
- [x] B: private GET-only capture; bot ownership; open-to-flat reconstruction; booked costs; full OpenAPI/semantic preflight and exact size bounds; canonical approval digest/checkpoints and fake submission tests.
- [x] Real capture completed: 2281 execution events, 7619 ledger rows, 4853 position events. Temporary runtime/host process/files cleaned, live worker unchanged.
- [x] Owner preview: 11 bot closed positions /34 native fills, one DOT short open, five manual/mixed cycles excluded. All11 have journal-confirmed firing IDs; actual Arena verification/tape status still unknown. Technical offline preflight: one10543-byte trade batch including the owner-requested10x leverage. Original account evidence stays outside Git.
- [ ] Owner approved import of the table and requested historical leverage. Capital was still supplied as literal<N>; numeric USD amount is pending. No write has been executed.
- [ ] Fresh native snapshot; import; API receipts and public curve/tape/signal acceptance; close B only after evidence.
- [ ] C (#23): only after B; independent fail-safe automatic feed PR. Denis merges it; never deploy the worker automatically.
