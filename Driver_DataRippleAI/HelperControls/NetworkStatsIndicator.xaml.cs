using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using DataRippleAIDesktop.Services;

namespace DataRippleAIDesktop.HelperControls
{
    /// <summary>
    /// Network statistics indicator control
    /// </summary>
    public partial class NetworkStatsIndicator : System.Windows.Controls.UserControl
    {
        private NetworkStatsService _networkStatsService;
        private DispatcherTimer _updateTimer;

        public NetworkStatsIndicator()
        {
            InitializeComponent();
            _updateTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _updateTimer.Tick += UpdateTimer_Tick;
            this.Unloaded += NetworkStatsIndicator_Unloaded;
        }

        /// <summary>
        /// Initialize with network stats service
        /// </summary>
        public void Initialize(NetworkStatsService networkStatsService)
        {
            _networkStatsService = networkStatsService;
            if (_networkStatsService != null)
            {
                _networkStatsService.StatsUpdated += NetworkStatsService_StatsUpdated;
                _updateTimer.Start();
                UpdateDisplay();
            }
        }

        private void NetworkStatsService_StatsUpdated(object sender, NetworkStatsEventArgs e)
        {
            // Update on UI thread
            Dispatcher.Invoke(() => UpdateDisplay());
        }

        private void UpdateTimer_Tick(object sender, EventArgs e)
        {
            UpdateDisplay();
        }

        private void UpdateDisplay()
        {
            if (_networkStatsService == null)
                return;

            try
            {
                // Update internet connection status indicator
                if (_networkStatsService.InternetConnected)
                {
                    StatusBrush.Color = Color.FromRgb(76, 175, 80); // Green
                    txtConnectionStatus.Text = "Connected";
                    txtConnectionStatus.Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80));
                }
                else
                {
                    StatusBrush.Color = Color.FromRgb(255, 107, 107); // Red
                    txtConnectionStatus.Text = "Disconnected";
                    txtConnectionStatus.Foreground = new SolidColorBrush(Color.FromRgb(255, 107, 107));
                }

                // Update internet latency
                var latency = _networkStatsService.GetFormattedInternetLatency();
                txtLatency.Text = $"Latency: {latency}";

                // Update network speed (upload/download) - only show if connected
                if (_networkStatsService.InternetConnected)
                {
                    var uploadSpeed = _networkStatsService.GetFormattedUploadSpeed();
                    var downloadSpeed = _networkStatsService.GetFormattedDownloadSpeed();
                    txtSpeed.Text = $"↑{uploadSpeed} ↓{downloadSpeed}";
                }
                else
                {
                    txtSpeed.Text = "↑0 B/s ↓0 B/s";
                }
            }
            catch (Exception ex)
            {
                LoggingService.Debug($"[NetworkStatsIndicator] Error updating display: {ex.Message}");
            }
        }

        #region Backend WS Status

        /// <summary>
        /// Shows the Backend WS status section within the indicator.
        /// Called by MainWindow after successful authentication.
        /// </summary>
        public void ShowBackendWsStatus()
        {
            BackendWsSection.Visibility = Visibility.Visible;
            BackendWsSeparator.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// Hides the Backend WS status section within the indicator.
        /// Called by MainWindow on logout / login screen.
        /// </summary>
        public void HideBackendWsStatus()
        {
            BackendWsSection.Visibility = Visibility.Collapsed;
            BackendWsSeparator.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// Updates the Backend WS indicator color (fill + glow).
        /// Must be called on the UI (Dispatcher) thread.
        /// </summary>
        /// <param name="hexColor">Hex color string, e.g. "#27AE60"</param>
        public void UpdateBackendWsStatus(string hexColor)
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(hexColor);
                BackendWsIndicator.Fill = new SolidColorBrush(color);
                if (BackendWsIndicator.Effect is DropShadowEffect glow)
                {
                    glow.Color = color;
                }
            }
            catch (Exception ex)
            {
                LoggingService.Debug($"[NetworkStatsIndicator] Error updating backend WS status: {ex.Message}");
            }
        }

        #endregion

        private void NetworkStatsIndicator_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_networkStatsService != null)
            {
                _networkStatsService.StatsUpdated -= NetworkStatsService_StatsUpdated;
            }
            _updateTimer?.Stop();
        }
    }
}

