using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DataRippleAIDesktop.Models;
using DataRippleAIDesktop.Services;

namespace DataRippleAIDesktop.Views
{
    public partial class ConversationHistoryPage : UserControl
    {
        private List<ConversationListItem> _conversations;
        private int _currentOffset;
        private int _pageLimit = 50;
        private bool _hasMore;
        private bool _isLoadingMore;
        private bool _isHistoryApiAvailable;
        private string _backendBaseUrl;

        public ConversationHistoryPage()
        {
            InitializeComponent();
            _conversations = new List<ConversationListItem>();
            _isHistoryApiAvailable = false;
            ResolveBackendConfiguration();
            Loaded += ConversationHistoryPage_Loaded;
        }

        /// <summary>
        /// Resolves backend API base URL from configuration.
        /// The backend history API is not yet available, so this prepares the infrastructure
        /// for when a GET /conversations/ (list) endpoint is added to the backend.
        /// </summary>
        private void ResolveBackendConfiguration()
        {
            try
            {
                _backendBaseUrl = Globals.ConfigurationInfo?["Backend:BaseUrl"];

                if (string.IsNullOrEmpty(_backendBaseUrl))
                {
                    LoggingService.Info("[ConversationHistory] Backend base URL not configured - history feature unavailable");
                    _isHistoryApiAvailable = false;
                    return;
                }

                // Backend has GET /api/conversation-logs (list) and GET /api/conversation-logs/{id} (detail)
                _isHistoryApiAvailable = true;

                LoggingService.Info("[ConversationHistory] Backend configured - history API available via /conversation-logs");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[ConversationHistory] Error resolving backend configuration: {ex.Message}");
                _isHistoryApiAvailable = false;
            }
        }

        private async void ConversationHistoryPage_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadConversationsAsync();
        }

