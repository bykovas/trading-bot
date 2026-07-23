-- Backfill normalized forensic tables from the legacy JSON archive.
--
-- Run manually after deploying the dual-write schema. The script is intentionally
-- not executed by worker startup: old JSON can contain partial legacy shapes, and a
-- one-off migration is safer to validate on a database copy first.

begin;

create or replace function pg_temp.try_numeric(value text)
returns numeric
language plpgsql
as $$
begin
    if value is null or btrim(value) = '' then
        return null;
    end if;

    return value::numeric;
exception when others then
    return null;
end;
$$;

create or replace function pg_temp.try_int(value text)
returns integer
language plpgsql
as $$
begin
    if value is null or btrim(value) = '' then
        return null;
    end if;

    return value::integer;
exception when others then
    return null;
end;
$$;

create or replace function pg_temp.try_bool(value text)
returns boolean
language plpgsql
as $$
begin
    if value is null or btrim(value) = '' then
        return null;
    end if;

    return value::boolean;
exception when others then
    return null;
end;
$$;

create or replace function pg_temp.try_timestamptz(value text)
returns timestamptz
language plpgsql
as $$
begin
    if value is null or btrim(value) = '' then
        return null;
    end if;

    return value::timestamptz;
exception when others then
    return null;
end;
$$;

insert into portfolio_state_summary (
    bot_instance_id,
    state_id,
    updated_at,
    cash_eur,
    positions_value_eur,
    total_value_eur,
    open_positions,
    daily_risk_date_utc,
    daily_realized_pnl_eur,
    external_pnl_eur)
select
    bot_instance_id,
    id,
    updated_at,
    coalesce(pg_temp.try_numeric(state_json ->> 'cashEur'), 0),
    coalesce(pg_temp.try_numeric(state_json ->> 'positionsValueEur'), 0),
    coalesce(pg_temp.try_numeric(state_json ->> 'totalValueEur'), pg_temp.try_numeric(state_json ->> 'cashEur'), 0),
    jsonb_array_length(coalesce(state_json -> 'positions', '[]'::jsonb)),
    state_json -> 'dailyRisk' ->> 'dateUtc',
    pg_temp.try_numeric(state_json -> 'dailyRisk' ->> 'realizedPnlEur'),
    coalesce(pg_temp.try_numeric(state_json ->> 'externalPnlEur'), 0)
from portfolio_state
on conflict (bot_instance_id) do update set
    state_id = excluded.state_id,
    updated_at = excluded.updated_at,
    cash_eur = excluded.cash_eur,
    positions_value_eur = excluded.positions_value_eur,
    total_value_eur = excluded.total_value_eur,
    open_positions = excluded.open_positions,
    daily_risk_date_utc = excluded.daily_risk_date_utc,
    daily_realized_pnl_eur = excluded.daily_realized_pnl_eur,
    external_pnl_eur = excluded.external_pnl_eur;

delete from portfolio_position_state
where bot_instance_id in (select bot_instance_id from portfolio_state);

insert into portfolio_position_state (
    bot_instance_id,
    updated_at,
    position_index,
    pair,
    side,
    quantity,
    entry_price,
    entry_notional_eur,
    last_price,
    market_value_eur,
    unrealized_pnl_eur,
    unrealized_pnl_percent,
    opened_at_utc,
    last_action_at_utc,
    peak_pnl_percent,
    entry_score,
    exit_mode,
    stop_loss_price,
    take_profit_price,
    stop_distance_pct,
    take_profit_distance_pct,
    exchange_stop_loss_price,
    exchange_take_profit_price,
    exchange_protection_multiplier_percent,
    trailing_stop_state,
    trailing_stop_percent,
    trailing_stop_order_id,
    trailing_activated_at_utc,
    low_score_cycles,
    leverage,
    initial_margin_eur,
    mark_price,
    liquidation_price,
    liquidation_distance_percent,
    funding_paid_eur,
    tp_order_state,
    sl_order_state,
    origin,
    entry_channel)
