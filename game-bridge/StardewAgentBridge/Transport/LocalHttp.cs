using System;
using System.Collections.Concurrent;
using System.Net.Http;

namespace StardewAgentBridge.Transport
{
    public enum TransportLogLevel
    {
        Debug,
        Info,
        Warn,
        Error
    }

    /// <summary>Shared HTTP setup for the loopback controller connection.</summary>
    internal static class LocalHttp
    {
        public static HttpClient CreateClient(TimeSpan requestTimeout)
        {
            var http = new HttpClient(
                new SocketsHttpHandler
                {
                    // Loopback only; never route through a system proxy.
                    UseProxy = false,
                    ConnectTimeout = requestTimeout,
                    // Re-resolve and reconnect periodically so a restarted controller is picked up cleanly.
                    PooledConnectionLifetime = TimeSpan.FromMinutes(1)
                },
                disposeHandler: true
            )
            {
                Timeout = requestTimeout
            };
            http.DefaultRequestHeaders.ExpectContinue = false;
            return http;
        }
    }

    /// <summary>Log lines written by a background thread and drained on the main thread. Bounded.</summary>
    internal sealed class TransportLog
    {
        private const int MaxQueued = 100;
        private readonly ConcurrentQueue<(TransportLogLevel Level, string Message)> lines = new();

        public void Add(TransportLogLevel level, string message)
        {
            // If the main thread stops draining, drop the oldest lines rather than grow.
            while (this.lines.Count > MaxQueued && this.lines.TryDequeue(out _))
            {
            }
            this.lines.Enqueue((level, message));
        }

        public void Drain(Action<TransportLogLevel, string> write)
        {
            while (this.lines.TryDequeue(out var entry))
                write(entry.Level, entry.Message);
        }
    }
}
