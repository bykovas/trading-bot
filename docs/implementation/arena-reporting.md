# BYKO → Arena automatic reporting (Part C, #23)

## Delivery and activation

Part A is deployed. Part B's import recorded 11 positions / 34 native fills, 11 verified signals and 15 tape-matched fills (4 fully tape trades). It also declared 6 USD effective 11 October; that denominator was not the intended fixed trading capital and must not be reused. Historical correction is owner-managed, separate from this reporter change. Five manual/mixed cycles were excluded. The single imported open snapshot correctly expires after 180 seconds until this reporter is activated.

PR #24 is merged and its main deployment succeeded. PR #25 has been rebased onto that main for a fresh full CI run. On 22 October Denis explicitly authorized Codex to merge both PRs, deploy the reporter, and verify automatic trades/position reporting in production. Activation follows a green current branch and a successful main deployment; it does not require another approval.

The production compose adds `arena-reporter` using the same versioned image but a **separate executable**, `TradingBot.ArenaReporter.dll`. It has its own process, HTTP clients, database pool (maximum 2 connections), 512 MiB memory limit, half a CPU and persistent state. An old image without the reporting executable fails closed; it cannot fall through to the trading entry point. The trading Worker never calls the reporter. Its only behavioral addition is saving the opening webhook `firing_id` on the actual position, including normalized PostgreSQL persistence and clones. Decisions, risk, sizing, orders and existing cycle/version metadata are unchanged.

### Protected configuration

Only BYKO's `futures-live` configuration with live or explicitly enabled paper trading and database persistence is accepted. The other instances are not enabled. In live mode the reporter reads existing Kraken credentials through the GET-only history adapter; it never calls a broker or an order/transfer endpoint, and omits the optional nonce so it cannot advance the trading process's nonce.

### Paper execution reporting

With `LiveTradingEnabled=false` and `PaperTradingEnabled=true`, the reporter uses saved `MODELED_PORTFOLIO` fills, not Kraken executions. Set `ARENA_PAPER_SINCE_UTC` in the protected reporter `arena.env` to the explicit UTC instant the owner switched execution (ISO-8601 ending `Z`). Keep this boundary across restarts: it is not today's date, current balance or a backfill guess. An absent/invalid boundary stops the reporter, not the worker.

Paper closed trades and open/flat snapshots use Arena's existing `account: "demo"`, label `BYKO paper`, and a note saying executions/fees are modeled and funding is not modeled. Arena separates demo and live results; the demo view is `/omenas?account=demo`. Historical real trades retain their original IDs/account. Paper IDs are deterministic hashes of bot instance and opening journal identity, under the `paper-` prefix; the durable outbox is in `arena-state/paper`, separate from native reporting state. No capital is sent when unset.

The paper source reads holdings and executions in a read-only repeatable-read transaction, maps instruments through the public Kraken contract registry, and requires each full modeled close to match its complete opening, size, side, entry and historical leverage. It rejects mixed/exchange identities, missing costs, partial/multiple fills it cannot reconstruct and missing USD state. Snapshots must reconcile exactly to the saved portfolio. With open positions its timestamp must be within 180 seconds; a stopped worker creates a reporting gap, never a fabricated fresh flat. A proven flat journal/portfolio can heartbeat while unchanged: DEBYKO-only flat workers intentionally do not rewrite portfolio timestamps while waiting for signals. Limits fail explicitly rather than truncating history. Current virtual funding is zero because the simulator does not book funding, not because real funding is free.

Paper mode never constructs the private Kraken history reader and does not continue native history backfill. Any final real close must be reconciled through the existing native reporting/import path, not fabricated from a virtual journal. Previously published live snapshots expire normally; switching mode does not send an unverified empty live snapshot. Re-enabling live mode is a separate owner decision; it restores the original native source/outbox.

