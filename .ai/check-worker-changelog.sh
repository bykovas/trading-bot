#!/usr/bin/env bash
set -euo pipefail

BASE_SHA="${1:-}"
HEAD_SHA="${2:-HEAD}"

if [[ -z "${BASE_SHA}" || "${BASE_SHA}" =~ ^0+$ ]]; then
  BASE_SHA="$(git rev-parse "${HEAD_SHA}^" 2>/dev/null || true)"
fi

if [[ -z "${BASE_SHA}" ]]; then
  echo "worker changelog check skipped: no base commit available"
  exit 0
fi

changed_files="$(git diff --name-only "${BASE_SHA}" "${HEAD_SHA}")"

worker_changed=false
changelog_changed=false

while IFS= read -r file; do
  [[ -z "${file}" ]] && continue

  if [[ "${file}" == src/TradingBot.SpotWorker/* || "${file}" == src/TradingBot.FuturesWorker/* || "${file}" == src/TradingBot.Core/* ]]; then
    worker_changed=true
  fi

  if [[ "${file}" == ".ai/worker-changelog.md" ]]; then
    changelog_changed=true
  fi
done <<< "${changed_files}"

if [[ "${worker_changed}" == "true" && "${changelog_changed}" != "true" ]]; then
  echo "ERROR: src/TradingBot.SpotWorker, src/TradingBot.FuturesWorker, or src/TradingBot.Core changed, but .ai/worker-changelog.md was not updated."
  echo "Add a latest changelog entry describing the worker logic/tuning change."
  exit 1
fi

echo "worker changelog check passed"
