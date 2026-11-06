using System.Collections.Generic;
using StardewAgentBridge.State;
using StardewValley;

namespace StardewAgentBridge.Observers
{
    /// <summary>Reads worn equipment slots. Main thread only.</summary>
    internal static class EquipmentObserver
    {
        public static EquipmentState Read(Farmer player)
        {
            // InventoryPage shows the trinket slot only when this stat is non-zero.
            bool trinketsUnlocked = player.stats.Get("trinketSlots") != 0;
            var trinkets = new List<ItemState?>();
            if (trinketsUnlocked)
            {
                for (int i = 0; i < Farmer.MaximumTrinkets; i++)
                    trinkets.Add(i < player.trinketItems.Count ? ItemMapper.From(player.trinketItems[i]) : null);
            }

            return new EquipmentState
            {
                Hat = ItemMapper.From(player.hat.Value),
                Shirt = ItemMapper.From(player.shirtItem.Value),
                Pants = ItemMapper.From(player.pantsItem.Value),
                Boots = ItemMapper.From(player.boots.Value),
                LeftRing = ItemMapper.From(player.leftRing.Value),
                RightRing = ItemMapper.From(player.rightRing.Value),
                TrinketSlotsUnlocked = trinketsUnlocked,
                Trinkets = trinkets
            };
        }
    }
}
