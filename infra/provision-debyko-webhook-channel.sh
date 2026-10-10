#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 2 || $# -gt 3 ]]; then
  echo "usage: $0 <bot-instance-id> <public-api-base-url> [postgres-container]" >&2
  exit 2
fi

BOT_INSTANCE_ID="$(printf '%s' "$1" | tr '[:upper:]' '[:lower:]')"
PUBLIC_API_BASE_URL="${2%/}"
POSTGRES_CONTAINER="${3:-trading-bot-db}"
NEW_CHANNEL_ID="$(uuidgen | tr '[:upper:]' '[:lower:]')"
SIGNING_SECRET="whsec_$(openssl rand -base64 32 | tr -d '\n')"

CHANNEL_ID="$({
  docker exec -i "$POSTGRES_CONTAINER" \
    psql -X -qAt -v ON_ERROR_STOP=1 -U tradingbot -d tradingbot \
      -v bot_instance_id="$BOT_INSTANCE_ID" \
      -v channel_id="$NEW_CHANNEL_ID" \
      -v signing_secret="$SIGNING_SECRET" <<'SQL'
insert into debyko_webhook_channels (
    channel_id,
    bot_instance_id,
    signing_secret,
    is_active
)
values (
    :'channel_id'::uuid,
    :'bot_instance_id',
    :'signing_secret',
    true
)
on conflict (bot_instance_id) do update set
    signing_secret = excluded.signing_secret,
    is_active = true,
    updated_at = now()
returning channel_id;
SQL
} | tail -1)"

cat <<EOF
Bot instance: $BOT_INSTANCE_ID
Webhook URL: ${PUBLIC_API_BASE_URL}/api/webhooks/debyko/${CHANNEL_ID}
Signing key: ${SIGNING_SECRET}

Enter that URL and key in the DEBYKO Agent webhook channel. The key is shown here once;
the database keeps it in plaintext so TradingBot.Api can verify the raw request body.
EOF
