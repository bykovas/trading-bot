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
WEB_DIR="${DEPLOY_DIR}/web"
WEB_ENV_FILE="${WEB_DIR}/.env"
SPOT_DIR="${DEPLOY_DIR}/spot"
LIVE_DIR="${SPOT_DIR}/live"
VIRTUAL_DIR="${SPOT_DIR}/virtual"
LIVE_APPSETTINGS="${LIVE_DIR}/appsettings.json"
VIRTUAL_APPSETTINGS="${VIRTUAL_DIR}/appsettings.json"
LIVE_ENV_FILE="${LIVE_DIR}/.env"
VIRTUAL_ENV_FILE="${VIRTUAL_DIR}/.env"
FUTURES_DIR="${DEPLOY_DIR}/futures"
FUTURES_LIVE_DIR="${FUTURES_DIR}/live"
FUTURES_LUKAS_LIVE_DIR="${FUTURES_DIR}/lukas-live"
FUTURES_VIRTUAL_DIR="${FUTURES_DIR}/virtual"
FUTURES_APPSETTINGS_SOURCE="src/TradingBot.FuturesWorker/appsettings.json"
FUTURES_LUKAS_APPSETTINGS_SOURCE="src/TradingBot.FuturesWorker/appsettings.lukas.json"
FUTURES_LIVE_APPSETTINGS="${FUTURES_LIVE_DIR}/appsettings.json"
FUTURES_LUKAS_LIVE_APPSETTINGS="${FUTURES_LUKAS_LIVE_DIR}/appsettings.json"
FUTURES_VIRTUAL_APPSETTINGS="${FUTURES_VIRTUAL_DIR}/appsettings.json"
FUTURES_LIVE_ENV_FILE="${FUTURES_LIVE_DIR}/.env"
FUTURES_LUKAS_LIVE_ENV_FILE="${FUTURES_LUKAS_LIVE_DIR}/.env"
FUTURES_VIRTUAL_ENV_FILE="${FUTURES_VIRTUAL_DIR}/.env"
DATABASE_DIR="${DEPLOY_DIR}/database"
DATABASE_ENV_DIR="${DEPLOY_DIR}/postgres"
DATABASE_ENV_FILE="${DATABASE_ENV_DIR}/.env"
MARKET_DATA_APPSETTINGS_SOURCE="src/TradingBot.MarketDataWorker/appsettings.json"
MARKET_DATA_DIR="${DEPLOY_DIR}/market-data"
MARKET_DATA_APPSETTINGS="${MARKET_DATA_DIR}/appsettings.json"
MARKET_DATA_ENV_FILE="${MARKET_DATA_DIR}/.env"

: "${UI_IMAGE_NAME:?UI_IMAGE_NAME is required}"
: "${API_IMAGE_NAME:?API_IMAGE_NAME is required}"
: "${WEB_IMAGE_NAME:?WEB_IMAGE_NAME is required}"
: "${SPOT_WORKER_IMAGE_NAME:?SPOT_WORKER_IMAGE_NAME is required}"
: "${FUTURES_WORKER_IMAGE_NAME:?FUTURES_WORKER_IMAGE_NAME is required}"
: "${MARKET_DATA_WORKER_IMAGE_NAME:?MARKET_DATA_WORKER_IMAGE_NAME is required}"
: "${GHCR_USERNAME:?GHCR_USERNAME is required}"
: "${GHCR_TOKEN:?GHCR_TOKEN is required}"
: "${TRADINGBOT_DB_PASSWORD:?TRADINGBOT_DB_PASSWORD is required}"
: "${TRADINGBOT_KRAKEN_API_KEY:?TRADINGBOT_KRAKEN_API_KEY is required}"
: "${TRADINGBOT_KRAKEN_API_SECRET:?TRADINGBOT_KRAKEN_API_SECRET is required}"
: "${TRADINGBOT_OPENAI_API_KEY:?TRADINGBOT_OPENAI_API_KEY is required}"

