using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DataRippleAIDesktop.Models
{
    /// <summary>
    /// Represents a conversation list item for history display.
    /// Used by ConversationHistoryPage to display past sessions.
    /// Properties are mapped from backend API responses.
    /// </summary>
    public class ConversationListItem
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("conversation_id")]
        public string ConversationId { get; set; }

        [JsonPropertyName("agent_id")]
        public string AgentId { get; set; }

        [JsonPropertyName("agent_name")]
        public string AgentName { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; }

        [JsonPropertyName("call_successful")]
        public string CallSuccessful { get; set; }

        [JsonPropertyName("start_time_unix_secs")]
        public long StartTimeUnixSecs { get; set; }

        [JsonPropertyName("call_duration_secs")]
        public int CallDurationSecs { get; set; }

        [JsonPropertyName("message_count")]
        public int MessageCount { get; set; }

        // Computed display properties

        /// <summary>
        /// DateTime representation of StartTimeUnixSecs for DataGrid date binding.
        /// </summary>
        public DateTime CreatedAtDateTime
        {
            get
            {
                if (StartTimeUnixSecs > 0)
                {
                    return DateTimeOffset.FromUnixTimeSeconds(StartTimeUnixSecs).LocalDateTime;
                }
                return DateTime.MinValue;
            }
        }

        public string DisplayName
        {
            get
            {
                if (StartTimeUnixSecs > 0)
                {
                    return CreatedAtDateTime.ToString("MMM dd, yyyy HH:mm");
                }
                return ConversationId ?? "Unknown";
            }
        }

        public string DisplayDuration
        {
            get
            {
                if (CallDurationSecs <= 0) return "N/A";
                var ts = TimeSpan.FromSeconds(CallDurationSecs);
                return ts.TotalHours >= 1
                    ? $"{(int)ts.TotalHours}h {ts.Minutes}m {ts.Seconds}s"
                    : ts.TotalMinutes >= 1
                        ? $"{ts.Minutes}m {ts.Seconds}s"
                        : $"{ts.Seconds}s";
            }
        }

        /// <summary>
        /// Human-readable display of the CallSuccessful status for DataGrid binding.
        /// </summary>
        public string DisplayStatus
        {
            get
            {
                if (string.IsNullOrEmpty(CallSuccessful)) return "N/A";
                return CallSuccessful switch
                {
                    "success" => "Success",
                    "unknown" => "Unknown",
                    "failure" => "Failed",
                    _ => CallSuccessful
                };
            }
        }
    }

    /// <summary>
    /// Represents a single transcript message in a conversation.
    /// Used by ConversationHistoryPage to render individual messages in a transcript view.
    /// </summary>
    public class TranscriptMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; }

        [JsonPropertyName("time_in_call_secs")]
        public double TimeInCallSecs { get; set; }

        // Computed properties

        public bool IsAgent => string.Equals(Role, "agent", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(Role, "ai", StringComparison.OrdinalIgnoreCase);

        public bool IsToolCall => Message != null &&
                                  (Message.StartsWith("[Tool:", StringComparison.OrdinalIgnoreCase) ||
                                   Message.Contains("Tool Call:", StringComparison.OrdinalIgnoreCase));

        public bool IsContextualMessage => Message != null &&
                                           (Message.Contains("user_contextual_message:", StringComparison.OrdinalIgnoreCase) ||
                                            Message.StartsWith("Context:", StringComparison.OrdinalIgnoreCase));

        public bool IsThinkingMessage => Message != null &&
                                         Message.StartsWith("Thinking:", StringComparison.OrdinalIgnoreCase);

        public string DisplayRole
        {
            get
            {
                if (string.IsNullOrEmpty(Role)) return "Unknown";
                return Role.Substring(0, 1).ToUpper() + Role.Substring(1).ToLower();
            }
        }

        public string DisplayTime
        {
            get
            {
                var ts = TimeSpan.FromSeconds(TimeInCallSecs);
                return ts.TotalMinutes >= 1
                    ? $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}"
                    : $"0:{ts.Seconds:D2}";
            }
        }

        /// <summary>
        /// Extract tool name from message text
        /// </summary>
        public string ExtractToolName()
        {
            if (string.IsNullOrEmpty(Message)) return null;

            // Format: [Tool: tool_name] ...
            if (Message.StartsWith("[Tool:", StringComparison.OrdinalIgnoreCase))
            {
                int endIndex = Message.IndexOf(']');
                if (endIndex > 6)
                {
                    return Message.Substring(6, endIndex - 6).Trim();
                }
            }

            // Format: Tool Call: tool_name ...
            if (Message.Contains("Tool Call:", StringComparison.OrdinalIgnoreCase))
            {
                int startIndex = Message.IndexOf("Tool Call:", StringComparison.OrdinalIgnoreCase) + "Tool Call:".Length;
                int endIndex = Message.IndexOf('\n', startIndex);
                if (endIndex < 0) endIndex = Math.Min(startIndex + 50, Message.Length);
                return Message.Substring(startIndex, endIndex - startIndex).Trim();
            }

            return null;
        }
    }

    /// <summary>
    /// Represents AI agent thinking/reasoning information associated with a tool call.
    /// </summary>
    public class ThinkingInfo
    {
        public string Summary { get; set; }
        public string Plan { get; set; }
    }

    /// <summary>
    /// Response wrapper for the backend conversation-logs list API.
    /// Backend returns: { status, message, data: [...] }
    /// </summary>
    public class ConversationListResponse
    {
        [JsonPropertyName("status")]
        public string Status { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; }

        [JsonPropertyName("data")]
        public List<ConversationListItem> Data { get; set; }
    }

    /// <summary>
    /// Conversation detail with transcript messages.
    /// Parsed from backend response where events array contains the transcript.
    /// </summary>
    public class ConversationDetail
    {
        [JsonPropertyName("conversation_id")]
        public string ConversationId { get; set; }

        /// <summary>
        /// Transcript messages parsed from the backend's "events" array.
        /// </summary>
        public List<TranscriptMessage> Transcript { get; set; }
    }
}
