using System.Collections.Generic;
using StardewAgentBridge.State;
using StardewValley;
using StardewValley.Objects;

namespace StardewAgentBridge.Observers
{
    /// <summary>Copies a game Item into a plain <see cref="ItemState"/>. Main thread only.</summary>
    internal static class ItemMapper
    {
        public static ItemState? From(Item? item, int? slotIndex = null)
        {
            if (item == null)
                return null;

            string typeId = item.TypeDefinitionId ?? "";
            return new ItemState
            {
                SlotIndex = slotIndex,
                QualifiedItemId = item.QualifiedItemId ?? "",
                ItemId = item.ItemId ?? "",
                Name = item.Name ?? "",
                DisplayName = item.DisplayName ?? "",
                Stack = item.Stack,
                Quality = item.Quality,
                ItemType = GetItemType(item, typeId),
                TypeDefinitionId = typeId,
                Category = item.Category,
                // Only real tools have upgrade tiers; weapons are also Tool subclasses but use "(W)".
                UpgradeLevel = item is Tool tool && typeId == ItemRegistry.type_tool ? tool.UpgradeLevel : null,
                CombinedRings = item is CombinedRing combined ? ReadCombinedRings(combined) : null
            };
        }

        private static string GetItemType(Item item, string typeId)
        {
            // Rings share the "(O)" prefix with ordinary objects.
            if (item is Ring)
                return "ring";

            return typeId switch
            {
                ItemRegistry.type_object => "object",
                ItemRegistry.type_bigCraftable => "big_craftable",
                ItemRegistry.type_tool => "tool",
                ItemRegistry.type_weapon => "weapon",
                ItemRegistry.type_hat => "hat",
                ItemRegistry.type_shirt => "shirt",
                ItemRegistry.type_pants => "pants",
                ItemRegistry.type_boots => "boots",
                ItemRegistry.type_trinket => "trinket",
                ItemRegistry.type_furniture => "furniture",
                ItemRegistry.type_floorpaper => "flooring",
                ItemRegistry.type_wallpaper => "wallpaper",
                ItemRegistry.type_mannequin => "mannequin",
                _ => typeId.Length > 0 ? typeId : "unknown"
            };
        }

        private static List<ItemState> ReadCombinedRings(CombinedRing ring)
        {
            var result = new List<ItemState>();
            for (int i = 0; i < ring.combinedRings.Count; i++)
            {
                ItemState? part = From(ring.combinedRings[i]);
                if (part != null)
                    result.Add(part);
            }
            return result;
        }
    }
}
