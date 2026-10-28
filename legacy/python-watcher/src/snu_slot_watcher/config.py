from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
import re
import tomllib
from typing import Any

from .models import CourseKey


MIN_POLL_INTERVAL_SECONDS = 30
MAX_COURSES = 20
ENVIRONMENT_NAME_PATTERN = re.compile(r"^[A-Z][A-Z0-9_]*$")


class ConfigError(ValueError):
    """Raised when watcher configuration is invalid."""


@dataclass(frozen=True, slots=True)
class CourseTarget:
    key: CourseKey
    label: str


@dataclass(frozen=True, slots=True)
class WatcherConfig:
    courses: tuple[CourseTarget, ...]
    poll_interval_seconds: int
    request_spacing_seconds: float
    desktop_notifications: bool
    webhook_url_environment_variable: str | None
    state_database: Path


def _require_mapping(value: Any, label: str) -> dict[str, Any]:
    if not isinstance(value, dict):
        raise ConfigError(f"{label} must be a TOML table")
    return value


def _parse_target(raw: Any) -> CourseTarget:
    item = _require_mapping(raw, "course")
    try:
        key = CourseKey(
            year=int(item["year"]),
            semester_code=str(item["semester_code"]).strip(),
            subject_code=str(item["course_code"]).strip(),
            section=str(item["section"]).strip(),
        )
    except KeyError as error:
        raise ConfigError(f"Course is missing {error.args[0]}") from error
    except (TypeError, ValueError) as error:
        raise ConfigError(str(error)) from error

    label = str(item.get("label", key.display_code)).strip()
    if not label or len(label) > 120:
        raise ConfigError("course label must contain 1 to 120 characters")
    return CourseTarget(key=key, label=label)


def load_config(path: Path) -> WatcherConfig:
    try:
        data = tomllib.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError as error:
        raise ConfigError(f"Configuration file not found: {path}") from error
    except tomllib.TOMLDecodeError as error:
        raise ConfigError(f"Invalid TOML: {error}") from error

    watcher = _require_mapping(data.get("watcher", {}), "watcher")
    try:
        poll_interval = int(watcher.get("poll_interval_seconds", 60))
    except (TypeError, ValueError) as error:
        raise ConfigError("poll_interval_seconds must be an integer") from error
    if poll_interval < MIN_POLL_INTERVAL_SECONDS:
        raise ConfigError(
            f"poll_interval_seconds must be at least {MIN_POLL_INTERVAL_SECONDS}"
        )
    if poll_interval > 3600:
        raise ConfigError("poll_interval_seconds cannot exceed 3600")

    try:
        request_spacing = float(watcher.get("request_spacing_seconds", 2.0))
    except (TypeError, ValueError) as error:
        raise ConfigError("request_spacing_seconds must be a number") from error
    if request_spacing < 1.0:
        raise ConfigError("request_spacing_seconds must be at least 1")

    raw_courses = data.get("courses")
    if not isinstance(raw_courses, list) or not raw_courses:
        raise ConfigError("At least one [[courses]] entry is required")
    if len(raw_courses) > MAX_COURSES:
        raise ConfigError(f"At most {MAX_COURSES} courses may be watched")

    courses = tuple(_parse_target(raw) for raw in raw_courses)
    course_keys = tuple(target.key.storage_key for target in courses)
    if len(set(course_keys)) != len(course_keys):
        raise ConfigError("Duplicate course identity in configuration")

    webhook_environment = watcher.get("webhook_url_environment_variable")
    if webhook_environment is not None:
        webhook_environment = str(webhook_environment).strip()
        if not ENVIRONMENT_NAME_PATTERN.fullmatch(webhook_environment):
            raise ConfigError("webhook_url_environment_variable is invalid")

    state_name = str(watcher.get("state_database", "watcher-state.sqlite3"))
    state_path = Path(state_name)
    if not state_path.is_absolute():
        state_path = path.parent / state_path

    return WatcherConfig(
        courses=courses,
        poll_interval_seconds=poll_interval,
        request_spacing_seconds=request_spacing,
        desktop_notifications=bool(watcher.get("desktop_notifications", True)),
        webhook_url_environment_variable=webhook_environment,
        state_database=state_path,
    )
