using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using DataRippleAIDesktop.Models;
using DataRippleAIDesktop.Services;

namespace DataRippleAIDesktop.Services
{
    /// <summary>
    /// Connects to the frontend WebSocket solely to check whether the frontend
    /// is active and logged in with the same credentials.  Exposes connection
    /// status for the "FE WS" indicator on the landing page.
    ///
    /// All data-sending / relay / forwarding logic has been removed — this
    /// service no longer sends transcripts, agent responses, tool calls, or
    /// any other payload to the frontend.
    /// </summary>
    public class FrontendIntegrationService : IDisposable
    {
        private FrontendWebSocketClient _webSocketManager;

        // Health ping/pong tracking (keeps the WS alive so the status indicator stays accurate)
        private CancellationTokenSource _healthPingCancellationToken;
        private Task _healthPingTask;
        private const int HealthPingIntervalSeconds = 30;

        /// <summary>Whether the frontend WebSocket is currently connected.</summary>
        public bool IsConnectedToFrontend => _webSocketManager?.IsFrontendConnected ?? false;

        // ── Constructors ──────────────────────────────────────────────

        public FrontendIntegrationService()
        {
            _webSocketManager = new FrontendWebSocketClient();
            LoggingService.Info("[Frontend] Frontend integration service initialized (connection-check only)");
        }

        public FrontendIntegrationService(IConfiguration configuration, IVRService ivrService = null)
        {
            var clientIntegrationConfig = configuration.GetSection("ClientIntegration").Get<ClientIntegrationConfiguration>();

            if (clientIntegrationConfig?.EnableFrontendIntegration == true && !string.IsNullOrEmpty(clientIntegrationConfig?.WebSocketUrl))
            {
                _webSocketManager = new FrontendWebSocketClient(clientIntegrationConfig.WebSocketUrl);
                LoggingService.Info($"[Frontend] WebSocket initialized from config - URL: {clientIntegrationConfig.WebSocketUrl}");
            }
            else if (!string.IsNullOrEmpty(Globals.FrontendSocketUrl))
            {
                _webSocketManager = new FrontendWebSocketClient(Globals.FrontendSocketUrl);
                LoggingService.Info($"[Frontend] WebSocket initialized from login API - URL: {Globals.FrontendSocketUrl}");
            }
            else
            {
                LoggingService.Info("[Frontend] Frontend integration disabled or URL not configured; will try to initialize at runtime if login provides a socket URL");
            }

            LoggingService.Info($"[Frontend] Service initialized (connection-check only) - Frontend Integration: {clientIntegrationConfig?.EnableFrontendIntegration}");
        }

        // ── Initialization ────────────────────────────────────────────

        /// <summary>
        /// Connect to the frontend WebSocket (no data is sent).
        /// </summary>
        public async Task<bool> InitializeAsync()
        {
            try
            {
                LoggingService.Info("[Frontend] Initializing frontend integration (connection-check only)...");

                if (_webSocketManager != null || !string.IsNullOrEmpty(Globals.FrontendSocketUrl))
                {
                    // Ensure manager exists and points to runtime socket URL when provided
                    if (!string.IsNullOrEmpty(Globals.FrontendSocketUrl))
                    {
                        if (_webSocketManager != null)
                        {
                            try { _webSocketManager.Dispose(); } catch { }
                        }
                        _webSocketManager = new FrontendWebSocketClient(Globals.FrontendSocketUrl);
                        LoggingService.Info($"[Frontend] Using WebSocket URL from login API: {Globals.FrontendSocketUrl}");
                    }

                    var token = string.IsNullOrEmpty(Globals.BackendAccessToken) ? "no_auth_websocket_only" : Globals.BackendAccessToken;
                    LoggingService.Info("[Frontend] Connecting to WebSocket (connection-check only)...");
                    bool wsSuccess = await _webSocketManager.ConnectToFrontendAsync(token);

                    if (!wsSuccess)
                    {
                        LoggingService.Info("[Frontend] WebSocket connection failed, but continuing in local mode");
                    }
                    else
                    {
                        LoggingService.Info("[Frontend] WebSocket connected successfully (connection-check only)");
                        StartHealthPingTask();
                    }

                    LoggingService.Info($"[Frontend] Integration initialized - WebSocket: {wsSuccess}");
                    return true;
                }

                LoggingService.Info("[Frontend] WebSocket disabled in configuration, skipping connection");
                return true;
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[Frontend] Error during initialization: {ex.Message}");
                return true; // Continue even if initialization fails
            }
        }

