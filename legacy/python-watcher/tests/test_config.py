from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

from snu_slot_watcher.config import ConfigError, load_config


VALID_CONFIG = """
[watcher]
poll_interval_seconds = 60
request_spacing_seconds = 2
desktop_notifications = true

[[courses]]
year = 2026
semester_code = "U000200002"
course_code = "4190.412"
section = "001"
label = "Computer Modeling"
"""


class ConfigTests(unittest.TestCase):
    def write_config(self, content: str) -> Path:
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        path = Path(directory.name) / "watcher.toml"
        path.write_text(content, encoding="utf-8")
        return path

    def test_loads_valid_config(self) -> None:
        config = load_config(self.write_config(VALID_CONFIG))

        self.assertEqual(config.poll_interval_seconds, 60)
        self.assertEqual(config.courses[0].key.subject_code, "4190.412")
        self.assertTrue(config.desktop_notifications)

    def test_rejects_polling_below_hard_floor(self) -> None:
        path = self.write_config(VALID_CONFIG.replace("60", "10", 1))

        with self.assertRaisesRegex(ConfigError, "at least 30"):
            load_config(path)

    def test_rejects_duplicate_course_keys(self) -> None:
        duplicate = VALID_CONFIG + VALID_CONFIG.split("[[courses]]", 1)[1].join(
            ["\n[[courses]]", ""]
        )

        with self.assertRaisesRegex(ConfigError, "Duplicate"):
            load_config(self.write_config(duplicate))

    def test_rejects_invalid_course_identity(self) -> None:
        invalid = VALID_CONFIG.replace('section = "001"', 'section = "1; DROP"')

        with self.assertRaisesRegex(ConfigError, "section"):
            load_config(self.write_config(invalid))

    def test_rejects_missing_course_list(self) -> None:
        with self.assertRaisesRegex(ConfigError, "At least one"):
            load_config(self.write_config("[watcher]\npoll_interval_seconds = 60"))

    def test_rejects_invalid_webhook_environment_name(self) -> None:
        invalid = VALID_CONFIG.replace(
            "desktop_notifications = true",
            'desktop_notifications = true\nwebhook_url_environment_variable = "not safe"',
        )

        with self.assertRaisesRegex(ConfigError, "webhook"):
            load_config(self.write_config(invalid))

    def test_wraps_invalid_numeric_types_as_config_error(self) -> None:
        invalid = VALID_CONFIG.replace(
            "poll_interval_seconds = 60",
            'poll_interval_seconds = "soon"',
        )

        with self.assertRaisesRegex(ConfigError, "poll_interval_seconds"):
            load_config(self.write_config(invalid))


if __name__ == "__main__":
    unittest.main()
