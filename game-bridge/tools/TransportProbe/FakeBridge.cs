using System;
using System.Diagnostics;
using System.Threading;
using StardewAgentBridge;
using StardewAgentBridge.Actions;
using StardewAgentBridge.State;
using StardewAgentBridge.Transport;

namespace TransportProbe
{
    /// <summary>
    /// The mod's real observation publisher, action channel and executor, driven by a 60 Hz simulated game
    /// instead of SMAPI. Automation is armed at start (standing in for the player pressing F7).
    /// Simulated map: the farmer starts at tile (64, 15) and a wall stops upward movement at tile row 12.
    /// </summary>
    internal static class FakeBridge
    {
        public static int Run(string url, int seconds)
        {
            var controller = new Uri(url);
            var game = new SimGame();
            var publisher = new ObservationPublisher(controller, TimeSpan.FromSeconds(2), "0.3.0-probe");
            var channel = new ActionChannel(controller, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1));
            void Log(TransportLogLevel level, string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff} {level}] {message}");
            var executor = new ActionExecutor(game, channel.Report, Log);

            var clock = Stopwatch.StartNew();
            int totalTicks = seconds * 60;
            for (int tick = 0; tick < totalTicks; tick++)
            {
                // UpdateTicking: same order as AutomationController.OnUpdateTicking.
                executor.SetSession(publisher.SessionId);
                if (publisher.SessionId != null && !executor.Armed && tick < 120)
                {
                    executor.Arm();
                    Log(TransportLogLevel.Info, "armed (simulated F7)");
                }
                while (channel.TryDequeueEvent(out ControlEvent? e))
                    if (e != null)
                        executor.Handle(e);
                executor.Tick(channel.SecondsSinceContact);
                game.Update();
                channel.UpdateStatus(executor.BuildClaim(publisher.SessionId == null ? "no_session" : game.GetMoveBlockReason(), "0.3.0-probe"));

                // OneSecondUpdateTicked: push the snapshot with the simulated position.
                if (tick % 60 == 0)
                {
                    publisher.Publish(game.Snapshot(tick));
                    publisher.DrainLogs(Log);
                }
                channel.DrainLogs(Log);

                // Hold 60 Hz real time.
                long target = (long)((tick + 1) * 1000.0 / 60);
                long wait = target - clock.ElapsedMilliseconds;
                if (wait > 0)
                    Thread.Sleep((int)wait);
            }

            executor.SetSession(null);
            publisher.EndSession();
            Log(TransportLogLevel.Info, "returned to title (simulated)");
            Thread.Sleep(1500);
            channel.DrainLogs(Log);
            channel.Shutdown(TimeSpan.FromMilliseconds(500));
            publisher.Shutdown(TimeSpan.FromSeconds(1));
            publisher.DrainLogs(Log);
            return 0;
        }

        private sealed class SimGame : IGameControl
        {
            private const float WallY = 12 * 64;
            private float x = 64 * 64, y = 15 * 64;
            private int facing = 2, slot;
            private Direction? pressed;

            public string? GetBlockReason() => null;
            public string? GetMoveBlockReason() => null;

            public PlayerPosition ReadPosition() => new()
            {
                LocationName = "Farm", X = this.x, Y = this.y, TileX = (int)(this.x / 64), TileY = (int)(this.y / 64), FacingDirection = this.facing
            };

            public string? PressDirection(Direction direction)
            {
                this.pressed = direction;
                return null;
            }

            public string? Face(Direction direction)
            {
                this.facing = (int)direction;
                return null;
            }

            public string? SelectSlot(int value)
            {
                this.slot = value;
                return null;
            }

            /// <summary>Farmer walk speed is about 5 px per tick.</summary>
            public void Update()
            {
                if (this.pressed is not Direction d)
                    return;
                this.pressed = null;
                this.facing = (int)d;
                switch (d)
                {
                    case Direction.Up: this.y = Math.Max(WallY, this.y - 5); break;
                    case Direction.Down: this.y += 5; break;
                    case Direction.Left: this.x -= 5; break;
                    case Direction.Right: this.x += 5; break;
                }
            }

            public GameStateSnapshot Snapshot(int tick)
            {
                GameStateSnapshot s = Program.Sample(DateTime.UtcNow, tick / 60);
                var pos = this.ReadPosition();
                string[] names = { "up", "right", "down", "left" };
                return s with
                {
                    Player = s.Player! with { TileX = pos.TileX, TileY = pos.TileY, FacingDirection = this.facing, Facing = names[this.facing] },
                    Toolbar = s.Toolbar! with { SelectedIndex = this.slot, SelectedItem = null, SelectedItemId = null }
                };
            }
        }
    }
}
