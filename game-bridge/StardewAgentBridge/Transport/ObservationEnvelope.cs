using System;
using System.IO;
using System.Text.Json;
using StardewAgentBridge.Serialization;

namespace StardewAgentBridge.Transport
{
    /// <summary>
    /// Wire format for POST /v1/observations. The snapshot is written with the same <see cref="StateJson.Options"/>
    /// as <c>agent_state json</c>, so the controller sees exactly the existing snake_case snapshot.
    /// </summary>
    public static class ObservationEnvelope
    {
        /// <summary>Bump when the envelope or snapshot shape changes incompatibly; the controller rejects other versions.</summary>
        public const int SchemaVersion = 1;

        /// <summary>Serialize one observation to UTF-8 JSON. Touches only the immutable snapshot, never Game1.</summary>
        public static byte[] Build(string sessionId, long sequence, string bridgeVersion, GameStateSnapshot snapshot, DateTime createdAtUtc)
        {
            using var buffer = new MemoryStream(16 * 1024);
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteNumber("schema_version", SchemaVersion);
                writer.WriteString("session_id", sessionId);
                writer.WriteNumber("sequence", sequence);
                writer.WriteString("created_at_utc", createdAtUtc);
                writer.WriteString("bridge_version", bridgeVersion);
                writer.WritePropertyName("snapshot");
                JsonSerializer.Serialize(writer, snapshot, StateJson.Options);
                writer.WriteEndObject();
            }
            return buffer.ToArray();
        }
    }
}
