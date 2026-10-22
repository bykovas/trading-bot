#!/usr/bin/env python3
"""Render Compose locally; a retired live account must never return via legacy flags."""
import json
import os
from pathlib import Path
import subprocess
import sys

compose = Path(sys.argv[1]) if len(sys.argv) > 1 else Path('infra/docker-compose.prod.yml')
expected = {'futures-worker-live', 'futures-worker-pukis-live'}
for profiles in ('', 'lukas-live'):
    env = dict(os.environ, COMPOSE_PROFILES=profiles,
               TRADINGBOT_LUKAS_FUTURES_LIVE_TRADING_ENABLED='true')
    for component in ('UI', 'API', 'FUTURES_WORKER', 'MARKET_DATA_WORKER', 'OG'):
        env[component + '_IMAGE_NAME'] = 'local/topology-check'
        env[component + '_IMAGE_TAG'] = 'fixture'
    rendered = subprocess.run(['docker', 'compose', '-f', str(compose), 'config',
                               '--no-env-resolution', '--format', 'json'],
                              env=env, text=True, capture_output=True, check=True)
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
print('Production topology OK: BYKO + PUKO, separate Arena reporter; legacy LUKO flags cannot start it.')
