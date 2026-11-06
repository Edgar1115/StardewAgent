"""Controller tests. Fixtures in tests/fixtures are produced by the real C# serializer:
dotnet game-bridge/tools/TransportProbe/bin/Debug/net10.0/TransportProbe.dll fixtures agent/tests/fixtures
"""

import copy
import json
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from stardew_agent.app import create_app
from stardew_agent.schema import ObservationEnvelope
from stardew_agent.store import ObservationStore

FIXTURES = Path(__file__).parent / "fixtures"


def load(name: str) -> dict:
    return json.loads((FIXTURES / name).read_text())


class FakeClock:
    def __init__(self) -> None:
        self.now = 1000.0

    def __call__(self) -> float:
        return self.now


@pytest.fixture
def clock() -> FakeClock:
    return FakeClock()


@pytest.fixture
def client(clock: FakeClock) -> TestClient:
    return TestClient(create_app(ObservationStore(stale_after_seconds=5, clock=clock)))


def envelope(session: str = "0123456789abcdef0123456789abcdef", sequence: int = 1, **snapshot_changes) -> dict:
    body = load("ready.json")
    body["session_id"] = session
    body["sequence"] = sequence
    for path, value in snapshot_changes.items():
        target = body["snapshot"]
        *parents, leaf = path.split("__")
        for p in parents:
            target = target[p]
        target[leaf] = value
    return body


def latest(client: TestClient) -> dict:
    r = client.get("/v1/observations/latest")
    assert r.status_code == 200
    return r.json()


# ---- schema ------------------------------------------------------------------------------------


@pytest.mark.parametrize("name", ["ready.json", "not_ready_initial.json", "not_ready_cleared.json"])
def test_csharp_fixtures_validate(name: str) -> None:
    ObservationEnvelope.model_validate_json((FIXTURES / name).read_bytes())


def test_dotnet_seven_digit_fraction_parses() -> None:
    body = envelope()
    body["created_at_utc"] = "2026-11-05T08:10:00.1234567Z"
    ObservationEnvelope.model_validate_json(json.dumps(body))


@pytest.mark.parametrize(
    "mutate",
    [
        pytest.param(lambda b: b.update(schema_version=2), id="schema_version"),
        pytest.param(lambda b: b.update(session_id="bad id!"), id="session_id"),
        pytest.param(lambda b: b.update(sequence=0), id="sequence_zero"),
        pytest.param(lambda b: b.update(sequence="5"), id="sequence_string"),
        pytest.param(lambda b: b.pop("snapshot"), id="missing_snapshot"),
        pytest.param(lambda b: b.update(extra_field=1), id="unknown_envelope_field"),
        pytest.param(lambda b: b["snapshot"]["world"].pop("weather"), id="missing_nullable_field"),
        pytest.param(lambda b: b["snapshot"]["world"].update(foo=1), id="unknown_snapshot_field"),
        pytest.param(lambda b: b["snapshot"].update(player=None), id="ready_with_null_section"),
        pytest.param(lambda b: b["snapshot"].update(world_ready=False), id="not_ready_with_sections"),
        pytest.param(lambda b: b["snapshot"]["world"].update(season="monsoon"), id="season"),
        pytest.param(lambda b: b["snapshot"]["world"].update(time_of_day=675, time_text="06:75"), id="time"),
        pytest.param(lambda b: b["snapshot"]["world"].update(time_text="07:00"), id="time_text_mismatch"),
        pytest.param(lambda b: b["snapshot"]["player"].update(facing="up"), id="facing_mismatch"),
        pytest.param(lambda b: b["snapshot"]["player"].update(tile_x=1.5), id="float_tile"),
        pytest.param(lambda b: b["snapshot"]["inventory"].update(used_slots=7), id="inventory_counts"),
        pytest.param(lambda b: b["snapshot"]["inventory"]["slots"].pop(), id="inventory_slot_count"),
        pytest.param(lambda b: b["snapshot"]["inventory"]["slots"][0].update(is_empty=True), id="slot_empty_flag"),
        pytest.param(lambda b: b["snapshot"]["inventory"]["slots"][0]["item"].update(quality=3), id="quality"),
        pytest.param(lambda b: b["snapshot"]["finance"].update(money="500"), id="money_string"),
        pytest.param(lambda b: b["snapshot"]["finance"].update(wallet_scope="individual"), id="wallet_scope"),
    ],
)
def test_invalid_schema_rejected(mutate) -> None:
    body = envelope()
    mutate(body)
    with pytest.raises(ValueError):
        ObservationEnvelope.model_validate_json(json.dumps(body))


# ---- HTTP --------------------------------------------------------------------------------------


def test_health(client: TestClient) -> None:
    r = client.get("/health")
    assert r.status_code == 200
    data = r.json()
    assert data["status"] == "ok"
    assert data["schema_version"] == 1
    assert data["observation"]["status"] == "no_data"


def test_latest_before_any_data(client: TestClient) -> None:
    data = latest(client)
    assert data["freshness"]["status"] == "no_data"
    assert data["freshness"]["is_live"] is False
    assert data["snapshot"] is None


