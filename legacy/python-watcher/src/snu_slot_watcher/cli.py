from __future__ import annotations

import argparse
from pathlib import Path
import sys

from .client import PublicSearchClient
from .config import ConfigError, WatcherConfig, load_config
from .models import Availability
from .notifier import NotificationError, build_notifier
from .repository import StateRepository
from .service import PollOutcome, WatchService


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="snu-slot-watcher",
        description="Read-only notifications for apparent SNU course openings",
    )
    parser.add_argument(
        "--config",
        type=Path,
        default=Path("watcher.toml"),
        help="Path to watcher TOML configuration",
    )
    parser.add_argument(
        "command",
        choices=("validate", "check-once", "watch", "status"),
    )
    return parser


def _build_service(config: WatcherConfig) -> tuple[WatchService, StateRepository]:
    repository = StateRepository(config.state_database)
    notifier = build_notifier(
        desktop_notifications=config.desktop_notifications,
        webhook_url_environment_variable=config.webhook_url_environment_variable,
    )
    service = WatchService(
        client=PublicSearchClient(
            page_spacing_seconds=config.request_spacing_seconds
        ),
        repository=repository,
        notifier=notifier,
        request_spacing_seconds=config.request_spacing_seconds,
    )
    return service, repository


def _print_outcome(outcome: PollOutcome) -> None:
    code = outcome.target.key.display_code
    if outcome.error:
        print(f"{code}: ERROR - {outcome.error}")
        return
    if outcome.observation is None:
        print(f"{code}: UNKNOWN")
        return
    observation = outcome.observation
    if observation.availability is Availability.UNKNOWN:
        print(f"{code}: UNKNOWN - exact course row was not safely parsed")
        return
    print(
        f"{code}: {observation.availability.value.upper()} "
        f"({observation.registered}/{observation.capacity})"
    )


def _run(arguments: argparse.Namespace) -> int:
    config = load_config(arguments.config.resolve())
    if arguments.command == "validate":
        print(
            f"Configuration is valid: {len(config.courses)} course(s), "
            f"{config.poll_interval_seconds}s interval"
        )
        return 0

    service, repository = _build_service(config)
    if arguments.command == "status":
        states = repository.list_all()
        if not states:
            print("No observations have been stored yet.")
            return 0
        for state in states:
            print(
                f"{state.key.display_code}: {state.availability.value.upper()} "
                f"({state.registered}/{state.capacity}), last checked {state.observed_at.isoformat()}"
            )
        return 0

    if arguments.command == "check-once":
        outcomes = service.poll_once(config.courses)
        for outcome in outcomes:
            _print_outcome(outcome)
        failed = any(
            outcome.error is not None
            or outcome.observation is None
            or outcome.observation.availability is Availability.UNKNOWN
            for outcome in outcomes
        )
        return 1 if failed else 0

    print(
        f"Watching {len(config.courses)} course(s) every ~{config.poll_interval_seconds}s. "
        "Press Ctrl+C to stop."
    )
    try:
        service.watch_forever(config.courses, config.poll_interval_seconds)
    except KeyboardInterrupt:
        print("\nWatcher stopped.")
    return 0


def main(argv: list[str] | None = None) -> int:
    arguments = _parser().parse_args(argv)
    try:
        return _run(arguments)
    except (ConfigError, NotificationError) as error:
        print(f"Error: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
