using System;
using StardewAgentBridge.Actions;
using StardewAgentBridge.Observers;
using StardewAgentBridge.Transport;
using StardewModdingAPI;
using StardewModdingAPI.Events;

namespace StardewAgentBridge
{
    /// <summary>
    /// SMAPI entry point. Pushes game state to the local controller and, once the player arms automation,
    /// executes basic controller actions (move_direction, face_direction, select_slot, stop) on the main thread.
    /// </summary>
    public class ModEntry : Mod
    {
        /// <summary>Sections that change from moment to moment (position, vitals, held item, gold, shipping bin).</summary>
        private const StateSections DynamicSections = StateSections.World | StateSections.Player | StateSections.Toolbar | StateSections.Finance;

        private ModConfig config = new();
        private GameStateObserver observer = null!;
        /// <summary>Null when the transport is disabled or misconfigured.</summary>
        private ObservationPublisher? publisher;
        /// <summary>Null when actions are disabled or the transport is off.</summary>
        private AutomationController? automation;

        /// <summary>Latest game state, kept current while a save is loaded. Safe to read from any thread.</summary>
        public GameStateSnapshot LatestSnapshot => this.observer.Latest;

        /// <summary>Latest game state as snake_case JSON. Safe to read from any thread.</summary>
        public string LatestSnapshotJson => this.observer.LatestJson;

        /// <summary>Register events only; Game1 state is not ready here.</summary>
        public override void Entry(IModHelper helper)
        {
            this.config = helper.ReadConfig<ModConfig>();
            this.observer = new GameStateObserver(this.Monitor, () => this.config);
            this.publisher = this.CreatePublisher(out Uri? controllerUrl, out TimeSpan timeout);
            if (this.publisher != null && controllerUrl != null)
            {
                if (this.config.ActionsEnabled)
                {
                    this.automation = new AutomationController(helper, this.Monitor, () => this.config, controllerUrl, timeout, this.ModManifest.Version.ToString());
                    this.Monitor.Log(
                        $"Actions enabled: polling {this.automation.ClaimUri}. Automation is OFF until you press {this.config.ToggleAutomationKey} or run 'agent_control on'; {this.config.EmergencyStopKey} = emergency stop.",
                        LogLevel.Info
                    );
                }
                else
                    this.Monitor.Log("Actions disabled (ActionsEnabled=false).", LogLevel.Info);
            }

            helper.Events.GameLoop.GameLaunched += this.OnGameLaunched;
            helper.Events.GameLoop.SaveLoaded += this.OnSaveLoaded;
            helper.Events.GameLoop.DayStarted += this.OnDayStarted;
            helper.Events.GameLoop.TimeChanged += this.OnTimeChanged;
            helper.Events.GameLoop.OneSecondUpdateTicked += this.OnOneSecondUpdateTicked;
            helper.Events.GameLoop.ReturnedToTitle += this.OnReturnedToTitle;
            helper.Events.Player.InventoryChanged += this.OnInventoryChanged;
            helper.Events.GameLoop.UpdateTicking += this.OnUpdateTicking;
            helper.Events.Input.ButtonPressed += this.OnButtonPressed;

            helper.ConsoleCommands.Add(
                "agent_state",
                "Print the latest StardewAgentBridge snapshot.\n\nUsage: agent_state [json]\n- json: print the full snake_case JSON.",
                this.OnStateCommand
            );
            helper.ConsoleCommands.Add(
                "agent_control",
                "Control StardewAgent automation.\n\nUsage: agent_control [on|off|stop|status]\n- on: arm automation (single-player, allowed saves only)\n- off: manual takeover; cancel and clear pending actions\n- stop: emergency stop (same as the emergency stop key)\n- status: show automation and controller link state",
                (command, args) =>
                {
                    if (this.automation == null)
                        this.Monitor.Log("Actions are disabled (ActionsEnabled=false or transport off).", LogLevel.Warn);
                    else
                        this.automation.OnCommand(command, args);
                }
            );
        }

        private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
        {
            this.observer.BindToCurrentThread();
            this.Monitor.Log("Mod loaded successfully", LogLevel.Info);
        }