UI_IMAGE_TAG="${UI_IMAGE_TAG:-latest}"
API_IMAGE_TAG="${API_IMAGE_TAG:-${UI_IMAGE_TAG}}"
WEB_IMAGE_TAG="${WEB_IMAGE_TAG:-${UI_IMAGE_TAG}}"
SPOT_WORKER_IMAGE_TAG="${SPOT_WORKER_IMAGE_TAG:-${UI_IMAGE_TAG}}"
FUTURES_WORKER_IMAGE_TAG="${FUTURES_WORKER_IMAGE_TAG:-${UI_IMAGE_TAG}}"
MARKET_DATA_WORKER_IMAGE_TAG="${MARKET_DATA_WORKER_IMAGE_TAG:-${UI_IMAGE_TAG}}"
TRAEFIK_NETWORK="${TRAEFIK_NETWORK:-traefik}"
POSTGRES_BIND_HOST="${POSTGRES_BIND_HOST:-127.0.0.1}"

echo "Deploying stack '${PROJECT_NAME}' to ${DEPLOY_DIR}"
echo "  ui     = ${UI_IMAGE_NAME}:${UI_IMAGE_TAG}"
echo "  api    = ${API_IMAGE_NAME}:${API_IMAGE_TAG}"
echo "  web    = ${WEB_IMAGE_NAME}:${WEB_IMAGE_TAG}"
echo "  worker = ${SPOT_WORKER_IMAGE_NAME}:${SPOT_WORKER_IMAGE_TAG}"
echo "  live   = trading-bot-spot-worker-live"
echo "  virtual= trading-bot-spot-worker-virtual"
echo "  futures= ${FUTURES_WORKER_IMAGE_NAME}:${FUTURES_WORKER_IMAGE_TAG}"
echo "  lukas  = trading-bot-lukas-futures-worker-live"
echo "  market-data= ${MARKET_DATA_WORKER_IMAGE_NAME}:${MARKET_DATA_WORKER_IMAGE_TAG}"

mkdir -p \
  "${DEPLOY_DIR}" \
  "${TRAEFIK_DYNAMIC_DIR}" \
  "${API_DIR}" \
  "${WEB_DIR}" \
  "${LIVE_DIR}/data" \
  "${LIVE_DIR}/logs" \
  "${VIRTUAL_DIR}/data" \
  "${VIRTUAL_DIR}/logs" \
  "${FUTURES_LIVE_DIR}/data" \
  "${FUTURES_LIVE_DIR}/logs" \
  "${FUTURES_LUKAS_LIVE_DIR}/data" \
  "${FUTURES_LUKAS_LIVE_DIR}/logs" \
  "${FUTURES_VIRTUAL_DIR}/data" \
  "${FUTURES_VIRTUAL_DIR}/logs" \
  "${MARKET_DATA_DIR}/logs" \
  "${DATABASE_DIR}" \
  "${DATABASE_ENV_DIR}"
cp infra/docker-compose.prod.yml "${COMPOSE_FILE}"
cp infra/traefik/trading-bot.yml "${TRAEFIK_DYNAMIC_FILE}"

# Appsettings files are repository-owned and refreshed on every deploy. The primary
# live and virtual workers share the default profile, but entry mirroring activates
# only in live mode. Lukas publishes normal entries; primary live follows them with
# the opposite side. Runtime identity and secrets belong in .env files.
#
# install_config overwrites the destination even when the existing file is owned by
# another user (e.g. a live appsettings.json created root-owned by an earlier
# create-once deploy): plain `cp` truncates in place and fails with EACCES on such a
# file, so remove-then-copy first, which only needs the (writable) target directory
# and re-creates the file owned by the deploy user.
install_config() {
  local src="$1" dest="$2"
  rm -f "${dest}"
  cp "${src}" "${dest}"
}

echo "Updating live worker appsettings from repository config (identical to virtual)"
install_config "${WORKER_APPSETTINGS_SOURCE}" "${LIVE_APPSETTINGS}"
echo "Updating virtual worker appsettings from repository config"
install_config "${WORKER_APPSETTINGS_SOURCE}" "${VIRTUAL_APPSETTINGS}"

echo "Updating futures live appsettings from repository config (mirror follower in live mode)"
install_config "${FUTURES_APPSETTINGS_SOURCE}" "${FUTURES_LIVE_APPSETTINGS}"
echo "Updating Lukas futures live appsettings from repository config"
install_config "${FUTURES_LUKAS_APPSETTINGS_SOURCE}" "${FUTURES_LUKAS_LIVE_APPSETTINGS}"
echo "Updating futures virtual appsettings from repository config"
install_config "${FUTURES_APPSETTINGS_SOURCE}" "${FUTURES_VIRTUAL_APPSETTINGS}"
echo "Updating market data worker appsettings from repository config"
install_config "${MARKET_DATA_APPSETTINGS_SOURCE}" "${MARKET_DATA_APPSETTINGS}"

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

