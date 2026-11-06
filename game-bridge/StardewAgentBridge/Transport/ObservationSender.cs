using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace StardewAgentBridge.Transport
{
    /// <summary>
    /// One background sender with a single-slot, latest-wins buffer. The main thread hands over finished
    /// payloads with <see cref="Enqueue"/> and never waits on the network; if the controller is slow or down,
    /// newer payloads replace older unsent ones, so nothing piles up. Log messages are queued for the main
    /// thread (<see cref="DrainLogs"/>) instead of being written from the background.
    /// </summary>
    public sealed class ObservationSender : IDisposable
    {
        private static readonly TimeSpan[] Backoff =
        {
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5)
        };
        private static readonly TimeSpan WarnInterval = TimeSpan.FromSeconds(30);
        private static readonly MediaTypeHeaderValue JsonContentType = new("application/json") { CharSet = "utf-8" };

        private readonly Uri endpoint;
        private readonly HttpClient http;
        private readonly SemaphoreSlim signal = new(0, 1);
        /// <summary>Stops waiting and backoff; lets an in-flight request finish.</summary>
        private readonly CancellationTokenSource stop = new();
        /// <summary>Cancels an in-flight request when shutdown can't wait any longer.</summary>
        private readonly CancellationTokenSource abort = new();
        private readonly TransportLog logs = new();
        private readonly Task loop;

        private byte[]? pending;
        private long superseded;
        private bool disposed;

        // Background-thread state only.
        private bool connected;
        private bool everConnected;
        private int failures;
        private DateTime lastWarnUtc = DateTime.MinValue;
        private bool rejectedWarned;

        public ObservationSender(Uri endpoint, TimeSpan requestTimeout)
        {
            this.endpoint = endpoint;
            this.http = LocalHttp.CreateClient(requestTimeout);
            this.loop = Task.Run(this.RunAsync);
        }

        /// <summary>Payloads replaced before they could be sent (the controller was slow or unreachable).</summary>
        public long SupersededCount => Interlocked.Read(ref this.superseded);

        /// <summary>Hand a finished payload to the sender. Never blocks; replaces any unsent payload.</summary>
        public void Enqueue(byte[] payload)
        {
            if (this.disposed)
                return;
            if (Interlocked.Exchange(ref this.pending, payload) != null)
                Interlocked.Increment(ref this.superseded);
            this.Signal();
        }

        /// <summary>Write queued log messages. Call from the main thread.</summary>
        public void DrainLogs(Action<TransportLogLevel, string> write)
        {
            this.logs.Drain(write);
        }

        /// <summary>Try to deliver the last pending payload for up to <paramref name="flushTimeout"/>, then release the connection.</summary>
        public void Shutdown(TimeSpan flushTimeout)
        {
            if (this.disposed)
                return;
            this.disposed = true;

            this.stop.Cancel();
            try
            {
                if (!this.loop.Wait(flushTimeout))
                {
                    this.abort.Cancel();
                    this.loop.Wait(TimeSpan.FromMilliseconds(500));
                }
            }
            catch (AggregateException)
            {
                // Loop faults are already logged; shutting down regardless.
            }

            this.http.Dispose();
            this.stop.Dispose();
            this.abort.Dispose();
            this.signal.Dispose();
        }

        public void Dispose()
        {
            this.Shutdown(TimeSpan.Zero);
        }

        private void Signal()
        {
            try
            {
                if (this.signal.CurrentCount == 0)
                    this.signal.Release();
            }
            catch (SemaphoreFullException)
            {
                // Already signalled.
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private async Task RunAsync()
        {
            try
            {
                while (true)
                {
                    byte[]? payload = Interlocked.Exchange(ref this.pending, null);
                    if (payload == null)
                    {
                        if (this.stop.IsCancellationRequested)
                            return;
                        try
                        {
                            await this.signal.WaitAsync(this.stop.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            // Stopping: loop once more to flush anything still pending.
                        }
                        continue;
                    }

                    if (await this.SendAsync(payload).ConfigureAwait(false))
                        continue;

                    if (this.stop.IsCancellationRequested)
                        return;

                    // Retry this payload after a backoff, unless a newer one arrives meanwhile.
                    Interlocked.CompareExchange(ref this.pending, payload, null);
                    try
                    {
                        await Task.Delay(Backoff[Math.Min(this.failures - 1, Backoff.Length - 1)], this.stop.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }
            }
            catch (Exception ex)
            {
                this.Log(TransportLogLevel.Error, $"Observation sender stopped unexpectedly: {ex}");
            }
        }

        /// <summary>POST one payload. Returns false only for failures worth retrying (unreachable, timeout, 5xx).</summary>
        private async Task<bool> SendAsync(byte[] payload)
        {
            string? error;
            try
            {
                using var content = new ByteArrayContent(payload);
                content.Headers.ContentType = JsonContentType;
                using HttpResponseMessage response = await this.http.PostAsync(this.endpoint, content, this.abort.Token).ConfigureAwait(false);
                int status = (int)response.StatusCode;

                if (response.IsSuccessStatusCode || status == 409)
                {
                    // 409 = the controller already has this or a newer sequence; delivered as far as we're concerned.
                    this.OnDelivered();
                    return true;
                }

                if (status >= 400 && status < 500)
                {
                    // Schema or protocol mismatch: retrying the same bytes can't help.
                    this.OnDelivered();
                    if (!this.rejectedWarned)
                    {
                        this.rejectedWarned = true;
                        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (body.Length > 600)
                            body = body.Substring(0, 600) + "...";
                        this.Log(TransportLogLevel.Warn, $"Controller rejected an observation (HTTP {status}); check bridge/controller schema versions. Response: {body}");
                    }
                    return true;
                }

                error = $"HTTP {status}";
            }
            catch (OperationCanceledException) when (!this.abort.IsCancellationRequested)
            {
                error = $"timed out after {this.http.Timeout.TotalSeconds:0.#}s";
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (HttpRequestException ex)
            {
                error = ex.InnerException?.Message ?? ex.Message;
            }
            catch (Exception ex)
            {
                // Anything else is still a network-side failure; keep the loop alive.
                error = $"{ex.GetType().Name}: {ex.Message}";
            }

            this.OnFailed(error);
            return false;
        }

        private void OnDelivered()
        {
            if (!this.connected)
            {
                this.Log(
                    TransportLogLevel.Info,
                    this.everConnected
                        ? $"Reconnected to controller at {this.endpoint} after {this.failures} failed attempt(s)."
                        : $"Connected to controller at {this.endpoint}."
                );
            }
            this.connected = true;
            this.everConnected = true;
            this.failures = 0;
        }

        private void OnFailed(string error)
        {
            this.failures++;
            DateTime now = DateTime.UtcNow;
            bool justLost = this.connected || this.failures == 1;
            this.connected = false;

            // Rate-limited: once when the connection drops, then at most every WarnInterval.
            if (justLost || now - this.lastWarnUtc >= WarnInterval)
            {
                this.lastWarnUtc = now;
                this.Log(
                    TransportLogLevel.Warn,
                    $"Controller unreachable at {this.endpoint} ({error}); {this.failures} failed attempt(s), retrying in the background. The game is unaffected."
                );
            }
        }

        private void Log(TransportLogLevel level, string message)
        {
            this.logs.Add(level, message);
        }
    }
}
