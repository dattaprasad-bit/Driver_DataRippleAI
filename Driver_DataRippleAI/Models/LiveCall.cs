using System;
using System.Text.Json.Serialization;
using Newtonsoft.Json;

public class TranscriptionSession
{
    [JsonProperty("session_name")]
    public string SessionName { get; set; }

    [JsonProperty("session_type")]
    public string SessionType { get; set; }

    [JsonProperty("id")]
    public Guid Id { get; set; }

    [JsonProperty("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonProperty("has_audio")]
    public bool HasAudio { get; set; }

    [JsonProperty("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }
}

// Main class for live call sessions
public class LiveCall : TranscriptionSession
{
    [JsonProperty("consultation_name")]
    public string ConsultationName 
    { 
        get => SessionName; 
        set => SessionName = value; 
    }

    [JsonProperty("consultation_type")]
    public string ConsultationType 
    { 
        get => SessionType; 
        set => SessionType = value; 
    }

    // Only keep properties that are actually used
    public DateTime StartTime { get; set; } = DateTime.Now;
}
