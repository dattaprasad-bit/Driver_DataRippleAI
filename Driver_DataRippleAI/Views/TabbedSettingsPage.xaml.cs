using System;
using System.IO;
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
    /// Settings page with three sections on a single scrollable panel:
    /// 1. Server Connection Settings (API URL + Username)
    /// 2. Devices (Microphone + Speakers side by side with Test buttons)
    /// 3. Access Logs
    /// </summary>
    public partial class TabbedSettingsPage : UserControl
    {
        public event Action NavigateBackRequested;

        private string _configFilePath;

        // API URL state (tracks the last saved value to detect changes)
        private string _loadedApiUrl = "";

        // Audio device state
        private string _selectedMicrophoneDevice = "";
        private string _selectedSpeakerDevice = "";
        private int _microphoneDeviceIndex = 0;
        private int _speakerDeviceIndex = 0;

        // Mic level monitoring
        private DispatcherTimer _micLevelTimer;
        private WaveInEvent _micLevelMonitor;
        private volatile bool _micHasAudio = false;
        private readonly object _micLevelLock = new object();

        public TabbedSettingsPage()
        {
            InitializeComponent();

            try
            {
                _configFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");

                // Load server connection settings (Username only)
                LoadServerConnectionSettings();

                // Load audio device settings and populate dropdowns
                LoadAudioDeviceSettings();
                LoadMicrophoneDevices();
                LoadSpeakerDevices();

                // Start mic level monitoring timer
                _micLevelTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(500)
                };
                _micLevelTimer.Tick += MicLevelTimer_Tick;
                _micLevelTimer.Start();

                // Start monitoring mic audio
                StartMicLevelMonitoring();

                LoggingService.Info("[TabbedSettingsPage] Initialized successfully");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[TabbedSettingsPage] Error during initialization: {ex.Message}");
            }

            this.Unloaded += TabbedSettingsPage_Unloaded;
        }

        #region Server Connection Settings

        /// <summary>
        /// Load server connection settings from appsettings.json (API URL + Username)
        /// </summary>
        private void LoadServerConnectionSettings()
        {
            try
            {
                if (!File.Exists(_configFilePath))
                {
                    LoggingService.Warn("[TabbedSettingsPage] appsettings.json not found");
                    return;
                }

                var json = File.ReadAllText(_configFilePath);
                var config = JObject.Parse(json);

                // Load API URL and Username from Backend section
                if (config["Backend"] != null)
                {
                    var backend = config["Backend"];

                    // Load API URL
                    _loadedApiUrl = backend["BaseUrl"]?.ToString() ?? "";
                    ApiUrlBox.Text = _loadedApiUrl;

                    // Load Username
                    UsernameBox.Text = backend["Username"]?.ToString() ?? "";

                    // If user is logged in, show email from stored user details
                    if (!string.IsNullOrEmpty(Globals.BackendAccessToken))
                    {
                        var userDetails = SecureTokenStorage.RetrieveUserDetails();
                        if (userDetails != null)
                        {
                            UsernameBox.Text = userDetails.Email ?? "";
                        }
                    }
                }

                // Load access logs
                LoadAccessLogs();

                LoggingService.Info("[TabbedSettingsPage] Server connection settings loaded");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[TabbedSettingsPage] Error loading server connection settings: {ex.Message}");
            }
        }

        #endregion

        #region Access Logs

        /// <summary>
        /// Load recent access logs from the application log files
        /// </summary>
        private void LoadAccessLogs()
        {
            try
            {
                string logsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                if (Directory.Exists(logsDir))
                {
                    var logFiles = Directory.GetFiles(logsDir, "DataRippleAI-*.log");
                    if (logFiles.Length > 0)
                    {
                        // Read the last few lines of the most recent log file
                        Array.Sort(logFiles);
                        string latestLog = logFiles[logFiles.Length - 1];

                        // Read last 2000 chars to show recent entries
                        var fileInfo = new FileInfo(latestLog);
                        if (fileInfo.Length > 0)
                        {
                            using (var fs = new FileStream(latestLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            {
                                long startPos = Math.Max(0, fs.Length - 2000);
                                fs.Seek(startPos, SeekOrigin.Begin);
                                using (var reader = new StreamReader(fs))
                                {
                                    string content = reader.ReadToEnd();
                                    // Skip partial first line
                                    int firstNewLine = content.IndexOf('\n');
                                    if (firstNewLine >= 0 && startPos > 0)
                                    {
                                        content = content.Substring(firstNewLine + 1);
                                    }
                                    AccessLogsText.Text = content.Trim();
                                }
                            }
                            return;
                        }
                    }
                }

                AccessLogsText.Text = "No access logs available.";
            }
            catch (Exception ex)
            {
                AccessLogsText.Text = $"Error loading logs: {ex.Message}";
                LoggingService.Error($"[TabbedSettingsPage] Error loading access logs: {ex.Message}");
            }
        }

        #endregion

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

                    LoggingService.Info($"[TabbedSettingsPage] Loaded audio settings - Mic: {_selectedMicrophoneDevice}, Speaker: {_selectedSpeakerDevice}");
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[TabbedSettingsPage] Error loading audio device settings: {ex.Message}");
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
                            LoggingService.Info($"[TabbedSettingsPage] Restored microphone: {_selectedMicrophoneDevice} (Index: {idx})");
                        }
                        else
                        {
                            MicrophoneCombo.SelectedIndex = 0;
                            _selectedMicrophoneDevice = MicrophoneCombo.Items[0].ToString();
                            _microphoneDeviceIndex = 0;
                            LoggingService.Warn($"[TabbedSettingsPage] Previous mic not found, using first: {_selectedMicrophoneDevice}");
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
                    LoggingService.Warn("[TabbedSettingsPage] No microphone devices found");
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[TabbedSettingsPage] Error loading microphone devices: {ex.Message}");
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
                            LoggingService.Info($"[TabbedSettingsPage] Restored speaker: {_selectedSpeakerDevice} (Index: {idx})");
                        }
                        else
                        {
                            SpeakerCombo.SelectedIndex = 0;
                            _selectedSpeakerDevice = SpeakerCombo.Items[0].ToString();
                            _speakerDeviceIndex = 0;
                            LoggingService.Warn($"[TabbedSettingsPage] Previous speaker not found, using first: {_selectedSpeakerDevice}");
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
                    LoggingService.Warn("[TabbedSettingsPage] No speaker devices found");
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[TabbedSettingsPage] Error loading speaker devices: {ex.Message}");
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

                LoggingService.Info($"[TabbedSettingsPage] Microphone changed: {_selectedMicrophoneDevice} (Index: {_microphoneDeviceIndex})");

                // Restart mic level monitoring for the new device
                StopMicLevelMonitoring();
                StartMicLevelMonitoring();

                // Auto-save device selection
                SaveDeviceSelection();
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[TabbedSettingsPage] Error on microphone selection change: {ex.Message}");
            }
        }

        private void SpeakerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (SpeakerCombo.SelectedItem == null) return;

                _selectedSpeakerDevice = SpeakerCombo.SelectedItem.ToString();
                _speakerDeviceIndex = SpeakerCombo.SelectedIndex;

                LoggingService.Info($"[TabbedSettingsPage] Speaker changed: {_selectedSpeakerDevice} (Index: {_speakerDeviceIndex})");

                // Auto-save device selection
                SaveDeviceSelection();
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[TabbedSettingsPage] Error on speaker selection change: {ex.Message}");
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

                LoggingService.Info($"[TabbedSettingsPage] Microphone test: audioDetected={audioDetected}, device={deviceIndex}");
            }
            catch (Exception ex)
            {
                MicTestStatus.Text = "Test failed";
                MicTestStatus.Foreground = new SolidColorBrush(Colors.Red);
                MicTestStatus.Visibility = Visibility.Visible;
                LoggingService.Error($"[TabbedSettingsPage] Microphone test failed: {ex.Message}");
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
                LoggingService.Info($"[TabbedSettingsPage] Speaker test completed for device {deviceIndex}");
            }
            catch (Exception ex)
            {
                SpeakerTestStatus.Text = "Test failed";
                SpeakerTestStatus.Foreground = new SolidColorBrush(Colors.Red);
                SpeakerTestStatus.Visibility = Visibility.Visible;
                LoggingService.Error($"[TabbedSettingsPage] Speaker test failed: {ex.Message}");
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
                LoggingService.Info($"[TabbedSettingsPage] Started mic level monitoring on device {deviceIndex}");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[TabbedSettingsPage] Error starting mic level monitoring: {ex.Message}");
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
                    LoggingService.Info("[TabbedSettingsPage] Stopped mic level monitoring");
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[TabbedSettingsPage] Error stopping mic level monitoring: {ex.Message}");
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
                }
                else
                {
                    MicWarningPanel.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[TabbedSettingsPage] Error in mic level timer: {ex.Message}");
            }
        }

        #endregion

        #region Save Configuration

        private void SaveDeviceSelection()
        {
            try
            {
                if (!File.Exists(_configFilePath))
                {
                    LoggingService.Warn("[TabbedSettingsPage] Config file not found, cannot save");
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

                LoggingService.Info($"[TabbedSettingsPage] Saved device selection - Mic: {_selectedMicrophoneDevice}, Speaker: {_selectedSpeakerDevice}");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[TabbedSettingsPage] Error saving device selection: {ex.Message}");
            }
        }

        /// <summary>
        /// Called when the API URL text box loses focus. Saves the value if it changed.
        /// </summary>
        private void ApiUrlBox_LostFocus(object sender, RoutedEventArgs e)
        {
            try
            {
                var newUrl = ApiUrlBox.Text?.Trim() ?? "";
                if (string.Equals(newUrl, _loadedApiUrl, StringComparison.Ordinal))
                    return; // No change, nothing to save

                SaveApiUrl(newUrl);
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[TabbedSettingsPage] Error in ApiUrlBox_LostFocus: {ex.Message}");
            }
        }

        /// <summary>
        /// Save the API URL to appsettings.json under Backend:BaseUrl
        /// </summary>
        private void SaveApiUrl(string newUrl)
        {
            try
            {
                if (!File.Exists(_configFilePath))
                {
                    LoggingService.Warn("[TabbedSettingsPage] Config file not found, cannot save API URL");
                    return;
                }

                var json = File.ReadAllText(_configFilePath);
                var config = JObject.Parse(json);

                if (config["Backend"] == null)
                    config["Backend"] = new JObject();

                var backendSection = (JObject)config["Backend"];
                backendSection["BaseUrl"] = newUrl;

                var output = config.ToString(Formatting.Indented);
                File.WriteAllText(_configFilePath, output);

                _loadedApiUrl = newUrl;

                LoggingService.Info($"[TabbedSettingsPage] Saved API URL: {newUrl}");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[TabbedSettingsPage] Error saving API URL: {ex.Message}");
            }
        }

        #endregion


        #region Navigation

        /// <summary>
        /// Close button click - navigate back
        /// </summary>
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
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

        #region Cleanup

        private void TabbedSettingsPage_Unloaded(object sender, RoutedEventArgs e)
        {
            try
            {
                _micLevelTimer?.Stop();
                StopMicLevelMonitoring();
                LoggingService.Info("[TabbedSettingsPage] Unloaded and timers stopped");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[TabbedSettingsPage] Error during unload: {ex.Message}");
            }
        }

        #endregion
    }
}
