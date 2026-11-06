namespace StardewAgentBridge.State
{
    /// <summary>Local player's position, vitals and skills.</summary>
    public sealed record PlayerState
    {
        public string Name { get; init; } = "";
        public int TileX { get; init; }
        public int TileY { get; init; }
        /// <summary>0 = up, 1 = right, 2 = down, 3 = left.</summary>
        public int FacingDirection { get; init; }
        /// <summary>"up", "right", "down" or "left".</summary>
        public string Facing { get; init; } = "";
        public float Stamina { get; init; }
        public int MaxStamina { get; init; }
        public int Health { get; init; }
        public int MaxHealth { get; init; }
        public SkillsState Skills { get; init; } = new();
    }

    public sealed record SkillsState
    {
        public SkillLevelState Farming { get; init; } = new();
        public SkillLevelState Fishing { get; init; } = new();
        public SkillLevelState Foraging { get; init; } = new();
        public SkillLevelState Mining { get; init; } = new();
        public SkillLevelState Combat { get; init; } = new();
        public SkillLevelState Luck { get; init; } = new();
    }

    public sealed record SkillLevelState
    {
        /// <summary>Earned level, without buffs (e.g. Farmer.farmingLevel).</summary>
        public int Base { get; init; }
        /// <summary>Level including food/buff bonuses (e.g. Farmer.FarmingLevel).</summary>
        public int Effective { get; init; }
    }
}