        // ── Status ────────────────────────────────────────────────────

        /// <summary>
        /// Get integration status for the FE WS indicator.
        /// </summary>
        public (bool authenticated, bool frontendConnected) GetStatus()
        {
            bool isAuthenticated = !string.IsNullOrEmpty(Globals.BackendAccessToken);
            return (isAuthenticated, _webSocketManager?.IsFrontendConnected ?? false);
        }

        /// <summary>
        /// Refresh the frontend WebSocket connection if it dropped.
        /// </summary>
        public async Task<bool> RefreshConnectionsAsync()
        {
            try
            {
                LoggingService.Info("[Frontend] Refreshing connections...");

                if (_webSocketManager != null && !_webSocketManager.IsFrontendConnected)
                {
                    string token = Globals.BackendAccessToken ?? "offline_mode_token";
                    await _webSocketManager.ConnectToFrontendAsync(token);
                }

                return true;
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[Frontend] Error refreshing connections: {ex.Message}");
                return false;
            }
        }

        // ── Health ping (keeps the WS alive) ──────────────────────────

        private void StartHealthPingTask()
        {
            try
            {
                if (_healthPingTask != null && !_healthPingTask.IsCompleted)
                {
                    return;
                }

                StopHealthPingTask();

                _healthPingCancellationToken = new CancellationTokenSource();
                _healthPingTask = Task.Run(async () => await HealthPingLoopAsync(_healthPingCancellationToken.Token), _healthPingCancellationToken.Token);
                LoggingService.Info("[Frontend] Health ping task started");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[Frontend] Error starting health ping task: {ex.Message}");
            }
        }

        private void StopHealthPingTask()
        {
            try
            {
                _healthPingCancellationToken?.Cancel();
                _healthPingTask?.Wait(TimeSpan.FromSeconds(2));
                _healthPingCancellationToken?.Dispose();
                _healthPingCancellationToken = null;
                _healthPingTask = null;
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[Frontend] Error stopping health ping task: {ex.Message}");
            }
        }

        private async Task HealthPingLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(HealthPingIntervalSeconds), cancellationToken);

                    if (!cancellationToken.IsCancellationRequested && _webSocketManager?.IsFrontendConnected == true)
                    {
                        await SendHealthPingAsync();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                LoggingService.Debug("[Frontend] Health ping loop cancelled");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[Frontend] Error in health ping loop: {ex.Message}");
            }
        }

        private async Task<bool> SendHealthPingAsync()
        {
            try
            {
                string userId = "unknown_user";
                try
                {
                    var userDetails = SecureTokenStorage.RetrieveUserDetails();
                    userId = userDetails?.Email?.ToString() ?? "unknown_user";
                }
                catch { }

                var pingEvent = new HealthPingEvent
                {
                    Timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    Meta = new { source = "driver", user_id = userId }
                };

                var sent = await _webSocketManager.ForwardToFrontendAsync(pingEvent);
                if (sent)
                {
                    LoggingService.Debug($"[Frontend] Health ping sent - UserID: {userId}");
                }
                return sent;
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[Frontend] Error sending health ping: {ex.Message}");
                return false;
            }
        }

        // ── Disposal ──────────────────────────────────────────────────

        public void Dispose()
        {
            try
            {
                StopHealthPingTask();
                _webSocketManager?.Dispose();
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[Frontend] Error during disposal: {ex.Message}");
            }
        }
    }
}
