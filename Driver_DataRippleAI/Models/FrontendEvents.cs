using System;
using System.Text.Json.Serialization;

namespace DataRippleAIDesktop.Models
{
    public class EmployeeInfo
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("email")]
        public string Email { get; set; }

        [JsonPropertyName("company_id")]
        public string CompanyId { get; set; }

        [JsonPropertyName("company_name")]
        public string CompanyName { get; set; }
    }

    public class CustomerInfo
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("customerId")]
        public string CustomerId { get; set; }

        [JsonPropertyName("phone_e164")]
        public string PhoneE164 { get; set; } = "+919876543210"; // Default fallback

        [JsonPropertyName("email")]
        public string Email { get; set; }
    }

    /// <summary>
    /// Event sent when an incoming call is detected and ringing
    /// </summary>
    public class IncomingCallRingingEvent
    {
        [JsonPropertyName("event_type")]
        public string EventType { get; set; } = "incoming_call_ringing";

        [JsonPropertyName("call_id")]
        public string CallId { get; set; }

        [JsonPropertyName("customer")]
        public CustomerInfo Customer { get; set; }

        [JsonPropertyName("received_at")]
        public string ReceivedAt { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        [JsonPropertyName("meta")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object Meta { get; set; }
    }

    public class CallStartedEvent
    {
        [JsonPropertyName("event_type")]
        public string EventType { get; set; } = "call_started";

        [JsonPropertyName("call_id")]
        public string CallId { get; set; }

        [JsonPropertyName("conversation_id")]
        public string ConversationId { get; set; }

        [JsonPropertyName("session_id")]
        public string SessionId { get; set; }

        [JsonPropertyName("agent_id")]
        public string AgentId { get; set; }

        [JsonPropertyName("user_id")]
        public string UserId { get; set; }

        [JsonPropertyName("employee")]
        public EmployeeInfo Employee { get; set; }

        [JsonPropertyName("customer")]
        public CustomerInfo Customer { get; set; }

        [JsonPropertyName("started_at")]
        public string StartedAt { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        [JsonPropertyName("received_at")]
        public string ReceivedAt { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        [JsonPropertyName("meta")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object Meta { get; set; }
    }

    public class TranscriptTurnEvent
    {
        [JsonPropertyName("event_type")]
        public string EventType { get; set; } = "transcript_turn";

        [JsonPropertyName("conversation_id")]
        public string ConversationId { get; set; }

        [JsonPropertyName("turn_id")]
        public string TurnId { get; set; }

        [JsonPropertyName("speaker")]
        public string Speaker { get; set; } // "caller" | "CSR"

        [JsonPropertyName("text")]
        public string Text { get; set; }

        [JsonPropertyName("timestamp")]
        public string Timestamp { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        // Optional sentiment field (not included by default)
        [JsonPropertyName("sentiment")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public SentimentInfo Sentiment { get; set; }

        [JsonPropertyName("meta")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object Meta { get; set; }
    }

    public class SentimentInfo
    {
        [JsonPropertyName("label")]
        public string Label { get; set; } = "neutral";

        [JsonPropertyName("score")]
        public double Score { get; set; } = 0.5;
    }

    /// <summary>
    /// Event sent when AI agent decides to call a tool (BEFORE execution)
    /// </summary>
    public class ToolCallStartedEvent
    {
        [JsonPropertyName("event_type")]
        public string EventType { get; set; } = "tool_call_started";

        [JsonPropertyName("conversation_id")]
        public string ConversationId { get; set; }

        [JsonPropertyName("tool_call_id")]
        public string ToolCallId { get; set; }

        [JsonPropertyName("tool_name")]
        public string ToolName { get; set; }

        [JsonPropertyName("input")]
        public object Input { get; set; }

        [JsonPropertyName("emitted_at")]
        public string EmittedAt { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

        [JsonPropertyName("meta")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object Meta { get; set; }
    }

    /// <summary>
    /// Event sent when tool execution completes
    /// </summary>
    public class ToolCallCompletedEvent
    {
        [JsonPropertyName("event_type")]
        public string EventType { get; set; } = "tool_call_completed";

        [JsonPropertyName("conversation_id")]
        public string ConversationId { get; set; }

        [JsonPropertyName("tool_call_id")]
        public string ToolCallId { get; set; }

        [JsonPropertyName("tool_name")]
        public string ToolName { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; } // "success" | "error"

        [JsonPropertyName("output")]
        public string Output { get; set; }

        [JsonPropertyName("completed_at")]
        public string CompletedAt { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

        [JsonPropertyName("meta")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object Meta { get; set; }
    }

    /// <summary>
    /// Event sent to show what the AI understood and what it's about to do
    /// Format matches frontend specification with simple thinking string
    /// </summary>
    public class AgentThinkingEvent
    {
        [JsonPropertyName("event_type")]
        public string EventType { get; set; } = "agent_thinking";

        [JsonPropertyName("conversation_id")]
        public string ConversationId { get; set; }

        [JsonPropertyName("turn_id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string TurnId { get; set; }

        [JsonPropertyName("speaker")]
        public string Speaker { get; set; } // "caller" | "CSR"

        [JsonPropertyName("thinking")]
        public string Thinking { get; set; }

        [JsonPropertyName("timestamp")]
        public string Timestamp { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        [JsonPropertyName("meta")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object Meta { get; set; }
    }

    /// <summary>
    /// Event sent before tool_call_started to show AI's understanding and intention
    /// </summary>
    public class AgentThoughtProcessEvent
    {
        [JsonPropertyName("event_type")]
        public string EventType { get; set; } = "agent_thought_process";

        [JsonPropertyName("conversation_id")]
        public string ConversationId { get; set; }

        [JsonPropertyName("thought")]
        public ThoughtObject Thought { get; set; }

        [JsonPropertyName("emitted_at")]
        public string EmittedAt { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

        [JsonPropertyName("meta")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object Meta { get; set; }
    }

    public class ThoughtObject
    {
        [JsonPropertyName("intention")]
        public string Intention { get; set; }

        [JsonPropertyName("action")]
        public string Action { get; set; }

        [JsonPropertyName("tool_name")]
        public string ToolName { get; set; }
    }

    public class CallEndedEvent
    {
        [JsonPropertyName("event_type")]
        public string EventType { get; set; } = "call_ended";

        [JsonPropertyName("conversation_id")]
        public string ConversationId { get; set; }

        [JsonPropertyName("session_id")]
        public string SessionId { get; set; }

        [JsonPropertyName("reason")]
        public string Reason { get; set; } = "completed";

        [JsonPropertyName("ended_at")]
        public string EndedAt { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        [JsonPropertyName("duration_secs")]
        public int DurationSecs { get; set; }

        [JsonPropertyName("meta")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object Meta { get; set; }
    }

    /// <summary>
    /// Event received from frontend when user accepts incoming call
    /// </summary>
    public class FrontendCallAcceptEvent
    {
        [JsonPropertyName("event_type")]
        public string EventType { get; set; }

        [JsonPropertyName("call_id")]
        public string CallId { get; set; }

        [JsonPropertyName("conversation_id")]
        public string ConversationId { get; set; }

        [JsonPropertyName("session_id")]
        public string SessionId { get; set; }

        [JsonPropertyName("user_id")]
        public string UserId { get; set; }

        [JsonPropertyName("received_at")]
        public string ReceivedAt { get; set; }

        [JsonPropertyName("accepted_at")]
        public string AcceptedAt { get; set; }
    }

    /// <summary>
    /// Event received from frontend when user rejects incoming call
    /// </summary>
    public class FrontendCallRejectEvent
    {
        [JsonPropertyName("event_type")]
        public string EventType { get; set; }

        [JsonPropertyName("call_id")]
        public string CallId { get; set; }

        [JsonPropertyName("conversation_id")]
        public string ConversationId { get; set; }

        [JsonPropertyName("session_id")]
        public string SessionId { get; set; }

        [JsonPropertyName("user_id")]
        public string UserId { get; set; }

        [JsonPropertyName("reason")]
        public string Reason { get; set; }

        [JsonPropertyName("received_at")]
        public string ReceivedAt { get; set; }

        [JsonPropertyName("rejected_at")]
        public string RejectedAt { get; set; }
    }

    /// <summary>
    /// Event received from frontend when user wants to end active call
    /// </summary>
    public class FrontendCallEndEvent
    {
        [JsonPropertyName("event_type")]
        public string EventType { get; set; }

        [JsonPropertyName("call_id")]
        public string CallId { get; set; }

        [JsonPropertyName("conversation_id")]
        public string ConversationId { get; set; }

        [JsonPropertyName("session_id")]
        public string SessionId { get; set; }

        [JsonPropertyName("user_id")]
        public string UserId { get; set; }

        [JsonPropertyName("reason")]
        public string Reason { get; set; }

        [JsonPropertyName("received_at")]
        public string ReceivedAt { get; set; }

        [JsonPropertyName("ended_at")]
        public string EndedAt { get; set; }

        [JsonPropertyName("meta")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object Meta { get; set; }
    }

    /// <summary>
    /// Health ping event sent from Driver (backend) to Frontend
    /// Format matches transcript_turn and agent_thinking events
    /// </summary>
    public class HealthPingEvent
    {
        [JsonPropertyName("event_type")]
        public string EventType { get; set; } = "health_ping";

        [JsonPropertyName("conversation_id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string ConversationId { get; set; }

        [JsonPropertyName("timestamp")]
        public string Timestamp { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        [JsonPropertyName("meta")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object Meta { get; set; }
    }

    /// <summary>
    /// Health pong event sent from Frontend to Driver (backend) in response to health_ping
    /// </summary>
    public class HealthPongEvent
    {
        [JsonPropertyName("event_type")]
        public string EventType { get; set; } = "health_pong";

        [JsonPropertyName("timestamp")]
        public string Timestamp { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        [JsonPropertyName("source")]
        public string Source { get; set; } = "frontend";

        [JsonPropertyName("user_id")]
        public string UserId { get; set; }
    }

    /// <summary>
    /// Event sent when user provides contextual information
    /// </summary>
    public class UserContextualMessageEvent
    {
        [JsonPropertyName("event_type")]
        public string EventType { get; set; } = "user_contextual_message";

        [JsonPropertyName("conversation_id")]
        public string ConversationId { get; set; }

        [JsonPropertyName("text")]
        public string Text { get; set; }

        [JsonPropertyName("timestamp")]
        public string Timestamp { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        [JsonPropertyName("meta")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object Meta { get; set; }
    }
}

