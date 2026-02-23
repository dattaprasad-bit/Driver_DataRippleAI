using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DataRippleAIDesktop.Models;
using DataRippleAICode.Models;
using DataRippleAIDesktop.Services;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using System.Media;
using System.Linq;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
using System.Runtime.InteropServices;

namespace DataRippleAIDesktop.Views
{
    // COM Interop definitions for Windows audio policy configuration
    [ComImport]
    [Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    internal class PolicyConfigClient
    {
    }

    [ComImport]
    [Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPolicyConfig
    {
        void GetMixFormat(string pszDeviceName, out IntPtr ppFormat);
        void GetDeviceFormat(string pszDeviceName, int bDefault, out IntPtr ppFormat);
        void SetDeviceFormat(string pszDeviceName, IntPtr pEndpointFormat, IntPtr pMixFormat);
        void GetProcessingPeriod(string pszDeviceName, int bDefault, out long pnDefaultPeriod, out long pnMinimumPeriod);
        void SetProcessingPeriod(string pszDeviceName, IntPtr pDefaultPeriod, IntPtr pMinimumPeriod);
        void GetShareMode(string pszDeviceName, out IntPtr ppShareMode);
        void SetShareMode(string pszDeviceName, IntPtr pShareMode);
        void GetPropertyValue(string pszDeviceName, ref PropertyKey pKey, out IntPtr pv);
        void SetPropertyValue(string pszDeviceName, ref PropertyKey pKey, IntPtr pv);
        void SetDefaultEndpoint(string pszDeviceName, ERole eRole);
        void SetEndpointVisibility(string pszDeviceName, int bVisible);
    }

    internal enum ERole
    {
        eConsole = 0,
        eMultimedia = 1,
        eCommunications = 2
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey
    {
        public Guid fmtid;
        public int pid;
    }

    /// <summary>
    /// Configuration Settings UserControl
    /// </summary>
    public partial class ConfigurationSettings : UserControl
    {
        public event Action NavigateBackRequested;
        
        private string _configFilePath;
        private bool _isConfigurationChanged = false;
        
        // Audio device settings
        private string _selectedMicrophoneDevice = "";
        private string _selectedSpeakerDevice = "";
        private int _microphoneDeviceIndex = 0;
        private int _speakerDeviceIndex = 0;
        private bool _devModeSystemAudioOnly = false;

        // WebSocket and Frontend Integration settings
        private string _webSocketUrl = "";
        private bool _enableFrontendIntegration = true;

        // Backend Audio Streaming settings
        private string _backendAudioWebSocketUrl = "";
        private int _backendAudioSampleRate = 16000;
        private int _backendAudioBitDepth = 16;
        private int _backendAudioChannels = 1;
        private int _backendAudioChunkingIntervalMs = 200;
        private int _backendAudioConnectionTimeoutSeconds = 20;
        private int _backendAudioMaxReconnectionAttempts = 10;

        public ConfigurationSettings()
        {
            InitializeComponent();
            InitializeConfiguration();
            LoadAudioDevices();
            UpdateUIFromConfig();
            
            // Refresh WebSocket URL display and fields when page is loaded
            this.Loaded += (s, e) =>
            {
                UpdateWebSocketFieldsBasedOnLogin();
                UpdateCurrentWebSocketUrlDisplay();
            };
        }

        private void InitializeConfiguration()
        {
            try
            {
                _configFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");

                if (File.Exists(_configFilePath))
                {
                    var json = File.ReadAllText(_configFilePath);
                    var config = JObject.Parse(json);

                    // Load Audio Device settings
                    LoadAudioDeviceSettings(config);
                }

                LoggingService.Info("[ConfigurationSettings] Configuration initialized");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[ConfigurationSettings] Error initializing configuration: {ex.Message}");
            }
        }

        private void LoadAudioDevices()
        {
            LoadVoiceInDevices();
            LoadVoiceOutDevices();
        }

        private void LoadAudioDeviceSettings(JObject config)
        {
            try
            {
                if (config["AudioDevices"] != null)
                {
                    var audioDevices = config["AudioDevices"];
                    _selectedMicrophoneDevice = audioDevices["SelectedMicrophoneDevice"]?.ToString() ?? "";
                    _selectedSpeakerDevice = audioDevices["SelectedSpeakerDevice"]?.ToString() ?? "";
                    _microphoneDeviceIndex = audioDevices["MicrophoneDeviceIndex"]?.Value<int>() ?? 0;
                    _speakerDeviceIndex = audioDevices["SpeakerDeviceIndex"]?.Value<int>() ?? 0;
                    
                    // Load audio format diagnostic settings
                    Globals.EnableAudioFormatDiagnostics = audioDevices["EnableAudioFormatDiagnostics"]?.Value<bool>() ?? true;
                    Globals.LogAudioFormatChanges = audioDevices["LogAudioFormatChanges"]?.Value<bool>() ?? true;
                    Globals.VerboseAudioLogging = audioDevices["VerboseAudioLogging"]?.Value<bool>() ?? false;
                    
                    // Load DevMode setting
                    _devModeSystemAudioOnly = audioDevices["DevModeSystemAudioOnly"]?.Value<bool>() ?? false;
                    
                    LoggingService.Info($"[ConfigurationSettings] Loaded audio device settings - Mic: {_selectedMicrophoneDevice}, Speaker: {_selectedSpeakerDevice}");
                    LoggingService.Info($"[ConfigurationSettings] Audio diagnostics - Enable: {Globals.EnableAudioFormatDiagnostics}, LogChanges: {Globals.LogAudioFormatChanges}, Verbose: {Globals.VerboseAudioLogging}");
                    LoggingService.Info($"[ConfigurationSettings] DevMode System Audio Only: {_devModeSystemAudioOnly}");
                }
                
                // Load WebSocket settings from ClientIntegration section
                if (config["ClientIntegration"] != null)
                {
                    var clientIntegration = config["ClientIntegration"];
                    _webSocketUrl = clientIntegration["demoFrontWebSocket"]?.ToString() ?? "";
                    _enableFrontendIntegration = clientIntegration["EnableFrontendIntegration"]?.ToObject<bool>() ?? true;

                    LoggingService.Info($"[ConfigurationSettings] Loaded WebSocket URL: {_webSocketUrl}");
                    LoggingService.Info($"[ConfigurationSettings] Loaded Frontend Integration: {_enableFrontendIntegration}");
                }

                // Load Backend Audio Streaming settings
                if (config["BackendAudioStreaming"] != null)
                {
                    var backendAudio = config["BackendAudioStreaming"];
                    _backendAudioWebSocketUrl = backendAudio["WebSocketUrl"]?.ToString() ?? "";
                    _backendAudioSampleRate = int.TryParse(backendAudio["SampleRate"]?.ToString(), out var sr) ? sr : 16000;
                    _backendAudioBitDepth = int.TryParse(backendAudio["BitDepth"]?.ToString(), out var bd) ? bd : 16;
                    _backendAudioChannels = int.TryParse(backendAudio["Channels"]?.ToString(), out var ch) ? ch : 1;
                    _backendAudioChunkingIntervalMs = int.TryParse(backendAudio["ChunkingIntervalMs"]?.ToString(), out var ci) ? ci : 200;
                    _backendAudioConnectionTimeoutSeconds = int.TryParse(backendAudio["ConnectionTimeoutSeconds"]?.ToString(), out var ct) ? ct : 20;
                    _backendAudioMaxReconnectionAttempts = int.TryParse(backendAudio["MaxReconnectionAttempts"]?.ToString(), out var mr) ? mr : 10;

                    LoggingService.Info($"[ConfigurationSettings] Loaded Backend Audio Streaming - URL: {_backendAudioWebSocketUrl}, SampleRate: {_backendAudioSampleRate}, BitDepth: {_backendAudioBitDepth}, Channels: {_backendAudioChannels}");
                    LoggingService.Info($"[ConfigurationSettings] Backend Audio Streaming - ChunkingInterval: {_backendAudioChunkingIntervalMs}ms, ConnectionTimeout: {_backendAudioConnectionTimeoutSeconds}s, MaxReconnect: {_backendAudioMaxReconnectionAttempts}");
                }


            }
            catch (Exception ex)
            {
                LoggingService.Error($"[ConfigurationSettings] Error loading audio device settings: {ex.Message}");
            }
        }

        public void LoadVoiceInDevices()
        {
            try
            {
                // Clear previous items
                MicrophoneDeviceCombo.Items.Clear();

                // Use MMDeviceEnumerator to get full device names (not limited to 32 chars)
                MMDeviceEnumerator deviceEnumerator = new MMDeviceEnumerator();

                // Enumerate all the capture devices (microphones)
                foreach (var device in deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                {
                    // Add the full friendly name to the ComboBox
                    MicrophoneDeviceCombo.Items.Add(device.FriendlyName);
                }

                // If there are devices, select the appropriate one
                if (MicrophoneDeviceCombo.Items.Count > 0)
                {
                    // Try to load previously selected device from settings
                    string previousDeviceName = GetStoredMicDeviceName();
                    if (!string.IsNullOrEmpty(previousDeviceName))
                    {
                        int selectedIndex = MicrophoneDeviceCombo.Items.IndexOf(previousDeviceName);
                        if (selectedIndex >= 0)
                        {
                            MicrophoneDeviceCombo.SelectedIndex = selectedIndex;
                            LoggingService.Info($"[ConfigurationSettings] Restored microphone device: {previousDeviceName} (Index: {selectedIndex})");
                        }
                        else
                        {
                            // Device not found, select first available
                            MicrophoneDeviceCombo.SelectedIndex = 0;
                            _selectedMicrophoneDevice = MicrophoneDeviceCombo.Items[0].ToString();
                            _microphoneDeviceIndex = 0;
                            LoggingService.Warn($"[ConfigurationSettings] Previously selected microphone device '{previousDeviceName}' not found, using first available: {_selectedMicrophoneDevice}");
                        }
                    }
                    else
                    {
                        // No previous selection, use first available
                        MicrophoneDeviceCombo.SelectedIndex = 0;
                        _selectedMicrophoneDevice = MicrophoneDeviceCombo.Items[0].ToString();
                        _microphoneDeviceIndex = 0;
                        LoggingService.Info($"[ConfigurationSettings] No previous microphone selection, using first available: {_selectedMicrophoneDevice}");
                    }
                }
                else
                {
                    LoggingService.Warn("[ConfigurationSettings] No audio input devices found.");
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[ConfigurationSettings] Error loading microphone devices: {ex.Message}");
            }
        }

        public void LoadVoiceOutDevices()
        {
            try
            {
                // Clear previous items
                SpeakerDeviceCombo.Items.Clear();
                
                // Create an instance of MMDeviceEnumerator to enumerate devices
                MMDeviceEnumerator deviceEnumerator = new MMDeviceEnumerator();

                // Enumerate all the playback devices
                foreach (var device in deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    // Add the device name to the ComboBox
                    SpeakerDeviceCombo.Items.Add(device.FriendlyName);
                }

                // If there are devices, select the appropriate one
                if (SpeakerDeviceCombo.Items.Count > 0)
                {
                    // Try to load previously selected device from settings
                    string previousDeviceName = GetStoredSpeakerDeviceName();
                    if (!string.IsNullOrEmpty(previousDeviceName))
                    {
                        int selectedIndex = SpeakerDeviceCombo.Items.IndexOf(previousDeviceName);
                        if (selectedIndex >= 0)
                        {
                            SpeakerDeviceCombo.SelectedIndex = selectedIndex;
                            LoggingService.Info($"[ConfigurationSettings] Restored speaker device: {previousDeviceName} (Index: {selectedIndex})");
                        }
                        else
                        {
                            // Device not found, select first available
                            SpeakerDeviceCombo.SelectedIndex = 0;
                            _selectedSpeakerDevice = SpeakerDeviceCombo.Items[0].ToString();
                            _speakerDeviceIndex = 0;
                            LoggingService.Warn($"[ConfigurationSettings] Previously selected speaker device '{previousDeviceName}' not found, using first available: {_selectedSpeakerDevice}");
                        }
                    }
                    else
                    {
                        // No previous selection, use first available
                        SpeakerDeviceCombo.SelectedIndex = 0;
                        _selectedSpeakerDevice = SpeakerDeviceCombo.Items[0].ToString();
                        _speakerDeviceIndex = 0;
                        LoggingService.Info($"[ConfigurationSettings] No previous speaker selection, using first available: {_selectedSpeakerDevice}");
                    }
                }
                else
                {
                    LoggingService.Warn("[ConfigurationSettings] No playback devices found.");
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[ConfigurationSettings] Error loading playback devices: {ex.Message}");
            }
        }

        private void UpdateUIFromConfig()
        {
            try
            {
                // Load Call Logging setting from file
                bool enableCallLogging = true; // Default to enabled
                if (File.Exists(_configFilePath))
                {
                    try
                    {
                        var json = File.ReadAllText(_configFilePath);
                        var config = JObject.Parse(json);
                        if (config["CallLogging"]?["EnableCallLogging"] != null)
                        {
                            enableCallLogging = config["CallLogging"]["EnableCallLogging"].Value<bool>();
                        }
                    }
                    catch { /* Use default */ }
                }
                EnableCallLoggingCheckBox.IsChecked = enableCallLogging;
                
                // Load DevMode setting
                DevModeSystemAudioOnlyCheckBox.IsChecked = _devModeSystemAudioOnly;

                // Load WebSocket URL and Frontend Integration setting
                WebSocketUrlBox.Text = _webSocketUrl ?? "";
                EnableFrontendIntegrationCheckBox.IsChecked = _enableFrontendIntegration;

                // Load Backend Audio Streaming settings into UI
                BackendAudioWebSocketUrlBox.Text = _backendAudioWebSocketUrl ?? "";
                SelectComboBoxItemByTag(BackendAudioSampleRateCombo, _backendAudioSampleRate.ToString());
                SelectComboBoxItemByTag(BackendAudioBitDepthCombo, _backendAudioBitDepth.ToString());
                SelectComboBoxItemByTag(BackendAudioChannelsCombo, _backendAudioChannels.ToString());
                BackendAudioChunkingIntervalBox.Text = _backendAudioChunkingIntervalMs.ToString();
                BackendAudioConnectionTimeoutBox.Text = _backendAudioConnectionTimeoutSeconds.ToString();
                BackendAudioMaxReconnectionBox.Text = _backendAudioMaxReconnectionAttempts.ToString();

                // Check login status and disable fields accordingly
                UpdateWebSocketFieldsBasedOnLogin();
                
                // Update Current Active WebSocket URL from Globals (set during login)
                UpdateCurrentWebSocketUrlDisplay();
                
                UpdateStatus("Configuration loaded successfully");
                
                LoggingService.Info("[ConfigurationSettings] UI updated from configuration");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[ConfigurationSettings] Error updating UI from config: {ex.Message}");
                UpdateStatus($"Error loading configuration: {ex.Message}");
            }
        }

        private void UpdateStatus(string message)
        {
            StatusText.Text = message;
            LoggingService.Info($"[ConfigurationSettings] {message}");
        }

        private string GetStoredMicDeviceName()
        {
            return _selectedMicrophoneDevice;
        }

        private string GetStoredSpeakerDeviceName()
        {
            return _selectedSpeakerDevice;
        }

        private void SaveConfigurationToFile()
        {
            try
            {
                // Validate backend audio streaming settings before saving
                if (!ValidateBackendAudioSettings(out string validationError))
                {
                    LoggingService.Warn($"[ConfigurationSettings] Validation failed: {validationError}");
                    UpdateStatus($"Validation error: {validationError}");
                    ShowToast(validationError, isError: true);
                    return;
                }

                // Read existing configuration to preserve all settings not managed by this UI
                JObject fullConfig;
                if (File.Exists(_configFilePath))
                {
                    var existingJson = File.ReadAllText(_configFilePath);
                    fullConfig = JObject.Parse(existingJson);
                }
                else
                {
                    // Create basic structure if file doesn't exist
                    fullConfig = new JObject
                    {
                        ["Logging"] = new JObject
                        {
                            ["LogLevel"] = new JObject
                            {
                                ["Default"] = "Information",
                                ["Microsoft.AspNetCore"] = "Warning"
                            }
                        },
                        ["AllowedHosts"] = "*",
                        ["Backend"] = new JObject
                        {
                            ["BaseUrl"] = "http://localhost:5000/api/"
                        }
                    };
                }

                // Update only the sections managed by this UI

                // Update AudioDevices section
                if (fullConfig["AudioDevices"] == null)
                    fullConfig["AudioDevices"] = new JObject();
                
                var audioDevicesSection = (JObject)fullConfig["AudioDevices"];
                audioDevicesSection["SelectedMicrophoneDevice"] = _selectedMicrophoneDevice;
                audioDevicesSection["SelectedSpeakerDevice"] = _selectedSpeakerDevice;
                audioDevicesSection["MicrophoneDeviceIndex"] = _microphoneDeviceIndex;
                audioDevicesSection["SpeakerDeviceIndex"] = _speakerDeviceIndex;
                audioDevicesSection["DevModeSystemAudioOnly"] = _devModeSystemAudioOnly;

                // Update ClientIntegration section (WebSocket URL and Frontend Integration)
                if (fullConfig["ClientIntegration"] == null)
                    fullConfig["ClientIntegration"] = new JObject();
                
                var clientIntegrationSection = (JObject)fullConfig["ClientIntegration"];
                clientIntegrationSection["demoFrontWebSocket"] = _webSocketUrl;
                clientIntegrationSection["EnableFrontendIntegration"] = EnableFrontendIntegrationCheckBox.IsChecked ?? true;

                // Update CallLogging section
                if (fullConfig["CallLogging"] == null)
                    fullConfig["CallLogging"] = new JObject();
                
                var callLoggingSection = (JObject)fullConfig["CallLogging"];
                callLoggingSection["EnableCallLogging"] = EnableCallLoggingCheckBox.IsChecked ?? true;

                // Update BackendAudioStreaming section
                if (fullConfig["BackendAudioStreaming"] == null)
                    fullConfig["BackendAudioStreaming"] = new JObject();

                var backendAudioSection = (JObject)fullConfig["BackendAudioStreaming"];
                backendAudioSection["WebSocketUrl"] = _backendAudioWebSocketUrl;
                backendAudioSection["SampleRate"] = _backendAudioSampleRate;
                backendAudioSection["BitDepth"] = _backendAudioBitDepth;
                backendAudioSection["Channels"] = _backendAudioChannels;
                backendAudioSection["ChunkingIntervalMs"] = _backendAudioChunkingIntervalMs;
                backendAudioSection["ConnectionTimeoutSeconds"] = _backendAudioConnectionTimeoutSeconds;
                backendAudioSection["MaxReconnectionAttempts"] = _backendAudioMaxReconnectionAttempts;
                LoggingService.Info($"[ConfigurationSettings] Saving Backend Audio Streaming - URL: {_backendAudioWebSocketUrl}, SampleRate: {_backendAudioSampleRate}, BitDepth: {_backendAudioBitDepth}, Channels: {_backendAudioChannels}");

                // Save the updated configuration (preserving all other sections like Auth0, ClientIntegration, etc.)
                var json = fullConfig.ToString(Formatting.Indented);
                File.WriteAllText(_configFilePath, json);

                _isConfigurationChanged = false;
                
                // Set Windows default audio devices
                SetWindowsDefaultAudioDevices();
                
                UpdateStatus("Configuration saved successfully");
                
                // Show toast notification
                ShowToast("Settings saved successfully!");
                
                LoggingService.Info("[ConfigurationSettings] Configuration saved to file");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[ConfigurationSettings] Error saving configuration: {ex.Message}");
                UpdateStatus($"Error saving configuration: {ex.Message}");
                ShowToast($"Error: {ex.Message}", isError: true);
            }
        }

        /// <summary>
        /// Show toast notification with animation
        /// </summary>
        private void ShowToast(string message, bool isError = false)
        {
            try
            {
                Dispatcher.Invoke(() =>
                {
                    // Update message
                    ToastMessage.Text = message;
                    
                    // Update icon and color based on error state
                    if (isError)
                    {
                        var stackPanel = ToastNotification.Child as StackPanel;
                        if (stackPanel != null && stackPanel.Children.Count > 0)
                        {
                            var iconTextBlock = stackPanel.Children[0] as TextBlock;
                            if (iconTextBlock != null)
                            {
                                iconTextBlock.Text = "❌";
                            }
                        }
                        ToastNotification.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF6B6B"));
                    }
                    else
                    {
                        var stackPanel = ToastNotification.Child as StackPanel;
                        if (stackPanel != null && stackPanel.Children.Count > 0)
                        {
                            var iconTextBlock = stackPanel.Children[0] as TextBlock;
                            if (iconTextBlock != null)
                            {
                                iconTextBlock.Text = "✅";
                            }
                        }
                        ToastNotification.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1DB8B8"));
                    }
                    
                    // Show toast with fade-in animation
                    ToastNotification.Visibility = Visibility.Visible;
                    
                    var fadeIn = new DoubleAnimation
                    {
                        From = 0,
                        To = 1,
                        Duration = TimeSpan.FromMilliseconds(300)
                    };
                    
                    ToastNotification.BeginAnimation(UIElement.OpacityProperty, fadeIn);
                    
                    // Auto-hide after 3 seconds with fade-out
                    var timer = new DispatcherTimer
                    {
                        Interval = TimeSpan.FromSeconds(3)
                    };
                    
                    timer.Tick += (s, args) =>
                    {
                        timer.Stop();
                        
                        var fadeOut = new DoubleAnimation
                        {
                            From = 1,
                            To = 0,
                            Duration = TimeSpan.FromMilliseconds(300)
                        };
                        
                        fadeOut.Completed += (sender, e) =>
                        {
                            ToastNotification.Visibility = Visibility.Collapsed;
                        };
                        
                        ToastNotification.BeginAnimation(UIElement.OpacityProperty, fadeOut);
                    };
                    
                    timer.Start();
                });
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[ConfigurationSettings] Error showing toast: {ex.Message}");
            }
        }

        // Event Handlers
        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isConfigurationChanged)
            {
                var result = MessageBox.Show("You have unsaved changes. Do you want to save before leaving?", 
                    "Unsaved Changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                
                if (result == MessageBoxResult.Yes)
                {
                    SaveConfigurationToFile();
                }
                else if (result == MessageBoxResult.Cancel)
                {
                    return;
                }
            }
            
            NavigateBackRequested?.Invoke();
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            SaveConfigurationToFile();
        }

        private void MicrophoneDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (MicrophoneDeviceCombo.SelectedItem != null)
            {
                var selectedDevice = MicrophoneDeviceCombo.SelectedItem.ToString();
                var deviceIndex = MicrophoneDeviceCombo.SelectedIndex;
                
                // Store the selected device
                _selectedMicrophoneDevice = selectedDevice;
                _microphoneDeviceIndex = deviceIndex;
                
                LoggingService.Info($"[ConfigurationSettings] Microphone device selected: {selectedDevice} (Index: {deviceIndex})");
                
                // Update device information when selection changes
                _isConfigurationChanged = true;
            }
        }

        private void SpeakerDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SpeakerDeviceCombo.SelectedItem != null)
            {
                var selectedDevice = SpeakerDeviceCombo.SelectedItem.ToString();
                var deviceIndex = SpeakerDeviceCombo.SelectedIndex;
                
                // Store the selected device
                _selectedSpeakerDevice = selectedDevice;
                _speakerDeviceIndex = deviceIndex;
                
                LoggingService.Info($"[ConfigurationSettings] Speaker device selected: {selectedDevice} (Index: {deviceIndex})");
                
                // Update device information when selection changes
                _isConfigurationChanged = true;
            }
        }

        private void WebSocketUrl_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (WebSocketUrlBox != null)
            {
                _webSocketUrl = WebSocketUrlBox.Text;
                _isConfigurationChanged = true;
                
                LoggingService.Info($"[ConfigurationSettings] WebSocket URL changed: {_webSocketUrl}");
            }
        }

        private async void TestMicrophone_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                TestMicrophoneButton.IsEnabled = false;
                MicrophoneTestStatus.Text = "Testing...";
                MicrophoneTestStatus.Foreground = new SolidColorBrush(Colors.Orange);
                
                var deviceIndex = MicrophoneDeviceCombo.SelectedIndex;
                if (deviceIndex < 0 || deviceIndex >= WaveInEvent.DeviceCount)
                {
                    throw new InvalidOperationException("Invalid microphone device selected");
                }
                
                // Test microphone by creating a WaveIn instance
                using (var waveIn = new WaveInEvent())
                {
                    waveIn.DeviceNumber = deviceIndex;
                    waveIn.WaveFormat = new WaveFormat(44100, 1); // 44.1kHz, mono
                    
                    // Test if we can initialize the device
                    waveIn.StartRecording();
                    await Task.Delay(1000); // Record for 1 second
                    waveIn.StopRecording();
                }
                
                MicrophoneTestStatus.Text = "✓ Microphone working";
                MicrophoneTestStatus.Foreground = new SolidColorBrush(Colors.Green);
                LoggingService.Info($"[ConfigurationSettings] Microphone test completed successfully for device {deviceIndex}");
            }
            catch (Exception ex)
            {
                MicrophoneTestStatus.Text = "✗ Test failed";
                MicrophoneTestStatus.Foreground = new SolidColorBrush(Colors.Red);
                LoggingService.Error($"[ConfigurationSettings] Microphone test failed: {ex.Message}");
            }
            finally
            {
                TestMicrophoneButton.IsEnabled = true;
            }
        }

