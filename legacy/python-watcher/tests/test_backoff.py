from __future__ import annotations

import unittest

from snu_slot_watcher.backoff import calculate_backoff_seconds


class BackoffTests(unittest.TestCase):
    def test_backoff_doubles_and_caps(self) -> None:
        self.assertEqual(calculate_backoff_seconds(1, jitter_factor=1.0), 30)
        self.assertEqual(calculate_backoff_seconds(2, jitter_factor=1.0), 60)
        self.assertEqual(calculate_backoff_seconds(20, jitter_factor=1.0), 900)

    def test_retry_after_wins_when_longer(self) -> None:
        self.assertEqual(
            calculate_backoff_seconds(1, retry_after_seconds=120, jitter_factor=1.0),
            120,
        )

    def test_rejects_invalid_failure_count(self) -> None:
        with self.assertRaises(ValueError):
            calculate_backoff_seconds(0)


if __name__ == "__main__":
    unittest.main()

