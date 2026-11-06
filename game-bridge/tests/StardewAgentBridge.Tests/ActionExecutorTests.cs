using System;
using System.Collections.Generic;
using System.Linq;
using StardewAgentBridge.Actions;
using StardewAgentBridge.Transport;
using Xunit;

namespace StardewAgentBridge.Tests
{
    /// <summary>Simulated game: each Update() moves the farmer 4 px in the pressed direction unless a wall is hit.</summary>
    internal sealed class FakeGame : IGameControl
    {
        public string? Block;
        public string? MoveBlock = null;
        public float X = 1000, Y = 1000;
        public string Location = "Farm";
        public int Facing = 2;
        public int Slot;
        /// <summary>Walls: farmer can't go above this Y (pixels) when moving up.</summary>
        public float? WallY;
        public bool UnboundKeys;
        public readonly List<Direction> Presses = new();
        private Direction? pressedThisTick;

        public string? GetBlockReason() => this.Block;
        public string? GetMoveBlockReason() => this.Block ?? this.MoveBlock;

        public PlayerPosition ReadPosition() => new()
        {
            LocationName = this.Location, X = this.X, Y = this.Y, TileX = (int)(this.X / 64), TileY = (int)(this.Y / 64), FacingDirection = this.Facing
        };

        public string? PressDirection(Direction direction)
        {
            if (this.UnboundKeys)
                return "no_keyboard_binding: move up";
            this.Presses.Add(direction);
            this.pressedThisTick = direction;
            return null;
        }

        public string? Face(Direction direction)
        {
            this.Facing = (int)direction;
            return null;
        }

        public string? SelectSlot(int slot)
        {
            this.Slot = slot;
            return null;
        }

        /// <summary>The game update that follows UpdateTicking.</summary>
        public void Update()
        {
            if (this.pressedThisTick is not Direction d)
                return;
            this.pressedThisTick = null;
            this.Facing = (int)d;
            switch (d)
            {
                case Direction.Up:
                    this.Y = this.WallY is float wall ? Math.Max(wall, this.Y - 4) : this.Y - 4;
                    break;
                case Direction.Down:
                    this.Y += 4;
                    break;
                case Direction.Left:
                    this.X -= 4;
                    break;
                case Direction.Right:
                    this.X += 4;
                    break;
            }
        }
    }

    public class ActionExecutorTests
    {
        private const string Session = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string Instance = "controller-1";

        private readonly FakeGame game = new();
        private readonly List<OutgoingReport> reports = new();
        private readonly ActionExecutor executor;
        private DateTime now = new(2026, 11, 5, 10, 0, 0, DateTimeKind.Utc);
        private double contactAge;

        public ActionExecutorTests()
        {
            this.executor = new ActionExecutor(this.game, this.reports.Add, (_, _) => { }, utcNow: () => this.now);
            this.executor.SetSession(Session);
            this.executor.Handle(new ControllerInstanceEvent(Instance));
            this.executor.Arm();
        }

        private static ActionCommand Move(string id, string direction, int ticks, string session = Session) => new()
        {
            ActionId = id, SessionId = session, Type = "move_direction", LeaseId = "lease-" + id,
            Params = new ActionParams { Direction = direction, DurationTicks = ticks }
        };

        private void Give(ActionCommand cmd, string instance = Instance) => this.executor.Handle(new ActionEvent(instance, cmd));

        /// <summary>Run n game ticks: executor before the update, then the simulated update.</summary>
        private void RunTicks(int n)
        {
            for (int i = 0; i < n; i++)
            {
                this.executor.Tick(this.contactAge);
                this.game.Update();
                this.now = this.now.AddSeconds(1.0 / 60);
            }
        }

        private OutgoingReport Last => this.reports.Last();
        private string[] States => this.reports.Select(r => r.Report.State).ToArray();

        [Fact]
        public void Move_HoldsKeyForExactDuration_AndReportsPositions()
        {
            this.Give(Move("m1", "up", 10));
            Assert.Equal("m1", this.executor.RunningActionId);
            RunTicks(12);

            Assert.Null(this.executor.RunningActionId);
            Assert.Equal(10, this.game.Presses.Count);
            Assert.All(this.game.Presses, d => Assert.Equal(Direction.Up, d));
            Assert.Equal(new[] { "RUNNING", "SUCCEEDED" }, this.States);

            ActionResult result = this.Last.Report.Result!;
            Assert.Equal("m1", result.ActionId);
            Assert.Equal(Session, result.SessionId);
            Assert.Equal(10, result.TicksExecuted);
            Assert.Equal(1000f, result.StartPosition!.Y);
            Assert.Equal(960f, result.EndPosition!.Y);
            Assert.Equal(40, result.DistancePixels);
            Assert.False(result.Blocked);
            Assert.NotNull(result.StartedAtUtc);
            Assert.NotNull(result.FinishedAtUtc);
            Assert.Equal("lease-m1", this.Last.Report.LeaseId);
        }

