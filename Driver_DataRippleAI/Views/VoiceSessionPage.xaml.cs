using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using DataRippleAIDesktop.Models;
using DataRippleAIDesktop.RecorderVisualizer;
using DataRippleAIDesktop.Services;
using DataRippleAIDesktop.Utilities;
using Microsoft.Extensions.Configuration;
using Orientation = System.Windows.Controls.Orientation;
using System.Text.Json;

namespace DataRippleAIDesktop.Views
{
    /// <summary>
    /// Interaction logic for VoiceSessionPage.xaml
    /// </summary>
    public partial class VoiceSessionPage : UserControl
    {
      
        private AudioRecorderVisualizer _audioRecorderVisualizer { get; set; } = null;
        // BackendAudioStreamingService replaces legacy STT services
        private BackendAudioStreamingService _backendAudioStreamingService;

        private IVRService _ivrService; // Independent IVR service (not tied to frontend)
        private CallLoggerService _callLogger = new CallLoggerService(); // Call logging service
        private int _MaxWidth { get; set; } = 1200; // Total available width
        private int _MessageMaxWidth { get; set; } // Will be calculated as 2/3 of total width
        public event Action NavigateToLiveCallRequested;
        
        // Event to notify when disposal is complete
        public event Action DisposalCompleted;
        
        // Event to notify when call is started (after answering)
        public event EventHandler OnVoiceSessionCallStarted;
        
        // Event to notify when call is rejected or canceled
        public event EventHandler OnVoiceSessionCallRejected;
        
        // Event to notify when call start fails
        public event EventHandler<string> OnVoiceSessionCallStartFailed;
        
        // Disposal state management
        public bool _isDisposing = false;
        private readonly object _disposalLock = new object();
        
        // Start state management
        public bool _isStarting = false;
        private readonly object _startLock = new object();
        
        // BackendAudioStreamingService initialization lock
        private readonly object _backendAudioInitLock = new object();

        // Cancellation token for frontend integration background tasks
        private CancellationTokenSource _frontendCancellationTokenSource;
        
        // Call duration tracking
        private DateTime _callStartTime;
        
        // Separate audio queues for customer and agent - using ConcurrentQueue for thread safety
        private ConcurrentQueue<byte[]> _customerAudioQueue = new ConcurrentQueue<byte[]>();
        private ConcurrentQueue<byte[]> _agentAudioQueue = new ConcurrentQueue<byte[]>();
        
        // Transcription queues for backend processing
        // Using ConcurrentQueue for thread-safe operations without manual locking
        private ConcurrentQueue<(DateTime timestamp, string text, string messageId, string turnId, bool isCustomer)> _micTranscriptionQueue = new ConcurrentQueue<(DateTime, string, string, string, bool)>();      // CSR/Agent sentences from MIC
        private ConcurrentQueue<(DateTime timestamp, string text, string messageId, string turnId, bool isCustomer)> _speakerTranscriptionQueue = new ConcurrentQueue<(DateTime, string, string, string, bool)>();  // Customer/Caller sentences from SPEAKER
        private ConcurrentQueue<(DateTime timestamp, string text, string messageId, string turnId)> _contextualMessageQueue = new ConcurrentQueue<(DateTime, string, string, string)>();  // user_contextual_message queue
        
        // Track pending contextual messages by messageId for updating when response arrives
        private Dictionary<string, ConversationMessage> _pendingContextualMessages = new Dictionary<string, ConversationMessage>();
        private readonly object _pendingContextualMessagesLock = new object();
        
        // Store contextual message data (refined_text and thinking) from agent_response events
        private Dictionary<string, (string refinedText, string thinking)> _contextualMessageData = new Dictionary<string, (string, string)>();
        private readonly object _contextualMessageDataLock = new object();
        
        // Conversation history tracking
        private List<ConversationMessage> _conversationHistory = new List<ConversationMessage>();
        private ConversationMessage _lastCallerMessage = null;
        private ConversationMessage _lastAgentMessage = null;
        
        // Conversation ID tracking - maintained throughout the call
        private string _currentConversationId = null;

        // Backend agent response delta streaming: tracks the in-progress message being built from delta chunks
        private ConversationMessage _backendDeltaAgentMessage = null;
        private string _backendDeltaAgentTurnId = null;
        private string _backendDeltaAgentText = "";
        
        // Track last agent_thinking for user_contextual_message display
        private string _lastAgentThinking = null;
        
        // Track displayed contextual messages to prevent duplicates
        private HashSet<string> _displayedContextualMessages = new HashSet<string>();
        private string _currentAgentId = null;
        private bool _callStartedEventSent = false; // Track if call_started event has been sent to prevent duplicates
        
        // Track turn_id to source mapping for correct speaker determination and frontend websocket
        // Key: turn_id, Value: source ("CSR", "Caller", or "user_contextual_message")
        private ConcurrentDictionary<string, string> _turnIdToSourceMap = new ConcurrentDictionary<string, string>();

        // Sequential turnID counter - ensures unique, sequential turnIDs alternating between CSR and Caller
        // Format: 001, 002, 003, etc. (sequential across all speakers)
        private int _globalTurnIdCounter = 0;
        private readonly object _turnIdCounterLock = new object();

        // Counter for contextual message turnIds
        private int _contextualTurnCounter = 0;
        
        // Backend API health check is now managed by MainWindow for the title bar indicator

        // Latency tracking — rolling average over the last N samples
        private const int LatencySampleCount = 10;
        private readonly Queue<double> _latencySamples = new Queue<double>();
        private readonly object _latencySamplesLock = new object();
        private DateTime _lastLatencyUiUpdate = DateTime.MinValue;

        // Session stats tracking for idle panel
        private int _callsAcceptedCount = 0;
        private int _callsRejectedCount = 0;
        private List<TimeSpan> _callDurations = new List<TimeSpan>();
        private List<double> _sessionLatencySamples = new List<double>();

        // Sequential call number for timeline dot badges (incremented on Start/Reject, reused on End)
        private int _callSequenceNumber = 0;

        // Call duration timer (ticks every second during active call)
        private DispatcherTimer _callDurationTimer;
        private bool _isCallActive = false;

        // Tracks whether InitializeUIOnly has been run at least once.
        // Prevents the Loaded event from clearing call logs when the
        // page is re-added to the visual tree after navigation.
        private bool _hasBeenInitialized = false;
        
        public VoiceSessionPage()
        {
            InitializeComponent();
            this.Unloaded += VoiceSessionPage_Unloaded;
            this.Loaded += VoiceSessionPage_Loaded;

            // Calculate message width as 2/3 of total width
            CalculateMessageWidth();

            // Mode indicator badge removed - mode is set by DevMode checkbox on login page

            // Initialize IVR service independently (always available, not tied to frontend)
            InitializeIVRService();

            // Frontend integration initialization removed - FE WS indicator moved to MainWindow
        }
        
        /// <summary>
        /// Handle page loaded event - ensure IVR service is initialized
        /// </summary>
        private void VoiceSessionPage_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                LoggingService.Info("[VoiceSessionPage] Page loaded - checking IVR service status");
                
                // Re-initialize IVR service if it's null (e.g., after navigation)
                if (_ivrService == null)
                {
                    LoggingService.Info("[VoiceSessionPage] IVR service is null, re-initializing...");
                    InitializeIVRService();
                }
                else
                {
                    LoggingService.Info("[VoiceSessionPage] IVR service is already initialized");
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[VoiceSessionPage] Error in page loaded handler: {ex.Message}");
            }
        }

        /// <summary>
        /// Initialize IVR service independently (not tied to frontend integration)
        /// </summary>
        private void InitializeIVRService()
        {
            try
            {
                _ivrService = new IVRService();
                
                // Subscribe to IVR events
                _ivrService.CallAnswered += OnIVRCallAnswered;
                _ivrService.CallRejected += OnIVRCallRejected;
                _ivrService.IncomingCallRinging += OnIVRIncomingCallRinging; // UI only; forwarding handled elsewhere
                
                LoggingService.Info("[VoiceSessionPage] IVR service initialized successfully (independent of frontend)");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[VoiceSessionPage] Failed to initialize IVR service: {ex.Message}");
                _ivrService = null;
            }
        }
        
        /// <summary>
        /// Calculates the maximum width for chat bubbles (2/3 of total width)
        /// </summary>
        private void CalculateMessageWidth()
        {
            try
            {
                // Set message width to 2/3 of total width
                _MessageMaxWidth = (int)(_MaxWidth * 2.0 / 3.0);
                LoggingService.Info($"[VoiceSessionPage] Message max width calculated: {_MessageMaxWidth} (2/3 of {_MaxWidth})");
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[VoiceSessionPage] Error calculating message width: {ex.Message}");
                // Fallback to a reasonable default
                _MessageMaxWidth = 800;
            }
        }

        /// <summary>
        /// Start call session — sets up agent ID and conversation tracking.
        /// Frontend notification has been removed; data flows only to the backend.
        /// </summary>
        private async Task StartCallSessionAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                if (cancellationToken.IsCancellationRequested) return;

                _currentAgentId = "default_agent";

                // Poll for backend connection and assign conversation ID
                _ = Task.Run(async () => await PollForConversationIdAndStartCall(cancellationToken), cancellationToken);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                LoggingService.Error($"[VoiceSessionPage] Error starting call session: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Poll for backend connection and send call_started event once the backend audio WebSocket is connected.
        /// </summary>
        private async Task PollForConversationIdAndStartCall(CancellationToken cancellationToken)
        {
            try
            {
                // Poll for up to 8 seconds (80 checks @ 100ms intervals)
                for (int i = 0; i < 80; i++)
                {
                    if (cancellationToken.IsCancellationRequested) return;

                    bool backendConnected = _backendAudioStreamingService?.IsConnected ?? false;
                    if (backendConnected)
                    {
                        if (string.IsNullOrEmpty(_currentConversationId))
                        {
                            _currentConversationId = $"session_{_callStartTime:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}";
                        }

                        // Update call logger
                        if (_callLogger != null)
                        {
                            try
                            {
                                bool enableCallLogging = true;
                                try
                                {
                                    var configuration = new ConfigurationBuilder()
                                        .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
                                        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                                        .Build();
                                    var callLoggingSetting = configuration["CallLogging:EnableCallLogging"];
                                    if (callLoggingSetting != null)
                                    {
                                        enableCallLogging = bool.Parse(callLoggingSetting);
                                    }
                                }
                                catch { }

                                if (enableCallLogging)
                                {
                                    string callId = _callStartTime.ToString("yyyyMMdd-HHmmss");
                                    _callLogger.StartCallLogging(_currentConversationId, callId);
                                }
                            }
                            catch (Exception ex)
                            {
                                LoggingService.Warn($"[VoiceSessionPage] Failed to update call logger: {ex.Message}");
                            }
                        }

                        // Start audio recording, show waveforms, and show latency indicator
                        _callStartedEventSent = true;
                        _lastLatencyUiUpdate = DateTime.MinValue; // Reset throttle so first latency sample updates immediately
                        Dispatcher.Invoke(() =>
                        {
                            if (_audioRecorderVisualizer != null && !_audioRecorderVisualizer.IsRecording())
                            {
                                _audioRecorderVisualizer.StartRecording();
                            }
                            // Waveform controls are always visible in the Customer/Agent cards;
                            // audio data will flow into them automatically via AudioRecorderVisualizer
                            LatencyIndicatorPanel.Visibility = Visibility.Visible;
                            LatencyValueText.Text = "-- ms";
                        });

                        LoggingService.Info($"[VoiceSessionPage] Call session started - ConversationID: {_currentConversationId}");
                        return;
                    }

                    await Task.Delay(100, cancellationToken);
                }

                // Backend never connected within 8s — start anyway with pending ID
                LoggingService.Warn("[VoiceSessionPage] Backend audio service did not connect within 8s - starting call session anyway");
                if (string.IsNullOrEmpty(_currentConversationId))
                {
                    _currentConversationId = $"session_{_callStartTime:yyyyMMdd_HHmmss}_pending";
                }

                _callStartedEventSent = true;
                _lastLatencyUiUpdate = DateTime.MinValue; // Reset throttle so first latency sample updates immediately
                Dispatcher.Invoke(() =>
                {
                    if (_audioRecorderVisualizer != null && !_audioRecorderVisualizer.IsRecording())
                    {
                        _audioRecorderVisualizer.StartRecording();
                    }
                    // Waveform controls are always visible in the Customer/Agent cards;
                    // audio data will flow into them automatically via AudioRecorderVisualizer
                    LatencyIndicatorPanel.Visibility = Visibility.Visible;
                    LatencyValueText.Text = "-- ms";
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                LoggingService.Error($"[VoiceSessionPage] Error polling for backend connection: {ex.Message}");
            }
        }

        // ForwardTranscriptToFrontendAsync removed — no longer sending data to frontend



        // ForwardToolCallToFrontendAsync removed — no longer sending data to frontend

        /// <summary>
        /// End call session — clears conversation and delta tracking state.
        /// Frontend notification has been removed.
        /// </summary>
        private Task EndCallSessionAsync()
        {
            try
            {
                if (string.IsNullOrEmpty(_currentConversationId))
                {
                    LoggingService.Info("[VoiceSessionPage] EndCallSessionAsync called but no active call - skipping");
                    return Task.CompletedTask;
                }

                LoggingService.Info($"[VoiceSessionPage] Ending call session - Conversation ID: {_currentConversationId}");

                // Clear conversation ID, agent ID, and delta tracking state after call ends
                _currentConversationId = null;
                _currentAgentId = null;
                _backendDeltaAgentMessage = null;
                _backendDeltaAgentTurnId = null;
                _backendDeltaAgentText = "";
                LoggingService.Info("[VoiceSessionPage] Conversation ID, Agent ID, and delta state cleared after call end");
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[VoiceSessionPage] Error ending call session: {ex.Message}");
            }
            return Task.CompletedTask;
        }


        /// <summary>
        /// Updates the width of existing messages to match new constraints
        /// </summary>
        private void UpdateExistingMessageWidths()
        {
            try
            {
                foreach (var message in _conversationHistory)
                {
                    if (message.MessageBorder != null)
                    {
                        message.MessageBorder.MaxWidth = _MessageMaxWidth;
                    }
                }
                LoggingService.Info($"[VoiceSessionPage] Updated {_conversationHistory.Count} existing message widths");
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[VoiceSessionPage] Error updating existing message widths: {ex.Message}");
            }
        }

        
        StackPanel _NewStackPanelSummary = null;
        System.Windows.Controls.RichTextBox summaryRichTextBox = null;

        private async void VoiceSessionPage_Unloaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // Determine if this is a full disposal (page being destroyed) vs
                // a lightweight navigation-away (page temporarily removed from visual tree
                // but cached for reuse). Only perform destructive cleanup during disposal.
                bool isFullDisposal = _isDisposing;

                if (isFullDisposal)
                {
                    LoggingService.Info("[VoiceSessionPage] Page unloaded during FULL DISPOSAL - performing complete cleanup");
                }
                else
                {
                    LoggingService.Info("[VoiceSessionPage] Page unloaded due to NAVIGATION (cached for reuse) - performing lightweight cleanup only");
                }

                // Stop call duration timer if running (lightweight - always safe)
                StopCallDurationTimer();

                // End call session state cleanup only during full disposal
                if (isFullDisposal && !string.IsNullOrEmpty(_currentConversationId))
                {
                    try
                    {
                        LoggingService.Info($"[VoiceSessionPage] Active call detected (ConvID: {_currentConversationId}) - cleaning up state before page unload");
                        await EndCallSessionAsync();
                    }
                    catch (Exception callEx)
                    {
                        LoggingService.Info($"[VoiceSessionPage] Error ending call session: {callEx.Message}");
                    }
                }
                else if (!isFullDisposal)
                {
                    LoggingService.Info("[VoiceSessionPage] Navigation unload - preserving call session state");
                }
                else
                {
                    LoggingService.Info("[VoiceSessionPage] No active call detected - skipping call end on page unload");
                }

                // Stop and dispose call logger only during full disposal
                if (isFullDisposal && _callLogger != null)
                {
                    _callLogger.Dispose();
                    _callLogger = null;
                    LoggingService.Info("[VoiceSessionPage] Call logger disposed");
                }

                // Stop audio recording - stop capture but only null the reference during full disposal
                if (_audioRecorderVisualizer != null)
                {
                    _audioRecorderVisualizer.StopCapture();
                    if (isFullDisposal)
                    {
                        _audioRecorderVisualizer = null;
                    }
                }

                // Clear waveform data and hide latency indicator when page is unloaded
                Dispatcher.Invoke(() =>
                {
                    AgentAvatarWaveform.ClearDisplay();
                    CustomerAvatarWaveform.ClearDisplay();
                    LatencyIndicatorPanel.Visibility = Visibility.Collapsed;
                    LoggingService.Info("[VoiceSessionPage] Waveforms cleared and latency indicator hidden during page unload");
                });

                // Only dispose backend audio streaming service during full disposal
                if (isFullDisposal)
                {
                    if (_backendAudioStreamingService != null && !string.IsNullOrEmpty(_currentConversationId))
                    {
                        LoggingService.Info("[VoiceSessionPage] Stopping BackendAudioStreamingService due to active call");
                        await _backendAudioStreamingService.DisconnectAsync();
                        _backendAudioStreamingService?.Dispose();
                        _backendAudioStreamingService = null;
                    }
                    else if (_backendAudioStreamingService != null)
                    {
                        LoggingService.Info("[VoiceSessionPage] BackendAudioStreamingService exists but no active call - disposing");
                        _backendAudioStreamingService?.Dispose();
                        _backendAudioStreamingService = null;
                    }
                }

                // Dispose IVR service only during full disposal
                if (isFullDisposal && _ivrService != null)
                {
                    try
                    {
                        _ivrService.Dispose();
                        _ivrService = null;
                        LoggingService.Info("[VoiceSessionPage] IVR service disposed");
                    }
                    catch (Exception ivrEx)
                    {
                        LoggingService.Info($"[VoiceSessionPage] Error disposing IVR service: {ivrEx.Message}");
                    }
                }

                LoggingService.Info($"[VoiceSessionPage] Cleanup completed (fullDisposal={isFullDisposal})");
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[VoiceSessionPage] ? Error during cleanup: {ex.Message}");
            }
        }

        /// <summary>
        /// Initialize UI components without starting transcription
        /// </summary>
        private async Task InitializeUIOnly()
        {
            try
            {
                LoggingService.Info("[VoiceSessionPage] Initializing UI components...");

                // Clear conversation history for new session
                ClearConversationHistory();

                // Show idle welcome panel
                ShowIdlePanel();

                AudioChunkholderUtility.ClearChunkHolder();
                // No config parameter needed - audio chunking defaults are set in AudioRecorderVisualizer
                _audioRecorderVisualizer = new AudioRecorderVisualizer(AgentAvatarWaveform);
                _audioRecorderVisualizer.NumberOfLines = 50;
                _audioRecorderVisualizer.ResetSpectrumData();

                // Set the individual spectrum visualizers
                _audioRecorderVisualizer.SetMicSpectrumVisualizer(AgentAvatarWaveform);
                _audioRecorderVisualizer.SetSpeakerSpectrumVisualizer(CustomerAvatarWaveform);
                _audioRecorderVisualizer.SetAgentAvatarVisualizer(AgentAvatarWaveform);
                _audioRecorderVisualizer.SetCustomerAvatarVisualizer(CustomerAvatarWaveform);

                // Set waveform colors: Cyan for CSR (Agent), Yellow-Orange for Caller (Customer)
                AgentAvatarWaveform.SetWaveformColor(System.Windows.Media.Color.FromRgb(0, 206, 209)); // Cyan
                CustomerAvatarWaveform.SetWaveformColor(System.Windows.Media.Color.FromRgb(255, 165, 0)); // Yellow-Orange

                LoggingService.Info("[VoiceSessionPage] UI components initialized successfully");
                LoggingService.Info("[VoiceSessionPage] Waiting for 'Start Call' button to initialize backend audio streaming");
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[VoiceSessionPage] ERROR in InitializeUIOnly: {ex.Message}");
                LoggingService.Info($"[VoiceSessionPage] Stack trace: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// Create a tool call result message in the center of the UI
        /// </summary>
        private ConversationMessage CreateToolCallMessage(string toolName, string identifier, string output, Models.ThinkingInfo thinking)
        {
            try
            {
                // Create message container - centered with distinct styling (cyan theme to match CSR)
                Border messageBorder = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(10, 30, 50)), // Dark cyan-tinted background
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0, 212, 255)), // #00D4FF - Match CSR avatar
                    BorderThickness = new Thickness(2),
                    CornerRadius = new CornerRadius(12),
                    Margin = new Thickness(50, 8, 50, 8), // More margin to center it
                    HorizontalAlignment = HorizontalAlignment.Center,
                    MaxWidth = _MessageMaxWidth - 100,
                    Padding = new Thickness(15, 10, 15, 10)
                };

                // Add glow effect
                messageBorder.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 8,
                    ShadowDepth = 2,
                    Color = Color.FromRgb(0, 212, 255), // Cyan glow to match CSR avatar
                    Opacity = 0.3
                };

                // Create message content panel
                StackPanel messagePanel = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };

