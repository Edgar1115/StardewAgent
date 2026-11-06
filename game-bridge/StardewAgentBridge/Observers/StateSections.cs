using System;

namespace StardewAgentBridge.Observers
{
    /// <summary>Snapshot sections that can be refreshed independently.</summary>
    [Flags]
    public enum StateSections
    {
        None = 0,
        World = 1 << 0,
        Player = 1 << 1,
        Inventory = 1 << 2,
        Toolbar = 1 << 3,
        Equipment = 1 << 4,
        Finance = 1 << 5,
        Quests = 1 << 6,
        All = World | Player | Inventory | Toolbar | Equipment | Finance | Quests
    }
}
