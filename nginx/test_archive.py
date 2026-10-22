import importlib.util
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('archive', ROOT / 'nginx/build-archive.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class ArchiveTests(unittest.TestCase):
    def test_archive_keeps_journal_and_only_retired_account(self):
        html = module.archive((ROOT / 'public/zurnalas.html').read_text())
        for removed in ['BYKO', 'PUKO', 'bykovas', 'DENISAS', 'bcPagesOpen', '/aktas/', '/en/', 'localStorage', 'searchParams', 'setInterval(refresh', 'futures-live', '/og/']:
            self.assertFalse(removed.lower() in html.lower(), removed)
        for kept in ['bcChartCanvas', 'bcTradeList', 'bcPosList', 'futures-lukas-live', 'Botas sustabdytas', '/api/dashboard?botInstanceId=', 'function renderPositions', 'function renderTrades']:
            self.assertIn(kept, html)

    def test_structure_changes_fail_closed(self):
        with self.assertRaises(ValueError):
            module.archive('<html>unexpected journal</html>')


if __name__ == '__main__':
    unittest.main()
