# Implementation Plans — Index

> Status: headings only (v0.1). Each plan gets its own detailed document (`NN-slug.md`) when its turn comes.
> Architecture reference: [solution-architecture.md](../architecture/solution-architecture.md) — see §16 for the component → plan map.
> Order is deliberate: every plan ends with the bot still runnable and safer/richer than before.

---

## Current Status (2026-07-02)

A first **console dry-run slice** exists in `src/TradingBot.Worker` (single .NET 8 project, no Docker/PostgreSQL yet). It runs one full decision cycle end to end against **sample data or Kraken public endpoints** and simulates fills in a virtual portfolio — **no real or `validate=true` orders are placed**. See [RUNBOOK.md](../../RUNBOOK.md).

Legend: ✅ done · 🚧 partial · ⬜ not started

| Area | Status | What exists today |
|---|---|---|
| Solution skeleton (§6) | 🚧 | Single `TradingBot.Worker` console project. **PostgreSQL persistence live** (Npgsql store: `portfolio_state` + `dry_run_cycles`; file store remains the no-DB fallback). No multi-project layout, no EF Core migrations yet. |
| Kraken market data (§4.1, §9.4) | 🚧 | Public `AssetPairs` (minimums/precision), `OHLC` polling, `Ticker` quotes; closed-candle filtering. No raw-payload store, no instrument registry, no CSV bootstrap. |
| Kraken broker adapter (§9.4) | 🚧 | Private API added: HMAC/nonce auth, `Balance`, `AddOrder` with `validate` flag ([00-live-kraken-ordering.md](00-live-kraken-ordering.md)). **Verified against the real exchange 2026-07-03: auth OK, real EUR balance read, first `validate=true` market order accepted (`VALIDATED_OK`, ADAEUR); validate-soak running since 13:09 UTC.** Real orders behind `LiveTradingEnabled=true`. Nonce not yet persisted; live-mode portfolio reconstruction still pending. |
| Indicators (§4.2) | 🚧 | EMA (fast/slow) + RSI computed from candles. No SMA/ATR/MACD, no test suite. |
| Signals / strategy (§4.4) | 🚧 | One hardcoded EMA-crossover + RSI + volatility scorer. Not yet a versioned `ISignalModule` registry. |
| Market Regime (§4.3) | ⬜ | No regime component (not even a `Normal` stub). |
| Decision Engine (§2.2, §4.5) | 🚧 | Deterministic score → `NONE` / `LONG_MICRO` with per-signal contribution breakdown. Config-driven scoring/versioning not done. |
| Risk Manager (§4.6, §11) | 🚧 | Kill switch, max-order-EUR cap, zero-notional guard; max-open-positions, per-cycle position limit, max-total-exposure, cash-reserve and buy/sell cooldowns enforced in the apply step. **Daily loss cap now enforced** (UTC-day realized-PnL tracking in portfolio state; new entries blocked with `DAILY_LOSS_BLOCK`, exits never blocked). Tiered exit policy: stop-loss/TP/max-hold bypass soft guards. |
| Execution Engine (§4.7) | 🚧 | Virtual Open/Close with notional→quantity conversion, taker-fee + slippage model, conservative mark-to-market. **Two-phase cycle:** held positions (exit/hold) always run first; new-entry BUY candidates are collected, **ranked (score → EMA gap → RSI quality → target → stable input order)** and executed best-first, so per-cycle/max-open limits go to the best candidates instead of CandidateUniverse order; candidates that lose the race are logged with `CYCLE_POSITION_LIMIT`. No Increase/Reduce, no real broker. |
| Portfolio / P&L (§4.8) | 🚧 | Virtual portfolio persisted to `data/dry-run/portfolio-state.json`; mark-to-market + realized/unrealized P&L; daily realized-PnL counter for the loss cap. Fresh portfolio auto-created with `Portfolio.StartingCashEur` (**75 EUR** by config decision 2026-07-03; code default 50) when the state file is missing, empty, or corrupt; an existing valid state is reused (verified 2026-07-03). `updatedAt` now refreshes on every mark-to-market, not only on fills. No broker balance fetch / reconstruction yet. |
| Audit / Replay (§4.9, §12) | 🚧 | Per-cycle journal with decisions, indicators, risk reasons, portfolio before/after — **now persisted to PostgreSQL (`dry_run_cycles`)** when the database is configured; JSONL file remains the fallback. No config versioning, no replay runner yet. |
| AI (§4.10, §10) | 🚧 | Optional OpenAI-compatible **watchlist** advisor (selection only) with heuristic fallback. Not the decision-path AI advisory of Plan 10. |
| Config (§4.11) | 🚧 | `appsettings.json` + `TRADINGBOT_*` env overrides. No versioning/stamping. |
| API / Dashboard (§4.12) | ⬜ | Console output only. |
| CI/CD (§13) | ⬜ | None. |