        private async void TestSpeaker_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                TestSpeakerButton.IsEnabled = false;
                SpeakerTestStatus.Text = "Testing...";
                SpeakerTestStatus.Foreground = new SolidColorBrush(Colors.Orange);
                
                var deviceIndex = SpeakerDeviceCombo.SelectedIndex;
                if (deviceIndex < 0 || SpeakerDeviceCombo.Items.Count == 0)
                {
                    throw new InvalidOperationException("Invalid speaker device selected");
                }
                
                // Test speaker by playing a test tone through the selected device
                using (var audioOut = new WaveOut())
                {
                    // Create a test tone (440Hz sine wave for 1 second)
                    var testTone = GenerateTestTone(440, 1000, 44100);
                    using (var waveProvider = new TestToneProvider(testTone))
                    {
                        audioOut.Init(waveProvider);
                        audioOut.Play();
                        
                        // Wait for the tone to finish playing
                        await Task.Delay(1200); // Slightly longer than the tone duration
                        
                        audioOut.Stop();
                    }
                }
                
                SpeakerTestStatus.Text = "✓ Speaker working";
                SpeakerTestStatus.Foreground = new SolidColorBrush(Colors.Green);
                LoggingService.Info($"[ConfigurationSettings] Speaker test completed successfully for device {deviceIndex}");
            }
            catch (Exception ex)
            {
                SpeakerTestStatus.Text = "✗ Test failed";
                SpeakerTestStatus.Foreground = new SolidColorBrush(Colors.Red);
                LoggingService.Error($"[ConfigurationSettings] Speaker test failed: {ex.Message}");
            }
            finally
            {
                TestSpeakerButton.IsEnabled = true;
            }
        }

        private async void DiagnoseSpeakerIssues_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                DiagnoseSpeakerButton.IsEnabled = false;
                AudioTestResultsText.Text = "🔍 Diagnosing speaker audio issues...\n\n";
                
                var results = new StringBuilder();
                results.AppendLine("=== SPEAKER AUDIO DIAGNOSTIC ===");
                results.AppendLine($"Diagnostic started at: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                results.AppendLine();
                
                // Get UI state on the main thread first
                var selectedItem = SpeakerDeviceCombo.SelectedItem?.ToString();
                var selectedIndex = SpeakerDeviceCombo.SelectedIndex;
                var itemsCount = SpeakerDeviceCombo.Items.Count;
                
                results.AppendLine("🔍 UI State Debugging:");
                results.AppendLine($"   SpeakerDeviceCombo.SelectedItem: {selectedItem ?? "null"}");
                results.AppendLine($"   SpeakerDeviceCombo.SelectedIndex: {selectedIndex}");
                results.AppendLine($"   SpeakerDeviceCombo.Items.Count: {itemsCount}");
                results.AppendLine();
                
                // Early exit if no device selected
                if (selectedIndex < 0 || string.IsNullOrEmpty(selectedItem))
                {
                    results.AppendLine("❌ No speaker device currently selected");
                    results.AppendLine("💡 Please select a speaker device from the dropdown above first.");
                    AudioTestResultsText.Text = results.ToString();
                    return;
                }
                
                // Enumerate devices and extract all info on UI thread (COM thread affinity)
                // IMPORTANT: Use DeviceState.Active to match what the ComboBox shows!
                MMDeviceEnumerator diagDeviceEnumerator = new MMDeviceEnumerator();
                var diagAllDevices = diagDeviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
                
                // Get device and extract ALL properties on UI thread
                MMDevice selectedDevice = null;
                string deviceName = null;
                string deviceState = null;
                string deviceId = null;
                WaveFormat mixFormat = null;
                bool hasAudioClient = false;
                
                if (selectedIndex >= 0 && selectedIndex < diagAllDevices.Count)
                {
                    selectedDevice = diagAllDevices[selectedIndex];
                    
                    // Extract ALL device info on UI thread (COM objects can't cross threads!)
                    try
                    {
                        try
                        {
                            deviceName = selectedDevice.FriendlyName;
                            LoggingService.Info($"[ConfigurationSettings] ✅ Got FriendlyName: '{deviceName}'");
                        }
                        catch (Exception ex)
                        {
                            LoggingService.Error($"[ConfigurationSettings] Failed to get FriendlyName: {ex.Message} (HRESULT: {ex.HResult:X8})");
                            deviceName = "Unknown Device";
                        }
                        
                        try
                        {
                            deviceState = selectedDevice.State.ToString();
                            LoggingService.Info($"[ConfigurationSettings] ✅ Got State: '{deviceState}'");
                        }
                        catch (Exception ex)
                        {
                            LoggingService.Error($"[ConfigurationSettings] Failed to get State: {ex.Message} (HRESULT: {ex.HResult:X8})");
                            deviceState = "Unknown";
                        }
                        
                        try
                        {
                            deviceId = selectedDevice.ID;
                            LoggingService.Info($"[ConfigurationSettings] ✅ Got ID: '{deviceId}'");
                        }
                        catch (Exception ex)
                        {
                            LoggingService.Error($"[ConfigurationSettings] Failed to get ID: {ex.Message} (HRESULT: {ex.HResult:X8})");
                            deviceId = "Unknown";
                        }
                        
                        // Test AudioClient availability
                        try
                        {
                            var audioClient = selectedDevice.AudioClient;
                            hasAudioClient = (audioClient != null);
                            LoggingService.Info($"[ConfigurationSettings] ✅ AudioClient available: {hasAudioClient}");
                            
                            if (hasAudioClient)
                            {
                                try
                                {
                                    mixFormat = audioClient.MixFormat;
                                    LoggingService.Info($"[ConfigurationSettings] ✅ Got MixFormat: {mixFormat?.SampleRate}Hz");
                                }
                                catch (Exception ex)
                                {
                                    LoggingService.Error($"[ConfigurationSettings] Failed to get MixFormat: {ex.Message} (HRESULT: {ex.HResult:X8})");
                                }
                            }
                        }
                        catch (Exception audioEx)
                        {
                            LoggingService.Warn($"[ConfigurationSettings] AudioClient access failed: {audioEx.Message} (HRESULT: {audioEx.HResult:X8})");
                        }
                        
                        LoggingService.Info($"[ConfigurationSettings] ✅ Device info extracted: '{deviceName}'");
                    }
                    catch (Exception extractEx)
                    {
                        LoggingService.Error($"[ConfigurationSettings] Failed to extract device info: {extractEx.Message} (HRESULT: {extractEx.HResult:X8})");
                        results.AppendLine($"❌ Failed to access device properties: {extractEx.Message}");
                        results.AppendLine($"   HRESULT: {extractEx.HResult:X8}");
                        results.AppendLine();
                        results.AppendLine("💡 This device may be in an invalid state or disconnected.");
                        results.AppendLine("   Try selecting a different device or restarting the application.");
                        AudioTestResultsText.Text = results.ToString();
                        return;
                    }
                }
                else
                {
                    results.AppendLine($"❌ Invalid device index {selectedIndex} (available: {diagAllDevices.Count})");
                    AudioTestResultsText.Text = results.ToString();
                    return;
                }
                
                // Now display device information
                results.AppendLine("1. Selected Speaker Device Information:");
                results.AppendLine($"   Name: {deviceName}");
                results.AppendLine($"   State: {deviceState}");
                results.AppendLine($"   ID: {deviceId}");
                results.AppendLine();
                
                // Test basic device properties
                results.AppendLine("2. Device Capability Tests:");
                
                if (hasAudioClient)
                {
                    results.AppendLine("   ✅ AudioClient: Available");
                    
                    if (mixFormat != null)
                    {
                        results.AppendLine($"   ✅ MixFormat: {mixFormat.SampleRate}Hz, {mixFormat.BitsPerSample}-bit, {mixFormat.Channels} channels");
                    }
                    else
                    {
                        results.AppendLine("   ❌ MixFormat: Not available");
                    }
                }
                else
                {
                    results.AppendLine("   ❌ AudioClient: Not available");
                }
                results.AppendLine();
                
                // Test 3: WasapiLoopbackCapture creation and audio activity
                // IMPORTANT: Must be done on UI thread due to COM thread affinity!
                results.AppendLine("3. Loopback Capture Test:");
                
                try
                {
                    using (var testCapture = new WasapiLoopbackCapture(selectedDevice))
                    {
                        results.AppendLine("   ✅ WasapiLoopbackCapture: Created successfully");
                        results.AppendLine($"   ✅ Capture Format: {testCapture.WaveFormat.SampleRate}Hz, {testCapture.WaveFormat.BitsPerSample}-bit, {testCapture.WaveFormat.Channels} channels");
                        
                        // Test recording with a brief delay to allow audio detection
                        bool audioDetected = false;
                        int samplesCollected = 0;
                        
                        testCapture.DataAvailable += (s, e) =>
                        {
                            samplesCollected += e.BytesRecorded;
                            if (e.BytesRecorded > 0)
                            {
                                audioDetected = true;
                            }
                        };
                        
                        testCapture.StartRecording();
                        results.AppendLine("   ✅ Recording Start: Successful");
                        
                        // Wait for audio data (use a Task.Delay to not block UI thread)
                        await Task.Delay(2000);
                        
                        testCapture.StopRecording();
                        results.AppendLine("   ✅ Recording Stop: Successful");
                        results.AppendLine();
                        
                        // Audio activity results
                        results.AppendLine("4. Audio Activity Check:");
                        if (audioDetected)
                        {
                            results.AppendLine("   ✅ Audio Activity: Detected");
                            results.AppendLine($"   📊 Samples Collected: {samplesCollected} bytes");
                            results.AppendLine("   💡 Audio is currently playing through this device");
                            results.AppendLine();
                            results.AppendLine("   🎉 RESULT: Your speaker device is fully compatible!");
                            results.AppendLine("   ✅ Speaker audio recording is working correctly.");
                        }
                        else
                        {
                            results.AppendLine("   ⚠️ Audio Activity: No audio detected");
                            results.AppendLine("   💡 The device works, but no audio is currently playing");
                            results.AppendLine();
                            results.AppendLine("   🎉 RESULT: Device is compatible!");
                            results.AppendLine("   💡 If you're still having issues:");
                            results.AppendLine("      - Play audio through this device and test again");
                            results.AppendLine("      - Check audio is not muted");
                            results.AppendLine("      - Verify this is the active playback device in Windows");
                        }
                    }
                }
                catch (Exception ex)
                {
                    results.AppendLine($"   ❌ WasapiLoopbackCapture: Failed - {ex.Message}");
                    results.AppendLine($"   Exception Type: {ex.GetType().Name}");
                    results.AppendLine($"   HRESULT: {ex.HResult:X8}");
                    results.AppendLine();
                    
                    // Provide detailed guidance
                    if (ex.HResult == unchecked((int)0xE000020B) || ex.Message.Contains("0xE000020B"))
                    {
                        results.AppendLine("   💡 ERROR CODE 0xE000020B ANALYSIS:");
                        results.AppendLine("   This error typically means:");
                        results.AppendLine("      1. Device is in exclusive mode (another app is using it)");
                        results.AppendLine("      2. No audio stream is currently active on this device");
                        results.AppendLine("      3. Device doesn't support loopback capture");
                        results.AppendLine();
                        results.AppendLine("   🔧 SOLUTIONS TO TRY:");
                        results.AppendLine("      1. ▶️ START PLAYING AUDIO through this device first (YouTube, Spotify, etc.)");
                        results.AppendLine("      2. Close other audio applications that might have exclusive access");
                        results.AppendLine("      3. Check Windows Sound Settings:");
                        results.AppendLine("         - Right-click speaker icon → Sounds → Playback tab");
                        results.AppendLine("         - Double-click your device → Advanced tab");
                        results.AppendLine("         - UNCHECK 'Allow applications to take exclusive control'");
                        results.AppendLine("      4. Try a different speaker device (USB devices often don't support loopback)");
                        results.AppendLine("      5. Update audio drivers");
                        results.AppendLine();
                        results.AppendLine("   ⚠️ NOTE: Some devices (USB, Bluetooth, HDMI) may not support loopback capture at all.");
                    }
                    else if (ex.HResult == unchecked((int)0x80004002))
                    {
                        results.AppendLine("   💡 ERROR CODE 0x80004002 (E_NOINTERFACE) ANALYSIS:");
                        results.AppendLine("   This error means COM interface not supported.");
                        results.AppendLine("   This can happen with certain device types or driver issues.");
                        results.AppendLine();
                        results.AppendLine("   🔧 SOLUTIONS TO TRY:");
                        results.AppendLine("      1. Update audio drivers");
                        results.AppendLine("      2. Try a different audio device");
                        results.AppendLine("      3. Check if device supports loopback (onboard audio usually does)");
                    }
                    else
                    {
                        AppendErrorGuidance(ex, results);
                    }
                }
                
                results.AppendLine();
                results.AppendLine($"Diagnostic completed at: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                
                // Update UI
                AudioTestResultsText.Text = results.ToString();
                AudioTestResults.ScrollToEnd();
            }
            catch (Exception ex)
            {
                AudioTestResultsText.Text = $"❌ Diagnostic failed: {ex.Message}\n\nStack Trace:\n{ex.StackTrace}";
                LoggingService.Error($"[ConfigurationSettings] Speaker diagnostic failed: {ex.Message}");
            }
            finally
            {
                DiagnoseSpeakerButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// Append appropriate error guidance based on exception type
        /// </summary>
        private void AppendErrorGuidance(Exception ex, StringBuilder results)
        {
            if (ex.Message.Contains("exclusive"))
            {
                results.AppendLine("   💡 SOLUTION: Close other audio applications (Spotify, YouTube, etc.)");
            }
            else if (ex.Message.Contains("access") || ex.HResult == unchecked((int)0x80070005))
            {
                results.AppendLine("   💡 SOLUTION: Check Windows privacy settings for microphone access");
            }
            else if (ex.Message.Contains("format"))
            {
                results.AppendLine("   💡 SOLUTION: This device doesn't support loopback capture");
            }
            else if (ex.Message.Contains("driver"))
            {
                results.AppendLine("   💡 SOLUTION: Update audio drivers or try a different device");
            }
            else if (ex.HResult == unchecked((int)0x80070490))
            {
                results.AppendLine("   💡 SOLUTION: Device not found - may have been disconnected");
            }
        }

        /// <summary>
        /// Copy diagnostic results to clipboard
        /// </summary>
        private void CopyResults_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(AudioTestResultsText.Text))
                {
                    Clipboard.SetText(AudioTestResultsText.Text);
                    
                    // Show a brief confirmation
                    var originalContent = CopyResultsButton.Content;
                    CopyResultsButton.Content = "✅ Copied!";
                    CopyResultsButton.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#28A745"));
                    
                    // Reset button after 2 seconds
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                    timer.Tick += (s, args) =>
                    {
                        CopyResultsButton.Content = originalContent;
                        CopyResultsButton.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#17A2B8"));
                        timer.Stop();
                    };
                    timer.Start();
                    
                    LoggingService.Info("[ConfigurationSettings] Diagnostic results copied to clipboard");
                }
                else
                {
                    MessageBox.Show("No results to copy. Please run a diagnostic test first.", 
                                    "No Results", 
                                    MessageBoxButton.OK, 
                                    MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[ConfigurationSettings] Failed to copy results: {ex.Message}");
                MessageBox.Show($"Failed to copy results: {ex.Message}", 
                                "Copy Failed", 
                                MessageBoxButton.OK, 
                                MessageBoxImage.Error);
            }
        }

        private void UpdateCurrentWebSocketUrlDisplay()
        {
            try
            {
                string currentUrl = Globals.FrontendSocketUrl;
                
                if (!string.IsNullOrWhiteSpace(currentUrl))
                {
                    CurrentWebSocketUrlLabel.Text = currentUrl;
                    CurrentWebSocketUrlLabel.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#28A745"));
                    CopyWebSocketUrlButton.Visibility = Visibility.Visible;
                    LoggingService.Info($"[ConfigurationSettings] Current WebSocket URL displayed: {currentUrl}");
                }
                else
                {
                    CurrentWebSocketUrlLabel.Text = "Not connected (no active WebSocket URL from login)";
                    CurrentWebSocketUrlLabel.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6C757D"));
                    CopyWebSocketUrlButton.Visibility = Visibility.Collapsed;
                    LoggingService.Info("[ConfigurationSettings] No active WebSocket URL to display");
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[ConfigurationSettings] Error updating current WebSocket URL display: {ex.Message}");
                CurrentWebSocketUrlLabel.Text = "Error loading WebSocket URL";
                CurrentWebSocketUrlLabel.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#DC3545"));
                CopyWebSocketUrlButton.Visibility = Visibility.Collapsed;
            }
        }

        private void CopyWebSocketUrl_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string urlToCopy = Globals.FrontendSocketUrl;
                
                if (!string.IsNullOrWhiteSpace(urlToCopy))
                {
                    Clipboard.SetText(urlToCopy);
                    
                    // Show a brief confirmation
                    var originalContent = CopyWebSocketUrlButton.Content;
                    var originalBackground = CopyWebSocketUrlButton.Background;
                    CopyWebSocketUrlButton.Content = "✅ Copied!";
                    CopyWebSocketUrlButton.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#28A745"));
                    
                    // Reset button after 2 seconds
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                    timer.Tick += (s, args) =>
                    {
                        CopyWebSocketUrlButton.Content = originalContent;
                        CopyWebSocketUrlButton.Background = originalBackground;
                        timer.Stop();
                    };
                    timer.Start();
                    
                    LoggingService.Info("[ConfigurationSettings] WebSocket URL copied to clipboard");
                }
                else
                {
                    MessageBox.Show("No WebSocket URL available to copy. Please login first to establish a connection.", 
                                    "No URL Available", 
                                    MessageBoxButton.OK, 
                                    MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[ConfigurationSettings] Failed to copy WebSocket URL: {ex.Message}");
                MessageBox.Show($"Failed to copy WebSocket URL: {ex.Message}", 
                                "Copy Failed", 
                                MessageBoxButton.OK, 
                                MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Checks if current login is skip login (temporary/test login)
        /// </summary>
        private bool IsSkipLogin()
        {
            try
            {
                var userDetails = SecureTokenStorage.RetrieveUserDetails();
                if (userDetails == null) return false;

                string accessToken = userDetails?.AccessToken?.ToString() ?? string.Empty;
                string userName = userDetails?.Name?.ToString() ?? string.Empty;

                // Skip login uses temp-access-token prefix and "Test User (Temp)" name
                bool isSkipLogin = accessToken.StartsWith("temp-access-token-", StringComparison.OrdinalIgnoreCase) ||
                                   userName.Contains("Test User (Temp)", StringComparison.OrdinalIgnoreCase);

                if (isSkipLogin)
                {
                    LoggingService.Info("[ConfigurationSettings] Detected skip login - temporary/test session");
                }

                return isSkipLogin;
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[ConfigurationSettings] Error checking skip login status: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Checks if current login is proper FrontEnd Auth login (real JWT token)
        /// </summary>
        private bool IsProperFrontEndAuthLogin()
        {
            try
            {
                var userDetails = SecureTokenStorage.RetrieveUserDetails();
                if (userDetails == null) return false;

                string accessToken = userDetails?.AccessToken?.ToString() ?? string.Empty;

                // Skip login check first
                if (IsSkipLogin()) return false;

                // Proper FrontEnd Auth login should have a valid JWT token (not temp token)
                // Check if token looks like a JWT (has 3 parts separated by dots)
                if (string.IsNullOrWhiteSpace(accessToken)) return false;

                var tokenParts = accessToken.Split('.');
                bool isJwtToken = tokenParts.Length == 3;

                if (isJwtToken)
                {
                    LoggingService.Info("[ConfigurationSettings] Detected proper FrontEnd Auth login - real JWT token");
                }

                return isJwtToken;
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[ConfigurationSettings] Error checking FrontEnd Auth login status: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Updates WebSocket fields based on login type
        /// - Skip login: Disable/hide Current Active WebSocket URL section
        /// - Proper FrontEnd Auth: Disable WebSocket URL input and Test button
        /// </summary>
        private void UpdateWebSocketFieldsBasedOnLogin()
        {
            try
            {
                bool isSkipLogin = IsSkipLogin();
                bool isProperAuth = IsProperFrontEndAuthLogin();

                // If skip login: Hide/disable Current Active WebSocket URL section
                if (isSkipLogin)
                {
                    // Hide the entire Current Active WebSocket URL section
                    if (CurrentWebSocketUrlSeparator != null)
                    {
                        CurrentWebSocketUrlSeparator.Visibility = Visibility.Collapsed;
                    }
                    if (CurrentWebSocketUrlTitle != null)
                    {
                        CurrentWebSocketUrlTitle.Visibility = Visibility.Collapsed;
                    }
                    if (CurrentWebSocketUrlDescription != null)
                    {
                        CurrentWebSocketUrlDescription.Visibility = Visibility.Collapsed;
                    }
                    if (CurrentWebSocketUrlBorder != null)
                    {
                        CurrentWebSocketUrlBorder.Visibility = Visibility.Collapsed;
                    }
                    if (CurrentWebSocketUrlLabel != null)
                    {
                        CurrentWebSocketUrlLabel.Visibility = Visibility.Collapsed;
                    }
                    if (CopyWebSocketUrlButton != null)
                    {
                        CopyWebSocketUrlButton.Visibility = Visibility.Collapsed;
                    }
                    LoggingService.Info("[ConfigurationSettings] Current Active WebSocket URL section hidden (skip login)");
                }
                else
                {
                    // Show Current Active WebSocket URL section for proper login
                    if (CurrentWebSocketUrlSeparator != null)
                    {
                        CurrentWebSocketUrlSeparator.Visibility = Visibility.Visible;
                    }
                    if (CurrentWebSocketUrlTitle != null)
                    {
                        CurrentWebSocketUrlTitle.Visibility = Visibility.Visible;
                    }
                    if (CurrentWebSocketUrlDescription != null)
                    {
                        CurrentWebSocketUrlDescription.Visibility = Visibility.Visible;
                    }
                    if (CurrentWebSocketUrlBorder != null)
                    {
                        CurrentWebSocketUrlBorder.Visibility = Visibility.Visible;
                    }
                    if (CurrentWebSocketUrlLabel != null)
                    {
                        CurrentWebSocketUrlLabel.Visibility = Visibility.Visible;
                    }
                }

                // If proper FrontEnd Auth login: Disable WebSocket URL input and Test button
                if (isProperAuth)
                {
                    if (WebSocketUrlBox != null)
                    {
                        WebSocketUrlBox.IsEnabled = false;
                        WebSocketUrlBox.ToolTip = "WebSocket URL is managed by FrontEnd Auth login and cannot be changed";
                        LoggingService.Info("[ConfigurationSettings] WebSocket URL input disabled (FrontEnd Auth login)");
                    }
                    if (TestWebSocketButton != null)
                    {
                        TestWebSocketButton.IsEnabled = false;
                        TestWebSocketButton.ToolTip = "WebSocket testing is disabled when logged in via FrontEnd Auth";
                        LoggingService.Info("[ConfigurationSettings] Test WebSocket button disabled (FrontEnd Auth login)");
                    }
                }
                else
                {
                    // Enable fields for skip login or no login
                    if (WebSocketUrlBox != null)
                    {
                        WebSocketUrlBox.IsEnabled = true;
                        WebSocketUrlBox.ToolTip = "WebSocket URL for frontend integration (e.g., ws://localhost:3005/ws/)";
                    }
                    if (TestWebSocketButton != null)
                    {
                        TestWebSocketButton.IsEnabled = true;
                        TestWebSocketButton.ToolTip = "Test Frontend WebSocket connection";
                    }
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[ConfigurationSettings] Error updating WebSocket fields based on login: {ex.Message}");
            }
        }

        private async void ComprehensiveAudioTest_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ComprehensiveAudioTestButton.IsEnabled = false;
                AudioTestResultsText.Text = "🔍 Starting comprehensive audio device test...\n\n";
                
                // Run the test in a background task to avoid blocking UI
                await Task.Run(() =>
                {
                    var results = new StringBuilder();
                    results.AppendLine("=== COMPREHENSIVE AUDIO DEVICE TEST ===");
                    results.AppendLine($"Test started at: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    results.AppendLine();
                    
                    try
                    {
                        // Test Windows Audio Service (using alternative method)
                        results.AppendLine("1. Checking Windows Audio Service...");
                        try
                        {
                            // Try to enumerate audio devices as a way to test if audio service is working
                            MMDeviceEnumerator testEnumerator = new MMDeviceEnumerator();
                            var testDevices = testEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.All).ToList();
                            results.AppendLine($"   ✅ Windows Audio Service appears to be running (found {testDevices.Count} devices)");
                        }
                        catch (Exception ex)
                        {
                            results.AppendLine($"   ⚠️ Windows Audio Service may not be running: {ex.Message}");
                        }
                        results.AppendLine();
                        
                        // Test device enumeration
                        results.AppendLine("2. Enumerating audio devices...");
                        try
                        {
                            MMDeviceEnumerator deviceEnumerator = new MMDeviceEnumerator();
                            
                            var activeDevices = deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
                            var disabledDevices = deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Disabled).ToList();
                            var unpluggedDevices = deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Unplugged).ToList();
                            var notPresentDevices = deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.NotPresent).ToList();
                            
                            results.AppendLine($"   📊 Device Summary:");
                            results.AppendLine($"      Active devices: {activeDevices.Count}");
                            results.AppendLine($"      Disabled devices: {disabledDevices.Count}");
                            results.AppendLine($"      Unplugged devices: {unpluggedDevices.Count}");
                            results.AppendLine($"      Not present devices: {notPresentDevices.Count}");
                            results.AppendLine();
                            
                            // Test each device for loopback capability
                            results.AppendLine("3. Testing devices for loopback capture compatibility...");
                            var allDevices = deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.All).ToList();
                            int compatibleDevices = 0;
                            int incompatibleDevices = 0;
                            
                            // Test currently selected speaker device first
                            results.AppendLine("4. Testing currently selected speaker device...");
                            try
                            {
                                var selectedSpeakerDevice = GetSelectedSpeakerDeviceObject();
                                if (selectedSpeakerDevice != null)
                                {
                                    results.AppendLine($"   --- Testing Selected Speaker Device ---");
                                    results.AppendLine($"   Name: {selectedSpeakerDevice.FriendlyName}");
                                    results.AppendLine($"   State: {selectedSpeakerDevice.State}");
                                    results.AppendLine($"   ID: {selectedSpeakerDevice.ID}");
                                    
                                    bool isSelectedCompatible = TestDeviceForLoopback(selectedSpeakerDevice, results);
                                    
                                    if (isSelectedCompatible)
                                    {
                                        results.AppendLine($"   🎯 SELECTED DEVICE: ✅ COMPATIBLE - This device supports loopback capture");
                                        results.AppendLine($"   💡 This is your current speaker device and it works for loopback capture!");
                                    }
                                    else
                                    {
                                        results.AppendLine($"   🎯 SELECTED DEVICE: ❌ INCOMPATIBLE - This device does not support loopback capture");
                                        results.AppendLine($"   ⚠️ WARNING: Your selected speaker device cannot be used for loopback capture");
                                        results.AppendLine($"   💡 Consider selecting one of the compatible devices below");
                                    }
                                }
                                else
                                {
                                    results.AppendLine($"   ⚠️ No speaker device currently selected");
                                }
                            }
                            catch (Exception selectedEx)
                            {
                                results.AppendLine($"   ❌ ERROR testing selected device: {selectedEx.Message}");
                            }
                            results.AppendLine();
                            
                            // Test all devices
                            results.AppendLine("5. Testing all available devices...");
                            for (int i = 0; i < allDevices.Count; i++)
                            {
                                try
                                {
                                    var device = allDevices[i];
                                    results.AppendLine($"   --- Testing Device {i + 1}/{allDevices.Count} ---");
                                    results.AppendLine($"   Name: {device.FriendlyName}");
                                    results.AppendLine($"   State: {device.State}");
                                    results.AppendLine($"   ID: {device.ID}");
                                    
                                    bool isCompatible = TestDeviceForLoopback(device, results);
                                    
                                    if (isCompatible)
                                    {
                                        compatibleDevices++;
                                        results.AppendLine($"   ✅ COMPATIBLE - This device supports loopback capture");
                                    }
                                    else
                                    {
                                        incompatibleDevices++;
                                        results.AppendLine($"   ❌ INCOMPATIBLE - This device does not support loopback capture");
                                    }
                                }
                                catch (Exception deviceEx)
                                {
                                    incompatibleDevices++;
                                    results.AppendLine($"   ❌ ERROR testing device: {deviceEx.Message}");
                                    
                                    // Provide specific guidance for common error codes
                                    if (deviceEx.Message.Contains("0xE000020B"))
                                    {
                                        results.AppendLine($"   💡 This device is likely in use by another application");
                                        results.AppendLine($"   💡 Try closing other audio applications and test again");
                                    }
                                    else if (deviceEx.Message.Contains("0x80070005"))
                                    {
                                        results.AppendLine($"   💡 Access denied - check Windows privacy settings");
                                    }
                                    else if (deviceEx.Message.Contains("0x80070490"))
                                    {
                                        results.AppendLine($"   💡 Device not found - may have been disconnected");
                                    }
                                }
                                results.AppendLine();
                            }
                            
                            // Summary
                            results.AppendLine("=== TEST SUMMARY ===");
                            results.AppendLine($"✅ Compatible devices: {compatibleDevices}");
                            results.AppendLine($"❌ Incompatible devices: {incompatibleDevices}");
                            results.AppendLine();
                            
                            if (compatibleDevices == 0)
                            {
                                results.AppendLine("⚠️ NO COMPATIBLE DEVICES FOUND!");
                                results.AppendLine();
                                results.AppendLine("Common solutions:");
                                results.AppendLine("1. Try playing audio through speakers to activate devices");
                                results.AppendLine("2. Update audio drivers");
                                results.AppendLine("3. Check Windows privacy settings for microphone access");
                                results.AppendLine("4. Close other audio applications");
                                results.AppendLine("5. Try different audio device in Windows settings");
                                results.AppendLine("6. Some devices (USB, Bluetooth, HDMI) may not support loopback capture");
                            }
                            else
                            {
                                results.AppendLine($"🎉 Found {compatibleDevices} compatible device(s) for loopback capture!");
                                results.AppendLine("Your system should work with speaker audio capture.");
                            }
                        }
                catch (Exception ex)
                {
                    results.AppendLine($"❌ Error during device testing: {ex.Message}");
                    
                    // Provide specific guidance for common error codes
                    if (ex.Message.Contains("0xE000020B"))
                    {
                        results.AppendLine();
                        results.AppendLine("💡 Error Code 0xE000020B Analysis:");
                        results.AppendLine("   This error typically indicates one of the following:");
                        results.AppendLine("   1. Device is in exclusive mode (another app is using it)");
                        results.AppendLine("   2. Unsupported audio format");
                        results.AppendLine("   3. Device access denied");
                        results.AppendLine();
                        results.AppendLine("🔧 Solutions to try:");
                        results.AppendLine("   1. Close other audio applications (Spotify, YouTube, etc.)");
                        results.AppendLine("   2. Check Windows Sound settings - ensure 'Allow applications to take exclusive control' is disabled");
                        results.AppendLine("   3. Try changing the default audio device in Windows");
                        results.AppendLine("   4. Restart Windows Audio Service");
                        results.AppendLine("   5. Update audio drivers");
                    }
                    else if (ex.Message.Contains("0x80070005"))
                    {
                        results.AppendLine();
                        results.AppendLine("💡 Error Code 0x80070005 Analysis:");
                        results.AppendLine("   Access denied - check Windows privacy settings");
                        results.AppendLine("🔧 Solutions:");
                        results.AppendLine("   1. Go to Windows Settings > Privacy > Microphone");
                        results.AppendLine("   2. Ensure 'Allow apps to access your microphone' is enabled");
                        results.AppendLine("   3. Check if this app has microphone permission");
                    }
                    else if (ex.Message.Contains("0x80070490"))
                    {
                        results.AppendLine();
                        results.AppendLine("💡 Error Code 0x80070490 Analysis:");
                        results.AppendLine("   Element not found - device may have been disconnected");
                        results.AppendLine("🔧 Solutions:");
                        results.AppendLine("   1. Check if audio device is properly connected");
                        results.AppendLine("   2. Try unplugging and reconnecting the device");
                        results.AppendLine("   3. Update device drivers");
                    }
                }
            }
            catch (Exception ex)
            {
                results.AppendLine($"❌ Test failed: {ex.Message}");
            }
                    
                    results.AppendLine();
                    results.AppendLine($"Test completed at: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    
                    // Update UI on the main thread
                    Dispatcher.Invoke(() =>
                    {
                        AudioTestResultsText.Text = results.ToString();
                        AudioTestResults.ScrollToEnd();
                    });
                });
            }
            catch (Exception ex)
            {
                AudioTestResultsText.Text = $"❌ Test failed: {ex.Message}";
                LoggingService.Error($"[ConfigurationSettings] Comprehensive audio test failed: {ex.Message}");
            }
            finally
            {
                ComprehensiveAudioTestButton.IsEnabled = true;
            }
        }

        private MMDevice GetSelectedSpeakerDeviceObject(string selectedItemText = null)
        {
            try
            {
                MMDeviceEnumerator deviceEnumerator = new MMDeviceEnumerator();
                var devices = deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.All).ToList();
                
                LoggingService.Info($"[ConfigurationSettings] Available speaker devices ({devices.Count}):");
                for (int i = 0; i < devices.Count; i++)
                {
                    LoggingService.Info($"[ConfigurationSettings] Device {i}: '{devices[i].FriendlyName}'");
                }
                
                // STRATEGY 1: Try to use SelectedIndex from ComboBox (most reliable)
                int selectedIndex = SpeakerDeviceCombo.SelectedIndex;
                if (selectedIndex >= 0 && selectedIndex < devices.Count)
                {
                    LoggingService.Info($"[ConfigurationSettings] ✅ Using device by index {selectedIndex}: '{devices[selectedIndex].FriendlyName}'");
                    return devices[selectedIndex];
                }
                
                // STRATEGY 2: Fallback to name matching if provided
                string selectedItem = selectedItemText;
                if (string.IsNullOrEmpty(selectedItem))
                {
                    if (SpeakerDeviceCombo.SelectedItem != null)
                    {
                        selectedItem = SpeakerDeviceCombo.SelectedItem.ToString();
                    }
                }
                
                if (!string.IsNullOrEmpty(selectedItem))
                {
                    LoggingService.Info($"[ConfigurationSettings] Trying name match for: '{selectedItem}'");
                    
                    // Try exact match first
                    foreach (var device in devices)
                    {
                        if (device.FriendlyName == selectedItem)
                        {
                            LoggingService.Info($"[ConfigurationSettings] ✅ Found exact name match: '{device.FriendlyName}'");
                            return device;
                        }
                    }
                    
                    // Try partial match (in case of slight differences)
                    foreach (var device in devices)
                    {
                        if (device.FriendlyName.Contains(selectedItem) || selectedItem.Contains(device.FriendlyName))
                        {
                            LoggingService.Info($"[ConfigurationSettings] ✅ Found partial name match: '{device.FriendlyName}'");
                            return device;
                        }
                    }
                    
                    // Try case-insensitive match
                    foreach (var device in devices)
                    {
                        if (string.Equals(device.FriendlyName, selectedItem, StringComparison.OrdinalIgnoreCase))
                        {
                            LoggingService.Info($"[ConfigurationSettings] ✅ Found case-insensitive name match: '{device.FriendlyName}'");
                            return device;
                        }
                    }
                    
                    LoggingService.Warn($"[ConfigurationSettings] ❌ No matching device found for: '{selectedItem}'");
                }
                else
                {
                    LoggingService.Warn("[ConfigurationSettings] ❌ No device selected (index: {selectedIndex}, name: empty)");
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[ConfigurationSettings] Error getting selected speaker device: {ex.Message}");
            }
            
            return null;
        }

        private bool TestDeviceForLoopback(MMDevice device, StringBuilder results)
        {
            try
            {
                // Test 1: Check if device is active
                if (device.State != DeviceState.Active)
                {
                    results.AppendLine($"   ❌ Device is not active (State: {device.State})");
                    return false;
                }
                
                // Test 2: Check if device has audio client
                try
                {
                    var audioClient = device.AudioClient;
                    if (audioClient == null)
                    {
                        results.AppendLine("   ❌ Device has no AudioClient");
                        return false;
                    }
                    results.AppendLine("   ✅ Device has AudioClient");
                }
                catch (Exception ex)
                {
                    results.AppendLine($"   ❌ Failed to get AudioClient: {ex.Message}");
                    return false;
                }
                
                // Test 3: Check if device supports loopback format
                try
                {
                    var mixFormat = device.AudioClient?.MixFormat;
                    if (mixFormat == null)
                    {
                        results.AppendLine("   ❌ Device has no MixFormat");
                        return false;
                    }
                    results.AppendLine($"   ✅ Device format: {mixFormat.SampleRate}Hz, {mixFormat.BitsPerSample}-bit, {mixFormat.Channels} channels");
                }
                catch (Exception ex)
                {
                    results.AppendLine($"   ❌ Failed to get MixFormat: {ex.Message}");
                    return false;
                }
                
                // Test 4: Try to create WasapiLoopbackCapture (this is the real test)
                try
                {
                    using (var testCapture = new WasapiLoopbackCapture(device))
                    {
                        results.AppendLine("   ✅ WasapiLoopbackCapture created successfully");
                        results.AppendLine($"   ✅ Capture format: {testCapture.WaveFormat.SampleRate}Hz, {testCapture.WaveFormat.BitsPerSample}-bit, {testCapture.WaveFormat.Channels} channels");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    results.AppendLine($"   ❌ Failed to create WasapiLoopbackCapture: {ex.Message}");
                    results.AppendLine($"   Exception type: {ex.GetType().Name}");
                    
                    // Provide specific guidance based on exception type
                    if (ex.Message.Contains("exclusive"))
                    {
                        results.AppendLine("   💡 Device is in exclusive mode - close other audio applications");
                    }
                    else if (ex.Message.Contains("access"))
                    {
                        results.AppendLine("   💡 Access denied - check Windows privacy settings for microphone access");
                    }
                    else if (ex.Message.Contains("format"))
                    {
                        results.AppendLine("   💡 Format not supported - device may not support loopback capture");
                    }
                    else if (ex.Message.Contains("driver"))
                    {
                        results.AppendLine("   💡 Driver issue - update audio drivers or try different device");
                    }
                    
                    return false;
                }
            }
            catch (Exception ex)
            {
                results.AppendLine($"   ❌ Test failed with exception: {ex.Message}");
                return false;
            }
        }


        private void EnableFrontendIntegrationCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            // Save the frontend integration setting
            _enableFrontendIntegration = EnableFrontendIntegrationCheckBox.IsChecked ?? true;
            _isConfigurationChanged = true;
            
            LoggingService.Info($"[ConfigurationSettings] Frontend Integration changed: {_enableFrontendIntegration}");
        }

        #region Backend Audio Streaming Event Handlers

        private void BackendAudioWebSocketUrl_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (BackendAudioWebSocketUrlBox != null)
            {
                _backendAudioWebSocketUrl = BackendAudioWebSocketUrlBox.Text?.Trim() ?? "";
                _isConfigurationChanged = true;
            }
        }

        private void BackendAudioSampleRate_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (BackendAudioSampleRateCombo?.SelectedItem is ComboBoxItem selectedItem)
            {
                if (int.TryParse(selectedItem.Tag?.ToString(), out var sampleRate) && sampleRate > 0)
                {
                    _backendAudioSampleRate = sampleRate;
                    _isConfigurationChanged = true;
                    LoggingService.Info($"[ConfigurationSettings] Backend audio sample rate changed to: {_backendAudioSampleRate}");
                }
            }
        }

        private void BackendAudioBitDepth_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (BackendAudioBitDepthCombo?.SelectedItem is ComboBoxItem selectedItem)
            {
                if (int.TryParse(selectedItem.Tag?.ToString(), out var bitDepth) && (bitDepth == 8 || bitDepth == 16 || bitDepth == 24))
                {
                    _backendAudioBitDepth = bitDepth;
                    _isConfigurationChanged = true;
                    LoggingService.Info($"[ConfigurationSettings] Backend audio bit depth changed to: {_backendAudioBitDepth}");
                }
            }
        }

        private void BackendAudioChannels_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (BackendAudioChannelsCombo?.SelectedItem is ComboBoxItem selectedItem)
            {
                if (int.TryParse(selectedItem.Tag?.ToString(), out var channels) && (channels == 1 || channels == 2))
                {
                    _backendAudioChannels = channels;
                    _isConfigurationChanged = true;
                    LoggingService.Info($"[ConfigurationSettings] Backend audio channels changed to: {_backendAudioChannels}");
                }
            }
        }

        private void BackendAudioChunkingInterval_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (BackendAudioChunkingIntervalBox != null)
            {
                if (int.TryParse(BackendAudioChunkingIntervalBox.Text, out var interval) && interval > 0)
                {
                    _backendAudioChunkingIntervalMs = interval;
                    _isConfigurationChanged = true;
                }
            }
        }

        private void BackendAudioConnectionTimeout_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (BackendAudioConnectionTimeoutBox != null)
            {
                if (int.TryParse(BackendAudioConnectionTimeoutBox.Text, out var timeout) && timeout > 0)
                {
                    _backendAudioConnectionTimeoutSeconds = timeout;
                    _isConfigurationChanged = true;
                }
            }
        }

        private void BackendAudioMaxReconnection_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (BackendAudioMaxReconnectionBox != null)
            {
                if (int.TryParse(BackendAudioMaxReconnectionBox.Text, out var maxAttempts) && maxAttempts > 0)
                {
                    _backendAudioMaxReconnectionAttempts = maxAttempts;
                    _isConfigurationChanged = true;
                }
            }
        }

        /// <summary>
        /// Selects a ComboBox item by matching the Tag value.
        /// </summary>
        private void SelectComboBoxItemByTag(ComboBox comboBox, string tagValue)
        {
            if (comboBox == null || string.IsNullOrEmpty(tagValue)) return;

            for (int i = 0; i < comboBox.Items.Count; i++)
            {
                if (comboBox.Items[i] is ComboBoxItem item && item.Tag?.ToString() == tagValue)
                {
                    comboBox.SelectedIndex = i;
                    return;
                }
            }

            // If no match found, select the first item as default
            if (comboBox.Items.Count > 0)
            {
                comboBox.SelectedIndex = 0;
            }
        }

        /// <summary>
        /// Validates backend audio streaming settings before saving.
        /// Returns true if valid, false otherwise with error message.
        /// </summary>
        private bool ValidateBackendAudioSettings(out string validationError)
        {
            validationError = "";

            // Validate WebSocket URL (empty is allowed - auto-derived from Backend BaseUrl)
            if (!string.IsNullOrWhiteSpace(_backendAudioWebSocketUrl))
            {
                if (!_backendAudioWebSocketUrl.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) &&
                    !_backendAudioWebSocketUrl.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
                {
                    validationError = "Backend Audio WebSocket URL must start with ws:// or wss:// (or leave empty to auto-derive)";
                    return false;
                }
            }

            // Validate Sample Rate
            if (_backendAudioSampleRate <= 0)
            {
                validationError = "Sample Rate must be greater than 0";
                return false;
            }

            // Validate Bit Depth
            if (_backendAudioBitDepth != 8 && _backendAudioBitDepth != 16 && _backendAudioBitDepth != 24)
            {
                validationError = "Bit Depth must be 8, 16, or 24";
                return false;
            }

            // Validate Channels
            if (_backendAudioChannels != 1 && _backendAudioChannels != 2)
            {
                validationError = "Channels must be 1 (Mono) or 2 (Stereo)";
                return false;
            }

            // Validate Chunking Interval
            if (_backendAudioChunkingIntervalMs <= 0)
            {
                validationError = "Chunking Interval must be greater than 0 ms";
                return false;
            }

            // Validate Connection Timeout
            if (_backendAudioConnectionTimeoutSeconds <= 0)
            {
                validationError = "Connection Timeout must be greater than 0 seconds";
                return false;
            }

            // Validate Max Reconnection Attempts
            if (_backendAudioMaxReconnectionAttempts <= 0)
            {
                validationError = "Max Reconnection Attempts must be greater than 0";
                return false;
            }

            return true;
        }

        #endregion

        private void EnableCallLoggingCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            // This would be used to enable/disable call logging
            _isConfigurationChanged = true;
        }

        private void DevModeSystemAudioOnlyCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            _devModeSystemAudioOnly = DevModeSystemAudioOnlyCheckBox.IsChecked ?? false;
            _isConfigurationChanged = true;
            LoggingService.Info($"[ConfigurationSettings] DevMode System Audio Only changed to: {_devModeSystemAudioOnly}");
        }


        // Public methods for external access
        public string GetSelectedMicrophoneDevice()
        {
            return MicrophoneDeviceCombo.SelectedItem?.ToString() ?? string.Empty;
        }

        public string GetSelectedSpeakerDevice()
        {
            return SpeakerDeviceCombo.SelectedItem?.ToString() ?? string.Empty;
        }

        public int GetSelectedMicrophoneDeviceIndex()
        {
            return MicrophoneDeviceCombo.SelectedIndex;
        }

        public int GetSelectedSpeakerDeviceIndex()
        {
            return SpeakerDeviceCombo.SelectedIndex;
        }

        #region Helper Methods for Enhanced Error Reporting

        private async void TestWebSocket_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                TestWebSocketButton.IsEnabled = false;
                TestWebSocketButton.Content = "🔄 Testing...";

                // Get WebSocket URL from text box (current UI value)
                string webSocketUrl = WebSocketUrlBox?.Text?.Trim() ?? "";
                
                // If text box is empty, try to get from saved configuration
                if (string.IsNullOrEmpty(webSocketUrl))
                {
                    try
                    {
                        var config = Globals.ConfigurationInfo;
                        var clientIntegration = config?.GetSection("ClientIntegration").Get<ClientIntegrationConfiguration>();
                        webSocketUrl = clientIntegration?.WebSocketUrl ?? "";
                    }
                    catch (Exception configEx)
                    {
                        LoggingService.Info($"[ConfigTest] Could not load WebSocket URL from config: {configEx.Message}");
                    }
                }
                
                if (string.IsNullOrEmpty(webSocketUrl))
                {
                    MessageBox.Show("❌ WebSocket URL not configured!\n\nPlease enter a WebSocket URL in the text box above or save your settings first.", 
                                  "Configuration Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                LoggingService.Info($"[ConfigTest] Testing WebSocket connection to: {webSocketUrl}");

                // Test WebSocket connection
                using (var webSocket = new System.Net.WebSockets.ClientWebSocket())
                {
                    try
                    {
                        // Set connection options
                        webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                        
                        // Connect with timeout
                        using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                        {
                            await webSocket.ConnectAsync(new Uri(webSocketUrl), timeoutCts.Token);
                        }

                        if (webSocket.State == System.Net.WebSockets.WebSocketState.Open)
                        {
                            // Send a test message
                            var testMessage = System.Text.Json.JsonSerializer.Serialize(new
                            {
                                type = "test",
                                message = "Connection test from DataRippleAI",
                                timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
                            });

                            var messageBytes = System.Text.Encoding.UTF8.GetBytes(testMessage);
                            var buffer = new ArraySegment<byte>(messageBytes);
                            
                            await webSocket.SendAsync(buffer, System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                            LoggingService.Info("[ConfigTest] Test message sent to WebSocket");

                            // Try to receive a response (with timeout)
                            var responseBuffer = new byte[4096];
                            var responseSegment = new ArraySegment<byte>(responseBuffer);
                            
                            using (var responseCts = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                            {
                                try
                                {
                                    var result = await webSocket.ReceiveAsync(responseSegment, responseCts.Token);
                                    if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Text)
                                    {
                                        var responseText = System.Text.Encoding.UTF8.GetString(responseBuffer, 0, result.Count);
                                        LoggingService.Info($"[ConfigTest] Received response: {responseText}");
                                        
                                        MessageBox.Show($"✅ WebSocket connection successful!\n\n" +
                                                      $"URL: {webSocketUrl}\n" +
                                                      $"Status: Connected\n" +
                                                      $"Response: {responseText.Substring(0, Math.Min(100, responseText.Length))}" +
                                                      (responseText.Length > 100 ? "..." : ""), 
                                                      "WebSocket Test Success", MessageBoxButton.OK, MessageBoxImage.Information);
                                    }
                                    else
                                    {
                                        MessageBox.Show($"✅ WebSocket connection successful!\n\n" +
                                                      $"URL: {webSocketUrl}\n" +
                                                      $"Status: Connected\n" +
                                                      $"Note: Server connected but no text response received", 
                                                      "WebSocket Test Success", MessageBoxButton.OK, MessageBoxImage.Information);
                                    }
                                }
                                catch (OperationCanceledException)
                                {
                                    // No response received, but connection was successful
                                    MessageBox.Show($"✅ WebSocket connection successful!\n\n" +
                                                  $"URL: {webSocketUrl}\n" +
                                                  $"Status: Connected\n" +
                                                  $"Note: Server connected but no response received (this is normal)", 
                                                  "WebSocket Test Success", MessageBoxButton.OK, MessageBoxImage.Information);
                                }
                            }
                        }
                        else
                        {
                            MessageBox.Show($"❌ WebSocket connection failed!\n\n" +
                                          $"URL: {webSocketUrl}\n" +
                                          $"Status: {webSocket.State}\n" +
                                          $"Check if your WebSocket server is running on the specified URL.", 
                                          "WebSocket Test Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                    }
                    catch (System.Net.WebSockets.WebSocketException wsEx)
                    {
                        string errorDetails = GetWebSocketErrorDetails(wsEx);
                        MessageBox.Show($"❌ WebSocket connection error:\n\n" +
                                      $"URL: {webSocketUrl}\n" +
                                      $"Error: {wsEx.Message}\n" +
                                      $"Details: {errorDetails}\n\n" +
                                      $"💡 Troubleshooting:\n" +
                                      $"• Check if WebSocket server is running\n" +
                                      $"• Verify the URL format (ws:// or wss://)\n" +
                                      $"• Check firewall settings\n" +
                                      $"• Ensure port is accessible", 
                                      "WebSocket Connection Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                    catch (UriFormatException uriEx)
                    {
                        MessageBox.Show($"❌ Invalid WebSocket URL format:\n\n" +
                                      $"URL: {webSocketUrl}\n" +
                                      $"Error: {uriEx.Message}\n\n" +
                                      $"💡 Expected format: ws://localhost:3005/ws/ or wss://domain.com/ws/", 
                                      "Invalid URL Format", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                    catch (TaskCanceledException)
                    {
                        MessageBox.Show($"❌ WebSocket connection timeout:\n\n" +
                                      $"URL: {webSocketUrl}\n" +
                                      $"Error: Connection timed out after 10 seconds\n\n" +
                                      $"💡 Troubleshooting:\n" +
                                      $"• Check if server is running and accessible\n" +
                                      $"• Verify network connectivity\n" +
                                      $"• Check if port is blocked by firewall", 
                                      "Connection Timeout", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    finally
                    {
                        if (webSocket.State == System.Net.WebSockets.WebSocketState.Open)
                        {
                            try
                            {
                                await webSocket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, 
                                                         "Test completed", CancellationToken.None);
                            }
                            catch (Exception closeEx)
                            {
                                LoggingService.Info($"[ConfigTest] Error closing WebSocket: {closeEx.Message}");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[ConfigTest] WebSocket test error: {ex.Message}");
                MessageBox.Show($"❌ WebSocket test error:\n{ex.Message}", 
                              "Test Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                TestWebSocketButton.IsEnabled = true;
                TestWebSocketButton.Content = "🧪 Test WebSocket";
            }
        }

        private string GetWebSocketErrorDetails(System.Net.WebSockets.WebSocketException wsEx)
        {
            return wsEx.WebSocketErrorCode switch
            {
                System.Net.WebSockets.WebSocketError.ConnectionClosedPrematurely => "Connection closed unexpectedly",
                System.Net.WebSockets.WebSocketError.InvalidMessageType => "Invalid message format",
                System.Net.WebSockets.WebSocketError.InvalidState => "WebSocket in invalid state",
                System.Net.WebSockets.WebSocketError.NotAWebSocket => "Server does not support WebSocket protocol",
                System.Net.WebSockets.WebSocketError.UnsupportedProtocol => "WebSocket protocol not supported",
                System.Net.WebSockets.WebSocketError.UnsupportedVersion => "WebSocket version not supported",
                _ => $"WebSocket error code: {wsEx.WebSocketErrorCode}"
            };
        }

        #endregion

        #region Windows Default Audio Device Management

        /// <summary>
        /// Sets the Windows default audio devices based on the selected devices in the configuration
        /// NOTE: This is an optional feature that may fail on some Windows versions due to COM compatibility
        /// </summary>
        private void SetWindowsDefaultAudioDevices()
        {
            try
            {
                LoggingService.Info("[ConfigurationSettings] Attempting to set Windows default audio devices (optional feature)...");
                
                // Set default microphone device
                if (!string.IsNullOrEmpty(_selectedMicrophoneDevice))
                {
                    try
                    {
                        SetDefaultMicrophoneDevice(_selectedMicrophoneDevice);
                    }
                    catch (Exception micEx)
                    {
                        LoggingService.Warn($"[ConfigurationSettings] Could not set default microphone (non-critical): {micEx.Message}");
                    }
                }
                
                // Set default speaker device
                if (!string.IsNullOrEmpty(_selectedSpeakerDevice))
                {
                    try
                    {
                        SetDefaultSpeakerDevice(_selectedSpeakerDevice);
                    }
                    catch (Exception speakerEx)
                    {
                        LoggingService.Warn($"[ConfigurationSettings] Could not set default speaker (non-critical): {speakerEx.Message}");
                    }
                }
                
                LoggingService.Info("[ConfigurationSettings] Windows default audio device setup completed (optional feature)");
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"[ConfigurationSettings] Windows default audio device feature not available: {ex.Message}");
                // Don't show error to user - this is optional functionality
            }
        }

        /// <summary>
        /// Sets the default microphone device in Windows
        /// </summary>
        /// <param name="deviceName">The name of the microphone device to set as default</param>
        private void SetDefaultMicrophoneDevice(string deviceName)
        {
            using (var deviceEnumerator = new MMDeviceEnumerator())
            {
                // Get all capture devices (microphones)
                var captureDevices = deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
                
                foreach (var device in captureDevices)
                {
                    // Try both exact match and partial match (to handle truncated device names)
                    if (device.FriendlyName.Equals(deviceName, StringComparison.OrdinalIgnoreCase) ||
                        device.FriendlyName.StartsWith(deviceName, StringComparison.OrdinalIgnoreCase))
                    {
                        // Set as default device using COM interface
                        var policyConfig = (IPolicyConfig)new PolicyConfigClient();
                        policyConfig.SetDefaultEndpoint(device.ID, ERole.eCommunications);
                        policyConfig.SetDefaultEndpoint(device.ID, ERole.eConsole);
                        
                        LoggingService.Info($"[ConfigurationSettings] Set default microphone: {device.FriendlyName}");
                        return;
                    }
                }
                
                LoggingService.Warn($"[ConfigurationSettings] Microphone device not found: {deviceName}");
            }
        }

        /// <summary>
        /// Sets the default speaker device in Windows
        /// </summary>
        /// <param name="deviceName">The name of the speaker device to set as default</param>
        private void SetDefaultSpeakerDevice(string deviceName)
        {
            using (var deviceEnumerator = new MMDeviceEnumerator())
            {
                // Get all render devices (speakers)
                var renderDevices = deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                
                foreach (var device in renderDevices)
                {
                    if (device.FriendlyName.Equals(deviceName, StringComparison.OrdinalIgnoreCase))
                    {
                        // Set as default device using COM interface
                        var policyConfig = (IPolicyConfig)new PolicyConfigClient();
                        policyConfig.SetDefaultEndpoint(device.ID, ERole.eCommunications);
                        policyConfig.SetDefaultEndpoint(device.ID, ERole.eConsole);
                        
                        LoggingService.Info($"[ConfigurationSettings] Set default speaker: {deviceName}");
                        return;
                    }
                }
                
                LoggingService.Warn($"[ConfigurationSettings] Speaker device not found: {deviceName}");
            }
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
                _waveFormat = new WaveFormat(44100, 1); // 44.1kHz, mono
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