        private async Task LoadConversationsAsync()
        {
            if (!_isHistoryApiAvailable)
            {
                ShowComingSoonPlaceholder();
                return;
            }

            try
            {
                LoadingProgressBar.Visibility = Visibility.Visible;
                txtStatus.Text = "Loading conversations...";

                LoggingService.Info("[ConversationHistory] Loading conversations from backend...");

                _currentOffset = 0;
                var items = await FetchConversationListAsync(_pageLimit, _currentOffset);

                if (items != null && items.Count > 0)
                {
                    _conversations = items;
                    _currentOffset = items.Count;
                    _hasMore = items.Count >= _pageLimit;

                    ConversationsList.ItemsSource = _conversations;

                    txtStatus.Text = $"Loaded {_conversations.Count} conversations";
                    btnLoadMore.Visibility = _hasMore ? Visibility.Visible : Visibility.Collapsed;

                    LoggingService.Info($"[ConversationHistory] Loaded {_conversations.Count} conversations (hasMore={_hasMore})");
                }
                else
                {
                    _conversations = new List<ConversationListItem>();
                    ConversationsList.ItemsSource = null;
                    txtStatus.Text = "No conversation history found";
                    btnLoadMore.Visibility = Visibility.Collapsed;

                    LoggingService.Info("[ConversationHistory] No conversations found");
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[ConversationHistory] Error loading conversations: {ex.Message}");
                txtStatus.Text = $"Error loading conversations: {ex.Message}";
            }
            finally
            {
                LoadingProgressBar.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// Fetches the paginated conversation list from the backend API.
        /// Endpoint: GET {baseUrl}/conversation-logs?limit={limit}&amp;offset={offset}
        /// Response: { status, message, data: [ { id, user_id, conversation_id, customer, agent, events, started_at, ended_at, ... } ] }
        /// </summary>
        private async Task<List<ConversationListItem>> FetchConversationListAsync(int limit, int offset)
        {
            try
            {
                if (string.IsNullOrEmpty(_backendBaseUrl) || string.IsNullOrEmpty(Globals.BackendAccessToken))
                {
                    LoggingService.Info("[ConversationHistory] Cannot fetch conversations - missing backend URL or auth token");
                    return null;
                }

                var url = $"{_backendBaseUrl.TrimEnd('/')}/conversation-logs?limit={limit}&offset={offset}";

                using var httpClient = HttpClientFactory.CreateApiHttpClient();
                httpClient.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", Globals.BackendAccessToken);

                var response = await httpClient.GetAsync(url).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    LoggingService.Error($"[ConversationHistory] Backend returned {(int)response.StatusCode} for conversation list");
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                // Backend returns: { status: "success", message: "...", data: [...] }
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("data", out var dataElement) || dataElement.ValueKind != JsonValueKind.Array)
                {
                    LoggingService.Warn("[ConversationHistory] Response has no 'data' array");
                    return null;
                }

                var items = new List<ConversationListItem>();
                foreach (var item in dataElement.EnumerateArray())
                {
                    var conversation = new ConversationListItem();

                    conversation.ConversationId = item.TryGetProperty("conversation_id", out var convIdProp)
                        ? convIdProp.GetString()
                        : (item.TryGetProperty("id", out var idProp) ? idProp.GetString() : null);

                    conversation.AgentName = item.TryGetProperty("agent", out var agentProp) && agentProp.ValueKind == JsonValueKind.Object
                        ? (agentProp.TryGetProperty("name", out var agentNameProp) ? agentNameProp.GetString() : "Agent")
                        : "Agent";

                    conversation.Status = item.TryGetProperty("is_evaluated", out var evalProp) && evalProp.ValueKind == JsonValueKind.True
                        ? "evaluated" : "pending";

                    if (item.TryGetProperty("started_at", out var startedAtProp) && startedAtProp.ValueKind == JsonValueKind.String)
                    {
                        if (DateTimeOffset.TryParse(startedAtProp.GetString(), out var startedAt))
                        {
                            conversation.StartTimeUnixSecs = startedAt.ToUnixTimeSeconds();

                            if (item.TryGetProperty("ended_at", out var endedAtProp) && endedAtProp.ValueKind == JsonValueKind.String)
                            {
                                if (DateTimeOffset.TryParse(endedAtProp.GetString(), out var endedAt))
                                {
                                    conversation.CallDurationSecs = (int)(endedAt - startedAt).TotalSeconds;
                                }
                            }
                        }
                    }

                    if (item.TryGetProperty("events", out var eventsProp) && eventsProp.ValueKind == JsonValueKind.Array)
                    {
                        conversation.MessageCount = eventsProp.GetArrayLength();
                    }

                    items.Add(conversation);
                }

                LoggingService.Info($"[ConversationHistory] Parsed {items.Count} conversation items from backend response");
                return items;
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[ConversationHistory] Error fetching conversation list: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Fetches conversation detail from the backend API.
        /// Endpoint: GET {baseUrl}/conversation-logs/{conversation_id}
        /// Response: { status, message, data: { id, user_id, conversation_id, customer, agent, events, ... } }
        /// </summary>
        private async Task<ConversationDetail> FetchConversationDetailAsync(string conversationId)
        {
            try
            {
                if (string.IsNullOrEmpty(_backendBaseUrl) || string.IsNullOrEmpty(Globals.BackendAccessToken))
                {
                    LoggingService.Info("[ConversationHistory] Cannot fetch conversation detail - missing backend URL or auth token");
                    return null;
                }

                var url = $"{_backendBaseUrl.TrimEnd('/')}/conversation-logs/{Uri.EscapeDataString(conversationId)}";

                using var httpClient = HttpClientFactory.CreateApiHttpClient();
                httpClient.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", Globals.BackendAccessToken);

                var response = await httpClient.GetAsync(url).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    LoggingService.Error($"[ConversationHistory] Backend returned {(int)response.StatusCode} for conversation detail: {conversationId}");
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                // Backend returns: { status: "success", message: "...", data: { ... } }
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("data", out var dataElement) || dataElement.ValueKind != JsonValueKind.Object)
                {
                    LoggingService.Warn("[ConversationHistory] Detail response has no 'data' object");
                    return null;
                }

                var detail = new ConversationDetail();
                detail.ConversationId = dataElement.TryGetProperty("conversation_id", out var convIdProp)
                    ? convIdProp.GetString() : conversationId;

                detail.Transcript = new List<TranscriptMessage>();
                if (dataElement.TryGetProperty("events", out var eventsProp) && eventsProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var evt in eventsProp.EnumerateArray())
                    {
                        var msg = new TranscriptMessage();
                        msg.Role = evt.TryGetProperty("role", out var roleProp) ? roleProp.GetString() : "unknown";
                        msg.Message = evt.TryGetProperty("message", out var msgProp) ? msgProp.GetString() :
                                     (evt.TryGetProperty("text", out var textProp) ? textProp.GetString() : "");
                        msg.TimeInCallSecs = evt.TryGetProperty("time_in_call_secs", out var timeProp) ? timeProp.GetDouble() : 0;
                        detail.Transcript.Add(msg);
                    }
                }

                LoggingService.Info($"[ConversationHistory] Parsed conversation detail: {detail.ConversationId} with {detail.Transcript.Count} events");
                return detail;
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[ConversationHistory] Error fetching conversation detail: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Displays a clean placeholder message when the history API is not yet available.
        /// </summary>
        private void ShowComingSoonPlaceholder()
        {
            txtStatus.Text = "Conversation history is not yet available. This feature will be enabled when the backend history API is ready.";
            btnLoadMore.Visibility = Visibility.Collapsed;
            ConversationsList.ItemsSource = null;
            LoggingService.Info("[ConversationHistory] History API not available - showing placeholder");
        }

        private async void ConversationItem_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is DataGrid dataGrid && dataGrid.SelectedItem is ConversationListItem conversation)
            {
                await LoadConversationTranscriptAsync(conversation);
            }
        }

        private async Task LoadConversationTranscriptAsync(ConversationListItem conversation)
        {
            if (!_isHistoryApiAvailable)
            {
                return;
            }

            try
            {
                TranscriptModal.Visibility = Visibility.Visible;
                TranscriptLoadingOverlay.Visibility = Visibility.Visible;

                txtTranscriptTitle.Text = "Conversation Transcript";
                txtTranscriptSubtitle.Text = $"ID: {conversation.ConversationId} | {conversation.DisplayName} | {conversation.MessageCount} messages | {conversation.DisplayDuration}";

                LoggingService.Info($"[ConversationHistory] Loading transcript for: {conversation.ConversationId}");

                var detail = await FetchConversationDetailAsync(conversation.ConversationId);

                TranscriptPanel.Children.Clear();

                if (detail?.Transcript != null && detail.Transcript.Count > 0)
                {
                    foreach (var message in detail.Transcript)
                    {
                        var messageUI = CreateTranscriptMessageUI(message);
                        TranscriptPanel.Children.Add(messageUI);
                    }

                    LoggingService.Info($"[ConversationHistory] Rendered {detail.Transcript.Count} transcript messages");
                }
                else
                {
                    var noTranscriptText = new TextBlock
                    {
                        Text = "No transcript available for this conversation",
                        FontSize = 14,
                        Foreground = new SolidColorBrush(Color.FromRgb(139, 154, 175)),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 50, 0, 0)
                    };
                    TranscriptPanel.Children.Add(noTranscriptText);

                    LoggingService.Info("[ConversationHistory] No transcript found for this conversation");
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[ConversationHistory] Error loading transcript: {ex.Message}");

                var errorText = new TextBlock
                {
                    Text = $"Error loading transcript: {ex.Message}",
                    FontSize = 14,
                    Foreground = new SolidColorBrush(Color.FromRgb(255, 107, 107)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 50, 0, 0)
                };
                TranscriptPanel.Children.Clear();
                TranscriptPanel.Children.Add(errorText);
            }
            finally
            {
                TranscriptLoadingOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private Border CreateTranscriptMessageUI(TranscriptMessage message)
        {
            if (message.IsToolCall)
            {
                return CreateToolCallMessageUI(message);
            }
            else if (message.IsContextualMessage)
            {
                return CreateContextualMessageUI(message);
            }
            else if (message.IsThinkingMessage)
            {
                return CreateThinkingMessageUI(message);
            }

            bool isAgent = message.IsAgent;

            var messageBorder = new Border
            {
                Background = new SolidColorBrush(isAgent ? Color.FromRgb(26, 41, 66) : Color.FromRgb(19, 35, 64)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(58, 74, 100)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Margin = new Thickness(0, 0, 0, 15),
                Padding = new Thickness(20, 15, 20, 15),
                HorizontalAlignment = isAgent ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                MaxWidth = 700
            };

            var messagePanel = new StackPanel();

            var headerPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 8)
            };

            var roleLabel = new Border
            {
                Background = new SolidColorBrush(isAgent ? Color.FromRgb(29, 184, 184) : Color.FromRgb(255, 107, 53)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 0, 10, 0)
            };

            var roleText = new TextBlock
            {
                Text = message.DisplayRole,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Colors.White)
            };
            roleLabel.Child = roleText;

            var timestampText = new TextBlock
            {
                Text = message.DisplayTime,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(107, 122, 148)),
                VerticalAlignment = VerticalAlignment.Center
            };

            headerPanel.Children.Add(roleLabel);
            headerPanel.Children.Add(timestampText);

            var messageText = new TextBlock
            {
                Text = message.Message,
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromRgb(139, 154, 175)),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20
            };

            messagePanel.Children.Add(headerPanel);
            messagePanel.Children.Add(messageText);

            messageBorder.Child = messagePanel;

            return messageBorder;
        }

