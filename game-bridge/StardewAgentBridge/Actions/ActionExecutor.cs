using System;
using System.Collections.Generic;
using StardewAgentBridge.Transport;

namespace StardewAgentBridge.Actions
{
    public sealed record ExecutorOptions
    {
        /// <summary>Upper bound for move_direction duration_ticks (the controller enforces the same limit).</summary>
        public int MaxMoveTicks { get; init; } = 60;
        /// <summary>Ticks pressed before stall detection starts (the farmer needs a moment to start walking).</summary>
        public int StallGraceTicks { get; init; } = 4;
        /// <summary>Consecutive ticks without movement while the key is held that count as blocked.</summary>
        public int StallTicks { get; init; } = 10;
        /// <summary>Movement below this many pixels in a tick counts as no movement.</summary>
        public float StallEpsilonPixels { get; init; } = 0.5f;
        /// <summary>Hard real-time limit per action, whatever the tick count.</summary>
        public double MaxActionSeconds { get; init; } = 3;
        /// <summary>Cancel the running action when the controller hasn't answered for this long.</summary>
        public double LinkTimeoutSeconds { get; init; } = 2;
        /// <summary>Seconds between RUNNING heartbeats for a running action.</summary>
        public double HeartbeatSeconds { get; init; } = 1;
        /// <summary>Action IDs remembered per session to refuse replays.</summary>
        public int RecentIdLimit { get; init; } = 512;
        /// <summary>How long a received action waits for a transient block (tool swing, fade, refocus) to clear before it is rejected.</summary>
        public double StartWaitSeconds { get; init; } = 2;
    }

    /// <summary>
    /// Runs controller actions on the main thread, one at a time. Game-agnostic: all game access goes through
    /// <see cref="IGameControl"/>, all output through report/log callbacks. Call every member from the main thread.
    /// </summary>
    public sealed class ActionExecutor
    {
        private readonly IGameControl game;
        private readonly Action<OutgoingReport> report;
        private readonly Action<TransportLogLevel, string> log;
        private readonly ExecutorOptions options;
        private readonly Func<DateTime> utcNow;
        private readonly HashSet<string> recentIds = new();
        private readonly Queue<string> recentOrder = new();
        private Running? current;
        /// <summary>Received and valid, waiting for a transient block to clear.</summary>
        private (ActionCommand Command, DateTime DeadlineUtc)? pending;

        /// <summary>
        /// Blocks that clear by themselves within a moment. An action arriving during one waits
        /// (<see cref="ExecutorOptions.StartWaitSeconds"/>) instead of being rejected; e.g. the mouse click that
        /// refocuses the game window also swings the held tool.
        /// </summary>
        private static readonly string[] TransientBlocks =
        {
            "using_tool", "player_cannot_move", "screen_fading", "controls_frozen", "game_window_inactive"
        };

        public ActionExecutor(IGameControl game, Action<OutgoingReport> report, Action<TransportLogLevel, string> log, ExecutorOptions? options = null, Func<DateTime>? utcNow = null)
        {
            this.utcNow = utcNow ?? (() => DateTime.UtcNow);
            this.game = game;
            this.report = report;
            this.log = log;
            this.options = options ?? new ExecutorOptions();
        }

        /// <summary>Automation is armed. Off by default; only the player turns it on.</summary>
        public bool Armed { get; private set; }

        public string? SessionId { get; private set; }

        public string? ControllerInstanceId { get; private set; }

        /// <summary>Highest stop request applied for <see cref="ControllerInstanceId"/>.</summary>
        public long StopSeqSeen { get; private set; }

        public string? RunningActionId => this.current?.Command.ActionId ?? this.pending?.Command.ActionId;

        public string? RunningActionType => this.current?.Command.Type ?? this.pending?.Command.Type;

        /// <summary>True while a received action waits for a transient block to clear.</summary>
        public bool IsWaitingToStart => this.pending != null;

        /// <summary>Follow the bridge's save session. Any change cancels the running action and disarms.</summary>
        public void SetSession(string? sessionId)
        {
            if (sessionId == this.SessionId)
                return;

            this.Abort(ActionStates.Cancelled, "session_changed");
            if (this.Armed)
                this.log(TransportLogLevel.Info, "Automation disarmed: save session changed.");
            this.Armed = false;
            this.SessionId = sessionId;
            this.recentIds.Clear();
            this.recentOrder.Clear();
        }

        public void Arm()
        {
            this.Armed = true;
        }

        /// <summary>Stop automation now: cancel the running action and refuse new ones until re-armed.</summary>
        public void Disarm(string reason)
        {
            this.Armed = false;
            this.Abort(ActionStates.Cancelled, reason);
        }

