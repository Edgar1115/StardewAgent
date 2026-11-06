using System;
using StardewAgentBridge.State;

namespace StardewAgentBridge
{
    /// <summary>Immutable read-only view of the game state. Sections are null while no save is loaded.</summary>
    public sealed record GameStateSnapshot
    {
        /// <summary>Snapshot exposed when no save is loaded (main menu, loading).</summary>
        public static readonly GameStateSnapshot NotReady = new() { WorldReady = false };

        public bool WorldReady { get; init; }

        /// <summary>Increments only when some state value changes, so consumers can detect fresh data.</summary>
        public long Revision { get; init; }
        /// <summary>Last time any section was read from the game.</summary>
        public DateTime CapturedAtUtc { get; init; }
        /// <summary>Last time <see cref="Revision"/> changed.</summary>
        public DateTime ChangedAtUtc { get; init; }

        public WorldState? World { get; init; }
        public PlayerState? Player { get; init; }
        public InventoryState? Inventory { get; init; }
        public ToolbarState? Toolbar { get; init; }
        public EquipmentState? Equipment { get; init; }
        public FinanceState? Finance { get; init; }
        public QuestState? Quests { get; init; }
    }
}
