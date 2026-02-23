using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataRippleAIDesktop.Services
{
    public enum AudioChunkingStrategy
    {
        Fixed,          // Fixed time intervals (e.g., 3s, 5s, 10s)
        Adaptive,       // Adaptive based on audio characteristics
        VADBased,       // Based on Voice Activity Detection
        Hybrid          // Combination of strategies
    }

    public class AudioChunk
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public byte[] AudioData { get; set; }
        public string Source { get; set; } // "Mic" or "Speaker"
        public bool IsCustomer { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan Duration => EndTime - StartTime;
        public int SequenceNumber { get; set; }
        public bool HasVoiceActivity { get; set; }
        public double AverageAmplitude { get; set; }
        public AudioChunkingStrategy ChunkType { get; set; }

        public AudioChunk()
        {
            StartTime = DateTime.UtcNow;
            EndTime = StartTime;
        }

        public AudioChunk(byte[] audioData, string source, bool isCustomer, AudioChunkingStrategy chunkType)
        {
            AudioData = audioData;
            Source = source;
            IsCustomer = isCustomer;
            ChunkType = chunkType;
            StartTime = DateTime.UtcNow;
            EndTime = StartTime;
        }

        public override string ToString()
        {
            return $"[{Source}] ({StartTime:HH:mm:ss}-{EndTime:HH:mm:ss}) {Duration.TotalSeconds:F1}s - {AudioData?.Length ?? 0} bytes";
        }
    }

    public class AudioChunkingService
    {
        private readonly AudioChunkingStrategy _strategy;
        private float _targetChunkSizeSeconds;
        private readonly float _minChunkSizeSeconds;
        private readonly float _maxChunkSizeSeconds;
        private readonly bool _enableVADBasedChunking;
        private readonly int _vadSilenceThresholdSeconds;
        private readonly int _vadVoiceThresholdSeconds;

        private readonly List<byte> _micAudioBuffer = new List<byte>();
        private readonly List<byte> _speakerAudioBuffer = new List<byte>();
        private readonly object _micLock = new object();
        private readonly object _speakerLock = new object();

        private DateTime _micChunkStartTime = DateTime.UtcNow;
        private DateTime _speakerChunkStartTime = DateTime.UtcNow;
        private DateTime _lastMicChunkTime = DateTime.UtcNow;
        private DateTime _lastSpeakerChunkTime = DateTime.UtcNow;
        private DateTime _lastMicVoiceUpdateTime = DateTime.UtcNow;  // Add this for voice state tracking
        private DateTime _lastSpeakerVoiceUpdateTime = DateTime.UtcNow;  // Add this for voice state tracking
        private bool _lastMicVoiceState = false;
        private bool _lastSpeakerVoiceState = false;
        private int _micConsecutiveVoiceSeconds = 0;
        private int _micConsecutiveSilenceSeconds = 0;
        private int _speakerConsecutiveVoiceSeconds = 0;
        private int _speakerConsecutiveSilenceSeconds = 0;

        public event EventHandler<AudioChunk> AudioChunkReady;
        public event EventHandler<string> ChunkingProgress;

        public AudioChunkingService(AudioChunkingStrategy strategy = AudioChunkingStrategy.VADBased, float? targetChunkSeconds = null, float? minChunkSeconds = null, float? maxChunkSeconds = null)
        {
            _strategy = strategy;
            
            // Use configuration values if provided, otherwise use safe defaults
            _targetChunkSizeSeconds = targetChunkSeconds ?? 0.5f; // 500ms - reduced for faster response
            _minChunkSizeSeconds = minChunkSeconds ?? (float)Globals.MinAudioChunkSizeInSeconds;
            _maxChunkSizeSeconds = maxChunkSeconds ?? (float)Globals.MaxAudioChunkSizeInSeconds;
          
            _vadSilenceThresholdSeconds = Globals.VADSilenceThresholdSeconds;
            _vadVoiceThresholdSeconds = Globals.VADVoiceThresholdSeconds;
            
            LoggingService.Info($"[AudioChunkingService] Initialized with Strategy={_strategy}, Target={_targetChunkSizeSeconds}s, Min={_minChunkSizeSeconds}s, Max={_maxChunkSizeSeconds}s");

            // Validate configuration - allow smaller chunks for STT compatibility
            if (_targetChunkSizeSeconds < 0.05f || _targetChunkSizeSeconds > _maxChunkSizeSeconds)
            {
                LoggingService.Info($"[AudioChunkingService] WARNING: Invalid target chunk size {_targetChunkSizeSeconds}s, clamping to valid range");
                _targetChunkSizeSeconds = Math.Max(0.05f, Math.Min(_targetChunkSizeSeconds, _maxChunkSizeSeconds));
            }
        }

        /// <summary>
        /// Add microphone audio data to the chunking system
        /// </summary>
        public void AddMicAudioData(byte[] audioData, bool hasVoiceActivity = false)
        {
            if (audioData == null || audioData.Length == 0)
                return;

            lock (_micLock)
            {
                _micAudioBuffer.AddRange(audioData);
                UpdateMicVoiceState(hasVoiceActivity);
                ProcessMicAudioChunking();
            }
        }

        /// <summary>
        /// Add speaker audio data to the chunking system
        /// </summary>
        public void AddSpeakerAudioData(byte[] audioData, bool hasVoiceActivity = false)
        {
            if (audioData == null || audioData.Length == 0)
                return;

            lock (_speakerLock)
            {
                _speakerAudioBuffer.AddRange(audioData);
                UpdateSpeakerVoiceState(hasVoiceActivity);
                ProcessSpeakerAudioChunking();
            }
        }

        private void UpdateMicVoiceState(bool hasVoiceActivity)
        {
            var now = DateTime.UtcNow;
            var timeSinceLastUpdate = (now - _lastMicVoiceUpdateTime).TotalSeconds;

            // Auto-detect voice activity if not provided
            if (!hasVoiceActivity)
            {
                hasVoiceActivity = DetectVoiceActivity(_micAudioBuffer);
            }

            if (hasVoiceActivity)
            {
                // Make voice detection much more responsive - increment immediately
                _micConsecutiveVoiceSeconds = Math.Max(_micConsecutiveVoiceSeconds, 1);
                _micConsecutiveSilenceSeconds = 0;
            }
            else
            {
                // Increment silence counter normally
                var timeMs = (int)(timeSinceLastUpdate * 1000);
                if (timeMs > 0)
                {
                    _micConsecutiveSilenceSeconds += timeMs / 1000;
                    if (timeMs % 1000 >= 500) _micConsecutiveSilenceSeconds++;
                }
                _micConsecutiveVoiceSeconds = 0;
            }

            _lastMicVoiceUpdateTime = now;
        }

        private void UpdateSpeakerVoiceState(bool hasVoiceActivity)
        {
            var now = DateTime.UtcNow;
            var timeSinceLastUpdate = (now - _lastSpeakerVoiceUpdateTime).TotalSeconds;

            // Auto-detect voice activity if not provided
            if (!hasVoiceActivity)
            {
                hasVoiceActivity = DetectVoiceActivity(_speakerAudioBuffer);
            }

            if (hasVoiceActivity)
            {
                // Make voice detection much more responsive - increment immediately
                _speakerConsecutiveVoiceSeconds = Math.Max(_speakerConsecutiveVoiceSeconds, 1);
                _speakerConsecutiveSilenceSeconds = 0;
            }
            else
            {
                // Increment silence counter normally
                var timeMs = (int)(timeSinceLastUpdate * 1000);
                if (timeMs > 0)
                {
                    _speakerConsecutiveSilenceSeconds += timeMs / 1000;
                    if (timeMs % 1000 >= 500) _speakerConsecutiveSilenceSeconds++;
                }
                _speakerConsecutiveVoiceSeconds = 0;
            }

            _lastSpeakerVoiceUpdateTime = now;
        }

        private void ProcessMicAudioChunking()
        {
            var shouldCreateChunk = false;
            var chunkType = AudioChunkingStrategy.VADBased;

            switch (_strategy)
            {
                case AudioChunkingStrategy.Fixed:
                    shouldCreateChunk = ShouldCreateFixedChunk(_micAudioBuffer, _targetChunkSizeSeconds);
                    chunkType = AudioChunkingStrategy.Fixed;
                    break;

                case AudioChunkingStrategy.VADBased:
                    shouldCreateChunk = ShouldCreateVADBasedChunk(_micConsecutiveVoiceSeconds, _micConsecutiveSilenceSeconds);
                    chunkType = AudioChunkingStrategy.VADBased;
                    break;

                case AudioChunkingStrategy.Adaptive:
                    shouldCreateChunk = ShouldCreateAdaptiveChunk(_micAudioBuffer, _micConsecutiveVoiceSeconds, _micConsecutiveSilenceSeconds);
                    chunkType = AudioChunkingStrategy.Adaptive;
                    break;

                case AudioChunkingStrategy.Hybrid:
                    shouldCreateChunk = ShouldCreateHybridChunk(_micAudioBuffer, _micConsecutiveVoiceSeconds, _micConsecutiveSilenceSeconds);
                    chunkType = AudioChunkingStrategy.Hybrid;
                    break;
            }

            if (shouldCreateChunk)
            {
                CreateMicAudioChunk(chunkType);
            }
        }

        private void ProcessSpeakerAudioChunking()
        {
            var shouldCreateChunk = false;
            var chunkType = AudioChunkingStrategy.Fixed;

            switch (_strategy)
            {
                case AudioChunkingStrategy.Fixed:
                    shouldCreateChunk = ShouldCreateFixedChunk(_speakerAudioBuffer, _targetChunkSizeSeconds);
                    chunkType = AudioChunkingStrategy.Fixed;
                    break;

                case AudioChunkingStrategy.VADBased:
                    shouldCreateChunk = ShouldCreateVADBasedChunk(_speakerConsecutiveVoiceSeconds, _speakerConsecutiveSilenceSeconds);
                    chunkType = AudioChunkingStrategy.VADBased;
                    break;

                case AudioChunkingStrategy.Adaptive:
                    shouldCreateChunk = ShouldCreateAdaptiveChunk(_speakerAudioBuffer, _speakerConsecutiveVoiceSeconds, _speakerConsecutiveSilenceSeconds);
                    chunkType = AudioChunkingStrategy.Adaptive;
                    break;

                case AudioChunkingStrategy.Hybrid:
                    shouldCreateChunk = ShouldCreateHybridChunk(_speakerAudioBuffer, _speakerConsecutiveVoiceSeconds, _speakerConsecutiveSilenceSeconds);
                    chunkType = AudioChunkingStrategy.Hybrid;
                    break;
            }

            if (shouldCreateChunk)
            {
                CreateSpeakerAudioChunk(chunkType);
            }
        }

        private bool ShouldCreateFixedChunk(List<byte> audioBuffer, float targetSeconds)
        {
            // Estimate buffer duration based on sample rate and format
            // Assuming 16-bit mono at 16kHz: 2 bytes per sample, 16000 samples per second
            var estimatedDurationSeconds = audioBuffer.Count / (2.0 * 16000);
            return estimatedDurationSeconds >= targetSeconds;
        }

        private bool ShouldCreateVADBasedChunk(int consecutiveVoiceSeconds, int consecutiveSilenceSeconds)
        {
            // If voice threshold is 0, create chunk immediately when voice is detected
            if (_vadVoiceThresholdSeconds == 0 && consecutiveVoiceSeconds > 0)
            {
                return true;
            }
            
            // Only create chunks when there's actual voice activity
            if (consecutiveVoiceSeconds >= _vadVoiceThresholdSeconds)
            {
                return true; // Voice detected, create chunk
            }
            
            // For continuous speech without gaps, create chunks more frequently
            // Create a new chunk every 0.2 seconds of continuous voice for faster response
            if (consecutiveVoiceSeconds >= 0.2)
            {
                return true;
            }
            
            // Only create silence-based chunks if we have some voice activity first
            if (consecutiveSilenceSeconds >= _vadSilenceThresholdSeconds && consecutiveVoiceSeconds > 0)
            {
                return true; // End of speech detected, create final chunk
            }
            
            return false;
        }

        private bool ShouldCreateAdaptiveChunk(List<byte> audioBuffer, int consecutiveVoiceSeconds, int consecutiveSilenceSeconds)
        {
            // Adaptive: adjust chunk size based on voice activity
            var baseDuration = _targetChunkSizeSeconds;
            var adjustedDuration = baseDuration;

            if (consecutiveVoiceSeconds > 0)
            {
                // If voice is active, extend the chunk slightly
                adjustedDuration = Math.Min(baseDuration + 2, _maxChunkSizeSeconds);
            }
            else if (consecutiveSilenceSeconds > _vadSilenceThresholdSeconds)
            {
                // If silence detected, create chunk earlier
                adjustedDuration = Math.Max(baseDuration - 1, _minChunkSizeSeconds);
            }

            return ShouldCreateFixedChunk(audioBuffer, adjustedDuration);
        }

        private bool ShouldCreateHybridChunk(List<byte> audioBuffer, int consecutiveVoiceSeconds, int consecutiveSilenceSeconds)
        {
            // Hybrid: combine fixed timing with VAD, but prioritize VAD
            var vadChunkReady = ShouldCreateVADBasedChunk(consecutiveVoiceSeconds, consecutiveSilenceSeconds);
            
            if (vadChunkReady)
            {
                return true; // VAD detected voice activity, create chunk immediately
            }
            
            // Only use fixed timing if no voice activity detected
            if (consecutiveVoiceSeconds == 0)
            {
                var fixedChunkReady = ShouldCreateFixedChunk(audioBuffer, _targetChunkSizeSeconds);
                return fixedChunkReady;
            }
            
            return false; // Wait for VAD to determine chunk boundaries
        }

        private void CreateMicAudioChunk(AudioChunkingStrategy chunkType)
        {
            if (_micAudioBuffer.Count == 0)
                return;

            var audioData = _micAudioBuffer.ToArray();
            var now = DateTime.UtcNow;
            
            // Check if this chunk actually has voice activity using VAD
            bool hasVoice = DetectVoiceActivity(_micAudioBuffer);
            
            var chunk = new AudioChunk(audioData, "Mic", false, chunkType)
            {
                StartTime = _micChunkStartTime,
                EndTime = now,
                HasVoiceActivity = hasVoice,
                AverageAmplitude = CalculateAverageAmplitude(audioData)
            };


            OnAudioChunkReady(chunk);

            // Clear buffer and reset chunk start time
            _micAudioBuffer.Clear();
            _micChunkStartTime = now; // Start timing for next chunk
            
            // Don't reset voice state counters here - let them continue tracking
            // Only reset when we actually detect silence
        }

        private void CreateSpeakerAudioChunk(AudioChunkingStrategy chunkType)
        {
            if (_speakerAudioBuffer.Count == 0)
                return;

            var audioData = _speakerAudioBuffer.ToArray();
            var now = DateTime.UtcNow;
            
            // Check if this chunk actually has voice activity using VAD
            bool hasVoice = DetectVoiceActivity(_speakerAudioBuffer);
            
            var chunk = new AudioChunk(audioData, "Speaker", true, chunkType)
            {
                StartTime = _speakerChunkStartTime,
                EndTime = now,
                HasVoiceActivity = hasVoice,
                AverageAmplitude = CalculateAverageAmplitude(audioData)
            };

            // Fire event immediately for faster processing
            OnAudioChunkReady(chunk);

            // Clear buffer and reset chunk start time immediately
            _speakerAudioBuffer.Clear();
            _speakerChunkStartTime = now; // Start timing for next chunk
            
            // Don't reset voice state counters here - let them continue tracking
            // Only reset when we actually detect silence
            
            // Log chunk creation for debugging (minimal)
        }

        private double CalculateAverageAmplitude(byte[] audioData)
        {
            if (audioData == null || audioData.Length == 0)
                return 0.0;

            // Convert 16-bit PCM to amplitude values
            var sum = 0.0;
            for (int i = 0; i < audioData.Length; i += 2)
            {
                if (i + 1 < audioData.Length)
                {
                    var sample = BitConverter.ToInt16(audioData, i);
                    sum += Math.Abs(sample);
                }
            }

            return sum / (audioData.Length / 2);
        }

        /// <summary>
        /// Detect voice activity in audio buffer using advanced multi-layer VAD
        /// </summary>
        private bool DetectVoiceActivity(List<byte> audioBuffer)
        {
            if (audioBuffer == null || audioBuffer.Count == 0)
                return false;

            // Convert to byte array for analysis
            var audioData = audioBuffer.ToArray();
            
            // Use our advanced VAD system from AudioRecorderVisualizer
            bool hasVoice = CheckAudioBufferForVoiceAdvanced(audioData);
            
            // Debug logging for VAD detection (minimal)
            
            return hasVoice;
        }
        
        /// <summary>
        /// Advanced VAD implementation using multi-layer filtering
        /// </summary>
        private bool CheckAudioBufferForVoiceAdvanced(byte[] audioData)
        {
            try
            {
                if (audioData == null || audioData.Length == 0)
                    return false;

                // Special case: Very short audio buffers (like "Hi") - be more lenient
                if (audioData.Length < 8000) // Less than 0.25 seconds at 16kHz
                {
                    double rms = CalculateRMS(audioData);
                    double normalizedAmplitude = rms / 32768.0;
                    
                    // For short utterances, just check amplitude and basic spectrum
                    if (normalizedAmplitude > 0.0002) // Very low threshold for short words
                    {
                        bool hasBasicSpectrum = CheckBasicSpeechSpectrum(audioData);
                        return hasBasicSpectrum;
                    }
                    return false;
                }

                // 1. AMPLITUDE THRESHOLD (lower to detect speech more easily)
                double amplitudeThreshold = 0.0005; // Very low threshold for quiet speech in noisy environments
                double fullRms = CalculateRMS(audioData);
                double fullNormalizedAmplitude = fullRms / 32768.0;
                
                // If amplitude is too low, definitely no voice
                if (fullNormalizedAmplitude < amplitudeThreshold)
                {
                    return false;
                }
                
                // 2. TEMPORAL VARIANCE CHECK (speech varies, constant noise doesn't)
                bool hasTemporalVariance = CheckTemporalVariance(audioData);
                if (!hasTemporalVariance)
                {
                    return false;
                }
                
                // 3. SPECTRAL ANALYSIS (human speech has specific frequency characteristics)
                bool hasSpeechSpectrum = CheckSpeechSpectrum(audioData);
                if (!hasSpeechSpectrum)
                {
                    return false;
                }
                
                // 4. SILENCE RATIO CHECK (speech has natural pauses, constant noise doesn't)
                bool hasNaturalSilence = CheckNaturalSilenceRatio(audioData);
                if (!hasNaturalSilence)
                {
                    return false;
                }
                
                return true;
            }
            catch (Exception ex)
            {
                if (Globals.EnableVerboseLogging)
                {
                    LoggingService.Info($"[VAD AUDIO CHUNKING] Error in VAD analysis: {ex.Message}");
                }
                return false;
            }
        }
        
        /// <summary>
        /// Calculate RMS for audio data
        /// </summary>
        private double CalculateRMS(byte[] audioData)
        {
            try
            {
                int sampleCount = audioData.Length / 2;
                short[] samples = new short[sampleCount];
                
                for (int i = 0; i < sampleCount; i++)
                {
                    samples[i] = BitConverter.ToInt16(audioData, i * 2);
                }
                
                double sumSquares = 0;
                for (int i = 0; i < samples.Length; i++)
                {
                    sumSquares += samples[i] * samples[i];
                }
                
                return Math.Sqrt(sumSquares / samples.Length);
            }
            catch
            {
                return 0.0;
            }
        }
        
        /// <summary>
        /// Check if audio has temporal variance (speech varies, constant noise doesn't)
        /// </summary>
        private bool CheckTemporalVariance(byte[] audioData)
        {
            try
            {
                if (audioData.Length < 1024) // Need enough samples for variance analysis
                    return true; // Default to true for very short audio
                
                // Convert to samples
                int sampleCount = audioData.Length / 2;
                short[] samples = new short[sampleCount];
                
                for (int i = 0; i < sampleCount; i++)
                {
                    samples[i] = BitConverter.ToInt16(audioData, i * 2);
                }
                
                // Calculate variance across time windows
                int windowSize = Math.Min(512, sampleCount / 4); // 32ms windows at 16kHz
                int windowCount = sampleCount / windowSize;
                
                if (windowCount < 2) return true; // Not enough windows
                
                double[] windowEnergies = new double[windowCount];
                
                for (int w = 0; w < windowCount; w++)
                {
                    int startIdx = w * windowSize;
                    int endIdx = Math.Min(startIdx + windowSize, sampleCount);
                    
                    double sumSquares = 0;
                    for (int i = startIdx; i < endIdx; i++)
                    {
                        sumSquares += samples[i] * samples[i];
                    }
                    windowEnergies[w] = sumSquares / (endIdx - startIdx);
                }
                
                // Calculate coefficient of variation (CV = std/mean)
                double meanEnergy = windowEnergies.Average();
                if (meanEnergy < 100) return false; // Too quiet overall
                
                double variance = windowEnergies.Select(e => Math.Pow(e - meanEnergy, 2)).Average();
                double stdDev = Math.Sqrt(variance);
                double coefficientOfVariation = stdDev / meanEnergy;
                
                // For short utterances like "Hi", be more permissive
                // Speech typically has CV > 0.05, constant noise has CV < 0.02
                bool hasTemporalVariance = coefficientOfVariation > 0.05;
                
                // Debug logging for temporal variance
                LoggingService.Info($"[VAD AUDIO CHUNKING] Temporal variance: CV={coefficientOfVariation:F3}, Threshold: >0.05, Result: {hasTemporalVariance}");
                
                return hasTemporalVariance;
            }
            catch
            {
                return true; // Default to true on error
            }
        }
        
        /// <summary>
        /// Check if audio has speech-like spectral characteristics
        /// </summary>
        private bool CheckSpeechSpectrum(byte[] audioData)
        {
            try
            {
                if (audioData.Length < 2048) // Need enough samples for spectral analysis
                    return true; // Default to true for very short audio
                
                // Convert to samples
                int sampleCount = audioData.Length / 2;
                short[] samples = new short[sampleCount];
                
                for (int i = 0; i < sampleCount; i++)
                {
                    samples[i] = BitConverter.ToInt16(audioData, i * 2);
                }
                
                // Calculate zero-crossing rate (indicates frequency content)
                int zeroCrossings = 0;
                for (int i = 1; i < samples.Length; i++)
                {
                    if ((samples[i] >= 0 && samples[i - 1] < 0) || 
                        (samples[i] < 0 && samples[i - 1] >= 0))
                    {
                        zeroCrossings++;
                    }
                }
                
                double zeroCrossingRate = (double)zeroCrossings / samples.Length;
                
                // For short utterances like "Hi", be more permissive
                // Human speech typically has ZCR between 0.03 and 0.5
                // Constant noise (like fan) typically has very low ZCR
                bool hasSpeechSpectrum = zeroCrossingRate > 0.03 && zeroCrossingRate < 0.5;
                
                // Debug logging for spectral analysis
                LoggingService.Info($"[VAD AUDIO CHUNKING] Zero-crossing rate: {zeroCrossingRate:F3}, Threshold: 0.03-0.5, Result: {hasSpeechSpectrum}");
                
                return hasSpeechSpectrum;
            }
            catch
            {
                return true; // Default to true on error
            }
        }
        
        /// <summary>
        /// Basic speech spectrum check for very short audio buffers
        /// </summary>
        private bool CheckBasicSpeechSpectrum(byte[] audioData)
        {
            try
            {
                if (audioData.Length < 512) // Too short for meaningful analysis
                    return true; // Default to true for very short audio
                
                // Convert to samples
                int sampleCount = audioData.Length / 2;
                short[] samples = new short[sampleCount];
                
                for (int i = 0; i < sampleCount; i++)
                {
                    samples[i] = BitConverter.ToInt16(audioData, i * 2);
                }
                
                // Simple zero-crossing rate check
                int zeroCrossings = 0;
                for (int i = 1; i < samples.Length; i++)
                {
                    if ((samples[i] >= 0 && samples[i - 1] < 0) || 
                        (samples[i] < 0 && samples[i - 1] >= 0))
                    {
                        zeroCrossings++;
                    }
                }
                
                double zeroCrossingRate = (double)zeroCrossings / samples.Length;
                
                // Very permissive for short utterances
                return zeroCrossingRate > 0.01 && zeroCrossingRate < 0.6;
            }
            catch
            {
                return true; // Default to true on error
            }
        }
        
        /// <summary>
        /// Check if audio has natural silence patterns (speech has pauses, constant noise doesn't)
        /// </summary>
        private bool CheckNaturalSilenceRatio(byte[] audioData)
        {
            try
            {
                if (audioData.Length < 1024) // Need enough samples for silence analysis
                    return true; // Default to true for very short audio
                
                // Convert to samples
                int sampleCount = audioData.Length / 2;
                short[] samples = new short[sampleCount];
                
                for (int i = 0; i < sampleCount; i++)
                {
                    samples[i] = BitConverter.ToInt16(audioData, i * 2);
                }
                
                // Count samples below silence threshold
                int silenceThreshold = 1000; // Adjustable silence threshold
                int silentSamples = 0;
                
                for (int i = 0; i < samples.Length; i++)
                {
                    if (Math.Abs(samples[i]) < silenceThreshold)
                    {
                        silentSamples++;
                    }
                }
                
                double silenceRatio = (double)silentSamples / samples.Length;
                
                // For short utterances like "Hi", be more permissive
                // Human speech can have 5-98% silence (including very short words and noisy environments)
                // Constant noise typically has very low silence ratio (< 3%)
                bool hasNaturalSilence = silenceRatio > 0.03 && silenceRatio < 0.98;
                
                // Debug logging for silence ratio
                LoggingService.Info($"[VAD AUDIO CHUNKING] Silence ratio: {silenceRatio:F3} ({silentSamples}/{sampleCount} samples), Threshold: 0.03-0.98, Result: {hasNaturalSilence}");
                
                return hasNaturalSilence;
            }
            catch
            {
                return true; // Default to true on error
            }
        }

        /// <summary>
        /// Force creation of chunks from remaining audio data
        /// </summary>
        public void FlushRemainingAudio()
        {
            lock (_micLock)
            {
                if (_micAudioBuffer.Count > 0)
                {
                    CreateMicAudioChunk(_strategy);
                }
            }

            lock (_speakerLock)
            {
                if (_speakerAudioBuffer.Count > 0)
                {
                    CreateSpeakerAudioChunk(_strategy);
                }
            }
        }

        /// <summary>
        /// Clear all audio buffers without sending them
        /// </summary>
        public void Clear()
        {
            lock (_micLock)
            {
                int micCleared = _micAudioBuffer.Count;
                _micAudioBuffer.Clear();
                LoggingService.Info($"[AudioChunking] 🗑️ Cleared {micCleared} bytes from mic buffer");
            }

            lock (_speakerLock)
            {
                int speakerCleared = _speakerAudioBuffer.Count;
                _speakerAudioBuffer.Clear();
                LoggingService.Info($"[AudioChunking] 🗑️ Cleared {speakerCleared} bytes from speaker buffer");
            }
        }

        protected virtual void OnAudioChunkReady(AudioChunk chunk)
        {
            AudioChunkReady?.Invoke(this, chunk);
        }

        protected virtual void OnChunkingProgress(string message)
        {
            ChunkingProgress?.Invoke(this, message);
        }
    }
}
