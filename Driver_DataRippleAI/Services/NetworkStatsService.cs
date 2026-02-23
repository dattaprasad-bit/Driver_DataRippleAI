using System.Net.NetworkInformation;

namespace DataRippleAIDesktop.Services
{
    /// <summary>
    /// Service to track and report network statistics for the application
    /// </summary>
    public class NetworkStatsService : IDisposable
    {
        private readonly object _lock = new object();
        private CancellationTokenSource _cancellationTokenSource;
        private bool _isDisposed = false;

        // WebSocket Stats
        public bool WebSocketConnected { get; private set; } = false;
        public long WebSocketMessagesSent { get; private set; } = 0;
        public long WebSocketMessagesReceived { get; private set; } = 0;
        public long WebSocketBytesSent { get; private set; } = 0;
        public long WebSocketBytesReceived { get; private set; } = 0;
        public DateTime? WebSocketConnectedAt { get; private set; } = null;
        public int WebSocketReconnectionAttempts { get; private set; } = 0;
        public TimeSpan? WebSocketLatency { get; private set; } = null;
        public DateTime? LastWebSocketMessageSent { get; private set; } = null;
        public DateTime? LastWebSocketMessageReceived { get; private set; } = null;

        // Network Interface Stats
        public long TotalBytesSent { get; private set; } = 0;
        public long TotalBytesReceived { get; private set; } = 0;
        public double CurrentUploadSpeed { get; private set; } = 0; // bytes per second
        public double CurrentDownloadSpeed { get; private set; } = 0; // bytes per second

        // Internet Connectivity Stats
        public bool InternetConnected { get; private set; } = false;
        public TimeSpan? InternetLatency { get; private set; } = null;
        private const string PingHost = "8.8.8.8"; // Google DNS - reliable for connectivity testing
        private const int PingTimeoutMs = 3000; // 3 second timeout

        // Events
        public event EventHandler<NetworkStatsEventArgs> StatsUpdated;

        public NetworkStatsService()
        {
            _cancellationTokenSource = new CancellationTokenSource();
            _ = Task.Run(MonitorNetworkInterfaceAsync);
            _ = Task.Run(MonitorInternetConnectivityAsync);
        }

        /// <summary>
        /// Update WebSocket connection status
        /// </summary>
        public void UpdateWebSocketStatus(bool connected, DateTime? connectedAt = null)
        {
            lock (_lock)
            {
                WebSocketConnected = connected;
                if (connected && connectedAt.HasValue)
                {
                    WebSocketConnectedAt = connectedAt.Value;
                }
                else if (!connected)
                {
                    WebSocketConnectedAt = null;
                }
                OnStatsUpdated();
            }
        }

        /// <summary>
        /// Record a WebSocket message sent
        /// </summary>
        public void RecordWebSocketMessageSent(int bytes)
        {
            lock (_lock)
            {
                WebSocketMessagesSent++;
                WebSocketBytesSent += bytes;
                LastWebSocketMessageSent = DateTime.UtcNow;
                OnStatsUpdated();
            }
        }

        /// <summary>
        /// Record a WebSocket message received
        /// </summary>
        public void RecordWebSocketMessageReceived(int bytes)
        {
            lock (_lock)
            {
                WebSocketMessagesReceived++;
                WebSocketBytesReceived += bytes;
                LastWebSocketMessageReceived = DateTime.UtcNow;
                OnStatsUpdated();
            }
        }

        /// <summary>
        /// Increment reconnection attempts
        /// </summary>
        public void IncrementReconnectionAttempts()
        {
            lock (_lock)
            {
                WebSocketReconnectionAttempts++;
                OnStatsUpdated();
            }
        }

        /// <summary>
        /// Reset reconnection attempts counter
        /// </summary>
        public void ResetReconnectionAttempts()
        {
            lock (_lock)
            {
                WebSocketReconnectionAttempts = 0;
                OnStatsUpdated();
            }
        }

        /// <summary>
        /// Update WebSocket latency
        /// </summary>
        public void UpdateWebSocketLatency(TimeSpan latency)
        {
            lock (_lock)
            {
                WebSocketLatency = latency;
                OnStatsUpdated();
            }
        }

        /// <summary>
        /// Get formatted connection uptime
        /// </summary>
        public string GetConnectionUptime()
        {
            if (!WebSocketConnectedAt.HasValue)
                return "N/A";

            var uptime = DateTime.UtcNow - WebSocketConnectedAt.Value;
            if (uptime.TotalHours >= 1)
                return $"{(int)uptime.TotalHours}h {uptime.Minutes}m";
            else if (uptime.TotalMinutes >= 1)
                return $"{uptime.Minutes}m {uptime.Seconds}s";
            else
                return $"{uptime.Seconds}s";
        }

        /// <summary>
        /// Get formatted data transfer (sent)
        /// </summary>
        public string GetFormattedBytesSent()
        {
            return FormatBytes(WebSocketBytesSent);
        }

        /// <summary>
        /// Get formatted data transfer (received)
        /// </summary>
        public string GetFormattedBytesReceived()
        {
            return FormatBytes(WebSocketBytesReceived);
        }

        /// <summary>
        /// Get formatted upload speed
        /// </summary>
        public string GetFormattedUploadSpeed()
        {
            return FormatSpeed(CurrentUploadSpeed);
        }

