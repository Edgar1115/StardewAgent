using System;
using System.Linq;
using StardewAgentBridge.Transport;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace StardewAgentBridge.Actions
{
    /// <summary>
    /// Ties the action channel (background HTTP) to the executor (main thread) and the player's safety controls:
    /// automation starts disarmed, only the player arms it (F7 / agent_control on), and F8 stops it locally
    /// without needing the controller. All members run on the main thread.
    /// </summary>
    internal sealed class AutomationController
    {
        private readonly IModHelper helper;
        private readonly IMonitor monitor;
        private readonly Func<ModConfig> config;
        private readonly ActionChannel channel;
        private readonly SmapiGameControl game;
        private readonly ActionExecutor executor;
        private readonly string bridgeVersion;

        public AutomationController(IModHelper helper, IMonitor monitor, Func<ModConfig> config, Uri controllerUrl, TimeSpan requestTimeout, string bridgeVersion)
        {
            this.helper = helper;
            this.monitor = monitor;
            this.config = config;
            this.bridgeVersion = bridgeVersion;
            this.game = new SmapiGameControl(helper.Input);
            this.channel = new ActionChannel(
                controllerUrl,
                requestTimeout,
                armedPollInterval: TimeSpan.FromMilliseconds(Math.Clamp(config().ActionPollIntervalMs, 50, 1000)),
                idlePollInterval: TimeSpan.FromSeconds(1)
            );
            this.executor = new ActionExecutor(this.game, this.channel.Report, this.Log);
        }

        public Uri ClaimUri => this.channel.ClaimUri;

        /// <summary>Per tick, before the game update: apply controller messages, then hold keys for the running action.</summary>
        public void OnUpdateTicking(string? sessionId)
        {
            this.executor.SetSession(Context.IsWorldReady ? sessionId : null);

            while (this.channel.TryDequeueEvent(out ControlEvent? controlEvent))
            {
                if (controlEvent != null)
                    this.executor.Handle(controlEvent);
            }

            this.executor.Tick(this.channel.SecondsSinceContact);

            string? block = this.executor.SessionId == null ? "no_session" : this.game.GetMoveBlockReason();
            this.channel.UpdateStatus(this.executor.BuildClaim(block, this.bridgeVersion));
            this.channel.DrainLogs(this.Log);
        }

        public void OnButtonPressed(ButtonPressedEventArgs e)
        {
            ModConfig cfg = this.config();
            if (cfg.EmergencyStopKey.JustPressed())
            {
                this.EmergencyStop();
                return;
            }
            if (cfg.ToggleAutomationKey.JustPressed())
            {
                if (this.executor.Armed)
                    this.TakeOver("manual takeover (toggle key)");
                else
                    this.TryArm();
            }
        }

        /// <summary>agent_control on|off|stop|status</summary>
        public void OnCommand(string command, string[] args)
        {
            switch (args.FirstOrDefault()?.ToLowerInvariant())
            {
                case "on":
                    this.TryArm();
                    break;
                case "off":
                    this.TakeOver("manual takeover (agent_control off)");
                    break;
                case "stop":
                    this.EmergencyStop();
                    break;
                default:
                    double contact = this.channel.SecondsSinceContact;
                    this.monitor.Log(
                        $"automation={(this.executor.Armed ? "ARMED" : "off")} session={this.executor.SessionId ?? "(none)"} "
                        + $"running={this.executor.RunningActionId ?? "(none)"} block={(this.executor.SessionId == null ? "no_session" : this.game.GetMoveBlockReason() ?? "(none)")} "
                        + $"controller={this.executor.ControllerInstanceId ?? "(never contacted)"} last_contact={(double.IsInfinity(contact) ? "never" : $"{contact:0.0}s ago")} "
                        + $"stop_seq_seen={this.executor.StopSeqSeen} dropped_reports={this.channel.DroppedReports}",
                        LogLevel.Info
                    );
                    break;
            }
        }

        public void Shutdown()
        {
            this.executor.Disarm("game_exit");
            this.channel.Shutdown(TimeSpan.FromMilliseconds(500));
        }

        private void TryArm()
        {
            string? problem = this.ArmProblem();
            if (problem != null)
            {
                this.monitor.Log($"Automation NOT armed: {problem}", LogLevel.Warn);
                this.Hud($"Agent automation not armed: {problem}", HUDMessage.error_type);
                return;
            }
            this.executor.Arm();
            string stopKey = this.config().EmergencyStopKey.ToString();
            this.monitor.Log($"Automation ARMED for save {Constants.SaveFolderName}. Press {stopKey} to stop at any time.", LogLevel.Alert);
            this.Hud($"Agent automation ON ({stopKey} to stop)", HUDMessage.newQuest_type);
        }

        private string? ArmProblem()
        {
            if (!Context.IsWorldReady)
                return "no save loaded";
            if (Context.IsMultiplayer)
                return "multiplayer/split-screen is not supported";
            if (this.executor.SessionId == null)
                return "transport session not open (is TransportEnabled on?)";

            string folder = Constants.SaveFolderName ?? "";
            string[] allowed = this.config().AutomationAllowedSaves
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            bool ok = allowed.Any(name => folder.Equals(name, StringComparison.OrdinalIgnoreCase) || folder.StartsWith(name + "_", StringComparison.OrdinalIgnoreCase));
            if (!ok)
                return $"save '{folder}' is not in AutomationAllowedSaves ({(allowed.Length == 0 ? "empty" : string.Join(", ", allowed))})";
            return null;
        }

        /// <summary>Local and immediate: works even if the controller is down. Also asks the controller to drop its queue.</summary>
        private void EmergencyStop()
        {
            bool wasActive = this.executor.Armed || this.executor.RunningActionId != null;
            this.executor.Disarm("emergency_stop");
            this.channel.RequestControllerStop("emergency stop", "bridge_emergency_stop");
            this.monitor.Log("EMERGENCY STOP: automation disarmed, running action cancelled, controller asked to clear its queue.", LogLevel.Alert);
            if (wasActive)
                this.Hud("Agent automation STOPPED", HUDMessage.error_type);
        }

        private void TakeOver(string reason)
        {
            this.executor.Disarm("manual_takeover");
            this.channel.RequestControllerStop(reason, "bridge_manual_takeover");
            this.monitor.Log($"Automation off ({reason}); pending actions cleared.", LogLevel.Info);
            this.Hud("Agent automation OFF", HUDMessage.error_type);
        }

        private void Hud(string message, int type)
        {
            if (Context.IsWorldReady)
                Game1.addHUDMessage(new HUDMessage(message, type));
        }

        private void Log(TransportLogLevel level, string message)
        {
            this.monitor.Log(
                message,
                level switch
                {
                    TransportLogLevel.Debug => LogLevel.Debug,
                    TransportLogLevel.Info => LogLevel.Info,
                    TransportLogLevel.Warn => LogLevel.Warn,
                    _ => LogLevel.Error
                }
            );
        }
    }
}
