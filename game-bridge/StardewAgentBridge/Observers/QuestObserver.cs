using System.Collections.Generic;
using StardewAgentBridge.State;
using StardewValley;
using StardewValley.Quests;
using StardewValley.SpecialOrders;
using StardewValley.SpecialOrders.Objectives;
using StardewValley.SpecialOrders.Rewards;

namespace StardewAgentBridge.Observers
{
    /// <summary>
    /// Reads the quest log and special orders. Main thread only.
    /// Avoids Quest.currentObjective / questDescription getters: in 1.6.15 they call reloadObjective/reloadDescription,
    /// which assign net fields (e.g. SlayMonsterQuest.objective, ResourceCollectionQuest.targetMessage).
    /// Reads the cached public fields instead, falling back to raw Data/Quests fields.
    /// </summary>
    internal static class QuestObserver
    {
        private static readonly string[] QuestTypeNames =
        {
            "unknown", "basic", "crafting", "item_delivery", "monster", "socialize",
            "location", "fishing", "building", "harvest", "resource", "weeding"
        };

        public static QuestState Read(Farmer player)
        {
            var quests = new List<QuestEntryState>();
            foreach (Quest? quest in player.questLog)
            {
                if (quest == null || quest.destroy.Value)
                    continue;
                quests.Add(ReadQuest(quest));
            }

            var orders = new List<SpecialOrderEntryState>();
            foreach (SpecialOrder? order in player.team.specialOrders)
            {
                // Same filter as QuestLog.GetAllQuests: only orders this player participates in.
                if (order == null || order.IsHidden())
                    continue;
                orders.Add(ReadSpecialOrder(order));
            }

            return new QuestState { Quests = quests, SpecialOrders = orders };
        }

        private static QuestEntryState ReadQuest(Quest quest)
        {
            string? id = NullIfEmpty(quest.id.Value);
            string[]? raw = id != null ? Quest.GetRawQuestFields(id) : null;
            int type = quest.questType.Value;
            bool timed = quest.IsTimedQuest();
            int money = quest.moneyReward.Value;

            return new QuestEntryState
            {
                QuestId = id,
                QuestType = type,
                QuestTypeName = type >= 0 && type < QuestTypeNames.Length ? QuestTypeNames[type] : "unknown",
                QuestClass = quest.GetType().Name,
                Title = quest.questTitle ?? "",
                Description = NullIfEmpty(quest._questDescription) ?? RawField(raw, 2),
                Objective = NullIfEmpty(quest._currentObjective) ?? RawField(raw, 3),
                Completed = quest.completed.Value,
                IsDailyQuest = quest.dailyQuest.Value,
                IsTimed = timed,
                DaysLeft = timed ? quest.GetDaysLeft() : null,
                Progress = ReadProgress(quest),
                TargetNpc = ReadTargetNpc(quest),
                MoneyReward = money > 0 ? money : null,
                RewardDescription = NullIfEmpty(quest.rewardDescription.Value),
                CanBeCancelled = quest.canBeCancelled.Value,
                IsHidden = quest.IsHidden()
            };
        }

        private static QuestProgressState? ReadProgress(Quest quest)
        {
            return quest switch
            {
                SlayMonsterQuest q => new QuestProgressState { Current = q.numberKilled.Value, Required = q.numberToKill.Value, Subject = NullIfEmpty(q.monsterName.Value) },
                ResourceCollectionQuest q => new QuestProgressState { Current = q.numberCollected.Value, Required = q.number.Value, Subject = Qualify(q.ItemId.Value) },
                FishingQuest q => new QuestProgressState { Current = q.numberFished.Value, Required = q.numberToFish.Value, Subject = Qualify(q.ItemId.Value) },
                // Delivery is all-or-nothing; the game tracks no partial count.
                ItemDeliveryQuest q => new QuestProgressState { Current = null, Required = q.number.Value, Subject = Qualify(q.ItemId.Value) },
                SocializeQuest q => new QuestProgressState { Current = q.total.Value - q.whoToGreet.Count, Required = q.total.Value, Subject = "npcs" },
                LostItemQuest q => new QuestProgressState { Current = q.itemFound.Value ? 1 : 0, Required = 1, Subject = Qualify(q.ItemId.Value) },
                SecretLostItemQuest q => new QuestProgressState { Current = q.itemFound.Value ? 1 : 0, Required = 1, Subject = Qualify(q.ItemId.Value) },
                ItemHarvestQuest q => new QuestProgressState { Subject = Qualify(q.ItemId.Value) },
                CraftingQuest q => new QuestProgressState { Subject = Qualify(q.ItemId.Value) },
                _ => null
            };
        }

        private static string? ReadTargetNpc(Quest quest)
        {
            return quest switch
            {
                SlayMonsterQuest q => NullIfEmpty(q.target.Value),
                ResourceCollectionQuest q => NullIfEmpty(q.target.Value),
                FishingQuest q => NullIfEmpty(q.target.Value),
                ItemDeliveryQuest q => NullIfEmpty(q.target.Value),
                LostItemQuest q => NullIfEmpty(q.npcName.Value),
                _ => null
            };
        }

        private static SpecialOrderEntryState ReadSpecialOrder(SpecialOrder order)
        {
            SpecialOrderStatus status = order.questState.Value;
            List<string> descriptions = order.GetObjectiveDescriptions();

            var objectives = new List<SpecialOrderObjectiveState>();
            for (int i = 0; i < order.objectives.Count; i++)
            {
                OrderObjective objective = order.objectives[i];
                objectives.Add(new SpecialOrderObjectiveState
                {
                    Description = i < descriptions.Count ? descriptions[i] : objective.GetDescription() ?? "",
                    Current = objective.GetCount(),
                    Required = objective.GetMaxCount(),
                    Completed = objective.IsComplete()
                });
            }

            var rewards = new List<SpecialOrderRewardState>();
            foreach (OrderReward reward in order.rewards)
            {
                rewards.Add(new SpecialOrderRewardState
                {
                    Type = reward.GetType().Name,
                    Amount = reward switch
                    {
                        MoneyReward m => m.GetRewardMoneyAmount(),
                        GemsReward g => g.amount.Value,
                        _ => null
                    }
                });
            }

            return new SpecialOrderEntryState
            {
                OrderKey = order.questKey.Value ?? "",
                OrderType = NullIfEmpty(order.orderType.Value),
                Title = order.GetName() ?? "",
                Description = NullIfEmpty(order.GetDescription()),
                Status = status.ToString(),
                Completed = status == SpecialOrderStatus.Complete,
                DaysLeft = status == SpecialOrderStatus.InProgress ? order.GetDaysLeft() : null,
                DueDateTotalDays = order.dueDate.Value,
                Duration = order.questDuration.Value.ToString(),
                Objectives = objectives,
                Rewards = rewards
            };
        }

        private static string? Qualify(string? itemId)
        {
            if (string.IsNullOrEmpty(itemId))
                return null;
            return ItemRegistry.QualifyItemId(itemId) ?? itemId;
        }

        private static string? RawField(string[]? fields, int index)
        {
            return fields != null && index < fields.Length ? NullIfEmpty(fields[index]) : null;
        }

        private static string? NullIfEmpty(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }
}
