# Repository instructions for AI coding agents

## Worker logic changes

When changing `src/TradingBot.Worker/**` or worker decision/risk/tuning behavior:

1. Update `.ai/worker-changelog.md` in the same commit.
2. Put the newest entry first under a `## <change-set-id>` heading.
3. Keep the entry specific: changed behavior, tuned thresholds, risk logic, or persistence changes.
4. Do not hardcode worker versions in settings files.
5. Preserve automatic cycle metadata: worker version, commit, build UTC, image tag, strategy version, and change set.

The CI check intentionally fails worker changes that do not update `.ai/worker-changelog.md`.
