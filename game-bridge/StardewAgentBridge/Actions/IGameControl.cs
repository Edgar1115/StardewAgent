using StardewAgentBridge.Transport;

namespace StardewAgentBridge.Actions
{
    /// <summary>
    /// The executor's only window into the game, so its logic can be tested without SMAPI.
    /// Every member is called on the main thread.
    /// </summary>
    public interface IGameControl
    {
        /// <summary>Why no automated action may run right now (title screen, menu, dialogue, unfocused window...), or null.</summary>
        string? GetBlockReason();

        /// <summary>Like <see cref="GetBlockReason"/>, plus reasons the player can't move or act (using a tool, eating...).</summary>
        string? GetMoveBlockReason();

        PlayerPosition ReadPosition();

        /// <summary>Press the key bound to this direction for the current tick. Returns an error when it can't (no keyboard binding).</summary>
        string? PressDirection(Direction direction);

        string? Face(Direction direction);

        string? SelectSlot(int slot);
    }

    public enum Direction
    {
        Up = 0,
        Right = 1,
        Down = 2,
        Left = 3
    }
}
