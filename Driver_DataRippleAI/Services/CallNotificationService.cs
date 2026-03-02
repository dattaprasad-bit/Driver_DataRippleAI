using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataRippleAIDesktop.Models;
using Microsoft.Extensions.Configuration;

namespace DataRippleAIDesktop.Services
{
    /// <summary>
    /// Manages a persistent WebSocket connection to the backend at /ws/audio/{user_id}?token={jwt}
    /// for call lifecycle notifications (call_incoming, call_accepted, call_rejected, call_ended).
    /// This connection opens as soon as the user logs in and remains open for the session.
    /// </summary>
    public class CallNotificationService : IDisposable
    {
        private const string DefaultWebSocketPathPrefix = "/ws/audio";
        private const int MaxReconnectionAttempts = 10;
        private const int BaseReconnectionDelayMs = 1000;
        private const int ConnectionTimeoutSeconds = 20;
        private const int PingIntervalSeconds = 30;

        // Connection state
        private ClientWebSocket _webSocket;
        private CancellationTokenSource _cancellationTokenSource;
        private bool _isConnected;
        private bool _isReconnecting;
        private Task _messageListenerTask;
        private Task _pingTask;
        private string _storedJwtToken;
        private int _reconnectionAttempts;

        // Protocol state machine
        private CallNotificationState _state = CallNotificationState.Disconnected;
        private readonly object _stateLock = new object();
        private string _activeCallId;
        private string _activeSessionId;

        // Thread-safe send
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);

        // WebSocket URL (base, without user_id)
        private readonly string _webSocketBaseUrl;

        // =====================================================================
        // Events
        // =====================================================================

        /// <summary>Fired when the protocol state changes.</summary>
        public event Action<CallNotificationState> StateChanged;

        /// <summary>Fired when backend confirms connected (authenticated, idle).</summary>
        public event Action<CallNotificationConnectedEvent> ConnectedReceived;

        /// <summary>Fired when backend ACKs our call_incoming (ringing state).</summary>
        public event Action<CallIncomingAckEvent> CallIncomingAcked;

        /// <summary>Fired when dashboard user accepts the call. Driver should start audio.</summary>
        public event Action<CallAcceptedEvent> CallAccepted;

        /// <summary>Fired when dashboard user rejects the call. Driver returns to idle.</summary>
        public event Action<CallRejectedEvent> CallRejected;

        /// <summary>Fired when backend signals call ended (from dashboard or confirmation).</summary>
        public event Action<CallEndedInboundEvent> CallEnded;

        /// <summary>Fired when an error event is received from the backend.</summary>
        public event Action<string> ErrorReceived;

        /// <summary>Fired when the WebSocket disconnects unexpectedly.</summary>
        public event Action<string> Disconnected;

        // =====================================================================
        // Public properties
        // =====================================================================

        public CallNotificationState CurrentState
        {
            get { lock (_stateLock) return _state; }
        }

        public bool IsConnected => _isConnected && _webSocket?.State == WebSocketState.Open;

        public string ActiveCallId => _activeCallId;
        public string ActiveSessionId => _activeSessionId;

        // =====================================================================
        // Constructor
        // =====================================================================

        public CallNotificationService(string webSocketBaseUrl)
        {
            _webSocketBaseUrl = webSocketBaseUrl ?? throw new ArgumentNullException(nameof(webSocketBaseUrl));
            LoggingService.Info($"[CallNotification] Service created - Base URL: {_webSocketBaseUrl}");
        }

        // =====================================================================
        // Factory
        // =====================================================================

        /// <summary>
        /// Creates a CallNotificationService from appsettings.json configuration.
        /// Derives the WebSocket URL from Backend:BaseUrl if not explicitly set.
        /// </summary>
        public static CallNotificationService CreateFromConfiguration()
        {
            string webSocketUrl = null;

            try
            {
                var config = Globals.ConfigurationInfo;
                if (config != null)
                {
                    // Check for explicit CallNotification:WebSocketUrl
                    var section = config.GetSection("CallNotification");
                    webSocketUrl = section?.GetValue<string>("WebSocketUrl", null);

                    if (string.IsNullOrWhiteSpace(webSocketUrl))
                    {
                        webSocketUrl = BuildWebSocketUrlFromBackendBase(config);
                    }
                }
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"[CallNotification] Error reading configuration: {ex.Message}");
            }

