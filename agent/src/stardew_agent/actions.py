"""Action queue: submission, bridge claim leases, status reports and stop requests. In memory only.

State machine (one record per action_id):

    QUEUED --claim--> CLAIMED --RUNNING--> RUNNING --> SUCCEEDED | FAILED | CANCELLED | TIMED_OUT
       |                 |                    |
       |                 +--> SUCCEEDED | FAILED | CANCELLED | REJECTED | TIMED_OUT   (bridge report)
       |                 +--> TIMED_OUT  (lease expired: bridge silent)       <-- also from RUNNING
       |                 +--> CANCELLED  (bridge session changed)             <-- also from RUNNING
       +--> CANCELLED (stop request, session changed, automation disabled) | EXPIRED (ttl passed unclaimed)

    stop actions: QUEUED --bridge acknowledges stop_seq--> SUCCEEDED, or EXPIRED.

Terminal states never change. Only one action is CLAIMED/RUNNING at a time.
"""

import json
import threading
import time
import uuid
from collections import OrderedDict, deque
from dataclasses import dataclass, field
from datetime import datetime, timedelta, timezone
from enum import StrEnum
from typing import Annotated, Any, Callable, Literal, Self

from pydantic import Field, TypeAdapter, model_validator

from .schema import StrictModel
from .store import Freshness, ObservationStore

ID_PATTERN = r"^[A-Za-z0-9_-]{8,64}$"
Direction = Literal["up", "down", "left", "right"]


class ActionType(StrEnum):
    MOVE_DIRECTION = "move_direction"
    FACE_DIRECTION = "face_direction"
    SELECT_SLOT = "select_slot"
    STOP = "stop"


class ActionState(StrEnum):
    QUEUED = "QUEUED"
    CLAIMED = "CLAIMED"
    RUNNING = "RUNNING"
    SUCCEEDED = "SUCCEEDED"
    FAILED = "FAILED"
    CANCELLED = "CANCELLED"
    REJECTED = "REJECTED"
    TIMED_OUT = "TIMED_OUT"
    EXPIRED = "EXPIRED"


TERMINAL = {
    ActionState.SUCCEEDED,
    ActionState.FAILED,
    ActionState.CANCELLED,
    ActionState.REJECTED,
    ActionState.TIMED_OUT,
    ActionState.EXPIRED,
}
ACTIVE = {ActionState.CLAIMED, ActionState.RUNNING}
FAILURE_STATES = {ActionState.FAILED, ActionState.CANCELLED, ActionState.REJECTED, ActionState.TIMED_OUT}


# ---- submission schema -------------------------------------------------------------------------


class MoveDirectionParams(StrictModel):
    direction: Direction
    # Ticks to hold the direction key (60 ticks = 1 s). Capped at 60 in this version.
    duration_ticks: int = Field(ge=1, le=60)


class FaceDirectionParams(StrictModel):
    direction: Direction


class SelectSlotParams(StrictModel):
    # Hotbar slot 0-11.
    slot: int = Field(ge=0, le=11)


class StopParams(StrictModel):
    reason: str | None = Field(default=None, max_length=200)


class _SubmissionBase(StrictModel):
    # Optional client-chosen ID for idempotent retries; generated when omitted.
    action_id: str | None = Field(default=None, pattern=ID_PATTERN)
    session_id: str = Field(pattern=ID_PATTERN)
    # How long the action may wait unclaimed before it expires.
    ttl_seconds: float = Field(default=10.0, ge=1.0, le=60.0)


class MoveDirectionAction(_SubmissionBase):
    type: Literal["move_direction"]
    params: MoveDirectionParams


class FaceDirectionAction(_SubmissionBase):
    type: Literal["face_direction"]
    params: FaceDirectionParams


class SelectSlotAction(_SubmissionBase):
    type: Literal["select_slot"]
    params: SelectSlotParams


class StopAction(_SubmissionBase):
    type: Literal["stop"]
    params: StopParams = StopParams()


ActionSubmission = Annotated[
    MoveDirectionAction | FaceDirectionAction | SelectSlotAction | StopAction,
    Field(discriminator="type"),
]
SubmissionAdapter: TypeAdapter[ActionSubmission] = TypeAdapter(ActionSubmission)


