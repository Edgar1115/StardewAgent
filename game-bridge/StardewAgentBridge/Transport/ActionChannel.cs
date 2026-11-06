using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StardewAgentBridge.Serialization;

namespace StardewAgentBridge.Transport
{
    /// <summary>Something the controller told the bridge, handed to the main thread.</summary>
    public abstract record ControlEvent;

    /// <summary>The controller answered with a different instance ID (it restarted, or this is the first contact).</summary>
    public sealed record ControllerInstanceEvent(string InstanceId) : ControlEvent;

    /// <summary>The controller's stop counter is ahead of what the main thread last applied.</summary>
    public sealed record StopEvent(string InstanceId, long StopSeq, string? Reason) : ControlEvent;

    public sealed record ActionEvent(string InstanceId, ActionCommand Command) : ControlEvent;

    /// <summary>The controller refused a status report (unknown action after a restart, lease lost, already terminal).</summary>
    public sealed record ReportRefusedEvent(string ActionId, int StatusCode, string Reason) : ControlEvent;

    /// <summary>
    /// Background half of the action transport. Polls POST /v1/actions/claim (the poll is also the heartbeat),
    /// delivers status reports and stop requests. Never touches the game: the main thread publishes a
    /// <see cref="ClaimRequest"/> describing its state and drains <see cref="ControlEvent"/>s.
    /// Everything is bounded, so a dead controller can't make work pile up.
    /// </summary>
    public sealed class ActionChannel : IDisposable
    {
        private const int MaxEvents = 16;
        private const int MaxReports = 64;
        private static readonly TimeSpan WarnInterval = TimeSpan.FromSeconds(30);
        private static readonly MediaTypeHeaderValue JsonContentType = new("application/json") { CharSet = "utf-8" };

        private readonly Uri claimUri;
        private readonly Uri stopUri;
        private readonly Uri baseUri;
        private readonly HttpClient http;
        private readonly TimeSpan armedPollInterval;
        private readonly TimeSpan idlePollInterval;
        private readonly ConcurrentQueue<ControlEvent> events = new();
        private readonly ConcurrentQueue<OutgoingReport> reports = new();
        private readonly TransportLog logs = new();
        private readonly SemaphoreSlim wake = new(0, 1);
        private readonly CancellationTokenSource stop = new();
        private readonly Task loop;

        private volatile ClaimRequest? status;
        private StopRequest? pendingStop;
        private long lastContactTicks;
        private bool linkDown;
        private long droppedReports;
        private bool disposed;

        // Background-thread state only.
        private string? lastInstanceId;
        private bool connected;
        private int failures;
        private DateTime lastWarnUtc = DateTime.MinValue;

        public ActionChannel(Uri controllerUrl, TimeSpan requestTimeout, TimeSpan armedPollInterval, TimeSpan idlePollInterval)
        {
            this.baseUri = controllerUrl;
            this.claimUri = new Uri(controllerUrl, "v1/actions/claim");
            this.stopUri = new Uri(controllerUrl, "v1/control/stop");
            this.armedPollInterval = armedPollInterval;
            this.idlePollInterval = idlePollInterval;
            this.http = LocalHttp.CreateClient(requestTimeout);
            this.loop = Task.Run(this.RunAsync);
        }

        public Uri ClaimUri => this.claimUri;

        /// <summary>
        /// Real seconds since the last successful exchange with the controller; infinity if never, or if the
        /// latest request failed (the link is known to be down, no need to wait out a timeout).
        /// </summary>
        public double SecondsSinceContact
        {
            get
            {
                if (Volatile.Read(ref this.linkDown))
                    return double.PositiveInfinity;
                long ticks = Interlocked.Read(ref this.lastContactTicks);
                return ticks == 0 ? double.PositiveInfinity : (DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc)).TotalSeconds;
            }
        }

        public long DroppedReports => Interlocked.Read(ref this.droppedReports);

        /// <summary>Publish the main thread's current state; sent with the next poll. Main thread.</summary>
        public void UpdateStatus(ClaimRequest request)
        {
            ClaimRequest? previous = this.status;
            this.status = request;
            // Becoming ready or changing session: poll now instead of waiting out the idle interval.
            if (previous == null || previous.Ready != request.Ready || previous.SessionId != request.SessionId || previous.AutomationEnabled != request.AutomationEnabled)
                this.Wake();
        }

