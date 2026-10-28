from __future__ import annotations

import unittest
from pathlib import Path

from snu_slot_watcher.models import Availability, CourseKey
from snu_slot_watcher.parser import InvalidSearchPage, find_course, parse_search_results


FIXTURES = Path(__file__).parent / "fixtures"


class ParserTests(unittest.TestCase):
    def fixture(self, name: str) -> str:
        return (FIXTURES / name).read_text(encoding="utf-8")

    def test_parses_full_and_available_sections(self) -> None:
        observations = parse_search_results(self.fixture("search_results.html"))

        self.assertEqual(len(observations), 2)
        self.assertEqual(observations[0].availability, Availability.FULL)
        self.assertEqual(observations[0].registered, 20)
        self.assertEqual(observations[1].availability, Availability.AVAILABLE)
        self.assertEqual(observations[1].available_seats, 5)

    def test_matches_exact_code_and_section(self) -> None:
        key = CourseKey(2026, "U000200002", "208.231A", "001")

        observation = find_course(self.fixture("search_results.html"), key)

        self.assertEqual(observation.key, key)
        self.assertEqual(observation.name, "컴퓨터 지도학")

    def test_missing_target_is_unknown_not_available(self) -> None:
        key = CourseKey(2026, "U000200002", "NOPE.001", "001")

        observation = find_course(self.fixture("search_results.html"), key)

        self.assertEqual(observation.availability, Availability.UNKNOWN)
        self.assertIsNone(observation.capacity)

    def test_malformed_counts_are_unknown(self) -> None:
        observations = parse_search_results(self.fixture("malformed_results.html"))

        self.assertEqual(observations[0].availability, Availability.UNKNOWN)

    def test_rejects_block_or_maintenance_page(self) -> None:
        with self.assertRaises(InvalidSearchPage):
            parse_search_results(self.fixture("block_page.html"))


if __name__ == "__main__":
    unittest.main()

