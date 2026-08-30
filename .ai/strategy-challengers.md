# Strategy challengers

Measured-but-not-shipped ideas. A challenger earns a place here only after a backtest on the
frozen 45-day window (2026-07-08..08-21), coin-day clustered, same entries, one lever changed.
It is NOT live. Each entry records the evidence, why it is parked, and the trigger to revisit.
Shipping one is a deliberate decision (mid-experiment tuning is avoided by default).

The live control/arm split and the shipped exit are in `worker-changelog.md`; the frozen live
exit is regime D (StopAtrMult 1.25, arm at +1R, trail 1.5x ATR flat, signal-reversal exit on,
no fixed TP). Baselines below are measured against that D.

---

## Findings ledger (tested — do not re-open without NEW data)

- **Slot count (MaxPositions 3 vs 6) — NO robust quality effect.** Event-driven portfolio sim
  (portsim.py, slot-release honoured) 2026-08-30: N=3 net +$73 beats N=6 +$2 on the frozen
  window, BUT the LONG-only sweep is wildly non-monotonic (N=2 +$177, N=3 -$10) = variance /
  path-dependence, not signal quality. Per-cycle entry-rank expectancy is flat (rank #4-6 longs
  >= #1-3; rankexp.py). N=3>N=6 is explained by fewer fees + less correlated drawdown, not by
  slots selecting better signals. Not a lever.
- **BTC-regime entry gating — FAILED robustness / OOS.** Point-in-time BTC return buckets
  (btcbuckets.py) 2026-08-30: LONG BTC-24h>2% looked strong (+$1.52/200, win 54%) and holds 77%
  of the top-5-day money, BUT excluding just 19-21 Aug it flips to -$0.545 (win 36%); BTC-4h>2%
  excl. pump = -$0.694. The entire "BTC-up longs pay" effect is 3 pump days out of 16 in the
  bucket. The good LUKO days (+$20-25) are a market EPISODE, not a filterable regime. Do NOT
  revisit without new out-of-window data.
- **Entry score / rank — no strong predictive edge.** Within longs, score is nearly flat
  (0.80-score longs = +$0.122/200, ~ same as 0.95+); the blended "high score = good" is an
  artifact of the LONG/SHORT mix (0.90+ buckets are 100% long). Rank does not stratify longs.
- **trailing / ratchet (R2) — works mechanistically, economically negligible.** See below.
- **Root cause across all of the above:** gross edge at entry is ~zero (held-out scorer verdict).
  Everything downstream (exits, slots, regime buckets) only reshuffles a near-zero-edge,
  cost-heavy stream. The one term that bleeds every trade — execution cost — is the next target.

---

## R2 — ratchet trailing exit  (CONFIRMED challenger, parked)

**What it is.** The regime-D trail, but the trail distance TIGHTENS as the trade's peak crosses
R-thresholds (R = the ATR stop distance), instead of staying flat at 1.5x ATR:

    arm at +1R -> trail 1.5x ATR
    peak >= +2R -> trail 1.0x ATR
    peak >= +3R -> trail 0.75x ATR

Everything else identical to D (stop 1.25x ATR, signal-exit on, no fixed TP, 24h cap). Ratchet
only — never loosens.

**Measured 2026-08-30** (ratchet.py, 77,286 entries, 6,888 coin-days, coin-day clustered):

| variant | /coin-day | Δ vs D | 1st half | 2nd half | LONG cd | SHORT cd | giveback mean |
|---|---|---|---|---|---|---|---|
| baseline D (1.5x flat) | +0.077% | — | +0.007 | +0.144 | +0.045 | -0.049 | 0.964 |
| R1 (+2R->1.0)          | +0.083% | +0.005 | +0.021 | +0.139 | +0.048 | -0.035 | 0.890 |
| R2 (+2R->1.0,+3R->0.75)| +0.086% | +0.009 | +0.024 | +0.139 | +0.052 | -0.035 | 0.873 |
| R3 (+2R->0.75)         | +0.088% | +0.010 | +0.026 | +0.139 | +0.053 | -0.035 | 0.854 |

Runner cohort (price reached +2R, n=54,510): D +0.374% -> R2 +0.395% (+0.020), giveback
0.969 -> 0.840. On the +3R cohort R2 (+0.469) edges R3 (+0.465) — aggressive tightening at +2R
slightly hurts the very biggest runners, which is why R2 is the pick over R3 for that tail.

**Why it is credible, not noise.** Win-rate and median realized are unchanged (36% / -0.33);
only the mean / right tail moves. The effect is pure profit-preservation on runners
(giveback -16% on the +2R cohort), positive in both halves, monotone in the tightening. It does
NOT create an entry edge and does NOT fix shorts (still negative).

**Why R2 over R3.** R3 is marginally best on average (+0.010 vs +0.009) but R2 protects the
biggest runners (+3R cohort) better and tightens less aggressively — a safer default.

**Economic size today.** Metric is % of NOTIONAL per trade, net of a 0.10% fee (leverage does
not enter the dollar P&L). On the current $200 notional: Δ ≈ +$0.018 per trade (~1.8 cents),
~+$0.18/day at ~10 trades/day, ~+$5/30 days. Same near-zero order as everything else; the
absolute D/R2 dollars are within the cost model's error of zero (breakeven).

**Why parked (blockers).**
1. Magnitude is cents at $200 notional — not worth breaking mid-experiment discipline.
2. Implementation cost: the live trail is a REAL Kraken `trailing_stop` order with a FIXED
   distance. A ratchet means cancel-and-replace the exchange order at each R-threshold — extra
   API calls, and a window where the position is momentarily unprotected between cancel and
   re-arm (race). Not a config flip.

**Revisit when** either is true:
- notional grows enough that +0.009%/trade is real money (e.g. at $1,000 notional it is ~9c/trade,
  and scales linearly), OR
- a safe atomic amend/replace path for the Kraken trailing order exists (modify-in-place, or a
  reduce-only re-arm that never leaves the position naked), removing blocker 2.

Then ship R2 (not R3) behind its own arm-only switch, measure the live arm delta over weeks.

Reproduce: `ratchet.py` in the session scratchpad (base D vs R1/R2/R3). Provenance of the D
values themselves: they were owner-specified (regime C spec), validated only as the bundle D,
never swept in isolation — see the trail-multiple sweep (`trailsweep.py`): wider is worse,
1.0x ATR marginally best, activation point is not a lever.

---

## Maker entry — QUEUED challenger (not yet tested)

**Rationale (why this and not more signal work).** Gross edge at entry is ~zero, so the P&L is
dominated by execution cost, not direction. Every entry today is a taker FOK (Fees.TakerPct 0.05,
`orderType: fok`) that also crosses the spread; round-trip cost ~0.10% fee + spread ~= $0.20-0.30
per $200 trade. At 10-30 trades/day that is several $/day bled continuously — the dollar scale we
have been chasing, and unlike the regime/slot ideas it is a CONSTANT loss, not a rare episode.

**The change.** Rest entries as maker / post-only (MakerPct 0.02 vs TakerPct 0.05) instead of
taker FOK: pay ~0.03% less fee AND stop paying the spread (earn it). Config scaffolding exists
(Entry.MakerFillTimeoutSec, Entry.MakerRepegs; the sizer already models MakerFillRate/QueueAhead/
TimeToFill). Potential saving ~0.06-0.11%/trade = +$0.12-0.30 on $200 — to the top of the target.

**Why it is only a candidate, not a win yet — must be modelled honestly first.** Maker orders do
not always fill. The real risk is ADVERSE SELECTION: a resting bid fills preferentially on names
that reverse into you and misses the ones that run away. On a momentum book that could be fatal;
but our own research says the tape mean-reverts (no continuation), so resting may even improve the
entry — this must be MEASURED, not assumed.

**Test spec (before any prod change).** On the frozen 45-day window: model, per candidate, whether
a post-only limit at/inside the bid would have filled within MakerFillTimeoutSec given the m1 path
(fill = price traded back through the resting level), with MakerRepegs re-pegs. Then compare, on
the FILLED subset only: (a) realized entry price vs the taker FOK entry, (b) fill-rate, (c) the
selection bias (do filled-vs-missed differ in forward return — adverse selection), (d) net P&L
after the lower maker fee. Report fill-rate, $/trade saved, and whether the missed entries were
net positive or negative (i.e., is skipping them free). Only if the fee saving survives the
adverse-selection check does it graduate from candidate to shippable.

**Do NOT ship on the fee-saving argument alone** — the adverse-selection term can erase it.
