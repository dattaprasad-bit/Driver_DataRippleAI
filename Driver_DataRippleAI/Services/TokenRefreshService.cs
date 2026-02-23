using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;

namespace DataRippleAIDesktop.Services
{
    /// <summary>
    /// Service that automatically refreshes authentication tokens at configured intervals
    /// </summary>
    public class TokenRefreshService : IDisposable
    {
        private readonly IConfiguration _configuration;
        private DispatcherTimer _refreshTimer;
        private readonly object _refreshLock = new object();
        private bool _isRefreshing = false;
        private bool _disposed = false;

        public event EventHandler<string> TokenRefreshSuccess;
        public event EventHandler<string> TokenRefreshFailed;

        public TokenRefreshService(IConfiguration configuration)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        }

        /// <summary>
        /// Start the token refresh timer
        /// </summary>
        public void StartRefreshTimer()
        {
            try
            {
                if (_disposed)
                {
                    LoggingService.Warn("[TokenRefreshService] Cannot start timer - service is disposed");
                    return;
                }

                // Get refresh interval from configuration (default to 15 minutes)
                int refreshIntervalInMinutes = _configuration.GetValue<int>("Backend:RefreshIntervalInMinutes", 15);
                
                if (refreshIntervalInMinutes <= 0)
                {
                    LoggingService.Warn($"[TokenRefreshService] Invalid refresh interval: {refreshIntervalInMinutes} minutes. Using default 15 minutes.");
                    refreshIntervalInMinutes = 15;
                }

                LoggingService.Info($"[TokenRefreshService] Starting token refresh timer with interval: {refreshIntervalInMinutes} minutes");

                // Create and configure timer
                _refreshTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMinutes(refreshIntervalInMinutes)
                };
                _refreshTimer.Tick += async (sender, e) => await RefreshTokenAsync();
                _refreshTimer.Start();

                LoggingService.Info($"[TokenRefreshService] Token refresh timer started successfully");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[TokenRefreshService] Error starting refresh timer: {ex.Message}");
            }
        }

        /// <summary>
        /// Stop the token refresh timer
        /// </summary>
        public void StopRefreshTimer()
        {
            try
            {
                if (_refreshTimer != null)
                {
                    _refreshTimer.Stop();
                    _refreshTimer = null;
                    LoggingService.Info("[TokenRefreshService] Token refresh timer stopped");
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[TokenRefreshService] Error stopping refresh timer: {ex.Message}");
            }
        }

        /// <summary>
        /// Refresh the authentication token
        /// </summary>
        private async Task RefreshTokenAsync()
        {
            // Prevent concurrent refresh attempts
            lock (_refreshLock)
            {
                if (_isRefreshing)
                {
                    LoggingService.Info("[TokenRefreshService] Token refresh already in progress, skipping...");
                    return;
                }
                _isRefreshing = true;
            }

            try
            {
                // Check if we have a refresh token
                var userDetails = SecureTokenStorage.RetrieveUserDetails();
                if (userDetails == null)
                {
                    LoggingService.Warn("[TokenRefreshService] No user details found - cannot refresh token");
                    return;
                }

                string refreshToken = userDetails?.RefreshToken?.ToString() ?? string.Empty;
                if (string.IsNullOrEmpty(refreshToken))
                {
                    LoggingService.Warn("[TokenRefreshService] No refresh token available - skipping refresh");
                    return;
                }

                // Debug: Log token info (first/last 10 chars only for security)
                string tokenPreview = refreshToken.Length > 20 
                    ? $"{refreshToken.Substring(0, 10)}...{refreshToken.Substring(refreshToken.Length - 10)}" 
                    : "***";
                LoggingService.Info($"[TokenRefreshService] Starting token refresh with token: {tokenPreview}");

                // Get backend URL from configuration
                var backendBase = _configuration["Backend:BaseUrl"] ?? "https://ghostagent-dev.dataripple.ai/api/";
                var refreshUrl = backendBase.TrimEnd('/') + "/auth/refresh";

                using (var httpClient = HttpClientFactory.CreateApiHttpClient())
                {
                    // Prepare payload
                    var payload = new { refresh_token = refreshToken };
                    var json = JsonConvert.SerializeObject(payload);
                    var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

                    LoggingService.Info($"[TokenRefreshService] Calling refresh token endpoint: {refreshUrl}");
                    LoggingService.Info($"[TokenRefreshService] Payload structure: {{ refresh_token: \"...\" }}");

                    // Call refresh token endpoint
                    var response = await httpClient.PostAsync(refreshUrl, content);
                    var responseText = await response.Content.ReadAsStringAsync();

                    LoggingService.Info($"[TokenRefreshService] Response status: {response.StatusCode}");
                    LoggingService.Info($"[TokenRefreshService] Response body: {responseText}");

                    if (!response.IsSuccessStatusCode)
                    {
                        string errorMsg = $"Token refresh failed: {response.StatusCode} - {responseText}";
                        LoggingService.Error($"[TokenRefreshService] {errorMsg}");
                        TokenRefreshFailed?.Invoke(this, errorMsg);
                        return;
                    }

                    // Parse response - Backend returns: { status, message, data: { access_token, token_type, expires_in } }
                    dynamic data = JsonConvert.DeserializeObject(responseText);

                    // The refresh endpoint returns data.access_token directly (no nested token object)
                    string newAccessToken = data?.data?.access_token ?? string.Empty;

                    // Backend refresh does NOT return refresh_token or socket_url
                    // Keep the existing refresh_token from stored session
                    string newRefreshToken = data?.data?.refresh_token ?? string.Empty;

                    if (string.IsNullOrEmpty(newAccessToken))
                    {
                        string errorMsg = $"Invalid refresh token response - no access token received. Response structure: {responseText}";
                        LoggingService.Error($"[TokenRefreshService] {errorMsg}");
                        LoggingService.Error($"[TokenRefreshService] Tried path: data.data.access_token");
                        TokenRefreshFailed?.Invoke(this, errorMsg);
                        return;
                    }

                    // If backend did not return a new refresh_token, keep the existing one
                    if (string.IsNullOrEmpty(newRefreshToken))
                    {
                        newRefreshToken = refreshToken; // Use the current refresh token
                        LoggingService.Info("[TokenRefreshService] Backend did not return new refresh_token - keeping existing one");
                    }

                    LoggingService.Info($"[TokenRefreshService] Successfully parsed tokens - Access token length: {newAccessToken.Length}, Refresh token length: {newRefreshToken.Length}");

                    // Update stored tokens - preserve existing socket URL and refresh token
                    string userName = userDetails?.Name?.ToString() ?? "User";
                    string userEmail = userDetails?.Email?.ToString() ?? "";
                    string existingSocketUrl = userDetails?.FrontendSocketUrl?.ToString() ?? "";

                    // Store updated session details (keeping existing refresh token and socket URL)
                    SecureTokenStorage.StoreSessionDetails(
                        newAccessToken,
                        newRefreshToken,
                        userName,
                        userEmail,
                        existingSocketUrl
                    );

                    // Update runtime globals
                    Globals.BackendAccessToken = newAccessToken;
                    Globals.BackendRefreshToken = newRefreshToken;

                    // Update the token in the frontend socket URL if it contains a token query param
                    if (!string.IsNullOrEmpty(Globals.FrontendSocketUrl) && Globals.FrontendSocketUrl.Contains("token="))
                    {
                        // Replace existing token value in the URL with the new access token
                        Globals.FrontendSocketUrl = System.Text.RegularExpressions.Regex.Replace(
                            Globals.FrontendSocketUrl,
                            @"(token=)[^&]*",
                            "$1" + Uri.EscapeDataString(newAccessToken));
                        LoggingService.Info("[TokenRefreshService] Updated token in FrontendSocketUrl");
                    }

                    LoggingService.Info("[TokenRefreshService] Token refresh successful");
                    TokenRefreshSuccess?.Invoke(this, "Token refreshed successfully");
                }
            }
            catch (Exception ex)
            {
                string errorMsg = $"Error during token refresh: {ex.Message}";
                LoggingService.Error($"[TokenRefreshService] {errorMsg}");
                TokenRefreshFailed?.Invoke(this, errorMsg);
            }
            finally
            {
                lock (_refreshLock)
                {
                    _isRefreshing = false;
                }
            }
        }

        /// <summary>
        /// Manually trigger a token refresh
        /// </summary>
        public async Task ManualRefreshAsync()
        {
            LoggingService.Info("[TokenRefreshService] Manual token refresh requested");
            await RefreshTokenAsync();
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                StopRefreshTimer();
                LoggingService.Info("[TokenRefreshService] Service disposed");
            }
        }
    }
}

