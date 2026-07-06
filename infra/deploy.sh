#!/usr/bin/env bash
set -euo pipefail

DEPLOY_DIR="/opt/trading-bot"
TRAEFIK_DYNAMIC_DIR="/opt/traefik/dynamic"
PROJECT_NAME="trading-bot"
COMPOSE_FILE="${DEPLOY_DIR}/docker-compose.prod.yml"
TRAEFIK_DYNAMIC_FILE="${TRAEFIK_DYNAMIC_DIR}/trading-bot.yml"
WORKER_APPSETTINGS_SOURCE="src/TradingBot.SpotWorker/appsettings.json"
API_DIR="${DEPLOY_DIR}/api"
API_ENV_FILE="${API_DIR}/.env"
SPOT_DIR="${DEPLOY_DIR}/spot"
LIVE_DIR="${SPOT_DIR}/live"
VIRTUAL_DIR="${SPOT_DIR}/virtual"
LIVE_APPSETTINGS="${LIVE_DIR}/appsettings.json"
VIRTUAL_APPSETTINGS="${VIRTUAL_DIR}/appsettings.json"
LIVE_ENV_FILE="${LIVE_DIR}/.env"
VIRTUAL_ENV_FILE="${VIRTUAL_DIR}/.env"
DATABASE_DIR="${DEPLOY_DIR}/database"
DATABASE_ENV_DIR="${DEPLOY_DIR}/postgres"
DATABASE_ENV_FILE="${DATABASE_ENV_DIR}/.env"

: "${UI_IMAGE_NAME:?UI_IMAGE_NAME is required}"
: "${API_IMAGE_NAME:?API_IMAGE_NAME is required}"
: "${SPOT_WORKER_IMAGE_NAME:?SPOT_WORKER_IMAGE_NAME is required}"
: "${GHCR_USERNAME:?GHCR_USERNAME is required}"
: "${GHCR_TOKEN:?GHCR_TOKEN is required}"
: "${TRADINGBOT_DB_PASSWORD:?TRADINGBOT_DB_PASSWORD is required}"
: "${TRADINGBOT_KRAKEN_API_KEY:?TRADINGBOT_KRAKEN_API_KEY is required}"
: "${TRADINGBOT_KRAKEN_API_SECRET:?TRADINGBOT_KRAKEN_API_SECRET is required}"
: "${TRADINGBOT_OPENAI_API_KEY:?TRADINGBOT_OPENAI_API_KEY is required}"

UI_IMAGE_TAG="${UI_IMAGE_TAG:-latest}"
API_IMAGE_TAG="${API_IMAGE_TAG:-${UI_IMAGE_TAG}}"
SPOT_WORKER_IMAGE_TAG="${SPOT_WORKER_IMAGE_TAG:-${UI_IMAGE_TAG}}"
TRAEFIK_NETWORK="${TRAEFIK_NETWORK:-traefik}"

echo "Deploying stack '${PROJECT_NAME}' to ${DEPLOY_DIR}"
echo "  ui     = ${UI_IMAGE_NAME}:${UI_IMAGE_TAG}"
echo "  api    = ${API_IMAGE_NAME}:${API_IMAGE_TAG}"
echo "  worker = ${SPOT_WORKER_IMAGE_NAME}:${SPOT_WORKER_IMAGE_TAG}"
echo "  live   = trading-bot-spot-worker-live"
echo "  virtual= trading-bot-spot-worker-virtual"

mkdir -p \
  "${DEPLOY_DIR}" \
  "${TRAEFIK_DYNAMIC_DIR}" \
  "${API_DIR}" \
  "${LIVE_DIR}/data" \
  "${LIVE_DIR}/logs" \
  "${VIRTUAL_DIR}/data" \
  "${VIRTUAL_DIR}/logs" \
  "${DATABASE_DIR}" \
  "${DATABASE_ENV_DIR}"
cp infra/docker-compose.prod.yml "${COMPOSE_FILE}"
cp infra/traefik/trading-bot.yml "${TRAEFIK_DYNAMIC_FILE}"

# Keep virtual config in sync with repository settings on every deploy.
# Live config is created only once and then treated as operator-owned.
if [ -f "${LIVE_APPSETTINGS}" ]; then
  echo "Keeping existing live worker appsettings at ${LIVE_APPSETTINGS}"