**Net:** this is an early, self-contained *paper/dry-run* preview that touches parts of Plans **00, 03, 07, 08** and a slice of **10** (watchlist only), but deliberately skips the live-broker (`validate=true`), PostgreSQL, and multi-project pieces that Plan 00 formally requires. The items below stay authoritative; each still needs completing on the real stack.

---

## 00 — Day-1 Walking Skeleton

**Status: 🚧 partial** — decision cycle, EMA/RSI, minimal Decision Engine, core risk caps + kill switch, audit-per-cycle, and now the Kraken private adapter with `Balance` + `AddOrder(validate=true)` all exist ([detailed plan](00-live-kraken-ordering.md)). **Still missing:** multi-project layout and `docker compose` + PostgreSQL/EF Core migrations (state/audit are still file-based).

**Goal:** one full decision cycle against Kraken, end to end, in `validate=true` mode. No live orders yet.

- .NET 9 solution skeleton (§6 structure), docker compose: `postgres` + `worker`
- Kraken adapters (minimal): `AssetPairs` (minimums/precision), OHLC polling, `Balance`, `AddOrder` with `validate=true` only
- One pair (SOL/EUR), one hardcoded strategy (EMA crossover + RSI filter), Market Regime = stub returning `Normal`
- Decision Engine (minimal): desired position `NONE` / `LONG (micro)` with score breakdown
- Risk Manager (core, not negotiable): max €2–5 per order, single position per instrument, daily loss cap, kill switch, `LiveTradingEnabled=false` default
- Execution Engine: notional→quantity conversion, Open/Close delta only
- Audit record for every cycle, including Hold / no-action
- EF Core migrations, minimal logging

## 01 — Go-Live Gate & Operations Hardening

**Goal:** first real €2 order on Kraken, safely; validate-soak → live procedure.

- Kraken API key hygiene: no-withdrawal permission, IP whitelist, nonce persistence across restarts
- **Live-mode state integrity (go-live blocker):** virtual fills are currently applied *before* the broker call — a live order error would leave a phantom virtual position, and live SELL volumes come from virtual quantities instead of real balances. Reorder to broker-result-first (or reconcile after) before `LiveTradingEnabled=true`.
- ToS read & confirmed (Kraken; T212 later in 09)
- ≥24h validate-mode soak with log review; go-live checklist
- Flip `LiveTradingEnabled=true`; first live micro-order; fee/slippage measurement vs. expectation, logged
- Post-trade balance fetch and storage (§4.8 refresh path)
- Error handling: 429/backoff, broker error taxonomy, kill-switch auto-trip on repeated errors / stale data (§11)
- Rate-limit budget accounting centralized in the adapter (§9.4)

## 02 — Market Data & Instrument Registry Foundation

**Goal:** proper market data layer replacing Day-1 shortcuts; the data the future backtests will replay.

- Raw + normalized storage for all ingested data (§4.1); append-only candle store
- Instrument registry from `AssetPairs`: venue routing, precision, minimums, calendars (§4.1)
- Kraken OHLCVT CSV bootstrap import (one-time deep history, §9.4)
- Data-quality metadata: gap detection, staleness flags; fail-safe on stale data
- `IMarketDataProvider` contract finalized; polling scheduler with per-endpoint budgets

## 03 — Indicators, Signals & Decision Scoring Framework

**Status: 🚧 partial** — EMA + RSI and a single hardcoded EMA-crossover/RSI/volatility scorer exist. **Still missing:** the `IIndicator` library (SMA/ATR/MACD + reference tests), the versioned `ISignalModule` registry, config-driven Decision Engine weights, and determinism/no-look-ahead unit tests.

**Goal:** replace the hardcoded strategy with the pluggable framework from §4.2–4.5.

- `IIndicator` library: EMA, SMA, RSI, ATR, MACD (+ tests against reference values)
- `ISignalModule` registry with versioning; first strategy re-implemented as a module
- Decision Engine v1: configurable scoring/weights, versioned config, explanation payloads
- `SignalContext` / `DecisionContext` finalized (point-in-time discipline, §2.1)
- Unit tests: determinism (same input ⇒ same output), no look-ahead access possible by construction

## 04 — Market Regime Engine v1

**Goal:** replace the `Normal` stub with the real rule-based classifier (§4.3).

- Regime taxonomy v1: trend (Bull/Bear/Range) + volatility (Normal/High) — resolves Open Question #13
- Deterministic rules (e.g., EMA slope + ATR percentile), versioned
- Regime history persistence; regime context wired into signals and decision scoring
- Regime-driven confidence adjustment in Decision Engine, visible in explanations

## 05 — Full Risk Rule Set & Position Transitions

**Goal:** complete §11 and the full Desired Position model (§2.2).

