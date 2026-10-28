from __future__ import annotations

from dataclasses import dataclass, replace

from .models import Availability, CourseObservation, CourseState


@dataclass(frozen=True, slots=True)
class TransitionResult:
    state: CourseState
    should_alert: bool


def _state_from_observation(
    observation: CourseObservation, episode: int, alerted_episode: int
) -> CourseState:
    return CourseState(
        key=observation.key,
        availability=observation.availability,
        episode=episode,
        alerted_episode=alerted_episode,
        name=observation.name,
        registered=observation.registered,
        capacity=observation.capacity,
        observed_at=observation.observed_at,
    )


def _free_seats(registered: int | None, capacity: int | None) -> int | None:
    if registered is None or capacity is None:
        return None
    return max(0, capacity - registered)


def advance_state(
    previous: CourseState | None, observation: CourseObservation
) -> TransitionResult:
    if observation.availability is Availability.UNKNOWN:
        if previous is not None:
            return TransitionResult(previous, False)
        return TransitionResult(_state_from_observation(observation, 0, 0), False)

    if previous is None:
        episode = 1 if observation.availability is Availability.AVAILABLE else 0
        state = _state_from_observation(observation, episode, 0)
        return TransitionResult(state, episode > 0)

    became_available = (
        previous.availability is not Availability.AVAILABLE
        and observation.availability is Availability.AVAILABLE
    )
    previous_free_seats = _free_seats(previous.registered, previous.capacity)
    current_free_seats = observation.available_seats
    more_seats_opened = (
        observation.availability is Availability.AVAILABLE
        and previous_free_seats is not None
        and current_free_seats is not None
        and current_free_seats > previous_free_seats
    )
    new_opening = became_available or more_seats_opened
    episode = previous.episode + 1 if new_opening else previous.episode
    state = _state_from_observation(
        observation,
        episode=episode,
        alerted_episode=previous.alerted_episode,
    )
    should_alert = (
        observation.availability is Availability.AVAILABLE
        and state.alerted_episode < state.episode
    )
    return TransitionResult(state, should_alert)


def acknowledge_alert(state: CourseState) -> CourseState:
    return replace(state, alerted_episode=state.episode)