# ---- bridge-facing schema ----------------------------------------------------------------------


class ClaimRequest(StrictModel):
    """Bridge poll. Doubles as the bridge heartbeat."""

    # Bridge's current save session; null on the title screen.
    session_id: str | None = Field(pattern=ID_PATTERN)
    automation_enabled: bool
    # True when the bridge can start an action now (idle, player free, window focused).
    ready: bool
    running_action_id: str | None
    # Highest stop_seq the bridge has already applied, for the controller_instance_id it names.
    stop_seq_seen: int = Field(ge=0)
    controller_instance_id: str | None
    block_reason: str | None
    bridge_version: str


class Position(StrictModel):
    location_name: str
    tile_x: int
    tile_y: int
    # Pixel position (Farmer.Position).
    x: float
    y: float
    facing_direction: int = Field(ge=0, le=3)


class ActionResult(StrictModel):
    action_id: str
    session_id: str
    type: str
    started_at_utc: datetime | None
    finished_at_utc: datetime | None
    ticks_executed: int = Field(ge=0)
    start_position: Position | None
    end_position: Position | None
    distance_pixels: float | None
    # True when the player stopped moving while the key was still held (wall, object, NPC).
    blocked: bool
    failure_reason: str | None


ReportedState = Literal["RUNNING", "SUCCEEDED", "FAILED", "CANCELLED", "REJECTED", "TIMED_OUT"]


class StatusReport(StrictModel):
    session_id: str
    lease_id: str
    state: ReportedState
    failure_reason: str | None
    result: ActionResult | None

    @model_validator(mode="after")
    def _check(self) -> Self:
        state = ActionState(self.state)
        if state in TERMINAL and self.result is None:
            raise ValueError(f"{self.state} report needs a result")
        if state in FAILURE_STATES and not self.failure_reason:
            raise ValueError(f"{self.state} report needs a failure_reason")
        return self


class StopRequest(StrictModel):
    reason: str | None = Field(default=None, max_length=200)
    source: str | None = Field(default=None, max_length=64)


# ---- records -----------------------------------------------------------------------------------


@dataclass
class ActionRecord:
    action_id: str
    session_id: str
    type: ActionType
    params: dict[str, Any]
    fingerprint: str
    state: ActionState
    created_at: datetime
    expires_mono: float
    expires_at: datetime
    claimed_at: datetime | None = None
    started_at: datetime | None = None
    finished_at: datetime | None = None
    lease_id: str | None = None
    lease_expires_mono: float | None = None
    lease_expires_at: datetime | None = None
    failure_reason: str | None = None
    result: dict[str, Any] | None = None
    stop_requested: bool = False
    stop_seq: int | None = None
    history: list[dict[str, Any]] = field(default_factory=list)

    def public(self) -> dict[str, Any]:
        """Client view. The lease_id is the bridge's write token and is not exposed."""

        def iso(value: datetime | None) -> str | None:
            return value.isoformat() if value else None

        return {
            "action_id": self.action_id,
            "session_id": self.session_id,
            "type": self.type.value,
            "params": self.params,
            "state": self.state.value,
            "terminal": self.state in TERMINAL,
            "created_at_utc": iso(self.created_at),
            "expires_at_utc": iso(self.expires_at),
            "claimed_at_utc": iso(self.claimed_at),
            "started_at_utc": iso(self.started_at),
            "finished_at_utc": iso(self.finished_at),
            "lease_expires_at_utc": iso(self.lease_expires_at),
            "failure_reason": self.failure_reason,
            "result": self.result,
            "stop_requested": self.stop_requested,
            "stop_seq": self.stop_seq,
            "history": self.history,
        }


@dataclass
class BridgeLink:
    last_seen_mono: float
    last_seen_at: datetime
    request: ClaimRequest


@dataclass(frozen=True)
class Outcome:
    status_code: int
    body: dict[str, Any]