        private Border CreateToolCallMessageUI(TranscriptMessage message)
        {
            try
            {
                string toolName = message.ExtractToolName() ?? "Unknown Tool";

                Border messageBorder = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(26, 41, 66)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(29, 184, 184)),
                    BorderThickness = new Thickness(2),
                    CornerRadius = new CornerRadius(12),
                    Margin = new Thickness(50, 8, 50, 8),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    MaxWidth = 700,
                    Padding = new Thickness(15, 10, 15, 10)
                };

                messageBorder.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 8,
                    ShadowDepth = 2,
                    Color = Color.FromRgb(29, 184, 184),
                    Opacity = 0.3
                };

                StackPanel messagePanel = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };

                StackPanel headerPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 8)
                };

                TextBlock toolNameText = new TextBlock
                {
                    Text = $"Tool Call: {toolName}",
                    FontWeight = FontWeights.Bold,
                    FontSize = 14,
                    Foreground = new SolidColorBrush(Color.FromRgb(29, 184, 184)),
                    VerticalAlignment = VerticalAlignment.Center
                };
                headerPanel.Children.Add(toolNameText);

                TextBlock timeText = new TextBlock
                {
                    Text = $" | {message.DisplayTime}",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(107, 122, 148)),
                    VerticalAlignment = VerticalAlignment.Center
                };
                headerPanel.Children.Add(timeText);

                messagePanel.Children.Add(headerPanel);

                string outputText = message.Message;
                if (outputText.StartsWith("[Tool:", StringComparison.OrdinalIgnoreCase))
                {
                    int endIndex = outputText.IndexOf(']');
                    if (endIndex > 0 && endIndex < outputText.Length - 1)
                    {
                        outputText = outputText.Substring(endIndex + 1).Trim();
                    }
                }
                else if (outputText.Contains("Tool Call:", StringComparison.OrdinalIgnoreCase))
                {
                    int startIndex = outputText.IndexOf("Tool Call:", StringComparison.OrdinalIgnoreCase);
                    int newlineIndex = outputText.IndexOf('\n', startIndex);
                    if (newlineIndex > startIndex)
                    {
                        outputText = outputText.Substring(newlineIndex).Trim();
                    }
                    else
                    {
                        int spaceIndex = outputText.IndexOf(' ', startIndex + "Tool Call:".Length);
                        if (spaceIndex > startIndex)
                        {
                            outputText = outputText.Substring(spaceIndex).Trim();
                        }
                    }
                }

                if (!string.IsNullOrEmpty(outputText))
                {
                    Border outputBlock = new Border
                    {
                        Background = new SolidColorBrush(Color.FromRgb(19, 35, 64)),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(58, 74, 100)),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(10, 8, 10, 8),
                        Margin = new Thickness(0, 8, 0, 0)
                    };

                    StackPanel outputContent = new StackPanel
                    {
                        Orientation = Orientation.Vertical
                    };

                    TextBlock outputLabel = new TextBlock
                    {
                        Text = "Output:",
                        FontSize = 11,
                        FontWeight = FontWeights.Bold,
                        Foreground = new SolidColorBrush(Color.FromRgb(29, 184, 184)),
                        Margin = new Thickness(0, 0, 0, 4)
                    };
                    outputContent.Children.Add(outputLabel);

                    TextBlock outputTextBlock = new TextBlock
                    {
                        Text = outputText,
                        FontSize = 11,
                        Foreground = new SolidColorBrush(Color.FromRgb(139, 154, 175)),
                        TextWrapping = TextWrapping.Wrap,
                        LineHeight = 16
                    };
                    outputContent.Children.Add(outputTextBlock);

                    outputBlock.Child = outputContent;
                    messagePanel.Children.Add(outputBlock);
                }

                messageBorder.Child = messagePanel;
                return messageBorder;
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[ConversationHistory] Error creating tool call UI: {ex.Message}");
                return CreateFallbackMessageUI(message);
            }
        }

        private Border CreateContextualMessageUI(TranscriptMessage message)
        {
            try
            {
                Border messageBorder = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(26, 41, 66)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(128, 0, 128)),
                    BorderThickness = new Thickness(2),
                    CornerRadius = new CornerRadius(12),
                    Margin = new Thickness(50, 8, 50, 8),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    MaxWidth = 700,
                    Padding = new Thickness(15, 10, 15, 10)
                };

                messageBorder.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 8,
                    ShadowDepth = 2,
                    Color = Color.FromRgb(128, 0, 128),
                    Opacity = 0.3
                };

                StackPanel messagePanel = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };

                TextBlock headerLabel = new TextBlock
                {
                    Text = "Context",
                    FontSize = 12,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Colors.White),
                    Margin = new Thickness(0, 0, 0, 8)
                };
                messagePanel.Children.Add(headerLabel);

                string contextText = message.Message;
                if (contextText.Contains("user_contextual_message:", StringComparison.OrdinalIgnoreCase))
                {
                    int index = contextText.IndexOf("user_contextual_message:", StringComparison.OrdinalIgnoreCase);
                    contextText = contextText.Substring(index + "user_contextual_message:".Length).Trim();
                }
                else if (contextText.StartsWith("Context:", StringComparison.OrdinalIgnoreCase))
                {
                    contextText = contextText.Substring("Context:".Length).Trim();
                }

                TextBlock textBlock = new TextBlock
                {
                    Text = contextText,
                    FontSize = 13,
                    Foreground = new SolidColorBrush(Colors.White),
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 18,
                    Margin = new Thickness(0, 0, 0, 8)
                };
                messagePanel.Children.Add(textBlock);

                TextBlock timeText = new TextBlock
                {
                    Text = message.DisplayTime,
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(107, 122, 148)),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 8, 0, 0)
                };
                messagePanel.Children.Add(timeText);

                messageBorder.Child = messagePanel;
                return messageBorder;
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[ConversationHistory] Error creating contextual message UI: {ex.Message}");
                return CreateFallbackMessageUI(message);
            }
        }

        private Border CreateThinkingMessageUI(TranscriptMessage message)
        {
            try
            {
                Border messageBorder = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(26, 41, 66)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(58, 74, 100)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(12),
                    Margin = new Thickness(0, 0, 0, 15),
                    Padding = new Thickness(20, 15, 20, 15),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    MaxWidth = 700
                };

                var messagePanel = new StackPanel();

                var headerPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 0, 0, 8)
                };

                var roleLabel = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(107, 122, 148)),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(8, 4, 8, 4),
                    Margin = new Thickness(0, 0, 10, 0)
                };

                var roleText = new TextBlock
                {
                    Text = "Thinking",
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Colors.White)
                };
                roleLabel.Child = roleText;

                var timestampText = new TextBlock
                {
                    Text = message.DisplayTime,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(107, 122, 148)),
                    VerticalAlignment = VerticalAlignment.Center
                };

                headerPanel.Children.Add(roleLabel);
                headerPanel.Children.Add(timestampText);

                string thinkingText = message.Message;
                if (thinkingText.StartsWith("Thinking:", StringComparison.OrdinalIgnoreCase))
                {
                    thinkingText = thinkingText.Substring("Thinking:".Length).Trim();
                }

                var messageText = new TextBlock
                {
                    Text = thinkingText,
                    FontSize = 13,
                    FontStyle = FontStyles.Italic,
                    Foreground = new SolidColorBrush(Color.FromRgb(139, 154, 175)),
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 18
                };

                messagePanel.Children.Add(headerPanel);
                messagePanel.Children.Add(messageText);

                messageBorder.Child = messagePanel;
                return messageBorder;
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[ConversationHistory] Error creating thinking message UI: {ex.Message}");
                return CreateFallbackMessageUI(message);
            }
        }

        /// <summary>
        /// Creates a simple fallback message border when specialized renderers fail.
        /// </summary>
        private Border CreateFallbackMessageUI(TranscriptMessage message)
        {
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(26, 41, 66)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(58, 74, 100)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Margin = new Thickness(0, 0, 0, 15),
                Padding = new Thickness(20, 15, 20, 15),
                MaxWidth = 700
            };

            var textBlock = new TextBlock
            {
                Text = message?.Message ?? "(empty message)",
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromRgb(139, 154, 175)),
                TextWrapping = TextWrapping.Wrap
            };

            border.Child = textBlock;
            return border;
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadConversationsAsync();
        }

        /// <summary>
        /// Public method to allow external callers (e.g., MainWindow sidebar refresh button)
        /// to trigger a conversation list refresh.
        /// </summary>
        public async void RefreshConversations()
        {
            await LoadConversationsAsync();
        }

        private async void LoadMoreButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadMoreConversationsAsync();
        }

        private async Task LoadMoreConversationsAsync()
        {
            if (!_isHistoryApiAvailable || _isLoadingMore || !_hasMore)
            {
                return;
            }

            try
            {
                _isLoadingMore = true;
                btnLoadMore.IsEnabled = false;
                btnLoadMore.Content = "Loading...";
                LoadingProgressBar.Visibility = Visibility.Visible;

                LoggingService.Info($"[ConversationHistory] Loading more conversations with offset: {_currentOffset}");

                var items = await FetchConversationListAsync(_pageLimit, _currentOffset);

                if (items != null && items.Count > 0)
                {
                    _conversations.AddRange(items);
                    _currentOffset += items.Count;
                    _hasMore = items.Count >= _pageLimit;

                    ConversationsList.ItemsSource = null;
                    ConversationsList.ItemsSource = _conversations;

                    txtStatus.Text = $"Loaded {_conversations.Count} conversations";
                    btnLoadMore.Visibility = _hasMore ? Visibility.Visible : Visibility.Collapsed;

                    LoggingService.Info($"[ConversationHistory] Loaded {items.Count} more conversations (total={_conversations.Count}, hasMore={_hasMore})");
                }
                else
                {
                    _hasMore = false;
                    btnLoadMore.Visibility = Visibility.Collapsed;
                    txtStatus.Text = "No more conversations to load";
                    LoggingService.Info("[ConversationHistory] No more conversations available");
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[ConversationHistory] Error loading more conversations: {ex.Message}");
                txtStatus.Text = $"Error: {ex.Message}";
            }
            finally
            {
                _isLoadingMore = false;
                btnLoadMore.IsEnabled = true;
                btnLoadMore.Content = "Load More";
                LoadingProgressBar.Visibility = Visibility.Collapsed;
            }
        }

        private void btnBack_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                LoggingService.Info("[ConversationHistory] Navigating back to call page");

                var mainWindow = Window.GetWindow(this) as MainWindow;
                if (mainWindow != null)
                {
                    mainWindow.GoToVoiceSessionPageFromHistory();
                }
                else
                {
                    LoggingService.Error("[ConversationHistory] Could not find MainWindow for navigation");
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[ConversationHistory] Error navigating back: {ex.Message}");
            }
        }

        private void CloseTranscript_Click(object sender, RoutedEventArgs e)
        {
            TranscriptModal.Visibility = Visibility.Collapsed;
            TranscriptPanel.Children.Clear();
            LoggingService.Info("[ConversationHistory] Transcript modal closed");
        }
    }
}
