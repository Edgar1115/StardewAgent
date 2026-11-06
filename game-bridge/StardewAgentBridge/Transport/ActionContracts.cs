using System;

namespace StardewAgentBridge.Transport
{
    // Wire DTOs for the action API (agent/src/stardew_agent/actions.py). Serialized with StateJson.Options
    // (snake_case, nulls written), so every property here maps to a required field on the Python side.

    /// <summary>POST /v1/actions/claim body. Also the bridge heartbeat.</summary>
    public sealed record ClaimRequest
    {
        public string? SessionId { get; init; }
        public bool AutomationEnabled { get; init; }
        /// <summary>The bridge can start an action right now (armed, idle, player free, window focused).</summary>
        public bool Ready { get; init; }
        public string? RunningActionId { get; init; }
        public long StopSeqSeen { get; init; }
        public string? ControllerInstanceId { get; init; }
        public string? BlockReason { get; init; }
        public string BridgeVersion { get; init; } = "";
    }

    /// <summary>POST /v1/actions/claim response.</summary>
    public sealed record ClaimResponse
    {
        public string ControllerInstanceId { get; init; } = "";
        public long StopSeq { get; init; }
        public string? StopReason { get; init; }
        public ActionCommand? Action { get; init; }
    }

    /// <summary>An action handed to the bridge under a lease.</summary>
    public sealed record ActionCommand
    {
        public string ActionId { get; init; } = "";
        public string SessionId { get; init; } = "";
        public string Type { get; init; } = "";
        public ActionParams Params { get; init; } = new();
        /// <summary>Write token for status reports on this action.</summary>
        public string LeaseId { get; init; } = "";
    }

    /// <summary>Union of all action parameters; each action type reads only its own fields.</summary>
    public sealed record ActionParams
    {
        public string? Direction { get; init; }
        public int? DurationTicks { get; init; }
        public int? Slot { get; init; }
    }

    /// <summary>POST /v1/actions/{action_id}/status body.</summary>
    public sealed record StatusReport
    {
        public string SessionId { get; init; } = "";
        public string LeaseId { get; init; } = "";
        /// <summary>RUNNING, SUCCEEDED, FAILED, CANCELLED, REJECTED or TIMED_OUT.</summary>
        public string State { get; init; } = "";
        public string? FailureReason { get; init; }
        public ActionResult? Result { get; init; }
    }

    public sealed record ActionResult
    {
        public string ActionId { get; init; } = "";
        public string SessionId { get; init; } = "";
        public string Type { get; init; } = "";
        public DateTime? StartedAtUtc { get; init; }
        public DateTime? FinishedAtUtc { get; init; }
        public int TicksExecuted { get; init; }
        public PlayerPosition? StartPosition { get; init; }
        public PlayerPosition? EndPosition { get; init; }
        /// <summary>Straight-line pixel distance between start and end; null when either is unknown.</summary>
        public double? DistancePixels { get; init; }
        /// <summary>The player stopped moving while the key was still held (wall, object, NPC).</summary>
        public bool Blocked { get; init; }
        public string? FailureReason { get; init; }
    }

    public sealed record PlayerPosition
    {
        public string LocationName { get; init; } = "";
        public int TileX { get; init; }
        public int TileY { get; init; }
        /// <summary>Pixel position (Farmer.Position).</summary>
        public float X { get; init; }
        public float Y { get; init; }
        public int FacingDirection { get; init; }
    }

    /// <summary>POST /v1/control/stop body.</summary>
    public sealed record StopRequest
    {
        public string? Reason { get; init; }
        public string? Source { get; init; }
    }

    /// <summary>One status report addressed to an action, queued for the background sender.</summary>
    public sealed record OutgoingReport(string ActionId, StatusReport Report);
}
