# Strategy challengers

Measured-but-not-shipped ideas. A challenger earns a place here only after a backtest on the
frozen 45-day window (2026-07-08..08-21), coin-day clustered, same entries, one lever changed.
It is NOT live. Each entry records the evidence, why it is parked, and the trigger to revisit.
Shipping one is a deliberate decision (mid-experiment tuning is avoided by default).

The live control/arm split and the shipped exit are in `worker-changelog.md`; the frozen live
exit is regime D (StopAtrMult 1.25, arm at +1R, trail 1.5x ATR flat, signal-reversal exit on,
no fixed TP). Baselines below are measured against that D.

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
