using System.Collections.Generic;

namespace StardewAgentBridge.State
{
    /// <summary>Worn equipment slots. Tools and weapons in the backpack are not equipment.</summary>
    public sealed record EquipmentState
    {
        public ItemState? Hat { get; init; }
        public ItemState? Shirt { get; init; }
        public ItemState? Pants { get; init; }
        public ItemState? Boots { get; init; }
        public ItemState? LeftRing { get; init; }
        public ItemState? RightRing { get; init; }
        /// <summary>Whether the 1.6 trinket slot is unlocked (stat "trinketSlots" != 0).</summary>
        public bool TrinketSlotsUnlocked { get; init; }
        /// <summary>One entry per unlocked trinket slot; null entries are empty slots.</summary>
        public IReadOnlyList<ItemState?> Trinkets { get; init; } = new List<ItemState?>();
    }
}
