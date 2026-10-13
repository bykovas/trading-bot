CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_actions_trades_cycle
ON dry_run_actions (cycle_id, decision_index)
INCLUDE (pair, action, actual_initial_margin_eur, requested_margin_eur)
WHERE action IN ('WOULD_BUY', 'WOULD_OPEN_LONG', 'WOULD_OPEN_SHORT', 'WOULD_SELL', 'WOULD_CLOSE');

CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_cycle_facts_flat_bot_utc
ON dry_run_cycle_facts (bot_instance_id, utc, cycle_id)
WHERE positions_value_after_eur = 0 AND NOT valuation_unsettled AND cycle_id NOT LIKE '%-backfill';
