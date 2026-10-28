from __future__ import annotations

from contextlib import contextmanager
from datetime import datetime
from pathlib import Path
import sqlite3
from typing import Iterator

from .models import Availability, CourseKey, CourseState


SCHEMA = """
CREATE TABLE IF NOT EXISTS course_state (
    storage_key TEXT PRIMARY KEY,
    year INTEGER NOT NULL,
    semester_code TEXT NOT NULL,
    subject_code TEXT NOT NULL,
    section TEXT NOT NULL,
    availability TEXT NOT NULL,
    episode INTEGER NOT NULL,
    alerted_episode INTEGER NOT NULL,
    name TEXT NOT NULL,
    registered INTEGER,
    capacity INTEGER,
    observed_at TEXT NOT NULL
)
"""


class StateRepository:
    def __init__(self, database_path: Path) -> None:
        self._database_path = database_path
        self._database_path.parent.mkdir(parents=True, exist_ok=True)
        with self._connect() as connection:
            connection.execute(SCHEMA)

    @contextmanager
    def _connect(self) -> Iterator[sqlite3.Connection]:
        connection = sqlite3.connect(self._database_path, timeout=10)
        connection.row_factory = sqlite3.Row
        try:
            yield connection
            connection.commit()
        finally:
            connection.close()

    def load(self, key: CourseKey) -> CourseState | None:
        with self._connect() as connection:
            row = connection.execute(
                "SELECT * FROM course_state WHERE storage_key = ?",
                (key.storage_key,),
            ).fetchone()
        if row is None:
            return None
        return CourseState(
            key=key,
            availability=Availability(row["availability"]),
            episode=row["episode"],
            alerted_episode=row["alerted_episode"],
            name=row["name"],
            registered=row["registered"],
            capacity=row["capacity"],
            observed_at=datetime.fromisoformat(row["observed_at"]),
        )

    def save(self, state: CourseState) -> None:
        values = (
            state.key.storage_key,
            state.key.year,
            state.key.semester_code,
            state.key.subject_code,
            state.key.section,
            state.availability.value,
            state.episode,
            state.alerted_episode,
            state.name,
            state.registered,
            state.capacity,
            state.observed_at.isoformat(),
        )
        with self._connect() as connection:
            connection.execute(
                """
                INSERT INTO course_state (
                    storage_key, year, semester_code, subject_code, section,
                    availability, episode, alerted_episode, name, registered,
                    capacity, observed_at
                ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                ON CONFLICT(storage_key) DO UPDATE SET
                    availability = excluded.availability,
                    episode = excluded.episode,
                    alerted_episode = excluded.alerted_episode,
                    name = excluded.name,
                    registered = excluded.registered,
                    capacity = excluded.capacity,
                    observed_at = excluded.observed_at
                """,
                values,
            )

    def list_all(self) -> tuple[CourseState, ...]:
        with self._connect() as connection:
            rows = connection.execute(
                "SELECT * FROM course_state ORDER BY subject_code, section"
            ).fetchall()
        return tuple(
            CourseState(
                key=CourseKey(
                    row["year"],
                    row["semester_code"],
                    row["subject_code"],
                    row["section"],
                ),
                availability=Availability(row["availability"]),
                episode=row["episode"],
                alerted_episode=row["alerted_episode"],
                name=row["name"],
                registered=row["registered"],
                capacity=row["capacity"],
                observed_at=datetime.fromisoformat(row["observed_at"]),
            )
            for row in rows
        )
