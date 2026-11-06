using System;
using System.Net;

namespace StardewAgentBridge.Transport
{
    /// <summary>
    /// Main-thread side of the transport: owns the session ID and sequence, serializes snapshots into envelopes
    /// and hands the bytes to <see cref="ObservationSender"/>. Only ever sees immutable snapshots, never Game1.
    /// </summary>
    /// <remarks>
    /// Session rules: a session starts with the first world_ready=true snapshot after a save is loaded and ends
    /// with one world_ready=false snapshot (return to title or game exit). No observations are sent while no
    /// session is open; the controller marks the last state stale once posts stop.
    /// </remarks>
    public sealed class ObservationPublisher : IDisposable
    {
        public const string ObservationsPath = "v1/observations";

        private readonly ObservationSender sender;
        private readonly string bridgeVersion;
        private string? sessionId;
        private long sequence;

        public ObservationPublisher(Uri controllerUrl, TimeSpan requestTimeout, string bridgeVersion)
        {
            this.Endpoint = new Uri(controllerUrl, ObservationsPath);
            this.bridgeVersion = bridgeVersion;
            this.sender = new ObservationSender(this.Endpoint, requestTimeout);
        }

        public Uri Endpoint { get; }

        /// <summary>Current session ID, or null while no save is loaded.</summary>
        public string? SessionId => this.sessionId;

        public long Sequence => this.sequence;

        /// <summary>Send the snapshot, opening a session on the first ready snapshot and closing it on the first not-ready one.</summary>
        /// <returns>The session ID that was opened, or null if this call didn't open one.</returns>
        public string? Publish(GameStateSnapshot snapshot)
        {
            string? opened = null;
            if (snapshot.WorldReady)
            {
                if (this.sessionId == null)
                {
                    this.sessionId = Guid.NewGuid().ToString("N");
                    this.sequence = 0;
                    opened = this.sessionId;
                }
                this.Send(this.sessionId, snapshot);
            }
            else if (this.sessionId != null)
            {
                this.EndSession(snapshot);
            }
            return opened;
        }

        /// <summary>Send a final world_ready=false observation and forget the session.</summary>
        public void EndSession(GameStateSnapshot? notReady = null)
        {
            if (this.sessionId == null)
                return;

            DateTime now = DateTime.UtcNow;
            GameStateSnapshot final = notReady is { WorldReady: false }
                ? notReady
                : GameStateSnapshot.NotReady with { CapturedAtUtc = now, ChangedAtUtc = now };
            this.Send(this.sessionId, final);
            this.sessionId = null;
            this.sequence = 0;
        }

        public void DrainLogs(Action<TransportLogLevel, string> write)
        {
            this.sender.DrainLogs(write);
        }

        /// <summary>Close any open session and give the sender a moment to deliver it.</summary>
        public void Shutdown(TimeSpan flushTimeout)
        {
            this.EndSession();
            this.sender.Shutdown(flushTimeout);
        }

        public void Dispose()
        {
            this.Shutdown(TimeSpan.FromSeconds(1));
        }

        /// <summary>Whether the URL targets this machine only (127.0.0.0/8, ::1 or "localhost") over http.</summary>
        public static bool IsLoopbackHttpUrl(Uri url)
        {
            if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttp)
                return false;
            if (string.Equals(url.Host, "localhost", StringComparison.OrdinalIgnoreCase))
                return true;
            return IPAddress.TryParse(url.Host.Trim('[', ']'), out IPAddress? address) && IPAddress.IsLoopback(address);
        }

        private void Send(string session, GameStateSnapshot snapshot)
        {
            byte[] payload = ObservationEnvelope.Build(session, ++this.sequence, this.bridgeVersion, snapshot, DateTime.UtcNow);
            this.sender.Enqueue(payload);
        }
    }
}