select
    state.bot_instance_id,
    state.updated_at,
    position.ordinality::integer - 1,
    position.value ->> 'pair',
    coalesce(position.value ->> 'side', 'LONG'),
    coalesce(pg_temp.try_numeric(position.value ->> 'quantity'), 0),
    coalesce(pg_temp.try_numeric(position.value ->> 'entryPrice'), 0),
    coalesce(pg_temp.try_numeric(position.value ->> 'entryNotionalEur'), 0),
    coalesce(pg_temp.try_numeric(position.value ->> 'lastPrice'), 0),
    coalesce(pg_temp.try_numeric(position.value ->> 'marketValueEur'), 0),
    coalesce(pg_temp.try_numeric(position.value ->> 'unrealizedPnlEur'), 0),
    coalesce(pg_temp.try_numeric(position.value ->> 'unrealizedPnlPercent'), 0),
    pg_temp.try_timestamptz(position.value ->> 'openedAtUtc'),
    pg_temp.try_timestamptz(position.value ->> 'lastActionAtUtc'),
    pg_temp.try_numeric(position.value ->> 'peakPnlPercent'),
    pg_temp.try_numeric(position.value ->> 'entryScore'),
    position.value ->> 'exitMode',
    pg_temp.try_numeric(position.value ->> 'stopLossPrice'),
    pg_temp.try_numeric(position.value ->> 'takeProfitPrice'),
    pg_temp.try_numeric(position.value ->> 'stopDistancePct'),
    pg_temp.try_numeric(position.value ->> 'takeProfitDistancePct'),
    pg_temp.try_numeric(position.value ->> 'exchangeStopLossPrice'),
    pg_temp.try_numeric(position.value ->> 'exchangeTakeProfitPrice'),
    pg_temp.try_numeric(position.value ->> 'exchangeProtectionMultiplierPercent'),
    position.value ->> 'trailingStopState',
    pg_temp.try_numeric(position.value ->> 'trailingStopPercent'),
    position.value ->> 'trailingStopOrderId',
    pg_temp.try_timestamptz(position.value ->> 'trailingActivatedAtUtc'),
    coalesce(pg_temp.try_int(position.value ->> 'lowScoreCycles'), 0),
    pg_temp.try_numeric(position.value ->> 'leverage'),
    pg_temp.try_numeric(position.value ->> 'initialMarginEur'),
    pg_temp.try_numeric(position.value ->> 'markPrice'),
    pg_temp.try_numeric(position.value ->> 'liquidationPrice'),
    pg_temp.try_numeric(position.value ->> 'liquidationDistancePercent'),
    pg_temp.try_numeric(position.value ->> 'fundingPaidEur'),
    position.value ->> 'tpOrderState',
    position.value ->> 'slOrderState',
    position.value ->> 'origin',
    position.value ->> 'entryChannel'
from portfolio_state state
cross join lateral jsonb_array_elements(coalesce(state.state_json -> 'positions', '[]'::jsonb)) with ordinality as position(value, ordinality)
where position.value ->> 'pair' is not null;

insert into dry_run_cycle_facts (
    cycle_id,
    bot_instance_id,
    bot_instance_name,
    utc,
    market_data_mode,
    ai_provider,
    worker_version,
    worker_commit,
    worker_build_utc,
    worker_image_tag,
    strategy_version,
    change_set,
    active_pairs_count,
    decisions_count,
    cash_before_eur,
    cash_after_eur,
    positions_value_before_eur,
    positions_value_after_eur,
    portfolio_value_before_eur,
    portfolio_value_after_eur,
    would_buy_count,
    would_sell_count,
    validated_order_count)
