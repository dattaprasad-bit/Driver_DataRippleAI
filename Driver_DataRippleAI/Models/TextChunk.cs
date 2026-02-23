using System;

namespace DataRippleAICode.Models
{
    public class TextChunk
    {
        public string Id { get; set; }
        public string Text { get; set; }
        public string Speaker { get; set; }
        public bool IsCustomer { get; set; }
        public DateTime Timestamp { get; set; }
        public TimeSpan Duration { get; set; }
        public ChunkingStrategy Strategy { get; set; }
        
        // Additional timing properties
        public TimeSpan StartTime { get; set; }
        public TimeSpan EndTime { get; set; }

        public TextChunk()
        {
            Id = Guid.NewGuid().ToString();
            Timestamp = DateTime.UtcNow;
        }

        public TextChunk(string text, string speaker, bool isCustomer, DateTime timestamp, ChunkingStrategy strategy = ChunkingStrategy.Sentence)
        {
            Id = Guid.NewGuid().ToString();
            Text = text ?? "";
            Speaker = speaker ?? "Unknown";
            IsCustomer = isCustomer;
            Timestamp = timestamp;
            Strategy = strategy;
        }

        public override string ToString()
        {
            return $"[{Speaker}] {Text}";
        }
    }

    public enum ChunkingStrategy
    {
        Fixed,           // Fixed time intervals (e.g., 3-10 seconds)
        SpeakerTurn,     // Complete speaker turns
        AlternatingSpeaker, // One turn from each speaker alternately
        Sentence,        // Sentence boundaries
        Paragraph,       // Paragraph boundaries
        SpeakerBased,    // Speaker-based chunking
        VADBased,        // Voice Activity Detection based chunking
        TimeBased,       // Time-based chunking (used by TextChunkingDemo)
        TurnBased,       // Turn-based chunking (used by TextChunkingDemo)
        Hybrid           // Hybrid chunking strategy (used by TextChunkingDemo)
    }

    public class ChunkingConfiguration
    {
        public ChunkingStrategy Strategy { get; set; } = ChunkingStrategy.Sentence;
        public TimeSpan FixedDuration { get; set; } = TimeSpan.FromSeconds(5);
        public TimeSpan TimeChunkDuration { get; set; } = TimeSpan.FromSeconds(5);
        public TimeSpan MinChunkDuration { get; set; } = TimeSpan.FromSeconds(1);
        public TimeSpan MaxChunkDuration { get; set; } = TimeSpan.FromSeconds(30);
        public bool MergeShortChunks { get; set; } = true;
        public int MaxChunkLength { get; set; } = 500; // characters
        public bool PreserveSpeakerBoundaries { get; set; } = true;
        
        // Additional properties used by TextChunkingDemo
        public bool EnableAutoMerge { get; set; } = true;
        public TimeSpan SpeakerChangeThreshold { get; set; } = TimeSpan.FromSeconds(1);
        public TimeSpan SilenceThreshold { get; set; } = TimeSpan.FromSeconds(2);

        public ChunkingConfiguration()
        {
        }

        public ChunkingConfiguration(ChunkingStrategy strategy, TimeSpan? fixedDuration = null)
        {
            Strategy = strategy;
            if (fixedDuration.HasValue)
                FixedDuration = fixedDuration.Value;
        }
    }
}
