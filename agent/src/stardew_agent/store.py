"""In-memory latest-observation store with session/sequence ordering and freshness."""

import threading
import time
from collections import deque
from dataclasses import dataclass
from datetime import datetime, timezone
from enum import StrEnum
from typing import Callable

from .schema import ObservationEnvelope


class Freshness(StrEnum):
    NO_DATA = "no_data"  # nothing received since the controller started
    NOT_READY = "not_ready"  # bridge reported world_ready=false (title screen / save closed)
    LIVE = "live"  # world loaded and the last observation is recent
    STALE = "stale"  # world was loaded but the bridge has gone quiet (game closed, crashed, network down)


class Rejection(StrEnum):
    DUPLICATE = "duplicate_sequence"
    OUT_OF_ORDER = "out_of_order_sequence"
    RETIRED_SESSION = "retired_session"


@dataclass(frozen=True)
class AcceptResult:
    accepted: bool
    rejection: Rejection | None = None
    new_session: bool = False
    latest_sequence: int | None = None


@dataclass(frozen=True)
class StoredObservation:
    envelope: ObservationEnvelope
    received_at: datetime
    received_monotonic: float


@dataclass(frozen=True)
class LatestView:
    status: Freshness
    observation: StoredObservation | None
    age_seconds: float | None
    stale_after_seconds: float

    @property
    def is_live(self) -> bool:
        return self.status is Freshness.LIVE


class ObservationStore:
    """
    Keeps only the newest observation of the current session.

    - Same session: sequence must increase; duplicates and older sequences are rejected.
    - Different session: the new session replaces the current one (game restarted or save reloaded),
      and the old session ID is retired so a late packet from it can't take over again.
    - Freshness is computed on read from the monotonic receive time, never trusted from the payload.
    """

    def __init__(self, stale_after_seconds: float = 5.0, clock: Callable[[], float] = time.monotonic, retired_limit: int = 256):
        self.stale_after_seconds = stale_after_seconds
        self._clock = clock
        self._lock = threading.Lock()
        self._current: StoredObservation | None = None
        self._retired_order: deque[str] = deque()
        self._retired: set[str] = set()
        self._retired_limit = retired_limit

    def accept(self, envelope: ObservationEnvelope) -> AcceptResult:
        with self._lock:
            current = self._current
            new_session = current is None or current.envelope.session_id != envelope.session_id

            if new_session:
                if envelope.session_id in self._retired:
                    return AcceptResult(False, Rejection.RETIRED_SESSION)
                if current is not None:
                    self._retire(current.envelope.session_id)
            else:
                last = current.envelope.sequence
                if envelope.sequence == last:
                    return AcceptResult(False, Rejection.DUPLICATE, latest_sequence=last)
                if envelope.sequence < last:
                    return AcceptResult(False, Rejection.OUT_OF_ORDER, latest_sequence=last)

            self._current = StoredObservation(
                envelope=envelope,
                received_at=datetime.now(timezone.utc),
                received_monotonic=self._clock(),
            )
            return AcceptResult(True, new_session=new_session, latest_sequence=envelope.sequence)

    def latest(self) -> LatestView:
        with self._lock:
            current = self._current
        if current is None:
            return LatestView(Freshness.NO_DATA, None, None, self.stale_after_seconds)

        age = max(0.0, self._clock() - current.received_monotonic)
        if not current.envelope.snapshot.world_ready:
            status = Freshness.NOT_READY
        elif age > self.stale_after_seconds:
            status = Freshness.STALE
        else:
            status = Freshness.LIVE
        return LatestView(status, current, age, self.stale_after_seconds)

    def _retire(self, session_id: str) -> None:
        if session_id in self._retired:
            return
        self._retired.add(session_id)
        self._retired_order.append(session_id)
        while len(self._retired_order) > self._retired_limit:
            self._retired.discard(self._retired_order.popleft())
