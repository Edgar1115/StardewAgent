using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using StardewAgentBridge;
using StardewAgentBridge.State;
using StardewAgentBridge.Transport;

namespace TransportProbe
{
    /// <summary>
    /// Usage:
    ///   fixtures &lt;dir&gt;                 write sample envelopes produced by the real serializer
    ///   send [url] [seconds]           publish a changing fake save at 1 Hz, then return to title
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            string mode = args.Length > 0 ? args[0] : "";
            switch (mode)
            {
                case "fixtures":
                    return WriteFixtures(args.Length > 1 ? args[1] : ".");
                case "send":
                    return Send(args.Length > 1 ? args[1] : "http://127.0.0.1:8765", args.Length > 2 ? int.Parse(args[2]) : 10);
                case "bridge":
                    return FakeBridge.Run(args.Length > 1 ? args[1] : "http://127.0.0.1:8765", args.Length > 2 ? int.Parse(args[2]) : 30);
                default:
                    Console.Error.WriteLine("usage: TransportProbe fixtures <dir> | send [url] [seconds] | bridge [url] [seconds]");
                    return 2;
            }
        }

        private static int WriteFixtures(string dir)
        {
            Directory.CreateDirectory(dir);
            DateTime now = new(2026, 11, 5, 8, 10, 0, 123, DateTimeKind.Utc);
            const string session = "0123456789abcdef0123456789abcdef";

            Write(dir, "ready.json", ObservationEnvelope.Build(session, 1, "0.2.0", Sample(now, tick: 0), now));
            // A fresh NotReady has default(DateTime) timestamps (no "Z"); the cleared one has real UTC times.
            Write(dir, "not_ready_initial.json", ObservationEnvelope.Build(session, 2, "0.2.0", GameStateSnapshot.NotReady, now));
            Write(dir, "not_ready_cleared.json", ObservationEnvelope.Build(session, 3, "0.2.0", GameStateSnapshot.NotReady with { Revision = 90, CapturedAtUtc = now, ChangedAtUtc = now }, now));
            return 0;
        }

        private static void Write(string dir, string name, byte[] payload)
        {
            File.WriteAllBytes(Path.Combine(dir, name), payload);
            Console.WriteLine($"wrote {name} ({payload.Length} bytes)");
        }

        private static int Send(string url, int seconds)
        {
            var publisher = new ObservationPublisher(new Uri(url), TimeSpan.FromSeconds(2), "0.2.0-probe");
            void Drain() => publisher.DrainLogs((level, message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss} {level}] {message}"));

            for (int tick = 0; tick < seconds; tick++)
            {
                DateTime now = DateTime.UtcNow;
                string? opened = publisher.Publish(Sample(now, tick));
                if (opened != null)
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss} Info] session {opened} started");
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss} Debug] queued sequence {publisher.Sequence}");
                Drain();
                Thread.Sleep(1000);
            }

            string? session = publisher.SessionId;
            publisher.EndSession(GameStateSnapshot.NotReady with { CapturedAtUtc = DateTime.UtcNow, ChangedAtUtc = DateTime.UtcNow });
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss} Info] session {session} closed (world_ready=false queued)");
            Thread.Sleep(1500);
            Drain();
            publisher.Shutdown(TimeSpan.FromSeconds(1));
            Drain();
            return 0;
        }

        /// <summary>A plausible day-one save; <paramref name="tick"/> advances time, position, money and the held tool.</summary>
        internal static GameStateSnapshot Sample(DateTime now, int tick)
        {
            int time = 600 + (tick / 6) * 100 + (tick % 6) * 10;
            string[] tools = { "(T)Axe", "(T)Hoe", "(T)WateringCan", "(T)Pickaxe", "(W)47" };
            int selected = tick % tools.Length;

            var inventorySlots = new List<InventorySlotState>();
            for (int i = 0; i < 12; i++)
            {
                ItemState? item = i < tools.Length ? Tool(tools[i], i) : i == 5 ? Obj("771", "Fiber", 3 + tick, i) : null;
                inventorySlots.Add(new InventorySlotState { SlotIndex = i, IsEmpty = item == null, Item = item });
            }

            return new GameStateSnapshot
            {
                WorldReady = true,
                Revision = 1 + tick,
                CapturedAtUtc = now,
                ChangedAtUtc = now,
                World = new WorldState
                {
                    Year = 1, Season = "spring", DayOfMonth = 1, DayOfWeek = "Monday", TotalDays = 0,
                    TimeOfDay = time, TimeText = $"{time / 100:00}:{time % 100:00}",
                    Weather = "Sun", WeatherTomorrow = "Sun", LocationContext = "Default",
                    LocationName = "Farm", LocationUniqueName = "Farm", LocationDisplayName = "Farm",
                    MapPath = "Maps\\Farm", IsOutdoors = true, TimePassing = true
                },
                Player = new PlayerState
                {
                    Name = "Probe", TileX = 64 + tick, TileY = 15, FacingDirection = 1, Facing = "right",
                    Stamina = 270 - tick * 2.5f, MaxStamina = 270, Health = 100, MaxHealth = 100
                },
                Inventory = new InventoryState { Capacity = 12, UsedSlots = 6, EmptySlots = 6, Slots = inventorySlots },
                Toolbar = new ToolbarState
                {
                    Size = 12, Slots = inventorySlots, SelectedIndex = selected,
                    SelectedItem = Tool(tools[selected], selected), SelectedItemId = tools[selected]
                },
                Equipment = new EquipmentState
                {
                    Shirt = new ItemState { QualifiedItemId = "(S)1000", ItemId = "1000", Name = "Shirt", DisplayName = "Shirt", Stack = 1, ItemType = "shirt", TypeDefinitionId = "(S)", Category = -100 },
                    Trinkets = new List<ItemState?>()
                },
                Finance = new FinanceState
                {
                    Money = 500 + tick * 10, WalletScope = "shared", TotalMoneyEarned = tick * 10,
                    NullReasons = new Dictionary<string, string> { ["shipped_income_today"] = "Paid overnight; not stored." }
                },
                Quests = new QuestState
                {
                    Quests = new List<QuestEntryState>
                    {
                        new()
                        {
                            QuestId = "9", QuestType = 1, QuestTypeName = "basic", QuestClass = "Quest", Title = "Introductions",
                            Description = "Meet everyone.", Objective = "Greet 0/28", Progress = new QuestProgressState { Current = 0, Required = 28, Subject = "npcs" }
                        }
                    },
                    SpecialOrders = new List<SpecialOrderEntryState>
                    {
                        new()
                        {
                            OrderKey = "Robin", Title = "Robin's Project", Status = "InProgress", DaysLeft = 6, DueDateTotalDays = 7, Duration = "Week",
                            Objectives = new List<SpecialOrderObjectiveState> { new() { Description = "Gather Hardwood", Current = 0, Required = 80 } },
                            Rewards = new List<SpecialOrderRewardState> { new() { Type = "MoneyReward", Amount = 3500 }, new() { Type = "MailReward" } }
                        }
                    }
                }
            };
        }

        private static ItemState Tool(string qualifiedId, int slot)
        {
            string type = qualifiedId.Substring(0, 3);
            string id = qualifiedId.Substring(3);
            return new ItemState
            {
                SlotIndex = slot, QualifiedItemId = qualifiedId, ItemId = id, Name = id, DisplayName = id, Stack = 1,
                ItemType = type == "(W)" ? "weapon" : "tool", TypeDefinitionId = type, Category = type == "(W)" ? -98 : -99,
                UpgradeLevel = type == "(T)" ? 0 : null
            };
        }

        private static ItemState Obj(string id, string name, int stack, int slot)
        {
            return new ItemState
            {
                SlotIndex = slot, QualifiedItemId = $"(O){id}", ItemId = id, Name = name, DisplayName = name, Stack = stack,
                ItemType = "object", TypeDefinitionId = "(O)", Category = -16
            };
        }
    }
}
