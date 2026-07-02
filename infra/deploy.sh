#!/usr/bin/env bash
set -euo pipefail

APP_DIR="/opt/static-html-site"
PROJECT_NAME="static-html-site"
COMPOSE_FILE="${APP_DIR}/docker-compose.prod.yml"
HEALTHCHECK_URL="http://localhost:8090"

: "${IMAGE_NAME:?IMAGE_NAME is required}"
: "${GHCR_USERNAME:?GHCR_USERNAME is required}"
: "${GHCR_TOKEN:?GHCR_TOKEN is required}"

IMAGE_TAG="${IMAGE_TAG:-latest}"

echo "Deploying ${IMAGE_NAME}:${IMAGE_TAG} to ${APP_DIR}"

mkdir -p "${APP_DIR}"
cp infra/docker-compose.prod.yml "${COMPOSE_FILE}"

echo "${GHCR_TOKEN}" | docker login ghcr.io \
  --username "${GHCR_USERNAME}" \
  --password-stdin

export IMAGE_NAME
export IMAGE_TAG

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

curl --fail --show-error --silent --max-time 15 "${HEALTHCHECK_URL}" >/tmp/static-html-site-healthcheck.html

echo "Health check passed: ${HEALTHCHECK_URL}"
