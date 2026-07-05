# Core Extraction, Spot Worker Rename, and Futures Worker Blueprint

## Goal

Separate reusable trading analysis from execution-specific behavior so spot long-only trading and futures long/short trading cannot accidentally share unsafe order semantics.

The important boundary is:

- spot `SELL` means close/reduce an owned asset position;
- futures `SELL` can open short, reduce long, close long, or flip exposure unless guarded by `reduceOnly`.

Because of that, futures must be a separate worker and must start as dry-run only.

## Target Projects

```text
src/
  TradingBot.Core/
    Indicators/
    MarketData/
    Signals/
    Scoring/
    Diagnostics/
    Common/

  TradingBot.SpotWorker/
    LongOnlyStrategy
    SpotPortfolio
    SpotExecution
    SpotRisk
    SpotPersistence

  TradingBot.FuturesWorker/
    LongShortStrategy
    FuturesPortfolio
    FuturesExecution
    MarginRisk
    TpSlOrchestration
    DryRunPersistence

  TradingBot.Api/
```

Tests:

```text
tests/
  TradingBot.Core.Tests/
  TradingBot.SpotWorker.Tests/
  TradingBot.FuturesWorker.Tests/
```

## Current Repo Shape

Current worker code lives in `src/TradingBot.Worker`:

- `Indicators.cs`
- `MarketData.cs`
- `PriceAction.cs`
- `WatchlistAdvisor.cs`
- `Decisioning.cs`
- `EntryGate.cs`
- `EntryRanking.cs`
- `CycleDiagnostics.cs`
- `DomainModels.cs`
- `DryRunPortfolio.cs`
- `DryRunPortfolioStore.cs`
- `CorrelationRisk.cs`
- `ExecutionPolicyEngine.cs`
- `KrakenBroker.cs`
- `DecisionWorker.cs`
- `Program.cs`
- `BotConfiguration.cs`

There is currently one test project: `tests/TradingBot.Worker.Tests`.

## Extraction Boundaries

Move to `TradingBot.Core` first:

- pure indicator calculations: EMA, RSI, volatility, trend filters;
- price-action state and calculations;
- market-data DTOs that are not tied to Kraken spot order execution;
- watchlist advisor interfaces and model/heuristic ranking;
- scoring primitives and signal contribution types;
- entry diagnostics DTOs that describe signals, not spot portfolio actions;
- common metadata DTOs: cycle id, worker metadata, strategy metadata, market snapshot DTOs.

Keep in spot worker:

- spot portfolio state and cash/accounting;
- dry-run spot fills;
- spot position side model, currently long-only;
- spot stop-loss/take-profit policy;
- spot correlation and execution policy as currently implemented;
- Kraken Spot broker adapter;
- persistence format that is explicitly spot portfolio/action semantics;
- current `DecisionWorker` orchestration until it can be split safely.

Do not move yet:

- `DryRunPortfolio.cs` as-is, because it encodes spot position semantics;
- `KrakenBroker.cs`, because it is spot execution;
- risk rules that assume owned asset inventory instead of margin exposure.

## Phase 1: Rename Worker Without Behavior Change

Purpose: make the current semantics explicit before adding futures.

Steps:

1. Rename project directory:
   - `src/TradingBot.Worker` -> `src/TradingBot.SpotWorker`
   - `tests/TradingBot.Worker.Tests` -> `tests/TradingBot.SpotWorker.Tests`
2. Rename project files:
   - `TradingBot.Worker.csproj` -> `TradingBot.SpotWorker.csproj`
   - `TradingBot.Worker.Tests.csproj` -> `TradingBot.SpotWorker.Tests.csproj`
3. Update namespaces from `TradingBot.Worker` to `TradingBot.SpotWorker`.
4. Keep appsettings behavior and environment variable names stable unless there is a deliberate migration plan.
5. Preserve cycle metadata:
   - worker version;
   - commit;
   - build UTC;
   - image tag;
   - strategy version;
   - change set.
6. Update Docker/deploy references, CI test paths, and docs.
7. Run the full existing test suite.

Expected outcome: same decisions, same persistence shape, same bot behavior, clearer name.

## Phase 2: Extract Core Without Behavior Change

Purpose: make reusable analysis available to spot and futures without touching order semantics.

Suggested extraction order:

1. Create `src/TradingBot.Core/TradingBot.Core.csproj`.
2. Move pure files first:
   - `Indicators.cs`
   - pure parts of `PriceAction.cs`
   - pure market DTOs from `DomainModels.cs`
3. Move watchlist and signal types:
   - `WatchlistAdvisor.cs`
   - signal contribution DTOs;
   - score contribution primitives.
4. Move scoring/gate primitives only where they do not know about spot portfolio actions:
   - pure parts of `Decisioning.cs`
   - pure parts of `EntryGate.cs`
   - pure parts of `EntryRanking.cs`
5. Move diagnostics DTOs that describe candidate evaluation, not spot execution results:
   - pure parts of `CycleDiagnostics.cs`.
