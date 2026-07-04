# Worker changelog

Latest entry must be first. The first `## <id>` heading is used as `worker.changeSet`.

## 2026-07-04-cycle-worker-metadata-columns

- Added queryable worker metadata columns to `dry_run_cycles`.
- Added cycle API filters for worker commit/version, strategy version, change set, and latest strategy version.
- Expected effect: cycle analysis can isolate records produced by a specific worker logic version without parsing raw JSON.

## 2026-07-04-worker-version-metadata

- Added automatic worker build metadata for persisted cycle records.
- Added CI enforcement that worker logic changes must update this changelog.
- No trading strategy behavior change.