        [Fact]
        public void Move_AgainstWall_FailsAsBlocked_InsteadOfSucceeding()
        {
            this.game.WallY = 1000; // already touching the wall
            this.Give(Move("m1", "up", 60));
            RunTicks(70);

            Assert.Equal("FAILED", this.Last.Report.State);
            Assert.StartsWith("blocked", this.Last.Report.FailureReason);
            Assert.True(this.Last.Report.Result!.Blocked);
            // Stall detection ended it early instead of holding the key for all 60 ticks.
            Assert.True(this.game.Presses.Count < 20, $"pressed {this.game.Presses.Count} ticks");
        }

        [Fact]
        public void Move_ShortDurationIntoWall_FailsAsBlocked()
        {
            this.game.WallY = 1000;
            this.Give(Move("m1", "up", 3));
            RunTicks(5);
            Assert.Equal("FAILED", this.Last.Report.State);
            Assert.Equal("blocked: player did not move", this.Last.Report.FailureReason);
        }

        [Fact]
        public void Move_HitsWallMidway_FailsWithActualEndPosition()
        {
            this.game.WallY = 980; // 5 ticks of room
            this.Give(Move("m1", "up", 60));
            RunTicks(70);
            Assert.Equal("FAILED", this.Last.Report.State);
            Assert.Equal(980f, this.Last.Report.Result!.EndPosition!.Y);
            Assert.Equal(20, this.Last.Report.Result.DistancePixels);
        }

        [Fact]
        public void Stop_InterruptsRunningMove()
        {
            this.Give(Move("m1", "right", 60));
            RunTicks(5);
            this.executor.Handle(new StopEvent(Instance, 1, "test"));
            RunTicks(5);

            Assert.Null(this.executor.RunningActionId);
            Assert.Equal(5, this.game.Presses.Count);
            Assert.Equal("CANCELLED", this.Last.Report.State);
            Assert.Equal("stopped: test", this.Last.Report.FailureReason);
            Assert.Equal(1, this.executor.StopSeqSeen);
        }

        [Fact]
        public void Stop_AlreadySeenOrFromOtherInstance_IsIgnored()
        {
            this.executor.Handle(new StopEvent(Instance, 1, "first"));
            this.Give(Move("m1", "right", 30));
            this.executor.Handle(new StopEvent(Instance, 1, "replayed"));
            this.executor.Handle(new StopEvent("other", 9, "foreign"));
            Assert.Equal("m1", this.executor.RunningActionId);
        }

        [Fact]
        public void EmergencyDisarm_CancelsAndRefusesNewActions()
        {
            this.Give(Move("m1", "down", 60));
            RunTicks(3);
            this.executor.Disarm("emergency_stop");
            RunTicks(3);

            Assert.Equal(3, this.game.Presses.Count);
            Assert.Equal("CANCELLED", this.Last.Report.State);
            Assert.Equal("emergency_stop", this.Last.Report.FailureReason);
            Assert.False(this.executor.Armed);

            this.Give(Move("m2", "down", 10));
            Assert.Equal("REJECTED", this.Last.Report.State);
            Assert.Equal("automation_disabled", this.Last.Report.FailureReason);
        }

        [Fact]
        public void MenuOpening_InterruptsMove()
        {
            this.Give(Move("m1", "left", 60));
            RunTicks(4);
            this.game.Block = "menu_open: GameMenu";
            RunTicks(10);

            Assert.Equal(4, this.game.Presses.Count);
            Assert.Equal("CANCELLED", this.Last.Report.State);
            Assert.Equal("interrupted: menu_open: GameMenu", this.Last.Report.FailureReason);
        }

        [Fact]
        public void TransientBlockAtStart_WaitsThenRuns()
        {
            // e.g. the click that refocuses the window also swings the tool.
            this.game.MoveBlock = "using_tool";
            this.Give(Move("m1", "down", 10));
            Assert.Empty(this.reports);
            Assert.True(this.executor.IsWaitingToStart);
            Assert.False(this.executor.BuildClaim(null, "t").Ready);
            RunTicks(20);
            Assert.Empty(this.game.Presses);

            this.game.MoveBlock = null;
            RunTicks(12);
            Assert.Equal(new[] { "RUNNING", "SUCCEEDED" }, this.States);
            Assert.Equal(10, this.game.Presses.Count);
        }

