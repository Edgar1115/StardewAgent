using System.Collections.Generic;

namespace StardewAgentBridge.State
{
    /// <summary>Quest log (Farmer.questLog) and active special orders (FarmerTeam.specialOrders), kept separate.</summary>
    public sealed record QuestState
    {
        public IReadOnlyList<QuestEntryState> Quests { get; init; } = new List<QuestEntryState>();
        public IReadOnlyList<SpecialOrderEntryState> SpecialOrders { get; init; } = new List<SpecialOrderEntryState>();
    }

    /// <summary>A regular quest (StardewValley.Quests.Quest and subclasses).</summary>
    public sealed record QuestEntryState
    {
        public string Kind { get; init; } = "quest";
        /// <summary>Data/Quests ID; null for generated quests (e.g. daily help-wanted quests).</summary>
        public string? QuestId { get; init; }
        /// <summary>Quest.questType: 1 basic, 2 crafting, 3 item_delivery, 4 monster, 5 socialize, 6 location, 7 fishing, 8 building, 9 harvest, 10 resource, 11 weeding.</summary>
        public int QuestType { get; init; }
        public string QuestTypeName { get; init; } = "";
        /// <summary>C# class name, e.g. "SlayMonsterQuest".</summary>
        public string QuestClass { get; init; } = "";
        public string Title { get; init; } = "";
        public string? Description { get; init; }
        public string? Objective { get; init; }
        public bool Completed { get; init; }
        public bool IsDailyQuest { get; init; }
        public bool IsTimed { get; init; }
        /// <summary>Days remaining for timed quests; null for untimed quests.</summary>
        public int? DaysLeft { get; init; }
        /// <summary>Counted progress for quest types that track it; null otherwise.</summary>
        public QuestProgressState? Progress { get; init; }
        /// <summary>NPC the quest is for (internal name), when the quest type has one.</summary>
        public string? TargetNpc { get; init; }
        /// <summary>Gold reward; null when the quest has no money reward.</summary>
        public int? MoneyReward { get; init; }
        public string? RewardDescription { get; init; }
        public bool CanBeCancelled { get; init; }
        /// <summary>Hidden quests are not shown in the in-game quest log.</summary>
        public bool IsHidden { get; init; }
    }

    public sealed record QuestProgressState
    {
        /// <summary>Done so far; null when the game does not track it (e.g. item delivery is all-or-nothing).</summary>
        public int? Current { get; init; }
        public int? Required { get; init; }
        /// <summary>What is counted: qualified item ID, monster name, or "npcs" for socialize quests.</summary>
        public string? Subject { get; init; }
    }

    /// <summary>A special order (StardewValley.SpecialOrders.SpecialOrder) from the town or Qi board.</summary>
    public sealed record SpecialOrderEntryState
    {
        public string Kind { get; init; } = "special_order";
        public string OrderKey { get; init; } = "";
        /// <summary>Raw SpecialOrder.orderType: null for the town board, "Qi" for Mr. Qi's orders.</summary>
        public string? OrderType { get; init; }
        public string Title { get; init; } = "";
        public string? Description { get; init; }
        /// <summary>"InProgress", "Complete" or "Failed".</summary>
        public string Status { get; init; } = "";
        public bool Completed { get; init; }
        /// <summary>Days remaining while in progress; null once complete or failed.</summary>
        public int? DaysLeft { get; init; }
        /// <summary>Due date as WorldDate.TotalDays.</summary>
        public int DueDateTotalDays { get; init; }
        /// <summary>QuestDuration, e.g. "Week", "Month".</summary>
        public string Duration { get; init; } = "";
        public IReadOnlyList<SpecialOrderObjectiveState> Objectives { get; init; } = new List<SpecialOrderObjectiveState>();
        public IReadOnlyList<SpecialOrderRewardState> Rewards { get; init; } = new List<SpecialOrderRewardState>();
    }

    public sealed record SpecialOrderObjectiveState
    {
        public string Description { get; init; } = "";
        public int Current { get; init; }
        public int Required { get; init; }
        public bool Completed { get; init; }
    }

    public sealed record SpecialOrderRewardState
    {
        /// <summary>Reward class name, e.g. "MoneyReward", "GemsReward", "MailReward".</summary>
        public string Type { get; init; } = "";
        /// <summary>Gold (MoneyReward) or Qi gems (GemsReward); null for rewards without an amount.</summary>
        public int? Amount { get; init; }
    }
}
