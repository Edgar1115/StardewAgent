using System;
using System.Collections.Generic;
using StardewAgentBridge.State;
using StardewValley;

namespace StardewAgentBridge.Observers
{
    /// <summary>Reads backpack and hotbar. Main thread only.</summary>
    internal static class InventoryObserver
    {
        /// <summary>Hotbar width; Toolbar.draw and Farmer.shiftToolbar both use 12.</summary>
        public const int ToolbarSize = 12;

        public static InventoryState ReadInventory(Farmer player)
        {
            int capacity = player.MaxItems;
            int count = player.Items.Count;
            var slots = new List<InventorySlotState>(capacity);
            int used = 0;

            for (int i = 0; i < capacity; i++)
            {
                Item? item = i < count ? player.Items[i] : null;
                if (item != null)
                    used++;
                slots.Add(new InventorySlotState { SlotIndex = i, IsEmpty = item == null, Item = ItemMapper.From(item, i) });
            }

            return new InventoryState
            {
                Capacity = capacity,
                UsedSlots = used,
                EmptySlots = capacity - used,
                Slots = slots
            };
        }

        /// <summary>
        /// Switching hotbar rows rotates Farmer.Items in place (Farmer.shiftToolbar), so the visible hotbar is
        /// always the first 12 entries of the current order. Read them fresh each time; never cache slot positions.
        /// </summary>
        public static ToolbarState ReadToolbar(Farmer player)
        {
            int count = player.Items.Count;
            int size = Math.Min(ToolbarSize, Math.Max(count, 0));
            var slots = new List<InventorySlotState>(size);
            for (int i = 0; i < size; i++)
            {
                Item? item = player.Items[i];
                slots.Add(new InventorySlotState { SlotIndex = i, IsEmpty = item == null, Item = ItemMapper.From(item, i) });
            }

            Item? held = player.CurrentItem;
            bool temporary = held != null && player.TemporaryItem != null && ReferenceEquals(held, player.TemporaryItem);
            int selected = player.CurrentToolIndex;

            return new ToolbarState
            {
                Size = size,
                Slots = slots,
                SelectedIndex = selected,
                SelectedItem = ItemMapper.From(held, temporary ? null : selected),
                SelectedItemId = held?.QualifiedItemId,
                SelectedItemIsTemporary = temporary,
                ItemStowed = player.netItemStowed.Value
            };
        }
    }
}