        /// <summary>Whether a new action could start right now; sent to the controller with each claim.</summary>
        public bool IsReady(string? blockReason)
        {
            return this.Armed && this.SessionId != null && this.ControllerInstanceId != null && this.current == null && this.pending == null && blockReason == null;
        }

        public ClaimRequest BuildClaim(string? blockReason, string bridgeVersion)
        {
            return new ClaimRequest
            {
                SessionId = this.SessionId,
                AutomationEnabled = this.Armed,
                Ready = this.IsReady(blockReason),
                RunningActionId = this.RunningActionId,
                StopSeqSeen = this.StopSeqSeen,
                ControllerInstanceId = this.ControllerInstanceId,
                BlockReason = blockReason,
                BridgeVersion = bridgeVersion
            };
        }

        public void Handle(ControlEvent controlEvent)
        {
            switch (controlEvent)
            {
                case ControllerInstanceEvent e:
                    if (this.ControllerInstanceId != null && this.ControllerInstanceId != e.InstanceId)
                    {
                        // The controller restarted and has no record of what we're running: stop, don't replay.
                        string? dropped = this.RunningActionId;
                        this.Abort(ActionStates.Cancelled, "controller_restarted", sendReport: false);
                        this.log(
                            TransportLogLevel.Warn,
                            dropped != null ? $"Controller restarted; dropped action {dropped} without replaying it." : "Controller restarted."
                        );
                    }
                    this.ControllerInstanceId = e.InstanceId;
                    this.StopSeqSeen = 0;
                    break;

                case StopEvent e:
                    if (e.InstanceId != this.ControllerInstanceId || e.StopSeq <= this.StopSeqSeen)
                        break;
                    this.StopSeqSeen = e.StopSeq;
                    this.Abort(ActionStates.Cancelled, $"stopped: {e.Reason ?? "stop requested"}");
                    break;

                case ActionEvent e:
                    this.Start(e);
                    break;

                case ReportRefusedEvent e:
                    if (e.ActionId == this.current?.Command.ActionId && e.Reason != "already_terminal")
                    {
                        // The controller no longer accepts reports for it (restart, lease expired): stop doing it.
                        this.Abort(ActionStates.Cancelled, $"controller_refused: {e.Reason}", sendReport: false);
                        this.log(TransportLogLevel.Warn, $"Controller stopped tracking action {e.ActionId} ({e.Reason}); cancelled it.");
                    }
                    break;
            }
        }

        /// <summary>Advance the running action by one game tick. Call once per UpdateTicking.</summary>
        public void Tick(double secondsSinceContact)
        {
            DateTime nowUtc = this.utcNow();

            if (this.pending is { } waiting)
            {
                string? waitBlock = this.game.GetMoveBlockReason();
                if (waitBlock == null)
                {
                    this.pending = null;
                    this.Begin(waiting.Command);
                }
                else if (!IsTransient(waitBlock) || nowUtc > waiting.DeadlineUtc || secondsSinceContact > this.options.LinkTimeoutSeconds)
                {
                    this.pending = null;
                    string reason = IsTransient(waitBlock) ? $"{waitBlock} (still blocked after {this.options.StartWaitSeconds:0.#}s)" : waitBlock;
                    this.ReportWithoutRun(waiting.Command, ActionStates.Rejected, reason);
                    return;
                }
                else
                    return;
            }

            Running? run = this.current;
            if (run == null || run.Direction == null)
                return;

            PlayerPosition position = this.game.ReadPosition();
            if (run.TicksPressed > 0)
            {
                // Position now reflects the game update that followed the previous press.
                run.StallTicks = Distance(position, run.LastPosition) < this.options.StallEpsilonPixels ? run.StallTicks + 1 : 0;
            }
            run.LastPosition = position;

            string? block = this.game.GetMoveBlockReason();
            if (block != null)
            {
                this.Finish(ActionStates.Cancelled, $"interrupted: {block}", position);
                return;
            }
            if (secondsSinceContact > this.options.LinkTimeoutSeconds)
            {
                this.Finish(ActionStates.Cancelled, "controller_unreachable", position);
                return;
            }
            if ((nowUtc - run.StartedAtUtc).TotalSeconds > this.options.MaxActionSeconds)
            {
                this.Finish(ActionStates.TimedOut, $"time_limit: exceeded {this.options.MaxActionSeconds:0.#}s", position);
                return;
            }
            if (position.LocationName != run.StartPosition.LocationName)
            {
                // Walked through a warp; stop pressing so the player doesn't keep walking on the new map.
                this.Finish(ActionStates.Succeeded, null, position);
                return;
            }
            if (run.TicksPressed >= this.options.StallGraceTicks && run.StallTicks >= this.options.StallTicks)
            {
                this.Finish(ActionStates.Failed, $"blocked: no movement for {run.StallTicks} ticks", position, blocked: true);
                return;
            }
            if (run.TicksPressed >= run.DurationTicks)
            {
                // Holding the key for the full duration does not mean the player went anywhere.
                bool blocked = Distance(position, run.StartPosition) < 1 || run.StallTicks >= this.options.StallTicks;
                if (blocked)
                    this.Finish(ActionStates.Failed, "blocked: player did not move", position, blocked: true);
                else
                    this.Finish(ActionStates.Succeeded, null, position);
                return;
            }

            string? error = this.game.PressDirection(run.Direction.Value);
            if (error != null)
            {
                this.Finish(ActionStates.Failed, error, position);
                return;
            }
            run.TicksPressed++;

            if ((nowUtc - run.LastHeartbeatUtc).TotalSeconds >= this.options.HeartbeatSeconds)
            {
                run.LastHeartbeatUtc = nowUtc;
                this.Send(run, ActionStates.Running, null, null, blocked: false);
            }
        }

