using StardewModdingAPI.Utilities;

namespace StardewAgentBridge
{
    /// <summary>User settings, stored in Mods/StardewAgentBridge/config.json.</summary>
    public sealed class ModConfig
    {
        /// <summary>Log time changes and snapshot field changes to the SMAPI console.</summary>
        public bool DebugMode { get; set; } = true;

        /// <summary>Seconds between full re-reads of every section (inventory, equipment, quests) as a consistency check.</summary>
        public int FullRefreshIntervalSeconds { get; set; } = 5;

        /// <summary>Max changed fields listed per debug log line; the rest are summarized as "+N more".</summary>
        public int MaxChangesPerLog { get; set; } = 12;

        /// <summary>Push snapshots to the local Python controller.</summary>
        public bool TransportEnabled { get; set; } = true;

        /// <summary>Controller base URL. Must be http on a loopback address; anything else disables the transport.</summary>
        public string ControllerUrl { get; set; } = "http://127.0.0.1:8765";

        /// <summary>Real-time seconds between pushes while a save is loaded.</summary>
        public int PushIntervalSeconds { get; set; } = 1;

        /// <summary>Per-request timeout (connect + response), in seconds.</summary>
        public double RequestTimeoutSeconds { get; set; } = 2;

        /// <summary>Poll the controller for actions. Automation itself still starts disarmed every time a save loads.</summary>
        public bool ActionsEnabled { get; set; } = true;

        /// <summary>Milliseconds between action polls while automation is armed (1 s while disarmed).</summary>
        public int ActionPollIntervalMs { get; set; } = 200;

        /// <summary>
        /// Comma-separated save names automation may be armed in, e.g. "Testone". Matches the save folder name
        /// (Constants.SaveFolderName) exactly or its "&lt;name&gt;_" prefix. Empty = automation can't be armed anywhere.
        /// </summary>
        public string AutomationAllowedSaves { get; set; } = "Testone";

        /// <summary>Turn automation on/off in game. Off on every save load.</summary>
        public KeybindList ToggleAutomationKey { get; set; } = KeybindList.Parse("F7");

        /// <summary>Emergency stop: cancels the running action, disarms automation and clears the controller queue. Works without the controller.</summary>
        public KeybindList EmergencyStopKey { get; set; } = KeybindList.Parse("F8");
    }
}