                // Create header with tool icon
                StackPanel headerPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 8)
                };

                // Tool icon
                TextBlock toolIcon = new TextBlock
                {
                    Text = "🔧",
                    FontSize = 20,
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                headerPanel.Children.Add(toolIcon);

                // Tool name
                TextBlock toolNameText = new TextBlock
                {
                    Text = $"Tool Call: {toolName}",
                    FontWeight = FontWeights.Bold,
                    FontSize = 14,
                    Foreground = new SolidColorBrush(Color.FromRgb(0, 212, 255)), // #00D4FF - Cyan to match theme
                    VerticalAlignment = VerticalAlignment.Center
                };
                headerPanel.Children.Add(toolNameText);

                // Add time
                TextBlock timeText = new TextBlock
                {
                    Text = $" • {DateTime.Now.ToString("hh:mm:ss tt")}",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(139, 154, 175)), // #8B9AAF - Light gray for dark theme
                    VerticalAlignment = VerticalAlignment.Center
                };
                headerPanel.Children.Add(timeText);

                messagePanel.Children.Add(headerPanel);

                // Thinking section (if available)
                if (thinking != null && !string.IsNullOrEmpty(thinking.Summary))
                {
                    Border thinkingBlock = new Border
                    {
                        Background = new SolidColorBrush(Color.FromRgb(20, 40, 60)), // Dark background
                        BorderBrush = new SolidColorBrush(Color.FromRgb(0, 212, 255)), // Cyan border
                        BorderThickness = new Thickness(1.5),
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(10, 8, 10, 8),
                        Margin = new Thickness(0, 0, 0, 6)
                    };

                    StackPanel thinkingContent = new StackPanel
                    {
                        Orientation = Orientation.Vertical
                    };

                    TextBlock thinkingLabel = new TextBlock
                    {
                        Text = "💭 AI Thinking:",
                        FontSize = 10,
                        FontWeight = FontWeights.Bold,
                        Foreground = new SolidColorBrush(Color.FromRgb(0, 212, 255)), // Cyan to match theme
                        Margin = new Thickness(0, 0, 0, 4)
                    };
                    thinkingContent.Children.Add(thinkingLabel);

                    TextBlock thinkingText = new TextBlock
                    {
                        Text = thinking.Summary,
                        FontSize = 11,
                        Foreground = new SolidColorBrush(Color.FromRgb(139, 154, 175)), // #8B9AAF - Light gray for dark theme
                        TextWrapping = TextWrapping.Wrap,
                        FontStyle = FontStyles.Italic
                    };
                    thinkingContent.Children.Add(thinkingText);

                    if (!string.IsNullOrEmpty(thinking.Plan))
                    {
                        TextBlock planText = new TextBlock
                        {
                            Text = $"Plan: {thinking.Plan}",
                            FontSize = 10,
                            Foreground = new SolidColorBrush(Color.FromRgb(107, 122, 148)), // #6B7A94 - Medium gray for dark theme
                            TextWrapping = TextWrapping.Wrap,
                            Margin = new Thickness(0, 4, 0, 0)
                        };
                        thinkingContent.Children.Add(planText);
                    }

                    thinkingBlock.Child = thinkingContent;
                    messagePanel.Children.Add(thinkingBlock);
                }

                // Identifier section
                if (!string.IsNullOrEmpty(identifier))
                {
                    Border identifierBlock = new Border
                    {
                        Background = new SolidColorBrush(Color.FromRgb(20, 40, 60)), // Dark background
                        BorderBrush = new SolidColorBrush(Color.FromRgb(245, 158, 11)), // Amber border to match caller
                        BorderThickness = new Thickness(1.5),
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(10, 6, 10, 6),
                        Margin = new Thickness(0, 0, 0, 6)
                    };

                    StackPanel identifierContent = new StackPanel
                    {
                        Orientation = Orientation.Horizontal
                    };

                    TextBlock identifierLabel = new TextBlock
                    {
                        Text = "🔑 Input: ",
                        FontSize = 10,
                        FontWeight = FontWeights.Bold,
                        Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11)) // Amber to match caller
                    };
                    identifierContent.Children.Add(identifierLabel);

                    TextBlock identifierText = new TextBlock
                    {
                        Text = identifier,
                        FontSize = 11,
                        Foreground = new SolidColorBrush(Color.FromRgb(139, 154, 175)), // #8B9AAF - Light gray for dark theme
                        TextWrapping = TextWrapping.Wrap
                    };
                    identifierContent.Children.Add(identifierText);

                    identifierBlock.Child = identifierContent;
                    messagePanel.Children.Add(identifierBlock);
                }

                // Output section
                if (!string.IsNullOrEmpty(output))
                {
                    Border outputBlock = new Border
                    {
                        Background = new SolidColorBrush(Color.FromRgb(20, 40, 60)), // Dark background
                        BorderBrush = new SolidColorBrush(Color.FromRgb(16, 185, 129)), // Emerald green (#10B981)
                        BorderThickness = new Thickness(1.5),
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(10, 8, 10, 8)
                    };

                    StackPanel outputContent = new StackPanel
                    {
                        Orientation = Orientation.Vertical
                    };

                    TextBlock outputLabel = new TextBlock
                    {
                        Text = "✅ Result:",
                        FontSize = 10,
                        FontWeight = FontWeights.Bold,
                        Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129)), // Emerald green (#10B981)
                        Margin = new Thickness(0, 0, 0, 4)
                    };
                    outputContent.Children.Add(outputLabel);

                    TextBlock outputText = new TextBlock
                    {
                        Text = output,
                        FontSize = 11,
                        Foreground = new SolidColorBrush(Color.FromRgb(139, 154, 175)), // #8B9AAF - Light gray for dark theme
                        TextWrapping = TextWrapping.Wrap,
                        LineHeight = 16
                    };
                    outputContent.Children.Add(outputText);

                    outputBlock.Child = outputContent;
                    messagePanel.Children.Add(outputBlock);
                }

                messageBorder.Child = messagePanel;

                // Create conversation message object
                var message = new ConversationMessage
                {
                    Text = $"[Tool: {toolName}] {output}",
                    IsCaller = false, // Neutral - tool call
                    Timestamp = DateTime.Now,
                    MessageBorder = messageBorder,
                    TranscriptionTextBlock = null,
                    AIResponseTextBlock = null,
                    PartialText = "",
                    HasAIResponse = true // Mark as complete to avoid interfering with message pairing
                };

                return message;
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[VoiceSessionPage] Error creating tool call message: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Update an existing contextual message with refined_text and thinking
        /// Updates the yellow AI response box (like regular messages)
        /// </summary>
        private void UpdateContextualMessage(ConversationMessage message, string refinedText, string thinking = null, string agentTurnId = null, string rawAgentResponseJson = null)
        {
            try
            {
                if (message == null)
                {
                    LoggingService.Warn("[VoiceSessionPage] ⚠️ Cannot update contextual message - message is null");
                    return;
                }

                LoggingService.Info($"[VoiceSessionPage] 🔄 UpdateContextualMessage called - Message: {message.Id}, TurnId: {message.TurnId}, HasAIResponseBlock: {message.AIResponseTextBlock != null}, HasMessageBorder: {message.MessageBorder != null}, RefinedText length: {refinedText?.Length ?? 0}, HasRawJson: {!string.IsNullOrEmpty(rawAgentResponseJson)}");

                Dispatcher.Invoke(() =>
                {
                    // Use provided turnId if available, otherwise use message.TurnId
                    string turnIdToDisplay = agentTurnId ?? message.TurnId ?? "";

                    LoggingService.Info($"[VoiceSessionPage] 🔄 Inside Dispatcher.Invoke - TurnIdToDisplay: {turnIdToDisplay}, MessageBorder: {message.MessageBorder != null}, AIResponseTextBlock: {message.AIResponseTextBlock != null}");

                    string responseText;
                    
                    // If raw agent_response JSON is provided, use it exactly as received from backend
                    // DO NOT MODIFY - Display as-is since backend already includes proper thinking
                    if (!string.IsNullOrEmpty(rawAgentResponseJson))
                    {
                        // Format the raw JSON for readability (pretty print) but preserve all original fields and values
                        try
                        {
                            using (JsonDocument doc = JsonDocument.Parse(rawAgentResponseJson))
                            {
                                var rootElement = doc.RootElement;
                                
                                // Log what we're about to display
                                if (rootElement.TryGetProperty("thinking", out var thinkingCheckProp))
                                {
                                    string thinkingCheck = thinkingCheckProp.GetString();
                                    LoggingService.Info($"[VoiceSessionPage] 📝 Raw JSON has thinking field - HasValue: {!string.IsNullOrEmpty(thinkingCheck)}, Length: {thinkingCheck?.Length ?? 0}");
                                    if (string.IsNullOrEmpty(thinkingCheck))
                                    {
                                        LoggingService.Warn($"[VoiceSessionPage] ⚠️ Raw JSON thinking field is EMPTY - this is the problem!");
                                    }
                                }
                                else
                                {
                                    LoggingService.Warn($"[VoiceSessionPage] ⚠️ Raw JSON does NOT have thinking field - will display as-is");
                                }
                                
                                responseText = System.Text.Json.JsonSerializer.Serialize(rootElement, new System.Text.Json.JsonSerializerOptions
                                {
                                    WriteIndented = true
                                });
                            }
                            LoggingService.Info($"[VoiceSessionPage] 📝 Using raw backend response JSON as-is (formatted for display) - Length: {responseText.Length}");
                        }
                        catch (Exception jsonEx)
                        {
                            // If parsing fails, use as-is
                            LoggingService.Warn($"[VoiceSessionPage] ⚠️ Could not parse raw JSON, using as-is: {jsonEx.Message}");
                            responseText = rawAgentResponseJson;
                        }
                    }
                    else
                    {
                        // Fallback: Build JSON structure with refined_text, thinking, and turnId
                        // IMPORTANT: Preserve existing thinking if new thinking is empty and we already have thinking in the message
                        string thinkingToUse = thinking;
                        if (string.IsNullOrEmpty(thinkingToUse) && message.AIResponseTextBlock != null && !string.IsNullOrEmpty(message.AIResponseTextBlock.Text))
                        {
                            try
                            {
                                // Try to extract existing thinking from current JSON
                                var existingJson = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(message.AIResponseTextBlock.Text);
                                if (existingJson.TryGetProperty("thinking", out var existingThinkingProp))
                                {
                                    string existingThinking = existingThinkingProp.GetString();
                                    if (!string.IsNullOrEmpty(existingThinking))
                                    {
                                        thinkingToUse = existingThinking;
                                        LoggingService.Info($"[VoiceSessionPage] 💭 Preserved existing thinking from previous update - Length: {thinkingToUse.Length}");
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                LoggingService.Debug($"[VoiceSessionPage] Could not extract existing thinking: {ex.Message}");
                            }
                        }
                        
                        var responseJson = new
                        {
                            refined_text = refinedText,
                            thinking = thinkingToUse ?? "",
                            turnId = turnIdToDisplay
                        };

                        // Format as JSON string for display
                        responseText = System.Text.Json.JsonSerializer.Serialize(responseJson, new System.Text.Json.JsonSerializerOptions
                        {
                            WriteIndented = true
                        });
                        LoggingService.Info($"[VoiceSessionPage] 📝 Formatted response JSON (fallback) - Length: {responseText.Length}, Has thinking: {!string.IsNullOrEmpty(thinkingToUse)}");
                    }

                    // Check if AIResponseTextBlock exists, if not create it
                    if (message.AIResponseTextBlock == null)
                    {
                        LoggingService.Warn($"[VoiceSessionPage] ⚠️ AIResponseTextBlock is null - attempting to create it. MessageBorder: {message.MessageBorder != null}");
                        // Find the message border and create AI response block if it doesn't exist
                        if (message.MessageBorder?.Child is StackPanel messagePanel)
                        {
                            // Look for textBlocksContainer (should be the second child after headerPanel)
                            StackPanel textBlocksContainer = null;
                            if (messagePanel.Children.Count > 1 && messagePanel.Children[1] is StackPanel container)
                            {
                                textBlocksContainer = container;
                            }
                            else
                            {
                                // Create textBlocksContainer if it doesn't exist
                                textBlocksContainer = new StackPanel
                                {
                                    Orientation = Orientation.Vertical,
                                    Margin = new Thickness(0, 2, 0, 0)
                                };
                                messagePanel.Children.Add(textBlocksContainer);
                            }

                            // Create AI response block - use CSR cyan color to match avatar
                            Border aiResponseBlock = new Border
                            {
                                Background = new SolidColorBrush(Color.FromRgb(10, 30, 50)), // Dark cyan-tinted background
                                BorderBrush = new SolidColorBrush(Color.FromRgb(0, 212, 255)), // #00D4FF - Match CSR avatar
                                BorderThickness = new Thickness(1.5),
                                CornerRadius = new CornerRadius(8),
                                Padding = new Thickness(8, 6, 8, 6),
                                Margin = new Thickness(0, 0, 0, 4)
                            };
                            aiResponseBlock.Effect = new System.Windows.Media.Effects.DropShadowEffect
                            {
                                BlurRadius = 8,
                                ShadowDepth = 0,
                                Color = Color.FromRgb(0, 212, 255), // Cyan glow
                                Opacity = 0.2
                            };

                            StackPanel aiResponseContent = new StackPanel
                            {
                                Orientation = Orientation.Vertical
                            };

                            TextBox aiResponseTextBlock = new TextBox
                            {
                                Text = "",
                                FontSize = 12,
                                FontStyle = FontStyles.Normal,
                                Foreground = new SolidColorBrush(Color.FromRgb(139, 154, 175)), // #8B9AAF
                                Background = Brushes.Transparent,
                                BorderThickness = new Thickness(0),
                                IsReadOnly = true,
                                TextWrapping = TextWrapping.Wrap,
                                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                                Padding = new Thickness(0),
                                Margin = new Thickness(0)
                            };
                            aiResponseContent.Children.Add(aiResponseTextBlock);
                            aiResponseBlock.Child = aiResponseContent;
                            textBlocksContainer.Children.Add(aiResponseBlock);

                            // Store reference in message
                            message.AIResponseTextBlock = aiResponseTextBlock;
                            LoggingService.Info("[VoiceSessionPage] ✅ Created AIResponseTextBlock for contextual message");
                        }
                    }

                    // Update the yellow AI response box with JSON
                    if (message.AIResponseTextBlock != null)
                    {
                        LoggingService.Info($"[VoiceSessionPage] 🔄 Updating AIResponseTextBlock with response - Text length: {responseText?.Length ?? 0}, HasAIResponseBlock: true");
                        message.AIResponseTextBlock.Text = responseText;
                        message.HasAIResponse = true;

                        // Verify the text was set
                        if (message.AIResponseTextBlock.Text == responseText)
                        {
                            LoggingService.Info($"[VoiceSessionPage] ✅ AIResponseTextBlock.Text successfully updated. Current text preview: '{message.AIResponseTextBlock.Text?.Substring(0, Math.Min(100, message.AIResponseTextBlock.Text?.Length ?? 0))}...'");
                        }
                        else
                        {
                            LoggingService.Warn($"[VoiceSessionPage] ⚠️ AIResponseTextBlock.Text was not updated correctly. Expected length: {responseText?.Length ?? 0}, Actual length: {message.AIResponseTextBlock.Text?.Length ?? 0}");
                        }
                        
                        // Change the AI response block styling to indicate it's active (like UpdateAIResponse does)
                        // Structure: Border -> StackPanel -> TextBlock
                        // So we need to go up one level from TextBlock to get StackPanel, then up to Border
                        var aiResponseContent = message.AIResponseTextBlock.Parent as StackPanel;
                        var aiResponseBlock = aiResponseContent?.Parent as Border;
                        if (aiResponseBlock != null)
                        {
                            aiResponseBlock.Background = new SolidColorBrush(Color.FromRgb(10, 30, 50)); // Dark cyan-tinted background
                            aiResponseBlock.BorderBrush = new SolidColorBrush(Color.FromRgb(0, 212, 255)); // #00D4FF - Match CSR avatar
                            // Ensure text color is set to theme color
                            message.AIResponseTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(139, 154, 175)); // #8B9AAF
                            LoggingService.Info("[VoiceSessionPage] ✅ Updated AI response block styling for contextual message");
                        }
                        else
                        {
                            LoggingService.Warn($"[VoiceSessionPage] ⚠️ Could not find AI response Border to update styling - Parent structure may be different. Parent type: {message.AIResponseTextBlock.Parent?.GetType().Name ?? "null"}");
                        }
                }
                else
                {
                        LoggingService.Warn("[VoiceSessionPage] ⚠️ Cannot update contextual message - AIResponseTextBlock is still null after creation attempt");
                    }

                    // Update the message text as well
                    message.Text = refinedText;

                    ScrollViewerTranscription.ScrollToEnd();
                    LoggingService.Info($"[VoiceSessionPage] ✅ Updated contextual message with refined_text, thinking, and turnId: '{refinedText.Substring(0, Math.Min(50, refinedText.Length))}...', TurnId: {turnIdToDisplay}");
                });
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[VoiceSessionPage] Error updating contextual message: {ex.Message}");
            }
        }

        /// <summary>
        /// Trigger incoming call (shows ringing panel)
        /// </summary>
        public async Task TriggerIncomingCall()
        {
            try
            {
                LoggingService.Info("[VoiceSessionPage] Triggering incoming call with ringing...");
                await SimulateIncomingCallAsync();
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[VoiceSessionPage] Error triggering incoming call: {ex.Message}");
            }
        }

        public async Task StartNewTranscription()
        {
            var threadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            
            // Prevent multiple simultaneous start operations
            lock (_startLock)
            {
                if (_isStarting)
                {
                    return;
                }
                _isStarting = true;
            }
            
            // Prevent starting while disposing
            lock (_disposalLock)
            {
                if (_isDisposing)
                {
                    lock (_startLock)
                    {
                        _isStarting = false;
                    }
                    return;
                }
                
                // Create new cancellation token for this call session
                _frontendCancellationTokenSource?.Cancel();
                _frontendCancellationTokenSource?.Dispose();
                _frontendCancellationTokenSource = new CancellationTokenSource();
            }
            
            // Minimal delay to ensure any previous disposal has fully completed
            await Task.Delay(10);
            
            // Reset call started flag for new call
            _callStartedEventSent = false;

            // Reset sequential turnID counter for new call
            lock (_turnIdCounterLock)
            {
                _globalTurnIdCounter = 0;
            }

            // Clear turnID to source mapping for new call
            _turnIdToSourceMap.Clear();

            // Reset conversation state from any previous call to prevent stale references
            _currentConversationId = null;
            _currentAgentId = null;
            _lastCallerMessage = null;
            _lastAgentMessage = null;
            _backendDeltaAgentMessage = null;
            _backendDeltaAgentTurnId = null;
            _backendDeltaAgentText = "";
            _lastAgentThinking = null;
            _contextualTurnCounter = 0;

            // Clear contextual message tracking for new call
            _displayedContextualMessages.Clear();
            lock (_pendingContextualMessagesLock)
            {
                _pendingContextualMessages.Clear();
            }
            lock (_contextualMessageDataLock)
            {
                _contextualMessageData.Clear();
            }
            while (_contextualMessageQueue.TryDequeue(out _)) { }

            try
            {

                // CRITICAL: Clear transcription queues to prevent stale data from previous calls
                int micCleared = _micTranscriptionQueue.Count;
                int speakerCleared = _speakerTranscriptionQueue.Count;

                // Clear ConcurrentQueues by dequeuing all items
                while (_micTranscriptionQueue.TryDequeue(out _)) { }
                while (_speakerTranscriptionQueue.TryDequeue(out _)) { }

                // Cleared stale transcriptions (logging removed for performance)
                
                // Add call start separator to conversation and track start time
                _callStartTime = DateTime.Now;
                Dispatcher.Invoke(() =>
                {
                    HideIdlePanel();
                    StartCallDurationTimer();
                    CreateCallSeparator("Start Call", _callStartTime, true);
                });
                
                // Start call logging - check if enabled in configuration
                bool enableCallLogging = true; // Default to true
                try
                {
                    var configuration = new ConfigurationBuilder()
                        .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
                        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                        .Build();

                    var callLoggingSetting = configuration["CallLogging:EnableCallLogging"];
                    if (callLoggingSetting != null)
                    {
                        enableCallLogging = bool.Parse(callLoggingSetting);
                    }
                }
                catch (Exception ex)
                {
                    // Failed to read CallLogging setting, defaulting to enabled
                }

                if (enableCallLogging)
                {
                    // Recreate call logger if null
                    if (_callLogger == null)
                    {
                        _callLogger = new CallLoggerService();
                    }

                    string conversationId = _currentConversationId ?? "pending";
                    string callId = _callStartTime.ToString("yyyyMMdd-HHmmss");
                    _callLogger.StartCallLogging(conversationId, callId);
                }

                // Always create a fresh backend audio streaming session for each call
                if (_backendAudioStreamingService != null)
                {
                    try
                    {
                        // Unsubscribe from events
                        _backendAudioStreamingService.TranscriptReceived -= OnBackendTranscriptReceived;
                        _backendAudioStreamingService.AgentResponseReceived -= OnBackendAgentResponseReceived;
                        _backendAudioStreamingService.ErrorOccurred -= OnBackendErrorOccurred;
                        _backendAudioStreamingService.ConnectionStatusChanged -= OnBackendConnectionStatusChanged;
                        _backendAudioStreamingService.SessionStatusReceived -= OnBackendSessionStatusReceived;
                    _backendAudioStreamingService.AudioChunkLatencyMeasured -= OnAudioChunkLatencyMeasured;

                        await _backendAudioStreamingService.DisconnectAsync();
                        await Task.Delay(50);

                        _backendAudioStreamingService.Dispose();
                        _backendAudioStreamingService = null;
                        LoggingService.Info("[VoiceSessionPage] Previous BackendAudioStreamingService disposed");
                    }
                    catch (Exception cleanupEx)
                    {
                        LoggingService.Error($"[VoiceSessionPage] Error disposing previous BackendAudioStreamingService: {cleanupEx.Message}");
                        _backendAudioStreamingService = null;
                    }
                }

                // Ensure UI is initialized
                if (_audioRecorderVisualizer == null)
                {
                    await InitializeUIOnly();
                }

                // Load and apply audio device settings from appsettings.json
                LoadAndApplyAudioDeviceSettings();

                // Don't show waveforms or start recording yet - wait until backend connects
                // Waveforms and audio recording will be started in PollForConversationIdAndStartCall() after backend connects

                // Ensure audio recorder is stopped before starting (in case of restart)
                if (_audioRecorderVisualizer.IsRecording())
                {
                    _audioRecorderVisualizer.StopRecording();
                    // Allow time for NAudio RecordingStopped event handlers to fire and dispose devices
                    // This prevents race conditions where devices are still being disposed when the next call starts
                    await Task.Delay(300);
                }
                else
                {
                    // Even if not recording, allow a brief pause for any pending audio device cleanup
                    // from the previous call's StopRecording -> RecordingStopped -> Dispose chain
                    await Task.Delay(100);
                }

                // Clear waveform data during initialization (controls remain visible in cards)
                Dispatcher.Invoke(() =>
                {
                    AgentAvatarWaveform.ClearDisplay();
                    CustomerAvatarWaveform.ClearDisplay();
                });

                // Set provider to Backend in audio recorder
                Dispatcher.Invoke(() =>
                {
                    _audioRecorderVisualizer?.SetSpeechToTextProvider("Backend");
                });

                // Define cancellationToken in shared scope
                var cancellationToken = _frontendCancellationTokenSource?.Token ?? CancellationToken.None;

                // Initialize BackendAudioStreamingService
                LoggingService.Info($"[VoiceSessionPage] Initializing BackendAudioStreamingService... (BackendAccessToken available: {!string.IsNullOrEmpty(Globals.BackendAccessToken)})");

                var backendAudioTask = Task.Run(async () =>
                {
                    try
                    {
                        // Create service from configuration
                        _backendAudioStreamingService = BackendAudioStreamingService.CreateFromConfiguration();

                        // Wire up events for receiving transcripts, agent responses, errors, etc.
                        _backendAudioStreamingService.TranscriptReceived += OnBackendTranscriptReceived;
                        _backendAudioStreamingService.AgentResponseReceived += OnBackendAgentResponseReceived;
                        _backendAudioStreamingService.ErrorOccurred += OnBackendErrorOccurred;
                        _backendAudioStreamingService.ConnectionStatusChanged += OnBackendConnectionStatusChanged;
                        _backendAudioStreamingService.SessionStatusReceived += OnBackendSessionStatusReceived;
                        _backendAudioStreamingService.AudioChunkLatencyMeasured += OnAudioChunkLatencyMeasured;

                        // Connect to backend with JWT token
                        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                        {
                            bool connected = await _backendAudioStreamingService.ConnectAsync(Globals.BackendAccessToken).WaitAsync(cts.Token);
                            if (!connected)
                            {
                                LoggingService.Error("[VoiceSessionPage] BackendAudioStreamingService connection failed");
                                throw new Exception("Backend audio streaming connection failed");
                            }
                        }

                        // Send call_started event with call context
                        string callId = _ivrService?.GetCurrentCallId() ?? $"call_{Guid.NewGuid().ToString("N").Substring(0, 8)}";
                        string csrId = Globals.UserId;
                        if (string.IsNullOrEmpty(csrId))
                        {
                            var userDetails = SecureTokenStorage.RetrieveUserDetails();
                            csrId = userDetails?.Email?.ToString() ?? "unknown_user";
                        }
                        var customer = new
                        {
                            name = _ivrService?.CurrentCustomerName ?? "Unknown",
                            phone = _ivrService?.CurrentCustomerPhone ?? "+000000000",
                            account_id = _ivrService?.CurrentCustomerId ?? "unknown"
                        };
                        var employee = new
                        {
                            name = csrId,
                            id = csrId,
                            department = "support"
                        };
                        await _backendAudioStreamingService.SendCallStartedAsync(callId, csrId, customer, employee);

                        // Set the backend service in the audio recorder for routing
                        Dispatcher.Invoke(() =>
                        {
                            _audioRecorderVisualizer?.SetBackendAudioStreamingService(_backendAudioStreamingService);
                        });

                        LoggingService.Info("[VoiceSessionPage] BackendAudioStreamingService initialized and connected successfully");
                    }
                    catch (OperationCanceledException)
                    {
                        LoggingService.Warn("[VoiceSessionPage] BackendAudioStreamingService initialization timed out after 20 seconds");
                        throw new TimeoutException("Backend audio streaming initialization timed out");
                    }
                    catch (Exception ex)
                    {
                        LoggingService.Error($"[VoiceSessionPage] BackendAudioStreamingService initialization failed: {ex.Message}");
                        throw;
                    }
                }, cancellationToken);

                // Wait for backend audio service to connect (required for call to proceed)
                try
                {
                    await backendAudioTask;
                }
                catch (TimeoutException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LoggingService.Error($"[VoiceSessionPage] Backend audio initialization error: {ex.Message}");
                    throw;
                }

                // FRONTEND INTEGRATION: Start call session in background (non-blocking to avoid UI delay)
                _ = Task.Run(async () => await StartCallSessionAsync(cancellationToken), cancellationToken);
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[VoiceSessionPage] ❌ ERROR in StartNewTranscription: {ex.Message}");
                LoggingService.Error($"[VoiceSessionPage] Stack trace: {ex.StackTrace}");
                
                // Clean up the conversation history since call failed to start
                Dispatcher.Invoke(() =>
                {
                    StackPanelTranscription.Children.Clear();
                });
                
                // Stop audio recording if it was started
                try
                {
                    if (_audioRecorderVisualizer != null && _audioRecorderVisualizer.IsRecording())
                    {
                        _audioRecorderVisualizer.StopRecording();
                    }
                }
                catch (Exception stopEx)
                {
                    LoggingService.Warn($"[VoiceSessionPage] Error stopping audio after call failure: {stopEx.Message}");
                }
                
                // Reset UI state to allow retry
                Dispatcher.Invoke(() =>
                {
                    // Clear waveform data (controls remain visible in cards)
                    AgentAvatarWaveform.ClearDisplay();
                    CustomerAvatarWaveform.ClearDisplay();
                    
                    // Reset button state
                    if (Application.Current?.Dispatcher != null)
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            // This will be handled by the MainWindow's disposal event
                        });
                    }
                });
                
                // Show error message
                string errorMessage = ex is TimeoutException ? 
                    "Call initialization timed out. Please check your internet connection and try again." : 
                    $"Failed to start call: {ex.Message}";
                
                // Notify MainWindow that call start failed
                OnVoiceSessionCallStartFailed?.Invoke(this, errorMessage);
                    
                MessageBox.Show(errorMessage, "Call Start Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                // No delay needed - initialization tasks are already running in background
                // Always reset the starting flag
                lock (_startLock)
                {
                    _isStarting = false;
                }
            }
        }

        /// <summary>
        /// Stop transcription session (called when Disconnect button is pressed)
        /// </summary>
        public async Task StopTranscription()
        {
            // Prevent multiple simultaneous stop operations
            lock (_disposalLock)
            {
                if (_isDisposing)
                {
                    LoggingService.Info("[VoiceSessionPage] StopTranscription called while already disposing - ignoring");
                    return;
                }
                _isDisposing = true; // Set disposal state
                LoggingService.Info("[VoiceSessionPage] Disposal state set to TRUE - blocking new starts");
                
                // Cancel any ongoing frontend integration tasks
                try
                {
                    _frontendCancellationTokenSource?.Cancel();
                    LoggingService.Info("[VoiceSessionPage] Cancelled frontend integration background tasks");
                }
                catch (Exception cancelEx)
                {
                    LoggingService.Info($"[VoiceSessionPage] Error cancelling frontend tasks: {cancelEx.Message}");
                }
            }
            
            try
            {
                LoggingService.Info("[VoiceSessionPage] Stopping transcription session...");
                
                // Stop call logging
                if (_callLogger != null)
                {
                    _callLogger.StopCallLogging();
                    LoggingService.Info("[VoiceSessionPage] Call logging stopped");
                }
                else
                {
                    LoggingService.Warn("[VoiceSessionPage] ⚠️ Call logger was null when trying to stop - skipping call logging stop");
                }
                
                // Add call end separator to conversation
                var callDuration = DateTime.Now - _callStartTime;
                Dispatcher.Invoke(() =>
                {
                    CreateCallSeparator("End Call", DateTime.Now, false);
                    LoggingService.Info("[VoiceSessionPage] ✅ 'End Call' separator added to conversation");

                    // Record stats, stop timer, show idle panel
                    RecordCallStats(callDuration);
                    StopCallDurationTimer();
                    ShowIdlePanel();
                });

                // End call session state cleanup (synchronous to prevent race conditions with next call)
                EndCallSessionAsync();

                // Stop audio recording first
                if (_audioRecorderVisualizer != null)
                {
                    _audioRecorderVisualizer.StopRecording();
                    // Reset spectrum data to clear waveforms
                    _audioRecorderVisualizer.ResetSpectrumData();
                    // Clear audio buffers to prevent old audio from being processed
                    _audioRecorderVisualizer.ClearBuffers();
                    LoggingService.Info("[VoiceSessionPage] 🗑️ Audio recording stopped, spectrum cleared, and buffers flushed");
                }

                // Clear waveform data and hide latency indicator when call ends
                Dispatcher.Invoke(() =>
                {
                    AgentAvatarWaveform.ClearDisplay();
                    CustomerAvatarWaveform.ClearDisplay();
                    LatencyIndicatorPanel.Visibility = Visibility.Collapsed;
                    LoggingService.Info("[VoiceSessionPage] Waveforms cleared and latency indicator hidden after call end");
                });

                // Clear latency samples
                lock (_latencySamplesLock)
                {
                    _latencySamples.Clear();
                }

                // Stop and dispose BackendAudioStreamingService (fresh session for each call)
                if (_backendAudioStreamingService != null)
                {
                    // Unsubscribe from events first to prevent issues
                    _backendAudioStreamingService.TranscriptReceived -= OnBackendTranscriptReceived;
                    _backendAudioStreamingService.AgentResponseReceived -= OnBackendAgentResponseReceived;
                    _backendAudioStreamingService.ErrorOccurred -= OnBackendErrorOccurred;
                    _backendAudioStreamingService.ConnectionStatusChanged -= OnBackendConnectionStatusChanged;
                    _backendAudioStreamingService.SessionStatusReceived -= OnBackendSessionStatusReceived;
                    _backendAudioStreamingService.AudioChunkLatencyMeasured -= OnAudioChunkLatencyMeasured;
                    LoggingService.Info("[VoiceSessionPage] BackendAudioStreamingService events unsubscribed");

                    await _backendAudioStreamingService.DisconnectAsync();
                    LoggingService.Info("[VoiceSessionPage] BackendAudioStreamingService disconnected");

                    // Small delay to allow background tasks to complete before disposal
                    await Task.Delay(200);

                    // Dispose and clear reference
                    _backendAudioStreamingService.Dispose();
                    _backendAudioStreamingService = null;
                    LoggingService.Info("[VoiceSessionPage] BackendAudioStreamingService disposed and cleared");
                }

                LoggingService.Info("[VoiceSessionPage] Transcription session stopped successfully");
                
                // Clear disposal state and notify completion
                lock (_disposalLock)
                {
                    _isDisposing = false;
                    LoggingService.Info("[VoiceSessionPage] Disposal state set to FALSE - allowing new starts");
                }
                
                // Dispose cancellation token source
                try
                {
                    _frontendCancellationTokenSource?.Dispose();
                    _frontendCancellationTokenSource = null;
                    LoggingService.Info("[VoiceSessionPage] Frontend cancellation token source disposed");
                }
                catch (Exception tokenEx)
                {
                    LoggingService.Info($"[VoiceSessionPage] Error disposing cancellation token: {tokenEx.Message}");
                }
                
                // Small additional delay to ensure all background tasks are fully stopped
                await Task.Delay(100);
                
                DisposalCompleted?.Invoke();
                LoggingService.Info("[VoiceSessionPage] Disposal completed event fired");
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[VoiceSessionPage] Error stopping transcription: {ex.Message}");
                
                // Always clear disposal state and fire completion event to prevent UI from being stuck
                lock (_disposalLock)
                {
                    _isDisposing = false;
                    LoggingService.Info("[VoiceSessionPage] Disposal state set to FALSE (after error) - allowing new starts");
                }
                
                // Dispose cancellation token source even on error
                try
                {
                    _frontendCancellationTokenSource?.Dispose();
                    _frontendCancellationTokenSource = null;
                    LoggingService.Info("[VoiceSessionPage] Frontend cancellation token source disposed (after error)");
                }
                catch (Exception tokenEx)
                {
                    LoggingService.Info($"[VoiceSessionPage] Error disposing cancellation token (after error): {tokenEx.Message}");
                }
                
                // Small additional delay even on error to ensure cleanup
                await Task.Delay(100);
                
                DisposalCompleted?.Invoke();
                LoggingService.Info("[VoiceSessionPage] Disposal completed event fired (after error)");
            }
        }

        private void ResetGUI()
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                ProgressBarForSummaryGeneration.Visibility = Visibility.Hidden;
                // StackPanelButtonsArea removed - buttons now in main window
            }));
        }

        /// <summary>
        /// Clears the conversation history and UI
        /// </summary>
        public void ClearConversationHistory()
        {
            try
            {
                // Clear conversation history
                _conversationHistory.Clear();
                _lastCallerMessage = null;
                _lastAgentMessage = null;
                _backendDeltaAgentMessage = null;
                _backendDeltaAgentTurnId = null;
                _backendDeltaAgentText = "";
                
                // Clear UI
                StackPanelTranscription.Children.Clear();
                
                LoggingService.Info("[VoiceSessionPage] Conversation history cleared for new session");
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[VoiceSessionPage] Error clearing conversation history: {ex.Message}");
            }
        }

        // =====================================================================
        // Idle Panel & Stats Tracking
        // =====================================================================

        /// <summary>
        /// Shows the idle welcome panel when no call is active.
        /// Called on page load and after a call ends.
        /// </summary>
        private void ShowIdlePanel()
        {
            try
            {
                IdleWelcomePanel.Visibility = Visibility.Visible;
                _isCallActive = false;
                ActiveCallIndicator.Visibility = Visibility.Collapsed;
                UpdateStatsDisplay();
                LoggingService.Info("[VoiceSessionPage] Idle welcome panel shown");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[VoiceSessionPage] Error showing idle panel: {ex.Message}");
            }
        }

        /// <summary>
        /// Hides the idle welcome panel when a call becomes active.
        /// </summary>
        private void HideIdlePanel()
        {
            try
            {
                IdleWelcomePanel.Visibility = Visibility.Collapsed;
                _isCallActive = true;
                ActiveCallIndicator.Visibility = Visibility.Visible;
                CallDurationText.Text = "00:00";
                LoggingService.Info("[VoiceSessionPage] Idle welcome panel hidden - call active");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[VoiceSessionPage] Error hiding idle panel: {ex.Message}");
            }
        }

        /// <summary>
        /// Updates the stats cards on the idle panel with current session data.
        /// </summary>
        private void UpdateStatsDisplay()
        {
            try
            {
                StatsCallsAccepted.Text = _callsAcceptedCount.ToString();
                StatsCallsRejected.Text = _callsRejectedCount.ToString();

                if (_callDurations.Count > 0)
                {
                    var avgTicks = _callDurations.Average(d => d.Ticks);
                    var avgDuration = TimeSpan.FromTicks((long)avgTicks);
                    StatsAvgDuration.Text = FormatDuration(avgDuration);
                }
                else
                {
                    StatsAvgDuration.Text = "--:--";
                }

                if (_sessionLatencySamples.Count > 0)
                {
                    var avgLatency = _sessionLatencySamples.Average();
                    StatsAvgLatency.Text = $"{avgLatency:F0} ms";
                }
                else
                {
                    StatsAvgLatency.Text = "-- ms";
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[VoiceSessionPage] Error updating stats display: {ex.Message}");
            }
        }

        /// <summary>
        /// Records the completed call's stats for the idle panel.
        /// </summary>
        private void RecordCallStats(TimeSpan callDuration)
        {
            try
            {
                _callsAcceptedCount++;
                _callDurations.Add(callDuration);

                // Capture the session's average latency from the rolling window
                lock (_latencySamplesLock)
                {
                    if (_latencySamples.Count > 0)
                    {
                        double sum = 0;
                        foreach (var s in _latencySamples) sum += s;
                        _sessionLatencySamples.Add(sum / _latencySamples.Count);
                    }
                }

                LoggingService.Info($"[VoiceSessionPage] Call stats recorded - Calls accepted: {_callsAcceptedCount}, Duration: {FormatDuration(callDuration)}");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[VoiceSessionPage] Error recording call stats: {ex.Message}");
            }
        }

        /// <summary>
        /// Starts the call duration timer that updates CallDurationText every second.
        /// </summary>
        private void StartCallDurationTimer()
        {
            try
            {
                _callDurationTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(1)
                };
                _callDurationTimer.Tick += (s, e) =>
                {
                    try
                    {
                        if (_callStartTime != default(DateTime))
                        {
                            var elapsed = DateTime.Now - _callStartTime;
                            CallDurationText.Text = FormatDuration(elapsed);
                        }
                    }
                    catch { }
                };
                _callDurationTimer.Start();
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[VoiceSessionPage] Error starting call duration timer: {ex.Message}");
            }
        }

        /// <summary>
        /// Stops the call duration timer.
        /// </summary>
        private void StopCallDurationTimer()
        {
            try
            {
                if (_callDurationTimer != null)
                {
                    _callDurationTimer.Stop();
                    _callDurationTimer = null;
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[VoiceSessionPage] Error stopping call duration timer: {ex.Message}");
            }
        }

        /// <summary>
        /// Creates a call session separator (Start Call / End Call / Call Reject with timestamp and duration)
        /// </summary>
        private void CreateCallSeparator(string title, DateTime timestamp, bool isCallStart = true, bool showDuration = true)
        {
            try
            {
                // Determine dot color and increment per-type counter
                bool isReject = title.Contains("Reject", StringComparison.OrdinalIgnoreCase);
                Color dotColor;
                int dotCount;

                if (isCallStart)
                {
                    _callSequenceNumber++;
                    dotCount = _callSequenceNumber;
                    dotColor = Color.FromRgb(39, 174, 96);   // #27AE60 green
                }
                else if (isReject)
                {
                    _callSequenceNumber++;
                    dotCount = _callSequenceNumber;
                    dotColor = Color.FromRgb(243, 156, 18);  // #F39C12 amber for reject
                }
                else
                {
                    // End Call: reuse the current sequence number (same call that was started)
                    dotCount = _callSequenceNumber;
                    dotColor = Color.FromRgb(231, 76, 60);   // #E74C3C red for end
                }

                // Timeline event container: [timeline dot + line] | [event content card]
                Grid timelineGrid = new Grid
                {
                    Margin = new Thickness(10, 6, 10, 6),
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                timelineGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
                timelineGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                // --- Timeline column: vertical line + colored dot ---
                Grid timelineColumn = new Grid { VerticalAlignment = VerticalAlignment.Stretch };

                // Vertical line (stretches full height)
                System.Windows.Shapes.Rectangle timelineLine = new System.Windows.Shapes.Rectangle
                {
                    Width = 2,
                    Fill = new SolidColorBrush(Color.FromRgb(58, 74, 100)), // #3A4A64
                    VerticalAlignment = VerticalAlignment.Stretch,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                timelineColumn.Children.Add(timelineLine);

                // Circular dot with count badge at the event node
                Grid dotBadge = new Grid
                {
                    Width = 20,
                    Height = 20,
                    VerticalAlignment = VerticalAlignment.Top,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 6, 0, 0)
                };

                System.Windows.Shapes.Ellipse timelineDot = new System.Windows.Shapes.Ellipse
                {
                    Width = 20,
                    Height = 20,
                    Fill = new SolidColorBrush(dotColor),
                    Stroke = new SolidColorBrush(Color.FromRgb(19, 35, 64)), // #132340 matches panel bg
                    StrokeThickness = 2,
                    Effect = new System.Windows.Media.Effects.DropShadowEffect
                    {
                        BlurRadius = 6,
                        ShadowDepth = 0,
                        Color = dotColor,
                        Opacity = 0.5
                    }
                };
                dotBadge.Children.Add(timelineDot);

                TextBlock dotCountText = new TextBlock
                {
                    Text = dotCount.ToString(),
                    FontSize = 9,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Colors.White),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    IsHitTestVisible = false
                };
                dotBadge.Children.Add(dotCountText);

                timelineColumn.Children.Add(dotBadge);

                Grid.SetColumn(timelineColumn, 0);
                timelineGrid.Children.Add(timelineColumn);

                // --- Content column: event details card ---
                Border contentBorder = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(26, 41, 66)),  // #1A2942
                    BorderBrush = new SolidColorBrush(Color.FromRgb(58, 74, 100)), // #3A4A64
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12, 8, 12, 8),
                    Margin = new Thickness(5, 0, 0, 0)
                };

                StackPanel contentPanel = new StackPanel { Orientation = Orientation.Vertical };

                // Event title (bold, white)
                contentPanel.Children.Add(new TextBlock
                {
                    Text = title,
                    FontWeight = FontWeights.Bold,
                    FontSize = 13,
                    Foreground = new SolidColorBrush(Colors.White),
                    Margin = new Thickness(0, 0, 0, 3)
                });

                // Timestamp (smaller, muted)
                contentPanel.Children.Add(new TextBlock
                {
                    Text = timestamp.ToString("hh:mm:ss tt"),
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(139, 154, 175)) // #8B9AAF
                });

                // Duration (end call only, highlighted in cyan)
                if (!isCallStart && showDuration && _callStartTime != default(DateTime))
                {
                    try
                    {
                        var duration = timestamp - _callStartTime;
                        if (duration.TotalSeconds > 0)
                        {
                            contentPanel.Children.Add(new TextBlock
                            {
                                Text = $"Duration: {FormatDuration(duration)}",
                                FontSize = 12,
                                FontWeight = FontWeights.SemiBold,
                                Foreground = new SolidColorBrush(Color.FromRgb(0, 212, 255)), // #00D4FF
                                Margin = new Thickness(0, 4, 0, 0)
                            });
                        }
                    }
                    catch (Exception durationEx)
                    {
                        LoggingService.Error(durationEx, "[VoiceSessionPage] Error calculating call duration: {0}", durationEx.Message);
                    }
                }

                contentBorder.Child = contentPanel;
                Grid.SetColumn(contentBorder, 1);
                timelineGrid.Children.Add(contentBorder);

                // Add to transcription panel
                StackPanelTranscription.Children.Add(timelineGrid);
                ScrollViewerTranscription.ScrollToBottom();

                LoggingService.Info($"[VoiceSessionPage] Added timeline call separator: {title} at {timestamp:HH:mm:ss}");
            }
            catch (Exception ex)
            {
                LoggingService.Error(ex, "[VoiceSessionPage] Error creating call separator: {0}", ex.Message);
            }
        }

        /// <summary>
        /// Formats a TimeSpan duration into a human-readable string
        /// </summary>
        private string FormatDuration(TimeSpan duration)
        {
            if (duration.TotalHours >= 1)
            {
                return $"{(int)duration.TotalHours:D2}:{duration.Minutes:D2}:{duration.Seconds:D2}";
            }
            else
            {
                return $"{duration.Minutes:D2}:{duration.Seconds:D2}";
            }
        }

        /// <summary>
        /// Creates a new message in the conversation
        /// </summary>
        private ConversationMessage CreateNewMessage(string transcriptionText, bool isCustomer, bool isAIResponse = false)
        {
            try
            {
                LoggingService.Info($"[VoiceSessionPage] 📝 CreateNewMessage called - Text: '{transcriptionText?.Substring(0, Math.Min(30, transcriptionText?.Length ?? 0))}...', isCustomer: {isCustomer}, isAIResponse: {isAIResponse}");
                LoggingService.Info($"[VoiceSessionPage] 📝 StackPanelTranscription exists: {StackPanelTranscription != null}");
                // Create message container with theme-appropriate colors
                // Use dark background with colored border matching avatar colors
                Color borderColor = isCustomer ? Color.FromRgb(245, 158, 11) : Color.FromRgb(0, 212, 255); // Amber for caller, Cyan for CSR
                Color backgroundColor = isCustomer ? Color.FromRgb(20, 15, 10) : Color.FromRgb(10, 20, 30); // Dark amber-tinted for caller, dark cyan-tinted for CSR
                
                Border messageBorder = new Border
                {
                    Background = new SolidColorBrush(backgroundColor),
                    BorderBrush = new SolidColorBrush(borderColor),
                    BorderThickness = new Thickness(1.5),
                    CornerRadius = new CornerRadius(15),
                    Margin = new Thickness(0, 5, 0, 5),
                    HorizontalAlignment = isCustomer ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                    MaxWidth = _MessageMaxWidth,
                    Padding = new Thickness(12, 8, 12, 8)
                };
                
                // Add subtle glow effect matching the border color
                messageBorder.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 8,
                    ShadowDepth = 0,
                    Color = borderColor,
                    Opacity = 0.15
                };

                // Create message content panel
                StackPanel messagePanel = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };

                // Create header with icon and role
                StackPanel headerPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 0, 0, 5)
                };

                // Create oval icon with same style as main avatars
                Grid iconGrid = new Grid
                {
                    Width = 50,
                    Height = 30,
                    Margin = new Thickness(0, 0, 8, 0),
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                
                // Create the ellipse background
                Ellipse iconEllipse = new Ellipse
                {
                    Width = 50,
                    Height = 30,
                    Fill = new SolidColorBrush(isCustomer ? Color.FromRgb(245, 158, 11) : (isAIResponse ? Color.FromRgb(0, 212, 255) : Color.FromRgb(0, 212, 255))), // #F59E0B for caller, #00D4FF for CSR/AI
                    Stroke = new SolidColorBrush(isCustomer ? Color.FromRgb(217, 119, 6) : (isAIResponse ? Color.FromRgb(0, 168, 204) : Color.FromRgb(0, 168, 204))), // #D97706 for caller, #00A8CC for CSR/AI
                    StrokeThickness = 2
                };
                
                // Add drop shadow effect like main avatars
                iconEllipse.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 6,
                    ShadowDepth = 1,
                    Color = isCustomer ? Color.FromRgb(245, 158, 11) : (isAIResponse ? Color.FromRgb(0, 212, 255) : Color.FromRgb(0, 212, 255)), // Match avatar colors
                    Opacity = 0.3
                };
                
                iconGrid.Children.Add(iconEllipse);

                TextBlock iconText = new TextBlock
                {
                    Text = isCustomer ? "CALLER" : (isAIResponse ? "AI" : "CSR"),
                    Foreground = new SolidColorBrush(Colors.White),
                    FontWeight = FontWeights.Bold,
                    FontSize = isCustomer ? 10 : (isAIResponse ? 14 : 10),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 2, 4, 2),
                    Padding = new Thickness(0)
                };
                iconGrid.Children.Add(iconText);
                headerPanel.Children.Add(iconGrid);

                // Create role and time text
                TextBlock roleText = new TextBlock
                {
                    Text = "",
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(borderColor), // Use border color (amber for caller, cyan for CSR)
                    VerticalAlignment = VerticalAlignment.Center
                };
                headerPanel.Children.Add(roleText);

                // Add time - use light gray for dark theme
                TextBlock timeText = new TextBlock
                {
                    Text = $" {DateTime.Now.ToString("hh:mm tt")}",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(139, 154, 175)), // #8B9AAF - Light gray for dark theme
                    VerticalAlignment = VerticalAlignment.Center
                };
                headerPanel.Children.Add(timeText);

                messagePanel.Children.Add(headerPanel);

                // Create dual text blocks container
                StackPanel textBlocksContainer = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    Margin = new Thickness(0, 2, 0, 0)
                };

                // First block: Transcription text (what was spoken)
                // Use speaker's color: amber for caller, cyan for CSR
                Color transcriptionBorderColor = isCustomer ? Color.FromRgb(245, 158, 11) : Color.FromRgb(0, 212, 255); // #F59E0B or #00D4FF
                Color transcriptionGlowColor = isCustomer ? Color.FromRgb(245, 158, 11) : Color.FromRgb(0, 212, 255);
                
                Border transcriptionBlock = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(15, 23, 42)), // Darker background
                    BorderBrush = new SolidColorBrush(transcriptionBorderColor),
                    BorderThickness = new Thickness(1.5),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(8, 6, 8, 6),
                    Margin = new Thickness(0, 0, 0, 4)
                };
                transcriptionBlock.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 8,
                    ShadowDepth = 0,
                    Color = transcriptionGlowColor,
                    Opacity = 0.2
                };

                StackPanel transcriptionContent = new StackPanel
                {
                    Orientation = Orientation.Vertical
                };

                // No label needed - badge shows speaker identity

                TextBox transcriptionTextBlock = new TextBox
                {
                    Text = transcriptionText,
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Colors.White),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    IsReadOnly = true,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Padding = new Thickness(0),
                    Margin = new Thickness(0)
                };
                transcriptionContent.Children.Add(transcriptionTextBlock);

                transcriptionBlock.Child = transcriptionContent;
                textBlocksContainer.Children.Add(transcriptionBlock);

                // Second block: AI agent response - use CSR cyan color to match avatar
                Border aiResponseBlock = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(10, 30, 50)), // Dark cyan-tinted background
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0, 212, 255)), // #00D4FF - Match CSR avatar
                    BorderThickness = new Thickness(1.5),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(8, 6, 8, 6),
                    Margin = new Thickness(0, 0, 0, 4)
                };
                aiResponseBlock.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 8,
                    ShadowDepth = 0,
                    Color = Color.FromRgb(0, 212, 255), // Cyan glow to match CSR avatar
                    Opacity = 0.2
                };

                StackPanel aiResponseContent = new StackPanel
                {
                    Orientation = Orientation.Vertical
                };

                // No label needed - badge shows speaker identity

                TextBox aiResponseTextBlock = new TextBox
                {
                    Text = "",
                    FontSize = 12,
                    FontStyle = FontStyles.Normal,
                    Foreground = new SolidColorBrush(Color.FromRgb(139, 154, 175)), // #8B9AAF
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    IsReadOnly = true,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Padding = new Thickness(0),
                    Margin = new Thickness(0)
                };
                aiResponseContent.Children.Add(aiResponseTextBlock);

                aiResponseBlock.Child = aiResponseContent;
                textBlocksContainer.Children.Add(aiResponseBlock);

                messagePanel.Children.Add(textBlocksContainer);

                messageBorder.Child = messagePanel;

                // Add the message to the transcription panel
                StackPanelTranscription.Children.Add(messageBorder);
                LoggingService.Info($"[VoiceSessionPage] 📝 Message added to StackPanel - Total children: {StackPanelTranscription.Children.Count}");

                // Create and store conversation message
                var conversationMessage = new ConversationMessage
                {
                    Text = transcriptionText,
                    IsCaller = isCustomer,
                    Timestamp = DateTime.Now,
                    MessageBorder = messageBorder,
                    MessageTextBlock = null, // Legacy compatibility - TextBox doesn't convert to TextBlock
                    FullText = transcriptionText,
                    
                    // Store references to dual text blocks
                    TranscriptionTextBlock = transcriptionTextBlock,
                    AIResponseTextBlock = aiResponseTextBlock,
                    HasAIResponse = false
                };

                // Add to conversation history
                _conversationHistory.Add(conversationMessage);

                // Update last message references based on speaker type
                if (isCustomer)
                {
                    _lastCallerMessage = conversationMessage;
                }
                else if (isAIResponse)
                {
                    // For AI responses, we don't update _lastAgentMessage since it's not a human agent
                    // AI responses are tracked separately in conversation history
                }
                else
                {
                    _lastAgentMessage = conversationMessage;
                }

                // Scroll to the bottom
                ScrollViewerTranscription.ScrollToBottom();
                
                string messageType = isCustomer ? "caller" : (isAIResponse ? "AI agent" : "agent");
                LoggingService.Debug($"[VoiceSessionPage] Created new {messageType} message: '{transcriptionText}'");
                return conversationMessage; // Return the created message
            }
            catch (Exception ex)
            {
                LoggingService.Error(ex, "[VoiceSessionPage] Error creating new message: {0}", ex.Message);
                return null; // Return null on error
            }
        }
      
        /// <summary>
        /// Get the AudioRecorderVisualizer instance (for external access, e.g., from MainWindow tray menu)
        /// </summary>
        internal AudioRecorderVisualizer GetAudioRecorderVisualizer()
        {
            return _audioRecorderVisualizer;
        }
        
        public async void DisposeVoiceSessionPage()
        {
            try
            {
                // Mark as disposing so that the Unloaded event handler performs full cleanup
                _isDisposing = true;
                // Also reset the initialization flag so a fresh page starts clean
                _hasBeenInitialized = false;

                LoggingService.Info("[VoiceSessionPage] Disposing VoiceSessionPage - stopping all services...");

                // Cancel frontend integration background tasks
                try
                {
                    _frontendCancellationTokenSource?.Cancel();
                    _frontendCancellationTokenSource?.Dispose();
                    _frontendCancellationTokenSource = null;
                }
                catch (Exception ctsEx)
                {
                    LoggingService.Warn($"[VoiceSessionPage] Error disposing cancellation token during disposal: {ctsEx.Message}");
                }

                // Unsubscribe IVR events to prevent callbacks during disposal
                if (_ivrService != null)
                {
                    try
                    {
                        _ivrService.CallAnswered -= OnIVRCallAnswered;
                        _ivrService.CallRejected -= OnIVRCallRejected;
                        _ivrService.IncomingCallRinging -= OnIVRIncomingCallRinging;
                        LoggingService.Info("[VoiceSessionPage] IVR events unsubscribed during disposal");
                    }
                    catch (Exception ivrEx)
                    {
                        LoggingService.Warn($"[VoiceSessionPage] Error unsubscribing IVR events: {ivrEx.Message}");
                    }
                }

                // Stop BackendAudioStreamingService
                if (_backendAudioStreamingService != null)
                {
                    LoggingService.Info("[VoiceSessionPage] Stopping BackendAudioStreamingService...");
                    // Unsubscribe events before disconnect to prevent callbacks during disposal
                    _backendAudioStreamingService.TranscriptReceived -= OnBackendTranscriptReceived;
                    _backendAudioStreamingService.AgentResponseReceived -= OnBackendAgentResponseReceived;
                    _backendAudioStreamingService.ErrorOccurred -= OnBackendErrorOccurred;
                    _backendAudioStreamingService.ConnectionStatusChanged -= OnBackendConnectionStatusChanged;
                    _backendAudioStreamingService.SessionStatusReceived -= OnBackendSessionStatusReceived;
                    _backendAudioStreamingService.AudioChunkLatencyMeasured -= OnAudioChunkLatencyMeasured;
                    try
                    {
                        await _backendAudioStreamingService.DisconnectAsync();
                    }
                    catch (Exception disconnectEx)
                    {
                        LoggingService.Warn($"[VoiceSessionPage] Error disconnecting BackendAudioStreamingService: {disconnectEx.Message}");
                    }
                    _backendAudioStreamingService.Dispose();
                    _backendAudioStreamingService = null;
                    LoggingService.Info("[VoiceSessionPage] BackendAudioStreamingService stopped and disposed");
                }

                // Stop audio recorder
                if (_audioRecorderVisualizer != null)
                {
                    LoggingService.Info("[VoiceSessionPage] Stopping audio recorder...");
                    _audioRecorderVisualizer.StopCapture();
                    LoggingService.Info("[VoiceSessionPage] Audio recorder stopped");
                }

                // Clear waveform data during disposal (controls remain visible in cards)
                Dispatcher.Invoke(() =>
                {
                    AgentAvatarWaveform.ClearDisplay();
                    CustomerAvatarWaveform.ClearDisplay();
                    LoggingService.Info("[VoiceSessionPage] Waveforms cleared during disposal");
                });

                // Clear audio chunks
                AudioChunkholderUtility.ClearChunkHolder();
                LoggingService.Info("[VoiceSessionPage] Audio chunks cleared");

                LoggingService.Info("[VoiceSessionPage] VoiceSessionPage disposal completed");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[VoiceSessionPage] Error during DisposeVoiceSessionPage: {ex.Message}");
            }
        }

        private void ClosePopup_Click(object sender, RoutedEventArgs e)
        {
            CloudPopup.IsOpen = false;
            PopupText.Text = string.Empty;
        }

        private async void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            // Only run full initialization on the very first load.
            // Subsequent loads (after navigating away to Settings/History and back)
            // must NOT clear the call logs or re-initialize from scratch.
            if (!_hasBeenInitialized)
            {
                LoggingService.Info("[VoiceSessionPage] First load - running full InitializeUIOnly");
                await InitializeUIOnly();
                _hasBeenInitialized = true;
            }
            else
            {
                LoggingService.Info("[VoiceSessionPage] Page re-loaded after navigation - skipping InitializeUIOnly to preserve call logs");

                // Re-initialize lightweight services that were cleaned up during Unloaded,
                // but do NOT clear conversation history or the StackPanelTranscription.
                if (_audioRecorderVisualizer == null)
                {
                    LoggingService.Info("[VoiceSessionPage] Re-creating AudioRecorderVisualizer after navigation");
                    AudioChunkholderUtility.ClearChunkHolder();
                    _audioRecorderVisualizer = new AudioRecorderVisualizer(AgentAvatarWaveform);
                    _audioRecorderVisualizer.NumberOfLines = 50;
                    _audioRecorderVisualizer.ResetSpectrumData();
                    _audioRecorderVisualizer.SetMicSpectrumVisualizer(AgentAvatarWaveform);
                    _audioRecorderVisualizer.SetSpeakerSpectrumVisualizer(CustomerAvatarWaveform);
                    _audioRecorderVisualizer.SetAgentAvatarVisualizer(AgentAvatarWaveform);
                    _audioRecorderVisualizer.SetCustomerAvatarVisualizer(CustomerAvatarWaveform);
                }

                if (_callLogger == null)
                {
                    LoggingService.Info("[VoiceSessionPage] Re-creating CallLoggerService after navigation");
                    _callLogger = new CallLoggerService();
                }
            }

            // NOTE: Legacy STT service initialization removed - backend audio streaming replaces all STT services
            // Backend API health check is now managed by MainWindow for the title bar indicator

            // ProgressBarForSummaryGeneration temporarily disabled for LiveKit testing
            // ProgressBarForSummaryGeneration.Visibility = Visibility.Hidden;
        }


        // Backend API health check and connection status monitoring have been moved to MainWindow.
        // The BE WS indicator now lives in the MainWindow title bar.

        /// <summary>
        /// Event handler for Close Conversation Status Popup button
        /// </summary>
        private void CloseConversationStatusPopup_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                LoggingService.Info("[VoiceSessionPage] Close Conversation Status Popup button clicked");
                // Close the conversation status popup
                CloseConversationStatusPopup();
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[VoiceSessionPage] Error closing conversation status popup: {ex.Message}");
            }
        }

        /// <summary>
        /// Close the conversation status popup
        /// </summary>
        private void CloseConversationStatusPopup()
        {
            try
            {
                // Implementation depends on how the popup is implemented
                // For now, just log the action
                LoggingService.Info("[VoiceSessionPage] Conversation status popup closed");
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[VoiceSessionPage] Error closing popup: {ex.Message}");
            }
        }

        #region Audio Device Management

        /// <summary>
        /// Load audio device settings from appsettings.json and apply them to AudioRecorderVisualizer
        /// </summary>
        private void LoadAndApplyAudioDeviceSettings()
        {
            try
            {
                if (Globals.ConfigurationInfo == null)
                {
                    LoggingService.Warn("[VoiceSessionPage] Configuration not loaded, using default audio devices");
                    return;
                }

                var audioDevicesSection = Globals.ConfigurationInfo.GetSection("AudioDevices");
                if (audioDevicesSection.Exists())
                {
                    var selectedMicDevice = audioDevicesSection["SelectedMicrophoneDevice"] ?? "";
                    var selectedSpeakerDevice = audioDevicesSection["SelectedSpeakerDevice"] ?? "";
                    var micDeviceIndex = audioDevicesSection.GetValue<int>("MicrophoneDeviceIndex", 0);
                    var speakerDeviceIndex = audioDevicesSection.GetValue<int>("SpeakerDeviceIndex", 0);
                    var devModeSystemAudioOnly = audioDevicesSection.GetValue<bool>("DevModeSystemAudioOnly", false);

                    LoggingService.Info($"[VoiceSessionPage] Loading audio device settings - Mic: {selectedMicDevice} (Index: {micDeviceIndex}), Speaker: {selectedSpeakerDevice} (Index: {speakerDeviceIndex})");
                    LoggingService.Info($"[VoiceSessionPage] DevMode System Audio Only: {devModeSystemAudioOnly}");

                    // Apply DevMode setting first (before device settings)
                    if (_audioRecorderVisualizer != null)
                    {
                        _audioRecorderVisualizer.SetDevModeSystemAudioOnly(devModeSystemAudioOnly);
                    }

                    // Apply microphone device settings (only if DevMode is not enabled)
                    if (!string.IsNullOrEmpty(selectedMicDevice) && _audioRecorderVisualizer != null && !devModeSystemAudioOnly)
                    {
                        _audioRecorderVisualizer.SetMicrophoneDevice(micDeviceIndex, selectedMicDevice);
                        LoggingService.Info($"[VoiceSessionPage] Applied microphone device: {selectedMicDevice} (Index: {micDeviceIndex})");
                    }
                    else if (devModeSystemAudioOnly)
                    {
                        LoggingService.Info("[VoiceSessionPage] DevMode enabled - Skipping microphone device setup");
                    }

                    // Apply speaker device settings
                    if (!string.IsNullOrEmpty(selectedSpeakerDevice) && _audioRecorderVisualizer != null)
                    {
                        _audioRecorderVisualizer.SetSpeakerDevice(speakerDeviceIndex, selectedSpeakerDevice);
                        LoggingService.Info($"[VoiceSessionPage] Applied speaker device: {selectedSpeakerDevice} (Index: {speakerDeviceIndex})");
                    }
                }
                else
                {
                    LoggingService.Info("[VoiceSessionPage] No audio device settings found in appsettings.json, using defaults");
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[VoiceSessionPage] Error loading audio device settings: {ex.Message}");
            }
        }

        #endregion

        #region Backend Audio Streaming Event Handlers

        /// <summary>
        /// Handle transcript messages received from the backend audio streaming service.
        /// Deserializes the full BackendTranscriptMessage to use all available fields:
        /// text, is_final, source, speaker, timestamp, turn_id, confidence.
        /// </summary>
        private void OnBackendTranscriptReceived(string text, bool isFinal, string rawJson)
        {
            try
            {
                if (string.IsNullOrEmpty(text)) return;

                // Deserialize the raw JSON into the strongly-typed BackendTranscriptMessage model
                // to access all fields (source, speaker, timestamp, turn_id, confidence)
                Models.BackendTranscriptMessage transcriptMsg = null;
                try
                {
                    if (!string.IsNullOrEmpty(rawJson))
                    {
                        transcriptMsg = System.Text.Json.JsonSerializer.Deserialize<Models.BackendTranscriptMessage>(rawJson);
                    }
                }
                catch (Exception parseEx)
                {
                    LoggingService.Warn($"[VoiceSessionPage] Failed to deserialize BackendTranscriptMessage, using fallback defaults: {parseEx.Message}");
                }

                // Extract fields from deserialized model with sensible defaults
                string source = transcriptMsg?.Source ?? "speaker";
                string speaker = transcriptMsg?.Speaker ?? "customer";
                string backendTurnId = transcriptMsg?.TurnId;
                long backendTimestamp = transcriptMsg?.Timestamp ?? 0;
                double? confidence = transcriptMsg?.Confidence;

                // Determine if the transcript is from the customer or the agent (CSR)
                // Primary: use the "speaker" field ("customer" or "agent") from the backend
                // Fallback: use the "source" field ("speaker" = customer audio, "microphone" = agent audio)
                bool isCustomer;
                if (!string.IsNullOrEmpty(speaker))
                {
                    isCustomer = string.Equals(speaker, "customer", StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    isCustomer = string.Equals(source, "speaker", StringComparison.OrdinalIgnoreCase);
                }

                // Log with all available diagnostic info
                string confidenceStr = confidence.HasValue ? $", confidence: {confidence.Value:F2}" : "";
                string timestampStr = backendTimestamp > 0 ? $", ts: {backendTimestamp}" : "";
                LoggingService.Info($"[VoiceSessionPage] Backend transcript ({(isFinal ? "final" : "partial")}): " +
                    $"speaker={speaker}, source={source}, isCustomer={isCustomer}{confidenceStr}{timestampStr} - " +
                    $"{text.Substring(0, Math.Min(80, text.Length))}");

                // Only process final transcripts for display (partial transcripts are informational only)
                if (isFinal)
                {
                    // Use backend-provided turn_id if available, otherwise generate a local one
                    string turnId;
                    if (!string.IsNullOrEmpty(backendTurnId))
                    {
                        turnId = backendTurnId;
                        LoggingService.Info($"[VoiceSessionPage] Using backend turn_id: {turnId}");
                    }
                    else
                    {
                        lock (_turnIdCounterLock)
                        {
                            _globalTurnIdCounter++;
                            turnId = _globalTurnIdCounter.ToString("D3");
                        }
                        LoggingService.Info($"[VoiceSessionPage] Generated local turn_id: {turnId}");
                    }

                    // Register in turnId-to-source map
                    string sourceLabel = isCustomer ? "Caller" : "CSR";
                    _turnIdToSourceMap.TryAdd(turnId, sourceLabel);

                    // Format the text with turn ID
                    string formattedText = $"[{turnId}] {text}";

                    // Convert backend timestamp (Unix ms) to DateTime if available, otherwise use current time
                    DateTime messageTimestamp = DateTime.Now;
                    if (backendTimestamp > 0)
                    {
                        try
                        {
                            messageTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(backendTimestamp).LocalDateTime;
                        }
                        catch
                        {
                            // If timestamp conversion fails, fall back to current time
                            messageTimestamp = DateTime.Now;
                        }
                    }

                    // Capture confidence for use inside dispatcher
                    double? capturedConfidence = confidence;
                    DateTime capturedTimestamp = messageTimestamp;

                    // Create UI message on the dispatcher thread
                    Dispatcher.Invoke(() =>
                    {
                        try
                        {
                            var newMessage = CreateNewMessage(formattedText, isCustomer, false);
                            if (newMessage != null)
                            {
                                newMessage.TurnId = turnId;
                                newMessage.Confidence = capturedConfidence;
                                newMessage.Timestamp = capturedTimestamp;
                                newMessage.Source = source;
                                newMessage.Speaker = speaker;
                                newMessage.IsFinal = true;
                            }
                        }
                        catch (Exception ex)
                        {
                            LoggingService.Error($"[VoiceSessionPage] Error creating transcript message: {ex.Message}");
                        }
                    });

                    // Log to call logger
                    if (isCustomer)
                    {
                        _callLogger?.LogSpeakerTranscript(text, isFinal: true, rawJson: rawJson);
                    }
                    else
                    {
                        _callLogger?.LogMicTranscript(text, isFinal: true, rawJson: rawJson);
                    }
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[VoiceSessionPage] Error handling backend transcript: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle agent response messages received from the backend.
        /// Deserializes the full BackendAgentResponseMessage to use all available fields:
        /// text, timestamp, conversation_id, is_delta, is_final.
        /// Supports streaming delta chunks (is_delta=true) by appending to an in-progress message,
        /// and finalizing the message when is_final=true.
        /// </summary>
        private void OnBackendAgentResponseReceived(string responseText, string rawJson)
        {
            try
            {
                if (string.IsNullOrEmpty(responseText)) return;

                // Deserialize the raw JSON into the strongly-typed BackendAgentResponseMessage model
                // to access all fields (timestamp, conversation_id, is_delta, is_final)
                Models.BackendAgentResponseMessage agentMsg = null;
                try
                {
                    if (!string.IsNullOrEmpty(rawJson))
                    {
                        agentMsg = System.Text.Json.JsonSerializer.Deserialize<Models.BackendAgentResponseMessage>(rawJson);
                    }
                }
                catch (Exception parseEx)
                {
                    LoggingService.Warn($"[VoiceSessionPage] Failed to deserialize BackendAgentResponseMessage, using fallback defaults: {parseEx.Message}");
                }

                // Extract fields from deserialized model with sensible defaults
                bool isDelta = agentMsg?.IsDelta ?? false;
                bool isFinal = agentMsg?.IsFinal ?? true; // Default to final if not specified (backward compat)
                long backendTimestamp = agentMsg?.Timestamp ?? 0;
                string conversationId = agentMsg?.ConversationId;

                // Update _currentConversationId from the backend if provided
                if (!string.IsNullOrEmpty(conversationId) && conversationId != _currentConversationId)
                {
                    string previousId = _currentConversationId;
                    _currentConversationId = conversationId;
                    LoggingService.Info($"[VoiceSessionPage] Updated conversation ID from backend agent response: {_currentConversationId} (was: {previousId ?? "null"})");
                }

                // Convert backend timestamp (Unix ms) to DateTime if available, otherwise use current time
                DateTime messageTimestamp = DateTime.Now;
                if (backendTimestamp > 0)
                {
                    try
                    {
                        messageTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(backendTimestamp).LocalDateTime;
                    }
                    catch
                    {
                        // If timestamp conversion fails, fall back to current time
                        messageTimestamp = DateTime.Now;
                    }
                }

                // Build diagnostic log string
                string deltaStr = isDelta ? "delta" : (isFinal ? "final" : "response");
                string timestampStr = backendTimestamp > 0 ? $", ts: {backendTimestamp}" : "";
                string convIdStr = !string.IsNullOrEmpty(conversationId) ? $", convId: {conversationId}" : "";
                LoggingService.Info($"[VoiceSessionPage] Backend agent response ({deltaStr}{timestampStr}{convIdStr}): " +
                    $"{responseText.Substring(0, Math.Min(80, responseText.Length))}");

                // Handle delta streaming vs complete responses
                if (isDelta)
                {
                    // Delta chunk: append to the in-progress agent message
                    HandleAgentResponseDelta(responseText, messageTimestamp);
                }
                else
                {
                    // Non-delta message (complete response or final indicator)
                    if (isFinal && _backendDeltaAgentMessage != null)
                    {
                        // This is the final chunk for a delta stream -- finalize the accumulated message
                        FinalizeAgentResponseDelta(responseText, messageTimestamp);
                    }
                    else
                    {
                        // Complete response (not part of a delta stream) -- create a new message directly
                        HandleAgentResponseComplete(responseText, messageTimestamp);
                    }
                }

                // Log to call logger using the provider-agnostic method
                _callLogger?.LogAgentResponse(responseText, rawJson: rawJson, isDelta: isDelta, isFinal: isFinal, conversationId: conversationId);
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[VoiceSessionPage] Error handling backend agent response: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles a delta (streaming) chunk for an agent response.
        /// If no in-progress delta message exists, creates one. Otherwise, appends text to the existing message.
        /// </summary>
        private void HandleAgentResponseDelta(string deltaText, DateTime messageTimestamp)
        {
            try
            {
                if (_backendDeltaAgentMessage == null)
                {
                    // First delta chunk -- generate a turn ID and create the UI message
                    lock (_turnIdCounterLock)
                    {
                        _globalTurnIdCounter++;
                        _backendDeltaAgentTurnId = _globalTurnIdCounter.ToString("D3");
                    }
                    _turnIdToSourceMap.TryAdd(_backendDeltaAgentTurnId, "CSR");
                    _backendDeltaAgentText = deltaText;

                    string capturedTurnId = _backendDeltaAgentTurnId;
                    DateTime capturedTimestamp = messageTimestamp;
                    string capturedText = _backendDeltaAgentText;

                    Dispatcher.Invoke(() =>
                    {
                        try
                        {
                            var newMessage = CreateNewMessage(capturedText, false, true);
                            if (newMessage != null)
                            {
                                newMessage.TurnId = capturedTurnId;
                                newMessage.Timestamp = capturedTimestamp;
                                newMessage.IsDelta = true;
                                newMessage.IsFinal = false;
                                newMessage.Speaker = "agent";
                                _backendDeltaAgentMessage = newMessage;
                            }
                        }
                        catch (Exception ex)
                        {
                            LoggingService.Error($"[VoiceSessionPage] Error creating delta agent response message: {ex.Message}");
                        }
                    });

                    LoggingService.Debug($"[VoiceSessionPage] Started new agent response delta stream, turnId: {_backendDeltaAgentTurnId}");
                }
                else
                {
                    // Subsequent delta chunk -- append to the existing message
                    _backendDeltaAgentText += deltaText;
                    string capturedFullText = _backendDeltaAgentText;

                    Dispatcher.Invoke(() =>
                    {
                        try
                        {
                            // Update TranscriptionTextBlock first (where CreateNewMessage places the initial text)
                            // then fall back to AIResponseTextBlock
                            if (_backendDeltaAgentMessage?.TranscriptionTextBlock != null)
                            {
                                _backendDeltaAgentMessage.TranscriptionTextBlock.Text = capturedFullText;
                            }
                            else if (_backendDeltaAgentMessage?.AIResponseTextBlock != null)
                            {
                                _backendDeltaAgentMessage.AIResponseTextBlock.Text = capturedFullText;
                            }
                            if (_backendDeltaAgentMessage != null)
                            {
                                _backendDeltaAgentMessage.Text = capturedFullText;
                            }
                        }
                        catch (Exception ex)
                        {
                            LoggingService.Error($"[VoiceSessionPage] Error appending delta to agent response: {ex.Message}");
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[VoiceSessionPage] Error handling agent response delta: {ex.Message}");
            }
        }

        /// <summary>
        /// Finalizes an in-progress delta agent response stream.
        /// Appends any remaining final text, forwards the complete message to the frontend, and resets delta state.
        /// </summary>
        private void FinalizeAgentResponseDelta(string finalText, DateTime messageTimestamp)
        {
            try
            {
                // Append the final text if it contains new content beyond what we already accumulated
                if (!string.IsNullOrEmpty(finalText) && finalText != _backendDeltaAgentText)
                {
                    // If the final text is a complete replacement (longer than accumulated), use it directly
                    // Otherwise append it as a final chunk
                    if (finalText.Length > _backendDeltaAgentText.Length && finalText.StartsWith(_backendDeltaAgentText))
                    {
                        _backendDeltaAgentText = finalText;
                    }
                    else if (!_backendDeltaAgentText.EndsWith(finalText))
                    {
                        _backendDeltaAgentText += finalText;
                    }
                }

                string completedText = _backendDeltaAgentText;
                string completedTurnId = _backendDeltaAgentTurnId;

                // Update the UI message with the final complete text
                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        if (_backendDeltaAgentMessage != null)
                        {
                            // Update TranscriptionTextBlock first (where CreateNewMessage places the initial text)
                            if (_backendDeltaAgentMessage.TranscriptionTextBlock != null)
                            {
                                _backendDeltaAgentMessage.TranscriptionTextBlock.Text = completedText;
                            }
                            else if (_backendDeltaAgentMessage.AIResponseTextBlock != null)
                            {
                                _backendDeltaAgentMessage.AIResponseTextBlock.Text = completedText;
                            }
                            _backendDeltaAgentMessage.Text = completedText;
                            _backendDeltaAgentMessage.Timestamp = messageTimestamp;
                            _backendDeltaAgentMessage.IsDelta = false;
                            _backendDeltaAgentMessage.IsFinal = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        LoggingService.Error($"[VoiceSessionPage] Error finalizing delta agent response UI: {ex.Message}");
                    }
                });

                LoggingService.Info($"[VoiceSessionPage] Finalized agent response delta stream, turnId: {completedTurnId}, length: {completedText.Length}");

                // Reset delta tracking state
                _backendDeltaAgentMessage = null;
                _backendDeltaAgentTurnId = null;
                _backendDeltaAgentText = "";
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[VoiceSessionPage] Error finalizing agent response delta: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles a complete (non-delta) agent response.
        /// Creates a new UI message, forwards to frontend, and generates a turn ID.
        /// </summary>
        private void HandleAgentResponseComplete(string responseText, DateTime messageTimestamp)
        {
            try
            {
                // If there's an incomplete delta stream, finalize it first before creating a new message
                if (_backendDeltaAgentMessage != null)
                {
                    LoggingService.Warn($"[VoiceSessionPage] Received complete agent response while delta stream was in progress. Finalizing previous delta.");
                    FinalizeAgentResponseDelta("", messageTimestamp);
                }

                // Generate a turn ID for the agent response
                string turnId;
                lock (_turnIdCounterLock)
                {
                    _globalTurnIdCounter++;
                    turnId = _globalTurnIdCounter.ToString("D3");
                }

                // Register in turnId-to-source map as CSR (agent response)
                _turnIdToSourceMap.TryAdd(turnId, "CSR");

                DateTime capturedTimestamp = messageTimestamp;

                // Create UI message for agent response on the dispatcher thread
                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        // Display as agent/CSR message (isCustomer=false, isAIResponse=true)
                        var newMessage = CreateNewMessage(responseText, false, true);
                        if (newMessage != null)
                        {
                            newMessage.TurnId = turnId;
                            newMessage.Timestamp = capturedTimestamp;
                            newMessage.Speaker = "agent";
                            newMessage.IsFinal = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        LoggingService.Error($"[VoiceSessionPage] Error creating agent response message: {ex.Message}");
                    }
                });

                // Frontend forwarding removed — data flows only to the backend
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[VoiceSessionPage] Error handling complete agent response: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle error messages from the backend audio streaming service.
        /// </summary>
        private void OnBackendErrorOccurred(string errorMessage)
        {
            try
            {
                LoggingService.Error($"[VoiceSessionPage] Backend audio streaming error: {errorMessage}");

                // Show error in UI as a toast
                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        ShowToast($"Backend Error: {errorMessage}", isError: true);
                    }
                    catch { }
                });
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[VoiceSessionPage] Error handling backend error: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle connection status changes from the backend audio streaming service.
        /// Note: The BE WS indicator now reflects backend API reachability (health check),
        /// not the audio WebSocket connection state. This handler is retained for logging.
        /// </summary>
        private void OnBackendConnectionStatusChanged(BackendAudioStreamingService.ConnectionState newState)
        {
            try
            {
                LoggingService.Info($"[VoiceSessionPage] Backend audio WebSocket connection state: {newState}");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[VoiceSessionPage] Error handling backend connection status: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle latency measurement from the backend audio streaming service.
        /// Updates the latency indicator in the top-right corner using a rolling average.
        /// </summary>
        private void OnAudioChunkLatencyMeasured(double latencyMs)
        {
            try
            {
                // Maintain a rolling average over the last N samples
                double avg;
                int sampleCount;
                lock (_latencySamplesLock)
                {
                    _latencySamples.Enqueue(latencyMs);
                    while (_latencySamples.Count > LatencySampleCount)
                        _latencySamples.Dequeue();

                    double sum = 0;
                    foreach (var s in _latencySamples) sum += s;
                    sampleCount = _latencySamples.Count;
                    avg = sum / sampleCount;
                }

                // Log first sample and every 50th for diagnostics
                if (sampleCount == 1 || sampleCount % 50 == 0)
                {
                    LoggingService.Debug($"[VoiceSessionPage] Latency sample #{sampleCount}: {latencyMs:F1} ms, avg: {avg:F1} ms");
                }

                // Throttle UI updates to at most every 500ms to avoid flooding the Dispatcher
                var now = DateTime.UtcNow;
                if ((now - _lastLatencyUiUpdate).TotalMilliseconds < 500)
                    return;
                _lastLatencyUiUpdate = now;

                // Determine color based on latency: green < 100ms, yellow < 300ms, red >= 300ms
                string dotColor;
                if (avg < 100) dotColor = "#27AE60";       // Green
                else if (avg < 300) dotColor = "#F39C12";   // Yellow/Amber
                else dotColor = "#E74C3C";                  // Red

                string displayText = $"{avg:F0} ms";

                Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        LatencyValueText.Text = displayText;
                        LatencyStatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dotColor));
                    }
                    catch (Exception uiEx)
                    {
                        LoggingService.Debug($"[VoiceSessionPage] Latency UI update error: {uiEx.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[VoiceSessionPage] Error updating latency UI: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle session status messages from the backend.
        /// The backend may assign a session ID or indicate processing status.
        /// </summary>
        private void OnBackendSessionStatusReceived(string status, string rawJson)
        {
            try
            {
                LoggingService.Info($"[VoiceSessionPage] Backend session status: {status}");

                // Parse session ID from the raw JSON if available
                try
                {
                    if (!string.IsNullOrEmpty(rawJson))
                    {
                        var doc = JsonDocument.Parse(rawJson);
                        if (doc.RootElement.TryGetProperty("session_id", out var sessionIdProp))
                        {
                            string sessionId = sessionIdProp.GetString();
                            if (!string.IsNullOrEmpty(sessionId))
                            {
                                _currentConversationId = sessionId;
                                LoggingService.Info($"[VoiceSessionPage] Updated conversation ID from backend session: {sessionId}");
                            }
                        }
                    }
                }
                catch { }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[VoiceSessionPage] Error handling backend session status: {ex.Message}");
            }
        }

        #endregion


        
        /// <summary>
        /// Show toast notification for connection errors
        /// </summary>
        private void ShowToast(string message, bool isError = false)
        {
            try
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
                
                // Auto-hide after 5 seconds with fade-out (longer for errors)
                var timer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(5)
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
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[VoiceSessionPage] Error showing toast: {ex.Message}");
            }
        }
        
        // SendCallStartedEventAsync removed — no longer sending call events to frontend

        /// <summary>
        /// Updates the AI response text block for a specific message
        /// </summary>
        private void UpdateAIResponse(string messageId, string aiResponse)
        {
            try
            {
                // Find the message in conversation history by ID first, then by text
                ConversationMessage message = null;
                
                // First try to find by ID if messageId looks like an ID (8 characters)
                if (messageId.Length == 8)
                {
                    message = _conversationHistory.FirstOrDefault(m => m.Id == messageId);
                }
                
                // If not found by ID, try to find by exact text match
                if (message == null)
                {
                    message = _conversationHistory.FirstOrDefault(m => m.Text == messageId);
                }
                
                // If still not found, try to find the most recent message without AI response from the same speaker
                if (message == null)
                {
                    // Find the most recent message without AI response
                    message = _conversationHistory
                        .Where(m => !m.HasAIResponse)
                        .OrderByDescending(m => m.Timestamp)
                        .FirstOrDefault();
                }
                
                if (message != null && message.AIResponseTextBlock != null)
                {
                    Dispatcher.Invoke(() =>
                    {
                        message.AIResponseTextBlock.Text = aiResponse;
                        message.HasAIResponse = true;
                        
                        // Change the AI response block styling to indicate it's active
                        var aiResponseBlock = message.AIResponseTextBlock.Parent as Border;
                        if (aiResponseBlock != null)
                        {
                            aiResponseBlock.Background = new SolidColorBrush(Color.FromRgb(10, 30, 50)); // Dark cyan-tinted background
                            aiResponseBlock.BorderBrush = new SolidColorBrush(Color.FromRgb(0, 212, 255)); // #00D4FF - Match CSR avatar
                            // Ensure text color is set to theme color
                            message.AIResponseTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(139, 154, 175)); // #8B9AAF
                        }
                        
                        LoggingService.Info($"[VoiceSessionPage] ? Updated AI response for message ID '{message.Id}': '{message.Text}' -> '{aiResponse}'");
                    });
                }
                else
                {
                    LoggingService.Info($"[VoiceSessionPage] ?? Could not find message or AI response block for: '{messageId}'");
                }
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[VoiceSessionPage] ? Error updating AI response: {ex.Message}");
            }
        }


        /// <summary>
        /// Enqueue user_contextual_message for processing through the queue
        /// </summary>
        /// <param name="contextualText">The contextual message text</param>
        /// <param name="isFromFrontend">True if message came from frontend, false if locally created</param>
        public void EnqueueContextualMessage(string contextualText, bool isFromFrontend = false)
        {
            try
            {
                // Ignore user_contextual_message if call has not started
                if (!_callStartedEventSent)
                {
                    LoggingService.Warn($"[VoiceSessionPage] ⚠️ Ignoring user_contextual_message - call has not started yet (callStartedEventSent: {_callStartedEventSent})");
                    return;
                }
                
                if (string.IsNullOrWhiteSpace(contextualText))
                {
                    LoggingService.Warn("[VoiceSessionPage] ⚠️ Cannot enqueue empty contextual message");
                    return;
                }
                
                var timestamp = DateTime.UtcNow;
                var messageId = Guid.NewGuid().ToString("N").Substring(0, 16);
                
                // Generate turnId for contextual message BEFORE formatting text
                // Use the same sequential counter to ensure turnIDs are in order with CSR/Caller messages
                string contextualTurnId = null;
                lock (_turnIdCounterLock)
                {
                    _globalTurnIdCounter++;
                    contextualTurnId = _globalTurnIdCounter.ToString("D3"); // Format as 3-digit number (001, 002, 003, etc.)
                }

                // Store in dictionary with source "user_contextual_message"
                _turnIdToSourceMap.TryAdd(contextualTurnId, "user_contextual_message");
                LoggingService.Info($"[VoiceSessionPage] 📝 Generated sequential TurnId for contextual message: {contextualTurnId} -> user_contextual_message (Global Counter: {_globalTurnIdCounter})");

                // Format contextual message with turnID - same format as UI and backend agent
                // Format: "user_contextual_message: The customer is asking about refund policy {turnID: 054}"
                string prefixedMessage = contextualText;
                if (!contextualText.StartsWith("user_contextual_message: ", StringComparison.OrdinalIgnoreCase))
                {
                    prefixedMessage = $"user_contextual_message: {contextualText}";
                }
                // Add turnID to the formatted message (same format as CSR/Caller messages)
                string formattedContextualMessage = $"{prefixedMessage} {{turnID: {contextualTurnId}}}";
                
                // Enqueue with formatted message (includes turnID) - for UI display
                _contextualMessageQueue.Enqueue((timestamp, formattedContextualMessage, messageId, contextualTurnId));
                LoggingService.Info($"[VoiceSessionPage] Enqueued user_contextual_message: '{contextualText.Substring(0, Math.Min(50, contextualText.Length))}...' (MsgID='{messageId}', TurnID='{contextualTurnId}', QueueSize: {_contextualMessageQueue.Count}, FromFrontend: {isFromFrontend})");

                // Create UI block - use the same formatted text as backend agent
                // Format: "user_contextual_message: {turnID: 054} The customer is asking about refund policy"
                Dispatcher.Invoke(() =>
                {
                    // Format contextual text for UI display (same format as what's sent to backend agent)
                    // Format: "user_contextual_message: {turnID: 054} The customer is asking about refund policy"
                    string contextualDisplayText = $"user_contextual_message: {{turnID: {contextualTurnId}}} {contextualText}";

                    // Create a regular message block with formatted text (same as backend agent)
                    var contextualMessage = CreateNewMessage(contextualDisplayText, isCustomer: false, isAIResponse: false);

                    // Store turnId in the contextual message
                    if (contextualMessage != null)
                    {
                        contextualMessage.TurnId = contextualTurnId;
                        contextualMessage.Id = messageId;

                        // Verify AIResponseTextBlock is set (should be created by CreateNewMessage)
                        if (contextualMessage.AIResponseTextBlock == null)
                        {
                            LoggingService.Error($"[VoiceSessionPage] ❌ AIResponseTextBlock is NULL after CreateNewMessage! This will cause UI update to fail.");
                        }
                        else
                        {
                            LoggingService.Info($"[VoiceSessionPage] ✅ AIResponseTextBlock is available: {contextualMessage.AIResponseTextBlock != null}");
                        }

                        LoggingService.Info($"[VoiceSessionPage] 📝 Stored TurnId in contextual message UI: {contextualTurnId}, MsgID: {messageId}, HasAIResponseBlock: {contextualMessage.AIResponseTextBlock != null}, MessageBorder: {contextualMessage.MessageBorder != null}");

                        // Center-align the contextual message (instead of right-aligned)
                        if (contextualMessage.MessageBorder != null)
                        {
                            contextualMessage.MessageBorder.HorizontalAlignment = HorizontalAlignment.Center;
                            // Adjust margin to center it better
                            contextualMessage.MessageBorder.Margin = new Thickness(50, 5, 50, 5);
                        }
                        
                        // Change the badge from "CSR" to "CONTEXT" for contextual messages
                        if (contextualMessage.MessageBorder?.Child is StackPanel messagePanel)
                        {
                            // Find the header panel (first child)
                            if (messagePanel.Children.Count > 0 && messagePanel.Children[0] is StackPanel headerPanel)
                            {
                                // Find the iconGrid (first child of headerPanel)
                                if (headerPanel.Children.Count > 0 && headerPanel.Children[0] is Grid iconGrid)
                                {
                                    // Find the TextBlock with the badge text (second child of iconGrid)
                                    var iconTextBlocks = iconGrid.Children.OfType<TextBlock>().ToList();
                                    if (iconTextBlocks.Count > 0)
                                    {
                                        iconTextBlocks[0].Text = "CONTEXT";
                                        iconTextBlocks[0].FontSize = 9;
                                        iconTextBlocks[0].Margin = new Thickness(4, 3, 4, 3);
                                        iconTextBlocks[0].Padding = new Thickness(0);
                                        
                                        // Also update the ellipse color to a distinct color for contextual messages
                                        var ellipses = iconGrid.Children.OfType<Ellipse>().ToList();
                                        if (ellipses.Count > 0)
                                        {
                                            ellipses[0].Fill = new SolidColorBrush(Colors.Purple);
                                            ellipses[0].Stroke = new SolidColorBrush(Colors.DarkViolet);
                                        }
                                    }
                                }
                            }
                        }
                        
                        // AIResponseTextBlock will be updated when response arrives
                        // No need to set raw JSON - response will update it with refined_text, thinking, and turnId
                        
                        // Store in pending messages dictionary with messageId as key
                        lock (_pendingContextualMessagesLock)
                        {
                            _pendingContextualMessages[messageId] = contextualMessage;
                            LoggingService.Info($"[VoiceSessionPage] 📋 Added to pending contextual messages: MsgID='{messageId}', Total pending: {_pendingContextualMessages.Count}");
                        }
                        
                        // Also add to conversation history so it can be found even after being removed from pending
                        _conversationHistory.Add(contextualMessage);
                        LoggingService.Info($"[VoiceSessionPage] 📋 Added contextual message to conversation history: MsgID='{messageId}', TurnId='{contextualTurnId}'");
                        
                        LoggingService.Info($"[VoiceSessionPage] ✅ User contextual message displayed in UI with raw JSON (pending response, MsgID='{messageId}')");
                    }
                });
                
                // NOTE: Backend handles contextual messages directly
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[VoiceSessionPage] ❌ Error enqueueing contextual message: {ex.Message}");
            }
        }

        /// <summary>
        /// Append text to an existing message
        /// </summary>
        private void AppendToExistingMessage(ConversationMessage message, string newText)
        {
            try
            {
                // Check if we have partial text that should be replaced
                if (!string.IsNullOrEmpty(message.PartialText))
                {
                    // We have partial text, so replace it with the final text
                    message.Text = newText; // Replace with final text
                    message.PartialText = ""; // Clear partial text
                    LoggingService.Info($"[VoiceSessionPage] ?? Replaced partial text with final text: '{newText}'");
                }
                else
                {
                    // No partial text, so append to existing text
                    message.Text += " " + newText;
                    LoggingService.Info($"[VoiceSessionPage] ? Appended text to existing message: '{newText}'");
                }
                
                message.FullText = message.Text;
                message.Timestamp = DateTime.Now; // Update timestamp
                
                // Update the UI transcription text block with final text
                if (message.TranscriptionTextBlock != null)
                {
                    message.TranscriptionTextBlock.Text = message.Text;
                    // Style as final (normal, black)
                    message.TranscriptionTextBlock.FontStyle = FontStyles.Normal;
                    message.TranscriptionTextBlock.Foreground = new SolidColorBrush(Colors.Black);
                }
                
                // Scroll to the bottom to show the updated message
                ScrollViewerTranscription.ScrollToBottom();
                
                LoggingService.Info($"[VoiceSessionPage] ? Finalized message. Full text: '{message.Text}'");
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[VoiceSessionPage] ? Error appending to existing message: {ex.Message}");
            }
        }

        #region IVR Incoming Call Handling

        /// <summary>
        /// Event handler when IVR detects incoming call ringing
        /// </summary>
        private void OnIVRIncomingCallRinging(object sender, IncomingCallRingingEvent e)
        {
            try
            {
                LoggingService.Info($"[VoiceSessionPage] 📞 Incoming call ringing - Call ID: {e.CallId}, Customer: {e.Customer?.Name}");

                // Show incoming call panel on UI thread
                Dispatcher.Invoke(() =>
                {
                    IncomingCallCustomerName.Text = e.Customer?.Name ?? "Unknown";
                    IncomingCallPhoneNumber.Text = e.Customer?.PhoneE164 ?? "Unknown";
                    IncomingCallPanel.Visibility = Visibility.Visible;
                    
                    LoggingService.Info("[VoiceSessionPage] Incoming call panel shown");
                });
                
                // Do not forward to frontend here to avoid duplicates; handled by FrontendIntegrationService
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[VoiceSessionPage] Error handling incoming call ringing: {ex.Message}");
            }
        }

        /// <summary>
        /// Event handler when call is answered
        /// </summary>
        private void OnIVRCallAnswered(object sender, string callId)
        {
            try
            {
                LoggingService.Info($"[VoiceSessionPage] ✅ Call answered - Call ID: {callId}");

                // Everything must run on UI thread since StartNewTranscription accesses UI elements
                Dispatcher.BeginInvoke(new Action(async () =>
                {
                    try
                    {
                        // Hide incoming call panel
                        IncomingCallPanel.Visibility = Visibility.Collapsed;

                        // Start the actual call session (on UI thread)
                        await StartNewTranscription();

                        // Waveforms will be shown automatically when backend connects
                        // (handled in PollForConversationIdAndStartCall)

                        // Notify MainWindow that call is active
                        OnVoiceSessionCallStarted?.Invoke(this, EventArgs.Empty);

                        LoggingService.Info("[VoiceSessionPage] ✅ Call started successfully after accepting");
                    }
                    catch (Exception ex)
                    {
                        LoggingService.Error($"[VoiceSessionPage] ❌ Error starting call after accept: {ex.Message}");
                        LoggingService.Error($"[VoiceSessionPage] Stack trace: {ex.StackTrace}");

                        // Notify MainWindow that call start failed so button state resets
                        OnVoiceSessionCallStartFailed?.Invoke(this, ex.Message);

                        MessageBox.Show($"Failed to start call: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }));
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[VoiceSessionPage] Error handling call answered: {ex.Message}");
            }
        }

        /// <summary>
        /// Event handler when call is rejected
        /// </summary>
        private void OnIVRCallRejected(object sender, string callId)
        {
            try
            {
                LoggingService.Info($"[VoiceSessionPage] ❌ Call rejected - Call ID: {callId}");

                // Hide incoming call panel and show reject separator
                Dispatcher.Invoke(() =>
                {
                    IncomingCallPanel.Visibility = Visibility.Collapsed;

                    // Show "Call Reject" separator (no duration since call didn't start)
                    CreateCallSeparator("Call Reject", DateTime.Now, false, showDuration: false);
                    LoggingService.Info("[VoiceSessionPage] ✅ 'Call Reject' separator added to conversation");

                    // Record rejected call stat and update display
                    _callsRejectedCount++;
                    LoggingService.Info($"[VoiceSessionPage] Calls rejected count: {_callsRejectedCount}");
                    UpdateStatsDisplay();

                    // Show idle panel
                    ShowIdlePanel();

                    // Notify MainWindow that call was rejected
                    OnVoiceSessionCallRejected?.Invoke(this, EventArgs.Empty);
                });
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[VoiceSessionPage] Error handling call rejected: {ex.Message}");
            }
        }

        /// <summary>
        /// Accept button click handler
        /// </summary>
        private async void AcceptCallButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                LoggingService.Info("[VoiceSessionPage] Accept call button clicked");

                if (_ivrService != null)
                {
                    await _ivrService.AnswerCallAsync();
                }
                else
                {
                    LoggingService.Warn("[VoiceSessionPage] IVR service not available");
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[VoiceSessionPage] Error accepting call: {ex.Message}");
            }
        }

        /// <summary>
        /// Reject button click handler
        /// </summary>
        private async void RejectCallButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                LoggingService.Info("[VoiceSessionPage] Reject call button clicked");

                if (_ivrService != null)
                {
                    await _ivrService.RejectCallAsync();
                }
                else
                {
                    LoggingService.Warn("[VoiceSessionPage] IVR service not available");
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[VoiceSessionPage] Error rejecting call: {ex.Message}");
            }
        }

        // OnFrontendCallReject and OnFrontendCallEnd event handlers removed — no longer receiving call control from frontend

        /// <summary>
        /// Simulate incoming call (for testing) - triggered by Start Call button
        /// </summary>
        private async Task SimulateIncomingCallAsync()
        {
            try
            {
                LoggingService.Info("[VoiceSessionPage] 📞 Simulating incoming call...");

                if (_ivrService != null)
                {
                    await _ivrService.SimulateIncomingCallAsync();
                }
                else
                {
                    LoggingService.Warn("[VoiceSessionPage] IVR service not available, attempting to re-initialize...");
                    
                    // Try to re-initialize the IVR service
                    InitializeIVRService();
                    
                    if (_ivrService != null)
                    {
                        LoggingService.Info("[VoiceSessionPage] IVR service re-initialized successfully, retrying...");
                        await _ivrService.SimulateIncomingCallAsync();
                    }
                    else
                    {
                        LoggingService.Error("[VoiceSessionPage] Failed to re-initialize IVR service");
                        MessageBox.Show("IVR service could not be initialized. Please check logs.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[VoiceSessionPage] Error simulating incoming call: {ex.Message}");
            }
        }

        #endregion

    }
}