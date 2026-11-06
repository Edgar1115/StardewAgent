"""Action API tests: schema, idempotency, claim/lease state machine, stop, sessions, restarts."""

import json
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from stardew_agent.actions import ActionStore
from stardew_agent.app import create_app
from stardew_agent.store import ObservationStore

FIXTURES = Path(__file__).parent / "fixtures"
S1 = "a" * 32
S2 = "b" * 32


class FakeClock:
    def __init__(self) -> None:
        self.now = 1000.0

    def __call__(self) -> float:
        return self.now


class Harness:
    def __init__(self, queue_limit: int = 4) -> None:
        self.clock = FakeClock()
        self.observations = ObservationStore(stale_after_seconds=5, clock=self.clock)
        self.actions = ActionStore(self.observations, clock=self.clock, queue_limit=queue_limit, lease_seconds=5, link_timeout_seconds=3)
        self.client = TestClient(create_app(self.observations, self.actions))
        self.seq = 0

    def observe(self, session: str = S1, world_ready: bool = True) -> None:
        body = json.loads((FIXTURES / ("ready.json" if world_ready else "not_ready_cleared.json")).read_text())
        self.seq += 1
        body["session_id"], body["sequence"] = session, self.seq
        r = self.client.post("/v1/observations", json=body)
        assert r.status_code == 200, r.text

    def claim(self, session: str | None = S1, armed: bool = True, ready: bool = True, stop_seq_seen: int = 0, instance: str | None = "self", running: str | None = None) -> dict:
        body = {
            "session_id": session,
            "automation_enabled": armed,
            "ready": ready,
            "running_action_id": running,
            "stop_seq_seen": stop_seq_seen,
            "controller_instance_id": self.actions.instance_id if instance == "self" else instance,
            "block_reason": None,
            "bridge_version": "test",
        }
        r = self.client.post("/v1/actions/claim", json=body)
        assert r.status_code == 200, r.text
        return r.json()

    def submit(self, **overrides):
        body = {"session_id": S1, "type": "move_direction", "params": {"direction": "up", "duration_ticks": 30}}
        body.update(overrides)
        return self.client.post("/v1/actions", json=body)

    def report(self, action: dict, state: str, reason: str | None = None, result: bool = True, lease: str | None = None):
        body = {
            "session_id": action["session_id"],
            "lease_id": lease or action["lease_id"],
            "state": state,
            "failure_reason": reason,
            "result": self.result(action, reason) if result else None,
        }
        return self.client.post(f"/v1/actions/{action['action_id']}/status", json=body)

    @staticmethod
    def result(action: dict, reason: str | None) -> dict:
        pos = {"location_name": "Farm", "tile_x": 64, "tile_y": 15, "x": 4096.0, "y": 960.0, "facing_direction": 0}
        return {
            "action_id": action["action_id"],
            "session_id": action["session_id"],
            "type": action["type"],
            "started_at_utc": "2026-11-05T10:00:00.1234567Z",
            "finished_at_utc": "2026-11-05T10:00:00.6234567Z",
            "ticks_executed": 30,
            "start_position": pos,
            "end_position": {**pos, "tile_y": 13, "y": 832.0},
            "distance_pixels": 128.0,
            "blocked": False,
            "failure_reason": reason,
        }

    def get(self, action_id: str) -> dict:
        r = self.client.get(f"/v1/actions/{action_id}")
        assert r.status_code == 200, r.text
        return r.json()


@pytest.fixture
def h() -> Harness:
    harness = Harness()
    harness.observe()
    harness.claim()  # bridge link established, armed, idle
    return harness


# ---- admission ---------------------------------------------------------------------------------


def test_submit_requires_live_session_and_polling_armed_bridge() -> None:
    h = Harness()
    assert h.submit().json()["reason"] == "session_not_live"

    h.observe()
    assert h.submit().json()["reason"] == "bridge_not_polling"

    h.claim(armed=False)
    assert h.submit().json()["reason"] == "automation_disabled"

    h.claim()
    assert h.submit(session_id=S2).json()["reason"] == "session_mismatch"

    h.clock.now += 3.5  # bridge silent longer than link timeout
    h.observe()
    assert h.submit().json()["reason"] == "bridge_not_polling"