        private ObservationPublisher? CreatePublisher(out Uri? url, out TimeSpan timeout)
        {
            timeout = TimeSpan.FromSeconds(Math.Clamp(this.config.RequestTimeoutSeconds, 0.2, 30));
            url = null;
            if (!this.config.TransportEnabled)
            {
                this.Monitor.Log("Transport disabled (TransportEnabled=false); snapshots stay local.", LogLevel.Info);
                return null;
            }

            if (!Uri.TryCreate(this.config.ControllerUrl, UriKind.Absolute, out url) || !ObservationPublisher.IsLoopbackHttpUrl(url))
            {
                url = null;
                this.Monitor.Log(
                    $"Transport disabled: ControllerUrl '{this.config.ControllerUrl}' must be http://127.0.0.1:<port> (or another loopback address).",
                    LogLevel.Error
                );
                return null;
            }

            var created = new ObservationPublisher(url, timeout, this.ModManifest.Version.ToString());
            this.Monitor.Log($"Transport enabled: pushing snapshots to {created.Endpoint} every {this.PushIntervalSeconds}s.", LogLevel.Info);
            return created;
        }

        private int PushIntervalSeconds => Math.Max(1, this.config.PushIntervalSeconds);

        /// <summary>Hand the latest snapshot to the transport and surface its queued log lines. Main thread.</summary>
        private void Push()
        {
            if (this.publisher == null)
                return;

            string? opened = this.publisher.Publish(this.observer.Latest);
            if (opened != null)
                this.Monitor.Log($"Transport session {opened} started.", LogLevel.Info);
            this.DrainTransportLogs();
        }

        private void DrainTransportLogs()
        {
            this.publisher?.DrainLogs((level, message) =>
                this.Monitor.Log(
                    message,
                    level switch
                    {
                        TransportLogLevel.Debug => LogLevel.Debug,
                        TransportLogLevel.Info => LogLevel.Info,
                        TransportLogLevel.Warn => LogLevel.Warn,
                        _ => LogLevel.Error
                    }
                )
            );
        }

        private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
        {
            if (!Context.IsWorldReady)
            {
                this.Monitor.Log("SaveLoaded fired but world is not ready; skipping state read.", LogLevel.Warn);
                return;
            }

            GameStateSnapshot s = this.observer.Refresh(StateSections.All);
            this.Push();
            if (!s.WorldReady || s.World == null || s.Player == null)
            {
                this.Monitor.Log("World loaded but player state is unavailable.", LogLevel.Warn);
                return;
            }

            this.Monitor.Log("World loaded.", LogLevel.Info);
            this.Monitor.Log($"Date: Year {s.World.Year}, {s.World.Season} {s.World.DayOfMonth} ({s.World.DayOfWeek})", LogLevel.Info);
            this.Monitor.Log($"Season: {s.World.Season}", LogLevel.Info);
            this.Monitor.Log($"Time: {s.World.TimeText} ({s.World.TimeOfDay}), weather {s.World.Weather ?? "(unknown)"}", LogLevel.Info);
            this.Monitor.Log($"Location: {s.World.LocationName} (map {s.World.MapPath})", LogLevel.Info);
            this.Monitor.Log($"Tile: ({s.Player.TileX}, {s.Player.TileY}) facing {s.Player.Facing}", LogLevel.Info);
            this.Monitor.Log($"Stamina: {s.Player.Stamina:0.#} / {s.Player.MaxStamina}, health {s.Player.Health} / {s.Player.MaxHealth}", LogLevel.Info);
            if (s.Inventory != null && s.Finance != null && s.Quests != null)
                this.Monitor.Log(
                    $"Inventory {s.Inventory.UsedSlots}/{s.Inventory.Capacity}, money {s.Finance.Money}g ({s.Finance.WalletScope}), "
                    + $"quests {s.Quests.Quests.Count}, special orders {s.Quests.SpecialOrders.Count}",
                    LogLevel.Info
                );
        }

        private void OnDayStarted(object? sender, DayStartedEventArgs e)
        {
            if (!Context.IsWorldReady)
                return;

            // New day: weather, quests, money after shipping and stamina all reset.
            GameStateSnapshot s = this.observer.Refresh(StateSections.All);
            if (s.World != null)
                this.Monitor.Log(
                    $"New day: Year {s.World.Year}, {s.World.Season} {s.World.DayOfMonth} ({s.World.DayOfWeek}), time {s.World.TimeText}",
                    LogLevel.Info
                );
        }

        private void OnTimeChanged(object? sender, TimeChangedEventArgs e)
        {
            if (!Context.IsWorldReady)
                return;

            if (this.config.DebugMode)
                this.Monitor.Log($"Time changed: {WorldObserver.FormatTime(e.OldTime)} -> {WorldObserver.FormatTime(e.NewTime)}", LogLevel.Debug);
            this.observer.Refresh(StateSections.World | StateSections.Player);
        }