select
    cycle_id,
    bot_instance_id,
    coalesce(record_json ->> 'botInstanceName', bot_instance_id),
    utc,
    coalesce(record_json ->> 'marketDataMode', ''),
    coalesce(record_json ->> 'aiProvider', ''),
    worker_version,
    worker_commit,
    worker_build_utc,
    worker_image_tag,
    strategy_version,
    change_set,
    jsonb_array_length(coalesce(record_json -> 'activePairs', '[]'::jsonb)),
    jsonb_array_length(coalesce(record_json -> 'decisions', '[]'::jsonb)),
    coalesce(pg_temp.try_numeric(record_json -> 'portfolioBefore' ->> 'cashEur'), 0),
    coalesce(pg_temp.try_numeric(record_json -> 'portfolioAfter' ->> 'cashEur'), 0),
    coalesce(pg_temp.try_numeric(record_json -> 'portfolioBefore' ->> 'positionsValueEur'), 0),
    coalesce(pg_temp.try_numeric(record_json -> 'portfolioAfter' ->> 'positionsValueEur'), 0),
    coalesce(pg_temp.try_numeric(record_json -> 'portfolioBefore' ->> 'totalValueEur'), 0),
    coalesce(pg_temp.try_numeric(record_json -> 'portfolioAfter' ->> 'totalValueEur'), 0),
    (
        select count(*)::integer
        from jsonb_array_elements(coalesce(cycle.record_json -> 'decisions', '[]'::jsonb)) as decision
        where decision -> 'dryRunAction' ->> 'action' in ('WOULD_BUY', 'WOULD_OPEN', 'OPEN_LONG', 'OPEN_SHORT')
    ),
    (
        select count(*)::integer
        from jsonb_array_elements(coalesce(cycle.record_json -> 'decisions', '[]'::jsonb)) as decision
        where decision -> 'dryRunAction' ->> 'action' in ('WOULD_SELL', 'WOULD_CLOSE', 'CLOSE')
    ),
    (
        select count(*)::integer
        from jsonb_array_elements(coalesce(cycle.record_json -> 'decisions', '[]'::jsonb)) as decision
        where coalesce(decision ->> 'broker', '') like 'VALIDATED_OK%'
    )
from dry_run_cycles cycle
on conflict (cycle_id) do nothing;

insert into dry_run_cycle_active_pairs (cycle_id, pair_index, pair)
select
    cycle.cycle_id,
    pair.ordinality::integer - 1,
    pair.value
from dry_run_cycles cycle
join dry_run_cycle_facts facts on facts.cycle_id = cycle.cycle_id
cross join lateral jsonb_array_elements_text(coalesce(cycle.record_json -> 'activePairs', '[]'::jsonb)) with ordinality as pair(value, ordinality)
on conflict do nothing;

insert into dry_run_decision_facts (
    cycle_id,
    decision_index,
    bot_instance_id,
    utc,
    pair,
    price,
    fast_ema,
    slow_ema,
    rsi,
    desired_position,
    score,
    risk_approved,
    broker,
    entry_rejection_reason,
    spread_percent,
    price_action_direction,
    price_action_trend_percent,
    exploratory,
    has_bullish_structure,
    ema_fully_confirmed,
    bullish_ema_gap_percent,
    ema_gap_velocity_percent,
    allows_short,
    has_bearish_structure,
    bearish_ema_gap_percent,
    short_score,
    long_score_threshold,
    short_score_threshold,
    minimum_ema_gap_percent,
    short_base_block_reason_code,
    short_base_block_reason,
    early_entry_eligible,
    early_entry_reason,
    early_entry_diagnostic_score,
    early_entry_suggested_notional_eur)
select
    cycle.cycle_id,
    decision.ordinality::integer - 1,
    cycle.bot_instance_id,
    cycle.utc,
    decision.value ->> 'pair',
    coalesce(pg_temp.try_numeric(decision.value ->> 'price'), 0),
    pg_temp.try_numeric(decision.value ->> 'fastEma'),
    pg_temp.try_numeric(decision.value ->> 'slowEma'),
    pg_temp.try_numeric(decision.value ->> 'rsi'),
    coalesce(decision.value ->> 'desiredPosition', ''),
    coalesce(pg_temp.try_numeric(decision.value ->> 'score'), 0),
    coalesce(pg_temp.try_bool(decision.value ->> 'riskApproved'), false),
    decision.value ->> 'broker',
    decision.value ->> 'entryRejectionReason',
    coalesce(pg_temp.try_numeric(decision.value ->> 'spreadPercent'), 0),
    decision.value ->> 'priceActionDirection',
    pg_temp.try_numeric(decision.value ->> 'priceActionTrendPercent'),
    coalesce(pg_temp.try_bool(decision.value ->> 'exploratory'), false),
    coalesce(pg_temp.try_bool(decision.value ->> 'hasBullishStructure'), false),
    coalesce(pg_temp.try_bool(decision.value ->> 'emaFullyConfirmed'), false),
    pg_temp.try_numeric(decision.value ->> 'bullishEmaGapPercent'),
    pg_temp.try_numeric(decision.value ->> 'emaGapVelocityPercent'),
    coalesce(pg_temp.try_bool(decision.value ->> 'allowsShort'), false),
    coalesce(pg_temp.try_bool(decision.value ->> 'hasBearishStructure'), false),
    pg_temp.try_numeric(decision.value ->> 'bearishEmaGapPercent'),
    pg_temp.try_numeric(decision.value ->> 'shortScore'),
    pg_temp.try_numeric(decision.value ->> 'longScoreThreshold'),
    pg_temp.try_numeric(decision.value ->> 'shortScoreThreshold'),
    pg_temp.try_numeric(decision.value ->> 'minimumEmaGapPercent'),
    decision.value ->> 'shortBaseBlockReasonCode',
    decision.value ->> 'shortBaseBlockReason',
    coalesce(pg_temp.try_bool(decision.value ->> 'earlyEntryEligible'), false),
    decision.value ->> 'earlyEntryReason',
    coalesce(pg_temp.try_numeric(decision.value ->> 'earlyEntryDiagnosticScore'), 0),
    coalesce(pg_temp.try_numeric(decision.value ->> 'earlyEntrySuggestedNotionalEur'), 0)
