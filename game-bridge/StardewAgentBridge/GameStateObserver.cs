using System;
using System.Collections.Generic;
using System.Linq;
using StardewAgentBridge.Observers;
using StardewAgentBridge.Serialization;
using StardewModdingAPI;
using StardewValley;

namespace StardewAgentBridge
{
    /// <summary>
    /// Reads Game1 on the SMAPI main thread and publishes the result as an immutable snapshot.
    /// <see cref="Latest"/> and <see cref="LatestJson"/> are safe to read from any thread;
    /// <see cref="Refresh"/> and <see cref="Clear"/> are main-thread only.
    /// </summary>
    public sealed class GameStateObserver
    {
        private readonly IMonitor monitor;
        private readonly Func<ModConfig> config;
        private int mainThreadId = -1;
        private long revision;
        private volatile GameStateSnapshot latest = GameStateSnapshot.NotReady;
        private volatile string latestJson;
        private Dictionary<string, string> latestFlat;

        public GameStateObserver(IMonitor monitor, Func<ModConfig> config)
        {
            this.monitor = monitor;
            this.config = config;
            this.latestJson = StateJson.Serialize(GameStateSnapshot.NotReady);
            this.latestFlat = StateJson.Flatten(this.latestJson);
        }

        /// <summary>Most recent snapshot; <see cref="GameStateSnapshot.NotReady"/> when no save is loaded.</summary>
        public GameStateSnapshot Latest => this.latest;

        /// <summary>Most recent snapshot as snake_case JSON.</summary>
        public string LatestJson => this.latestJson;

        /// <summary>Record the game thread. Call from a SMAPI event handler.</summary>
        public void BindToCurrentThread()
        {
            this.mainThreadId = Environment.CurrentManagedThreadId;
        }

        /// <summary>Re-read the given sections from the game; other sections keep their last values.</summary>
        public GameStateSnapshot Refresh(StateSections sections)
        {
            if (!this.IsOnMainThread())
                return this.latest;

            if (!Context.IsWorldReady)
            {
                this.Clear();
                return this.latest;
            }

            Farmer? player = Game1.player;
            if (player == null)
                return this.latest;

            GameStateSnapshot current = this.latest;
            // Coming from not-ready (save load, return from title) needs every section.
            if (!current.WorldReady)
                sections = StateSections.All;

            DateTime now = DateTime.UtcNow;
            var next = current with { WorldReady = true, CapturedAtUtc = now };
            next = this.ReadSection(next, sections, StateSections.World, s => s with { World = WorldObserver.Read(player) });
            next = this.ReadSection(next, sections, StateSections.Player, s => s with { Player = PlayerObserver.Read(player) });
            next = this.ReadSection(next, sections, StateSections.Inventory, s => s with { Inventory = InventoryObserver.ReadInventory(player) });
            next = this.ReadSection(next, sections, StateSections.Toolbar, s => s with { Toolbar = InventoryObserver.ReadToolbar(player) });
            next = this.ReadSection(next, sections, StateSections.Equipment, s => s with { Equipment = EquipmentObserver.Read(player) });
            next = this.ReadSection(next, sections, StateSections.Finance, s => s with { Finance = FinanceObserver.Read(player) });
            next = this.ReadSection(next, sections, StateSections.Quests, s => s with { Quests = QuestObserver.Read(player) });

            this.Publish(next, now);
            return this.latest;
        }

        /// <summary>Drop the current save's state, e.g. after returning to title.</summary>
        public void Clear()
        {
            if (!this.IsOnMainThread())
                return;

            if (this.latest.WorldReady)
            {
                DateTime now = DateTime.UtcNow;
                this.Publish(GameStateSnapshot.NotReady with { CapturedAtUtc = now }, now);
            }
        }

        /// <summary>Read one section; on failure keep its previous value so one bad read can't blank the snapshot.</summary>
        private GameStateSnapshot ReadSection(GameStateSnapshot snapshot, StateSections requested, StateSections section, Func<GameStateSnapshot, GameStateSnapshot> read)
        {
            if ((requested & section) == 0)
                return snapshot;

            try
            {
                return read(snapshot);
            }
            catch (Exception ex)
            {
                this.monitor.LogOnce($"Failed to read {section} state: {ex}", LogLevel.Error);
                return snapshot;
            }
        }

        /// <summary>Bump the revision and log a change summary only if some value differs (timestamps ignored).</summary>
        private void Publish(GameStateSnapshot candidate, DateTime now)
        {
            string probeJson = StateJson.Serialize(candidate with { Revision = 0, CapturedAtUtc = default, ChangedAtUtc = default });
            Dictionary<string, string> flat = StateJson.Flatten(probeJson);
            List<string> changes = Diff(this.latestFlat, flat);

            if (changes.Count == 0)
            {
                // Values unchanged: keep revision, only refresh the capture time.
                this.latest = this.latest with { CapturedAtUtc = candidate.CapturedAtUtc };
                return;
            }

            GameStateSnapshot published = candidate with { Revision = ++this.revision, ChangedAtUtc = now };
            this.latestFlat = flat;
            this.latestJson = StateJson.Serialize(published);
            this.latest = published;

            if (this.config().DebugMode)
                this.LogChanges(published.Revision, changes);
        }

        private void LogChanges(long rev, List<string> changes)
        {
            int max = Math.Max(1, this.config().MaxChangesPerLog);
            string text = string.Join("; ", changes.Take(max));
            if (changes.Count > max)
                text += $"; +{changes.Count - max} more";
            this.monitor.Log($"[rev {rev}] {text}", LogLevel.Debug);
        }

        private static List<string> Diff(Dictionary<string, string> before, Dictionary<string, string> after)
        {
            var changes = new List<string>();
            foreach ((string path, string value) in after)
            {
                if (!before.TryGetValue(path, out string? old))
                    changes.Add($"{path}: (none) -> {value}");
                else if (old != value)
                    changes.Add($"{path}: {old} -> {value}");
            }
            foreach (string path in before.Keys)
            {
                if (!after.ContainsKey(path))
                    changes.Add($"{path}: removed");
            }
            return changes;
        }

        private bool IsOnMainThread()
        {
            if (this.mainThreadId == -1 || Environment.CurrentManagedThreadId == this.mainThreadId)
                return true;

            this.monitor.LogOnce("GameStateObserver called off the main thread; ignoring. Read Latest instead.", LogLevel.Error);
            return false;
        }
    }
}
