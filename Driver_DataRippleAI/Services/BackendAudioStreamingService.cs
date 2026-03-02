using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace DataRippleAIDesktop.Services
{
    /// <summary>
    /// Backend Audio Streaming Service
    /// Handles WebSocket communication with the DataRipple backend for streaming
    /// microphone and speaker audio data in real-time.
    /// Replaces third-party STT services by routing
    /// raw audio directly to the backend for processing.
    /// </summary>
    public class BackendAudioStreamingService : IDisposable
    {
        // =====================================================================
        // Configuration section name and defaults
        // =====================================================================

        /// <summary>Configuration section name in appsettings.json.</summary>
        public const string ConfigSectionName = "BackendAudioStreaming";

        /// <summary>Default WebSocket path prefix appended to the backend base URL. User ID is appended at connection time.</summary>
        private const string DefaultWebSocketPathPrefix = "/ws/audio";

        // Default values for all configurable parameters
        private const int DefaultSampleRate = 16000;
        private const int DefaultBitDepth = 16;
        private const int DefaultChannels = 1;
        private const int DefaultChunkingIntervalMs = 200;
        private const int DefaultMaxReconnectionAttempts = 10;
        private const int DefaultBaseReconnectionDelayMs = 1000;
        private const int DefaultPingIntervalSeconds = 30;
        private const int DefaultPongTimeoutSeconds = 10;
        private const int DefaultConnectionTimeoutSeconds = 20;

        // =====================================================================
        // Connection state
        // =====================================================================
        private ClientWebSocket _webSocket;
        private CancellationTokenSource _cancellationTokenSource;
        private bool _isConnected = false;
        private bool _isReconnecting = false;
        private Task _messageListener;
        private Task _pingTask;
        private string _storedJwtToken; // Store JWT token for reconnection

        // =====================================================================
        // Connection configuration
        // =====================================================================
        private readonly string _webSocketUrl;
        private readonly int _sampleRate;
        private readonly int _bitDepth;
        private readonly int _channels;
        private readonly int _chunkingIntervalMs;
        private readonly int _connectionTimeoutSeconds;

        // =====================================================================
        // Reconnection configuration
        // =====================================================================
        private readonly int _maxReconnectionAttempts;
        private readonly int _baseReconnectionDelayMs;
        private int _reconnectionAttempts = 0;
        private DateTime _connectionLostTime = DateTime.MinValue;

        // =====================================================================
        // Health ping/pong
        // =====================================================================
        private readonly int _pingIntervalSeconds;
        private DateTime _lastPongReceivedTime = DateTime.MinValue;
        private DateTime _lastPingSentTime = DateTime.MinValue;
        private readonly int _pongTimeoutSeconds;

        // =====================================================================
        // Network latency measurement (HTTP ping to backend host)
        // =====================================================================
        private static readonly HttpClient _latencyHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        private Task _latencyMeasurementTask;
        private string _latencyPingUrl; // Derived from WebSocket URL (https://host/)

        // =====================================================================
        // Audio tracking & thread safety
        // =====================================================================
        private long _micChunksSent = 0;
        private long _speakerChunksSent = 0;
        private long _sequenceNumber = 0;
        private DateTime _lastAudioSentTime = DateTime.MinValue;
        private long _totalBytesSent = 0;

        // ClientWebSocket.SendAsync is NOT thread-safe. Because microphone and
        // speaker audio arrive on independent threads, all sends must be serialized.
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);

        // Log diagnostic stats every N chunks (per source) to avoid log noise
        private const int DiagnosticLogIntervalChunks = 500;

        // When true, log every audio chunk send at Debug level (for backend team debugging)
        private readonly bool _logEveryChunk = false;

        // Active call tracking for call_ended event
        private string _activeCallId = null;

        // Audio gating: only send audio_chunk after call_accepted, reset on call_ended/call_rejected
        private volatile bool _callAccepted = false;

        // Microphone mute gate: when true, mic audio chunks are silently dropped (speaker audio continues)
        private volatile bool _isMicrophoneMuted = false;

        /// <summary>
        /// Gets or sets whether the microphone audio is muted.
        /// When muted, microphone audio chunks are silently dropped (not sent to backend).
        /// Speaker audio is unaffected.
        /// </summary>
        public bool IsMicrophoneMuted
        {
            get => _isMicrophoneMuted;
            set
            {
                _isMicrophoneMuted = value;
                LoggingService.Info($"[BackendAudio] Microphone mute state changed: {(value ? "MUTED" : "UNMUTED")}");
            }
        }

        // Store last call_incoming params for re-send after reconnection
        private string _lastCallId;
        private string _lastCsrId;
        private object _lastCustomer;
        private object _lastEmployee;

        // =====================================================================
        // Connection state enumeration
        // =====================================================================
        public enum ConnectionState
        {
            Disconnected,
            Connecting,
            Connected,
            Reconnecting,
            Ringing,
            Active
        }

        private ConnectionState _connectionState = ConnectionState.Disconnected;

        // =====================================================================
        // Events
        // =====================================================================

        /// <summary>Fired when the connection status changes.</summary>
        public event Action<ConnectionState> ConnectionStatusChanged;

        /// <summary>Fired when successfully connected to the backend.</summary>
        public event Action Connected;

        /// <summary>Fired when disconnected from the backend. Includes reason.</summary>
        public event Action<string> Disconnected;

        /// <summary>Fired when a transcript message is received from the backend.</summary>
        public event Action<string, bool, string> TranscriptReceived; // text, isFinal, rawJson

        /// <summary>Fired when an agent response message is received from the backend.</summary>
        public event Action<string, string> AgentResponseReceived; // responseText, rawJson

        /// <summary>Fired when an error message is received from the backend.</summary>
        public event Action<string> ErrorOccurred;

        /// <summary>Fired when a session status message is received from the backend.</summary>
        public event Action<string, string> SessionStatusReceived; // status, rawJson

        /// <summary>Fired when reconnection attempts are exhausted.</summary>
        public event Action ReconnectionFailed;

        /// <summary>Fired after each audio chunk is sent, with the WebSocket send latency in milliseconds.</summary>
        public event Action<double> AudioChunkLatencyMeasured;

        /// <summary>Fired when the backend sends call_ended (dashboard ended the call). Parameters: callId, reason, durationSecs.</summary>
        public event Action<string, string, int> CallEndedReceived;

        /// <summary>Fired when the backend sends call_accepted (dashboard accepted the call). Parameters: callId, sessionId, conversationId, agentId.</summary>
        public event Action<string, string, string, string> CallAcceptedReceived;

        /// <summary>Fired when the backend sends call_rejected (dashboard rejected the call). Parameters: callId, reason.</summary>
        public event Action<string, string> CallRejectedReceived;

        /// <summary>Fired when the backend ACKs call_incoming. Parameters: callId, sessionId.</summary>
        public event Action<string, string> CallIncomingAcked;

        // =====================================================================
        // Public properties
        // =====================================================================

        /// <summary>Whether the service is currently connected and the WebSocket is open.</summary>
        public bool IsConnected => _isConnected && _webSocket?.State == WebSocketState.Open;

        /// <summary>Current connection state.</summary>
        public ConnectionState CurrentConnectionState => _connectionState;

        /// <summary>Number of microphone audio chunks sent in this session.</summary>
        public long MicChunksSent => _micChunksSent;

        /// <summary>Number of speaker audio chunks sent in this session.</summary>
        public long SpeakerChunksSent => _speakerChunksSent;

        /// <summary>Last measured WebSocket send latency in milliseconds.</summary>
        public double LastSendLatencyMs { get; private set; }

        // =====================================================================
        // Constructor
        // =====================================================================

        /// <summary>
        /// Creates a new BackendAudioStreamingService instance.
        /// </summary>
        /// <param name="webSocketUrl">Backend audio streaming WebSocket URL.</param>
        /// <param name="sampleRate">Audio sample rate in Hz (default 16000).</param>
        /// <param name="bitDepth">Audio bit depth (default 16).</param>
        /// <param name="channels">Number of audio channels (default 1 = mono).</param>
        /// <param name="chunkingIntervalMs">Interval between audio chunks in ms (default 200).</param>
        /// <param name="maxReconnectionAttempts">Maximum reconnection attempts before giving up (default 10).</param>
        /// <param name="baseReconnectionDelayMs">Base delay in ms for exponential backoff (default 1000).</param>
        /// <param name="pingIntervalSeconds">Interval in seconds between health ping messages (default 30).</param>
        /// <param name="pongTimeoutSeconds">Seconds to wait for pong before considering connection stale (default 10).</param>
        /// <param name="connectionTimeoutSeconds">Seconds to wait for the initial WebSocket handshake (default 20).</param>
        public BackendAudioStreamingService(
            string webSocketUrl,
            int sampleRate = 16000,
            int bitDepth = 16,
            int channels = 1,
            int chunkingIntervalMs = 200,
            int maxReconnectionAttempts = 10,
            int baseReconnectionDelayMs = 1000,
            int pingIntervalSeconds = 30,
            int pongTimeoutSeconds = 10,
            int connectionTimeoutSeconds = 20,
            bool logEveryChunk = false)
        {
            _webSocketUrl = webSocketUrl ?? throw new ArgumentNullException(nameof(webSocketUrl));
            _sampleRate = sampleRate;
            _bitDepth = bitDepth;
            _channels = channels;
            _chunkingIntervalMs = chunkingIntervalMs;
            _maxReconnectionAttempts = maxReconnectionAttempts;
            _baseReconnectionDelayMs = baseReconnectionDelayMs;
            _pingIntervalSeconds = pingIntervalSeconds;
            _pongTimeoutSeconds = pongTimeoutSeconds;
            _connectionTimeoutSeconds = connectionTimeoutSeconds;
            _logEveryChunk = logEveryChunk;

            LoggingService.Info($"[BackendAudio] Service created - URL: {MaskTokenInUrl(_webSocketUrl)}, SampleRate: {_sampleRate}, BitDepth: {_bitDepth}, Channels: {_channels}, ConnTimeout: {_connectionTimeoutSeconds}s, LogEveryChunk: {_logEveryChunk}");
        }

        // =====================================================================
        // Factory: create from appsettings.json configuration
        // =====================================================================

        /// <summary>
        /// Creates a BackendAudioStreamingService using values from the
        /// <c>BackendAudioStreaming</c> section of <c>appsettings.json</c>
        /// (accessed via <see cref="Globals.ConfigurationInfo"/>).
        /// Falls back to sensible defaults when a value is missing.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The WebSocket URL is resolved in this priority order:
        /// <list type="number">
        ///   <item><c>BackendAudioStreaming:WebSocketUrl</c> – explicit override in appsettings.json</item>
        ///   <item>Derived from <c>Backend:BaseUrl</c> – converts scheme to wss:// and appends <see cref="DefaultWebSocketPathPrefix"/></item>
        /// </list>
        /// The user ID (from JWT) and token query parameter are appended at connection time,
        /// resulting in <c>/ws/audio/{user_id}?token={jwt}</c>.
        /// </para>
        /// <para>
        /// Note: <c>Globals.FrontendSocketUrl</c> is NOT used here. That URL points to the
        /// general user WebSocket endpoint (<c>/ws/{user_id}</c>) and is intended for
        /// <see cref="FrontendIntegrationService"/>. This service connects to the
        /// dedicated audio streaming endpoint at <see cref="DefaultWebSocketPathPrefix"/>.
        /// </para>
        /// Configuration keys read from the <c>BackendAudioStreaming</c> section:
        /// <list type="bullet">
        ///   <item><c>WebSocketUrl</c>  – full WSS URL (optional; overrides derived URL)</item>
        ///   <item><c>SampleRate</c>    – audio sample rate in Hz  (default 16000)</item>
        ///   <item><c>BitDepth</c>      – bits per sample          (default 16)</item>
        ///   <item><c>Channels</c>      – mono = 1, stereo = 2     (default 1)</item>
        ///   <item><c>ChunkingIntervalMs</c> – ms between chunks   (default 200)</item>
        ///   <item><c>MaxReconnectionAttempts</c>                   (default 10)</item>
        ///   <item><c>BaseReconnectionDelayMs</c>                   (default 1000)</item>
        ///   <item><c>PingIntervalSeconds</c>                       (default 30)</item>
        ///   <item><c>PongTimeoutSeconds</c>                        (default 10)</item>
        ///   <item><c>ConnectionTimeoutSeconds</c>                  (default 20)</item>
        /// </list>
        /// </remarks>
        public static BackendAudioStreamingService CreateFromConfiguration()
        {
            string webSocketUrl = null;

            try
            {
                var config = Globals.ConfigurationInfo;
                if (config != null)
                {
                    var section = config.GetSection(ConfigSectionName);

                    // Priority 1: Use explicit URL from BackendAudioStreaming:WebSocketUrl config
                    webSocketUrl = section.GetValue<string>("WebSocketUrl", null);

                    if (string.IsNullOrWhiteSpace(webSocketUrl))
                    {
                        // Priority 2: Derive from Backend:BaseUrl (converts https to wss, appends /ws/audio-stream)
                        webSocketUrl = BuildWebSocketUrlFromBackendBase(config);
                    }

                    // --- Audio format ---
                    int sampleRate     = section.GetValue<int>("SampleRate", DefaultSampleRate);
                    int bitDepth       = section.GetValue<int>("BitDepth", DefaultBitDepth);
                    int channels       = section.GetValue<int>("Channels", DefaultChannels);
                    int chunkInterval  = section.GetValue<int>("ChunkingIntervalMs", DefaultChunkingIntervalMs);

                    // --- Reconnection ---
                    int maxReconnect   = section.GetValue<int>("MaxReconnectionAttempts", DefaultMaxReconnectionAttempts);
                    int reconnectDelay = section.GetValue<int>("BaseReconnectionDelayMs", DefaultBaseReconnectionDelayMs);

                    // --- Health ping ---
                    int pingInterval   = section.GetValue<int>("PingIntervalSeconds", DefaultPingIntervalSeconds);
                    int pongTimeout    = section.GetValue<int>("PongTimeoutSeconds", DefaultPongTimeoutSeconds);

                    // --- Connection ---
                    int connTimeout    = section.GetValue<int>("ConnectionTimeoutSeconds", DefaultConnectionTimeoutSeconds);

                    // --- Diagnostics ---
                    bool logEveryChunk = section.GetValue<bool>("LogEveryChunk", false);

                    LoggingService.Info($"[BackendAudio] Configuration loaded from {ConfigSectionName} section");

                    return new BackendAudioStreamingService(
                        webSocketUrl,
                        sampleRate,
                        bitDepth,
                        channels,
                        chunkInterval,
                        maxReconnect,
                        reconnectDelay,
                        pingInterval,
                        pongTimeout,
                        connTimeout,
                        logEveryChunk);
                }
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"[BackendAudio] Error reading configuration: {ex.Message} - using defaults");
            }

            // Fallback: derive from Backend:BaseUrl
            if (string.IsNullOrWhiteSpace(webSocketUrl))
            {
                webSocketUrl = BuildWebSocketUrlFromBackendBase(Globals.ConfigurationInfo);
            }

            if (string.IsNullOrWhiteSpace(webSocketUrl))
            {
                throw new InvalidOperationException(
                    "Cannot create BackendAudioStreamingService: no WebSocket URL configured. " +
                    $"Set '{ConfigSectionName}:WebSocketUrl' or 'Backend:BaseUrl' in appsettings.json.");
            }

            return new BackendAudioStreamingService(webSocketUrl);
        }

        /// <summary>
        /// Derives the audio streaming WebSocket base URL from the existing
        /// <c>Backend:BaseUrl</c> setting by converting the scheme to
        /// <c>wss://</c> (or <c>ws://</c>) and appending <see cref="DefaultWebSocketPathPrefix"/>.
        /// The user ID and token query parameter are appended at connection time.
        /// </summary>
        private static string BuildWebSocketUrlFromBackendBase(IConfiguration config)
        {
            try
            {
                if (config == null) return null;

                string baseUrl = config.GetSection("Backend").GetValue<string>("BaseUrl", null);
                if (string.IsNullOrWhiteSpace(baseUrl)) return null;

                // Trim trailing slashes and /api/ suffix so we get the host root
                baseUrl = baseUrl.TrimEnd('/');
                if (baseUrl.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
                {
                    baseUrl = baseUrl.Substring(0, baseUrl.Length - 4);
                }

                // Convert HTTP(S) scheme to WS(S)
                if (baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    baseUrl = "wss://" + baseUrl.Substring("https://".Length);
                }
                else if (baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                {
                    baseUrl = "ws://" + baseUrl.Substring("http://".Length);
                }

                string derivedUrl = baseUrl.TrimEnd('/') + DefaultWebSocketPathPrefix;
                LoggingService.Info($"[BackendAudio] Derived WebSocket base URL from Backend:BaseUrl: {derivedUrl}");
                return derivedUrl;
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"[BackendAudio] Could not derive WebSocket URL from Backend:BaseUrl: {ex.Message}");
                return null;
            }
        }

        // =====================================================================
        // Connection management
        // =====================================================================

        /// <summary>
        /// Initialize WebSocket connection to the backend audio streaming endpoint.
        /// Uses the JWT token from <see cref="Globals.BackendAccessToken"/> for authentication.
        /// </summary>
        /// <returns>True if connected successfully, false otherwise.</returns>
        public Task<bool> ConnectAsync()
        {
            return ConnectAsync(null);
        }

        /// <summary>
        /// Initialize WebSocket connection to the backend audio streaming endpoint
        /// with an explicit JWT token.
        /// <para>
        /// When <paramref name="jwtToken"/> is <c>null</c>, the token is read from
        /// <see cref="Globals.BackendAccessToken"/>. The resolved token is stored
        /// internally so that automatic reconnection attempts can re-authenticate
        /// without requiring the caller to supply the token again.
        /// </para>
        /// <para>
        /// Authentication is performed via the <c>token</c> query parameter on the
        /// WebSocket URL. The backend endpoint is <c>/ws/audio/{user_id}?token={jwt}</c>.
        /// The user ID is extracted from the JWT token's <c>sub</c> claim.
        /// </para>
        /// </summary>
        /// <param name="jwtToken">
        /// Optional JWT token. Pass <c>null</c> to use <see cref="Globals.BackendAccessToken"/>.
        /// </param>
        /// <returns>True if connected successfully, false otherwise.</returns>
        public async Task<bool> ConnectAsync(string jwtToken = null)
        {
            try
            {
                if (_isConnected && _webSocket?.State == WebSocketState.Open)
                {
                    LoggingService.Info("[BackendAudio] Already connected");
                    return true;
                }

                SetConnectionState(ConnectionState.Connecting);

                // Clean up existing connection if reconnecting
                await CleanupExistingConnectionAsync();

                // Resolve JWT token: explicit parameter > stored token > Globals
                string resolvedToken = ResolveJwtToken(jwtToken);

                if (string.IsNullOrEmpty(resolvedToken))
                {
                    LoggingService.Error("[BackendAudio] No valid JWT token available - cannot connect");
                    SetConnectionState(ConnectionState.Disconnected);
                    return false;
                }

                // Extract user_id from JWT sub claim for the URL path
                string userId = ExtractUserIdFromJwt(resolvedToken);
                if (string.IsNullOrEmpty(userId))
                {
                    LoggingService.Error("[BackendAudio] Could not extract user_id from JWT token - cannot connect");
                    SetConnectionState(ConnectionState.Disconnected);
                    return false;
                }

                // Store user ID globally for other services
                Globals.UserId = userId;

                // Build the full WebSocket URL: {baseUrl}/{user_id}?token={jwt_token}
                string effectiveUrl = $"{_webSocketUrl.TrimEnd('/')}/{Uri.EscapeDataString(userId)}?token={Uri.EscapeDataString(resolvedToken)}";

                // Create new WebSocket via factory
                _webSocket = WebSocketFactory.CreateCompatibleWebSocket();
                _cancellationTokenSource = new CancellationTokenSource();

                LoggingService.Info($"[BackendAudio] Connecting to backend: {MaskTokenInUrl(effectiveUrl)} (timeout: {_connectionTimeoutSeconds}s)");

                // Connect with configurable timeout
                using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_connectionTimeoutSeconds)))
                using (var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    _cancellationTokenSource.Token, timeoutCts.Token))
                {
                    await _webSocket.ConnectAsync(new Uri(effectiveUrl), combinedCts.Token);
                }

                if (_webSocket.State == WebSocketState.Open)
                {
                    _isConnected = true;
                    _reconnectionAttempts = 0;
                    _connectionLostTime = DateTime.MinValue;
                    _lastPongReceivedTime = DateTime.UtcNow;

                    // Reset counters for new session
                    _micChunksSent = 0;
                    _speakerChunksSent = 0;
                    _sequenceNumber = 0;
                    _totalBytesSent = 0;
                    _activeCallId = null;

                    // Start background message listener (no app-level ping; protocol-level keepalive is sufficient)
                    _messageListener = Task.Run(() => StartMessageListenerAsync());

                    // Start periodic latency measurement (HTTP ping to backend host)
                    _latencyPingUrl = DeriveLatencyPingUrl();
                    _latencyMeasurementTask = Task.Run(() => StartLatencyMeasurementAsync());

                    SetConnectionState(ConnectionState.Connected);
                    Connected?.Invoke();

                    LoggingService.Info("[BackendAudio] Connected successfully");
                    return true;
                }
                else
                {
                    LoggingService.Error($"[BackendAudio] Connection failed. State: {_webSocket.State}");
                    SetConnectionState(ConnectionState.Disconnected);
                    return false;
                }
            }
            catch (OperationCanceledException)
            {
                LoggingService.Error($"[BackendAudio] Connection timed out after {_connectionTimeoutSeconds}s");
                SetConnectionState(ConnectionState.Disconnected);
                return false;
            }
            catch (WebSocketException wsEx)
            {
                LoggingService.Error($"[BackendAudio] WebSocket connection error: {wsEx.Message}, WebSocketError: {wsEx.WebSocketErrorCode}");
                if (wsEx.InnerException != null)
                {
                    LoggingService.Error($"[BackendAudio] Inner exception: {wsEx.InnerException.Message}");
                }
                ErrorOccurred?.Invoke($"Connection error: {wsEx.Message}");
                SetConnectionState(ConnectionState.Disconnected);
                return false;
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[BackendAudio] Connection error: {ex.Message}");
                ErrorOccurred?.Invoke($"Connection error: {ex.Message}");
                SetConnectionState(ConnectionState.Disconnected);
                return false;
            }
        }

        /// <summary>
        /// Extracts the user ID from a JWT token's <c>sub</c> or <c>user_id</c> claim.
        /// Parses the token payload without cryptographic verification (the backend
        /// will verify the full token via the query parameter).
        /// </summary>
        private static string ExtractUserIdFromJwt(string token)
        {
            try
            {
                // JWT format: header.payload.signature — decode the payload (part 2)
                var parts = token.Split('.');
                if (parts.Length < 2)
                {
                    LoggingService.Warn("[BackendAudio] JWT token does not have expected format");
                    return null;
                }

                // Base64Url decode the payload
                string payload = parts[1];
                // Pad base64 string if necessary
                switch (payload.Length % 4)
                {
                    case 2: payload += "=="; break;
                    case 3: payload += "="; break;
                }
                payload = payload.Replace('-', '+').Replace('_', '/');

                var jsonBytes = Convert.FromBase64String(payload);
                var json = Encoding.UTF8.GetString(jsonBytes);

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Try "sub" first (JWT standard), then "user_id" (backward compat)
                if (root.TryGetProperty("sub", out var subProp))
                {
                    string sub = subProp.ToString();
                    if (!string.IsNullOrEmpty(sub))
                    {
                        LoggingService.Info($"[BackendAudio] Extracted user_id from JWT sub claim: {sub}");
                        return sub;
                    }
                }

                if (root.TryGetProperty("user_id", out var userIdProp))
                {
                    string uid = userIdProp.ToString();
                    if (!string.IsNullOrEmpty(uid))
                    {
                        LoggingService.Info($"[BackendAudio] Extracted user_id from JWT user_id claim: {uid}");
                        return uid;
                    }
                }

                LoggingService.Warn("[BackendAudio] JWT token has no sub or user_id claim");
                return null;
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[BackendAudio] Error extracting user_id from JWT: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Resolves the JWT token to use for authentication.
        /// Priority: explicit parameter > latest Globals value > previously stored token.
        /// The resolved token is stored for future reconnection attempts.
        /// </summary>
        private string ResolveJwtToken(string explicitToken)
        {
            // 1. Use explicit token if provided
            if (!string.IsNullOrEmpty(explicitToken) &&
                explicitToken != "offline_mode_token" &&
                explicitToken != "no_auth_websocket_only")
            {
                _storedJwtToken = explicitToken;
                return explicitToken;
            }

            // 2. Use latest token from Globals (may have been refreshed by TokenRefreshService)
            string globalsToken = Globals.BackendAccessToken;
            if (!string.IsNullOrEmpty(globalsToken) &&
                globalsToken != "offline_mode_token" &&
                globalsToken != "no_auth_websocket_only")
            {
                _storedJwtToken = globalsToken;
                return globalsToken;
            }

            // 3. Fall back to previously stored token (from initial connection)
            if (!string.IsNullOrEmpty(_storedJwtToken))
            {
                return _storedJwtToken;
            }

            return null;
        }

        /// <summary>
        /// Send a <c>call_started</c> event to the backend to create a call session.
        /// Must be sent before any <c>audio_chunk</c> messages.
        /// </summary>
        /// <param name="callId">Unique call identifier (alphanumeric with hyphens/underscores).</param>
        /// <param name="csrId">CSR/agent user ID.</param>
        /// <param name="customer">Customer info dictionary (name, phone, account_id).</param>
        /// <param name="employee">Employee info dictionary (name, id, department).</param>
        public async Task<bool> SendCallIncomingAsync(string callId, string csrId, object customer, object employee)
        {
            try
            {
                if (!IsConnected) return false;

                // Reset audio gate — audio will only flow after call_accepted
                _callAccepted = false;

                var callIncoming = new
                {
                    event_type = "call_incoming",
                    call_id = callId,
                    csr_id = csrId,
                    customer = customer,
                    employee = employee,
                    timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
                };

                string json = JsonSerializer.Serialize(callIncoming);
                LoggingService.Info($"[BackendAudio] Sending call_incoming JSON: {json}");

                await SendJsonMessageAsync(callIncoming);
                _activeCallId = callId;

                // Store params for re-send after reconnection
                _lastCallId = callId;
                _lastCsrId = csrId;
                _lastCustomer = customer;
                _lastEmployee = employee;

                LoggingService.Info($"[BackendAudio] call_incoming sent successfully - call_id: {callId}");

                // Transition to Ringing state per protocol state machine
                SetConnectionState(ConnectionState.Ringing);

                return true;
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[BackendAudio] Error sending call_incoming: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Send a <c>call_ended</c> event to the backend to end the active call session.
        /// </summary>
        /// <param name="callId">Call ID to end. If null, uses the active call ID from <see cref="SendCallIncomingAsync"/>.</param>
        /// <param name="reason">Reason for ending the call (default: "completed").</param>
        public async Task<bool> SendCallEndedAsync(string callId = null, string reason = "completed")
        {
            try
            {
                if (!IsConnected)
                {
                    LoggingService.Warn($"[BackendAudio] Cannot send call_ended - WebSocket not connected (State: {_webSocket?.State})");
                    return false;
                }

                string effectiveCallId = callId ?? _activeCallId;
                if (string.IsNullOrEmpty(effectiveCallId))
                {
                    LoggingService.Warn("[BackendAudio] Cannot send call_ended - no active call ID");
                    return false;
                }

                var callEnded = new
                {
                    event_type = "call_ended",
                    call_id = effectiveCallId,
                    reason = reason,
                    timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
                };

                string json = JsonSerializer.Serialize(callEnded);
                LoggingService.Info($"[BackendAudio] Sending call_ended JSON: {json}");

                await SendJsonMessageAsync(callEnded);
                LoggingService.Info($"[BackendAudio] call_ended sent successfully - call_id: {effectiveCallId}, reason: {reason}");
                _activeCallId = null;
                _callAccepted = false;

                // Transition back to Connected (idle) per protocol state machine
                SetConnectionState(ConnectionState.Connected);

                // Clear stored call params so reconnection doesn't re-send call_started
                _lastCallId = null;
                _lastCsrId = null;
                _lastCustomer = null;
                _lastEmployee = null;
                return true;
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[BackendAudio] Error sending call_ended: {ex.Message}");
                return false;
            }
        }

        // =====================================================================
        // Audio streaming methods
        // =====================================================================

        /// <summary>
        /// Send a microphone audio chunk to the backend.
        /// Audio must be raw PCM bytes matching the configured sample rate, bit depth, and channels.
        /// Thread-safe: concurrent calls from mic and speaker threads are serialized internally.
        /// </summary>
        /// <param name="audioData">Raw PCM audio bytes (must not be null or empty).</param>
        /// <returns>True if sent successfully, false otherwise.</returns>
        public async Task<bool> SendMicrophoneAudioAsync(byte[] audioData)
        {
            if (audioData == null || audioData.Length == 0)
            {
                LoggingService.Debug("[BackendAudio] SendMicrophoneAudioAsync called with null/empty data - skipping");
                return false;
            }

            // Drop mic audio when muted (speaker audio continues unaffected)
            if (_isMicrophoneMuted)
            {
                return true; // Silently succeed — caller doesn't need to know
            }

            return await SendAudioChunkAsync(audioData, "microphone");
        }

        /// <summary>
        /// Send a speaker audio chunk to the backend.
        /// Audio must be raw PCM bytes matching the configured sample rate, bit depth, and channels.
        /// Thread-safe: concurrent calls from mic and speaker threads are serialized internally.
        /// </summary>
        /// <param name="audioData">Raw PCM audio bytes (must not be null or empty).</param>
        /// <returns>True if sent successfully, false otherwise.</returns>
        public async Task<bool> SendSpeakerAudioAsync(byte[] audioData)
        {
            if (audioData == null || audioData.Length == 0)
            {
                LoggingService.Debug("[BackendAudio] SendSpeakerAudioAsync called with null/empty data - skipping");
                return false;
            }

            return await SendAudioChunkAsync(audioData, "speaker");
        }

        /// <summary>
        /// Sends an audio chunk to the backend with source identification.
        /// All sends are serialized via <see cref="_sendLock"/> because
        /// <c>ClientWebSocket.SendAsync</c> is not thread-safe and mic/speaker
        /// audio arrives on independent threads.
        /// </summary>
        private async Task<bool> SendAudioChunkAsync(byte[] audioData, string source)
        {
            try
            {
                // Audio gating: only send after call_accepted (protocol requirement)
                if (!_callAccepted)
                {
                    return false;
                }

                // Quick pre-lock checks to avoid acquiring the semaphore unnecessarily
                if (_webSocket?.State != WebSocketState.Open)
                {
                    if (_isConnected)
                    {
                        _isConnected = false;
                        string errorMsg = $"WebSocket state changed to {_webSocket?.State}";
                        HandleDisconnection(errorMsg);
                    }
                    return false;
                }

                if (!_isConnected) return false;

                // Build the message payload outside the lock (CPU work, no I/O)
                string audioBase64 = Convert.ToBase64String(audioData);
                long seqNum = Interlocked.Increment(ref _sequenceNumber);

                // Backend expects: event_type, call_id, channel ("mic"/"speaker"), audio_base_64, sample_rate, timestamp, sequence_number
                string channel = source == "microphone" ? "mic" : source;

                var audioMessage = new
                {
                    event_type = "audio_chunk",
                    call_id = _activeCallId,
                    channel = channel,
                    audio_base_64 = audioBase64,
                    sample_rate = _sampleRate,
                    timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    sequence_number = seqNum
                };

                string json = JsonSerializer.Serialize(audioMessage);
                var sendBuffer = Encoding.UTF8.GetBytes(json);

                // Serialize the actual WebSocket send
                await _sendLock.WaitAsync(_cancellationTokenSource.Token);
                try
                {
                    // Re-check connection state inside the lock
                    if (_webSocket?.State != WebSocketState.Open || !_isConnected)
                    {
                        return false;
                    }

                    await _webSocket.SendAsync(
                        new ArraySegment<byte>(sendBuffer),
                        WebSocketMessageType.Text,
                        true,
                        _cancellationTokenSource.Token);
                }
                finally
                {
                    _sendLock.Release();
                }

                // Track statistics (lock-free via Interlocked)
                Interlocked.Add(ref _totalBytesSent, audioData.Length);

                if (source == "microphone")
                {
                    long count = Interlocked.Increment(ref _micChunksSent);
                    if (count == 1)
                    {
                        LoggingService.Info($"[BackendAudio] First microphone audio chunk sent - seq: {seqNum}, call_id: {_activeCallId}, pcm: {audioData.Length} bytes, payload: {sendBuffer.Length} bytes");
                    }
                    else if (count % DiagnosticLogIntervalChunks == 0)
                    {
                        LoggingService.Info($"[BackendAudio] Microphone stats: {count} chunks sent, total bytes: {Interlocked.Read(ref _totalBytesSent)}");
                    }
                }
                else
                {
                    long count = Interlocked.Increment(ref _speakerChunksSent);
                    if (count == 1)
                    {
                        LoggingService.Info($"[BackendAudio] First speaker audio chunk sent - seq: {seqNum}, call_id: {_activeCallId}, pcm: {audioData.Length} bytes, payload: {sendBuffer.Length} bytes");
                    }
                    else if (count % DiagnosticLogIntervalChunks == 0)
                    {
                        LoggingService.Info($"[BackendAudio] Speaker stats: {count} chunks sent, total bytes: {Interlocked.Read(ref _totalBytesSent)}");
                    }
                }

                // Per-chunk debug logging (when enabled in config: LogEveryChunk=true)
                if (_logEveryChunk)
                {
                    LoggingService.Debug($"[BackendAudio] audio_chunk #{seqNum} sent - call_id: {_activeCallId}, channel: {channel}, pcm: {audioData.Length} bytes, base64: {audioBase64.Length} chars, sample_rate: {_sampleRate}");
                }

                _lastAudioSentTime = DateTime.UtcNow;
                return true;
            }
            catch (WebSocketException wsEx)
            {
                LoggingService.Error($"[BackendAudio] WebSocket error sending {source} audio: {wsEx.Message}, WebSocketError: {wsEx.WebSocketErrorCode}, State: {_webSocket?.State}");
                if (wsEx.InnerException != null)
                {
                    LoggingService.Error($"[BackendAudio] Inner exception: {wsEx.InnerException.Message}");
                }
                _isConnected = false;
                HandleDisconnection($"WebSocket error: {wsEx.Message}");
                return false;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[BackendAudio] Error sending {source} audio: {ex.Message}");
                return false;
            }
        }

        // =====================================================================
        // Message listener
        // =====================================================================

        /// <summary>
        /// Background task that listens for messages from the backend WebSocket.
        /// </summary>
        private async Task StartMessageListenerAsync()
        {
            var buffer = new byte[4096];
            var messageBuffer = new List<byte>();

            try
            {
                LoggingService.Info("[BackendAudio] Message listener started");

                while (_webSocket?.State == WebSocketState.Open && !_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    try
                    {
                        if (_webSocket?.State == WebSocketState.CloseReceived ||
                            _webSocket?.State == WebSocketState.CloseSent ||
                            _webSocket?.State == WebSocketState.Closed)
                        {
                            if (_isConnected)
                            {
                                _isConnected = false;
                                HandleDisconnection($"WebSocket state changed to {_webSocket?.State}");
                            }
                            break;
                        }

                        var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), _cancellationTokenSource.Token);

                        if (result.MessageType == WebSocketMessageType.Text)
                        {
                            messageBuffer.AddRange(new ArraySegment<byte>(buffer, 0, result.Count));

                            if (result.EndOfMessage)
                            {
                                string message = Encoding.UTF8.GetString(messageBuffer.ToArray());
                                ProcessBackendMessage(message);
                                messageBuffer.Clear();
                            }
                        }
                        else if (result.MessageType == WebSocketMessageType.Close)
                        {
                            string closeStatus = result.CloseStatus?.ToString() ?? "Unknown";
                            string closeDescription = result.CloseStatusDescription ?? "No description";
                            LoggingService.Warn($"[BackendAudio] Received Close message - Status: {closeStatus}, Description: {closeDescription}");

                            if (_isConnected)
                            {
                                _isConnected = false;
                                HandleDisconnection($"Connection closed by server - {closeStatus}: {closeDescription}");
                            }
                            break;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        LoggingService.Info("[BackendAudio] Message receive cancelled");
                        break;
                    }
                    catch (WebSocketException wsEx)
                    {
                        LoggingService.Error($"[BackendAudio] WebSocket receive error: {wsEx.Message}, WebSocketError: {wsEx.WebSocketErrorCode}");
                        break;
                    }
                    catch (Exception ex)
                    {
                        LoggingService.Error($"[BackendAudio] Message receive error: {ex.Message}");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[BackendAudio] Message listener error: {ex.Message}");
            }
            finally
            {
                if (_isConnected)
                {
                    _isConnected = false;
                    HandleDisconnection("Message listener stopped");
                }
                LoggingService.Info($"[BackendAudio] Message listener stopped - WebSocket State: {_webSocket?.State}");
            }
        }

        /// <summary>
        /// Process incoming messages from the backend WebSocket.
        /// Expected message types: transcript, agent_response, error, session_status, pong
        /// </summary>
        private void ProcessBackendMessage(string message)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(message)) return;

                if (message.Length < 500)
                {
                    LoggingService.Debug($"[BackendAudio] Received: {message}");
                }
                else
                {
                    LoggingService.Debug($"[BackendAudio] Received (truncated): {message.Substring(0, 500)}...");
                }

                JsonDocument jsonDoc;
                try
                {
                    jsonDoc = JsonDocument.Parse(message);
                }
                catch (JsonException)
                {
                    LoggingService.Warn($"[BackendAudio] Invalid JSON received: {message.Substring(0, Math.Min(200, message.Length))}");
                    return;
                }

                using (jsonDoc)
                {
                    var root = jsonDoc.RootElement;

                    // Backend uses "event_type"; support "type" and "message_type" for forward compat
                    string messageType = null;
                    if (root.TryGetProperty("event_type", out var eventTypeProp))
                    {
                        messageType = eventTypeProp.GetString();
                    }
                    else if (root.TryGetProperty("type", out var typeProp))
                    {
                        messageType = typeProp.GetString();
                    }
                    else if (root.TryGetProperty("message_type", out var messageTypeProp))
                    {
                        messageType = messageTypeProp.GetString();
                    }

                    if (string.IsNullOrEmpty(messageType))
                    {
                        LoggingService.Warn($"[BackendAudio] Message has no event_type field: {message.Substring(0, Math.Min(200, message.Length))}");
                        return;
                    }

                    switch (messageType)
                    {
                        case "connected":
                            HandleConnectedResponse(root, message);
                            break;

                        case "call_started":
                            HandleCallStartedResponse(root, message);
                            break;

                        case "call_incoming":
                            HandleCallIncomingAckResponse(root, message);
                            break;

                        case "call_accepted":
                        case "call_accept":
                            HandleCallAcceptedResponse(root, message);
                            break;

                        case "call_rejected":
                        case "call_reject":
                            HandleCallRejectedResponse(root, message);
                            break;

                        case "call_ended":
                            HandleCallEndedResponse(root, message);
                            break;

                        case "transcript":
                        case "transcript_turn":
                            HandleTranscriptMessage(root, message);
                            break;

                        case "agent_response":
                            HandleAgentResponseMessage(root, message);
                            break;

                        case "error":
                            HandleErrorMessage(root, message);
                            break;

                        case "session_status":
                            HandleSessionStatusMessage(root, message);
                            break;

                        case "pong":
                            _lastPongReceivedTime = DateTime.UtcNow;
                            LoggingService.Debug("[BackendAudio] Pong received");
                            break;

                        default:
                            LoggingService.Debug($"[BackendAudio] Unhandled message type: {messageType}");
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[BackendAudio] Error processing backend message: {ex.Message}");
            }
        }

        private void HandleConnectedResponse(JsonElement root, string rawJson)
        {
            string userId = root.TryGetProperty("user_id", out var uidProp) ? uidProp.GetString() : null;
            string msg = root.TryGetProperty("message", out var msgProp) ? msgProp.GetString() : null;
            LoggingService.Info($"[BackendAudio] Backend connected event - user_id: {userId}, message: {msg}");
        }

        private void HandleCallStartedResponse(JsonElement root, string rawJson)
        {
            string sessionId = root.TryGetProperty("session_id", out var sidProp) ? sidProp.GetString() : null;
            string status = root.TryGetProperty("status", out var statusProp) ? statusProp.GetString() : null;
            string callId = root.TryGetProperty("call_id", out var cidProp) ? cidProp.GetString() : null;
            LoggingService.Info($"[BackendAudio] Backend confirmed call_started - call_id: {callId}, session_id: {sessionId}, status: {status}");
            SessionStatusReceived?.Invoke(status ?? "active", rawJson);
        }

        private void HandleCallIncomingAckResponse(JsonElement root, string rawJson)
        {
            string callId = root.TryGetProperty("call_id", out var cidProp) ? cidProp.GetString() : null;
            string sessionId = root.TryGetProperty("session_id", out var sidProp) ? sidProp.GetString() : null;
            string status = root.TryGetProperty("status", out var statusProp) ? statusProp.GetString() : null;
            LoggingService.Info($"[BackendAudio] call_incoming ACK received - call_id: {callId}, session_id: {sessionId}, status: {status}");
            CallIncomingAcked?.Invoke(callId, sessionId);
        }

        private void HandleCallAcceptedResponse(JsonElement root, string rawJson)
        {
            string callId = root.TryGetProperty("call_id", out var cidProp) ? cidProp.GetString() : null;
            string sessionId = root.TryGetProperty("session_id", out var sidProp) ? sidProp.GetString() : null;
            string conversationId = root.TryGetProperty("conversation_id", out var convProp) ? convProp.GetString() : null;
            string agentId = root.TryGetProperty("agent_id", out var agentProp) ? agentProp.GetString() : null;
            LoggingService.Info($"[BackendAudio] call_accepted received - call_id: {callId}, session_id: {sessionId}, conversation_id: {conversationId}, agent_id: {agentId}");

            // Open the audio gate — audio_chunk events can now be sent
            _callAccepted = true;

            // Transition to Active state per protocol state machine
            SetConnectionState(ConnectionState.Active);

            CallAcceptedReceived?.Invoke(callId, sessionId, conversationId, agentId);
        }

        private void HandleCallRejectedResponse(JsonElement root, string rawJson)
        {
            string callId = root.TryGetProperty("call_id", out var cidProp) ? cidProp.GetString() : null;
            string reason = root.TryGetProperty("reason", out var rProp) ? rProp.GetString() : null;
            LoggingService.Info($"[BackendAudio] call_rejected received - call_id: {callId}, reason: {reason}");

            // Close the audio gate and clear active call
            _callAccepted = false;
            _activeCallId = null;
            _lastCallId = null;

            // Transition back to Connected (idle) per protocol state machine
            SetConnectionState(ConnectionState.Connected);

            CallRejectedReceived?.Invoke(callId, reason);
        }

        private void HandleCallEndedResponse(JsonElement root, string rawJson)
        {
            string callId = root.TryGetProperty("call_id", out var cidProp) ? cidProp.GetString() : null;
            string reason = root.TryGetProperty("reason", out var rProp) ? rProp.GetString() : null;
            int duration = root.TryGetProperty("duration_secs", out var dProp) ? dProp.GetInt32() : 0;
            LoggingService.Info($"[BackendAudio] call_ended received - call_id: {callId}, reason: {reason}, duration: {duration}s");

            // Close the audio gate and clear active call
            _callAccepted = false;
            _activeCallId = null;
            _lastCallId = null;

            // Transition back to Connected (idle) per protocol state machine
            SetConnectionState(ConnectionState.Connected);

            CallEndedReceived?.Invoke(callId, reason, duration);
        }

        private void HandleTranscriptMessage(JsonElement root, string rawJson)
        {
            string text = root.TryGetProperty("text", out var textProp) ? textProp.GetString() : null;
            bool isFinal = root.TryGetProperty("is_final", out var finalProp) && finalProp.GetBoolean();

            if (!string.IsNullOrEmpty(text))
            {
                TranscriptReceived?.Invoke(text, isFinal, rawJson);
                LoggingService.Info($"[BackendAudio] Transcript ({(isFinal ? "final" : "partial")}): {text.Substring(0, Math.Min(100, text.Length))}");
            }
        }

        private void HandleAgentResponseMessage(JsonElement root, string rawJson)
        {
            string responseText = root.TryGetProperty("text", out var textProp) ? textProp.GetString() : null;

            if (!string.IsNullOrEmpty(responseText))
            {
                AgentResponseReceived?.Invoke(responseText, rawJson);
                LoggingService.Info($"[BackendAudio] Agent response: {responseText.Substring(0, Math.Min(100, responseText.Length))}");
            }
        }

        private void HandleErrorMessage(JsonElement root, string rawJson)
        {
            string error = root.TryGetProperty("message", out var msgProp) ? msgProp.GetString() : "Unknown error";
            LoggingService.Error($"[BackendAudio] Backend error: {error}");
            ErrorOccurred?.Invoke(error);
        }

        private void HandleSessionStatusMessage(JsonElement root, string rawJson)
        {
            string status = root.TryGetProperty("status", out var statusProp) ? statusProp.GetString() : "unknown";
            LoggingService.Info($"[BackendAudio] Session status: {status}");
            SessionStatusReceived?.Invoke(status, rawJson);
        }

        // =====================================================================
        // Health ping/pong
        // =====================================================================

        /// <summary>
        /// Background task that sends periodic ping messages to detect stale connections.
        /// </summary>
        private async Task StartPingLoopAsync()
        {
            try
            {
                LoggingService.Info($"[BackendAudio] Ping loop started (interval: {_pingIntervalSeconds}s, timeout: {_pongTimeoutSeconds}s)");

                while (!_cancellationTokenSource.Token.IsCancellationRequested && _isConnected)
                {
                    await Task.Delay(TimeSpan.FromSeconds(_pingIntervalSeconds), _cancellationTokenSource.Token);

                    if (!_isConnected || _webSocket?.State != WebSocketState.Open) break;

                    // Check if last pong was received within timeout
                    if (_lastPingSentTime > _lastPongReceivedTime &&
                        (DateTime.UtcNow - _lastPingSentTime).TotalSeconds > _pongTimeoutSeconds)
                    {
                        LoggingService.Warn($"[BackendAudio] Pong timeout - no response within {_pongTimeoutSeconds}s. Connection may be stale.");
                        _isConnected = false;
                        HandleDisconnection("Pong timeout - connection stale");
                        break;
                    }

                    // Send ping
                    try
                    {
                        var pingMessage = new
                        {
                            event_type = "ping",
                            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        };

                        await SendJsonMessageAsync(pingMessage);
                        _lastPingSentTime = DateTime.UtcNow;
                        LoggingService.Debug("[BackendAudio] Ping sent");
                    }
                    catch (Exception ex)
                    {
                        LoggingService.Warn($"[BackendAudio] Error sending ping: {ex.Message}");
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal on cancellation
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[BackendAudio] Ping loop error: {ex.Message}");
            }

            LoggingService.Info("[BackendAudio] Ping loop stopped");
        }

        // =====================================================================
        // Network latency measurement
        // =====================================================================

        /// <summary>
        /// Derives the HTTP ping URL from the WebSocket URL.
        /// Converts wss://host/ws/audio → https://host/
        /// </summary>
        private string DeriveLatencyPingUrl()
        {
            try
            {
                var wsUri = new Uri(_webSocketUrl);
                string scheme = wsUri.Scheme == "wss" ? "https" : "http";
                int port = wsUri.Port;
                // Use default port for the scheme to keep URL clean
                bool isDefaultPort = (scheme == "https" && port == 443) || (scheme == "http" && port == 80) || port < 0;
                string hostPart = isDefaultPort ? wsUri.Host : $"{wsUri.Host}:{port}";
                return $"{scheme}://{hostPart}/";
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"[BackendAudio] Could not derive latency ping URL from {_webSocketUrl}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Background task that periodically measures network round-trip latency to the backend
        /// by sending lightweight HTTP HEAD requests to the backend host.
        /// Fires <see cref="AudioChunkLatencyMeasured"/> with each measurement.
        /// </summary>
        private async Task StartLatencyMeasurementAsync()
        {
            const int intervalMs = 3000; // Measure every 3 seconds

            try
            {
                if (string.IsNullOrEmpty(_latencyPingUrl))
                {
                    LoggingService.Warn("[BackendAudio] Latency measurement skipped — no ping URL available");
                    return;
                }

                LoggingService.Info($"[BackendAudio] Latency measurement started (interval: {intervalMs}ms, url: {_latencyPingUrl})");

                // Take first measurement immediately
                bool firstMeasurement = true;

                while (!_cancellationTokenSource.Token.IsCancellationRequested && _isConnected)
                {
                    if (!firstMeasurement)
                    {
                        await Task.Delay(intervalMs, _cancellationTokenSource.Token);
                    }
                    firstMeasurement = false;

                    if (!_isConnected) break;

                    try
                    {
                        var request = new HttpRequestMessage(HttpMethod.Head, _latencyPingUrl);
                        var sw = Stopwatch.StartNew();
                        using var response = await _latencyHttpClient.SendAsync(
                            request,
                            HttpCompletionOption.ResponseHeadersRead,
                            _cancellationTokenSource.Token);
                        sw.Stop();

                        double latencyMs = sw.Elapsed.TotalMilliseconds;
                        LastSendLatencyMs = latencyMs;

                        try
                        {
                            AudioChunkLatencyMeasured?.Invoke(latencyMs);
                        }
                        catch (Exception evtEx)
                        {
                            LoggingService.Debug($"[BackendAudio] Latency event handler error: {evtEx.Message}");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        LoggingService.Debug($"[BackendAudio] Latency ping failed: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal on cancellation
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[BackendAudio] Latency measurement loop error: {ex.Message}");
            }

            LoggingService.Info("[BackendAudio] Latency measurement stopped");
        }

        // =====================================================================
        // Reconnection logic
        // =====================================================================

        /// <summary>
        /// Handles disconnection by updating state and optionally starting reconnection.
        /// </summary>
        private void HandleDisconnection(string reason)
        {
            LoggingService.Warn($"[BackendAudio] Disconnected: {reason}");

            // Attempt to send call_ended if there's an active call and socket is still writeable
            if (!string.IsNullOrEmpty(_activeCallId))
            {
                if (_webSocket?.State == WebSocketState.Open)
                {
                    try
                    {
                        LoggingService.Info($"[BackendAudio] Attempting call_ended on unexpected disconnect - call_id: {_activeCallId}");
                        SendCallEndedAsync(reason: "connection_lost").Wait(TimeSpan.FromSeconds(2));
                    }
                    catch (Exception ex)
                    {
                        LoggingService.Warn($"[BackendAudio] Failed to send call_ended on disconnect: {ex.Message}");
                    }
                }
                else
                {
                    LoggingService.Warn($"[BackendAudio] Cannot send call_ended - socket already closed (State: {_webSocket?.State}), call_id: {_activeCallId}");
                    // Don't clear _activeCallId — reconnection will re-send call_incoming
                }
            }

            Disconnected?.Invoke(reason);

            if (_connectionLostTime == DateTime.MinValue)
            {
                _connectionLostTime = DateTime.UtcNow;
            }

            // Start reconnection if not already reconnecting and not cancelled
            if (!_isReconnecting && !_cancellationTokenSource.Token.IsCancellationRequested)
            {
                _ = Task.Run(() => AttemptReconnectionAsync());
            }
        }

        /// <summary>
        /// Attempts to reconnect to the backend with exponential backoff.
        /// </summary>
        private async Task AttemptReconnectionAsync()
        {
            if (_isReconnecting) return;
            _isReconnecting = true;

            try
            {
                SetConnectionState(ConnectionState.Reconnecting);

                while (_reconnectionAttempts < _maxReconnectionAttempts &&
                       !_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    _reconnectionAttempts++;

                    // Exponential backoff: base * 2^(attempt-1), capped at 60 seconds
                    int delayMs = Math.Min(_baseReconnectionDelayMs * (1 << (_reconnectionAttempts - 1)), 60000);
                    LoggingService.Info($"[BackendAudio] Reconnection attempt #{_reconnectionAttempts}/{_maxReconnectionAttempts} in {delayMs}ms");

                    await Task.Delay(delayMs, _cancellationTokenSource.Token);

                    bool success = await ConnectAsync();
                    if (success)
                    {
                        LoggingService.Info($"[BackendAudio] Reconnected successfully after {_reconnectionAttempts} attempt(s)");
                        _reconnectionAttempts = 0;
                        _connectionLostTime = DateTime.MinValue;

                        // Re-send call_incoming if a call was active when the connection dropped
                        if (!string.IsNullOrEmpty(_lastCallId))
                        {
                            try
                            {
                                LoggingService.Info($"[BackendAudio] Re-sending call_incoming after reconnection - call_id: {_lastCallId}");
                                await SendCallIncomingAsync(_lastCallId, _lastCsrId, _lastCustomer, _lastEmployee);
                            }
                            catch (Exception csEx)
                            {
                                LoggingService.Warn($"[BackendAudio] Failed to re-send call_incoming after reconnection: {csEx.Message}");
                            }
                        }

                        return;
                    }
                }

                // Exhausted all reconnection attempts
                LoggingService.Error($"[BackendAudio] Reconnection failed after {_maxReconnectionAttempts} attempts");
                SetConnectionState(ConnectionState.Disconnected);
                ReconnectionFailed?.Invoke();
            }
            catch (OperationCanceledException)
            {
                LoggingService.Info("[BackendAudio] Reconnection cancelled");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[BackendAudio] Reconnection error: {ex.Message}");
            }
            finally
            {
                _isReconnecting = false;
            }
        }

        // =====================================================================
        // Graceful disconnection and cleanup
        // =====================================================================

        /// <summary>
        /// Gracefully disconnect from the backend WebSocket.
        /// Sends a session end message before closing.
        /// </summary>
        public async Task DisconnectAsync()
        {
            try
            {
                LoggingService.Info("[BackendAudio] Disconnecting...");

                // Send call_ended BEFORE cancelling the CTS so the send can complete
                if (_webSocket?.State == WebSocketState.Open && !string.IsNullOrEmpty(_activeCallId))
                {
                    try
                    {
                        LoggingService.Info($"[BackendAudio] Sending call_ended before disconnect - call_id: {_activeCallId}");
                        await SendCallEndedAsync(reason: "completed");
                        LoggingService.Info("[BackendAudio] call_ended sent successfully before disconnect");
                    }
                    catch (Exception callEndEx)
                    {
                        LoggingService.Warn($"[BackendAudio] Failed to send call_ended before disconnect: {callEndEx.Message}");
                    }
                }
                else if (!string.IsNullOrEmpty(_activeCallId))
                {
                    LoggingService.Warn($"[BackendAudio] Cannot send call_ended - WebSocket not open (State: {_webSocket?.State}), call_id: {_activeCallId} will be orphaned");
                    _activeCallId = null;
                }
                else
                {
                    LoggingService.Info("[BackendAudio] No active call - skipping call_ended");
                }

                // Now prevent reconnection and stop background tasks
                _cancellationTokenSource?.Cancel();

                // Close the WebSocket gracefully
                if (_webSocket?.State == WebSocketState.Open)
                {
                    try
                    {
                        LoggingService.Info("[BackendAudio] Sending WebSocket close handshake...");
                        await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Session ended", CancellationToken.None);
                        LoggingService.Info("[BackendAudio] WebSocket close handshake completed");
                    }
                    catch (Exception closeEx)
                    {
                        LoggingService.Warn($"[BackendAudio] WebSocket close handshake failed: {closeEx.Message}");
                    }
                }

                // Wait for background tasks to complete
                if (_messageListener != null)
                {
                    await Task.WhenAny(_messageListener, Task.Delay(2000));
                }
                if (_pingTask != null)
                {
                    await Task.WhenAny(_pingTask, Task.Delay(1000));
                }
                if (_latencyMeasurementTask != null)
                {
                    await Task.WhenAny(_latencyMeasurementTask, Task.Delay(1000));
                }

                _isConnected = false;
                SetConnectionState(ConnectionState.Disconnected);

                LoggingService.Info($"[BackendAudio] Disconnected gracefully. Mic chunks: {_micChunksSent}, Speaker chunks: {_speakerChunksSent}, Total bytes: {Interlocked.Read(ref _totalBytesSent)}");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[BackendAudio] Error during disconnect: {ex.Message}");
            }
        }

        // =====================================================================
        // Helper methods
        // =====================================================================

        /// <summary>
        /// Send a JSON-serialized message through the WebSocket.
        /// Acquires <see cref="_sendLock"/> to prevent concurrent sends.
        /// </summary>
        private async Task SendJsonMessageAsync(object message)
        {
            string json = JsonSerializer.Serialize(message);
            var buffer = Encoding.UTF8.GetBytes(json);

            await _sendLock.WaitAsync(_cancellationTokenSource.Token);
            try
            {
                await _webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, _cancellationTokenSource.Token);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        /// <summary>
        /// Clean up an existing WebSocket connection before reconnecting.
        /// </summary>
        private async Task CleanupExistingConnectionAsync()
        {
            if (_webSocket != null)
            {
                try
                {
                    if (_webSocket.State == WebSocketState.Open || _webSocket.State == WebSocketState.Connecting)
                    {
                        _cancellationTokenSource?.Cancel();

                        if (_messageListener != null)
                        {
                            await Task.WhenAny(_messageListener, Task.Delay(1000));
                        }

                        await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Reconnecting", CancellationToken.None);
                    }
                }
                catch { }
                finally
                {
                    _webSocket?.Dispose();
                    _webSocket = null;
                    _cancellationTokenSource?.Dispose();
                    _cancellationTokenSource = null;
                }
            }
        }

        /// <summary>
        /// Update the connection state and fire the status changed event.
        /// </summary>
        private void SetConnectionState(ConnectionState newState)
        {
            if (_connectionState != newState)
            {
                var previousState = _connectionState;
                _connectionState = newState;
                LoggingService.Info($"[BackendAudio] Connection state: {previousState} -> {newState}");
                ConnectionStatusChanged?.Invoke(newState);
            }
        }

        /// <summary>
        /// Masks the token query parameter value in a URL for safe logging.
        /// Replaces the token value with "***" to prevent credential leakage in logs.
        /// </summary>
        private static string MaskTokenInUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            try
            {
                // Replace token=<value> with token=***
                return System.Text.RegularExpressions.Regex.Replace(
                    url,
                    @"(token=)[^&]*",
                    "$1***",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
            catch
            {
                return url;
            }
        }

        // =====================================================================
        // IDisposable
        // =====================================================================

        public void Dispose()
        {
            try
            {
                DisconnectAsync().Wait(TimeSpan.FromSeconds(5));
            }
            catch { }

            _cancellationTokenSource?.Dispose();
            _webSocket?.Dispose();
            _sendLock?.Dispose();
        }
    }
}
