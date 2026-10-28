from __future__ import annotations

import base64
import io
import os
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import requests

from snu_slot_watcher.notifier import (
    CompositeNotifier,
    ConsoleNotifier,
    NotificationError,
    WebhookNotifier,
    WindowsToastNotifier,
    build_notifier,
)


class FailingNotifier:
    def notify(self, title: str, message: str) -> None:
        raise NotificationError("failed")


class CollectingNotifier:
    def __init__(self) -> None:
        self.messages: tuple[tuple[str, str], ...] = ()

    def notify(self, title: str, message: str) -> None:
        self.messages = (*self.messages, (title, message))


class NotifierTests(unittest.TestCase):
    def test_console_notifier_prints_title_and_message(self) -> None:
        stream = io.StringIO()

        ConsoleNotifier(stream).notify("Open", "One apparent seat")

        self.assertIn("[Open] One apparent seat", stream.getvalue())

    def test_composite_succeeds_if_one_channel_succeeds(self) -> None:
        collector = CollectingNotifier()

        CompositeNotifier((FailingNotifier(), collector)).notify("Title", "Message")

        self.assertEqual(collector.messages, (("Title", "Message"),))

    def test_composite_fails_if_every_channel_fails(self) -> None:
        with self.assertRaisesRegex(NotificationError, "failed"):
            CompositeNotifier((FailingNotifier(),)).notify("Title", "Message")

    def test_webhook_requires_https(self) -> None:
        with self.assertRaisesRegex(ValueError, "HTTPS"):
            WebhookNotifier("http://example.test/hook")

    def test_webhook_rejects_unapproved_host(self) -> None:
        with self.assertRaisesRegex(ValueError, "approved"):
            WebhookNotifier("https://example.test/hook")

    @patch("snu_slot_watcher.notifier.requests.post")
    def test_webhook_posts_provider_compatible_payload(self, post: object) -> None:
        response = SimpleNamespace(raise_for_status=lambda: None)
        post.return_value = response

        WebhookNotifier("https://hooks.slack.com/services/test").notify("Title", "Message")

        payload = post.call_args.kwargs["json"]
        self.assertIn("Title", payload["text"])
        self.assertIn("Message", payload["content"])
        self.assertFalse(post.call_args.kwargs["allow_redirects"])

    @patch("snu_slot_watcher.notifier.requests.post")
    def test_webhook_wraps_request_errors(self, post: object) -> None:
        post.side_effect = requests.Timeout("timeout")

        with self.assertRaisesRegex(NotificationError, "Webhook"):
            WebhookNotifier("https://discord.com/api/webhooks/test").notify("Title", "Message")

    def test_build_notifier_rejects_missing_webhook_environment(self) -> None:
        with patch.dict(os.environ, {}, clear=True):
            with self.assertRaisesRegex(NotificationError, "SNU_TEST_HOOK"):
                build_notifier(
                    desktop_notifications=False,
                    webhook_url_environment_variable="SNU_TEST_HOOK",
                )

    @patch("snu_slot_watcher.notifier.subprocess.run")
    def test_windows_toast_uses_static_encoded_script(self, run: object) -> None:
        run.return_value = SimpleNamespace(returncode=0)
        with patch("snu_slot_watcher.notifier.os.name", "nt"):
            WindowsToastNotifier().notify("Course", "Seat")

        command = run.call_args.args[0]
        self.assertIn("-EncodedCommand", command)
        self.assertNotEqual(command[0].lower(), "powershell.exe")
        self.assertTrue(command[0].lower().endswith("powershell.exe"))
        encoded_script = command[command.index("-EncodedCommand") + 1]
        script = base64.b64decode(encoded_script).decode("utf-16-le")
        self.assertIn("Microsoft.PowerShell_8wekyb3d8bbwe!App", script)
        self.assertIn("SystemSounds]::Exclamation.Play()", script)
        self.assertEqual(run.call_args.kwargs["env"]["SNU_WATCHER_TITLE"], "Course")


if __name__ == "__main__":
    unittest.main()