def test_submit_refused_after_return_to_title(h: Harness) -> None:
    h.observe(world_ready=False)
    r = h.submit()
    assert r.status_code == 409 and r.json()["reason"] == "session_not_live"


@pytest.mark.parametrize(
    "body",
    [
        pytest.param({"type": "move_direction", "params": {"direction": "up", "duration_ticks": 0}}, id="ticks_0"),
        pytest.param({"type": "move_direction", "params": {"direction": "up", "duration_ticks": 61}}, id="ticks_61"),
        pytest.param({"type": "move_direction", "params": {"direction": "north", "duration_ticks": 10}}, id="direction"),
        pytest.param({"type": "move_direction", "params": {"direction": "up", "duration_ticks": "10"}}, id="ticks_string"),
        pytest.param({"type": "move_direction", "params": {"direction": "up"}}, id="missing_ticks"),
        pytest.param({"type": "move_direction", "params": {"direction": "up", "duration_ticks": 5, "speed": 2}}, id="extra_param"),
        pytest.param({"type": "move_to", "params": {"x": 1, "y": 2}}, id="unsupported_type"),
        pytest.param({"type": "select_slot", "params": {"slot": 12}}, id="slot_12"),
        pytest.param({"type": "move_direction", "params": {"direction": "up", "duration_ticks": 5}, "ttl_seconds": 600}, id="ttl"),
        pytest.param({"type": "move_direction", "params": {"direction": "up", "duration_ticks": 5}, "action_id": "x"}, id="action_id_short"),
        pytest.param({"type": "move_direction", "params": {"direction": "up", "duration_ticks": 5}, "priority": 1}, id="extra_field"),
    ],
)
def test_invalid_submissions_rejected(h: Harness, body: dict) -> None:
    r = h.client.post("/v1/actions", json={"session_id": S1, **body})
    assert r.status_code == 422, r.text
    assert h.actions.control_status()["queued"] == 0


# ---- idempotency and queue ---------------------------------------------------------------------


def test_submit_returns_unique_action_ids(h: Harness) -> None:
    a = h.submit().json()["action"]
    b = h.submit().json()["action"]
    assert a["action_id"] != b["action_id"]
    assert a["state"] == "QUEUED" and a["session_id"] == S1


def test_same_action_id_is_idempotent(h: Harness) -> None:
    first = h.submit(action_id="client-0001")
    assert first.status_code == 201
    again = h.submit(action_id="client-0001")
    assert again.status_code == 200 and again.json()["duplicate"] is True
    assert h.actions.control_status()["queued"] == 1

    # Only handed out once.
    assert h.claim()["action"]["action_id"] == "client-0001"
    assert h.claim(ready=False)["action"] is None

    conflict = h.submit(action_id="client-0001", params={"direction": "down", "duration_ticks": 30})
    assert conflict.status_code == 409 and conflict.json()["reason"] == "action_id_conflict"

    # Still idempotent once finished: no re-queue.
    action = h.actions.get("client-0001").public() | {"lease_id": h.actions.get("client-0001").lease_id}
    assert h.report(action, "SUCCEEDED").status_code == 200
    assert h.submit(action_id="client-0001").json()["action"]["state"] == "SUCCEEDED"
    assert h.claim()["action"] is None


def test_queue_is_bounded(h: Harness) -> None:
    for _ in range(4):
        assert h.submit().status_code == 201
    r = h.submit()
    assert r.status_code == 429 and r.json()["reason"] == "queue_full"


# ---- claim / lease -----------------------------------------------------------------------------


def test_single_running_action(h: Harness) -> None:
    first = h.submit().json()["action"]["action_id"]
    second = h.submit().json()["action"]["action_id"]

    claimed = h.claim()["action"]
    assert claimed["action_id"] == first and claimed["lease_id"]
    assert h.get(first)["state"] == "CLAIMED"
    assert "lease_id" not in h.get(first)

    # Bridge says ready again, but one action is still active.
    assert h.claim()["action"] is None

    assert h.report(claimed, "RUNNING").status_code == 200
    assert h.report(claimed, "SUCCEEDED").status_code == 200
    assert h.claim()["action"]["action_id"] == second