def test_accept_and_read_back(client: TestClient) -> None:
    r = client.post("/v1/observations", json=envelope(sequence=1))
    assert r.status_code == 200, r.text
    assert r.json() == {"accepted": True, "session_id": "0123456789abcdef0123456789abcdef", "sequence": 1, "new_session": True}

    data = latest(client)
    assert data["freshness"]["status"] == "live"
    assert data["freshness"]["is_live"] is True
    assert data["freshness"]["sequence"] == 1
    assert data["snapshot"]["world"]["time_of_day"] == 600
    assert data["snapshot"]["finance"]["money"] == 500


def test_updates_replace_state(client: TestClient) -> None:
    client.post("/v1/observations", json=envelope(sequence=1))
    body = envelope(sequence=2, world__time_of_day=610, world__time_text="06:10", player__tile_x=70)
    body["snapshot"]["finance"]["money"] = 650
    body["snapshot"]["toolbar"]["selected_index"] = 1
    assert client.post("/v1/observations", json=body).status_code == 200

    snap = latest(client)["snapshot"]
    assert (snap["world"]["time_of_day"], snap["player"]["tile_x"], snap["finance"]["money"], snap["toolbar"]["selected_index"]) == (610, 70, 650, 1)


def test_duplicate_and_out_of_order_do_not_overwrite(client: TestClient) -> None:
    client.post("/v1/observations", json=envelope(sequence=5, player__tile_x=50))

    dup = client.post("/v1/observations", json=envelope(sequence=5, player__tile_x=1))
    assert dup.status_code == 409 and dup.json()["reason"] == "duplicate_sequence"

    old = client.post("/v1/observations", json=envelope(sequence=3, player__tile_x=2))
    assert old.status_code == 409 and old.json()["reason"] == "out_of_order_sequence"
    assert old.json()["latest_sequence"] == 5

    assert latest(client)["snapshot"]["player"]["tile_x"] == 50


def test_invalid_post_does_not_touch_state(client: TestClient) -> None:
    client.post("/v1/observations", json=envelope(sequence=1, player__tile_x=50))
    bad = envelope(sequence=2, player__tile_x=99)
    bad["snapshot"]["world"]["season"] = "monsoon"
    r = client.post("/v1/observations", json=bad)
    assert r.status_code == 422
    assert r.json()["reason"] == "invalid_schema"

    r = client.post("/v1/observations", content=b"{not json", headers={"content-type": "application/json"})
    assert r.status_code == 422

    data = latest(client)
    assert data["freshness"]["sequence"] == 1
    assert data["snapshot"]["player"]["tile_x"] == 50


def test_body_too_large(client: TestClient) -> None:
    r = client.post("/v1/observations", content=b"x" * 1_000_001, headers={"content-type": "application/json"})
    assert r.status_code == 413


def test_new_session_resets_and_old_session_is_retired(client: TestClient) -> None:
    a, b = "a" * 32, "b" * 32
    client.post("/v1/observations", json=envelope(session=a, sequence=40, player__tile_x=40))

    # Game restarted / save reloaded: a new session starts at sequence 1 and replaces the old state.
    r = client.post("/v1/observations", json=envelope(session=b, sequence=1, player__tile_x=1))
    assert r.status_code == 200 and r.json()["new_session"] is True
    data = latest(client)
    assert data["freshness"]["session_id"] == b
    assert data["snapshot"]["player"]["tile_x"] == 1

    # A late packet from the old session must not take over again, even with a high sequence.
    late = client.post("/v1/observations", json=envelope(session=a, sequence=41, player__tile_x=41))
    assert late.status_code == 409 and late.json()["reason"] == "retired_session"
    assert latest(client)["freshness"]["session_id"] == b


def test_return_to_title_hides_old_state(client: TestClient) -> None:
    session = "0123456789abcdef0123456789abcdef"
    client.post("/v1/observations", json=envelope(session=session, sequence=1))
    closing = load("not_ready_cleared.json")
    closing["session_id"], closing["sequence"] = session, 2
    assert client.post("/v1/observations", json=closing).status_code == 200

    data = latest(client)
    assert data["freshness"]["status"] == "not_ready"
    assert data["freshness"]["is_live"] is False
    assert data["snapshot"]["world_ready"] is False
    assert data["snapshot"]["player"] is None

    # A delayed in-world packet from before the close is older and must not resurrect the save.
    late = client.post("/v1/observations", json=envelope(session=session, sequence=1))
    assert late.status_code == 409
    assert latest(client)["freshness"]["status"] == "not_ready"


def test_goes_stale_without_data_and_recovers(client: TestClient, clock: FakeClock) -> None:
    client.post("/v1/observations", json=envelope(sequence=1))
    clock.now += 4.9
    assert latest(client)["freshness"]["status"] == "live"

    clock.now += 0.2  # 5.1 s since last post: game closed / network down
    data = latest(client)
    assert data["freshness"]["status"] == "stale"
    assert data["freshness"]["is_live"] is False
    assert client.get("/health").json()["observation"]["status"] == "stale"

    client.post("/v1/observations", json=envelope(sequence=2))
    assert latest(client)["freshness"]["status"] == "live"


def test_stored_snapshot_is_immutable(client: TestClient) -> None:
    body = envelope()
    env = ObservationEnvelope.model_validate_json(json.dumps(body))
    with pytest.raises(ValueError):
        env.snapshot.world_ready = False  # type: ignore[misc]
    assert copy.deepcopy(env) == env