            if (string.IsNullOrWhiteSpace(webSocketUrl))
            {
                webSocketUrl = BuildWebSocketUrlFromBackendBase(Globals.ConfigurationInfo);
            }

            if (string.IsNullOrWhiteSpace(webSocketUrl))
            {
                throw new InvalidOperationException(
                    "Cannot create CallNotificationService: no WebSocket URL. " +
                    "Set 'CallNotification:WebSocketUrl' or 'Backend:BaseUrl' in appsettings.json.");
            }

            return new CallNotificationService(webSocketUrl);
        }

        /// <summary>
        /// Derives the notification WebSocket base URL from Backend:BaseUrl.
        /// Converts https to wss (or http to ws) and appends /ws.
        /// </summary>
        private static string BuildWebSocketUrlFromBackendBase(IConfiguration config)
        {
            try
            {
                if (config == null) return null;

                string baseUrl = config.GetSection("Backend").GetValue<string>("BaseUrl", null);
                if (string.IsNullOrWhiteSpace(baseUrl)) return null;

                baseUrl = baseUrl.TrimEnd('/');
                if (baseUrl.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
                {
                    baseUrl = baseUrl.Substring(0, baseUrl.Length - 4);
                }

                if (baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    baseUrl = "wss://" + baseUrl.Substring("https://".Length);
                }
                else if (baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                {
                    baseUrl = "ws://" + baseUrl.Substring("http://".Length);
                }

                string derivedUrl = baseUrl.TrimEnd('/') + DefaultWebSocketPathPrefix;
                LoggingService.Info($"[CallNotification] Derived WebSocket URL from Backend:BaseUrl: {derivedUrl}");
                return derivedUrl;
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"[CallNotification] Could not derive WebSocket URL: {ex.Message}");
                return null;
            }
        }

        // =====================================================================
        // Connection management
        // =====================================================================

        /// <summary>
        /// Connect to the backend notification WebSocket.
        /// URL: /ws/audio/{user_id}?token={jwt}
        /// </summary>
        public async Task<bool> ConnectAsync(string jwtToken = null)
        {
            try
            {
                if (_isConnected && _webSocket?.State == WebSocketState.Open)
                {
                    LoggingService.Info("[CallNotification] Already connected");
                    return true;
                }

                // Clean up existing connection
                await CleanupExistingConnectionAsync();

                string resolvedToken = ResolveJwtToken(jwtToken);
                if (string.IsNullOrEmpty(resolvedToken))
                {
                    LoggingService.Error("[CallNotification] No valid JWT token - cannot connect");
                    SetState(CallNotificationState.Disconnected);
                    return false;
                }

                string userId = Globals.UserId;
                if (string.IsNullOrEmpty(userId))
                {
                    userId = ExtractUserIdFromJwt(resolvedToken);
                    if (string.IsNullOrEmpty(userId))
                    {
                        LoggingService.Error("[CallNotification] Cannot determine user_id - cannot connect");
                        SetState(CallNotificationState.Disconnected);
                        return false;
                    }
                }

                // Build URL: /ws/audio/{user_id}?token={jwt}
                string effectiveUrl = $"{_webSocketBaseUrl.TrimEnd('/')}/{Uri.EscapeDataString(userId)}?token={Uri.EscapeDataString(resolvedToken)}";
                LoggingService.Info($"[CallNotification] Connecting to: {MaskTokenInUrl(effectiveUrl)}");

                _webSocket = WebSocketFactory.CreateCompatibleWebSocket();
                _cancellationTokenSource = new CancellationTokenSource();

                using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(ConnectionTimeoutSeconds)))
                using (var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    _cancellationTokenSource.Token, timeoutCts.Token))
                {
                    await _webSocket.ConnectAsync(new Uri(effectiveUrl), combinedCts.Token);
                }

                if (_webSocket.State == WebSocketState.Open)
                {
                    _isConnected = true;
                    _reconnectionAttempts = 0;
                    _activeCallId = null;
                    _activeSessionId = null;

                    // Start message listener
                    _messageListenerTask = Task.Run(() => MessageListenerAsync());

                    // Start keep-alive ping
                    _pingTask = Task.Run(() => PingLoopAsync());

                    // Don't set Connected state yet - wait for the "connected" event from backend
                    LoggingService.Info("[CallNotification] WebSocket connected, waiting for connected event...");
                    return true;
                }

                LoggingService.Error($"[CallNotification] Connection failed. State: {_webSocket.State}");
                SetState(CallNotificationState.Disconnected);
                return false;
            }
            catch (OperationCanceledException)
            {
                LoggingService.Error($"[CallNotification] Connection timed out after {ConnectionTimeoutSeconds}s");
                SetState(CallNotificationState.Disconnected);
                return false;
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[CallNotification] Connection error: {ex.Message}");
                SetState(CallNotificationState.Disconnected);
                return false;
            }
        }

        /// <summary>
        /// Gracefully disconnect.
        /// </summary>
        public async Task DisconnectAsync()
        {
            try
            {
                LoggingService.Info("[CallNotification] Disconnecting...");

                _cancellationTokenSource?.Cancel();

                if (_webSocket?.State == WebSocketState.Open)
                {
                    try
                    {
                        await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnecting", CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        LoggingService.Warn($"[CallNotification] Close handshake failed: {ex.Message}");
                    }
                }

                if (_messageListenerTask != null)
                    await Task.WhenAny(_messageListenerTask, Task.Delay(2000));
                if (_pingTask != null)
                    await Task.WhenAny(_pingTask, Task.Delay(1000));

                _isConnected = false;
                SetState(CallNotificationState.Disconnected);
                LoggingService.Info("[CallNotification] Disconnected gracefully");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[CallNotification] Error during disconnect: {ex.Message}");
            }
        }

        // =====================================================================
        // Outbound events (Driver -> Backend)
        // =====================================================================

        /// <summary>
        /// Send call_incoming when a new phone call is detected.
        /// Transitions state: connected -> ringing.
        /// </summary>
        public async Task<bool> SendCallIncomingAsync(string callId, string csrId, CustomerInfo customer, EmployeeInfo employee)
        {
            try
            {
                if (CurrentState != CallNotificationState.Connected)
                {
                    LoggingService.Warn($"[CallNotification] Cannot send call_incoming in state {CurrentState} (expected Connected)");
                    return false;
                }

                if (!IsConnected)
                {
                    LoggingService.Warn("[CallNotification] WebSocket not connected - cannot send call_incoming");
                    return false;
                }

                var message = new CallIncomingOutbound
                {
                    CallId = callId,
                    CsrId = csrId,
                    Customer = customer,
                    Employee = employee,
                    Timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
                };

                string json = JsonSerializer.Serialize(message);
                LoggingService.Info($"[CallNotification] Sending call_incoming: {json}");

                await SendJsonAsync(json);
                _activeCallId = callId;

                // State transitions to Ringing after we receive the ACK, but we track intent
                LoggingService.Info($"[CallNotification] call_incoming sent - call_id: {callId}");
                return true;
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[CallNotification] Error sending call_incoming: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Send call_ended when the driver ends the call.
        /// Transitions state: active -> connected.
        /// </summary>
        public async Task<bool> SendCallEndedAsync(string callId = null, string reason = "completed")
        {
            try
            {
                string effectiveCallId = callId ?? _activeCallId;
                if (string.IsNullOrEmpty(effectiveCallId))
                {
                    LoggingService.Warn("[CallNotification] Cannot send call_ended - no active call");
                    return false;
                }

                if (!IsConnected)
                {
                    LoggingService.Warn("[CallNotification] WebSocket not connected - cannot send call_ended");
                    return false;
                }

                var message = new CallEndedOutbound
                {
                    CallId = effectiveCallId,
                    Reason = reason,
                    Timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
                };

                string json = JsonSerializer.Serialize(message);
                LoggingService.Info($"[CallNotification] Sending call_ended: {json}");

                await SendJsonAsync(json);

                _activeCallId = null;
                _activeSessionId = null;
                SetState(CallNotificationState.Connected);

                LoggingService.Info($"[CallNotification] call_ended sent - call_id: {effectiveCallId}, reason: {reason}");
                return true;
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[CallNotification] Error sending call_ended: {ex.Message}");
                return false;
            }
        }

        // =====================================================================
        // Message listener
        // =====================================================================

        private async Task MessageListenerAsync()
        {
            var buffer = new byte[4096];
            var messageBuffer = new List<byte>();

            try
            {
                LoggingService.Info("[CallNotification] Message listener started");

                while (_webSocket?.State == WebSocketState.Open && !_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    try
                    {
                        var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), _cancellationTokenSource.Token);

                        if (result.MessageType == WebSocketMessageType.Text)
                        {
                            messageBuffer.AddRange(new ArraySegment<byte>(buffer, 0, result.Count));

                            if (result.EndOfMessage)
                            {
                                string message = Encoding.UTF8.GetString(messageBuffer.ToArray());
                                ProcessMessage(message);
                                messageBuffer.Clear();
                            }
                        }
                        else if (result.MessageType == WebSocketMessageType.Close)
                        {
                            LoggingService.Warn($"[CallNotification] Received Close: {result.CloseStatus} - {result.CloseStatusDescription}");
                            break;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (WebSocketException wsEx)
                    {
                        LoggingService.Error($"[CallNotification] WebSocket receive error: {wsEx.Message}");
                        break;
                    }
                    catch (Exception ex)
                    {
                        LoggingService.Error($"[CallNotification] Message receive error: {ex.Message}");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[CallNotification] Message listener error: {ex.Message}");
            }
            finally
            {
                if (_isConnected)
                {
                    _isConnected = false;
                    HandleDisconnection("Message listener stopped");
                }
                LoggingService.Info("[CallNotification] Message listener stopped");
            }
        }

        private void ProcessMessage(string rawMessage)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(rawMessage)) return;

                LoggingService.Debug($"[CallNotification] Received: {rawMessage}");

                JsonDocument jsonDoc;
                try
                {
                    jsonDoc = JsonDocument.Parse(rawMessage);
                }
                catch (JsonException)
                {
                    LoggingService.Warn($"[CallNotification] Invalid JSON: {rawMessage.Substring(0, Math.Min(200, rawMessage.Length))}");
                    return;
                }

                using (jsonDoc)
                {
                    var root = jsonDoc.RootElement;

                    string eventType = null;
                    if (root.TryGetProperty("event_type", out var etProp))
                        eventType = etProp.GetString();
                    else if (root.TryGetProperty("type", out var tProp))
                        eventType = tProp.GetString();

                    if (string.IsNullOrEmpty(eventType))
                    {
                        LoggingService.Warn($"[CallNotification] Message has no event_type: {rawMessage.Substring(0, Math.Min(200, rawMessage.Length))}");
                        return;
                    }

                    switch (eventType)
                    {
                        case "connected":
                            HandleConnectedEvent(root, rawMessage);
                            break;

                        case "call_incoming":
                            HandleCallIncomingAck(root, rawMessage);
                            break;

                        case "call_accepted":
                        case "call_accept":
                            HandleCallAcceptedEvent(root, rawMessage);
                            break;

                        case "call_rejected":
                        case "call_reject":
                            HandleCallRejectedEvent(root, rawMessage);
                            break;

                        case "call_ended":
                        case "call_end":
                            HandleCallEndedEvent(root, rawMessage);
                            break;

                        case "error":
                            HandleErrorEvent(root, rawMessage);
                            break;

                        case "pong":
                            LoggingService.Debug("[CallNotification] Pong received");
                            break;

                        default:
                            LoggingService.Debug($"[CallNotification] Unhandled event type: {eventType}");
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[CallNotification] Error processing message: {ex.Message}");
            }
        }

        private void HandleConnectedEvent(JsonElement root, string rawJson)
        {
            string userId = root.TryGetProperty("user_id", out var uidProp) ? uidProp.GetString() : null;
            string message = root.TryGetProperty("message", out var msgProp) ? msgProp.GetString() : null;

            LoggingService.Info($"[CallNotification] Connected event received - user_id: {userId}, message: {message}");
            SetState(CallNotificationState.Connected);

            var evt = new CallNotificationConnectedEvent
            {
                EventType = "connected",
                UserId = userId,
                Message = message,
                Timestamp = root.TryGetProperty("timestamp", out var tsProp) ? tsProp.GetString() : null
            };
            ConnectedReceived?.Invoke(evt);
        }

        private void HandleCallIncomingAck(JsonElement root, string rawJson)
        {
            string callId = root.TryGetProperty("call_id", out var cidProp) ? cidProp.GetString() : null;
            string sessionId = root.TryGetProperty("session_id", out var sidProp) ? sidProp.GetString() : null;
            string status = root.TryGetProperty("status", out var stProp) ? stProp.GetString() : null;

            LoggingService.Info($"[CallNotification] call_incoming ACK - call_id: {callId}, session_id: {sessionId}, status: {status}");

            _activeSessionId = sessionId;
            SetState(CallNotificationState.Ringing);

            var evt = new CallIncomingAckEvent
            {
                EventType = "call_incoming",
                CallId = callId,
                SessionId = sessionId,
                Status = status
            };
            CallIncomingAcked?.Invoke(evt);
        }

        private void HandleCallAcceptedEvent(JsonElement root, string rawJson)
        {
            if (CurrentState != CallNotificationState.Ringing && CurrentState != CallNotificationState.Connected)
            {
                LoggingService.Warn($"[CallNotification] Received call_accepted in unexpected state {CurrentState} - processing anyway");
            }

            string callId = root.TryGetProperty("call_id", out var cidProp) ? cidProp.GetString() : null;
            string sessionId = root.TryGetProperty("session_id", out var sidProp) ? sidProp.GetString() : null;
            string conversationId = root.TryGetProperty("conversation_id", out var convProp) ? convProp.GetString() : null;
            string agentId = root.TryGetProperty("agent_id", out var aidProp) ? aidProp.GetString() : null;
            string status = root.TryGetProperty("status", out var stProp) ? stProp.GetString() : null;
            string startedAt = root.TryGetProperty("started_at", out var saProp) ? saProp.GetString() : null;

            LoggingService.Info($"[CallNotification] call_accepted - call_id: {callId}, session_id: {sessionId}, conversation_id: {conversationId}, agent_id: {agentId}");

            _activeSessionId = sessionId;
            SetState(CallNotificationState.Active);

            var evt = new CallAcceptedEvent
            {
                EventType = "call_accepted",
                CallId = callId,
                SessionId = sessionId,
                ConversationId = conversationId,
                AgentId = agentId,
                Status = status,
                StartedAt = startedAt
            };
            CallAccepted?.Invoke(evt);
        }

        private void HandleCallRejectedEvent(JsonElement root, string rawJson)
        {
            string callId = root.TryGetProperty("call_id", out var cidProp) ? cidProp.GetString() : null;
            string reason = root.TryGetProperty("reason", out var rProp) ? rProp.GetString() : null;

            LoggingService.Info($"[CallNotification] call_rejected - call_id: {callId}, reason: {reason}");

            _activeCallId = null;
            _activeSessionId = null;
            SetState(CallNotificationState.Connected);

            var evt = new CallRejectedEvent
            {
                EventType = "call_rejected",
                CallId = callId,
                Reason = reason,
                Timestamp = root.TryGetProperty("timestamp", out var tsProp) ? tsProp.GetString() : null
            };
            CallRejected?.Invoke(evt);
        }

        private void HandleCallEndedEvent(JsonElement root, string rawJson)
        {
            string callId = root.TryGetProperty("call_id", out var cidProp) ? cidProp.GetString() : null;
            string conversationId = root.TryGetProperty("conversation_id", out var convProp) ? convProp.GetString() : null;
            string reason = root.TryGetProperty("reason", out var rProp) ? rProp.GetString() : null;
            int durationSecs = root.TryGetProperty("duration_secs", out var dProp) && dProp.ValueKind == JsonValueKind.Number ? dProp.GetInt32() : 0;

            LoggingService.Info($"[CallNotification] call_ended - call_id: {callId}, reason: {reason}, duration: {durationSecs}s");

            _activeCallId = null;
            _activeSessionId = null;
            SetState(CallNotificationState.Connected);

            var evt = new CallEndedInboundEvent
            {
                EventType = "call_ended",
                CallId = callId,
                ConversationId = conversationId,
                Reason = reason,
                DurationSecs = durationSecs
            };
            CallEnded?.Invoke(evt);
        }

        private void HandleErrorEvent(JsonElement root, string rawJson)
        {
            string message = root.TryGetProperty("message", out var msgProp) ? msgProp.GetString() : "Unknown error";
            LoggingService.Error($"[CallNotification] Backend error: {message}");
            ErrorReceived?.Invoke(message);
        }

        // =====================================================================
        // Keep-alive ping
        // =====================================================================

        private async Task PingLoopAsync()
        {
            try
            {
                while (!_cancellationTokenSource.Token.IsCancellationRequested && _isConnected)
                {
                    await Task.Delay(TimeSpan.FromSeconds(PingIntervalSeconds), _cancellationTokenSource.Token);

                    if (!_isConnected || _webSocket?.State != WebSocketState.Open) break;

                    try
                    {
                        string ping = JsonSerializer.Serialize(new { event_type = "ping", timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
                        await SendJsonAsync(ping);
                        LoggingService.Debug("[CallNotification] Ping sent");
                    }
                    catch (Exception ex)
                    {
                        LoggingService.Warn($"[CallNotification] Error sending ping: {ex.Message}");
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                LoggingService.Error($"[CallNotification] Ping loop error: {ex.Message}");
            }
        }

        // =====================================================================
        // Reconnection
        // =====================================================================

        private void HandleDisconnection(string reason)
        {
            LoggingService.Warn($"[CallNotification] Disconnected: {reason}");
            SetState(CallNotificationState.Disconnected);
            Disconnected?.Invoke(reason);

            if (!_isReconnecting && !(_cancellationTokenSource?.Token.IsCancellationRequested ?? true))
            {
                _ = Task.Run(() => AttemptReconnectionAsync());
            }
        }

        private async Task AttemptReconnectionAsync()
        {
            if (_isReconnecting) return;
            _isReconnecting = true;

            try
            {
                while (_reconnectionAttempts < MaxReconnectionAttempts &&
                       !(_cancellationTokenSource?.Token.IsCancellationRequested ?? true))
                {
                    _reconnectionAttempts++;
                    int delayMs = Math.Min(BaseReconnectionDelayMs * (1 << (_reconnectionAttempts - 1)), 60000);
                    LoggingService.Info($"[CallNotification] Reconnection attempt #{_reconnectionAttempts}/{MaxReconnectionAttempts} in {delayMs}ms");

                    await Task.Delay(delayMs);

                    bool success = await ConnectAsync();
                    if (success)
                    {
                        LoggingService.Info($"[CallNotification] Reconnected after {_reconnectionAttempts} attempt(s)");
                        _reconnectionAttempts = 0;
                        return;
                    }
                }

                LoggingService.Error($"[CallNotification] Reconnection failed after {MaxReconnectionAttempts} attempts");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[CallNotification] Reconnection error: {ex.Message}");
            }
            finally
            {
                _isReconnecting = false;
            }
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private void SetState(CallNotificationState newState)
        {
            lock (_stateLock)
            {
                if (_state != newState)
                {
                    var prev = _state;
                    _state = newState;
                    LoggingService.Info($"[CallNotification] State: {prev} -> {newState}");
                    StateChanged?.Invoke(newState);
                }
            }
        }

        private async Task SendJsonAsync(string json)
        {
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

        private string ResolveJwtToken(string explicitToken)
        {
            if (!string.IsNullOrEmpty(explicitToken) && explicitToken != "offline_mode_token")
            {
                _storedJwtToken = explicitToken;
                return explicitToken;
            }

            string globalsToken = Globals.BackendAccessToken;
            if (!string.IsNullOrEmpty(globalsToken) && globalsToken != "offline_mode_token")
            {
                _storedJwtToken = globalsToken;
                return globalsToken;
            }

            return _storedJwtToken;
        }

        private static string ExtractUserIdFromJwt(string token)
        {
            try
            {
                var parts = token.Split('.');
                if (parts.Length < 2) return null;

                string payload = parts[1];
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

                if (root.TryGetProperty("sub", out var subProp))
                {
                    string sub = subProp.ToString();
                    if (!string.IsNullOrEmpty(sub)) return sub;
                }
                if (root.TryGetProperty("user_id", out var uidProp))
                {
                    string uid = uidProp.ToString();
                    if (!string.IsNullOrEmpty(uid)) return uid;
                }

                return null;
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[CallNotification] Error extracting user_id from JWT: {ex.Message}");
                return null;
            }
        }

        private async Task CleanupExistingConnectionAsync()
        {
            if (_webSocket != null)
            {
                try
                {
                    if (_webSocket.State == WebSocketState.Open || _webSocket.State == WebSocketState.Connecting)
                    {
                        _cancellationTokenSource?.Cancel();
                        if (_messageListenerTask != null)
                            await Task.WhenAny(_messageListenerTask, Task.Delay(1000));
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

        private static string MaskTokenInUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            try
            {
                return System.Text.RegularExpressions.Regex.Replace(
                    url, @"(token=)[^&]*", "$1***",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
            catch { return url; }
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
