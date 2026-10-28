from __future__ import annotations

from argparse import Namespace
from pathlib import Path
import unittest
from unittest.mock import patch

from snu_slot_watcher.cli import _run
from snu_slot_watcher.config import CourseTarget, WatcherConfig
from snu_slot_watcher.models import CourseKey
from snu_slot_watcher.service import PollOutcome


class FakeService:
    def __init__(self, outcomes: tuple[PollOutcome, ...]) -> None:
        self.outcomes = outcomes

    def poll_once(self, targets: tuple[CourseTarget, ...]) -> tuple[PollOutcome, ...]:
        return self.outcomes


class CliTests(unittest.TestCase):
    def test_check_once_returns_nonzero_on_fetch_error(self) -> None:
        target = CourseTarget(
            CourseKey(2026, "U000200002", "4190.412", "001"),
            "Computer Modeling",
        )
        config = WatcherConfig(
            courses=(target,),
            poll_interval_seconds=60,
            request_spacing_seconds=2,
            desktop_notifications=False,
            webhook_url_environment_variable=None,
            state_database=Path("unused.sqlite3"),
        )
        outcomes = (PollOutcome(target, None, RuntimeError("blocked")),)
        arguments = Namespace(config=Path("watcher.toml"), command="check-once")

        with patch("snu_slot_watcher.cli.load_config", return_value=config):
            with patch("snu_slot_watcher.cli._build_service", return_value=(FakeService(outcomes), object())):
                exit_code = _run(arguments)

        self.assertEqual(exit_code, 1)


if __name__ == "__main__":
    unittest.main()