from dry_run_cycles cycle
join dry_run_cycle_facts facts on facts.cycle_id = cycle.cycle_id
cross join lateral jsonb_array_elements(coalesce(cycle.record_json -> 'decisions', '[]'::jsonb)) with ordinality as decision(value, ordinality)
where decision.value ->> 'pair' is not null
on conflict do nothing;

insert into dry_run_actions (
    cycle_id,
    decision_index,
    pair,
    action,
    reason,
    hold_reason_code,
    exit_reason_code,
    desired_position,
    target_notional_eur,
    quantity,
    entry_price,
    last_price,
    fill_price,
    fee_eur,
    gross_notional_eur,
    net_notional_eur,
    cash_before_eur,
    cash_after_eur,
    portfolio_value_before_eur,
    portfolio_value_after_eur,
    fill_source,
    side,
    reduce_only,
    leverage,
    exit_trigger_source,
    entry_channel,
    exchange_order_id,
    exchange_fill_timestamp)
select
    decision.cycle_id,
    decision.decision_index,
    coalesce(action.value ->> 'pair', decision.pair),
    coalesce(action.value ->> 'action', ''),
    coalesce(action.value ->> 'reason', ''),
    action.value ->> 'holdReasonCode',
    action.value ->> 'exitReasonCode',
    coalesce(action.value ->> 'desiredPosition', ''),
    coalesce(pg_temp.try_numeric(action.value ->> 'targetNotionalEur'), 0),
    coalesce(pg_temp.try_numeric(action.value ->> 'quantity'), 0),
    coalesce(pg_temp.try_numeric(action.value ->> 'entryPrice'), 0),
    coalesce(pg_temp.try_numeric(action.value ->> 'lastPrice'), 0),
    coalesce(pg_temp.try_numeric(action.value ->> 'fillPrice'), 0),
    coalesce(pg_temp.try_numeric(action.value ->> 'feeEur'), 0),
    coalesce(pg_temp.try_numeric(action.value ->> 'grossNotionalEur'), 0),
    coalesce(pg_temp.try_numeric(action.value ->> 'netNotionalEur'), 0),
    coalesce(pg_temp.try_numeric(action.value ->> 'cashBeforeEur'), 0),
    coalesce(pg_temp.try_numeric(action.value ->> 'cashAfterEur'), 0),
    coalesce(pg_temp.try_numeric(action.value ->> 'portfolioValueBeforeEur'), 0),
    coalesce(pg_temp.try_numeric(action.value ->> 'portfolioValueAfterEur'), 0),
    action.value ->> 'fillSource',
    action.value ->> 'side',
    pg_temp.try_bool(action.value ->> 'reduceOnly'),
    pg_temp.try_numeric(action.value ->> 'leverage'),
    action.value ->> 'exitTriggerSource',
    action.value ->> 'entryChannel',
    action.value ->> 'exchangeOrderId',
    pg_temp.try_timestamptz(action.value ->> 'exchangeFillTimestamp')
from dry_run_decision_facts decision
join dry_run_cycles cycle on cycle.cycle_id = decision.cycle_id
cross join lateral (select coalesce(
    (
        select raw_decision.value -> 'dryRunAction'
        from jsonb_array_elements(coalesce(cycle.record_json -> 'decisions', '[]'::jsonb)) with ordinality as raw_decision(value, ordinality)
        where raw_decision.ordinality::integer - 1 = decision.decision_index
    ),
    '{}'::jsonb
)) as action(value)
on conflict do nothing;

commit;
