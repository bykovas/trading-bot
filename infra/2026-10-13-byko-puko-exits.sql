BEGIN;

WITH source AS (
    SELECT r.values_jsonb, p.overrides_jsonb
    FROM bot_instance_strategy_profiles p
    JOIN bot_strategy_profile_revisions r
      ON r.profile_id = p.profile_id AND r.revision = p.profile_revision
    WHERE p.bot_instance_id = 'futures-pukis-live'
), blocks AS (
    SELECT jsonb_object_agg(key, coalesce(values_jsonb->key, '{}'::jsonb)
        || coalesce(overrides_jsonb->key, '{}'::jsonb)) AS settings
    FROM source CROSS JOIN (VALUES ('Exits'), ('TpSl'), ('Strategy'), ('Shorts'), ('ExecutionPolicy'), ('Regime')) AS keys(key)
)
UPDATE bot_instance_strategy_profiles p
SET overrides_jsonb = p.overrides_jsonb || blocks.settings
    || jsonb_build_object('Exits', blocks.settings->'Exits'
        || jsonb_build_object(
            'MinStopDistancePct', coalesce(blocks.settings->'Exits'->'MinStopDistancePct', blocks.settings->'TpSl'->'StopLossPercent'),
            'MinTakeProfitPct', coalesce(blocks.settings->'Exits'->'MinTakeProfitPct', blocks.settings->'TpSl'->'TakeProfitPercent'),
            'SignalReversalExitEnabled', coalesce(blocks.settings->'Exits'->'SignalReversalExitEnabled', 'true'::jsonb)))
    || jsonb_build_object('TpSl', blocks.settings->'TpSl'
        || jsonb_build_object('TrailingStopMinSpreadMultiple',
            coalesce(blocks.settings->'TpSl'->'TrailingStopMinSpreadMultiple', '0'::jsonb)))
    || jsonb_build_object('Shorts', blocks.settings->'Shorts'
        || jsonb_build_object('RequireBtc4hDropPercent',
            coalesce(blocks.settings->'Shorts'->'RequireBtc4hDropPercent', '0'::jsonb),
            'MaxBtc24hRisePercentForShort',
            coalesce(blocks.settings->'Shorts'->'MaxBtc24hRisePercentForShort', 'null'::jsonb))),
    updated_at = now(), updated_by = 'byko-puko-exit-parity-2026-10-13'
FROM blocks
WHERE p.bot_instance_id = 'futures-live';

COMMIT;
