# PLAN — Exit regime D + LUKO-divergence marker (BYKO only)

> TEMPORARY. Delete this file once every task box is checked and merged.
> Work in SMALL commits. After each, tick the box and note the commit hash here.
> Arm = BYKO = `src/TradingBot.FuturesWorker/appsettings.json`. Control = LUKO =
> `appsettings.lukas.json` — NEVER change LUKO. A guard test
> (`tests/TradingBot.FuturesWorker.Tests/FuturesInstanceConfigurationTests.cs`)
> enforces that every arm↔control divergence is in an allow-list; add new keys there.
> CI-enforced: any change under `src/TradingBot.FuturesWorker` OR `src/TradingBot.Core`
> MUST add an entry to `.ai/worker-changelog.md` or the build fails.

## Goal
On BYKO only, add exit regime **D**, behind a single master switch for instant rollback:
- SL = `StopAtrMult` × ATR(14), with a small floor (not the 1.75% one), cap `StopDistanceCapPct`.
- Fixed take-profit **disabled** (no TP order; position exits only via stop / trail / signal).
- Trailing stop **activates** at +`TrailingActivationRMultiple` × R, where R = the stop distance %.
- Trailing **distance** = `TrailingAtrMultiple` × ATR%.
- Current **signal-reversal exit stays active** (`SignalReversalExitEnabled=true`, already set).
- D values on BYKO: StopAtrMult=1.25, TrailingActivationRMultiple=1.0, TrailingAtrMultiple=1.5.
Rollback = set the master switch false → exact current behaviour returns.

Measured 2026-08-30 (45 days, coin-day clustered): D = +0.077%/coin-day, positive in
BOTH halves, best of A/C/signal-exit/D. Still ~breakeven (no entry edge); shorts still negative.

## Config fields to ADD (class `FuturesExitOptions` in FuturesBotConfiguration.cs; the "Exits" section)
- `bool AtrTrailingRegimeEnabled` (default false = current logic; BYKO true). MASTER SWITCH.
- `decimal TrailingActivationRMultiple` (default 0). When regime on and >0, arm the trail at
  profit ≥ this × stopDistancePct. When 0, fall back to activating at the working take-profit.
- `decimal TrailingAtrMultiple` (default 0). When regime on and >0, trail distance = this × ATR%
  (still floored by `TrailingStopMinSpreadMultiple`×spread and rounded to 2dp — reuse
  `EffectiveTrailingStopPercent`). When 0, use `TrailingStopPercent` (fixed %).
`StopAtrMult` already exists (set 1.25 on BYKO). Reuse `MinStopDistancePct` for the small regime
floor (set e.g. 0.3 on BYKO so a 1.25×ATR stop is not floored up to 1.75). Fixed-TP-off is implied
by `AtrTrailingRegimeEnabled` (see sizer task) — do NOT add a separate flag.

## Tasks
- [x] **1. Config fields + normalize/clamp.** Add the three fields to `FuturesExitOptions`
      with XML-doc comments. Clamp in `Normalize()` (TrailingActivationRMultiple 0..10,
      TrailingAtrMultiple 0..10). Add env `SetIfPresent` lines if the file has them for siblings.
      Commit. Hash: (this commit)
- [x] **2. Sizer (`FuturesPositionSizer.cs`, lines ~30-112).** When `AtrTrailingRegimeEnabled`:
      stop uses `StopAtrMult`×ATR floored by `MinStopDistancePct` (not by StopLossPercent), capped
      by `StopDistanceCapPct`; and the TAKE-PROFIT distance must be set so NO fixed TP is placed —
      simplest: set `takeProfitDistancePct` to a sentinel the caller treats as "no TP" (e.g. very
      large, past cap) OR add a `TakeProfitPlaced=false` to the plan record and have the
      order-placement path skip the TP order when regime on. Trace where `TakeProfitDistancePct`
      becomes an exchange TP order (`EnsureExchangeProtectionOrdersAsync` /
      `FuturesVirtualPortfolio` open path) and skip it. Keep SL untouched (still needed).
      Add unit tests in `FuturesPositionSizerTests` (or a new file): stop = 1.25×ATR at various
      ATR, floored at 0.3, capped at cap; TP suppressed when regime on. Commit. Hash: ____
- [x] **3. Trailing arm (`FuturesDecisionWorker.cs`).**
      (a) Activation: the trail is armed from the TAKE_PROFIT handoff (~line 1947, in the fast/slow
      TpSl trigger path) and from `TryActivateExternalTrailingStopAsync`. When regime on, arm when
      unrealized profit ≥ `TrailingActivationRMultiple` × `position.StopDistancePct` instead of at
      the take-profit level. Find where "working take-profit reached" is decided (TpSlOrchestrator
      / the TAKE_PROFIT trigger) and add the R-based trigger.
      (b) Distance: in `ActivateTrailingStopAsync` (~line 2050-2081), `configuredTrailingPercent`
      must become `TrailingAtrMultiple`×ATR% when regime on (ATR from the position/plan —
      `position.AtrPct`), then still passed through `EffectiveTrailingStopPercent(...)`.
      Add unit tests for the distance calc (ATR mult → percent, floored/rounded). Commit. Hash: ____
