from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

from snu_slot_watcher.config import CourseTarget
from snu_slot_watcher.models import CourseKey
from snu_slot_watcher.repository import StateRepository
from snu_slot_watcher.service import WatchService


class FakeClient:
    def __init__(self, pages: list[str | tuple[str, ...]]) -> None:
        self.pages = list(pages)

    def search(
        self, year: int, semester_code: str, course_code: str
    ) -> tuple[str, ...]:
        response = self.pages.pop(0)
        return response if isinstance(response, tuple) else (response,)


class CollectingNotifier:
    def __init__(self) -> None:
        self.messages: list[str] = []

    def notify(self, title: str, message: str) -> None:
        self.messages.append(f"{title}: {message}")


class FailingNotifier:
    def notify(self, title: str, message: str) -> None:
        raise RuntimeError("notification unavailable")


class ServiceIntegrationTests(unittest.TestCase):
    def fixture(
        self, registered: int, capacity: int, section: str = "001"
    ) -> str:
        return f"""
        <html><head><title>검색결과 | 강좌검색 | 서울대학교 수강신청 시스템</title></head>
        <body><div class='course-info-list'><div class='course-info-item'>
          <input name='openSchyy' value='2026'><input name='openShtmFg' value='U000200002'>
          <input name='sbjtNm' value='Computer Modeling'><input name='sbjtCd' value='4190.412'>
          <input name='ltNo' value='{section}'><div class='course-name'><strong>Computer Modeling</strong></div>
          <ul class='course-info'><li class='txt'>4190.412({section})</li>
          <li class='txt'>수강신청인원/정원(재학생) <em>{registered}/{capacity} ({capacity})</em></li></ul>
        </div></div></body></html>
        """

    def test_target_on_second_page_sends_alert(self) -> None:
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        notifier = CollectingNotifier()
        service = WatchService(
            client=FakeClient([(
                self.fixture(20, 20, "001"),
                self.fixture(19, 20, "012"),
            )]),
            repository=StateRepository(Path(directory.name) / "state.sqlite3"),
            notifier=notifier,
        )
        target = CourseTarget(
            CourseKey(2026, "U000200002", "4190.412", "012"),
            "Computer Modeling",
        )

        outcome = service.poll_once((target,))[0]

        self.assertEqual(outcome.observation.available_seats, 1)
        self.assertEqual(len(notifier.messages), 1)

    def test_full_then_each_additional_opening_sends_alert(self) -> None:
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        notifier = CollectingNotifier()
        service = WatchService(
            client=FakeClient([self.fixture(20, 20), self.fixture(19, 20), self.fixture(18, 20)]),
            repository=StateRepository(Path(directory.name) / "state.sqlite3"),
            notifier=notifier,
        )
        target = CourseTarget(CourseKey(2026, "U000200002", "4190.412", "001"), "Computer Modeling")

        service.poll_once((target,))
        service.poll_once((target,))
        service.poll_once((target,))

        self.assertEqual(len(notifier.messages), 2)
        self.assertIn("1 apparent seat", notifier.messages[0])
        self.assertIn("2 apparent seats", notifier.messages[1])

    def test_notification_failure_remains_retryable(self) -> None:
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        repository = StateRepository(Path(directory.name) / "state.sqlite3")
        target = CourseTarget(CourseKey(2026, "U000200002", "4190.412", "001"), "Computer Modeling")
        service = WatchService(
            client=FakeClient([self.fixture(19, 20), self.fixture(19, 20)]),
            repository=repository,
            notifier=FailingNotifier(),
        )

        first = service.poll_once((target,))[0]
        second = service.poll_once((target,))[0]

        self.assertIsNotNone(first.error)
        self.assertIsNotNone(second.error)
        self.assertEqual(repository.load(target.key).alerted_episode, 0)

    def test_invalid_page_is_reported_without_changing_state(self) -> None:
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        target = CourseTarget(CourseKey(2026, "U000200002", "4190.412", "001"), "Computer Modeling")
        repository = StateRepository(Path(directory.name) / "state.sqlite3")
        service = WatchService(
            client=FakeClient(["<html><title>Maintenance</title></html>"]),
            repository=repository,
            notifier=CollectingNotifier(),
        )

        outcome = service.poll_once((target,))[0]

        self.assertIsNotNone(outcome.error)
        self.assertIsNone(repository.load(target.key))


if __name__ == "__main__":
    unittest.main()
