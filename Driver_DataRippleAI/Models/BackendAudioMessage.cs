using System.Text.Json.Serialization;

namespace DataRippleAIDesktop.Models
{
    /// <summary>
    /// Enumerates the types of messages sent to the backend audio streaming WebSocket.
    /// </summary>
    public enum BackendAudioMessageType
    {
        AudioChunk,
        SessionStart,
        SessionEnd,
        Ping
    }

    /// <summary>
    /// Identifies the audio source stream.
    /// </summary>
    public enum AudioSourceType
    {
        Microphone,
        Speaker
    }

    /// <summary>
    /// Audio configuration sent with session_start messages.
    /// </summary>
    public class AudioConfig
    {
        [JsonPropertyName("sample_rate")]
        public int SampleRate { get; set; } = 16000;

        [JsonPropertyName("bit_depth")]
        public int BitDepth { get; set; } = 16;

        [JsonPropertyName("channels")]
        public int Channels { get; set; } = 1;

        [JsonPropertyName("format")]
        public string Format { get; set; } = "pcm";
    }

    /// <summary>
    /// Outgoing audio chunk message sent to the backend.
    /// </summary>
    public class BackendAudioChunkMessage
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "audio_chunk";

        [JsonPropertyName("source")]
        public string Source { get; set; } // "microphone" or "speaker"

        [JsonPropertyName("timestamp")]
        public long Timestamp { get; set; }

        [JsonPropertyName("sequence")]
        public long Sequence { get; set; }

        [JsonPropertyName("audio_data")]
        public string AudioData { get; set; } // base64-encoded PCM

        [JsonPropertyName("sample_rate")]
        public int SampleRate { get; set; }

        [JsonPropertyName("format")]
        public string Format { get; set; } = "pcm";
    }

    /// <summary>
    /// Session start message sent to the backend when audio streaming begins.
    /// </summary>
    public class BackendSessionStartMessage
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "session_start";

        [JsonPropertyName("timestamp")]
        public long Timestamp { get; set; }

        [JsonPropertyName("audio_config")]
        public AudioConfig AudioConfig { get; set; }
    }

    /// <summary>
    /// Session end message sent to the backend when audio streaming stops.
    /// </summary>
    public class BackendSessionEndMessage
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "session_end";

        [JsonPropertyName("timestamp")]
        public long Timestamp { get; set; }
    }

    /// <summary>
    /// Ping message for health checking the WebSocket connection.
    /// </summary>
    public class BackendPingMessage
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "ping";

        [JsonPropertyName("timestamp")]
        public long Timestamp { get; set; }
    }
}