        /// <summary>
        /// Get formatted download speed
        /// </summary>
        public string GetFormattedDownloadSpeed()
        {
            return FormatSpeed(CurrentDownloadSpeed);
        }

        /// <summary>
        /// Get formatted WebSocket latency
        /// </summary>
        public string GetFormattedWebSocketLatency()
        {
            if (!WebSocketLatency.HasValue)
                return "N/A";
            
            var ms = WebSocketLatency.Value.TotalMilliseconds;
            if (ms < 1)
                return $"{ms * 1000:F0}μs";
            else if (ms < 1000)
                return $"{ms:F0}ms";
            else
                return $"{ms / 1000:F2}s";
        }

        /// <summary>
        /// Get formatted internet latency
        /// </summary>
        public string GetFormattedInternetLatency()
        {
            if (!InternetLatency.HasValue)
                return "N/A";
            
            var ms = InternetLatency.Value.TotalMilliseconds;
            if (ms < 1)
                return $"{ms * 1000:F0}μs";
            else if (ms < 1000)
                return $"{ms:F0}ms";
            else
                return $"{ms / 1000:F2}s";
        }

        /// <summary>
        /// Monitor internet connectivity and latency
        /// </summary>
        private async Task MonitorInternetConnectivityAsync()
        {
            try
            {
                using (var ping = new Ping())
                {
                    while (!_cancellationTokenSource.Token.IsCancellationRequested)
                    {
                        try
                        {
                            var reply = await ping.SendPingAsync(PingHost, PingTimeoutMs);
                            
                            lock (_lock)
                            {
                                InternetConnected = reply.Status == IPStatus.Success;
                                if (InternetConnected)
                                {
                                    InternetLatency = TimeSpan.FromMilliseconds(reply.RoundtripTime);
                                }
                                else
                                {
                                    InternetLatency = null;
                                }
                                OnStatsUpdated();
                            }
                        }
                        catch (Exception ex)
                        {
                            lock (_lock)
                            {
                                InternetConnected = false;
                                InternetLatency = null;
                                OnStatsUpdated();
                            }
                            LoggingService.Debug($"[NetworkStats] Ping error: {ex.Message}");
                        }

                        // Ping every 5 seconds
                        await Task.Delay(5000, _cancellationTokenSource.Token);
                    }
                }
            }
            catch (TaskCanceledException)
            {
                // Expected on dispose
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[NetworkStats] Internet connectivity monitor error: {ex.Message}");
            }
        }

        /// <summary>
        /// Monitor network interface statistics
        /// </summary>
        private async Task MonitorNetworkInterfaceAsync()
        {
            try
            {
                long lastBytesSent = 0;
                long lastBytesReceived = 0;
                DateTime lastCheck = DateTime.UtcNow;

                while (!_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    await Task.Delay(1000, _cancellationTokenSource.Token); // Update every second

                    try
                    {
                        var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                            .Where(ni => ni.OperationalStatus == OperationalStatus.Up &&
                                        ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                            .ToList();

                        long totalSent = 0;
                        long totalReceived = 0;

                        foreach (var ni in interfaces)
                        {
                            var stats = ni.GetIPStatistics();
                            totalSent += stats.BytesSent;
                            totalReceived += stats.BytesReceived;
                        }

                        lock (_lock)
                        {
                            var timeDelta = (DateTime.UtcNow - lastCheck).TotalSeconds;
                            if (timeDelta > 0)
                            {
                                CurrentUploadSpeed = (totalSent - lastBytesSent) / timeDelta;
                                CurrentDownloadSpeed = (totalReceived - lastBytesReceived) / timeDelta;
                            }

                            TotalBytesSent = totalSent;
                            TotalBytesReceived = totalReceived;

                            lastBytesSent = totalSent;
                            lastBytesReceived = totalReceived;
                            lastCheck = DateTime.UtcNow;

                            OnStatsUpdated();
                        }
                    }
                    catch (Exception ex)
                    {
                        LoggingService.Debug($"[NetworkStats] Error monitoring network interface: {ex.Message}");
                    }
                }
            }
            catch (TaskCanceledException)
            {
                // Expected on dispose
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[NetworkStats] Monitor error: {ex.Message}");
            }
        }

        private void OnStatsUpdated()
        {
            try
            {
                StatsUpdated?.Invoke(this, new NetworkStatsEventArgs(this));
            }
            catch (Exception ex)
            {
                LoggingService.Debug($"[NetworkStats] Error raising stats updated event: {ex.Message}");
            }
        }

        private string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        private string FormatSpeed(double bytesPerSecond)
        {
            string[] sizes = { "B/s", "KB/s", "MB/s", "GB/s" };
            double speed = bytesPerSecond;
            int order = 0;
            while (speed >= 1024 && order < sizes.Length - 1)
            {
                order++;
                speed = speed / 1024;
            }
            return $"{speed:0.##} {sizes[order]}";
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource?.Dispose();
        }
    }

    /// <summary>
    /// Event args for network stats updates
    /// </summary>
    public class NetworkStatsEventArgs : EventArgs
    {
        public NetworkStatsService Stats { get; }

        public NetworkStatsEventArgs(NetworkStatsService stats)
        {
            Stats = stats;
        }
    }
}