- All `IRiskRule` implementations: exposure caps, cooldown after losses, transition-aware rules (risk-increasing vs. risk-reducing)
- Platform-monitored stop-loss / take-profit: evaluation cycle emits Close/Reduce (§9.5)
- Execution Engine: full delta actions (Increase / Reduce) in addition to Open/Close
- **Rotation (opportunity-cost exit):** when slots/exposure are full and a top-quality fresh candidate (strong EMA gap, healthy RSI) is blocked only by capacity, allow closing the weakest held position (genuine signal-flip victim, age ≥ MinHold, PnL above a small floor) to fund it. Quality-gap threshold must clear the double round-trip friction (~2.2–2.5%); max 1 rotation/cycle, capped per day. **Phase 1: log-only `WOULD_ROTATE` records for ≥1 week, enable only if the measured benefit beats the measured cost.**
- Risk-limit config versioning; kill-switch drill (test criterion §13 #13)
- Slippage tolerance buffer on micro caps (§9.2 #3)

## 06 — Audit Journal & Replay

**Goal:** replay-from-persisted-snapshot proven by a test — success criterion §13 #12.

- Full `DecisionCycleSnapshot`: input references/hashes, indicator values, regime, signal versions, config version
- Config version stamping across the pipeline (§4.11)
- Replay runner: re-execute pipeline from a stored snapshot with **zero external calls**, assert functionally identical decision
- Integration test: at least one stored cycle replayed in CI
- Human-readable explanation generator ("why did the system buy X on date Y")

## 07 — Portfolio & P&L Reconstruction

**Status: 🚧 partial (virtual only)** — a persisted virtual portfolio with mark-to-market and realized/unrealized P&L + fee accounting exists in dry-run (auto-creates a fresh 50 EUR portfolio, reuses an existing one). **Still missing:** everything against a real venue — balance fetch, reconstruction from recorded fills, `TradesHistory`/`Ledgers` cross-check, DB snapshots.

**Goal:** trustworthy portfolio state on a balance-only venue (§4.8, §9.4 #2).

- Position reconstruction from recorded fills (average entry, realized/unrealized P&L)
- Cross-check against Kraken `TradesHistory` / `Ledgers`; discrepancy logging
- Portfolio snapshots (periodic + post-fill); exposure metrics for risk rules
- Fee accounting per trade — micro-trading economics dashboard data (fees vs. P&L)

## 08 — Paper Broker & Backtesting v1

**Status: 🚧 seedling** — the dry-run virtual fill model (fee + slippage, conservative mark-to-market) is a precursor to the Paper Broker, but it is bespoke worker code, not an `IPaperBroker : IBroker` implementation, and there is no backtest driver yet.

**Goal:** the secondary `IBroker` and the first backtest over accumulated + bootstrapped history (§12).

- `IPaperBroker` implementation: configurable fill model (next-candle fill, slippage, fees)
- Backtest driver: historical iterator → existing pipeline → Paper Broker → audit records
- Backtest report: trades, win rate, P&L, max drawdown, fee drag
- CI integration: deterministic backtest as regression test for strategy changes

## 09 — Trading 212 Venue (Equities Track)

**Goal:** second venue live; proves the abstraction is venue-agnostic (§9.1–9.3, §9.5).

- Equities market data provider selected (Open Question #3) and adapter built
- `Trading212BrokerAdapter` against practice env: auth, account, instruments, positions, market orders
- `Trading212MetadataProvider`; instrument registry extended with equities + market hours
- Empirical checks from §9.3 (demo vs. live order types, minimums, fees at €2–5)
- Instrument→venue routing proven end-to-end; equities universe selected (Open Question #1)
- Practice-env soak → first live equities micro-order

## 10 — AI Analysis Service v1

**Status: 🚧 adjacent** — an OpenAI-compatible advisor exists but only picks the **watchlist** (which pairs to evaluate), with a heuristic fallback; it never touches the decision path. The Plan 10 decision-path advisory (`IAIAnalysisProvider`, schema validation, persisted snapshots, bounded decision features) is not started.

**Goal:** first structured AI advisory input into the Decision Engine (§4.10, §10).

- `IAIAnalysisProvider` + one provider adapter; output schema + validation
- AI snapshots persisted (provider/model/version/prompt/confidence)
- Decision Engine consumes AI features as weighted, bounded inputs; missing/stale/invalid ⇒ discarded + audit-logged
- Out-of-band schedule (never blocks the decision cycle); cost budget & cadence (Open Question #11)

## 11 — Read-Only API & Dashboard

**Goal:** see what the bot is doing without psql (§4.12; UI mockups from the design phase).

- Minimal HTTP API: health, status, cycles, decisions, portfolio, orders, audit detail
- Dashboard v1: Ops status (kill switch, mode, budgets), decision cycle log, portfolio, trade history, decision explanation view
- Read-only via Application services / read models (§3) — no trading actions from UI in this plan

## 12 — CI/CD & Operations

**Goal:** repeatable builds and guarded deployments.

- GitHub Actions: build, unit + integration tests (Postgres service container), backtest regression
- Secrets handling for API keys; environment config strategy
- Deployment target hardening (the box the bot runs on), backup/restore for PostgreSQL
- Alerting: kill-switch trips, error bursts, data staleness → notification channel
