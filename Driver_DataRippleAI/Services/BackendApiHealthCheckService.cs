using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace DataRippleAIDesktop.Services
{
    /// <summary>
    /// Periodically checks the reachability of the backend REST API via an HTTP request.
    /// Exposes a <see cref="ReachabilityState"/> and fires <see cref="StateChanged"/>
    /// whenever the state transitions.  Designed to run independently of whether a
    /// call/session is active.
    /// </summary>
    public sealed class BackendApiHealthCheckService : IDisposable
    {
        // =====================================================================
        // State enumeration
        // =====================================================================

        public enum ReachabilityState
        {
            /// <summary>No check has been performed yet.</summary>
            Unknown,
            /// <summary>A check is currently in progress.</summary>
            Checking,
            /// <summary>The backend API responded successfully.</summary>
            Reachable,
            /// <summary>The backend API did not respond or returned an error.</summary>
            Unreachable
        }

        // =====================================================================
        // Events
        // =====================================================================

        /// <summary>
        /// Fired on a background thread whenever the reachability state changes.
        /// Subscribers that need to update the UI must marshal to the Dispatcher.
        /// </summary>
        public event Action<ReachabilityState> StateChanged;

        // =====================================================================
        // Configuration
        // =====================================================================

        /// <summary>Default interval between health checks.</summary>
        private static readonly TimeSpan DefaultCheckInterval = TimeSpan.FromSeconds(10);

        /// <summary>Timeout for each individual HTTP request.</summary>
        private static readonly TimeSpan HttpRequestTimeout = TimeSpan.FromSeconds(5);

        // =====================================================================
        // Internal state
        // =====================================================================

        private readonly string _healthCheckUrl;
        private readonly TimeSpan _checkInterval;
        private Timer _timer;
        private HttpClient _httpClient;
        private ReachabilityState _currentState = ReachabilityState.Unknown;
        private readonly object _stateLock = new object();
        private bool _disposed = false;
        private int _checkInProgress = 0; // 0 = idle, 1 = running (interlocked guard)

        // =====================================================================
        // Public properties
        // =====================================================================

        /// <summary>Current reachability state (thread-safe read).</summary>
        public ReachabilityState CurrentState
        {
            get { lock (_stateLock) { return _currentState; } }
        }

        // =====================================================================
        // Construction
        // =====================================================================

        /// <summary>
        /// Creates a new health-check service targeting the given backend URL.
        /// </summary>
        /// <param name="backendBaseUrl">
        /// The backend REST API base URL (e.g. "https://ghostagent-dev.dataripple.ai/api/").
        /// A lightweight HEAD / GET request will be sent to this URL.
        /// </param>
        /// <param name="checkInterval">
        /// Optional interval between checks.  Defaults to 10 seconds.
        /// </param>
        public BackendApiHealthCheckService(string backendBaseUrl, TimeSpan? checkInterval = null)
        {
            if (string.IsNullOrWhiteSpace(backendBaseUrl))
                throw new ArgumentException("Backend base URL must not be empty.", nameof(backendBaseUrl));

            _healthCheckUrl = backendBaseUrl.TrimEnd('/');
            _checkInterval = checkInterval ?? DefaultCheckInterval;

            LoggingService.Info($"[BackendApiHealthCheck] Created. URL={_healthCheckUrl}, Interval={_checkInterval.TotalSeconds}s");
        }

        // =====================================================================
        // Start / Stop
        // =====================================================================

        /// <summary>
        /// Starts the periodic health-check timer.  Safe to call multiple times;
        /// subsequent calls are ignored if the timer is already running.
        /// </summary>
        public void Start()
        {
            if (_disposed) return;

            if (_timer != null)
            {
                LoggingService.Debug("[BackendApiHealthCheck] Already running, ignoring Start()");
                return;
            }

            try
            {
                _httpClient = HttpClientFactory.CreateApiHttpClient(timeout: HttpRequestTimeout);

                // Fire the first check almost immediately (500 ms), then repeat at the configured interval.
                _timer = new Timer(OnTimerTick, null, TimeSpan.FromMilliseconds(500), _checkInterval);

                LoggingService.Info("[BackendApiHealthCheck] Started periodic health checks");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[BackendApiHealthCheck] Error starting: {ex.Message}");
            }
        }

        /// <summary>
        /// Stops the periodic health-check timer and resets state to Unknown.
        /// </summary>
        public void Stop()
        {
            try
            {
                _timer?.Dispose();
                _timer = null;

                _httpClient?.Dispose();
                _httpClient = null;

                SetState(ReachabilityState.Unknown);
                LoggingService.Info("[BackendApiHealthCheck] Stopped");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[BackendApiHealthCheck] Error stopping: {ex.Message}");
            }
        }

        // =====================================================================
        // Timer callback
        // =====================================================================

        private async void OnTimerTick(object state)
        {
            // Guard against overlapping checks (e.g., if a previous request is still pending).
            if (Interlocked.CompareExchange(ref _checkInProgress, 1, 0) != 0)
                return;

            try
            {
                await PerformHealthCheckAsync().ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref _checkInProgress, 0);
            }
        }

        // =====================================================================
        // Health check logic
        // =====================================================================

        private async Task PerformHealthCheckAsync()
        {
            if (_disposed || _httpClient == null) return;

            SetState(ReachabilityState.Checking);

            try
            {
                // Use a HEAD request to minimise payload.  If the backend does not support
                // HEAD on the base path, fall back to GET.  We only care about receiving
                // *any* HTTP response (even 4xx/5xx means the server is reachable).
                using var cts = new CancellationTokenSource(HttpRequestTimeout);

                HttpResponseMessage response = null;
                try
                {
                    var request = new HttpRequestMessage(HttpMethod.Head, _healthCheckUrl);

                    // Add authorization header if we have a token -- some endpoints may require auth
                    if (!string.IsNullOrEmpty(Globals.BackendAccessToken))
                    {
                        request.Headers.Authorization =
                            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Globals.BackendAccessToken);
                    }

                    response = await _httpClient.SendAsync(request, cts.Token).ConfigureAwait(false);
                }
                catch (HttpRequestException)
                {
                    // HEAD might not be supported; try GET as fallback
                    try
                    {
                        using var getCts = new CancellationTokenSource(HttpRequestTimeout);
                        var getRequest = new HttpRequestMessage(HttpMethod.Get, _healthCheckUrl);

                        if (!string.IsNullOrEmpty(Globals.BackendAccessToken))
                        {
                            getRequest.Headers.Authorization =
                                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Globals.BackendAccessToken);
                        }

                        response = await _httpClient.SendAsync(getRequest, getCts.Token).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Both HEAD and GET failed -- unreachable
                    }
                }

                if (response != null)
                {
                    // Any HTTP response (even 4xx/5xx) means the server process is alive.
                    // Only network-level failures (timeout, DNS, connection refused) are "unreachable".
                    SetState(ReachabilityState.Reachable);

                    if (Globals.EnableVerboseLogging)
                    {
                        LoggingService.Debug($"[BackendApiHealthCheck] Reachable (HTTP {(int)response.StatusCode})");
                    }

                    response.Dispose();
                }
                else
                {
                    SetState(ReachabilityState.Unreachable);
                    LoggingService.Warn("[BackendApiHealthCheck] Unreachable - no HTTP response received");
                }
            }
            catch (TaskCanceledException)
            {
                // Request timed out
                SetState(ReachabilityState.Unreachable);
                LoggingService.Warn("[BackendApiHealthCheck] Unreachable - request timed out");
            }
            catch (OperationCanceledException)
            {
                // Service is being disposed -- do nothing
            }
            catch (Exception ex)
            {
                SetState(ReachabilityState.Unreachable);
                LoggingService.Warn($"[BackendApiHealthCheck] Unreachable - {ex.GetType().Name}: {ex.Message}");
            }
        }

        // =====================================================================
        // State management
        // =====================================================================

        private void SetState(ReachabilityState newState)
        {
            bool changed;
            lock (_stateLock)
            {
                changed = _currentState != newState;
                _currentState = newState;
            }

            if (changed)
            {
                try
                {
                    StateChanged?.Invoke(newState);
                }
                catch (Exception ex)
                {
                    LoggingService.Error($"[BackendApiHealthCheck] Error in StateChanged handler: {ex.Message}");
                }
            }
        }

        // =====================================================================
        // IDisposable
        // =====================================================================

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                _timer?.Dispose();
                _timer = null;

                _httpClient?.Dispose();
                _httpClient = null;

                LoggingService.Info("[BackendApiHealthCheck] Disposed");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[BackendApiHealthCheck] Error during dispose: {ex.Message}");
            }
        }
    }
}
