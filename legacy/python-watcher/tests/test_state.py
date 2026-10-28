from __future__ import annotations

import unittest
from datetime import UTC, datetime

from snu_slot_watcher.models import Availability, CourseKey, CourseObservation
from snu_slot_watcher.state import acknowledge_alert, advance_state


KEY = CourseKey(2026, "U000200002", "4190.412", "001")


def observation(status: Availability, registered: int | None = None, capacity: int | None = None) -> CourseObservation:
    return CourseObservation(
        key=KEY,
        name="Computer Modeling",
        registered=registered,
        capacity=capacity,
        total_registered=registered,
        availability=status,
        observed_at=datetime(2026, 8, 28, tzinfo=UTC),
    )


class StateTransitionTests(unittest.TestCase):
    def test_initial_full_does_not_alert(self) -> None:
        result = advance_state(None, observation(Availability.FULL, 20, 20))

        self.assertFalse(result.should_alert)
        self.assertEqual(result.state.episode, 0)

    def test_initial_available_alerts_once(self) -> None:
        first = advance_state(None, observation(Availability.AVAILABLE, 19, 20))
        acknowledged = acknowledge_alert(first.state)
        repeated = advance_state(acknowledged, observation(Availability.AVAILABLE, 19, 20))

        self.assertTrue(first.should_alert)
        self.assertFalse(repeated.should_alert)
        self.assertEqual(repeated.state.episode, 1)

    def test_more_free_seats_creates_another_alert(self) -> None:
        first = advance_state(None, observation(Availability.AVAILABLE, 19, 20))
        acknowledged = acknowledge_alert(first.state)

        more_open = advance_state(
            acknowledged, observation(Availability.AVAILABLE, 16, 20)
        )

        self.assertTrue(more_open.should_alert)
        self.assertEqual(more_open.state.episode, 2)

    def test_fewer_free_seats_does_not_alert(self) -> None:
        first = advance_state(None, observation(Availability.AVAILABLE, 16, 20))
        acknowledged = acknowledge_alert(first.state)

        fewer_open = advance_state(
            acknowledged, observation(Availability.AVAILABLE, 18, 20)
        )

        self.assertFalse(fewer_open.should_alert)
        self.assertEqual(fewer_open.state.episode, 1)

    def test_failed_alert_retries_even_if_free_seats_decrease(self) -> None:
        unacknowledged = advance_state(
            None, observation(Availability.AVAILABLE, 18, 20)
        )

        fewer_open = advance_state(
            unacknowledged.state, observation(Availability.AVAILABLE, 19, 20)
        )

        self.assertTrue(fewer_open.should_alert)
        self.assertEqual(fewer_open.state.episode, 1)

    def test_full_open_full_open_creates_two_episodes(self) -> None:
        full = advance_state(None, observation(Availability.FULL, 20, 20)).state
        open_one = advance_state(full, observation(Availability.AVAILABLE, 19, 20))
        full_again = advance_state(acknowledge_alert(open_one.state), observation(Availability.FULL, 20, 20)).state
        open_two = advance_state(full_again, observation(Availability.AVAILABLE, 19, 20))

        self.assertTrue(open_one.should_alert)
        self.assertTrue(open_two.should_alert)
        self.assertEqual(open_two.state.episode, 2)

    def test_unknown_preserves_last_valid_state(self) -> None:
        full = advance_state(None, observation(Availability.FULL, 20, 20)).state

        unknown = advance_state(full, observation(Availability.UNKNOWN))

        self.assertEqual(unknown.state, full)
        self.assertFalse(unknown.should_alert)


if __name__ == "__main__":
    unittest.main()
