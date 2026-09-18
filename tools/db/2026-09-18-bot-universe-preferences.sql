begin;

create table if not exists bot_universe_preferences (
    bot_instance_id text primary key,
    auto_instrument_count integer not null check (auto_instrument_count between 0 and 500),
    force_include_pairs text[] not null default '{}'::text[],
    force_exclude_pairs text[] not null default '{}'::text[],
    updated_at timestamptz not null default now(),
    updated_by text,
    check (btrim(bot_instance_id) <> ''),
    check (not (force_include_pairs && force_exclude_pairs))
);

do $$
declare
    current_profile_id uuid;
    current_revision integer;
    current_values jsonb;
begin
    select assignment.profile_id, assignment.profile_revision, revision.values_jsonb
      into current_profile_id, current_revision, current_values
      from bot_instance_strategy_profiles assignment
      join bot_strategy_profiles profile on profile.profile_id = assignment.profile_id
      join bot_strategy_profile_revisions revision
        on revision.profile_id = assignment.profile_id
       and revision.revision = assignment.profile_revision
     where profile.name = 'LUKO current strategy'
       and assignment.is_active = true
     limit 1;

    if found and not (current_values -> 'Exits' ? 'AtrTrailingRegimeEnabled') then
        insert into bot_strategy_profile_revisions (
            profile_id, revision, values_jsonb, change_note, created_by)
        values (
            current_profile_id,
            current_revision + 1,
            jsonb_set(
                jsonb_set(
                    jsonb_set(
                        jsonb_set(current_values, '{Exits,AtrTrailingRegimeEnabled}', 'false'::jsonb, true),
                        '{Exits,TrailingActivationRMultiple}', '0'::jsonb, true),
                    '{Exits,TrailingAtrMultiple}', '0'::jsonb, true),
                '{Exits,MaxHoldTrailingStopPercent}', '0'::jsonb, true),
            'Explicit legacy fixed-percent exit mode for UI and worker parity',
            'migration');

        update bot_instance_strategy_profiles
           set profile_revision = current_revision + 1,
               updated_at = now(),
               updated_by = 'migration'
         where profile_id = current_profile_id
           and profile_revision = current_revision;
    end if;
end $$;

insert into bot_universe_preferences (
    bot_instance_id,
    auto_instrument_count,
    force_include_pairs,
    force_exclude_pairs,
    updated_by)
values
    (
        'futures-live',
        78,
        array[
            'XBT/USD', 'ETH/USD', 'SOL/USD', 'XRP/USD', 'HYPE/USD', 'DOGE/USD',
            'ADA/USD', 'SUI/USD', 'ZEC/USD', 'LINK/USD', 'AVAX/USD', 'NEAR/USD',
            'XLM/USD', 'BNB/USD', 'LTC/USD', 'BCH/USD', 'AAVE/USD', 'TAO/USD',
            'INJ/USD', 'ENA/USD', 'ONDO/USD', 'UNI/USD', 'TRX/USD', 'DOT/USD',
            'ATOM/USD', 'FIL/USD', 'ARB/USD', 'OP/USD', 'POL/USD', 'CRV/USD',
            'HBAR/USD', 'LDO/USD', 'XMR/USD', 'PEPE/USD', 'WIF/USD', 'SHIB/USD',
            'BONK/USD', 'PENGU/USD', 'APT/USD', 'ALGO/USD'
        ],
        array[]::text[],
        'seed-from-current-appsettings'
    ),
    (
        'futures-lukas-live',
        50,
        array[
            'XBT/USD', 'ETH/USD', 'SOL/USD', 'XRP/USD', 'HYPE/USD', 'DOGE/USD',
            'ADA/USD', 'SUI/USD', 'ZEC/USD', 'LINK/USD', 'AVAX/USD', 'NEAR/USD',
            'XLM/USD', 'BNB/USD', 'LTC/USD', 'BCH/USD', 'AAVE/USD', 'TAO/USD',
            'INJ/USD', 'ENA/USD', 'ONDO/USD', 'UNI/USD', 'TRX/USD', 'DOT/USD',
            'ATOM/USD', 'FIL/USD', 'ARB/USD', 'OP/USD', 'POL/USD', 'CRV/USD',
            'HBAR/USD', 'LDO/USD', 'XMR/USD', 'PEPE/USD', 'WIF/USD', 'SHIB/USD',
            'BONK/USD', 'PENGU/USD', 'APT/USD', 'ALGO/USD'
        ],
        array[]::text[],
        'seed-from-current-appsettings'
    ),
    (
        'futures-pukis-live',
        50,
        array[
            'XBT/USD', 'ETH/USD', 'SOL/USD', 'XRP/USD', 'HYPE/USD', 'DOGE/USD',
            'ADA/USD', 'SUI/USD', 'ZEC/USD', 'LINK/USD', 'AVAX/USD', 'NEAR/USD',
            'XLM/USD', 'BNB/USD', 'LTC/USD', 'BCH/USD', 'AAVE/USD', 'TAO/USD',
            'INJ/USD', 'ENA/USD', 'ONDO/USD', 'UNI/USD', 'TRX/USD', 'DOT/USD',
            'ATOM/USD', 'FIL/USD', 'ARB/USD', 'OP/USD', 'POL/USD', 'CRV/USD',
            'HBAR/USD', 'LDO/USD', 'XMR/USD', 'PEPE/USD', 'WIF/USD', 'SHIB/USD',
            'BONK/USD', 'PENGU/USD', 'APT/USD', 'ALGO/USD'
        ],
        array[]::text[],
        'seed-from-current-appsettings'
    )
on conflict (bot_instance_id) do update set
    auto_instrument_count = excluded.auto_instrument_count,
    force_include_pairs = excluded.force_include_pairs,
    force_exclude_pairs = excluded.force_exclude_pairs,
    updated_at = now(),
    updated_by = excluded.updated_by;

commit;