if [ "$(printf '%s' "${TRADINGBOT_FUTURES_LIVE_TRADING_ENABLED:-false}" | tr '[:upper:]' '[:lower:]')" = "true" ]; then
  FUTURES_LIVE_TRADING_FLAG="true"
  : "${TRADINGBOT_KRAKEN_FUTURES_API_KEY:?TRADINGBOT_KRAKEN_FUTURES_API_KEY is required when futures live trading is enabled}"
  : "${TRADINGBOT_KRAKEN_FUTURES_API_SECRET:?TRADINGBOT_KRAKEN_FUTURES_API_SECRET is required when futures live trading is enabled}"
  echo "!!! TRADINGBOT_FUTURES_LIVE_TRADING_ENABLED=true from PROD environment: deploying with FUTURES LIVE trading ON !!!"
else
  FUTURES_LIVE_TRADING_FLAG="false"
  echo "Futures live trading disabled (TRADINGBOT_FUTURES_LIVE_TRADING_ENABLED='${TRADINGBOT_FUTURES_LIVE_TRADING_ENABLED:-}' is not 'true')"
fi

if [ "$(printf '%s' "${TRADINGBOT_LUKAS_FUTURES_LIVE_TRADING_ENABLED:-false}" | tr '[:upper:]' '[:lower:]')" = "true" ]; then
  FUTURES_LUKAS_LIVE_TRADING_FLAG="true"
  : "${TRADINGBOT_LUKAS_KRAKEN_FUTURES_API_KEY:?TRADINGBOT_LUKAS_KRAKEN_FUTURES_API_KEY is required when Lukas futures live trading is enabled}"
  : "${TRADINGBOT_LUKAS_KRAKEN_FUTURES_API_SECRET:?TRADINGBOT_LUKAS_KRAKEN_FUTURES_API_SECRET is required when Lukas futures live trading is enabled}"
  COMPOSE_PROFILES="lukas-live"
  echo "!!! TRADINGBOT_LUKAS_FUTURES_LIVE_TRADING_ENABLED=true from PROD environment: deploying Lukas FUTURES LIVE trading ON !!!"
else
  FUTURES_LUKAS_LIVE_TRADING_FLAG="false"
  COMPOSE_PROFILES=""
  echo "Lukas futures live trading disabled (TRADINGBOT_LUKAS_FUTURES_LIVE_TRADING_ENABLED='${TRADINGBOT_LUKAS_FUTURES_LIVE_TRADING_ENABLED:-}' is not 'true')"
fi

echo "Writing API environment to ${API_ENV_FILE}"
umask 077
{
  printf 'TRADINGBOT_DATABASE_ENABLED=true\n'
  printf 'TRADINGBOT_DATABASE_CONNECTION_STRING=Host=database;Port=5432;Database=tradingbot;Username=tradingbot;Password=%s\n' "${TRADINGBOT_DB_PASSWORD:-}"
  # Optional keyed Base RPC for the coin rate; the free endpoints are the
  # fallback, so an unset secret is not an error. DRPC_API_KEY carries the bare
  # key, so the URL is assembled here; an explicit TRADINGBOT_BASE_RPC_URL wins.
  if [[ -n "${TRADINGBOT_BASE_RPC_URL:-}" ]]; then
    printf 'TRADINGBOT_BASE_RPC_URL=%s\n' "${TRADINGBOT_BASE_RPC_URL}"
  elif [[ -n "${DRPC_API_KEY:-}" ]]; then
    printf 'TRADINGBOT_BASE_RPC_URL=https://lb.drpc.live/base/%s\n' "${DRPC_API_KEY}"
  fi
} > "${API_ENV_FILE}"

