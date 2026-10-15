# PUKO Read-Only Preflight and Fees

This change does not place, cancel, or validate orders on Kraken. PUKO remains
paper trading (`LiveTradingEnabled=false`). No additional API keys are needed.
BYKO and LUKO credentials must not be copied for diagnostics.

## Contract Checks

Only paper `futures-pukis-live` receives the diagnostic service. Public GET
`/derivatives/api/v3/instruments` is cached for one hour, audited at startup and
hourly in an independent maintenance loop. Failures retry after one
minute. Each candidate virtual entry checks fresh metadata, executable symbol,
positive rounded quantity, and tick-rounded indicative FOK limit; the journal
records the rounded notional and delta. It uses the signal price, not a fresh
executable bid/ask. It does not establish liquidity, account margin or exchange
acceptance, and does not change the paper fill. Missing entry instruments alert
instead of returning silently. Warnings are throttled independently for 30
minutes per pair and reason; Telegram retains the instance emoji prefix.

## Fee Assumptions

`PukoReadOnly.FeeScheduleUid` selects a public schedule; `FeeVolumeUsd` is the
explicit assumed rolling USD volume (default 0). The PUKO settings file selects
`97ae0cc8-2964-43ea-91e9-d8e720b02b19`, Linear Multi-Collateral Rebate Fees.
The owner must confirm this schedule matches the intended simulated account;
this is not an assertion about any real account's fee tier. No account volume
or BYKO fee tier is read.

Public GET `/derivatives/api/v3/feeschedules` supplies percentage rates, not
fractions. The greatest tier threshold at or below the configured volume wins.
The cache expires after one hour. A stale/missing/invalid response falls back
to configured `Fees.TakerPct`, raises a warning and records `configured_fallback`.
The current entry path is an immediate taker FOK; simulated closes are immediate
taker executions too. A limit order is not automatically maker. Maker is only
appropriate with evidence of resting and later filling; this change does not
introduce that execution model. Actual fill fees and source are retained in
existing journal actions, visible in technical records, not in a separate account
fee dashboard. Changing fees affects simulated cash/PnL and can affect margin
capacity, but does not change strategy thresholds or entry/exit formulas.

## Rollout

No deployment is authorized by this PR. After review, confirm the schedule UID
and assumed volume, then explicitly approve deployment of PUKO. Existing paper
positions use the current fee assumption when closed; historic fills are not
rewritten. BYKO/LUKO do not receive this fee provider. There is no live-probe
flag or probe implementation in this PR. PR 2 requires separate approval and an
isolated subaccount; without one, it must not be implemented.
