#!/usr/bin/env bash
set -euo pipefail

DEPLOY_DIR="/opt/trading-bot"
TRAEFIK_DYNAMIC_DIR="/opt/traefik/dynamic"
PROJECT_NAME="trading-bot"
COMPOSE_FILE="${DEPLOY_DIR}/docker-compose.prod.yml"
TRAEFIK_DYNAMIC_FILE="${TRAEFIK_DYNAMIC_DIR}/trading-bot.yml"

: "${IMAGE_NAME:?IMAGE_NAME is required}"
: "${GHCR_USERNAME:?GHCR_USERNAME is required}"
: "${GHCR_TOKEN:?GHCR_TOKEN is required}"

IMAGE_TAG="${IMAGE_TAG:-latest}"
TRAEFIK_NETWORK="${TRAEFIK_NETWORK:-traefik}"

echo "Deploying ${IMAGE_NAME}:${IMAGE_TAG} to ${DEPLOY_DIR}"

mkdir -p "${DEPLOY_DIR}" "${TRAEFIK_DYNAMIC_DIR}"
cp infra/docker-compose.prod.yml "${COMPOSE_FILE}"
cp infra/traefik/trading-bot.yml "${TRAEFIK_DYNAMIC_FILE}"

echo "${GHCR_TOKEN}" | docker login ghcr.io \
  --username "${GHCR_USERNAME}" \
  --password-stdin

export IMAGE_NAME
export IMAGE_TAG
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

docker compose \
  -p "${PROJECT_NAME}" \
  -f "${COMPOSE_FILE}" \
  exec -T trading-bot wget -q -O /tmp/trading-bot-healthcheck.html http://127.0.0.1/

echo "Health check passed for trading-bot container."
