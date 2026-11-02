-- Explicitly reviewed DEBYKO versions; do not derive trusted pins from incoming events.
-- Only instance overrides change. No restart, historical event replay or trading-mode change.
\set ON_ERROR_STOP on
begin;
do $$
declare
    item record;
    effective jsonb;
    n integer := 0;
begin
    for item in
        select a.*, r.values_jsonb
        from public.bot_instance_strategy_profiles a
        join public.bot_strategy_profile_revisions r
          on r.profile_id = a.profile_id and r.revision = a.profile_revision
        where a.bot_instance_id in ('futures-live', 'futures-pukis-live') and a.is_active
        for update of a
    loop
        effective := coalesce(item.values_jsonb->'BykoV2', '{}'::jsonb)
                     || coalesce(item.overrides_jsonb->'BykoV2', '{}'::jsonb);
        if effective#>>'{DebykoRuleIds,BYKO_BREAKOUT_LONG}' <> 'bd6a4033-ce4f-4acb-97b7-a384c5368f2e'
           or effective#>>'{DebykoRuleIds,BYKO_BREAKOUT_SHORT}' <> '2bb871a8-6c38-4c32-aa88-2fd5b7873527'
           or effective#>>'{DebykoRuleIds,BYKO_BREAKOUT_LONG}' is null
           or effective#>>'{DebykoRuleIds,BYKO_BREAKOUT_SHORT}' is null then
            raise exception 'Unexpected rule IDs for %', item.bot_instance_id;
        end if;
        if coalesce(effective#>>'{DebykoRuleVersions,BYKO_BREAKOUT_LONG}', '') not in
             ('f1ccef58f4421530ce42bf78eb48ca674920b06a95ab4e757a8a375cb728f4da',
              '3eee341e9979ee76240a89d6e1a030bef39b328513d671454d4b9099badb6ad0')
           or coalesce(effective#>>'{DebykoRuleVersions,BYKO_BREAKOUT_SHORT}', '') not in
             ('3ffe4d4b054f909c622cf5a3d57f443c856f962442252454fa2ce57e95683238',
              'ef82714c71096f626749578ea5bf90962183c4f0969286e7088521eb9c9ec473') then
            raise exception 'Unexpected version pins for %', item.bot_instance_id;
        end if;
        update public.bot_instance_strategy_profiles
        set overrides_jsonb = jsonb_set(coalesce(item.overrides_jsonb, '{}'::jsonb), '{BykoV2}',
            coalesce(item.overrides_jsonb->'BykoV2', '{}'::jsonb) || jsonb_build_object(
                'DebykoRuleVersions', (effective->'DebykoRuleVersions') || jsonb_build_object(
                    'BYKO_BREAKOUT_LONG', '3eee341e9979ee76240a89d6e1a030bef39b328513d671454d4b9099badb6ad0',
                    'BYKO_BREAKOUT_SHORT', 'ef82714c71096f626749578ea5bf90962183c4f0969286e7088521eb9c9ec473'))),
            updated_at = now(), updated_by = '2026-11-02-breakout-pins'
        where bot_instance_id = item.bot_instance_id and is_active;
        n := n + 1;
    end loop;
    if n <> 2 then raise exception 'Expected exactly two active bot assignments, found %', n; end if;
end $$;
commit;
