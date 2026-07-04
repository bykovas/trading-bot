# Worker versioning policy

Every persisted dry-run cycle must identify the worker build and strategy/tuning set that produced it.

Required cycle metadata:
- `worker.version`: human-readable worker version. In CI this is the commit SHA.
- `worker.commit`: exact git commit SHA.
- `worker.buildUtc`: UTC timestamp produced by CI.
- `worker.imageTag`: deployed worker container image tag.
- `worker.strategyVersion`: deterministic hash of worker source/config inputs used by CI.
- `worker.changeSet`: latest entry id from `.ai/worker-changelog.md`.

Rules for AI/code agents:
- Do not hardcode worker versions in `appsettings.json`.
- Do not ask the operator to manually set worker versions.
- If `src/TradingBot.Worker/**` changes, update `.ai/worker-changelog.md` in the same commit.
- Keep changelog entries short and analysis-focused: what changed, what was tuned, and why it matters for cycle analysis.
- If changelog is missing for a worker change, stop and add it before committing.

Current implementation:
- CI computes `strategyVersion` from tracked files under `src/TradingBot.Worker`.
- CI reads `changeSet` from the latest `## <id>` heading in `.ai/worker-changelog.md`.
- Docker build args become runtime environment variables.
- `DecisionWorker` writes `Worker` metadata into each `DryRunCycleRecord`.
