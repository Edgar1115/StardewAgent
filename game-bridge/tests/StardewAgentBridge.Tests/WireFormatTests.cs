using System.Linq;
using System.Text.Json;
using StardewAgentBridge.Serialization;
using StardewAgentBridge.Transport;
using Xunit;

namespace StardewAgentBridge.Tests
{
    /// <summary>Field names must match agent/src/stardew_agent/actions.py exactly (the controller forbids unknown fields).</summary>
    public class WireFormatTests
    {
        private static string[] Keys<T>(T value)
        {
            using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(value, StateJson.Options));
            return doc.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
        }

        [Fact]
        public void ClaimRequest_MatchesControllerSchema()
        {
            Assert.Equal(
                new[] { "automation_enabled", "block_reason", "bridge_version", "controller_instance_id", "ready", "running_action_id", "session_id", "stop_seq_seen" },
                Keys(new ClaimRequest())
            );
        }

        [Fact]
        public void StatusReport_MatchesControllerSchema()
        {
            Assert.Equal(new[] { "failure_reason", "lease_id", "result", "session_id", "state" }, Keys(new StatusReport()));
            Assert.Equal(
                new[] { "action_id", "blocked", "distance_pixels", "end_position", "failure_reason", "finished_at_utc", "session_id", "start_position", "started_at_utc", "ticks_executed", "type" },
                Keys(new ActionResult())
            );
            Assert.Equal(new[] { "facing_direction", "location_name", "tile_x", "tile_y", "x", "y" }, Keys(new PlayerPosition()));
            Assert.Equal(new[] { "reason", "source" }, Keys(new StopRequest()));
        }

        [Fact]
        public void ClaimResponse_ParsesControllerJson()
        {
            // Shape produced by ActionStore.claim().
            const string json = """
                {"controller_instance_id":"abc","stop_seq":2,"stop_reason":"test",
                 "action":{"action_id":"a1","session_id":"s1","type":"move_direction",
                           "params":{"direction":"up","duration_ticks":30},
                           "lease_id":"l1","lease_expires_at_utc":"2026-11-05T10:00:05+00:00","created_at_utc":"2026-11-05T10:00:00+00:00"}}
                """;
            ClaimResponse r = JsonSerializer.Deserialize<ClaimResponse>(json, StateJson.Options)!;
            Assert.Equal("abc", r.ControllerInstanceId);
            Assert.Equal(2, r.StopSeq);
            Assert.Equal("move_direction", r.Action!.Type);
            Assert.Equal("up", r.Action.Params.Direction);
            Assert.Equal(30, r.Action.Params.DurationTicks);
            Assert.Equal("l1", r.Action.LeaseId);

            ClaimResponse idle = JsonSerializer.Deserialize<ClaimResponse>("""{"controller_instance_id":"abc","stop_seq":0,"stop_reason":null,"action":null}""", StateJson.Options)!;
            Assert.Null(idle.Action);
        }
    }
}
