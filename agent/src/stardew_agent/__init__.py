"""StardewAgent Python controller."""

import argparse
import ipaddress
import logging
import os

__version__ = "0.1.0"

DEFAULT_HOST = "127.0.0.1"
DEFAULT_PORT = 8765


def _is_loopback(host: str) -> bool:
    if host == "localhost":
        return True
    try:
        return ipaddress.ip_address(host).is_loopback
    except ValueError:
        return False


def main() -> None:
    parser = argparse.ArgumentParser(prog="stardew-agent", description="Run the StardewAgent controller (local HTTP).")
    parser.add_argument("--host", default=os.environ.get("STARDEW_AGENT_HOST", DEFAULT_HOST))
    parser.add_argument("--port", type=int, default=int(os.environ.get("STARDEW_AGENT_PORT", DEFAULT_PORT)))
    parser.add_argument(
        "--stale-after",
        type=float,
        default=float(os.environ.get("STARDEW_AGENT_STALE_AFTER", "5")),
        help="seconds without observations before the latest state is reported as stale (default 5)",
    )
    parser.add_argument("--log-level", default="info")
    args = parser.parse_args()

    # The API has no authentication, so it must never listen beyond this machine.
    if not _is_loopback(args.host):
        parser.error(f"--host must be a loopback address (127.0.0.1, ::1, localhost), got {args.host!r}")

    import uvicorn

    from .app import create_app
    from .store import ObservationStore

    logging.basicConfig(level=args.log_level.upper(), format="%(asctime)s %(levelname)s %(name)s: %(message)s")
    app = create_app(ObservationStore(stale_after_seconds=args.stale_after))
    # Access log off: the bridge posts once per second.
    uvicorn.run(app, host=args.host, port=args.port, log_level=args.log_level, access_log=False)