        private void OnInventoryChanged(object? sender, InventoryChangedEventArgs e)
        {
            if (!Context.IsWorldReady || !e.IsLocalPlayer)
                return;

            // Equipping moves items out of the backpack, so equipment is re-read too.
            this.observer.Refresh(StateSections.Inventory | StateSections.Toolbar | StateSections.Equipment | StateSections.Finance);
        }

        private void OnOneSecondUpdateTicked(object? sender, OneSecondUpdateTickedEventArgs e)
        {
            if (!Context.IsWorldReady)
            {
                // Title screen: nothing to push, but keep reporting transport status (e.g. delivery of the final world_ready=false).
                this.DrainTransportLogs();
                return;
            }

            // Every few seconds re-read everything, catching changes with no SMAPI event
            // (equipment swaps without an inventory change, quest progress, upgrades, toolbar row shifts).
            uint interval = (uint)System.Math.Max(1, this.config.FullRefreshIntervalSeconds) * 60;
            this.observer.Refresh(e.IsMultipleOf(interval) ? StateSections.All : DynamicSections);

            // Serialized here on the main thread; the sender thread only sees the finished bytes.
            if (e.IsMultipleOf((uint)this.PushIntervalSeconds * 60))
                this.Push();
            else
                this.DrainTransportLogs();
        }

        private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
        {
            this.observer.Clear();
            string? session = this.publisher?.SessionId;
            this.publisher?.EndSession(this.observer.Latest);
            this.Monitor.Log(
                session != null
                    ? $"Returned to title; snapshot cleared (world_ready=false), transport session {session} closed."
                    : "Returned to title; snapshot cleared (world_ready=false).",
                LogLevel.Info
            );
        }

        /// <summary>Main thread, before the game reads input: apply controller messages and hold keys for the running action.</summary>
        private void OnUpdateTicking(object? sender, UpdateTickingEventArgs e)
        {
            this.automation?.OnUpdateTicking(this.publisher?.SessionId);
        }

        private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
        {
            this.automation?.OnButtonPressed(e);
        }

        /// <summary>Game exit: stop automation, tell the controller the world is gone (best effort, short wait) and release the HTTP clients.</summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.automation?.Shutdown();
                this.automation = null;
                this.publisher?.Shutdown(TimeSpan.FromSeconds(1));
                this.publisher = null;
            }
            base.Dispose(disposing);
        }

        private void OnStateCommand(string command, string[] args)
        {
            if (args.Length > 0 && args[0] == "json")
            {
                this.Monitor.Log(Serialization.StateJson.Serialize(this.observer.Latest, indented: true), LogLevel.Info);
                return;
            }

            GameStateSnapshot s = this.observer.Latest;
            if (!s.WorldReady || s.World == null || s.Player == null)
            {
                this.Monitor.Log($"world_ready=false (revision {s.Revision})", LogLevel.Info);
                return;
            }

            var w = s.World;
            var p = s.Player;
            this.Monitor.Log(
                $"world_ready=true revision={s.Revision} changed={s.ChangedAtUtc:HH:mm:ss}Z captured={s.CapturedAtUtc:HH:mm:ss}Z\n"
                + $"  world: Y{w.Year} {w.Season} {w.DayOfMonth} {w.DayOfWeek} {w.TimeText} | weather {w.Weather} | {w.LocationName} [{w.MapPath}] | paused={w.IsPaused} time_passing={w.TimePassing}\n"
                + $"  player: ({p.TileX}, {p.TileY}) {p.Facing} | stamina {p.Stamina:0.#}/{p.MaxStamina} | health {p.Health}/{p.MaxHealth}\n"
                + $"  toolbar: index {s.Toolbar?.SelectedIndex} holding {s.Toolbar?.SelectedItemId ?? "(nothing)"}\n"
                + $"  inventory: {s.Inventory?.UsedSlots}/{s.Inventory?.Capacity} used\n"
                + $"  money: {s.Finance?.Money}g ({s.Finance?.WalletScope}) | team earned {s.Finance?.TotalMoneyEarned}g | bin est. {s.Finance?.ShippingBinEstimatedValue}g\n"
                + $"  quests: {s.Quests?.Quests.Count} | special orders: {s.Quests?.SpecialOrders.Count}",
                LogLevel.Info
            );
        }
    }
}
