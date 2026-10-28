from __future__ import annotations

import base64
from dataclasses import dataclass
import os
import subprocess
import sys
from typing import Protocol
from urllib.parse import urlparse

import requests


class NotificationError(RuntimeError):
    """Raised when every configured notification channel fails."""


class Notifier(Protocol):
    def notify(self, title: str, message: str) -> None: ...


APPROVED_WEBHOOKS: tuple[tuple[str, str], ...] = (
    ("hooks.slack.com", "/services/"),
    ("discord.com", "/api/webhooks/"),
    ("discordapp.com", "/api/webhooks/"),
)


@dataclass(frozen=True, slots=True)
class ConsoleNotifier:
    stream: object = sys.stdout

    def notify(self, title: str, message: str) -> None:
        print(f"\a[{title}] {message}", file=self.stream, flush=True)


WINDOWS_TOAST_SCRIPT = r"""
$ErrorActionPreference = 'Stop'
[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] > $null
[Windows.UI.Notifications.ToastNotification, Windows.UI.Notifications, ContentType = WindowsRuntime] > $null
[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] > $null
$xml = New-Object Windows.Data.Xml.Dom.XmlDocument
$xml.LoadXml('<toast><visual><binding template="ToastGeneric"><text></text><text></text></binding></visual></toast>')
$nodes = $xml.GetElementsByTagName('text')
$nodes.Item(0).InnerText = $env:SNU_WATCHER_TITLE
$nodes.Item(1).InnerText = $env:SNU_WATCHER_MESSAGE
$toast = [Windows.UI.Notifications.ToastNotification]::new($xml)
[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('Microsoft.PowerShell_8wekyb3d8bbwe!App').Show($toast)
[System.Media.SystemSounds]::Exclamation.Play()
""".strip()


@dataclass(frozen=True, slots=True)
class WindowsToastNotifier:
    def notify(self, title: str, message: str) -> None:
        if os.name != "nt":
            raise NotificationError("Windows desktop notifications are unavailable")
        system_root = os.environ.get("SystemRoot") or os.environ.get("WINDIR")
        if not system_root:
            raise NotificationError("Windows system directory is unavailable")
        powershell_path = os.path.join(
            system_root,
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe",
        )
        encoded_script = base64.b64encode(
            WINDOWS_TOAST_SCRIPT.encode("utf-16-le")
        ).decode("ascii")
        inherited_names = ("SystemRoot", "WINDIR", "PATH", "PATHEXT", "TEMP", "TMP")
        inherited_environment = {
            name: os.environ[name] for name in inherited_names if name in os.environ
        }
        environment = {
            **inherited_environment,
            "SNU_WATCHER_TITLE": title,
            "SNU_WATCHER_MESSAGE": message,
        }
        result = subprocess.run(
            [
                powershell_path,
                "-NoProfile",
                "-NonInteractive",
                "-EncodedCommand",
                encoded_script,
            ],
            check=False,
            capture_output=True,
            text=True,
            timeout=10,
            env=environment,
        )
        if result.returncode != 0:
            raise NotificationError("Windows toast delivery failed")


@dataclass(frozen=True, slots=True)
class WebhookNotifier:
    url: str
    timeout_seconds: float = 10.0

    def __post_init__(self) -> None:
        parsed = urlparse(self.url)
        if parsed.scheme != "https":
            raise ValueError("Webhook URL must use HTTPS")
        if parsed.username or parsed.password:
            raise ValueError("Webhook URL must not contain credentials")
        host = parsed.hostname or ""
        if not any(
            host == approved_host and parsed.path.startswith(approved_path)
            for approved_host, approved_path in APPROVED_WEBHOOKS
        ):
            raise ValueError("Webhook URL must be an approved Slack or Discord webhook")

    def notify(self, title: str, message: str) -> None:
        try:
            response = requests.post(
                self.url,
                json={"text": f"{title}\n{message}", "content": f"**{title}**\n{message}"},
                timeout=self.timeout_seconds,
                allow_redirects=False,
            )
            response.raise_for_status()
        except requests.RequestException as error:
            raise NotificationError("Webhook delivery failed") from error


@dataclass(frozen=True, slots=True)
class CompositeNotifier:
    notifiers: tuple[Notifier, ...]

    def notify(self, title: str, message: str) -> None:
        successes = 0
        errors: tuple[str, ...] = ()
        for notifier in self.notifiers:
            try:
                notifier.notify(title, message)
                successes += 1
            except Exception as error:  # A failed channel must not block another one.
                errors = (*errors, str(error))
        if successes == 0:
            detail = "; ".join(errors) or "no notification channels configured"
            raise NotificationError(detail)


def build_notifier(
    *, desktop_notifications: bool, webhook_url_environment_variable: str | None
) -> CompositeNotifier:
    channels: tuple[Notifier, ...] = (ConsoleNotifier(),)
    if desktop_notifications and os.name == "nt":
        channels = (*channels, WindowsToastNotifier())
    if webhook_url_environment_variable:
        webhook_url = os.environ.get(webhook_url_environment_variable)
        if not webhook_url:
            raise NotificationError(
                f"Environment variable {webhook_url_environment_variable} is not set"
            )
        channels = (*channels, WebhookNotifier(webhook_url))
    return CompositeNotifier(channels)