echo "Writing web environment to ${WEB_ENV_FILE}"
{
  printf 'TRADINGBOT_DATABASE_ENABLED=true\n'
  printf 'TRADINGBOT_DATABASE_CONNECTION_STRING=Host=database;Port=5432;Database=tradingbot;Username=tradingbot;Password=%s\n' "${TRADINGBOT_DB_PASSWORD:-}"
  printf 'ASPNETCORE_PATHBASE=/web\n'
} > "${WEB_ENV_FILE}"

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
    printf 'TRADINGBOT_MARKET_DATA_MODE=database\n'
    printf 'TRADINGBOT_MARKET_DATA_FALLBACK_ENABLED=true\n'
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
  printf 'TRADINGBOT_MARKET_DATA_MODE=database\n'
  printf 'TRADINGBOT_MARKET_DATA_FALLBACK_ENABLED=true\n'
  printf 'TRADINGBOT_KRAKEN_API_KEY=%s\n' "${TRADINGBOT_KRAKEN_API_KEY:-}"
  printf 'TRADINGBOT_KRAKEN_API_SECRET=%s\n' "${TRADINGBOT_KRAKEN_API_SECRET:-}"
  printf 'TRADINGBOT_OPENAI_API_KEY=%s\n' "${TRADINGBOT_OPENAI_API_KEY:-}"
  printf 'TRADINGBOT_LIVE_TRADING_ENABLED=false\n'
  printf 'TRADINGBOT_LOG_DIRECTORY=/app/logs\n'
} > "${VIRTUAL_ENV_FILE}"

# Futures live execution is separately gated from spot and remains off unless
# TRADINGBOT_FUTURES_LIVE_TRADING_ENABLED is explicitly true in the PROD env.
# The live env is still operator-owned/create-once like spot's, with safe upgrades.
if [ -f "${FUTURES_LIVE_ENV_FILE}" ]; then
  echo "Keeping existing futures live environment at ${FUTURES_LIVE_ENV_FILE}"
  if grep -q '^TRADINGBOT_FUTURES_LIVE_TRADING_ENABLED=' "${FUTURES_LIVE_ENV_FILE}"; then
    sed -i "s/^TRADINGBOT_FUTURES_LIVE_TRADING_ENABLED=.*/TRADINGBOT_FUTURES_LIVE_TRADING_ENABLED=${FUTURES_LIVE_TRADING_FLAG}/" "${FUTURES_LIVE_ENV_FILE}"
  else
    printf 'TRADINGBOT_FUTURES_LIVE_TRADING_ENABLED=%s\n' "${FUTURES_LIVE_TRADING_FLAG}" >> "${FUTURES_LIVE_ENV_FILE}"
  fi
  if ! grep -q '^TRADINGBOT_KRAKEN_FUTURES_API_KEY=' "${FUTURES_LIVE_ENV_FILE}"; then
    printf 'TRADINGBOT_KRAKEN_FUTURES_API_KEY=%s\n' "${TRADINGBOT_KRAKEN_FUTURES_API_KEY:-}" >> "${FUTURES_LIVE_ENV_FILE}"
  fi
  if ! grep -q '^TRADINGBOT_KRAKEN_FUTURES_API_SECRET=' "${FUTURES_LIVE_ENV_FILE}"; then
    printf 'TRADINGBOT_KRAKEN_FUTURES_API_SECRET=%s\n' "${TRADINGBOT_KRAKEN_FUTURES_API_SECRET:-}" >> "${FUTURES_LIVE_ENV_FILE}"
  fi
else
  echo "Creating futures live environment at ${FUTURES_LIVE_ENV_FILE}"
  {
    printf 'TRADINGBOT_BOT_INSTANCE_ID=futures-live\n'
    printf 'TRADINGBOT_BOT_INSTANCE_NAME=Live futures worker\n'
    printf 'TRADINGBOT_DATABASE_ENABLED=true\n'
    printf 'TRADINGBOT_DATABASE_CONNECTION_STRING=Host=database;Port=5432;Database=tradingbot;Username=tradingbot;Password=%s\n' "${TRADINGBOT_DB_PASSWORD:-}"
    printf 'TRADINGBOT_MARKET_DATA_MODE=database\n'
    printf 'TRADINGBOT_MARKET_DATA_FALLBACK_ENABLED=true\n'
    printf 'TRADINGBOT_FUTURES_LIVE_TRADING_ENABLED=%s\n' "${FUTURES_LIVE_TRADING_FLAG}"
    printf 'TRADINGBOT_KRAKEN_FUTURES_API_KEY=%s\n' "${TRADINGBOT_KRAKEN_FUTURES_API_KEY:-}"
    printf 'TRADINGBOT_KRAKEN_FUTURES_API_SECRET=%s\n' "${TRADINGBOT_KRAKEN_FUTURES_API_SECRET:-}"
    printf 'TRADINGBOT_LOG_DIRECTORY=/app/logs\n'
  } > "${FUTURES_LIVE_ENV_FILE}"
fi