- [x] **4. Exit-reason logging.** New closure codes so the journal/TG can name the D exits:
      in `ClosureReason` (~1618) the trailing-stop close already returns EXCHANGE_TRAILING_STOP /
      EXCHANGE_MAX_HOLD_RELEASE. Under regime, an armed-at-1R ATR trail that fires should read as a
      profit trail (keep EXCHANGE_TRAILING_STOP) — but LOG the regime + the numbers
      (`futures-trailing-arm` line already logs distancePct; extend it with
      `regime=ATR act=<R> atr=<pct>`). No new close code strictly required; the divergence marker
      (task 6) carries the "not LUKO" signal. Commit. Hash: ____
- [ ] **5. appsettings + guard test.** BYKO appsettings.json Exits: AtrTrailingRegimeEnabled true,
      StopAtrMult 1.25, TrailingActivationRMultiple 1.0, TrailingAtrMultiple 1.5,
      MinStopDistancePct 0.3. Add the four new keys to the guard-test allow-list (`mayDiffer`) and
      any explicit value assertions. LUKO untouched. Commit. Hash: ____
- [ ] **6. LUKO-divergence marker (⚠️) in TG + zurnalas.** Each open/close post is marked when the
      trade's logic diverges from LUKO. Compute one boolean `DivergesFromLuko` at OPEN time and
      persist it on the position (add a bool column like the ExitMode pattern; carry it through the
      reconcile rebuild — see the ExitMode/PeakPnl fix at FuturesDecisionWorker importedPosition).
      Definition (pragmatic): true if ANY holds — entry channel is in
      `Futures.DisabledLongEntryChannels` (LUKO would have taken it), the short passed only because
      of a BYKO-only short gate difference, the entry came from the Reversal book, OR the exit
      regime is D (`AtrTrailingRegimeEnabled`, which LUKO does not run). Since D is always on for
      BYKO, in practice this marks essentially every BYKO trade — CONFIRM with owner whether they
      want ⚠️ only for ENTRY divergences (channel/short-gate/reversal) and treat the D-exit as
      "expected". Default to ENTRY-divergence-only unless told otherwise; note it clearly.
      - TG: `FuturesEntryAnnouncement.Compose`/`ComposeClose` — after the emoji head (💲/💰+face),
        BEFORE the label, insert `⚠️ ` when DivergesFromLuko. Pass the bool into both composers.
      - API: expose `divergesFromLuko` on `PortfolioPositionDto` and `DashboardTradeDto`
        (ordinal-mapped SQL — append the column at the END of the SELECT and read at the new
        highest ordinal; append the field at the END of the record; add to store schema +
        insert if a new column).
      - zurnalas.html: render a ⚠️ badge on each position/trade whose `divergesFromLuko` is true,
        with a one-line tooltip of WHY (channel / short-gate / reversal / D-exit).
      Add tests for the composer marker. Commit. Hash: ____
- [ ] **7. Changelog + final.** `.ai/worker-changelog.md` entry. Run full `dotnet test`. Delete
      THIS file. Commit + push. Watch CI green. Hash: ____

## Status log (append)
- (fill in as you go)

- Task 1 done: AtrTrailingRegimeEnabled + TrailingActivationRMultiple + TrailingAtrMultiple added to FuturesExitOptions, clamped in Normalize. Build green.

- Task 2 done: stop already ATR-based via config (no code change) - verified by new sizer test (1.25xATR floored 0.3, cap flag). Fixed TP suppressed under regime in FuturesVirtualPortfolio (TakeProfitPrice/Distance/ExchangeTakeProfitPrice null when AtrTrailingRegimeEnabled), which also stops the live TP order (placed only when ExchangeTakeProfitPrice>0). SL untouched.

- Task 3+4 done: TryArmRegimeTrailAsync arms the trail at +TrailingActivationRMultiple x StopDistancePct, distance = TrailingAtrMultiple x AtrPct (set on the position, run through EffectiveTrailingStopPercent). Called right after the max-hold arm. Keeps EXCHANGE_TRAILING_STOP close code; the arm reason string logs '+X% (NR) reached, MxATR', and a NOT-armed line is 'futures-regime-trail'. Tests: ProfitPercentInDirection sign, ATR-scaled distance + spread floor. Inert until slice 5.
