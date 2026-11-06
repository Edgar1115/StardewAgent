using StardewAgentBridge.State;
using StardewValley;

namespace StardewAgentBridge.Observers
{
    /// <summary>Reads position, vitals and skills. Main thread only.</summary>
    internal static class PlayerObserver
    {
        // Game1.up = 0, right = 1, down = 2, left = 3.
        private static readonly string[] FacingNames = { "up", "right", "down", "left" };

        public static PlayerState Read(Farmer player)
        {
            var tile = player.TilePoint;
            int facing = player.FacingDirection;

            return new PlayerState
            {
                Name = player.Name ?? "",
                TileX = tile.X,
                TileY = tile.Y,
                FacingDirection = facing,
                Facing = facing >= 0 && facing < FacingNames.Length ? FacingNames[facing] : "unknown",
                Stamina = player.Stamina,
                MaxStamina = player.MaxStamina,
                Health = player.health,
                MaxHealth = player.maxHealth,
                Skills = new SkillsState
                {
                    Farming = new SkillLevelState { Base = player.farmingLevel.Value, Effective = player.FarmingLevel },
                    Fishing = new SkillLevelState { Base = player.fishingLevel.Value, Effective = player.FishingLevel },
                    Foraging = new SkillLevelState { Base = player.foragingLevel.Value, Effective = player.ForagingLevel },
                    Mining = new SkillLevelState { Base = player.miningLevel.Value, Effective = player.MiningLevel },
                    Combat = new SkillLevelState { Base = player.combatLevel.Value, Effective = player.CombatLevel },
                    Luck = new SkillLevelState { Base = player.luckLevel.Value, Effective = player.LuckLevel }
                }
            };
        }
    }
}