class ActionStore:
    def __init__(
        self,
        observations: ObservationStore,
        clock: Callable[[], float] = time.monotonic,
        queue_limit: int = 16,
        lease_seconds: float = 5.0,
        link_timeout_seconds: float = 3.0,
        history_limit: int = 1000,
    ):
        self.instance_id = uuid.uuid4().hex
        self._observations = observations
        self._clock = clock
        self.queue_limit = queue_limit
        self.lease_seconds = lease_seconds
        self.link_timeout_seconds = link_timeout_seconds
        self._history_limit = history_limit
        self._lock = threading.Lock()
        self._records: OrderedDict[str, ActionRecord] = OrderedDict()
        self._queue: deque[str] = deque()
        self._active: str | None = None
        self._stop_seq = 0
        self._stop_reason: str | None = None
        self._link: BridgeLink | None = None

    # ---- client API ----------------------------------------------------------------------------

    def submit(self, sub: MoveDirectionAction | FaceDirectionAction | SelectSlotAction | StopAction) -> Outcome:
        fingerprint = json.dumps(sub.model_dump(mode="json", exclude={"action_id"}), sort_keys=True)
        with self._lock:
            now = self._clock()
            self._sweep(now)

            if sub.action_id is not None and sub.action_id in self._records:
                existing = self._records[sub.action_id]
                if existing.fingerprint != fingerprint:
                    return Outcome(409, {"accepted": False, "reason": "action_id_conflict", "action": existing.public()})
                # Idempotent retry: same action, never queued twice.
                return Outcome(200, {"accepted": True, "duplicate": True, "action": existing.public()})

            is_stop = sub.type == ActionType.STOP
            if not is_stop:
                problem = self._admission_problem(sub.session_id, now)
                if problem is not None:
                    return Outcome(409, {"accepted": False, "reason": problem})
                if len(self._queue) >= self.queue_limit:
                    return Outcome(429, {"accepted": False, "reason": "queue_full", "queue_limit": self.queue_limit})

            record = self._new_record(sub, fingerprint, now)
            if is_stop:
                reason = sub.params.reason or "stop action"
                record.stop_seq = self._raise_stop(reason)
                record.history[-1]["reason"] = f"stop_seq {record.stop_seq}"
            else:
                self._queue.append(record.action_id)
            return Outcome(201, {"accepted": True, "duplicate": False, "action": record.public()})

    def request_stop(self, req: StopRequest) -> Outcome:
        with self._lock:
            self._sweep(self._clock())
            reason = req.reason or "stop requested"
            if req.source:
                reason = f"{reason} ({req.source})"
            cancelled = self._cancel_queued(f"stopped: {reason}")
            seq = self._raise_stop(reason, already_cancelled=True)
            return Outcome(
                200,
                {
                    "accepted": True,
                    "stop_seq": seq,
                    "cancelled_action_ids": cancelled,
                    "active_action_id": self._active,
                },
            )

    def get(self, action_id: str) -> ActionRecord | None:
        with self._lock:
            self._sweep(self._clock())
            return self._records.get(action_id)

    def control_status(self) -> dict[str, Any]:
        with self._lock:
            now = self._clock()
            self._sweep(now)
            link = self._link
            polling = link is not None and now - link.last_seen_mono <= self.link_timeout_seconds
            return {
                "controller_instance_id": self.instance_id,
                "bridge_polling": polling,
                "bridge_last_seen_at_utc": link.last_seen_at.isoformat() if link else None,
                "bridge_session_id": link.request.session_id if link else None,
                "automation_enabled": bool(link and polling and link.request.automation_enabled),
                "bridge_ready": bool(link and polling and link.request.ready),
                "bridge_block_reason": link.request.block_reason if link else None,
                "active_action_id": self._active,
                "queued": len(self._queue),
                "queue_limit": self.queue_limit,
                "stop_seq": self._stop_seq,
            }

    # ---- bridge API ----------------------------------------------------------------------------

    def claim(self, req: ClaimRequest) -> dict[str, Any]:
        with self._lock:
            now = self._clock()
            self._link = BridgeLink(now, datetime.now(timezone.utc), req)
            self._sweep(now)

            # A stop_seq from another controller instance (we restarted) means nothing here.
            seen = req.stop_seq_seen if req.controller_instance_id == self.instance_id else 0
            self._ack_stops(seen)

            # Queued work that no longer matches what the bridge is doing.
            for action_id in list(self._queue):
                rec = self._records[action_id]
                if rec.session_id != req.session_id:
                    self._finish(rec, ActionState.CANCELLED, "session_changed")
                elif not req.automation_enabled:
                    self._finish(rec, ActionState.CANCELLED, "automation_disabled")
            self._compact_queue()

            if self._active is not None:
                active = self._records[self._active]
                if active.session_id != req.session_id:
                    self._finish(active, ActionState.CANCELLED, "session_changed")

            handed: dict[str, Any] | None = None
            if req.ready and req.automation_enabled and req.session_id is not None and self._active is None and self._queue:
                rec = self._records[self._queue.popleft()]
                rec.lease_id = uuid.uuid4().hex
                rec.claimed_at = datetime.now(timezone.utc)
                self._set_lease(rec, now)
                self._transition(rec, ActionState.CLAIMED)
                self._active = rec.action_id
                handed = {
                    "action_id": rec.action_id,
                    "session_id": rec.session_id,
                    "type": rec.type.value,
                    "params": rec.params,
                    "lease_id": rec.lease_id,
                    "lease_expires_at_utc": rec.lease_expires_at.isoformat() if rec.lease_expires_at else None,
                    "created_at_utc": rec.created_at.isoformat(),
                }

            return {
                "controller_instance_id": self.instance_id,
                "stop_seq": self._stop_seq,
                "stop_reason": self._stop_reason,
                "action": handed,
            }

    def report(self, action_id: str, rep: StatusReport) -> Outcome:
        with self._lock:
            now = self._clock()
            self._sweep(now)
            rec = self._records.get(action_id)
            if rec is None:
                # Unknown here: e.g. the controller restarted after the bridge claimed it.
                return Outcome(404, {"accepted": False, "reason": "unknown_action"})
            if rep.session_id != rec.session_id:
                return self._reject_report(rec, "session_mismatch")
            if rec.lease_id is None or rep.lease_id != rec.lease_id:
                return self._reject_report(rec, "lease_mismatch")
            if rep.result is not None and (rep.result.action_id != action_id or rep.result.session_id != rec.session_id):
                return Outcome(422, {"accepted": False, "reason": "result_mismatch"})

            new_state = ActionState(rep.state)
            if rec.state in TERMINAL:
                if rec.state == new_state:
                    # Retry of a report we already applied.
                    return Outcome(200, {"accepted": True, "duplicate": True, "action": rec.public()})
                return self._reject_report(rec, "already_terminal")

            allowed = {
                ActionState.CLAIMED: {ActionState.RUNNING} | FAILURE_STATES | {ActionState.SUCCEEDED},
                ActionState.RUNNING: {ActionState.RUNNING, ActionState.SUCCEEDED, ActionState.FAILED, ActionState.CANCELLED, ActionState.TIMED_OUT},
            }.get(rec.state, set())
            if new_state not in allowed:
                return self._reject_report(rec, "invalid_transition")

            result = rep.result.model_dump(mode="json") if rep.result else None
            if new_state == ActionState.RUNNING:
                if rec.state != ActionState.RUNNING:
                    rec.started_at = rep.result.started_at_utc if rep.result and rep.result.started_at_utc else datetime.now(timezone.utc)
                    self._transition(rec, ActionState.RUNNING)
                self._set_lease(rec, now)
                if result is not None:
                    rec.result = result
            else:
                if rep.result and rep.result.started_at_utc:
                    rec.started_at = rep.result.started_at_utc
                rec.result = result
                self._finish(rec, new_state, rep.failure_reason)
            return Outcome(200, {"accepted": True, "duplicate": False, "action": rec.public()})

    # ---- internals (lock held) -----------------------------------------------------------------

    def _admission_problem(self, session_id: str, now: float) -> str | None:
        view = self._observations.latest()
        if view.status is not Freshness.LIVE or view.observation is None:
            return "session_not_live"
        if view.observation.envelope.session_id != session_id:
            return "session_mismatch"
        link = self._link
        if link is None or now - link.last_seen_mono > self.link_timeout_seconds:
            return "bridge_not_polling"
        if link.request.session_id != session_id:
            return "bridge_session_mismatch"
        if not link.request.automation_enabled:
            return "automation_disabled"
        return None

    def _new_record(self, sub, fingerprint: str, now: float) -> ActionRecord:
        action_id = sub.action_id or uuid.uuid4().hex
        created = datetime.now(timezone.utc)
        record = ActionRecord(
            action_id=action_id,
            session_id=sub.session_id,
            type=ActionType(sub.type),
            params=sub.params.model_dump(mode="json"),
            fingerprint=fingerprint,
            state=ActionState.QUEUED,
            created_at=created,
            expires_mono=now + sub.ttl_seconds,
            expires_at=created + timedelta(seconds=sub.ttl_seconds),
            history=[{"state": ActionState.QUEUED.value, "at_utc": created.isoformat(), "reason": None}],
        )
        self._records[action_id] = record
        self._trim_history()
        return record

    def _raise_stop(self, reason: str, already_cancelled: bool = False) -> int:
        if not already_cancelled:
            self._cancel_queued(f"stopped: {reason}")
        self._stop_seq += 1
        self._stop_reason = reason
        if self._active is not None:
            # The bridge cancels it and reports CANCELLED; if it can't, the lease expires.
            self._records[self._active].stop_requested = True
        return self._stop_seq

    def _cancel_queued(self, reason: str) -> list[str]:
        cancelled = []
        for action_id in list(self._queue):
            rec = self._records[action_id]
            if rec.state == ActionState.QUEUED:
                self._finish(rec, ActionState.CANCELLED, reason)
                cancelled.append(action_id)
        self._queue.clear()
        return cancelled

    def _ack_stops(self, seen: int) -> None:
        for rec in self._records.values():
            if rec.type == ActionType.STOP and rec.state == ActionState.QUEUED and rec.stop_seq is not None and rec.stop_seq <= seen:
                rec.finished_at = datetime.now(timezone.utc)
                self._finish(rec, ActionState.SUCCEEDED, None)

    def _sweep(self, now: float) -> None:
        for rec in self._records.values():
            if rec.state == ActionState.QUEUED and now >= rec.expires_mono:
                self._finish(rec, ActionState.EXPIRED, "not claimed before ttl_seconds")
            elif rec.state in ACTIVE and rec.lease_expires_mono is not None and now >= rec.lease_expires_mono:
                self._finish(rec, ActionState.TIMED_OUT, "lease_expired: bridge stopped reporting")
        self._compact_queue()

    def _compact_queue(self) -> None:
        if any(self._records[a].state != ActionState.QUEUED for a in self._queue):
            self._queue = deque(a for a in self._queue if self._records[a].state == ActionState.QUEUED)

    def _set_lease(self, rec: ActionRecord, now: float) -> None:
        rec.lease_expires_mono = now + self.lease_seconds
        rec.lease_expires_at = datetime.now(timezone.utc) + timedelta(seconds=self.lease_seconds)

    def _transition(self, rec: ActionRecord, state: ActionState, reason: str | None = None) -> None:
        rec.state = state
        rec.history.append({"state": state.value, "at_utc": datetime.now(timezone.utc).isoformat(), "reason": reason})

    def _finish(self, rec: ActionRecord, state: ActionState, reason: str | None) -> None:
        if rec.state in TERMINAL:
            return
        rec.failure_reason = reason
        rec.finished_at = rec.finished_at or datetime.now(timezone.utc)
        rec.lease_expires_mono = None
        self._transition(rec, state, reason)
        if self._active == rec.action_id:
            self._active = None

    def _reject_report(self, rec: ActionRecord, reason: str) -> Outcome:
        return Outcome(409, {"accepted": False, "reason": reason, "state": rec.state.value})

    def _trim_history(self) -> None:
        # Drop the oldest terminal records; queued/active ones are always kept.
        while len(self._records) > self._history_limit:
            victim = next((a for a, r in self._records.items() if r.state in TERMINAL), None)
            if victim is None:
                break
            del self._records[victim]
