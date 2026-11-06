"""FastAPI controller: receives bridge observations, serves the latest one, and queues actions for the bridge."""

import logging
from typing import Any

from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse
from pydantic import BaseModel, TypeAdapter, ValidationError

from . import __version__
from .actions import ActionStore, ClaimRequest, Outcome, StatusReport, StopRequest, SubmissionAdapter
from .schema import SCHEMA_VERSION, ObservationEnvelope
from .store import LatestView, ObservationStore

log = logging.getLogger("stardew_agent.controller")

# A full snapshot is ~20-60 KB; anything far larger is not from the bridge.
MAX_BODY_BYTES = 1_000_000


def _freshness(view: LatestView) -> dict[str, Any]:
    obs = view.observation
    return {
        "status": view.status.value,
        "is_live": view.is_live,
        "age_seconds": round(view.age_seconds, 3) if view.age_seconds is not None else None,
        "stale_after_seconds": view.stale_after_seconds,
        "received_at": obs.received_at.isoformat() if obs else None,
        "session_id": obs.envelope.session_id if obs else None,
        "sequence": obs.envelope.sequence if obs else None,
    }


def _json_body(schema: type[BaseModel] | TypeAdapter) -> dict[str, Any]:
    """OpenAPI requestBody for routes that validate the raw body by hand."""
    json_schema = schema.json_schema() if isinstance(schema, TypeAdapter) else schema.model_json_schema()
    return {"requestBody": {"required": True, "content": {"application/json": {"schema": json_schema}}}}


async def _parse(request: Request, schema: type[BaseModel] | TypeAdapter, what: str, allow_empty: bool = False) -> Any:
    """Size-limited strict JSON validation. Returns the model, or a JSONResponse to send back."""
    declared = request.headers.get("content-length")
    if declared is not None and declared.isdigit() and int(declared) > MAX_BODY_BYTES:
        return JSONResponse({"accepted": False, "reason": "body_too_large"}, status_code=413)
    body = await request.body()
    if len(body) > MAX_BODY_BYTES:
        return JSONResponse({"accepted": False, "reason": "body_too_large"}, status_code=413)
    if allow_empty and not body.strip():
        body = b"{}"
    try:
        # JSON mode keeps strict typing while still parsing ISO datetimes.
        return schema.validate_json(body) if isinstance(schema, TypeAdapter) else schema.model_validate_json(body)
    except ValidationError as ex:
        errors = ex.errors(include_url=False, include_input=False, include_context=False)
        log.warning("Rejected invalid %s: %d error(s), first: %s", what, len(errors), errors[0] if errors else "?")
        return JSONResponse({"accepted": False, "reason": "invalid_schema", "errors": errors}, status_code=422)


def _send(outcome: Outcome) -> JSONResponse:
    return JSONResponse(outcome.body, status_code=outcome.status_code)


