from __future__ import annotations

import tempfile
import unittest
from datetime import UTC, datetime
from pathlib import Path

from snu_slot_watcher.models import Availability, CourseKey, CourseState
from snu_slot_watcher.repository import StateRepository


class RepositoryTests(unittest.TestCase):
    def test_round_trips_state(self) -> None:
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        repository = StateRepository(Path(directory.name) / "state.sqlite3")
        key = CourseKey(2026, "U000200002", "4190.412", "001")
        state = CourseState(
            key=key,
            availability=Availability.AVAILABLE,
            episode=2,
            alerted_episode=1,
            name="Computer Modeling",
            registered=19,
            capacity=20,
            observed_at=datetime(2026, 8, 28, tzinfo=UTC),
        )

        repository.save(state)

        self.assertEqual(repository.load(key), state)


if __name__ == "__main__":
    unittest.main()

