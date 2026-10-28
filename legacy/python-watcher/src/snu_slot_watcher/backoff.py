from __future__ import annotations


def calculate_backoff_seconds(
    consecutive_failures: int,
    *,
    retry_after_seconds: int | None = None,
    jitter_factor: float = 1.0,
) -> int:
    if consecutive_failures < 1:
        raise ValueError("consecutive_failures must be positive")
    if not 0.8 <= jitter_factor <= 1.2:
        raise ValueError("jitter_factor must be between 0.8 and 1.2")

    exponential = min(900, 30 * (2 ** (consecutive_failures - 1)))
    jittered = min(900, max(30, round(exponential * jitter_factor)))
    return max(jittered, retry_after_seconds or 0)

