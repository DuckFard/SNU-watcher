from __future__ import annotations

from dataclasses import dataclass
from itertools import groupby
import random
import time
from typing import Callable, Protocol

from .backoff import calculate_backoff_seconds
from .client import RateLimited
from .config import CourseTarget
from .models import Availability, CourseObservation
from .notifier import Notifier
from .parser import find_course_in_pages
from .repository import StateRepository
from .state import acknowledge_alert, advance_state


REGISTRATION_URL = "https://sugang.snu.ac.kr/sugang/co/co010.action"


class SearchClient(Protocol):
    def search(
        self, year: int, semester_code: str, course_code: str
    ) -> tuple[str, ...]: ...


@dataclass(frozen=True, slots=True)
class PollOutcome:
    target: CourseTarget
    observation: CourseObservation | None
    error: Exception | None


def _search_identity(target: CourseTarget) -> tuple[int, str, str]:
    key = target.key
    return key.year, key.semester_code, key.subject_code


def format_alert(target: CourseTarget, observation: CourseObservation) -> tuple[str, str]:
    seats = observation.available_seats or 0
    seat_word = "seat" if seats == 1 else "seats"
    title = f"SNU: {target.label} may have an opening"
    message = (
        f"{target.key.display_code}: {seats} apparent {seat_word} "
        f"({observation.registered}/{observation.capacity}) at "
        f"{observation.observed_at.astimezone().isoformat(timespec='seconds')}. "
        "Public capacity does not guarantee eligibility. Register manually in your "
        f"existing SNU session: {REGISTRATION_URL}"
    )
    return title, message


class WatchService:
    def __init__(
        self,
        *,
        client: SearchClient,
        repository: StateRepository,
        notifier: Notifier,
        request_spacing_seconds: float = 2.0,
        sleep: Callable[[float], None] = time.sleep,
    ) -> None:
        self._client = client
        self._repository = repository
        self._notifier = notifier
        self._request_spacing_seconds = request_spacing_seconds
        self._sleep = sleep

    def _poll_group(self, targets: tuple[CourseTarget, ...]) -> tuple[PollOutcome, ...]:
        representative = targets[0]
        key = representative.key
        try:
            pages = self._client.search(key.year, key.semester_code, key.subject_code)
            observations = tuple(
                find_course_in_pages(pages, target.key) for target in targets
            )
        except Exception as error:
            return tuple(PollOutcome(target, None, error) for target in targets)

        outcomes: tuple[PollOutcome, ...] = ()
        for target, observation in zip(targets, observations, strict=True):
            previous = self._repository.load(target.key)
            transition = advance_state(previous, observation)
            self._repository.save(transition.state)
            error: Exception | None = None
            if transition.should_alert:
                try:
                    title, message = format_alert(target, observation)
                    self._notifier.notify(title, message)
                    self._repository.save(acknowledge_alert(transition.state))
                except Exception as notification_error:
                    error = notification_error
            outcomes = (*outcomes, PollOutcome(target, observation, error))
        return outcomes

    def poll_once(self, targets: tuple[CourseTarget, ...]) -> tuple[PollOutcome, ...]:
        ordered = tuple(sorted(targets, key=_search_identity))
        grouped = tuple(
            tuple(items)
            for _, items in groupby(ordered, key=_search_identity)
        )
        outcomes: tuple[PollOutcome, ...] = ()
        for index, group in enumerate(grouped):
            if index > 0:
                self._sleep(self._request_spacing_seconds)
            outcomes = (*outcomes, *self._poll_group(group))
        return outcomes

    def watch_forever(
        self, targets: tuple[CourseTarget, ...], poll_interval_seconds: int
    ) -> None:
        consecutive_failures = 0
        while True:
            outcomes = self.poll_once(targets)
            failures = tuple(outcome.error for outcome in outcomes if outcome.error)
            if failures:
                consecutive_failures += 1
                retry_after = max(
                    (
                        error.retry_after_seconds
                        for error in failures
                        if isinstance(error, RateLimited)
                        and error.retry_after_seconds is not None
                    ),
                    default=None,
                )
                delay = calculate_backoff_seconds(
                    consecutive_failures,
                    retry_after_seconds=retry_after,
                    jitter_factor=random.uniform(0.9, 1.1),
                )
            else:
                consecutive_failures = 0
                delay = max(30, round(poll_interval_seconds * random.uniform(0.95, 1.05)))
            self._sleep(delay)
