#!/usr/bin/env bash
set -euo pipefail

DEPLOY_DIR="/opt/trading-bot"
TRAEFIK_DYNAMIC_DIR="/opt/traefik/dynamic"
PROJECT_NAME="trading-bot"
COMPOSE_FILE="${DEPLOY_DIR}/docker-compose.prod.yml"
TRAEFIK_DYNAMIC_FILE="${TRAEFIK_DYNAMIC_DIR}/trading-bot.yml"
WORKER_APPSETTINGS="${DEPLOY_DIR}/appsettings.json"
WORKER_DATA_DIR="${DEPLOY_DIR}/data"

: "${IMAGE_NAME:?IMAGE_NAME is required}"
: "${WORKER_IMAGE_NAME:?WORKER_IMAGE_NAME is required}"
: "${GHCR_USERNAME:?GHCR_USERNAME is required}"
: "${GHCR_TOKEN:?GHCR_TOKEN is required}"

IMAGE_TAG="${IMAGE_TAG:-latest}"
WORKER_IMAGE_TAG="${WORKER_IMAGE_TAG:-${IMAGE_TAG}}"
TRAEFIK_NETWORK="${TRAEFIK_NETWORK:-traefik}"

echo "Deploying stack '${PROJECT_NAME}' to ${DEPLOY_DIR}"
echo "  ui     = ${IMAGE_NAME}:${IMAGE_TAG}"
echo "  worker = ${WORKER_IMAGE_NAME}:${WORKER_IMAGE_TAG}"

mkdir -p "${DEPLOY_DIR}" "${TRAEFIK_DYNAMIC_DIR}" "${WORKER_DATA_DIR}"
cp infra/docker-compose.prod.yml "${COMPOSE_FILE}"
cp infra/traefik/trading-bot.yml "${TRAEFIK_DYNAMIC_FILE}"

# Seed the worker config once; keep host edits (risk/strategy/universe) on redeploys.
if [ ! -f "${WORKER_APPSETTINGS}" ]; then
  echo "Seeding ${WORKER_APPSETTINGS} from repository defaults"
  cp src/TradingBot.Worker/appsettings.json "${WORKER_APPSETTINGS}"
else
  echo "Keeping existing ${WORKER_APPSETTINGS}"
fi

echo "${GHCR_TOKEN}" | docker login ghcr.io \
  --username "${GHCR_USERNAME}" \
  --password-stdin

export IMAGE_NAME
export IMAGE_TAG
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

# UI health: nginx must answer over HTTP.
docker compose \
  -p "${PROJECT_NAME}" \
  -f "${COMPOSE_FILE}" \
  exec -T trading-bot wget -q -O /tmp/trading-bot-healthcheck.html http://127.0.0.1/
echo "Health check passed for trading-bot (ui) container."

# Worker health: it has no HTTP endpoint, so verify the container is running.
WORKER_RUNNING="$(docker inspect -f '{{.State.Running}}' trading-bot-worker 2>/dev/null || echo false)"
if [ "${WORKER_RUNNING}" != "true" ]; then
  echo "ERROR: trading-bot-worker is not running."
  docker logs --tail 50 trading-bot-worker || true
  exit 1
fi
echo "Health check passed for trading-bot-worker container."
