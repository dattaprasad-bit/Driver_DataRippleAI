using System.Text.Json.Serialization;

namespace DataRippleAIDesktop.Models
{
    /// <summary>
    /// State machine for the driver WebSocket call notification protocol.
    /// </summary>
    public enum CallNotificationState
    {
        Disconnected,
        Connected,
        Ringing,
        Active
    }

    // =====================================================================
    // Events SENT by Driver (to Backend)
    // =====================================================================

    /// <summary>
    /// Sent when a new phone call is detected by the driver.
    /// </summary>
    public class CallIncomingOutbound
    {
        [JsonPropertyName("event_type")]
        public string EventType { get; set; } = "call_incoming";

        [JsonPropertyName("call_id")]
        public string CallId { get; set; }

        [JsonPropertyName("csr_id")]
        public string CsrId { get; set; }

        [JsonPropertyName("customer")]
        public CustomerInfo Customer { get; set; }

        [JsonPropertyName("employee")]
        public EmployeeInfo Employee { get; set; }

        [JsonPropertyName("timestamp")]
        public string Timestamp { get; set; }
    }

    /// <summary>
    /// Sent when the driver detects that the phone call has ended.
    /// </summary>
    public class CallEndedOutbound
    {
        [JsonPropertyName("event_type")]
        public string EventType { get; set; } = "call_ended";

        [JsonPropertyName("call_id")]
        public string CallId { get; set; }

        [JsonPropertyName("reason")]
        public string Reason { get; set; } = "completed";

        [JsonPropertyName("timestamp")]
        public string Timestamp { get; set; }
    }

    // =====================================================================
    // Events RECEIVED by Driver (from Backend)
    // =====================================================================

    /// <summary>
    /// Received immediately after the WebSocket connection is authenticated.
    /// </summary>
    public class CallNotificationConnectedEvent
    {
        [JsonPropertyName("event_type")]
        public string EventType { get; set; }

        [JsonPropertyName("user_id")]
        public string UserId { get; set; }

        [JsonPropertyName("timestamp")]
        public string Timestamp { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; }
    }

    /// <summary>
    /// ACK for call_incoming: backend registered the call and notified the dashboard.
    /// </summary>
    public class CallIncomingAckEvent
    {
        [JsonPropertyName("event_type")]
        public string EventType { get; set; }

        [JsonPropertyName("call_id")]
        public string CallId { get; set; }

        [JsonPropertyName("session_id")]
        public string SessionId { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; }
    }

    /// <summary>
    /// Received when the dashboard user accepts the call.
    /// </summary>
    public class CallAcceptedEvent
    {
        [JsonPropertyName("event_type")]
        public string EventType { get; set; }

        [JsonPropertyName("call_id")]
        public string CallId { get; set; }

        [JsonPropertyName("session_id")]
        public string SessionId { get; set; }

        [JsonPropertyName("conversation_id")]
        public string ConversationId { get; set; }

        [JsonPropertyName("agent_id")]
        public string AgentId { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; }

        [JsonPropertyName("started_at")]
        public string StartedAt { get; set; }
    }

    /// <summary>
    /// Received when the dashboard user rejects the call.
    /// </summary>
    public class CallRejectedEvent
    {
        [JsonPropertyName("event_type")]
        public string EventType { get; set; }

        [JsonPropertyName("call_id")]
        public string CallId { get; set; }

        [JsonPropertyName("reason")]
        public string Reason { get; set; }

        [JsonPropertyName("timestamp")]
        public string Timestamp { get; set; }
    }

    /// <summary>
    /// Received when the call has ended (from dashboard or confirmation).
    /// </summary>
    public class CallEndedInboundEvent
    {
        [JsonPropertyName("event_type")]
        public string EventType { get; set; }

        [JsonPropertyName("call_id")]
        public string CallId { get; set; }

        [JsonPropertyName("conversation_id")]
        public string ConversationId { get; set; }

        [JsonPropertyName("reason")]
        public string Reason { get; set; }

        [JsonPropertyName("duration_secs")]
        public int DurationSecs { get; set; }
    }

    /// <summary>
    /// Error event from the backend.
    /// </summary>
    public class CallNotificationErrorEvent
    {
        [JsonPropertyName("event_type")]
        public string EventType { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; }
    }
}