        [Fact]
        public void TransientBlockThatPersists_IsRejectedAfterWait()
        {
            this.game.MoveBlock = "using_tool";
            this.Give(Move("m1", "down", 10));
            RunTicks(150); // > 2 s
            Assert.Equal("REJECTED", this.Last.Report.State);
            Assert.StartsWith("using_tool (still blocked", this.Last.Report.FailureReason);
            Assert.False(this.executor.IsWaitingToStart);
            Assert.Empty(this.game.Presses);
        }

        [Fact]
        public void WaitingAction_IsCancelledByStop()
        {
            this.game.MoveBlock = "using_tool";
            this.Give(Move("m1", "down", 10));
            this.executor.Handle(new StopEvent(Instance, 1, "test"));
            Assert.Equal("CANCELLED", this.Last.Report.State);
            this.game.MoveBlock = null;
            RunTicks(10);
            Assert.Empty(this.game.Presses);
        }

        [Fact]
        public void BlockedAtStart_IsRejected()
        {
            this.game.Block = "dialogue_open";
            this.Give(Move("m1", "left", 10));
            Assert.Null(this.executor.RunningActionId);
            Assert.Equal("REJECTED", this.Last.Report.State);
            Assert.Equal("dialogue_open", this.Last.Report.FailureReason);
            Assert.Empty(this.game.Presses);
        }

        [Fact]
        public void LostController_CancelsMove()
        {
            this.Give(Move("m1", "left", 60));
            RunTicks(3);
            this.contactAge = 2.5;
            RunTicks(3);
            Assert.Equal(3, this.game.Presses.Count);
            Assert.Equal("controller_unreachable", this.Last.Report.FailureReason);
        }

        [Fact]
        public void RealTimeLimit_TimesOut()
        {
            this.Give(Move("m1", "left", 60));
            RunTicks(2);
            this.now = this.now.AddSeconds(5); // game stalled
            RunTicks(1);
            Assert.Equal("TIMED_OUT", this.Last.Report.State);
        }

        [Fact]
        public void WarpToNewMap_StopsPressing()
        {
            this.Give(Move("m1", "down", 60));
            RunTicks(5);
            this.game.Location = "BusStop";
            RunTicks(5);
            Assert.Equal(5, this.game.Presses.Count);
            Assert.Equal("SUCCEEDED", this.Last.Report.State);
            Assert.Null(this.Last.Report.Result!.DistancePixels);
        }

        [Theory]
        [InlineData("up", 0)]
        [InlineData("up", 61)]
        [InlineData("north", 10)]
        public void InvalidParams_AreRejected(string direction, int ticks)
        {
            this.Give(Move("m1", direction, ticks));
            Assert.Equal("REJECTED", this.Last.Report.State);
            Assert.StartsWith("invalid_params", this.Last.Report.FailureReason);
        }

        [Fact]
        public void UnsupportedType_IsRejected()
        {
            this.Give(new ActionCommand { ActionId = "x1", SessionId = Session, Type = "move_to", LeaseId = "l" });
            Assert.Equal("unsupported_action_type: move_to", this.Last.Report.FailureReason);
        }

