using System;
using System.Windows.Controls;
using System.Windows.Shapes;

namespace DataRippleAIDesktop.Models
{
    public class ConversationMessage
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8]; // Unique identifier
        public string Text { get; set; }
        public bool IsCaller { get; set; } // true = Caller, false = Agent
        public DateTime Timestamp { get; set; }
        public Border MessageBorder { get; set; }
        public TextBlock MessageTextBlock { get; set; } // Legacy - kept for compatibility
        public string FullText { get; set; } // Combined text for customer messages

        // New dual text block support (using TextBox for copyable text)
        public TextBox TranscriptionTextBlock { get; set; } // Backend STT transcription
        public TextBox AIResponseTextBlock { get; set; } // Backend AI agent response
        public bool HasAIResponse { get; set; } = false; // Track if AI has responded

        // Partial text for real-time transcription updates
        public string PartialText { get; set; } = ""; // Temporary partial text (not stored permanently)

        // Turn ID for tracking conversation turns (from backend or locally generated)
        public string TurnId { get; set; } = null;

        // Confidence score from backend STT (0.0 - 1.0), null if not available
        public double? Confidence { get; set; } = null;

        // Audio source from backend: "microphone" (agent) or "speaker" (customer)
        public string Source { get; set; } = null;

        // Speaker label from backend: "agent" or "customer"
        public string Speaker { get; set; } = null;

        // Delta streaming flags for agent responses built from streaming chunks
        public bool IsDelta { get; set; } = false;
        public bool IsFinal { get; set; } = true;

        // Conversation ID from backend (used to track which conversation this message belongs to)
        public string ConversationId { get; set; } = null;
    }
}

