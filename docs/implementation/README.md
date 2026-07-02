# Implementation Plans — Index

> Status: headings only (v0.1). Each plan gets its own detailed document (`NN-slug.md`) when its turn comes.
> Architecture reference: [solution-architecture.md](../architecture/solution-architecture.md) — see §16 for the component → plan map.
> Order is deliberate: every plan ends with the bot still runnable and safer/richer than before.

---

## 00 — Day-1 Walking Skeleton

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

**Goal:** trustworthy portfolio state on a balance-only venue (§4.8, §9.4 #2).

- Position reconstruction from recorded fills (average entry, realized/unrealized P&L)
- Cross-check against Kraken `TradesHistory` / `Ledgers`; discrepancy logging
- Portfolio snapshots (periodic + post-fill); exposure metrics for risk rules
- Fee accounting per trade — micro-trading economics dashboard data (fees vs. P&L)

## 08 — Paper Broker & Backtesting v1

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