def test_claim_only_when_ready_armed_and_same_session(h: Harness) -> None:
    h.submit()
    assert h.claim(ready=False)["action"] is None
    assert h.claim(armed=False)["action"] is None
    # automation_enabled=false cleared the queue (manual takeover)
    assert h.actions.control_status()["queued"] == 0

    h.claim(ready=False)  # re-armed
    aid = h.submit().json()["action"]["action_id"]
    assert h.claim(session=S2)["action"] is None
    assert h.get(aid)["state"] == "CANCELLED"
    assert h.get(aid)["failure_reason"] == "session_changed"


def test_full_lifecycle_and_result_query(h: Harness) -> None:
    aid = h.submit().json()["action"]["action_id"]
    action = h.claim()["action"]
    assert h.report(action, "RUNNING").json()["action"]["state"] == "RUNNING"
    r = h.report(action, "SUCCEEDED")
    assert r.status_code == 200

    rec = h.get(aid)
    assert rec["state"] == "SUCCEEDED" and rec["terminal"] is True
    assert rec["result"]["ticks_executed"] == 30
    assert rec["result"]["start_position"]["tile_y"] == 15
    assert rec["result"]["end_position"]["tile_y"] == 13
    assert rec["started_at_utc"] and rec["finished_at_utc"]
    assert [step["state"] for step in rec["history"]] == ["QUEUED", "CLAIMED", "RUNNING", "SUCCEEDED"]


def test_terminal_states_are_final(h: Harness) -> None:
    h.submit()
    action = h.claim()["action"]
    assert h.report(action, "FAILED", reason="blocked: player did not move").status_code == 200

    dup = h.report(action, "FAILED", reason="blocked: player did not move")
    assert dup.status_code == 200 and dup.json()["duplicate"] is True
    other = h.report(action, "SUCCEEDED")
    assert other.status_code == 409 and other.json()["reason"] == "already_terminal"
    running = h.report(action, "RUNNING")
    assert running.status_code == 409
    assert h.get(action["action_id"])["state"] == "FAILED"


def test_report_validation(h: Harness) -> None:
    h.submit()
    action = h.claim()["action"]
    assert h.report(action, "RUNNING", lease="f" * 32).json()["reason"] == "lease_mismatch"
    assert h.report({**action, "session_id": S2}, "RUNNING").json()["reason"] == "session_mismatch"
    assert h.report(action, "SUCCEEDED", result=False).status_code == 422
    assert h.report(action, "FAILED", reason=None).status_code == 422
    assert h.report(action, "QUEUED").status_code == 422
    unknown = h.report({**action, "action_id": "c" * 32}, "RUNNING")
    assert unknown.status_code == 404 and unknown.json()["reason"] == "unknown_action"
    assert h.get(action["action_id"])["state"] == "CLAIMED"


def test_lease_expiry_times_out_and_frees_the_slot(h: Harness) -> None:
    h.submit()
    second = h.submit(ttl_seconds=30).json()["action"]["action_id"]
    action = h.claim()["action"]
    h.report(action, "RUNNING")

    h.clock.now += 4.9
    h.claim(ready=False)  # bridge still polling, but not reporting on the action
    h.report(action, "RUNNING")  # heartbeat renews the lease
    h.clock.now += 4.9
    assert h.get(action["action_id"])["state"] == "RUNNING"

    h.clock.now += 0.2
    rec = h.get(action["action_id"])
    assert rec["state"] == "TIMED_OUT" and rec["failure_reason"].startswith("lease_expired")

    late = h.report(action, "SUCCEEDED")
    assert late.status_code == 409 and late.json()["reason"] == "already_terminal"
    assert h.claim()["action"]["action_id"] == second


def test_unclaimed_action_expires(h: Harness) -> None:
    aid = h.submit(ttl_seconds=2).json()["action"]["action_id"]
    h.clock.now += 2.1
    assert h.claim()["action"] is None
    assert h.get(aid)["state"] == "EXPIRED"


# ---- stop --------------------------------------------------------------------------------------


