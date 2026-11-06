namespace StardewAgentBridge.State
{
    /// <summary>Date, time, weather and current location. Plain data only.</summary>
    public sealed record WorldState
    {
        public int Year { get; init; }
        /// <summary>Season key, e.g. "spring" (Game1.Date.SeasonKey).</summary>
        public string Season { get; init; } = "";
        public int DayOfMonth { get; init; }
        /// <summary>In-game weekday, e.g. "Monday" (Game1.Date.DayOfWeek).</summary>
        public string DayOfWeek { get; init; } = "";
        /// <summary>Game's day counter (WorldDate.TotalDays).</summary>
        public int TotalDays { get; init; }
        /// <summary>Stardew HHMM integer, e.g. 600, 1350, 2500.</summary>
        public int TimeOfDay { get; init; }
        /// <summary>"HH:MM" form of <see cref="TimeOfDay"/>.</summary>
        public string TimeText { get; init; } = "";

        /// <summary>Weather ID for the current location context, e.g. "Sun", "Rain", "Storm", "Snow", "Wind", "GreenRain".</summary>
        public string? Weather { get; init; }
        public string? WeatherTomorrow { get; init; }
        public bool IsRaining { get; init; }
        public bool IsSnowing { get; init; }
        public bool IsLightning { get; init; }
        public bool IsDebrisWeather { get; init; }
        public bool IsGreenRain { get; init; }
        /// <summary>Location context ID, e.g. "Default", "Island".</summary>
        public string? LocationContext { get; init; }

        /// <summary>Location name, e.g. "FarmHouse", "Farm".</summary>
        public string LocationName { get; init; } = "";
        /// <summary>Unique name; differs from <see cref="LocationName"/> for buildings/instanced locations.</summary>
        public string LocationUniqueName { get; init; } = "";
        public string LocationDisplayName { get; init; } = "";
        /// <summary>Map asset path, e.g. "Maps\FarmHouse".</summary>
        public string MapPath { get; init; } = "";
        public bool IsOutdoors { get; init; }

        /// <summary>Game paused (Game1.paused) or host paused in multiplayer (Game1.HostPaused).</summary>
        public bool IsPaused { get; init; }
        /// <summary>Whether the clock is currently advancing (Game1.shouldTimePass()); false in menus, events, festivals.</summary>
        public bool TimePassing { get; init; }
        public bool IsFestival { get; init; }
        public bool EventActive { get; init; }
    }
}