# Lukas has a managed, isolated runtime environment. Rewriting this file on every
# deploy rotates only his account credentials and prevents stale primary-account
# credentials from surviving in the dedicated worker.
echo "Writing Lukas futures live environment to ${FUTURES_LUKAS_LIVE_ENV_FILE}"
{
  printf 'TRADINGBOT_BOT_INSTANCE_ID=futures-lukas-live\n'
  printf 'TRADINGBOT_BOT_INSTANCE_NAME=Lukas live futures worker\n'
  printf 'TRADINGBOT_DATABASE_ENABLED=true\n'
  printf 'TRADINGBOT_DATABASE_CONNECTION_STRING=Host=database;Port=5432;Database=tradingbot;Username=tradingbot;Password=%s\n' "${TRADINGBOT_DB_PASSWORD:-}"
  printf 'TRADINGBOT_MARKET_DATA_MODE=database\n'
  printf 'TRADINGBOT_MARKET_DATA_FALLBACK_ENABLED=true\n'
  printf 'TRADINGBOT_FUTURES_LIVE_TRADING_ENABLED=%s\n' "${FUTURES_LUKAS_LIVE_TRADING_FLAG}"
  printf 'TRADINGBOT_KRAKEN_FUTURES_API_KEY=%s\n' "${TRADINGBOT_LUKAS_KRAKEN_FUTURES_API_KEY:-}"
  printf 'TRADINGBOT_KRAKEN_FUTURES_API_SECRET=%s\n' "${TRADINGBOT_LUKAS_KRAKEN_FUTURES_API_SECRET:-}"
  printf 'TRADINGBOT_LOG_DIRECTORY=/app/logs\n'
} > "${FUTURES_LUKAS_LIVE_ENV_FILE}"

echo "Writing futures virtual environment to ${FUTURES_VIRTUAL_ENV_FILE}"
{
  printf 'TRADINGBOT_BOT_INSTANCE_ID=futures-virtual\n'
  printf 'TRADINGBOT_BOT_INSTANCE_NAME=Virtual futures worker\n'
  printf 'TRADINGBOT_DATABASE_ENABLED=true\n'
  printf 'TRADINGBOT_DATABASE_CONNECTION_STRING=Host=database;Port=5432;Database=tradingbot;Username=tradingbot;Password=%s\n' "${TRADINGBOT_DB_PASSWORD:-}"
  printf 'TRADINGBOT_MARKET_DATA_MODE=database\n'
  printf 'TRADINGBOT_MARKET_DATA_FALLBACK_ENABLED=true\n'
  printf 'TRADINGBOT_FUTURES_LIVE_TRADING_ENABLED=false\n'
  printf 'TRADINGBOT_LOG_DIRECTORY=/app/logs\n'
} > "${FUTURES_VIRTUAL_ENV_FILE}"

echo "Writing market data worker environment to ${MARKET_DATA_ENV_FILE}"
{
  printf 'TRADINGBOT_DATABASE_ENABLED=true\n'
  printf 'TRADINGBOT_DATABASE_CONNECTION_STRING=Host=database;Port=5432;Database=tradingbot;Username=tradingbot;Password=%s\n' "${TRADINGBOT_DB_PASSWORD:-}"
  printf 'TRADINGBOT_MARKET_DATA_LIGHT_INTERVAL_SECONDS=30\n'
  printf 'TRADINGBOT_MARKET_DATA_CANDLE_INTERVAL_SECONDS=120\n'
  printf 'TRADINGBOT_TIMEFRAME_MINUTES=15\n'
  printf 'TRADINGBOT_MARKET_DATA_MAX_CANDLE_PAIRS=100\n'
  printf 'TRADINGBOT_MARKET_DATA_TOP_VOLUME_PAIRS=40\n'
  printf 'TRADINGBOT_MARKET_DATA_TOP_MOVER_PAIRS=40\n'
  printf 'TRADINGBOT_LOG_DIRECTORY=/app/logs\n'
} > "${MARKET_DATA_ENV_FILE}"

echo "${GHCR_TOKEN}" | docker login ghcr.io \
  --username "${GHCR_USERNAME}" \
  --password-stdin

export UI_IMAGE_NAME
export UI_IMAGE_TAG
export API_IMAGE_NAME
export API_IMAGE_TAG
export WEB_IMAGE_NAME
export WEB_IMAGE_TAG
export SPOT_WORKER_IMAGE_NAME
export SPOT_WORKER_IMAGE_TAG
export FUTURES_WORKER_IMAGE_NAME
export FUTURES_WORKER_IMAGE_TAG
export MARKET_DATA_WORKER_IMAGE_NAME
export MARKET_DATA_WORKER_IMAGE_TAG
export TRAEFIK_NETWORK
export POSTGRES_BIND_HOST
export COMPOSE_PROFILES

