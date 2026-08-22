#!/bin/bash
# Cheapest resolution first, so partial results are useful early and the long
# minute series runs last against a warm, already-correct database.
set -u
cd "$(dirname "$0")/../.."
LOG=/Volumes/FileDepot/bykovas-pg-data-logs
mkdir -p "$LOG"
for res in 4h 1h 15m 5m 1m; do
  echo "=== $res started $(date -u '+%F %T') ==="
  tools/db/kraken-dump.py \
    --container bykovas-pg --user bykovas --database bykovas-pg \
    --resolution "$res" --from 2025-01-01 \
    --workers 6 --sleep 0.05 \
    --disk-path /Volumes/FileDepot --min-free-gb 40 \
    --symbols-file tools/db/kraken-futures-registry-symbols.txt \
    > "$LOG/dump-$res.log" 2>&1
  echo "=== $res finished $(date -u '+%F %T') rc=$? ==="
done
echo "ALL DONE $(date -u '+%F %T')"
