#!/usr/bin/env bash
set -euo pipefail

DEPLOY_DIR="/opt/trading-bot"
TRAEFIK_DYNAMIC_DIR="/opt/traefik/dynamic"
PROJECT_NAME="trading-bot"
COMPOSE_FILE="${DEPLOY_DIR}/docker-compose.prod.yml"
TRAEFIK_DYNAMIC_FILE="${TRAEFIK_DYNAMIC_DIR}/trading-bot.yml"
WORKER_APPSETTINGS="${DEPLOY_DIR}/appsettings.json"
WORKER_APPSETTINGS_SOURCE="src/TradingBot.Worker/appsettings.json"
WORKER_ENV_FILE="${DEPLOY_DIR}/.env"
WORKER_DATA_DIR="${DEPLOY_DIR}/data"
WORKER_LOG_DIR="${DEPLOY_DIR}/logs"
DATABASE_DIR="${DEPLOY_DIR}/database"

: "${UI_IMAGE_NAME:?UI_IMAGE_NAME is required}"
: "${API_IMAGE_NAME:?API_IMAGE_NAME is required}"
: "${WORKER_IMAGE_NAME:?WORKER_IMAGE_NAME is required}"
: "${GHCR_USERNAME:?GHCR_USERNAME is required}"
: "${GHCR_TOKEN:?GHCR_TOKEN is required}"
: "${TRADINGBOT_DB_PASSWORD:?TRADINGBOT_DB_PASSWORD is required}"
: "${TRADINGBOT_KRAKEN_API_KEY:?TRADINGBOT_KRAKEN_API_KEY is required}"
: "${TRADINGBOT_KRAKEN_API_SECRET:?TRADINGBOT_KRAKEN_API_SECRET is required}"
: "${TRADINGBOT_OPENAI_API_KEY:?TRADINGBOT_OPENAI_API_KEY is required}"

UI_IMAGE_TAG="${UI_IMAGE_TAG:-latest}"
API_IMAGE_TAG="${API_IMAGE_TAG:-${UI_IMAGE_TAG}}"
WORKER_IMAGE_TAG="${WORKER_IMAGE_TAG:-${UI_IMAGE_TAG}}"
TRAEFIK_NETWORK="${TRAEFIK_NETWORK:-traefik}"

echo "Deploying stack '${PROJECT_NAME}' to ${DEPLOY_DIR}"
echo "  ui     = ${UI_IMAGE_NAME}:${UI_IMAGE_TAG}"
echo "  api    = ${API_IMAGE_NAME}:${API_IMAGE_TAG}"
echo "  worker = ${WORKER_IMAGE_NAME}:${WORKER_IMAGE_TAG}"

mkdir -p "${DEPLOY_DIR}" "${TRAEFIK_DYNAMIC_DIR}" "${WORKER_DATA_DIR}" "${WORKER_LOG_DIR}" "${DATABASE_DIR}"
cp infra/docker-compose.prod.yml "${COMPOSE_FILE}"
cp infra/traefik/trading-bot.yml "${TRAEFIK_DYNAMIC_FILE}"

# Keep the mounted worker config in sync with repository settings on every deploy.
echo "Updating ${WORKER_APPSETTINGS} from repository config"
cp "${WORKER_APPSETTINGS_SOURCE}" "${WORKER_APPSETTINGS}"

echo "Writing worker environment overrides to ${WORKER_ENV_FILE}"
umask 077
{
  printf 'TRADINGBOT_DB_PASSWORD=%s\n' "${TRADINGBOT_DB_PASSWORD:-}"
  printf 'POSTGRES_PASSWORD=%s\n' "${TRADINGBOT_DB_PASSWORD:-}"
  printf 'TRADINGBOT_DATABASE_ENABLED=true\n'
  printf 'TRADINGBOT_DATABASE_CONNECTION_STRING=Host=database;Port=5432;Database=tradingbot;Username=tradingbot;Password=%s\n' "${TRADINGBOT_DB_PASSWORD:-}"
  printf 'TRADINGBOT_MARKET_DATA_MODE=kraken\n'
  printf 'TRADINGBOT_LIVE_TRADING_ENABLED=false\n'
  printf 'TRADINGBOT_LOG_DIRECTORY=/app/logs\n'
  printf 'TRADINGBOT_KRAKEN_API_KEY=%s\n' "${TRADINGBOT_KRAKEN_API_KEY:-}"
  printf 'TRADINGBOT_KRAKEN_API_SECRET=%s\n' "${TRADINGBOT_KRAKEN_API_SECRET:-}"
  printf 'TRADINGBOT_OPENAI_API_KEY=%s\n' "${TRADINGBOT_OPENAI_API_KEY:-}"
} > "${WORKER_ENV_FILE}"

echo "${GHCR_TOKEN}" | docker login ghcr.io \
  --username "${GHCR_USERNAME}" \
  --password-stdin

export UI_IMAGE_NAME
export UI_IMAGE_TAG
export API_IMAGE_NAME
export API_IMAGE_TAG
export WORKER_IMAGE_NAME
export WORKER_IMAGE_TAG
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

# Database health: Postgres must be ready before the worker can persist dry-run state.
docker compose \
  -p "${PROJECT_NAME}" \
  -f "${COMPOSE_FILE}" \
  exec -T database pg_isready -U tradingbot -d tradingbot
echo "Health check passed for trading-bot-db container."

# UI health: nginx must answer over HTTP.
docker compose \
  -p "${PROJECT_NAME}" \
  -f "${COMPOSE_FILE}" \
  exec -T ui wget -q -O /tmp/trading-bot-ui-healthcheck.html http://127.0.0.1/
echo "Health check passed for trading-bot-ui container."

# API health: read-only HTTP API must answer inside the compose network.
docker compose \
  -p "${PROJECT_NAME}" \
  -f "${COMPOSE_FILE}" \
  exec -T ui wget -q -O - http://trading-bot-api:8080/api/health
echo "Health check passed for trading-bot-api container."

# Worker health: it has no HTTP endpoint, so verify the container is running.
WORKER_RUNNING="$(docker inspect -f '{{.State.Running}}' trading-bot-worker 2>/dev/null || echo false)"
if [ "${WORKER_RUNNING}" != "true" ]; then
  echo "ERROR: trading-bot-worker is not running."
  docker logs --tail 50 trading-bot-worker || true
  exit 1
fi
echo "Health check passed for trading-bot-worker container."
