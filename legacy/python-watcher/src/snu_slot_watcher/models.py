from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime
from enum import StrEnum
import re


COURSE_CODE_PATTERN = re.compile(r"^[A-Za-z0-9.]+$")
SEMESTER_CODE_PATTERN = re.compile(r"^U000200\d{3}$")
SECTION_PATTERN = re.compile(r"^\d{3}$")


class Availability(StrEnum):
    UNKNOWN = "unknown"
    FULL = "full"
    AVAILABLE = "available"


@dataclass(frozen=True, slots=True)
class CourseKey:
    year: int
    semester_code: str
    subject_code: str
    section: str

    def __post_init__(self) -> None:
        if not 2000 <= self.year <= 2200:
            raise ValueError("year must be between 2000 and 2200")
        if not SEMESTER_CODE_PATTERN.fullmatch(self.semester_code):
            raise ValueError("semester_code must look like U000200002")
        if not COURSE_CODE_PATTERN.fullmatch(self.subject_code):
            raise ValueError("course_code contains unsupported characters")
        if not SECTION_PATTERN.fullmatch(self.section):
            raise ValueError("section must be exactly three digits")

    @property
    def storage_key(self) -> str:
        return ":".join(
            (str(self.year), self.semester_code, self.subject_code, self.section)
        )

    @property
    def display_code(self) -> str:
        return f"{self.subject_code}({self.section})"


@dataclass(frozen=True, slots=True)
class CourseObservation:
    key: CourseKey
    name: str
    registered: int | None
    capacity: int | None
    total_registered: int | None
    availability: Availability
    observed_at: datetime

    @property
    def available_seats(self) -> int | None:
        if self.registered is None or self.capacity is None:
            return None
        return max(0, self.capacity - self.registered)


@dataclass(frozen=True, slots=True)
class CourseState:
    key: CourseKey
    availability: Availability
    episode: int
    alerted_episode: int
    name: str
    registered: int | None
    capacity: int | None
    observed_at: datetime