def test_control_stop_cancels_queue_and_signals_bridge(h: Harness) -> None:
    h.submit()
    queued = h.submit().json()["action"]["action_id"]
    active = h.claim()["action"]
    h.report(active, "RUNNING")

    r = h.client.post("/v1/control/stop", json={"reason": "test"})
    assert r.status_code == 200
    body = r.json()
    assert body["stop_seq"] == 1 and body["cancelled_action_ids"] == [queued] and body["active_action_id"] == active["action_id"]
    assert h.get(queued)["state"] == "CANCELLED"
    assert h.get(active["action_id"])["stop_requested"] is True

    poll = h.claim(ready=False)
    assert poll["stop_seq"] == 1 and poll["stop_reason"] == "test" and poll["action"] is None

    assert h.report(active, "CANCELLED", reason="stopped: test").status_code == 200
    assert h.get(active["action_id"])["state"] == "CANCELLED"


def test_control_stop_accepts_empty_body(h: Harness) -> None:
    assert h.client.post("/v1/control/stop").json()["stop_seq"] == 1


def test_stop_action_is_priority_and_acknowledged(h: Harness) -> None:
    move = h.submit().json()["action"]["action_id"]
    stop = h.submit(type="stop", params={"reason": "halt"})
    assert stop.status_code == 201
    stop_rec = stop.json()["action"]
    assert stop_rec["stop_seq"] == 1
    # Queued moves are cancelled; nothing else is handed out ahead of the stop.
    assert h.get(move)["state"] == "CANCELLED"

    # Another controller instance's stop counter must not acknowledge it.
    assert h.claim(stop_seq_seen=5, instance="someone-else")["stop_seq"] == 1
    assert h.get(stop_rec["action_id"])["state"] == "QUEUED"

    h.claim(stop_seq_seen=1)
    assert h.get(stop_rec["action_id"])["state"] == "SUCCEEDED"


def test_stop_action_accepted_even_without_bridge() -> None:
    h = Harness()
    r = h.submit(type="stop", params={})
    assert r.status_code == 201


# ---- sessions and restarts ---------------------------------------------------------------------


def test_new_session_invalidates_old_actions(h: Harness) -> None:
    old_queued = h.submit().json()["action"]["action_id"]

    # Save reloaded: new session in both observations and bridge polls.
    h.observe(session=S2)
    assert h.claim(session=S2)["action"] is None
    assert h.get(old_queued)["state"] == "CANCELLED"
    assert h.submit(session_id=S1).json()["reason"] == "session_mismatch"
    assert h.submit(session_id=S2).status_code == 201


def test_active_action_cancelled_when_bridge_session_changes(h: Harness) -> None:
    h.submit()
    active = h.claim()["action"]
    h.claim(session=None, armed=False, ready=False)  # back on the title screen
    rec = h.get(active["action_id"])
    assert rec["state"] == "CANCELLED" and rec["failure_reason"] == "session_changed"


def test_restarted_controller_does_not_replay(h: Harness) -> None:
    h.submit()
    claimed = h.claim()["action"]

    # Restart: new stores, same bridge.
    fresh = Harness()
    fresh.observe()
    poll = fresh.claim(stop_seq_seen=3, instance=h.actions.instance_id)
    assert poll["controller_instance_id"] != h.actions.instance_id
    assert poll["action"] is None and poll["stop_seq"] == 0

    late = fresh.report(claimed, "SUCCEEDED")
    assert late.status_code == 404
    assert fresh.client.get(f"/v1/actions/{claimed['action_id']}").status_code == 404


def test_history_is_bounded() -> None:
    h = Harness(queue_limit=100)
    h.actions._history_limit = 10
    h.observe()
    h.claim()
    for _ in range(30):
        aid = h.submit().json()["action"]["action_id"]
        h.client.post("/v1/control/stop", json={})  # cancels it -> terminal
        assert h.get(aid)["state"] == "CANCELLED"
    assert len(h.actions._records) <= 10


def test_health_and_control_status(h: Harness) -> None:
    status = h.client.get("/health").json()["control"]
    assert status["bridge_polling"] is True and status["automation_enabled"] is True
    assert status["bridge_session_id"] == S1
    assert h.client.get("/v1/control/status").json()["controller_instance_id"] == h.actions.instance_id
