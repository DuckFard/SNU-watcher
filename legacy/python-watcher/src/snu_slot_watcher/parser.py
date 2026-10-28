from __future__ import annotations

from datetime import UTC, datetime
import re

from bs4 import BeautifulSoup, Tag

from .models import Availability, CourseKey, CourseObservation


CAPACITY_LABELS = ("수강신청인원/정원(재학생)", "Enrollment/Quota")
CAPACITY_PATTERN = re.compile(r"(\d+)\s*/\s*(\d+)(?:\s*\(\s*\d+\s*\))?")
TOTAL_PATTERN = re.compile(r"총수강인원\s*(\d+)")


class InvalidSearchPage(ValueError):
    """Raised when HTML is not a valid public SNU result page."""


def _input_value(card: Tag, name: str) -> str:
    element = card.select_one(f"input[name='{name}']")
    return str(element.get("value", "")).strip() if element else ""


def _unknown_observation(
    key: CourseKey, name: str, observed_at: datetime
) -> CourseObservation:
    return CourseObservation(
        key=key,
        name=name or key.display_code,
        registered=None,
        capacity=None,
        total_registered=None,
        availability=Availability.UNKNOWN,
        observed_at=observed_at,
    )


def _parse_card(card: Tag, observed_at: datetime) -> CourseObservation | None:
    year_text = _input_value(card, "openSchyy")
    semester_code = _input_value(card, "openShtmFg")
    subject_code = _input_value(card, "sbjtCd")
    section = _input_value(card, "ltNo")
    name = _input_value(card, "sbjtNm")
    title = card.select_one(".course-name strong")
    name = name or (title.get_text(" ", strip=True) if title else "")

    try:
        key = CourseKey(int(year_text), semester_code, subject_code, section)
    except (TypeError, ValueError):
        return None

    card_text = card.get_text(" ", strip=True)
    capacity_container = next(
        (
            element
            for element in card.select("li.txt, li.txt span")
            if any(
                label in element.get_text(" ", strip=True)
                for label in CAPACITY_LABELS
            )
        ),
        None,
    )
    capacity_text = (
        capacity_container.get_text(" ", strip=True) if capacity_container else ""
    )
    capacity_match = CAPACITY_PATTERN.search(capacity_text)
    if capacity_match is None:
        return _unknown_observation(key, name, observed_at)

    registered = int(capacity_match.group(1))
    capacity = int(capacity_match.group(2))
    total_match = TOTAL_PATTERN.search(card_text)
    total_registered = int(total_match.group(1)) if total_match else None
    availability = (
        Availability.AVAILABLE
        if capacity > 0 and registered < capacity
        else Availability.FULL
    )
    return CourseObservation(
        key=key,
        name=name or key.display_code,
        registered=registered,
        capacity=capacity,
        total_registered=total_registered,
        availability=availability,
        observed_at=observed_at,
    )


def parse_search_results(
    html: str, observed_at: datetime | None = None
) -> tuple[CourseObservation, ...]:
    soup = BeautifulSoup(html, "html.parser")
    title = soup.title.get_text(" ", strip=True) if soup.title else ""
    if "검색결과" not in title and "Search Results" not in title:
        raise InvalidSearchPage("Response is not an SNU public course-search result page")

    timestamp = observed_at or datetime.now(UTC)
    parsed = tuple(
        observation
        for card in soup.select(".course-info-item")
        if (observation := _parse_card(card, timestamp)) is not None
    )
    return parsed


def find_course(
    html: str, key: CourseKey, observed_at: datetime | None = None
) -> CourseObservation:
    timestamp = observed_at or datetime.now(UTC)
    observations = parse_search_results(html, timestamp)
    match = next((item for item in observations if item.key == key), None)
    return match or _unknown_observation(key, key.display_code, timestamp)


def find_course_in_pages(
    pages: tuple[str, ...],
    key: CourseKey,
    observed_at: datetime | None = None,
) -> CourseObservation:
    timestamp = observed_at or datetime.now(UTC)
    observations = tuple(
        observation
        for page in pages
        for observation in parse_search_results(page, timestamp)
    )
    match = next((item for item in observations if item.key == key), None)
    return match or _unknown_observation(key, key.display_code, timestamp)
