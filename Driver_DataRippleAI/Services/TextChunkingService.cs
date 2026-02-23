using System;
using System.Collections.Generic;
using System.Linq;
using DataRippleAICode.Models;

namespace DataRippleAIDesktop.Services
{
    public class TextChunkingService : IDisposable
    {
        private readonly ChunkingConfiguration _configuration;
        private readonly List<TextChunk> _chunks;
        private bool _disposed = false;

        public event EventHandler<TextChunk> ChunkCreated;

        public IReadOnlyList<TextChunk> Chunks => _chunks.AsReadOnly();

        public TextChunkingService(ChunkingConfiguration configuration = null)
        {
            _configuration = configuration ?? new ChunkingConfiguration();
            _chunks = new List<TextChunk>();
        }

        public void AddTranscriptionText(string text, string speaker, bool isCustomer, TimeSpan timestamp)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            var chunk = new TextChunk(text, speaker, isCustomer, DateTime.UtcNow, ChunkingStrategy.Sentence);
            _chunks.Add(chunk);

            OnChunkCreated(chunk);
        }

        public void AddTranscriptionText(string text, string speaker, bool isCustomer, DateTime timestamp)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            var chunk = new TextChunk(text, speaker, isCustomer, timestamp, ChunkingStrategy.Sentence);
            _chunks.Add(chunk);

            OnChunkCreated(chunk);
        }

        public List<TextChunk> GetChunks(ChunkingStrategy strategy = ChunkingStrategy.Sentence)
        {
            switch (strategy)
            {
                case ChunkingStrategy.Fixed:
                    return GetFixedTimeChunks();
                case ChunkingStrategy.SpeakerTurn:
                    return GetSpeakerTurnChunks();
                case ChunkingStrategy.AlternatingSpeaker:
                    return GetAlternatingSpeakerChunks();
                default:
                    return _chunks.ToList();
            }
        }

        private List<TextChunk> GetFixedTimeChunks()
        {
            return _chunks.ToList(); // Simple implementation
        }

        private List<TextChunk> GetSpeakerTurnChunks()
        {
            var result = new List<TextChunk>();
            string currentSpeaker = null;
            var currentText = "";
            DateTime? startTime = null;

            foreach (var chunk in _chunks)
            {
                if (currentSpeaker != chunk.Speaker)
                {
                    if (!string.IsNullOrEmpty(currentText) && currentSpeaker != null && startTime.HasValue)
                    {
                        result.Add(new TextChunk(currentText.Trim(), currentSpeaker, 
                            chunk.IsCustomer, startTime.Value, ChunkingStrategy.SpeakerTurn));
                    }
                    currentSpeaker = chunk.Speaker;
                    currentText = chunk.Text;
                    startTime = chunk.Timestamp;
                }
                else
                {
                    currentText += " " + chunk.Text;
                }
            }

            // Add the last chunk
            if (!string.IsNullOrEmpty(currentText) && currentSpeaker != null && startTime.HasValue)
            {
                result.Add(new TextChunk(currentText.Trim(), currentSpeaker, 
                    false, startTime.Value, ChunkingStrategy.SpeakerTurn));
            }

            return result;
        }

        private List<TextChunk> GetAlternatingSpeakerChunks()
        {
            var speakerTurns = GetSpeakerTurnChunks();
            var result = new List<TextChunk>();

            string lastSpeaker = null;
            foreach (var turn in speakerTurns)
            {
                if (lastSpeaker != turn.Speaker)
                {
                    result.Add(turn);
                    lastSpeaker = turn.Speaker;
                }
            }

            return result;
        }

        public void ClearChunks()
        {
            _chunks.Clear();
        }

        protected virtual void OnChunkCreated(TextChunk chunk)
        {
            ChunkCreated?.Invoke(this, chunk);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                _chunks.Clear();
                _disposed = true;
            }
        }
    }
}