        [Fact]
        public void OldSessionAction_IsRejected()
        {
            this.Give(Move("m1", "up", 10, session: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
            Assert.Equal("session_mismatch", this.Last.Report.FailureReason);
        }

        [Fact]
        public void ActionFromOtherControllerInstance_IsRejected()
        {
            this.Give(Move("m1", "up", 10), instance: "controller-0");
            Assert.Equal("controller_instance_mismatch", this.Last.Report.FailureReason);
        }

        [Fact]
        public void DuplicateActionId_IsNotExecutedTwice()
        {
            this.Give(Move("m1", "up", 5));
            RunTicks(8);
            int presses = this.game.Presses.Count;
            this.Give(Move("m1", "up", 5));
            RunTicks(8);
            Assert.Equal(presses, this.game.Presses.Count);
            Assert.Equal("duplicate_action_id", this.Last.Report.FailureReason);
        }

        [Fact]
        public void SecondActionWhileBusy_IsRejected()
        {
            this.Give(Move("m1", "up", 30));
            this.Give(Move("m2", "up", 30));
            Assert.Equal("m1", this.executor.RunningActionId);
            Assert.Equal("busy", this.Last.Report.FailureReason);
        }

        [Fact]
        public void SessionChange_CancelsAndDisarms()
        {
            this.Give(Move("m1", "up", 30));
            RunTicks(2);
            this.executor.SetSession(null);
            Assert.False(this.executor.Armed);
            Assert.Null(this.executor.RunningActionId);
            Assert.Equal("session_changed", this.Last.Report.FailureReason);
        }

        [Fact]
        public void ControllerRestart_CancelsWithoutReplay()
        {
            this.Give(Move("m1", "up", 30));
            RunTicks(2);
            int reportCount = this.reports.Count;
            this.executor.Handle(new ControllerInstanceEvent("controller-2"));
            RunTicks(5);

            Assert.Null(this.executor.RunningActionId);
            Assert.Equal(2, this.game.Presses.Count);
            // The new instance doesn't know the action, so no report is sent for it.
            Assert.Equal(reportCount, this.reports.Count);
            Assert.Equal(0, this.executor.StopSeqSeen);
        }

        [Fact]
        public void RefusedReport_StopsTheAction()
        {
            this.Give(Move("m1", "up", 30));
            RunTicks(2);
            this.executor.Handle(new ReportRefusedEvent("m1", 404, "unknown_action"));
            RunTicks(5);
            Assert.Null(this.executor.RunningActionId);
            Assert.Equal(2, this.game.Presses.Count);
        }

        [Fact]
        public void Ready_OnlyWhenArmedIdleAndUnblocked()
        {
            Assert.True(this.executor.BuildClaim(null, "t").Ready);
            Assert.False(this.executor.BuildClaim("menu_open: GameMenu", "t").Ready);
            this.Give(Move("m1", "up", 30));
            ClaimRequest busy = this.executor.BuildClaim(null, "t");
            Assert.False(busy.Ready);
            Assert.Equal("m1", busy.RunningActionId);
            this.executor.Disarm("off");
            Assert.False(this.executor.BuildClaim(null, "t").Ready);
            Assert.False(this.executor.BuildClaim(null, "t").AutomationEnabled);
        }

        [Fact]
        public void FaceAndSelect_AreInstant()
        {
            this.Give(new ActionCommand { ActionId = "f1", SessionId = Session, Type = "face_direction", LeaseId = "l1", Params = new ActionParams { Direction = "left" } });
            Assert.Equal(3, this.game.Facing);
            Assert.Equal("SUCCEEDED", this.Last.Report.State);
            Assert.Equal(3, this.Last.Report.Result!.EndPosition!.FacingDirection);

            this.Give(new ActionCommand { ActionId = "s1", SessionId = Session, Type = "select_slot", LeaseId = "l2", Params = new ActionParams { Slot = 4 } });
            Assert.Equal(4, this.game.Slot);
            Assert.Equal("SUCCEEDED", this.Last.Report.State);

            this.Give(new ActionCommand { ActionId = "s2", SessionId = Session, Type = "select_slot", LeaseId = "l3", Params = new ActionParams { Slot = 12 } });
            Assert.Equal("REJECTED", this.Last.Report.State);
            Assert.Empty(this.game.Presses);
        }

        [Fact]
        public void MissingKeyBinding_Fails()
        {
            this.game.UnboundKeys = true;
            this.Give(Move("m1", "up", 10));
            RunTicks(2);
            Assert.Equal("FAILED", this.Last.Report.State);
            Assert.StartsWith("no_keyboard_binding", this.Last.Report.FailureReason);
        }

        [Fact]
        public void LongMove_SendsRunningHeartbeats()
        {
            var sent = new List<OutgoingReport>();
            var fast = new ActionExecutor(this.game, sent.Add, (_, _) => { }, new ExecutorOptions { HeartbeatSeconds = 0.25 }, () => this.now);
            fast.SetSession(Session);
            fast.Handle(new ControllerInstanceEvent(Instance));
            fast.Arm();
            fast.Handle(new ActionEvent(Instance, Move("m1", "right", 60)));
            for (int i = 0; i < 62; i++)
            {
                fast.Tick(0);
                this.game.Update();
                this.now = this.now.AddSeconds(1.0 / 60);
            }
            Assert.True(sent.Count(r => r.Report.State == "RUNNING") >= 3);
            Assert.Equal("SUCCEEDED", sent.Last().Report.State);
        }
    }
}