def create_app(store: ObservationStore | None = None, actions: ActionStore | None = None) -> FastAPI:
    store = store or ObservationStore()
    actions = actions or ActionStore(store)
    app = FastAPI(title="StardewAgent Controller", version=__version__)
    app.state.store = store
    app.state.actions = actions

    @app.get("/health")
    def health() -> dict[str, Any]:
        view = store.latest()
        return {
            "status": "ok",
            "service": "stardew-agent-controller",
            "version": __version__,
            "schema_version": SCHEMA_VERSION,
            "observation": _freshness(view),
            "control": actions.control_status(),
        }

    # ---- observations --------------------------------------------------------------------------

    # The body is validated by hand (strict JSON mode, size limit); the schema is documented anyway.
    @app.post("/v1/observations", openapi_extra=_json_body(ObservationEnvelope))
    async def post_observation(request: Request) -> JSONResponse:
        envelope = await _parse(request, ObservationEnvelope, "observation")
        if isinstance(envelope, JSONResponse):
            return envelope

        result = store.accept(envelope)
        if not result.accepted:
            assert result.rejection is not None
            log.info("Rejected observation %s #%d: %s", envelope.session_id, envelope.sequence, result.rejection.value)
            return JSONResponse(
                {
                    "accepted": False,
                    "reason": result.rejection.value,
                    "session_id": envelope.session_id,
                    "sequence": envelope.sequence,
                    "latest_sequence": result.latest_sequence,
                },
                status_code=409,
            )

        if result.new_session:
            log.info(
                "New session %s from bridge %s (world_ready=%s, sequence %d)",
                envelope.session_id,
                envelope.bridge_version,
                envelope.snapshot.world_ready,
                envelope.sequence,
            )
        elif not envelope.snapshot.world_ready:
            log.info("Session %s reported world_ready=false (returned to title)", envelope.session_id)

        return JSONResponse(
            {
                "accepted": True,
                "session_id": envelope.session_id,
                "sequence": envelope.sequence,
                "new_session": result.new_session,
            }
        )

    @app.get("/v1/observations/latest")
    def latest() -> dict[str, Any]:
        view = store.latest()
        obs = view.observation
        return {
            "freshness": _freshness(view),
            "bridge_version": obs.envelope.bridge_version if obs else None,
            "created_at_utc": obs.envelope.created_at_utc.isoformat() if obs else None,
            # Still returned when stale so callers can inspect it, but freshness.is_live is false.
            "snapshot": obs.envelope.snapshot.model_dump(mode="json") if obs else None,
        }

    # ---- actions -------------------------------------------------------------------------------

    @app.post("/v1/actions", openapi_extra=_json_body(SubmissionAdapter))
    async def submit_action(request: Request) -> JSONResponse:
        sub = await _parse(request, SubmissionAdapter, "action")
        if isinstance(sub, JSONResponse):
            return sub
        outcome = actions.submit(sub)
        if outcome.status_code == 201:
            a = outcome.body["action"]
            log.info("Queued action %s %s %s", a["action_id"], a["type"], a["params"])
        elif outcome.status_code >= 400:
            log.info("Refused action (%s): %s", sub.type, outcome.body.get("reason"))
        return _send(outcome)

    @app.post("/v1/actions/claim", openapi_extra=_json_body(ClaimRequest))
    async def claim_action(request: Request) -> JSONResponse:
        req = await _parse(request, ClaimRequest, "claim")
        if isinstance(req, JSONResponse):
            return req
        response = actions.claim(req)
        if response["action"] is not None:
            a = response["action"]
            log.info("Bridge claimed action %s %s (lease %s)", a["action_id"], a["type"], a["lease_id"][:8])
        return JSONResponse(response)

    @app.post("/v1/actions/{action_id}/status", openapi_extra=_json_body(StatusReport))
    async def report_status(action_id: str, request: Request) -> JSONResponse:
        rep = await _parse(request, StatusReport, "status report")
        if isinstance(rep, JSONResponse):
            return rep
        outcome = actions.report(action_id, rep)
        if outcome.status_code == 200 and rep.state != "RUNNING" and not outcome.body.get("duplicate"):
            log.info("Action %s %s%s", action_id, rep.state, f": {rep.failure_reason}" if rep.failure_reason else "")
        elif outcome.status_code >= 400:
            log.info("Refused %s report for %s: %s", rep.state, action_id, outcome.body.get("reason"))
        return _send(outcome)

    @app.get("/v1/actions/{action_id}")
    def get_action(action_id: str) -> JSONResponse:
        record = actions.get(action_id)
        if record is None:
            return JSONResponse({"reason": "unknown_action", "action_id": action_id}, status_code=404)
        return JSONResponse(record.public())

    # ---- control -------------------------------------------------------------------------------

    @app.post("/v1/control/stop", openapi_extra=_json_body(StopRequest))
    async def control_stop(request: Request) -> JSONResponse:
        req = await _parse(request, StopRequest, "stop request", allow_empty=True)
        if isinstance(req, JSONResponse):
            return req
        outcome = actions.request_stop(req)
        log.warning(
            "Stop requested (%s, source %s): stop_seq %d, cancelled %d queued action(s)",
            req.reason or "no reason",
            req.source or "client",
            outcome.body["stop_seq"],
            len(outcome.body["cancelled_action_ids"]),
        )
        return _send(outcome)

    @app.get("/v1/control/status")
    def control_status() -> dict[str, Any]:
        return actions.control_status()

    return app
