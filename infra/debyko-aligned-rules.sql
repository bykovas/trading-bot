-- Apply only after owner-auth probes and the aligned worker deployment pass.
-- No credentials, position sizes or entry authority are changed here.
-- Required psql variables are the semantic versions returned AFTER live activation.
-- Never use the old settled-rule versions; missing variables deliberately fail.
begin;
update public.bot_instance_strategy_profiles
set overrides_jsonb = jsonb_set(coalesce(overrides_jsonb, '{}'::jsonb), '{BykoV2}',
    coalesce(overrides_jsonb->'BykoV2', '{}'::jsonb) || jsonb_build_object(
        'DebykoAlignedDataEnabled', true,
        'EvaluationDelaySeconds', 15,
        'FlushCooldownMinutes', 0,
        'BreakoutCooldownMinutes', 0,
        'DebykoRuleVersions', jsonb_build_object(
            'BYKO_BREAKOUT_LONG', :'breakout_long_version',
            'BYKO_BREAKOUT_SHORT', :'breakout_short_version',
            'BYKO_FLUSH_LONG', :'flush_long_version',
            'BYKO_FLUSH_SHORT', :'flush_short_version'),
        'DebykoRuleIds', jsonb_build_object(
            'BYKO_BREAKOUT_LONG', 'bd6a4033-ce4f-4acb-97b7-a384c5368f2e',
            'BYKO_BREAKOUT_SHORT', '2bb871a8-6c38-4c32-aa88-2fd5b7873527',
            'BYKO_FLUSH_LONG', '3214ba4c-613a-4b0a-aeb4-715ec84cbb88',
            'BYKO_FLUSH_SHORT', '6393df1b-4f82-4970-a89c-55cc38019edb')),
    true), updated_at = now(), updated_by = 'debyko-aligned-cutover'
where bot_instance_id in ('futures-live', 'futures-pukis-live') and is_active;
commit;
