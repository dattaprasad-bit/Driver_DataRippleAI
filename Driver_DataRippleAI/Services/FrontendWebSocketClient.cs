using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DataRippleAIDesktop.Services
{
    public class FrontendWebSocketClient : IDisposable
    {
        private ClientWebSocket _frontendSocket;
        private CancellationTokenSource _cancellationTokenSource;
        private string _frontendWebSocketUrl;
        private bool _frontendConnected = false;
        private bool _frontendAvailable = true;
        private Action<string> _frontendMessageHandler;
        private string _storedJwtToken; // Store JWT token for reconnection
        private bool _isReconnecting = false;
        private DateTime _connectionLostTime = DateTime.MinValue;
        private int _reconnectionAttempts = 0;
        private const int MaxReconnectionAttempts = 10;
        private const int ReconnectionDelaySeconds = 5;

        public bool IsFrontendConnected => _frontendConnected;
        public bool IsFrontendAvailable => _frontendAvailable;

        public FrontendWebSocketClient(string frontendWebSocketUrl = null)
        {
            _frontendWebSocketUrl = frontendWebSocketUrl ?? "wss://frontend.clientdomain.com/ws";
            _cancellationTokenSource = new CancellationTokenSource();
            _ = Task.Run(MonitorConnectionAsync);
        }

        public void SetFrontendMessageHandler(Action<string> handler)
        {
            _frontendMessageHandler = handler;
        }

        public async Task<bool> ConnectToFrontendAsync(string jwtToken)
        {
            try
            {
                LoggingService.Info("[WebSocket] Attempting to connect to frontend WebSocket...");

                // Store JWT token for reconnection
                _storedJwtToken = jwtToken;

                _frontendAvailable = true;
                
                // Clean up old socket if exists
                if (_frontendSocket != null)
                {
                    try
                    {
                        if (_frontendSocket.State == WebSocketState.Open || _frontendSocket.State == WebSocketState.CloseReceived)
                        {
                            await _frontendSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Reconnecting", CancellationToken.None);
                        }
                    }
                    catch { }
                    _frontendSocket?.Dispose();
                }
                
                _frontendSocket = new ClientWebSocket();

                if (!string.IsNullOrEmpty(jwtToken) && jwtToken != "offline_mode_token" && jwtToken != "no_auth_websocket_only")
                {
                    _frontendSocket.Options.SetRequestHeader("Authorization", $"Bearer {jwtToken}");
                    LoggingService.Info("[WebSocket] Authentication header added");
                }
                else
                {
                    LoggingService.Info("[WebSocket] Connecting without authentication header");
                }

                _frontendSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

                using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                using (var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token, timeoutCts.Token))
                {
                    await _frontendSocket.ConnectAsync(new Uri(_frontendWebSocketUrl), combinedCts.Token);
                }

                if (_frontendSocket.State == WebSocketState.Open)
                {
                    _frontendConnected = true;
                    _reconnectionAttempts = 0; // Reset reconnection attempts on successful connection
                    _connectionLostTime = DateTime.MinValue;
                    LoggingService.Info("[WebSocket] Successfully connected to frontend WebSocket");
                    _ = Task.Run(ListenForFrontendMessagesAsync);
                    return true;
                }

                LoggingService.Info($"[WebSocket] Frontend connection failed, state: {_frontendSocket.State}");
                return false;
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[WebSocket] Frontend connection error: {ex.Message}");
                _frontendConnected = false;
                return false;
            }
        }

        public async Task<bool> ForwardToFrontendAsync(object eventData)
        {
            try
            {
                if (_frontendSocket?.State != WebSocketState.Open)
                {
                    LoggingService.Info("[WebSocket] Frontend not connected - cannot forward event object");
                    return false;
                }

                var json = JsonSerializer.Serialize(eventData);
                var bytes = Encoding.UTF8.GetBytes(json);
                await _frontendSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cancellationTokenSource.Token);
                LoggingService.Debug($"[WebSocket] Successfully forwarded event: {eventData?.GetType().Name}");
                return true;
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[WebSocket] Error forwarding event object: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> ForwardToFrontendAsync(string content)
        {
            try
            {
                if (_frontendSocket?.State != WebSocketState.Open)
                {
                    LoggingService.Info("[WebSocket] Frontend not connected - cannot forward content");
                    return false;
                }

                var bytes = Encoding.UTF8.GetBytes(content);
                await _frontendSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cancellationTokenSource.Token);
                LoggingService.Debug($"[WebSocket] Successfully forwarded string content ({Math.Min(content?.Length ?? 0, 50)} chars)");
                return true;
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[WebSocket] Error forwarding string content: {ex.Message}");
                return false;
            }
        }

        private async Task ListenForFrontendMessagesAsync()
        {
            try
            {
                var buffer = new byte[4096 * 457];

                while (_frontendSocket?.State == WebSocketState.Open && !_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    var result = await _frontendSocket.ReceiveAsync(new ArraySegment<byte>(buffer), _cancellationTokenSource.Token);

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        LoggingService.Debug($"[WebSocket] Received message from frontend: {message}");
                        _frontendMessageHandler?.Invoke(message);
                    }
                    else if (result.MessageType == WebSocketMessageType.Close)
                    {
                        LoggingService.Info("[WebSocket] Frontend requested connection close");
                        _frontendConnected = false;
                        break;
                    }
                }
            }
            catch (WebSocketException wsEx)
            {
                LoggingService.Info($"[WebSocket] Frontend listener WebSocket error: {wsEx.Message}");
                _frontendConnected = false;
                _connectionLostTime = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[WebSocket] Frontend listener error: {ex.Message}");
                _frontendConnected = false;
                _connectionLostTime = DateTime.UtcNow;
            }

            LoggingService.Info("[WebSocket] Frontend connection listener exited - monitoring loop will attempt reconnection");
        }

        public async Task DisconnectFromFrontendAsync()
        {
            try
            {
                if (_frontendSocket?.State == WebSocketState.Open)
                {
                    LoggingService.Info("[WebSocket] Closing frontend WebSocket connection...");
                    await _frontendSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Application closing", CancellationToken.None);
                }
                _frontendConnected = false;
                LoggingService.Info("[WebSocket] Frontend WebSocket disconnected");
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[WebSocket] Error disconnecting from frontend: {ex.Message}");
            }
        }

        public (bool frontendConnected, bool frontendAvailable) GetConnectionStatus()
        {
            return (_frontendConnected, _frontendAvailable);
        }

        private async Task MonitorConnectionAsync()
        {
            LoggingService.Info("[WebSocket] Connection monitoring started");
            try
            {
                while (!_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    await Task.Delay(5000, _cancellationTokenSource.Token);
                    
                    // Check if connection is lost
                    bool isConnectionLost = false;
                    
                    if (_frontendSocket != null)
                    {
                        var state = _frontendSocket.State;
                        if (state == WebSocketState.Closed || state == WebSocketState.CloseReceived || 
                            state == WebSocketState.CloseSent || state == WebSocketState.Aborted)
                        {
                            isConnectionLost = true;
                            if (_connectionLostTime == DateTime.MinValue)
                            {
                                _connectionLostTime = DateTime.UtcNow;
                            }
                        }
                        else if (state != WebSocketState.Open && _frontendConnected)
                        {
                            isConnectionLost = true;
                            if (_connectionLostTime == DateTime.MinValue)
                            {
                                _connectionLostTime = DateTime.UtcNow;
                            }
                        }
                    }
                    else if (!_frontendConnected && _frontendAvailable)
                    {
                        isConnectionLost = true;
                        if (_connectionLostTime == DateTime.MinValue)
                        {
                            _connectionLostTime = DateTime.UtcNow;
                        }
                    }
                    
                    // Attempt reconnection if connection is lost
                    if (isConnectionLost && _frontendAvailable && !_isReconnecting)
                    {
                        var timeSinceLost = DateTime.UtcNow - _connectionLostTime;
                        
                        // Wait at least 5 seconds before attempting reconnection
                        if (timeSinceLost.TotalSeconds >= ReconnectionDelaySeconds)
                        {
                            if (_reconnectionAttempts < MaxReconnectionAttempts)
                            {
                                _isReconnecting = true;
                                _reconnectionAttempts++;
                                
                                LoggingService.Info($"[WebSocket] Attempting reconnection #{_reconnectionAttempts} (after {timeSinceLost.TotalSeconds:F0}s)");
                                
                                try
                                {
                                    _frontendConnected = false;
                                    
                                    // Use stored JWT token or get from Globals
                                    string token = _storedJwtToken ?? Globals.BackendAccessToken ?? "no_auth_websocket_only";
                                    
                                    bool success = await ConnectToFrontendAsync(token);
                                    
                                    if (success)
                                    {
                                        LoggingService.Info($"[WebSocket] ✅ Reconnected successfully after {_reconnectionAttempts} attempts");
                                        _reconnectionAttempts = 0;
                                        _connectionLostTime = DateTime.MinValue;
                                    }
                                    else
                                    {
                                        LoggingService.Warn($"[WebSocket] ❌ Reconnection attempt #{_reconnectionAttempts} failed, will retry...");
                                    }
                                }
                                catch (Exception reconnectEx)
                                {
                                    LoggingService.Info($"[WebSocket] Reconnection error: {reconnectEx.Message}");
                                }
                                finally
                                {
                                    _isReconnecting = false;
                                }
                            }
                            else
                            {
                                LoggingService.Warn($"[WebSocket] Max reconnection attempts ({MaxReconnectionAttempts}) reached. Stopping reconnection attempts.");
                                _frontendAvailable = false; // Stop trying
                            }
                        }
                    }
                }
            }
            catch (TaskCanceledException)
            {
                // normal on dispose
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[WebSocket] Monitoring error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            try
            {
                _cancellationTokenSource?.Cancel();
                _frontendSocket?.Dispose();
                _cancellationTokenSource?.Dispose();
            }
            catch { }
        }
    }
}


