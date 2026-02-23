using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DataRippleAIDesktop.Services;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DataRippleAIDesktop.Views
{
    /// <summary>
    /// Production-mode Devices settings panel.
    /// Shows a streamlined dark-themed UI for selecting and testing audio devices.
    /// </summary>
    public partial class DeviceSettingsPanel : UserControl
    {
        public event Action NavigateBackRequested;

        private string _configFilePath;
        private string _selectedMicrophoneDevice = "";
        private string _selectedSpeakerDevice = "";
        private int _microphoneDeviceIndex = 0;
        private int _speakerDeviceIndex = 0;

        private DispatcherTimer _statusTimer;
        private DispatcherTimer _micLevelTimer;
        private WaveInEvent _micLevelMonitor;
        private volatile bool _micHasAudio = false;
        private readonly object _micLevelLock = new object();

        public DeviceSettingsPanel()
        {
            InitializeComponent();

            try
            {
                _configFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
                LoadAudioDeviceSettings();
                LoadMicrophoneDevices();
                LoadSpeakerDevices();

                // Start status bar update timer
                _statusTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(2)
                };
                _statusTimer.Tick += StatusTimer_Tick;
                _statusTimer.Start();

                // Start mic level monitoring timer
                _micLevelTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(500)
                };
                _micLevelTimer.Tick += MicLevelTimer_Tick;
                _micLevelTimer.Start();

                // Initial status bar update
                UpdateStatusBar();

                // Start monitoring mic audio after a brief delay so the device is ready
                StartMicLevelMonitoring();

                LoggingService.Info("[DeviceSettingsPanel] Initialized successfully");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[DeviceSettingsPanel] Error during initialization: {ex.Message}");
            }

            this.Unloaded += DeviceSettingsPanel_Unloaded;
        }

        private void DeviceSettingsPanel_Unloaded(object sender, RoutedEventArgs e)
        {
            Cleanup();
        }

        private void Cleanup()
        {
            try
            {
                _statusTimer?.Stop();
                _micLevelTimer?.Stop();
                StopMicLevelMonitoring();
                LoggingService.Info("[DeviceSettingsPanel] Cleaned up timers and mic monitor");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[DeviceSettingsPanel] Error during cleanup: {ex.Message}");
            }
        }

        /// <summary>
        /// Puts the panel into embedded mode by hiding the internal sidebar, header, and status bar.
        /// This is used when the panel is hosted inside another container (e.g., TabbedSettingsPage)
        /// that already provides its own chrome.
        /// </summary>
        public void SetEmbeddedMode()
        {
            try
            {
                // Hide the internal sidebar
                InternalSidebar.Visibility = Visibility.Collapsed;
                SidebarColumn.Width = new GridLength(0);

                // Hide the internal header (title + close button)
                InternalHeader.Visibility = Visibility.Collapsed;

                // Hide the internal status bar
                InternalStatusBar.Visibility = Visibility.Collapsed;

                // Stop the status timer since the parent container handles status
                _statusTimer?.Stop();

                LoggingService.Info("[DeviceSettingsPanel] Embedded mode activated - internal chrome hidden");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[DeviceSettingsPanel] Error setting embedded mode: {ex.Message}");
            }
        }

        #region Audio Device Loading

        private void LoadAudioDeviceSettings()
        {
            try
            {
                if (!File.Exists(_configFilePath)) return;

                var json = File.ReadAllText(_configFilePath);
                var config = JObject.Parse(json);

                if (config["AudioDevices"] != null)
                {
                    var audioDevices = config["AudioDevices"];
                    _selectedMicrophoneDevice = audioDevices["SelectedMicrophoneDevice"]?.ToString() ?? "";
                    _selectedSpeakerDevice = audioDevices["SelectedSpeakerDevice"]?.ToString() ?? "";
                    _microphoneDeviceIndex = audioDevices["MicrophoneDeviceIndex"]?.Value<int>() ?? 0;
                    _speakerDeviceIndex = audioDevices["SpeakerDeviceIndex"]?.Value<int>() ?? 0;

                    LoggingService.Info($"[DeviceSettingsPanel] Loaded settings - Mic: {_selectedMicrophoneDevice}, Speaker: {_selectedSpeakerDevice}");
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[DeviceSettingsPanel] Error loading audio device settings: {ex.Message}");
            }
        }

        private void LoadMicrophoneDevices()
        {
            try
            {
                MicrophoneCombo.Items.Clear();

                var deviceEnumerator = new MMDeviceEnumerator();
                foreach (var device in deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                {
                    MicrophoneCombo.Items.Add(device.FriendlyName);
                }

                if (MicrophoneCombo.Items.Count > 0)
                {
                    if (!string.IsNullOrEmpty(_selectedMicrophoneDevice))
                    {
                        int idx = MicrophoneCombo.Items.IndexOf(_selectedMicrophoneDevice);
                        if (idx >= 0)
                        {
                            MicrophoneCombo.SelectedIndex = idx;
                            LoggingService.Info($"[DeviceSettingsPanel] Restored microphone: {_selectedMicrophoneDevice} (Index: {idx})");
                        }
                        else
                        {
                            MicrophoneCombo.SelectedIndex = 0;
                            _selectedMicrophoneDevice = MicrophoneCombo.Items[0].ToString();
                            _microphoneDeviceIndex = 0;
                            LoggingService.Warn($"[DeviceSettingsPanel] Previous mic not found, using first: {_selectedMicrophoneDevice}");
                        }
                    }
                    else
                    {
                        MicrophoneCombo.SelectedIndex = 0;
                        _selectedMicrophoneDevice = MicrophoneCombo.Items[0].ToString();
                        _microphoneDeviceIndex = 0;
                    }
                }
                else
                {
                    LoggingService.Warn("[DeviceSettingsPanel] No microphone devices found");
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[DeviceSettingsPanel] Error loading microphone devices: {ex.Message}");
            }
        }

        private void LoadSpeakerDevices()
        {
            try
            {
                SpeakerCombo.Items.Clear();

                var deviceEnumerator = new MMDeviceEnumerator();
                foreach (var device in deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    SpeakerCombo.Items.Add(device.FriendlyName);
                }

                if (SpeakerCombo.Items.Count > 0)
                {
                    if (!string.IsNullOrEmpty(_selectedSpeakerDevice))
                    {
                        int idx = SpeakerCombo.Items.IndexOf(_selectedSpeakerDevice);
                        if (idx >= 0)
                        {
                            SpeakerCombo.SelectedIndex = idx;
                            LoggingService.Info($"[DeviceSettingsPanel] Restored speaker: {_selectedSpeakerDevice} (Index: {idx})");
                        }
                        else
                        {
                            SpeakerCombo.SelectedIndex = 0;
                            _selectedSpeakerDevice = SpeakerCombo.Items[0].ToString();
                            _speakerDeviceIndex = 0;
                            LoggingService.Warn($"[DeviceSettingsPanel] Previous speaker not found, using first: {_selectedSpeakerDevice}");
                        }
                    }
                    else
                    {
                        SpeakerCombo.SelectedIndex = 0;
                        _selectedSpeakerDevice = SpeakerCombo.Items[0].ToString();
                        _speakerDeviceIndex = 0;
                    }
                }
                else
                {
                    LoggingService.Warn("[DeviceSettingsPanel] No speaker devices found");
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[DeviceSettingsPanel] Error loading speaker devices: {ex.Message}");
            }
        }

        #endregion

        #region Selection Changed Handlers

        private void MicrophoneCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (MicrophoneCombo.SelectedItem == null) return;

                _selectedMicrophoneDevice = MicrophoneCombo.SelectedItem.ToString();
                _microphoneDeviceIndex = MicrophoneCombo.SelectedIndex;

                LoggingService.Info($"[DeviceSettingsPanel] Microphone changed: {_selectedMicrophoneDevice} (Index: {_microphoneDeviceIndex})");

                // Restart mic level monitoring for the new device
                StopMicLevelMonitoring();
                StartMicLevelMonitoring();

                // Auto-save device selection
                SaveDeviceSelection();
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[DeviceSettingsPanel] Error on microphone selection change: {ex.Message}");
            }
        }

        private void SpeakerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (SpeakerCombo.SelectedItem == null) return;

                _selectedSpeakerDevice = SpeakerCombo.SelectedItem.ToString();
                _speakerDeviceIndex = SpeakerCombo.SelectedIndex;

                LoggingService.Info($"[DeviceSettingsPanel] Speaker changed: {_selectedSpeakerDevice} (Index: {_speakerDeviceIndex})");

                // Auto-save device selection
                SaveDeviceSelection();
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[DeviceSettingsPanel] Error on speaker selection change: {ex.Message}");
            }
        }

        #endregion

        #region Test Buttons

        private async void TestMicButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                TestMicButton.IsEnabled = false;
                MicTestStatus.Text = "Testing...";
                MicTestStatus.Foreground = new SolidColorBrush(Colors.Orange);
                MicTestStatus.Visibility = Visibility.Visible;

                var deviceIndex = MicrophoneCombo.SelectedIndex;
                if (deviceIndex < 0 || deviceIndex >= WaveInEvent.DeviceCount)
                {
                    throw new InvalidOperationException("Invalid microphone device selected");
                }

                bool audioDetected = false;

                using (var waveIn = new WaveInEvent())
                {
                    waveIn.DeviceNumber = deviceIndex;
                    waveIn.WaveFormat = new WaveFormat(44100, 1);

                    waveIn.DataAvailable += (s, args) =>
                    {
                        // Check if there is meaningful audio level
                        float maxLevel = 0;
                        for (int i = 0; i < args.BytesRecorded - 1; i += 2)
                        {
                            short sample = (short)(args.Buffer[i] | (args.Buffer[i + 1] << 8));
                            float sampleLevel = Math.Abs(sample / (float)short.MaxValue);
                            if (sampleLevel > maxLevel) maxLevel = sampleLevel;
                        }
                        if (maxLevel > 0.01f)
                        {
                            audioDetected = true;
                        }
                    };

                    waveIn.StartRecording();
                    await Task.Delay(1500);
                    waveIn.StopRecording();
                }

                if (audioDetected)
                {
                    MicTestStatus.Text = "Audio detected - Microphone working";
                    MicTestStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#28A745"));
                    MicWarningPanel.Visibility = Visibility.Collapsed;
                }
                else
                {
                    MicTestStatus.Text = "No audio detected - check microphone";
                    MicTestStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFA500"));
                    MicWarningPanel.Visibility = Visibility.Visible;
                    MicWarningText.Text = "Microphone has no audio";
                }

                LoggingService.Info($"[DeviceSettingsPanel] Microphone test: audioDetected={audioDetected}, device={deviceIndex}");
            }
            catch (Exception ex)
            {
                MicTestStatus.Text = "Test failed";
                MicTestStatus.Foreground = new SolidColorBrush(Colors.Red);
                MicTestStatus.Visibility = Visibility.Visible;
                LoggingService.Error($"[DeviceSettingsPanel] Microphone test failed: {ex.Message}");
            }
            finally
            {
                TestMicButton.IsEnabled = true;
            }
        }

        private async void TestSpeakerButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                TestSpeakerButton.IsEnabled = false;
                SpeakerTestStatus.Text = "Playing test tone...";
                SpeakerTestStatus.Foreground = new SolidColorBrush(Colors.Orange);
                SpeakerTestStatus.Visibility = Visibility.Visible;

                var deviceIndex = SpeakerCombo.SelectedIndex;
                if (deviceIndex < 0 || SpeakerCombo.Items.Count == 0)
                {
                    throw new InvalidOperationException("Invalid speaker device selected");
                }

                using (var audioOut = new WaveOut())
                {
                    var testTone = GenerateTestTone(440, 1000, 44100);
                    using (var waveProvider = new TestToneProvider(testTone))
                    {
                        audioOut.Init(waveProvider);
                        audioOut.Play();
                        await Task.Delay(1200);
                        audioOut.Stop();
                    }
                }

                SpeakerTestStatus.Text = "Speaker working";
                SpeakerTestStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#28A745"));
                LoggingService.Info($"[DeviceSettingsPanel] Speaker test completed for device {deviceIndex}");
            }
            catch (Exception ex)
            {
                SpeakerTestStatus.Text = "Test failed";
                SpeakerTestStatus.Foreground = new SolidColorBrush(Colors.Red);
                SpeakerTestStatus.Visibility = Visibility.Visible;
                LoggingService.Error($"[DeviceSettingsPanel] Speaker test failed: {ex.Message}");
            }
            finally
            {
                TestSpeakerButton.IsEnabled = true;
            }
        }

        #endregion

        #region Mic Level Monitoring

        private void StartMicLevelMonitoring()
        {
            try
            {
                StopMicLevelMonitoring();

                var deviceIndex = MicrophoneCombo.SelectedIndex;
                if (deviceIndex < 0 || deviceIndex >= WaveInEvent.DeviceCount) return;

                _micHasAudio = false;

                _micLevelMonitor = new WaveInEvent
                {
                    DeviceNumber = deviceIndex,
                    WaveFormat = new WaveFormat(16000, 1),
                    BufferMilliseconds = 100
                };

                _micLevelMonitor.DataAvailable += (s, args) =>
                {
                    try
                    {
                        float maxLevel = 0;
                        for (int i = 0; i < args.BytesRecorded - 1; i += 2)
                        {
                            short sample = (short)(args.Buffer[i] | (args.Buffer[i + 1] << 8));
                            float sampleLevel = Math.Abs(sample / (float)short.MaxValue);
                            if (sampleLevel > maxLevel) maxLevel = sampleLevel;
                        }

                        lock (_micLevelLock)
                        {
                            _micHasAudio = maxLevel > 0.01f;
                        }
                    }
                    catch
                    {
                        // Silently ignore audio processing errors during monitoring
                    }
                };

                _micLevelMonitor.StartRecording();
                LoggingService.Info($"[DeviceSettingsPanel] Started mic level monitoring on device {deviceIndex}");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[DeviceSettingsPanel] Error starting mic level monitoring: {ex.Message}");
            }
        }

        private void StopMicLevelMonitoring()
        {
            try
            {
                if (_micLevelMonitor != null)
                {
                    _micLevelMonitor.StopRecording();
                    _micLevelMonitor.Dispose();
                    _micLevelMonitor = null;
                    LoggingService.Info("[DeviceSettingsPanel] Stopped mic level monitoring");
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[DeviceSettingsPanel] Error stopping mic level monitoring: {ex.Message}");
                _micLevelMonitor = null;
            }
        }

        private void MicLevelTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                bool hasAudio;
                lock (_micLevelLock)
                {
                    hasAudio = _micHasAudio;
                }

                if (!hasAudio && MicrophoneCombo.SelectedIndex >= 0)
                {
                    MicWarningPanel.Visibility = Visibility.Visible;
                    MicWarningText.Text = "Microphone has no audio";
                    MicBadge.Visibility = Visibility.Visible;
                }
                else
                {
                    MicWarningPanel.Visibility = Visibility.Collapsed;
                    MicBadge.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[DeviceSettingsPanel] Error in mic level timer: {ex.Message}");
            }
        }

        #endregion

        #region Status Bar

        private void StatusTimer_Tick(object sender, EventArgs e)
        {
            UpdateStatusBar();
        }

        private void UpdateStatusBar()
        {
            try
            {
                var statsService = Globals.NetworkStatsService;
                if (statsService != null)
                {
                    bool wsConnected = statsService.WebSocketConnected;
                    bool internetConnected = statsService.InternetConnected;

                    if (wsConnected)
                    {
                        ConnectionDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#28A745"));
                        ConnectionStatusText.Text = "Connected";
                    }
                    else if (internetConnected)
                    {
                        ConnectionDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFA500"));
                        ConnectionStatusText.Text = "Internet Only";
                    }
                    else
                    {
                        ConnectionDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#DC3545"));
                        ConnectionStatusText.Text = "Disconnected";
                    }

                    // Update latency
                    var latency = statsService.InternetLatency;
                    if (latency.HasValue)
                    {
                        long ms = (long)latency.Value.TotalMilliseconds;
                        LatencyText.Text = $"Latency: {ms}ms";
                        UpdateSignalBars(ms);
                    }
                    else
                    {
                        LatencyText.Text = "Latency: --";
                        SetSignalBarsColor("#6B7A94");
                    }
                }
                else
                {
                    // No stats service available - show default
                    ConnectionDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6B7A94"));
                    ConnectionStatusText.Text = "Status unavailable";
                    LatencyText.Text = "Latency: --";
                    SetSignalBarsColor("#6B7A94");
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[DeviceSettingsPanel] Error updating status bar: {ex.Message}");
            }
        }

        private void UpdateSignalBars(long latencyMs)
        {
            // Excellent: < 50ms (4 bars green)
            // Good: 50-100ms (3 bars green, 1 gray)
            // Fair: 100-200ms (2 bars orange)
            // Poor: > 200ms (1 bar red)

            if (latencyMs < 50)
            {
                SetSignalBarColors("#28A745", "#28A745", "#28A745", "#28A745");
            }
            else if (latencyMs < 100)
            {
                SetSignalBarColors("#28A745", "#28A745", "#28A745", "#3A4A64");
            }
            else if (latencyMs < 200)
            {
                SetSignalBarColors("#FFA500", "#FFA500", "#3A4A64", "#3A4A64");
            }
            else
            {
                SetSignalBarColors("#DC3545", "#3A4A64", "#3A4A64", "#3A4A64");
            }
        }

        private void SetSignalBarColors(string bar1, string bar2, string bar3, string bar4)
        {
            SignalBar1.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bar1));
            SignalBar2.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bar2));
            SignalBar3.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bar3));
            SignalBar4.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bar4));
        }

        private void SetSignalBarsColor(string color)
        {
            SetSignalBarColors(color, color, color, color);
        }

        #endregion

        #region Save Configuration

        private void SaveDeviceSelection()
        {
            try
            {
                if (!File.Exists(_configFilePath))
                {
                    LoggingService.Warn("[DeviceSettingsPanel] Config file not found, cannot save");
                    return;
                }

                var json = File.ReadAllText(_configFilePath);
                var config = JObject.Parse(json);

                if (config["AudioDevices"] == null)
                    config["AudioDevices"] = new JObject();

                var audioDevicesSection = (JObject)config["AudioDevices"];
                audioDevicesSection["SelectedMicrophoneDevice"] = _selectedMicrophoneDevice;
                audioDevicesSection["SelectedSpeakerDevice"] = _selectedSpeakerDevice;
                audioDevicesSection["MicrophoneDeviceIndex"] = _microphoneDeviceIndex;
                audioDevicesSection["SpeakerDeviceIndex"] = _speakerDeviceIndex;

                var output = config.ToString(Formatting.Indented);
                File.WriteAllText(_configFilePath, output);

                LoggingService.Info($"[DeviceSettingsPanel] Saved device selection - Mic: {_selectedMicrophoneDevice}, Speaker: {_selectedSpeakerDevice}");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[DeviceSettingsPanel] Error saving device selection: {ex.Message}");
            }
        }

        #endregion

        #region Close / Navigate Back

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Cleanup();
            NavigateBackRequested?.Invoke();
        }

        #endregion

        #region Test Tone Generation

        private byte[] GenerateTestTone(double frequency, int durationMs, int sampleRate)
        {
            var samples = (int)(sampleRate * durationMs / 1000.0);
            var buffer = new byte[samples * 2]; // 16-bit audio

            for (int i = 0; i < samples; i++)
            {
                var sample = (short)(Math.Sin(2 * Math.PI * frequency * i / sampleRate) * short.MaxValue * 0.3);
                buffer[i * 2] = (byte)(sample & 0xFF);
                buffer[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
            }

            return buffer;
        }

        private class TestToneProvider : IWaveProvider, IDisposable
        {
            private readonly byte[] _audioData;
            private int _position;
            private readonly WaveFormat _waveFormat;
            private bool _disposed = false;

            public TestToneProvider(byte[] audioData)
            {
                _audioData = audioData;
                _waveFormat = new WaveFormat(44100, 1);
            }

            public WaveFormat WaveFormat => _waveFormat;

            public int Read(byte[] buffer, int offset, int count)
            {
                if (_disposed) return 0;

                var bytesToRead = Math.Min(count, _audioData.Length - _position);
                if (bytesToRead > 0)
                {
                    Array.Copy(_audioData, _position, buffer, offset, bytesToRead);
                    _position += bytesToRead;
                }
                return bytesToRead;
            }

            public void Dispose()
            {
                _disposed = true;
            }
        }

        #endregion
    }
}