else
  echo "Creating live worker appsettings at ${LIVE_APPSETTINGS}"
  cp "${WORKER_APPSETTINGS_SOURCE}" "${LIVE_APPSETTINGS}"
fi
echo "Updating virtual worker appsettings from repository config"
cp "${WORKER_APPSETTINGS_SOURCE}" "${VIRTUAL_APPSETTINGS}"

# Live trading comes from the GitHub PROD environment variable
# TRADINGBOT_LIVE_TRADING_ENABLED. Anything but an explicit "true" (any case)
# deploys with live trading OFF, so a missing/typo'd variable stays safe.
if [ "$(printf '%s' "${TRADINGBOT_LIVE_TRADING_ENABLED:-false}" | tr '[:upper:]' '[:lower:]')" = "true" ]; then
  LIVE_TRADING_FLAG="true"
  echo "!!! TRADINGBOT_LIVE_TRADING_ENABLED=true from PROD environment: deploying with LIVE trading ON !!!"
else
  LIVE_TRADING_FLAG="false"
  echo "Live trading disabled (TRADINGBOT_LIVE_TRADING_ENABLED='${TRADINGBOT_LIVE_TRADING_ENABLED:-}' is not 'true')"
fi

echo "Writing API environment to ${API_ENV_FILE}"
umask 077
{
  printf 'TRADINGBOT_DATABASE_ENABLED=true\n'
  printf 'TRADINGBOT_DATABASE_CONNECTION_STRING=Host=database;Port=5432;Database=tradingbot;Username=tradingbot;Password=%s\n' "${TRADINGBOT_DB_PASSWORD:-}"
} > "${API_ENV_FILE}"

echo "Writing database environment to ${DATABASE_ENV_FILE}"
{
  printf 'POSTGRES_PASSWORD=%s\n' "${TRADINGBOT_DB_PASSWORD:-}"
} > "${DATABASE_ENV_FILE}"

if [ -f "${LIVE_ENV_FILE}" ]; then
  echo "Keeping existing live worker environment at ${LIVE_ENV_FILE}"
  # One-time upgrade of the operator-owned file to the market-prefixed
  # instance-id scheme; without this the live worker keeps writing rows
  # under the retired 'live' id.
  if grep -q '^TRADINGBOT_BOT_INSTANCE_ID=live$' "${LIVE_ENV_FILE}"; then
    echo "Upgrading live worker instance id: live -> spot-live"
    sed -i 's/^TRADINGBOT_BOT_INSTANCE_ID=live$/TRADINGBOT_BOT_INSTANCE_ID=spot-live/' "${LIVE_ENV_FILE}"
  fi
else
  echo "Creating live worker environment at ${LIVE_ENV_FILE}"
  {
    printf 'TRADINGBOT_BOT_INSTANCE_ID=spot-live\n'
    printf 'TRADINGBOT_BOT_INSTANCE_NAME=Live spot worker\n'
    printf 'TRADINGBOT_DB_PASSWORD=%s\n' "${TRADINGBOT_DB_PASSWORD:-}"
    printf 'TRADINGBOT_DATABASE_ENABLED=true\n'
    printf 'TRADINGBOT_DATABASE_CONNECTION_STRING=Host=database;Port=5432;Database=tradingbot;Username=tradingbot;Password=%s\n' "${TRADINGBOT_DB_PASSWORD:-}"
    printf 'TRADINGBOT_MARKET_DATA_MODE=kraken\n'
    printf 'TRADINGBOT_KRAKEN_API_KEY=%s\n' "${TRADINGBOT_KRAKEN_API_KEY:-}"
    printf 'TRADINGBOT_KRAKEN_API_SECRET=%s\n' "${TRADINGBOT_KRAKEN_API_SECRET:-}"
    printf 'TRADINGBOT_OPENAI_API_KEY=%s\n' "${TRADINGBOT_OPENAI_API_KEY:-}"
    printf 'TRADINGBOT_LIVE_TRADING_ENABLED=%s\n' "${LIVE_TRADING_FLAG}"
    printf 'TRADINGBOT_LOG_DIRECTORY=/app/logs\n'
  } > "${LIVE_ENV_FILE}"
fi

