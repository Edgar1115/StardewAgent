using System.Collections.Generic;

namespace StardewAgentBridge.State
{
    /// <summary>Serializable copy of one item. Never holds a reference to the game's Item.</summary>
    public sealed record ItemState
    {
        /// <summary>Inventory slot, or null for equipped items and the held item.</summary>
        public int? SlotIndex { get; init; }
        /// <summary>Globally unique item ID, e.g. "(O)24", "(T)Hoe", "(W)0". Use this, not ParentSheetIndex.</summary>
        public string QualifiedItemId { get; init; } = "";
        /// <summary>Unqualified ID within its type, e.g. "24".</summary>
        public string ItemId { get; init; } = "";
        /// <summary>Internal (English) name.</summary>
        public string Name { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public int Stack { get; init; }
        /// <summary>0 = normal, 1 = silver, 2 = gold, 4 = iridium.</summary>
        public int Quality { get; init; }
        /// <summary>Readable type: object, big_craftable, tool, weapon, ring, hat, shirt, pants, boots, trinket, furniture, ...</summary>
        public string ItemType { get; init; } = "";
        /// <summary>Type prefix, e.g. "(O)", "(T)", "(W)".</summary>
        public string TypeDefinitionId { get; init; } = "";
        public int Category { get; init; }
        /// <summary>Tool upgrade level for "(T)" tools (0 = basic ... 4 = iridium; fishing rods use it as rod tier); null otherwise.</summary>
        public int? UpgradeLevel { get; init; }
        /// <summary>Component rings when this is a Combined Ring; null otherwise.</summary>
        public IReadOnlyList<ItemState>? CombinedRings { get; init; }
    }

    /// <summary>One unlocked inventory slot; <see cref="Item"/> is null when empty.</summary>
    public sealed record InventorySlotState
    {
        public int SlotIndex { get; init; }
        public bool IsEmpty { get; init; }
        public ItemState? Item { get; init; }
    }
}