        private void Start(ActionEvent e)
        {
            ActionCommand cmd = e.Command;
            string? problem =
                e.InstanceId != this.ControllerInstanceId ? "controller_instance_mismatch"
                : cmd.SessionId != this.SessionId ? "session_mismatch"
                : !this.Armed ? "automation_disabled"
                : this.current != null || this.pending != null ? "busy"
                : this.recentIds.Contains(cmd.ActionId) ? "duplicate_action_id"
                : null;

            Direction? direction = null;
            int duration = 0;
            if (problem == null)
            {
                switch (cmd.Type)
                {
                    case "move_direction":
                        direction = ParseDirection(cmd.Params.Direction);
                        duration = cmd.Params.DurationTicks ?? 0;
                        if (direction == null)
                            problem = "invalid_params: direction";
                        else if (duration < 1 || duration > this.options.MaxMoveTicks)
                            problem = $"invalid_params: duration_ticks must be 1-{this.options.MaxMoveTicks}";
                        break;
                    case "face_direction":
                        if (ParseDirection(cmd.Params.Direction) == null)
                            problem = "invalid_params: direction";
                        break;
                    case "select_slot":
                        if (cmd.Params.Slot is not (>= 0 and <= 11))
                            problem = "invalid_params: slot must be 0-11";
                        break;
                    default:
                        problem = $"unsupported_action_type: {cmd.Type}";
                        break;
                }
            }
            if (problem != null)
            {
                this.Remember(cmd.ActionId);
                this.ReportWithoutRun(cmd, ActionStates.Rejected, problem);
                return;
            }

            this.Remember(cmd.ActionId);
            string? block = this.game.GetMoveBlockReason();
            if (block != null)
            {
                if (IsTransient(block))
                {
                    this.pending = (cmd, this.utcNow().AddSeconds(this.options.StartWaitSeconds));
                    this.log(TransportLogLevel.Debug, $"Action {cmd.ActionId} waiting for '{block}' to clear");
                }
                else
                    this.ReportWithoutRun(cmd, ActionStates.Rejected, block);
                return;
            }

            this.Begin(cmd);
        }

        /// <summary>Start a validated action now that nothing blocks it.</summary>
        private void Begin(ActionCommand cmd)
        {
            DateTime now = this.utcNow();
            Direction? direction = cmd.Type == "move_direction" ? ParseDirection(cmd.Params.Direction) : null;
            int duration = cmd.Params.DurationTicks ?? 0;
            PlayerPosition start = this.game.ReadPosition();
            var run = new Running(cmd, now, start, direction, duration);
            this.current = run;
            this.Send(run, ActionStates.Running, null, null, blocked: false);
            this.log(TransportLogLevel.Debug, $"Started action {cmd.ActionId}: {Describe(cmd)}");

            if (direction != null)
                return; // advanced by Tick

            // Instant actions finish in the same tick.
            string? error = cmd.Type == "face_direction"
                ? this.game.Face(ParseDirection(cmd.Params.Direction)!.Value)
                : this.game.SelectSlot(cmd.Params.Slot!.Value);
            PlayerPosition end = this.game.ReadPosition();
            if (error == null)
                this.Finish(ActionStates.Succeeded, null, end);
            else
                this.Finish(ActionStates.Failed, error, end);
        }

        /// <summary>Report a final state for an action that never started running (rejected, or cancelled while waiting).</summary>
        private void ReportWithoutRun(ActionCommand cmd, string state, string reason)
        {
            this.report(new OutgoingReport(cmd.ActionId, new StatusReport
            {
                SessionId = cmd.SessionId,
                LeaseId = cmd.LeaseId,
                State = state,
                FailureReason = reason,
                Result = new ActionResult
                {
                    ActionId = cmd.ActionId, SessionId = cmd.SessionId, Type = cmd.Type,
                    FinishedAtUtc = this.utcNow(), FailureReason = reason
                }
            }));
            this.log(TransportLogLevel.Info, $"Action {cmd.ActionId} ({cmd.Type}) {state}: {reason}");
        }