echo "Writing virtual worker environment to ${VIRTUAL_ENV_FILE}"
{
  printf 'TRADINGBOT_BOT_INSTANCE_ID=spot-virtual\n'
  printf 'TRADINGBOT_BOT_INSTANCE_NAME=Virtual spot worker\n'
  printf 'TRADINGBOT_DB_PASSWORD=%s\n' "${TRADINGBOT_DB_PASSWORD:-}"
  printf 'TRADINGBOT_DATABASE_ENABLED=true\n'
  printf 'TRADINGBOT_DATABASE_CONNECTION_STRING=Host=database;Port=5432;Database=tradingbot;Username=tradingbot;Password=%s\n' "${TRADINGBOT_DB_PASSWORD:-}"
  printf 'TRADINGBOT_MARKET_DATA_MODE=kraken\n'
  printf 'TRADINGBOT_KRAKEN_API_KEY=%s\n' "${TRADINGBOT_KRAKEN_API_KEY:-}"
  printf 'TRADINGBOT_KRAKEN_API_SECRET=%s\n' "${TRADINGBOT_KRAKEN_API_SECRET:-}"
  printf 'TRADINGBOT_OPENAI_API_KEY=%s\n' "${TRADINGBOT_OPENAI_API_KEY:-}"
  printf 'TRADINGBOT_LIVE_TRADING_ENABLED=false\n'
  printf 'TRADINGBOT_LOG_DIRECTORY=/app/logs\n'
} > "${VIRTUAL_ENV_FILE}"

echo "${GHCR_TOKEN}" | docker login ghcr.io \
  --username "${GHCR_USERNAME}" \
  --password-stdin

export UI_IMAGE_NAME
export UI_IMAGE_TAG
export API_IMAGE_NAME
export API_IMAGE_TAG
export SPOT_WORKER_IMAGE_NAME
export SPOT_WORKER_IMAGE_TAG
export TRAEFIK_NETWORK

docker compose \
  -p "${PROJECT_NAME}" \
  -f "${COMPOSE_FILE}" \
  pull

docker compose \
  -p "${PROJECT_NAME}" \
  -f "${COMPOSE_FILE}" \
  up -d --remove-orphans

docker compose \
  -p "${PROJECT_NAME}" \
  -f "${COMPOSE_FILE}" \
  ps

run_healthcheck_with_retries() {
  local description="$1"
  local attempts="$2"
  local delay_seconds="$3"
  shift 3

  local attempt
  for attempt in $(seq 1 "${attempts}"); do
    if "$@"; then
      echo "Health check passed for ${description}."
      return 0
    fi

    if [ "${attempt}" -lt "${attempts}" ]; then
      echo "Health check for ${description} failed; retrying in ${delay_seconds}s (${attempt}/${attempts})..."
      sleep "${delay_seconds}"
    fi
  done

  echo "ERROR: health check failed for ${description} after ${attempts} attempts."
  return 1
}

# Database health: Postgres must be ready before the worker can persist dry-run state.
run_healthcheck_with_retries "trading-bot-db container" 30 2 \
  docker compose \
    -p "${PROJECT_NAME}" \
    -f "${COMPOSE_FILE}" \
    exec -T database pg_isready -U tradingbot -d tradingbot

# UI health: nginx must answer over HTTP.
run_healthcheck_with_retries "trading-bot-ui container" 30 2 \
  docker compose \
    -p "${PROJECT_NAME}" \
    -f "${COMPOSE_FILE}" \
    exec -T ui wget -q -O /tmp/trading-bot-ui-healthcheck.html http://127.0.0.1/

# API health: read-only HTTP API must answer inside the compose network.
run_healthcheck_with_retries "trading-bot-api container" 30 2 \
  docker compose \
    -p "${PROJECT_NAME}" \
    -f "${COMPOSE_FILE}" \
    exec -T ui wget -q -O - http://trading-bot-api:8080/api/health

# Worker health: workers have no HTTP endpoint, so verify both containers are running.
for WORKER_CONTAINER in trading-bot-spot-worker-live trading-bot-spot-worker-virtual; do
  WORKER_RUNNING="$(docker inspect -f '{{.State.Running}}' "${WORKER_CONTAINER}" 2>/dev/null || echo false)"
  if [ "${WORKER_RUNNING}" != "true" ]; then
    echo "ERROR: ${WORKER_CONTAINER} is not running."
    docker logs --tail 50 "${WORKER_CONTAINER}" || true
    exit 1
  fi
  echo "Health check passed for ${WORKER_CONTAINER} container."
done
