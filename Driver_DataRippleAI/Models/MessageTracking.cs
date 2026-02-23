using System;

namespace DataRippleAIDesktop.Models
{
    /// <summary>
    /// Message tracking entry with status and sequential index
    /// </summary>
    public class MessageTrackingEntry
    {
        public string TurnId { get; set; } // Generated contextual turn_id
        public string Speaker { get; set; } // "CSR", "caller", or "user_contextual_message"
        public string Status { get; set; } // "pending", "sent", "completed"
        public int Index { get; set; } // Sequential index number
        public string MessageId { get; set; } // Internal message ID
        public string Text { get; set; } // Original message text
        public DateTime Timestamp { get; set; } // When message was created
        public bool IsCustomer { get; set; } // true = Caller, false = CSR
        public bool IsContextual { get; set; } // true = user_contextual_message
    }
}

