using StardewAgentBridge.State;
using StardewValley;
using StardewValley.Network;

namespace StardewAgentBridge.Observers
{
    /// <summary>Reads date, time, weather and location. Main thread only.</summary>
    internal static class WorldObserver
    {
        public static WorldState Read(Farmer player)
        {
            WorldDate date = Game1.Date;
            GameLocation? location = player.currentLocation;
            string? contextId = location?.GetLocationContextId();
            LocationWeather? weather = ReadWeather(contextId);

            return new WorldState
            {
                Year = date.Year,
                Season = date.SeasonKey ?? "",
                DayOfMonth = date.DayOfMonth,
                // Computed by the game from the in-game day (WorldDate.GetDayOfWeekFor), not the real clock.
                DayOfWeek = date.DayOfWeek.ToString(),
                TotalDays = date.TotalDays,
                TimeOfDay = Game1.timeOfDay,
                TimeText = FormatTime(Game1.timeOfDay),

                Weather = weather?.Weather,
                WeatherTomorrow = weather?.WeatherForTomorrow,
                IsRaining = weather?.IsRaining ?? false,
                IsSnowing = weather?.IsSnowing ?? false,
                IsLightning = weather?.IsLightning ?? false,
                IsDebrisWeather = weather?.IsDebrisWeather ?? false,
                IsGreenRain = weather?.IsGreenRain ?? false,
                LocationContext = contextId,

                LocationName = location?.Name ?? "",
                LocationUniqueName = location?.NameOrUniqueName ?? "",
                LocationDisplayName = location?.DisplayName ?? "",
                MapPath = location?.mapPath.Value ?? "",
                IsOutdoors = location?.IsOutdoors ?? false,

                IsPaused = Game1.paused || Game1.HostPaused,
                TimePassing = Game1.shouldTimePass(),
                IsFestival = Game1.isFestival(),
                EventActive = Game1.eventUp
            };
        }

        /// <summary>
        /// Look up existing weather only. GameLocation.GetWeather() would create and roll weather (Game1.random)
        /// for a context that has no entry yet, which mutates world state.
        /// </summary>
        private static LocationWeather? ReadWeather(string? contextId)
        {
            if (contextId == null || Game1.netWorldState?.Value == null)
                return null;
            return Game1.netWorldState.Value.LocationWeather.TryGetValue(contextId, out LocationWeather weather) ? weather : null;
        }

        /// <summary>Convert Stardew's HHMM integer (e.g. 600, 1350, 2500) to "HH:MM".</summary>
        public static string FormatTime(int timeOfDay)
        {
            return $"{timeOfDay / 100:00}:{timeOfDay % 100:00}";
        }
    }
}
