# DEBYKO Aligned Entry Cutover

## Implemented

- Independent BYKO-v2 evaluator using DQL double arithmetic, indicator warmup and Kleene conditions.
- Rule-specific candle/history reads at Agent `data_as_of`; explicit result API, not webhook absence, determines the remote verdict.
- Background evaluation, zero-margin cancellation, stale-entry checks and credential backoff.
- Rule ID/version checks and explicit webhook `bar_end` linkage.
- Original responses and receipts in `byko_v2_aligned_checks`; automatic four-hour retention. Older raw audit inputs are unavailable, even if Agent still retains a result.
- Shared protected DEBYKO env file for BYKO/PUKO only. Existing trading authority and protection remain unchanged.

## Activation Gate

Do not enable the database flag before all gates pass. An Agent connector session is not a runtime user API key.

1. Finish tests and deploy the bot with `DebykoAlignedDataEnabled=false`.
2. Verify DEBYKO #303 production supports liquidation `as_of`.
3. Obtain the rule owner's user API key through an owner-created protected file. Transfer without printing it to `/opt/trading-bot/futures/debyko.env`, mode 600, as `TRADINGBOT_DEBYKO_API_KEY=...`. Never commit credentials or overwrite this file during deploy.
4. Run owner-auth HTTP probes: account, four result endpoints, exact-slice candles, book/funding/stats history and liquidations. Check bounds, units, receipts and coverage; missing layers are not zero.
5. Change all four Agent rules to live through the owner API/MCP, preserving DQL, channels, universe and rearming configuration. Read back each new semantic version. No fabricated firing or historical trade replay.
6. Run `infra/debyko-aligned-rules.sql` with psql variables `breakout_long_version`, `breakout_short_version`, `flush_long_version`, `flush_short_version`. These must be the NEW live versions. Confirm exactly the two intended active instance profiles changed.
7. Restart BYKO/PUKO to load the runtime key. Leave LUKO untouched. Verify results for the next new bar, persisted inputs, comparison notices and signed inbox processing.

## Rule Bindings

| Local Name | Agent Rule ID |
| --- | --- |
| BYKO_BREAKOUT_LONG | bd6a4033-ce4f-4acb-97b7-a384c5368f2e |
| BYKO_BREAKOUT_SHORT | 2bb871a8-6c38-4c32-aa88-2fd5b7873527 |
| BYKO_FLUSH_LONG | 3214ba4c-613a-4b0a-aeb4-715ec84cbb88 |
| BYKO_FLUSH_SHORT | 6393df1b-4f82-4970-a89c-55cc38019edb |

## Honest Limits

- A true Agent verdict need not produce a webhook: arming, cooldown and delivery policy are separate.
- Matching `data_as_of` alone does not freeze writer materialization or listing metadata. Diagnose disagreements from retained original inputs, not an assumed timing explanation.
- Non-base liquidation quantities are unsupported until valuation metadata is implemented. They stay unknown.
- Market evaluation uses DEBYKO only when the aligned flag is enabled. Execution quotes, balances and protective exits keep their existing executor paths.
- Missing or mismatched versions fail closed. A rule edit requires rebinding its new semantic version.

## Rollback

Set `BykoV2.DebykoAlignedDataEnabled=false` in the two active instance profile overrides. Preserve position state, stops, keys and execution authority. Do not replay old ignored inbox events. Agent rule rollback is a separate owner-side operation.