6. Add `TradingBot.Core.Tests` and move the tests that only cover core behavior:
   - indicator tests;
   - price-action tests;
   - watchlist cache/advisor tests;
   - entry gate/scoring tests where assertions do not depend on spot fills.
7. Keep compatibility shims only if they reduce churn. Remove them after tests settle.

Expected outcome: spot worker references core; behavior remains unchanged.

## Phase 3: Split Decision Model From Execution Model

Purpose: prevent futures from reusing spot-only desired-position/action concepts.

Introduce core-level signal intent:

```text
SignalIntent:
  None
  LongCandidate
  ShortCandidate
```

Keep execution-specific desired positions separate:

```text
SpotDesiredPosition:
  None
  LongMicro

FuturesDesiredExposure:
  Flat
  Long
  Short
```

Rules:

- Core may say "candidate looks long" or "candidate looks short".
- Spot worker may only translate long candidates into spot buys.
- Futures worker may translate long/short candidates into futures exposure changes.
- Core must not emit `BUY` or `SELL`.
- Execution layers own `BUY` / `SELL` semantics.

## Phase 4: Add Futures Worker Dry-Run Only

Purpose: collect evidence before any real futures order is possible.

Initial capabilities:

- read the same market snapshot/candle source;
- select active instruments;
- compute long and short signals;
- decide `FuturesDesiredExposure`;
- simulate position size;
- simulate leverage;
- simulate entries/exits;
- simulate TP/SL orders;
- calculate liquidation distance;
- persist dry-run decisions and diagnostics;
- never call live futures `sendorder`.

Suggested futures-only persisted fields:

- `side`: `LONG` / `SHORT`;
- `reduce_only`;
- `leverage`;
- `initial_margin_eur`;
- `maintenance_margin_eur`;
- `liquidation_price`;
- `liquidation_distance_percent`;
- `mark_price`;
- `index_price`;
- `funding_rate`;
- `unrealized_pnl_eur`;
- `unrealized_pnl_percent`;
- `tp_order_state`;
- `sl_order_state`;
- `exit_trigger_source`: `mark` / `index` / `last`.

Safety defaults:

- dry-run only;
- leverage cap `1x` or `2x`;
- no position flip;
- all simulated exits marked `reduceOnly=true`;
- max one futures position until diagnostics prove behavior;
- separate cash/margin ledger from spot portfolio.

## Phase 5: Kraken Futures API Adapter

Only after dry-run evidence is good.

Adapter responsibilities:

- instruments and contract specs;
- wallets/margin balances;
- open positions;
- leverage settings;
- order placement;
- order edits/cancels;
- dead man's switch;
- fills/order events;
- reduce-only enforcement.

Required guardrails before live:

- hard config flag: futures live disabled by default;
- integration tests for `sell` semantics;
- all stop-loss and take-profit orders use `reduceOnly=true`;
- pre-trade liquidation distance check;
- pre-trade max loss check;
- no order if current exchange position does not match local state;
- explicit emergency close path.

## Phase 6: API and Dashboard

Keep spot and futures visible as separate systems.

Dashboard/API should not merge them into one ambiguous portfolio table.

Suggested views:

- Spot portfolio;
- Spot dry-run decisions;
- Futures dry-run positions;
- Futures margin/liquidation diagnostics;
- Combined equity summary only as a top-level rollup.

## Test Strategy

Core tests:

- indicators;
- price action;
- scoring;
- watchlist normalization/cache;
- long/short signal generation.

Spot worker tests:

- current behavior replay;
- dry-run buys/sells;
- cash accounting;
- stop/take-profit behavior;
- correlation/execution policy;
- no behavior drift after core extraction.

Futures worker tests:

- `BUY` opens/increases long;
- `SELL` opens/increases short only when allowed;
- `SELL reduceOnly` closes/reduces long and never opens short;
- `BUY reduceOnly` closes/reduces short and never opens long;
- no accidental flip;
- liquidation distance calculation;
- margin cap blocks unsafe entries;
- TP/SL orchestration emits reduce-only simulated orders.

## Implementation Order

Recommended safe order:

1. Rename current worker to SpotWorker with no behavior change.
2. Extract pure `TradingBot.Core` types and tests.
3. Split core signal intent from spot execution action.
4. Add futures dry-run project with no live API.
5. Add futures persistence and dashboard diagnostics.
6. Run futures dry-run for several days.
7. Review edge and failure cases.
8. Add Kraken Futures adapter behind hard-disabled live flag.
9. Enable tiny-size live only after dry-run evidence and safety tests.

## What Not To Do

- Do not add futures to the existing spot worker.
- Do not reuse spot `DryRunPortfolio` for futures.
- Do not let core emit `BUY` or `SELL`.
- Do not treat spot `SELL` and futures `SELL` as the same action.
- Do not enable live futures before dry-run TP/SL/liquidation diagnostics exist.
- Do not raise size/leverage to compensate for low trade frequency.