        private static bool IsTransient(string block)
        {
            return Array.IndexOf(TransientBlocks, block) >= 0;
        }

        /// <summary>End the running or waiting action, if any, and report it unless the controller no longer knows it.</summary>
        private void Abort(string state, string reason, bool sendReport = true)
        {
            if (this.pending is { } waiting)
            {
                this.pending = null;
                if (sendReport)
                    this.ReportWithoutRun(waiting.Command, state, reason);
            }
            if (this.current == null)
                return;
            PlayerPosition? position = null;
            try
            {
                position = this.game.ReadPosition();
            }
            catch (Exception)
            {
                // Returning to title: the player may already be gone.
            }
            this.Finish(state, reason, position, sendReport: sendReport);
        }

        private void Finish(string state, string? reason, PlayerPosition? end, bool blocked = false, bool sendReport = true)
        {
            Running? run = this.current;
            if (run == null)
                return;
            this.current = null;

            if (sendReport)
                this.Send(run, state, reason, end, blocked, finished: true);
            this.log(
                state == ActionStates.Succeeded ? TransportLogLevel.Debug : TransportLogLevel.Info,
                $"Action {run.Command.ActionId} ({run.Command.Type}) {state}{(reason != null ? $": {reason}" : "")} after {run.TicksPressed} tick(s)"
            );
        }

        private void Send(Running run, string state, string? reason, PlayerPosition? end, bool blocked, bool finished = false)
        {
            ActionCommand cmd = run.Command;
            this.report(new OutgoingReport(cmd.ActionId, new StatusReport
            {
                SessionId = cmd.SessionId,
                LeaseId = cmd.LeaseId,
                State = state,
                FailureReason = reason,
                Result = new ActionResult
                {
                    ActionId = cmd.ActionId,
                    SessionId = cmd.SessionId,
                    Type = cmd.Type,
                    StartedAtUtc = run.StartedAtUtc,
                    FinishedAtUtc = finished ? this.utcNow() : null,
                    TicksExecuted = run.TicksPressed,
                    StartPosition = run.StartPosition,
                    EndPosition = end ?? (finished ? null : run.LastPosition),
                    // Null across a map change: pixel coordinates of different maps aren't comparable (and JSON has no Infinity).
                    DistancePixels = end != null && end.LocationName == run.StartPosition.LocationName ? Math.Round(Distance(end, run.StartPosition), 2) : null,
                    Blocked = blocked,
                    FailureReason = reason
                }
            }));
        }

        private void Remember(string actionId)
        {
            if (!this.recentIds.Add(actionId))
                return;
            this.recentOrder.Enqueue(actionId);
            while (this.recentOrder.Count > this.options.RecentIdLimit)
                this.recentIds.Remove(this.recentOrder.Dequeue());
        }

        private static Direction? ParseDirection(string? value)
        {
            return value switch
            {
                "up" => Direction.Up,
                "right" => Direction.Right,
                "down" => Direction.Down,
                "left" => Direction.Left,
                _ => null
            };
        }

        private static double Distance(PlayerPosition a, PlayerPosition b)
        {
            if (a.LocationName != b.LocationName)
                return double.PositiveInfinity;
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static string Describe(ActionCommand cmd)
        {
            return cmd.Type switch
            {
                "move_direction" => $"move {cmd.Params.Direction} for {cmd.Params.DurationTicks} ticks",
                "face_direction" => $"face {cmd.Params.Direction}",
                "select_slot" => $"select slot {cmd.Params.Slot}",
                _ => cmd.Type
            };
        }

        private sealed class Running
        {
            public Running(ActionCommand command, DateTime startedAtUtc, PlayerPosition start, Direction? direction, int durationTicks)
            {
                this.Command = command;
                this.StartedAtUtc = startedAtUtc;
                this.LastHeartbeatUtc = startedAtUtc;
                this.StartPosition = start;
                this.LastPosition = start;
                this.Direction = direction;
                this.DurationTicks = durationTicks;
            }

            public ActionCommand Command { get; }
            public DateTime StartedAtUtc { get; }
            public PlayerPosition StartPosition { get; }
            public Direction? Direction { get; }
            public int DurationTicks { get; }
            public DateTime LastHeartbeatUtc { get; set; }
            public PlayerPosition LastPosition { get; set; }
            public int TicksPressed { get; set; }
            public int StallTicks { get; set; }
        }
    }
}
