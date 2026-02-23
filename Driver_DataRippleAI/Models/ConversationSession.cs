using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace DataRippleAICode.Models
{
    /// <summary>
    /// Represents a real-time conversation session with unique ID and context
    /// </summary>
    public class ConversationSession
    {
        [JsonProperty("session_id")]
        public string SessionId { get; set; } = "Need to check";

        [JsonProperty("agent_id")]
        public string AgentId { get; set; }

        [JsonProperty("driver_id")]
        public string DriverId { get; set; }

        [JsonProperty("session_name")]
        public string SessionName { get; set; }

        [JsonProperty("session_type")]
        public string SessionType { get; set; }

        [JsonProperty("created_at")]
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        [JsonProperty("updated_at")]
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

        [JsonProperty("status")]
        public SessionStatus Status { get; set; } = SessionStatus.Active;

        [JsonProperty("transcript_chunks")]
        public List<TranscriptChunk> TranscriptChunks { get; set; } = new List<TranscriptChunk>();

        [JsonProperty("ai_responses")]
        public List<AIResponse> AIResponses { get; set; } = new List<AIResponse>();

        [JsonProperty("metadata")]
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Represents a single transcript chunk with full context
    /// </summary>
    public class TranscriptChunk
    {
        [JsonProperty("chunk_id")]
        public string ChunkId { get; set; } = Guid.NewGuid().ToString("N");

        [JsonProperty("session_id")]
        public string SessionId { get; set; }

        [JsonProperty("text")]
        public string Text { get; set; }

        [JsonProperty("speaker")]
        public string Speaker { get; set; }

        [JsonProperty("is_customer")]
        public bool IsCustomer { get; set; }

        [JsonProperty("timestamp")]
        public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

        [JsonProperty("sequence_number")]
        public int SequenceNumber { get; set; }

        [JsonProperty("confidence")]
        public double? Confidence { get; set; }

        [JsonProperty("duration_ms")]
        public int? DurationMs { get; set; }

        [JsonProperty("source")]
        public string Source { get; set; } // "Backend", etc.

        [JsonProperty("is_final")]
        public bool IsFinal { get; set; } = true;

        [JsonProperty("audio_chunk_id")]
        public string AudioChunkId { get; set; }
    }

    /// <summary>
    /// Represents an AI response from the backend agent
    /// </summary>
    public class AIResponse
    {
        [JsonProperty("response_id")]
        public string ResponseId { get; set; } = Guid.NewGuid().ToString("N");

        [JsonProperty("session_id")]
        public string SessionId { get; set; }

        [JsonProperty("text")]
        public string Text { get; set; }

        [JsonProperty("agent_type")]
        public string AgentType { get; set; } // "MIC" or "SPEAKER"

        [JsonProperty("timestamp")]
        public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

        [JsonProperty("triggered_by_chunk_id")]
        public string TriggeredByChunkId { get; set; }

        [JsonProperty("response_time_ms")]
        public int? ResponseTimeMs { get; set; }

        [JsonProperty("metadata")]
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    public enum SessionStatus
    {
        Active,
        Paused,
        Completed,
        Error,
        Cancelled
    }
}


