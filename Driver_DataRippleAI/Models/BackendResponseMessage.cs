using System.Text.Json.Serialization;

namespace DataRippleAIDesktop.Models
{
    /// <summary>
    /// Enumerates the types of messages received from the backend audio streaming WebSocket.
    /// </summary>
    public enum BackendResponseMessageType
    {
        Transcript,
        AgentResponse,
        Error,
        SessionStatus,
        Pong
    }

    /// <summary>
    /// Transcript message received from the backend.
    /// Contains transcribed text with speaker identification.
    /// </summary>
    public class BackendTranscriptMessage
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "transcript";

        [JsonPropertyName("text")]
        public string Text { get; set; }

        [JsonPropertyName("is_final")]
        public bool IsFinal { get; set; }

        [JsonPropertyName("source")]
        public string Source { get; set; } // "microphone" or "speaker" - identifies which stream the transcript came from

        [JsonPropertyName("speaker")]
        public string Speaker { get; set; } // "agent" or "customer"

        [JsonPropertyName("timestamp")]
        public long Timestamp { get; set; }

        [JsonPropertyName("turn_id")]
        public string TurnId { get; set; }

        [JsonPropertyName("confidence")]
        public double? Confidence { get; set; }
    }

    /// <summary>
    /// Agent response message received from the backend AI.
    /// </summary>
    public class BackendAgentResponseMessage
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "agent_response";

        [JsonPropertyName("text")]
        public string Text { get; set; }

        [JsonPropertyName("timestamp")]
        public long Timestamp { get; set; }

        [JsonPropertyName("conversation_id")]
        public string ConversationId { get; set; }

        [JsonPropertyName("is_delta")]
        public bool IsDelta { get; set; } // true if this is a streaming delta chunk

        [JsonPropertyName("is_final")]
        public bool IsFinal { get; set; } // true if this is the final response
    }

    /// <summary>
    /// Error message received from the backend.
    /// </summary>
    public class BackendErrorMessage
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "error";

        [JsonPropertyName("message")]
        public string Message { get; set; }

        [JsonPropertyName("code")]
        public string Code { get; set; }

        [JsonPropertyName("timestamp")]
        public long Timestamp { get; set; }
    }

    /// <summary>
    /// Session status message received from the backend.
    /// </summary>
    public class BackendSessionStatusMessage
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "session_status";

        [JsonPropertyName("status")]
        public string Status { get; set; } // e.g., "ready", "processing", "ended"

        [JsonPropertyName("session_id")]
        public string SessionId { get; set; }

        [JsonPropertyName("timestamp")]
        public long Timestamp { get; set; }
    }

    /// <summary>
    /// Pong response to a ping health check.
    /// </summary>
    public class BackendPongMessage
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "pong";

        [JsonPropertyName("timestamp")]
        public long Timestamp { get; set; }
    }
}