Supply the account-owned key as `DEBYKO_ARENA_KEY` in the protected runtime file `/opt/trading-bot/futures/live/arena.env` (owned by the deployment service account, mode0600), for example with a secret manager/editor. Never put the value in Git, a URL, a command argument or a log. This optional env file is mounted only into the reporter; `deploy.sh` does not rewrite it. Missing key/configuration leaves the reporter idle and logs one disabled message. It reuses the existing participant handle.

### Optional fixed trading capital

`ARENA_DECLARED_CAPITAL_USD` is a single positive invariant decimal, fixed by the owner, not account equity, available balance, margin per trade, notional, leverage or cash flows. The owner's current choice is **unset**: no capital value and no profile request are sent. PUKO and LUKO do not currently run this reporter; this change does not enable their reporting.

When explicitly configured, startup reads `GET /v1/profile`. Only a different amount or non-USD declaration causes one `PUT /v1/profile`, preserving the returned handle and bio. The request omits `effective_from` so server receipt time applies. Identical declarations cause no PUT. Configuration changes take effect by restarting only the reporter; positions, trades and periodic heartbeats never update capital. A full user key and the new Arena profile-read API (platform issue #641) are required for this optional operation; a restricted `arena:write` key cannot edit/read a profile.

Unset does not mean zero and does not clear a previous declaration. To enter no-capital/ROE mode, the owner must explicitly clear capital with `PUT /v1/profile` and `"declared_capital": null`, or perform the separately approved historical correction. No existing capital row is changed by this bot. Clearing now preserves earlier percentages, so it does not repair the old USD 6 denominator retroactively.

Invalid configuration or a profile-read/write error stops the reporting process at startup, leaving trading independent and unaffected. A PUT is not blindly retried after an uncertain response: on restart, GET checks what Arena actually holds before any new PUT. No profile amount is inferred when the endpoint or its response is unavailable.

The normal owner-approved main deploy starts the new service. To stop/restart reporting independently, use the existing runtime compose with `stop arena-reporter` / `up -d --no-deps arena-reporter`. Do not roll back this service to an image lacking the reporting executable. Stopping it does not stop the trading Worker. To erase Arena data intentionally, stop reporting and revoke its Arena key first; otherwise future explicitly authorized reports would create a new record.

## Source and identity

Kraken execution events, native position updates and the USD account log supply fills, position transitions, prices, quantities, fees and signed funding. The bot journal supplies **order ownership, the opening firing_id/rule and recorded leverage**. Current configuration is never substituted for historical leverage. Only cycles with every leg proven to be bot-owned are included. Manual or mixed cycles are not silently relabelled BYKO.

The importer and reporter use the same `PositionReconstruction`, `KrakenEvidence` and `BotJournalEvidence`. Partial closes/adds, reversals, opening history and native identifiers are preserved. Every closed trade uses `kraken-<native-symbol>-<first-native-execution-UID>`, account `live`, venue `KRAKEN-FUTURES`, label `BYKO`, native fills and times. Missing/ambiguous costs fail the source read rather than becoming zero. A distinct, immutable USD ledger booking for the same execution is added to its booked fees/funding; position-ledger rows are not counted as cash costs.

The first native capture is complete (including earlier opens). It can take several minutes and does not block fresh position heartbeats. Subsequent captures use the documented `since` timestamp in milliseconds with a 24-hour overlap for execution/position events and inclusive `from`/`to` IDs for new account-log bookings. **Previously captured history is retained**: the overlap is not a shortened reconstruction window. Complete tokens/pages, account identity, repeated IDs and byte bounds are checked. A source checkpoint is durably saved before reconstruction/queueing, so a later outbox failure does not discard venue facts. There is no order or transfer method in the adapter.

References: [execution events](https://docs.kraken.com/api-reference/account-history/get-execution-events), [position update events](https://docs.kraken.com/api-reference/account-history/get-position-update-events), [account log](https://docs.kraken.com/api-reference/account-history/get-account-log).

## Positions and freshness

A separate read-only connection observes BYKO's saved position scope every second. A change wakes the snapshot and history loops; a 60-second heartbeat also runs while unchanged or flat. Coalesced changes obtain the **current** native snapshot; an old snapshot is never queued for later delivery. Bot/manual ownership is retained. Direction, entry, size, native mapping, opening time and recorded leverage must reconcile. A mismatch, missing scope, account change or uninitialized state is an error, never a fabricated flat snapshot. A flat bot sends an explicit empty positions array.

The observation is rechecked against the saved scope after the native/API read and must be at most 60 seconds old before sending. Position HTTP requests are not internally retried with the same aging body: a retry reads native positions again, honors Retry-After, and sends current state. Arena itself stops valuing an open snapshot at 180 seconds and displays a gap. API/rate-limit failures can therefore cause an honest gap, without affecting order flow.

## Durable dispatch and corrections

Intent (including the first attempt time) is durably written **before** HTTP. Closed-trade retries use the exact same identity and body. 429 honors Retry-After even after the inner retry budget is exhausted; 5xx/network failures use bounded backoff. A 400 is retained as rejected and never automatically retried; 401/403/409 are also terminal for the queued trade. Logs contain status/counts and redacted failure details, never the key. Receipt counts must reconcile.

Settled fees/funding can change a payload under the same trade ID only before the first persisted attempt plus 24 hours; that conservative boundary never moves on retry/restart/update. Arena enforces its own actual first-receipt lock too. A first local response for an imported/existing trade (`accepted=0`) has an unknown original receipt time, so the reporter freezes further corrections instead of inventing a new 24 hour window. A newer cost payload arriving while an older HTTP request is in flight is kept for the next dispatch. Identical delivered payloads are not resent on each loop.

## Bounds and recovery

State is private (directory0700/files0600) under the separate `/opt/trading-bot/futures/live/arena-state` volume, mounted at `/app/data/arena`; `DEBYKO_ARENA_STATE_DIRECTORY` overrides it for isolated fixtures. The open file lock prevents two local dispatchers sharing the same state.

- Outbox: maximum 10,000 known trade identities,1,000 pending entries,16 MiB serialized state.
- Native input:64 MiB facts; serialized source checkpoint at most80 MiB. Original opens/identities are retained; a full checkpoint fails explicitly instead of truncating history.
- Protective order provenance: at most10,000 identities / 8 MiB, retained across log rotation. Only bounded tails of four worker log files are inspected.
- HTTP requests retain the existing 30 second timeout and64 KiB / 100 trade payload bounds; no platform limit/rate limit was raised.

Corrupt/full state, account mismatch, incomplete history or failed durable writes are logged and leave the prior checkpoint intact. Do not delete state, change native IDs or reset Arena to bypass a failure/lock. Stop only the reporter, retain its private audit files, correct the source/configuration and review recovery while preserving first-attempt times. Rejected 400 entries need an explicit reviewed repair, not an automatic retry. The trading process continues independently, including if the reporting process is killed or OOMs.

## Verification

Automated tests use fake HTTP/clock/source/transport or loopback only. They cover native pagination and incremental bounds, economic corrections, deterministic IDs, partial/reversal reconstruction, restart/receipt persistence,24 hour boundaries,400/401/409 terminal handling,429 Retry-After, failed checkpoints, queue bounds, in-flight correction races, native snapshot mismatch/freshness and explicit flat heartbeats. The existing trading tests remain unchanged and green. Real local PostgreSQL additionally verified normalized save/load, the appended firing_id ordinal, cloning and an additive upgrade from the legacy table.

Owner-authorized production idempotency verification on 22 October used the approved imported manifest through the same `ArenaSubmission.TradesAsync` transport: **HTTP 200, submitted 11, accepted 0, updated 0**. Profile and positions were not sent. The digest-bound receipt is retained privately outside Git; no worker was activated.

For a later identical verification, use the separate importer tool with the protected environment and the original approved private manifest:

```sh
dotnet run --no-build -c Release --project tools/TradingBot.ArenaImport -- replay-trades <approved-plan.json> <approved-sha256>
```

It validates the whole plan/digests/recorded schema/registry before any write, uses the original IDs, records private receipts, requires every response to be a no-op, and never sends profile/positions or deletes data.
