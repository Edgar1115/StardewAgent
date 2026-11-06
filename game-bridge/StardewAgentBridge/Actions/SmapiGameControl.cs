using Microsoft.Xna.Framework.Input;
using StardewAgentBridge.Transport;
using StardewModdingAPI;
using StardewValley;

namespace StardewAgentBridge.Actions
{
    /// <summary>
    /// <see cref="IGameControl"/> over SMAPI and Game1. Main thread only. Movement goes through
    /// <see cref="IInputHelper.Press"/> with the player's own key bindings, so the game handles it exactly like
    /// a held key (collisions, speed, animations); nothing writes the player's position directly.
    /// </summary>
    internal sealed class SmapiGameControl : IGameControl
    {
        private readonly IInputHelper input;

        public SmapiGameControl(IInputHelper input)
        {
            this.input = input;
        }

        public string? GetBlockReason()
        {
            if (!Context.IsWorldReady || Game1.player == null)
                return "world_not_ready";
            if (Context.IsMultiplayer)
                return "multiplayer_not_supported";
            // Game1._update skips UpdateControlInput (and pauses, by default) while the window is unfocused,
            // so a pressed key would do nothing.
            if (!Game1.game1.IsActiveNoOverlay)
                return "game_window_inactive";
            if (Game1.paused || Game1.HostPaused)
                return "paused";
            if (Game1.activeClickableMenu != null)
                return $"menu_open: {Game1.activeClickableMenu.GetType().Name}";
            if (Game1.dialogueUp)
                return "dialogue_open";
            if (Game1.eventUp)
                return "event_active";
            if (Game1.globalFade || Game1.fadeToBlack)
                return "screen_fading";
            if (Game1.freezeControls)
                return "controls_frozen";
            if (!Context.IsPlayerFree)
                return "player_busy";
            return null;
        }

        public string? GetMoveBlockReason()
        {
            string? reason = this.GetBlockReason();
            if (reason != null)
                return reason;
            if (Game1.player.UsingTool)
                return "using_tool";
            if (!Context.CanPlayerMove)
                return "player_cannot_move";
            return null;
        }

        public PlayerPosition ReadPosition()
        {
            Farmer player = Game1.player;
            var tile = player.TilePoint;
            return new PlayerPosition
            {
                LocationName = player.currentLocation?.NameOrUniqueName ?? "",
                TileX = tile.X,
                TileY = tile.Y,
                X = player.Position.X,
                Y = player.Position.Y,
                FacingDirection = player.FacingDirection
            };
        }

        public string? PressDirection(Direction direction)
        {
            // The player's current bindings (Options > Controls), not a hard-coded WASD.
            InputButton[] bindings = direction switch
            {
                Direction.Up => Game1.options.moveUpButton,
                Direction.Right => Game1.options.moveRightButton,
                Direction.Down => Game1.options.moveDownButton,
                _ => Game1.options.moveLeftButton
            };
            foreach (InputButton binding in bindings)
            {
                if (binding.key == Keys.None)
                    continue;
                // Released again on the next input tick; the executor presses it every tick it wants it held.
                this.input.Press(binding.key.ToSButton());
                return null;
            }
            return $"no_keyboard_binding: move {direction.ToString().ToLowerInvariant()}";
        }

        public string? Face(Direction direction)
        {
            Game1.player.faceDirection((int)direction);
            return null;
        }

        public string? SelectSlot(int slot)
        {
            Farmer player = Game1.player;
            if (slot < 0 || slot >= player.Items.Count)
                return $"slot_out_of_range: {slot}";
            // Same field the number keys and scroll wheel set.
            player.CurrentToolIndex = slot;
            return null;
        }
    }
}