        /// <summary>Queue a status report. Never blocks; when full, the oldest report is dropped. Main thread.</summary>
        public void Report(OutgoingReport report)
        {
            if (this.disposed)
                return;
            while (this.reports.Count >= MaxReports && this.reports.TryDequeue(out _))
                Interlocked.Increment(ref this.droppedReports);
            this.reports.Enqueue(report);
            this.Wake();
        }

        /// <summary>Ask the controller to cancel its queue (emergency stop, manual takeover). Latest request wins. Main thread.</summary>
        public void RequestControllerStop(string reason, string source)
        {
            Volatile.Write(ref this.pendingStop, new StopRequest { Reason = reason, Source = source });
            this.Wake();
        }

        public bool TryDequeueEvent(out ControlEvent? controlEvent)
        {
            bool ok = this.events.TryDequeue(out ControlEvent? e);
            controlEvent = e;
            return ok;
        }

        public void DrainLogs(Action<TransportLogLevel, string> write)
        {
            this.logs.Drain(write);
        }

        /// <summary>Give queued reports a moment to go out, then release the connection.</summary>
        public void Shutdown(TimeSpan flushTimeout)
        {
            if (this.disposed)
                return;
            this.disposed = true;
            this.status = null;
            this.stop.Cancel();
            try
            {
                this.loop.Wait(flushTimeout);
            }
            catch (AggregateException)
            {
            }
            this.http.Dispose();
        }

        public void Dispose()
        {
            this.Shutdown(TimeSpan.Zero);
        }

