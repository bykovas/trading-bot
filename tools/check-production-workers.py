#!/usr/bin/env python3
"""Render Compose locally; a retired live account must never return via legacy flags."""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile

compose = Path(sys.argv[1]) if len(sys.argv) > 1 else Path('infra/docker-compose.prod.yml')
expected = {'futures-worker-live', 'futures-worker-pukis-live'}
# Service env files do not decide topology and exist only on the production host.
# Use a disposable render fixture without those blocks, including on older Compose
# releases that do not support --no-env-resolution. Never create/read real env files.
lines = []
skip = False
for line in compose.read_text().splitlines():
    indent = len(line) - len(line.lstrip())
    if indent == 4 and line.strip() == 'env_file:':
        skip = True
        continue
    if skip and line.strip() and indent <= 4:
        skip = False
    if not skip:
        lines.append(line)
fixture = tempfile.TemporaryDirectory(prefix='production-topology-')
fixture_path = Path(fixture.name) / 'compose.yml'
fixture_path.write_text('\n'.join(lines) + '\n')
for profiles in ('', 'lukas-live'):
    env = dict(os.environ, COMPOSE_PROFILES=profiles,
               TRADINGBOT_LUKAS_FUTURES_LIVE_TRADING_ENABLED='true')
    for component in ('UI', 'API', 'FUTURES_WORKER', 'MARKET_DATA_WORKER', 'OG'):
        env[component + '_IMAGE_NAME'] = 'local/topology-check'
        env[component + '_IMAGE_TAG'] = 'fixture'
    try:
        rendered = subprocess.run(['docker', 'compose', '-f', str(fixture_path),
                                   'config', '--format', 'json'],
                                  env=env, text=True, capture_output=True, check=True)
    except subprocess.CalledProcessError as error:
        sys.stderr.write(error.stderr)
        raise
    services = json.loads(rendered.stdout)['services']
    if any('lukas' in name.lower() or 'luko' in name.lower() for name in services):
        raise SystemExit('Retired LUKO service is still defined in production Compose.')
    active = {name for name, service in services.items()
              if name.startswith('futures-worker-') and
              (not service.get('profiles') or profiles in service['profiles'])}
    if active != expected:
        raise SystemExit(f'Unexpected production trading workers: {sorted(active)}')
    if services['arena-reporter']['entrypoint'] != ['dotnet', 'TradingBot.ArenaReporter.dll']:
        raise SystemExit('Arena reporting must remain a separate executable, not a trading worker.')
fixture.cleanup()
print('Production topology OK: BYKO + PUKO, separate Arena reporter; legacy LUKO flags cannot start it.')
