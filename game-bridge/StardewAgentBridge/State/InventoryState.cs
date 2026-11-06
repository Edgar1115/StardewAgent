using System.Collections.Generic;

namespace StardewAgentBridge.State
{
    /// <summary>Backpack contents, all unlocked slots including empty ones.</summary>
    public sealed record InventoryState
    {
        /// <summary>Unlocked slot count (Farmer.MaxItems): 12, 24 or 36.</summary>
        public int Capacity { get; init; }
        public int UsedSlots { get; init; }
        public int EmptySlots { get; init; }
        public IReadOnlyList<InventorySlotState> Slots { get; init; } = new List<InventorySlotState>();
    }

    /// <summary>
    /// The hotbar. In 1.6.15 switching toolbar rows (Farmer.shiftToolbar) rotates Farmer.Items in place,
    /// so the visible hotbar is always Items[0..11] of the *current* order; inventory slot indexes shift with it.
    /// </summary>
    public sealed record ToolbarState
    {
        public int Size { get; init; }
        /// <summary>Hotbar slots in on-screen order (left to right).</summary>
        public IReadOnlyList<InventorySlotState> Slots { get; init; } = new List<InventorySlotState>();
        /// <summary>Selected hotbar index (Farmer.CurrentToolIndex).</summary>
        public int SelectedIndex { get; init; }
        /// <summary>What the player actually holds (Farmer.CurrentItem); null when empty-handed or stowed.</summary>
        public ItemState? SelectedItem { get; init; }
        public string? SelectedItemId { get; init; }
        /// <summary>True when the held item is a temporary item (e.g. during an event), not from the hotbar.</summary>
        public bool SelectedItemIsTemporary { get; init; }
        /// <summary>True when the player has stowed (put away) the selected item.</summary>
        public bool ItemStowed { get; init; }
    }
}