docker compose \
  -p "${PROJECT_NAME}" \
  -f "${COMPOSE_FILE}" \
  pull

if [ "${POSTGRES_BIND_HOST}" != "127.0.0.1" ] && [ "${POSTGRES_BIND_HOST}" != "localhost" ] && [ "${POSTGRES_BIND_HOST}" != "0.0.0.0" ]; then
  echo "Waiting for Postgres bind address ${POSTGRES_BIND_HOST} before starting database."
  for attempt in $(seq 1 60); do
    if ip -br addr | awk '{print $3}' | tr ' ' '\n' | grep -q "^${POSTGRES_BIND_HOST}/"; then
      echo "Postgres bind address ${POSTGRES_BIND_HOST} is present."
      break
    fi

    if [ "${attempt}" -eq 60 ]; then
      echo "ERROR: Postgres bind address ${POSTGRES_BIND_HOST} is not present; refusing to half-start the stack."
      exit 1
    fi

    sleep 2
  done
fi

echo "Starting database first."
docker compose \
  -p "${PROJECT_NAME}" \
  -f "${COMPOSE_FILE}" \
  up -d database

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

database_ready_check() {
  docker run --rm --network "${PROJECT_NAME}_default" postgres:16-alpine \
    pg_isready -h database -p 5432 -U tradingbot -d tradingbot >/dev/null
}

# Database health: check the real compose-network DNS alias instead of docker
# exec'ing into Postgres. This still works on hosts where Docker exec healthchecks
# are broken by runtime/seccomp issues.
run_healthcheck_with_retries "database DNS and Postgres readiness" 45 2 database_ready_check

echo "Starting application services after database readiness."
if [ "${FUTURES_LUKAS_LIVE_TRADING_FLAG}" != "true" ]; then
  docker rm -f trading-bot-lukas-futures-worker-live >/dev/null 2>&1 || true
fi
docker compose \
  -p "${PROJECT_NAME}" \
  -f "${COMPOSE_FILE}" \
  up -d --remove-orphans

docker compose \
  -p "${PROJECT_NAME}" \
  -f "${COMPOSE_FILE}" \
  ps

# UI health: nginx must answer over HTTP.
run_healthcheck_with_retries "trading-bot-ui container" 30 2 \
  docker run --rm --network container:trading-bot-ui busybox:1.36 \
    wget -q -O /dev/null http://127.0.0.1/

# Web health: MVC preview must answer inside the compose network.
run_healthcheck_with_retries "trading-bot-web container" 30 2 \
  docker run --rm --network container:trading-bot-ui busybox:1.36 \
    wget -q -O /dev/null http://trading-bot-web:8080/web/

# API health: read-only HTTP API must answer inside the compose network.
run_healthcheck_with_retries "trading-bot-api container" 30 2 \
  docker run --rm --network container:trading-bot-ui busybox:1.36 \
    wget -q -O /dev/null http://trading-bot-api:8080/api/health

# Worker health: workers have no HTTP endpoint, so verify every enabled container.
WORKER_CONTAINERS=(
  trading-bot-spot-worker-live
  trading-bot-spot-worker-virtual
  trading-bot-futures-worker-live
  trading-bot-futures-worker-virtual
)
if [ "${FUTURES_LUKAS_LIVE_TRADING_FLAG}" = "true" ]; then
  WORKER_CONTAINERS+=(trading-bot-lukas-futures-worker-live)
fi

for WORKER_CONTAINER in "${WORKER_CONTAINERS[@]}"; do
  WORKER_RUNNING="$(docker inspect -f '{{.State.Running}}' "${WORKER_CONTAINER}" 2>/dev/null || echo false)"
  if [ "${WORKER_RUNNING}" != "true" ]; then
    echo "ERROR: ${WORKER_CONTAINER} is not running."
    docker logs --tail 50 "${WORKER_CONTAINER}" || true
    exit 1
  fi
  echo "Health check passed for ${WORKER_CONTAINER} container."
done

echo "Docker disk usage before image cleanup:"
docker system df

echo "Removing unused Docker images from previous deploys."
docker image prune -a -f

echo "Docker disk usage after image cleanup:"
docker system df