        private void Wake()
        {
            try
            {
                if (this.wake.CurrentCount == 0)
                    this.wake.Release();
            }
            catch (SemaphoreFullException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private async Task RunAsync()
        {
            while (true)
            {
                bool stopping = this.stop.IsCancellationRequested;
                try
                {
                    bool reachable = await this.SendStopAsync().ConfigureAwait(false)
                        && await this.SendReportsAsync().ConfigureAwait(false);
                    if (stopping)
                        return;
                    if (reachable)
                        await this.ClaimAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    this.logs.Add(TransportLogLevel.Error, $"Action channel error: {ex}");
                    if (stopping)
                        return;
                }

                ClaimRequest? current = this.status;
                TimeSpan delay = current is { AutomationEnabled: true } ? this.armedPollInterval : this.idlePollInterval;
                if (!this.connected)
                    delay = TimeSpan.FromSeconds(Math.Min(5, Math.Max(delay.TotalSeconds, this.failures)));
                try
                {
                    // Woken early by new reports, stop requests or a state change.
                    await this.wake.WaitAsync(delay, this.stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Stopping: one more pass to flush reports, then exit.
                }
            }
        }

        private async Task<bool> SendStopAsync()
        {
            StopRequest? request = Volatile.Read(ref this.pendingStop);
            if (request == null)
                return true;

            (int status, _) = await this.PostAsync(this.stopUri, request).ConfigureAwait(false);
            if (status == 0)
                return false;
            Interlocked.CompareExchange(ref this.pendingStop, null, request);
            if (status >= 300)
                this.logs.Add(TransportLogLevel.Warn, $"Controller refused the stop request (HTTP {status}).");
            return true;
        }

        /// <summary>Send queued reports in order. Returns false if the controller is unreachable (the rest wait).</summary>
        private async Task<bool> SendReportsAsync()
        {
            while (this.reports.TryPeek(out OutgoingReport? next))
            {
                var uri = new Uri(this.baseUri, $"v1/actions/{Uri.EscapeDataString(next.ActionId)}/status");
                (int status, string body) = await this.PostAsync(uri, next.Report).ConfigureAwait(false);
                if (status == 0 || status >= 500)
                    return false;

                this.reports.TryDequeue(out _);
                if (status >= 400)
                {
                    string reason = ReadReason(body) ?? $"HTTP {status}";
                    // A duplicate-terminal 409 or a RUNNING heartbeat after the end is harmless; anything else matters to the executor.
                    this.Enqueue(new ReportRefusedEvent(next.ActionId, status, reason));
                    this.logs.Add(
                        status == 404 ? TransportLogLevel.Warn : TransportLogLevel.Debug,
                        $"Controller refused {next.Report.State} report for action {next.ActionId}: HTTP {status} {reason}."
                    );
                }
            }
            return true;
        }

        private async Task ClaimAsync()
        {
            ClaimRequest? request = this.status;
            if (request == null)
                return;

            (int status, string body) = await this.PostAsync(this.claimUri, request).ConfigureAwait(false);
            if (status == 0)
                return;
            if (status != 200)
            {
                this.logs.Add(TransportLogLevel.Warn, $"Action claim refused (HTTP {status}): {Truncate(body)}");
                return;
            }

            ClaimResponse? response;
            try
            {
                response = JsonSerializer.Deserialize<ClaimResponse>(body, StateJson.Options);
            }
            catch (JsonException ex)
            {
                this.logs.Add(TransportLogLevel.Warn, $"Unreadable claim response: {ex.Message}");
                return;
            }
            if (response == null || response.ControllerInstanceId.Length == 0)
                return;

            if (response.ControllerInstanceId != this.lastInstanceId)
            {
                this.lastInstanceId = response.ControllerInstanceId;
                this.Enqueue(new ControllerInstanceEvent(response.ControllerInstanceId));
            }

            bool sameInstance = request.ControllerInstanceId == response.ControllerInstanceId;
            long seen = sameInstance ? request.StopSeqSeen : 0;
            if (response.StopSeq > seen)
                this.Enqueue(new StopEvent(response.ControllerInstanceId, response.StopSeq, response.StopReason));

            if (response.Action != null && !this.Enqueue(new ActionEvent(response.ControllerInstanceId, response.Action)))
            {
                // Main thread is not draining; refuse rather than buffer.
                this.Report(
                    new OutgoingReport(
                        response.Action.ActionId,
                        new StatusReport
                        {
                            SessionId = response.Action.SessionId,
                            LeaseId = response.Action.LeaseId,
                            State = ActionStates.Rejected,
                            FailureReason = "bridge_busy",
                            Result = new ActionResult { ActionId = response.Action.ActionId, SessionId = response.Action.SessionId, Type = response.Action.Type, FailureReason = "bridge_busy" }
                        }
                    )
                );
            }
        }

        private bool Enqueue(ControlEvent controlEvent)
        {
            if (this.events.Count >= MaxEvents)
                return false;
            this.events.Enqueue(controlEvent);
            return true;
        }

        /// <summary>POST JSON. Returns (0, "") when the controller can't be reached.</summary>
        private async Task<(int Status, string Body)> PostAsync<T>(Uri uri, T payload)
        {
            string? error;
            try
            {
                using var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(payload, StateJson.Options));
                content.Headers.ContentType = JsonContentType;
                using HttpResponseMessage response = await this.http.PostAsync(uri, content).ConfigureAwait(false);
                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                this.OnContact();
                return ((int)response.StatusCode, body);
            }
            catch (OperationCanceledException)
            {
                error = $"timed out after {this.http.Timeout.TotalSeconds:0.#}s";
            }
            catch (HttpRequestException ex)
            {
                error = ex.InnerException?.Message ?? ex.Message;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
            }

            this.OnFailed(error);
            return (0, "");
        }

        private void OnContact()
        {
            Interlocked.Exchange(ref this.lastContactTicks, DateTime.UtcNow.Ticks);
            Volatile.Write(ref this.linkDown, false);
            if (!this.connected)
                this.logs.Add(TransportLogLevel.Info, $"Action channel connected to {this.claimUri}.");
            this.connected = true;
            this.failures = 0;
        }

        private void OnFailed(string error)
        {
            Volatile.Write(ref this.linkDown, true);
            bool justLost = this.connected || this.failures == 0;
            this.connected = false;
            this.failures++;
            DateTime now = DateTime.UtcNow;
            if (justLost || now - this.lastWarnUtc >= WarnInterval)
            {
                this.lastWarnUtc = now;
                this.logs.Add(TransportLogLevel.Warn, $"Action channel cannot reach controller ({error}); {this.failures} failed attempt(s). Automation pauses until it is back.");
            }
        }

        private static string? ReadReason(string body)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(body);
                return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("reason", out JsonElement reason) && reason.ValueKind == JsonValueKind.String
                    ? reason.GetString()
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string Truncate(string text)
        {
            return text.Length > 300 ? text.Substring(0, 300) + "..." : text;
        }
    }

    public static class ActionStates
    {
        public const string Running = "RUNNING";
        public const string Succeeded = "SUCCEEDED";
        public const string Failed = "FAILED";
        public const string Cancelled = "CANCELLED";
        public const string Rejected = "REJECTED";
        public const string TimedOut = "TIMED_OUT";
    }
}
