using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using NAudio.Wave;
using DataRippleAIDesktop.Models;
using DataRippleAICode.Models;
using DataRippleAIDesktop.HelperControls.AudioSpectrumAnalyzer;
using DataRippleAIDesktop.Services;
using NAudio.CoreAudioApi;
using System.IO;
using System.Reflection;
using DataRippleAIDesktop.Utilities;
using System.Diagnostics;
using DataRippleAIDesktop.Views;



namespace DataRippleAIDesktop.RecorderVisualizer
{
    /// <summary>
    /// Analyzer class for handling microphone input, loopback, and audio spectrum analysis.
    /// Provides functionalities to start/stop recording, process separate customer/agent audio streams, and visualize spectrum data.
    /// 
    /// WAVEFORM DISPLAY: Modified to show ALL audio without VAD filtering
    /// - Removed VAD dependency for waveform display
    /// - Removed amplitude thresholds (MIN_WAVEFORM_AMPLITUDE)
    /// - Waveforms now display raw audio signal regardless of voice activity
    /// - This ensures users can see all audio levels, not just voice-detected audio
    /// </summary>
    internal class AudioRecorderVisualizer
    {
        private WasapiLoopbackCapture _LoopbackCapture { get; set; } = null;
        private WaveInEvent _MicCapture { get; set; } = null;

        // private WaveFileWriter _writer { get; set; }=null;  // For testing

        private WaveFormat _CommonFormat { get; set; } = null;
        private ConcurrentQueue<DataBuffer> _MicQueue { get; set; } = null;
        private ConcurrentQueue<DataBuffer> _LoopBackFloatQueue { get; set; } = null;
        private Thread _AudioProcessingThread { get; set; } = null;
        private bool _IsRecording { get; set; } = false;
        
        // WAV Recording fields
        private WaveFileWriter _micWavWriter = null;
        private WaveFileWriter _speakerWavWriter = null;
        private string _recordingDirectory = "Recordings";
        private bool _isWavRecording = false;
        
        // Device selection fields
        private int _selectedMicDeviceIndex = 0;
        private string _selectedMicDeviceName = "";
        private int _selectedSpeakerDeviceIndex = 0;
        private string _selectedSpeakerDeviceName = "";
        private bool _devModeSystemAudioOnly = false;  // DevMode: System audio only (no mic)
        
        public AudioSpectrumVisualizer SpectrumVisualizer { get; set; } = null;
        public AudioSpectrumVisualizer MicSpectrumVisualizer { get; set; } = null;
        public AudioSpectrumVisualizer SpeakerSpectrumVisualizer { get; set; } = null;
        private List<byte> _Spectrumdata { get; set; } = null;   //spectrum data buffer        
        private List<byte> _MicSpectrumdata { get; set; } = null;   //microphone spectrum data buffer
        private List<byte> _SpeakerSpectrumdata { get; set; } = null;   //speaker spectrum data buffer
        private double _PeakAmplitudeSeen { get; set; } = 0;
        private double _MicPeakAmplitudeSeen { get; set; } = 0;
        private double _SpeakerPeakAmplitudeSeen { get; set; } = 0;
        public int NumberOfLines { get; set; } = 50;

        // VAD (Voice Activity Detection) related fields - Optimized for minimal delays
        private double _vadThreshold = 0.010; // Lowered threshold for voice detection (1.0%) - more sensitive to normal speech
        private double _vadSilenceThreshold = 0.002; // Lowered threshold for silence detection (0.2%) - more sensitive to quiet speech for faster response
        private int _vadMinVoiceDuration = 1; // Minimum consecutive voice frames to confirm speech - reduced for faster response
        private int _vadMinSilenceDuration = 1; // Minimum consecutive silence frames to confirm silence - reduced for faster response
        private bool _enableVerboseVadLogging = false; // Control verbose VAD debug logging
        private const int VAD_FRAME_SIZE = 160; // 10ms at 16kHz
        private  int VAD_HISTORY_SIZE = 2; // Reduced to 2 seconds for faster speaker response
        private ConcurrentQueue<bool> _micVadHistory = new ConcurrentQueue<bool>();
        private ConcurrentQueue<bool> _speakerVadHistory = new ConcurrentQueue<bool>();
        private bool _lastMicVadState = false;
        private bool _lastSpeakerVadState = false;
        private bool _enableVadFiltering = true; // Enable/disable VAD-based audio filtering
        
        // Enhanced VAD state tracking
        private int _micConsecutiveVoiceFrames = 0;
        private int _micConsecutiveSilenceFrames = 0;
        private int _speakerConsecutiveVoiceFrames = 0;
        private int _speakerConsecutiveSilenceFrames = 0;
        
        // VAD history management
        private int _micSilenceCounter = 0;
        private int _speakerSilenceCounter = 0;
        private const int VAD_HISTORY_RESET_THRESHOLD = 4; // Reset history after 4 consecutive silence frames - reduced for faster response
        
        // Speaker audio buffer management for delay reduction
        private DateTime _lastSpeakerBufferCleanup = DateTime.Now;
        private const int SPEAKER_BUFFER_CLEANUP_INTERVAL_SECONDS = 1; // Clean up speaker buffers every 1 second for faster processing
        
        // Manual silence signaling disabled to prevent text loss
        // private DateTime _lastSpeakerVoiceTime = DateTime.Now;
        // private const int SPEAKER_SILENCE_TIMEOUT_MS = 2000; // Send silence after 2 seconds of no voice
        
        // OPTIMIZATION: Disable old 10-second timer system completely
        private bool _disableLegacyTimerSystem = true; // Set to true to disable old accumulation system
        
        // Audio amplitude history for analysis - using ConcurrentQueue for thread safety
        private ConcurrentQueue<double> _micAmplitudeHistory = new ConcurrentQueue<double>();
        private ConcurrentQueue<double> _speakerAmplitudeHistory = new ConcurrentQueue<double>();
        private double _lastMicAmplitude = 0.0;
        private double _lastSpeakerAmplitude = 0.0;
        
        // VAD-filtered audio data for waveform visualization - using ConcurrentQueue for thread safety
        private ConcurrentQueue<byte[]> _micVadFilteredAudio = new ConcurrentQueue<byte[]>();
        private ConcurrentQueue<byte[]> _speakerVadFilteredAudio = new ConcurrentQueue<byte[]>();
        private ConcurrentQueue<bool> _micVadDecisions = new ConcurrentQueue<bool>();
        private ConcurrentQueue<bool> _speakerVadDecisions = new ConcurrentQueue<bool>();
        private const int VAD_WAVEFORM_HISTORY_SIZE = 10; // Keep last 50 VAD decisions for visualization
        
        // Look-back buffers to capture audio before VAD detection - using ConcurrentQueue for thread safety
        private ConcurrentQueue<byte[]> _micLookbackBuffer = new ConcurrentQueue<byte[]>();
        private ConcurrentQueue<byte[]> _speakerLookbackBuffer = new ConcurrentQueue<byte[]>();
        private const int LOOKBACK_BUFFER_SIZE = 2; // Keep last 2 audio chunks (about 20-30ms) for smooth start - reduced for faster response
        
        // Waveform display thresholds
        private const double MIN_WAVEFORM_AMPLITUDE = 0.02; // Minimum amplitude to show on waveform (2%)
        private const double MAX_WAVEFORM_AMPLITUDE = 0.90; // Maximum amplitude cap to prevent clipping (90%)
        private const double PEAK_DECAY_RATE = 0.95; // Peak decay rate for better responsiveness (5% decay per frame)
        
        // Waveform auto-reset for silence detection
        private DateTime _lastMicAudioTime = DateTime.Now;
        private DateTime _lastSpeakerAudioTime = DateTime.Now;
        private DateTime _lastSpeakerWaveformUpdate = DateTime.Now; // Track last waveform update for speaker
        private int _waveformResetDelayMs = 2000; // Reset waveform - will be loaded from max_turn_silence config
        
        // Fan/AC noise detection and filtering
        private const int FAN_NOISE_DETECTION_WINDOW = 3; // Frames to analyze for fan noise
        private Queue<double> _micAmplitudeVariation = new Queue<double>();
        private Queue<double> _speakerAmplitudeVariation = new Queue<double>();
        private double _micBaselineAmplitude = 0.0;
        private double _speakerBaselineAmplitude = 0.0;
        private bool _enableFanNoiseFiltering = true;
        private const double FAN_NOISE_VARIATION_THRESHOLD = 0.1; // Low variation indicates fan noise
        private const double FAN_NOISE_AMPLITUDE_THRESHOLD = 0.02; // Fan noise typically has low amplitude
        
        // Pitch analysis for VAD
        private bool _enablePitchAnalysis = true;
        private const double MIN_HUMAN_PITCH = 80.0; // Hz - minimum human speech pitch
        private const double MAX_HUMAN_PITCH = 800.0; // Hz - maximum human speech pitch
        private const double FAN_NOISE_PITCH_THRESHOLD = 60.0; // Hz - typical fan noise pitch
        private Queue<double> _micPitchHistory = new Queue<double>();
        private Queue<double> _speakerPitchHistory = new Queue<double>();
        private const int PITCH_HISTORY_SIZE = 5; // Keep last 5 pitch values

        // New configurable audio chunking service
        private AudioChunkingService _audioChunkingService;
        
        // Audio accumulation buffers used by legacy timer system (managed by DisableLegacyTimerSystem)
        private System.Timers.Timer _voiceDetectionTimer;
        private List<byte> _accumulatedMicAudio = new List<byte>();
        private List<byte> _accumulatedSpeakerAudio = new List<byte>();
        private DateTime _lastVoiceDetectionCheck = DateTime.Now;
        
        // Look-back buffers for previous audio chunks (to capture speech onset)
        private ConcurrentQueue<byte[]> _micLookbackChunks = new ConcurrentQueue<byte[]>();
        private ConcurrentQueue<byte[]> _speakerLookbackChunks = new ConcurrentQueue<byte[]>();

        /// <summary>
        /// Set the BackendAudioStreamingService for routing audio to the backend.
        /// Replaces the legacy STT handler methods.
        /// </summary>
        public void SetBackendAudioStreamingService(BackendAudioStreamingService service)
        {
            _backendAudioStreamingService = service;
            _chunkDebugCount = 0; // Reset debug counter for new call
            LoggingService.Info($"[AudioRecorder] BackendAudioStreamingService set: {(service != null ? "OK" : "null")}");
        }

        public void SetSpeechToTextProvider(string provider)
        {
            _currentSpeechToTextProvider = provider ?? "Backend";
            LoggingService.Info($"[AudioRecorder] Speech-to-text provider set to: {_currentSpeechToTextProvider}");
        }

        public void SetMicSpectrumVisualizer(AudioSpectrumVisualizer micVisualizer)
        {
            MicSpectrumVisualizer = micVisualizer;
            LoggingService.Info($"[AudioRecorder] Microphone spectrum visualizer set: {(MicSpectrumVisualizer != null ? "SUCCESS" : "FAILED")}");
        }

        public void SetSpeakerSpectrumVisualizer(AudioSpectrumVisualizer speakerVisualizer)
        {
            SpeakerSpectrumVisualizer = speakerVisualizer;
            LoggingService.Info($"[AudioRecorder] Speaker spectrum visualizer set: {(SpeakerSpectrumVisualizer != null ? "SUCCESS" : "FAILED")}");
        }
        
        // Additional visualizers for avatar display
        private AudioSpectrumVisualizer _agentAvatarVisualizer = null;
        private AudioSpectrumVisualizer _customerAvatarVisualizer = null;
        
        public void SetAgentAvatarVisualizer(AudioSpectrumVisualizer agentAvatarVisualizer)
        {
            _agentAvatarVisualizer = agentAvatarVisualizer;
        }
        
        public void SetCustomerAvatarVisualizer(AudioSpectrumVisualizer customerAvatarVisualizer)
        {
            _customerAvatarVisualizer = customerAvatarVisualizer;
        }
        
        // WAV Recording Methods
        /// <summary>
        /// Starts recording both microphone and speaker audio to separate WAV files
        /// Files are saved in the Recordings directory with timestamps:
        /// - Mic_YYYYMMDD_HHMMSS.wav (microphone audio)
        /// - Speaker_YYYYMMDD_HHMMSS.wav (speaker/loopback audio)
        /// 
        /// Usage:
        /// 1. Call StartWavRecording() to begin recording
        /// 2. Audio will be automatically saved as WAV PCM files
        /// 3. Call StopWavRecording() to stop and save files
        /// 4. Files are saved in 10-second chunks during audio processing
        /// </summary>
        public void StartWavRecording()
        {
            try
            {
                // Safety check: ensure _CommonFormat is initialized
                if (_CommonFormat == null)
                {
                    LoggingService.Info("[AudioRecorder] Warning: _CommonFormat is null, initializing audio devices...");
                    InitializeAudioCaptureDevices();
                    
                    // If still null after initialization, use default values
                    if (_CommonFormat == null)
                    {
                        LoggingService.Info("[AudioRecorder] Error: Failed to initialize _CommonFormat, using default values");
                        _CommonFormat = new WaveFormat(16000, 16, 1);
                    }
                }

                // Create recordings directory if it doesn't exist
                if (!Directory.Exists(_recordingDirectory))
                {
                    Directory.CreateDirectory(_recordingDirectory);
                }
                
                // Generate timestamp for file names
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                
                // Create WAV writers for both microphone and speaker
                string micFilePath = Path.Combine(_recordingDirectory, $"Mic_{timestamp}.wav");
                string speakerFilePath = Path.Combine(_recordingDirectory, $"Speaker_{timestamp}.wav");
                
                _micWavWriter = new WaveFileWriter(micFilePath, _CommonFormat);
                _speakerWavWriter = new WaveFileWriter(speakerFilePath, _CommonFormat);
                
                _isWavRecording = true;
                LoggingService.Info($"[AudioRecorder] WAV recording started - Mic: {micFilePath}, Speaker: {speakerFilePath}");
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[AudioRecorder] Error starting WAV recording: {ex.Message}");
            }
        }
        
        public void StopWavRecording()
        {
            try
            {
                _isWavRecording = false;
                
                // Dispose and close WAV writers
                _micWavWriter?.Dispose();
                _speakerWavWriter?.Dispose();
                
                _micWavWriter = null;
                _speakerWavWriter = null;
                
                LoggingService.Info("[AudioRecorder] WAV recording stopped and files saved");
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[AudioRecorder] Error stopping WAV recording: {ex.Message}");
            }
        }
        
        public bool IsWavRecording => _isWavRecording;
        
        public string GetRecordingDirectory => _recordingDirectory;
        
        public void SetRecordingDirectory(string directory)
        {
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
            {
                _recordingDirectory = directory;
                LoggingService.Info($"[AudioRecorder] Recording directory changed to: {_recordingDirectory}");
            }
        }

        private SynchronizationContext _syncContext;

        private bool _IsDefault24BitSelected = false;
        private BackendAudioStreamingService _backendAudioStreamingService;
        private string _currentSpeechToTextProvider = "Backend"; // Backend audio streaming provider
        private int _chunkDebugCount = 0;
        
        /// <summary>
        /// Routes audio data to the BackendAudioStreamingService.
        /// Mic audio is sent as microphone source, speaker audio as speaker source.
        /// </summary>
        private void RouteAudioToProvider(byte[] audioData, bool isMic)
        {
            if (audioData == null || audioData.Length == 0)
            {
                return;
            }

            if (_backendAudioStreamingService == null)
            {
                // Only log once to avoid spam
                if (_chunkDebugCount == 1)
                {
                    LoggingService.Warn("[AudioRecorder] BackendAudioStreamingService is null - cannot route audio");
                }
                return;
            }

            if (!_backendAudioStreamingService.IsConnected)
            {
                // Skip sending when not connected - will be picked up after reconnection
                return;
            }

            // Fire-and-forget async send. The service handles thread safety internally.
            if (isMic)
            {
                _ = _backendAudioStreamingService.SendMicrophoneAudioAsync(audioData);
            }
            else
            {
                _ = _backendAudioStreamingService.SendSpeakerAudioAsync(audioData);
            }
        }

        /// <summary>
        /// Initializes the Analyzer object and prepares recording devices.
        /// Audio is routed to the backend via BackendAudioStreamingService.
        /// </summary>
        public AudioRecorderVisualizer(AudioSpectrumVisualizer audioSpectrumVisualizer)
        {
            _Spectrumdata = new List<byte>();
            _MicSpectrumdata = new List<byte>();
            _SpeakerSpectrumdata = new List<byte>();
            SpectrumVisualizer = audioSpectrumVisualizer;

            // Initialize synchronization context for UI updates
            _syncContext = SynchronizationContext.Current;
            if (_syncContext == null)
            {
                // Fallback to main thread synchronization context
                _syncContext = new SynchronizationContext();
            }

            // Initialize VAD history queues
            for (int i = 0; i < VAD_HISTORY_SIZE; i++)
            {
                _micVadHistory.Enqueue(false);
                _speakerVadHistory.Enqueue(false);
            }

            // Initialize the audio chunking service for backend streaming.
            // Backend streaming uses VADBased strategy with reasonable defaults.
            var strategy = AudioChunkingStrategy.VADBased;
            float targetSeconds = 0.5f; // 500ms chunks for responsive backend streaming
            float minSeconds = 0.05f;
            float maxSeconds = 5.0f; // Allow larger chunks since backend handles splitting

            _audioChunkingService = new AudioChunkingService(strategy, targetSeconds, minSeconds, maxSeconds);
            _audioChunkingService.AudioChunkReady += OnAudioChunkReady;

            LoggingService.Info($"[AudioRecorder] Audio chunking initialized: Strategy={strategy}, Target={targetSeconds}s, Min={minSeconds}s, Max={maxSeconds}s");

            // OPTIMIZATION: Disable legacy timer system immediately to prevent delays
            DisableLegacyTimerSystem();
        }

        /// <summary>
        /// STRICT Voice Activity Detection using ONLY pitch analysis
        /// Eliminates false positives from spectral analysis and other methods
        /// </summary>
        /// <param name="audioData">Raw audio data in bytes</param>
        /// <returns>True if voice activity is detected, false otherwise</returns>
        private bool DetectVoiceActivity(byte[] audioData)
        {
            if (audioData == null || audioData.Length == 0)
                return false;

            try
            {
                // Convert byte array to 16-bit samples
                int sampleCount = audioData.Length / 2;
                short[] samples = new short[sampleCount];
                
                for (int i = 0; i < sampleCount; i++)
                {
                    samples[i] = BitConverter.ToInt16(audioData, i * 2);
                }

                // Calculate RMS (Root Mean Square) energy
                double sumSquares = 0;
                for (int i = 0; i < samples.Length; i++)
                {
                    sumSquares += samples[i] * samples[i];
                }
                
                double rms = Math.Sqrt(sumSquares / samples.Length);
                double normalizedRms = rms / 32768.0;
                
                // STRICT VAD: Only use pitch analysis for voice detection
                bool hasVoice = false;
                
                // 1. Must have sufficient energy to even consider voice
                if (normalizedRms < _vadSilenceThreshold)
                {
                    // Too quiet - definitely silence
                    hasVoice = false;
                }
                else if (normalizedRms >= _vadSilenceThreshold)
                {
                    // 2. CRITICAL: Only use pitch analysis - no spectral fallbacks
                    hasVoice = AnalyzePitchForSpeech(audioData);
                    
                    // 3. Additional energy threshold check for pitch analysis
                    if (hasVoice && normalizedRms < _vadThreshold)
                    {
                        // Even if pitch suggests voice, require minimum energy
                        hasVoice = false;
                    }
                }
                
                // Debug VAD calculation (only log occasionally to avoid spam)
                // if (audioData.Length > 0 && (DateTime.Now.Ticks % 10000000 == 0)) // Log every ~1 second
                // {
                //     LoggingService.Info($"[VAD DEBUG] Audio length: {audioData.Length}, Sample count: {sampleCount}, RMS: {rms:F2}, Normalized: {normalizedRms:F6}, Threshold: {_vadThreshold:F6}, Silence: {_vadSilenceThreshold:F6}, HasVoice: {hasVoice}, PitchAnalysis: {_enablePitchAnalysis}");
                // }
                
                // Additional debug logging for troubleshooting
                // if (audioData.Length > 0 && (DateTime.Now.Ticks % 5000000 == 0)) // Log every ~0.5 seconds
                // {
                //     if (normalizedRms >= _vadSilenceThreshold)
                //     {
                //         LoggingService.Info($"[VAD TROUBLESHOOT] RMS: {normalizedRms:F6} >= SilenceThreshold: {_vadSilenceThreshold:F6} - Checking pitch...");
                //         
                //         // Additional pitch analysis debug logging
                //         if (_enablePitchAnalysis)
                //         {
                //             bool pitchResult = AnalyzePitchForSpeech(audioData);
                //             LoggingService.Info($"[VAD PITCH DEBUG] Pitch analysis result: {pitchResult}");
                //         }
                //     }
                //     else
                //     {
                //         LoggingService.Info($"[VAD TROUBLESHOOT] RMS: {normalizedRms:F6} < SilenceThreshold: {_vadSilenceThreshold:F6} - Too quiet, silence confirmed");
                //     }
                // }
                
                return hasVoice;
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[VAD] Error in voice activity detection: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Calculate spectral centroid (center of mass of the spectrum)
        /// </summary>
        /// <param name="samples">Audio samples</param>
        /// <returns>Spectral centroid value (0-1)</returns>
        private double CalculateSpectralCentroid(short[] samples)
        {
            try
            {
                // Simple approximation using FFT-like analysis
                // For real-time processing, we'll use a simplified approach
                
                // Calculate magnitude spectrum (simplified)
                double[] magnitudes = new double[samples.Length / 2];
                for (int i = 0; i < magnitudes.Length; i++)
                {
                    double sum = 0;
                    for (int j = 0; j < samples.Length; j++)
                    {
                        sum += samples[j] * Math.Cos(2 * Math.PI * i * j / samples.Length);
                    }
                    magnitudes[i] = Math.Abs(sum);
                }
                
                // Calculate centroid
                double weightedSum = 0;
                double totalMagnitude = 0;
                
                for (int i = 0; i < magnitudes.Length; i++)
                {
                    weightedSum += i * magnitudes[i];
                    totalMagnitude += magnitudes[i];
                }
                
                if (totalMagnitude > 0)
                {
                    return weightedSum / (totalMagnitude * magnitudes.Length);
                }
                
                return 0.5; // Default middle value
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[VAD] Error calculating spectral centroid: {ex.Message}");
                return 0.5; // Default middle value
            }
        }
        
        /// <summary>
        /// Calculate spectral rolloff (frequency below which 85% of energy is contained)
        /// </summary>
        /// <param name="samples">Audio samples</param>
        /// <returns>Spectral rolloff value (0-1)</returns>
        private double CalculateSpectralRolloff(short[] samples)
        {
            try
            {
                // Simplified spectral rolloff calculation
                // Calculate total energy
                double totalEnergy = 0;
                for (int i = 0; i < samples.Length; i++)
                {
                    totalEnergy += samples[i] * samples[i];
                }
                
                // Find frequency where 85% of energy is contained
                double targetEnergy = totalEnergy * 0.85;
                double cumulativeEnergy = 0;
                
                for (int i = 0; i < samples.Length / 2; i++)
                {
                    double sum = 0;
                    for (int j = 0; j < samples.Length; j++)
                    {
                        sum += samples[j] * Math.Cos(2 * Math.PI * i * j / samples.Length);
                    }
                    cumulativeEnergy += sum * sum;
                    
                    if (cumulativeEnergy >= targetEnergy)
                    {
                        return (double)i / (samples.Length / 2);
                    }
                }
                
                return 0.5; // Default middle value
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[VAD] Error calculating spectral rolloff: {ex.Message}");
                return 0.5; // Default middle value
            }
        }

        /// <summary>
        /// Calculate audio amplitude from byte array
        /// </summary>
        /// <param name="audioData">Raw audio data</param>
        /// <returns>Normalized amplitude value (0.0 to 1.0)</returns>
        private double CalculateAmplitude(byte[] audioData)
        {
            try
            {
                if (audioData == null || audioData.Length == 0)
                    return 0.0;

                // Convert bytes to 16-bit samples
                int sampleCount = audioData.Length / 2;
                short[] samples = new short[sampleCount];
                
                for (int i = 0; i < sampleCount; i++)
                {
                    samples[i] = BitConverter.ToInt16(audioData, i * 2);
                }
                
                // Calculate RMS (Root Mean Square) amplitude
                double sum = 0.0;
                for (int i = 0; i < samples.Length; i++)
                {
                    sum += samples[i] * samples[i];
                }
                
                double rms = Math.Sqrt(sum / samples.Length);
                
                // Normalize to 0.0-1.0 range (16-bit audio has max value of 32767)
                double normalizedAmplitude = rms / 32767.0;
                
                return Math.Min(normalizedAmplitude, 1.0);
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[VAD] Error calculating amplitude: {ex.Message}");
                return 0.0;
            }
        }

        /// <summary>
        /// Detect voice activity with hysteresis and cross-talk detection
        /// </summary>
        /// <param name="audioData">Audio data to analyze</param>
        /// <param name="isMic">True if microphone audio, false if speaker audio</param>
        /// <returns>True if voice activity is detected, false otherwise</returns>
        private bool DetectVoiceActivityWithHysteresis(byte[] audioData, bool isMic)
        {
            bool currentVadState = DetectVoiceActivity(audioData);
            ConcurrentQueue<bool> vadHistory = isMic ? _micVadHistory : _speakerVadHistory;
            bool lastVadState = isMic ? _lastMicVadState : _lastSpeakerVadState;
            
            // Calculate current amplitude for cross-talk detection
            double currentAmplitude = CalculateAmplitude(audioData);
            
            // Update amplitude history for analysis
            if (isMic)
            {
                _micAmplitudeHistory.Enqueue(currentAmplitude);
                while (_micAmplitudeHistory.Count > 10) // Keep last 10 values
                    _micAmplitudeHistory.TryDequeue(out _);
                _lastMicAmplitude = currentAmplitude;
                }
                else
                {
                _speakerAmplitudeHistory.Enqueue(currentAmplitude);
                while (_speakerAmplitudeHistory.Count > 10) // Keep last 10 values
                    _speakerAmplitudeHistory.TryDequeue(out _);
                _lastSpeakerAmplitude = currentAmplitude;
            }
            
            // Track consecutive frames for better noise filtering
            if (isMic)
            {
                if (currentVadState)
                {
                    _micConsecutiveVoiceFrames++;
                    _micConsecutiveSilenceFrames = 0;
                    _micSilenceCounter = 0; // Reset silence counter
                }
                else
                {
                    _micConsecutiveSilenceFrames++;
                    _micConsecutiveVoiceFrames = 0;
                    _micSilenceCounter++; // Increment silence counter
                }
            }
            else
            {
                if (currentVadState)
                {
                    _speakerConsecutiveVoiceFrames++;
                    _speakerConsecutiveSilenceFrames = 0;
                    _speakerSilenceCounter = 0; // Reset silence counter
                }
                else
                {
                    _speakerConsecutiveSilenceFrames++;
                    _speakerConsecutiveVoiceFrames = 0;
                    _speakerSilenceCounter++; // Increment silence counter
                }
            }
            
            // Add current state to history
            vadHistory.Enqueue(currentVadState);
            if (vadHistory.Count > VAD_HISTORY_SIZE)
                vadHistory.TryDequeue(out _);
            
            // Reset VAD history if there's sustained silence (prevents history contamination)
            if (isMic && _micSilenceCounter >= VAD_HISTORY_RESET_THRESHOLD)
            {
                ResetVadHistory(true); // Reset microphone history
                //LoggingService.Info("[VAD] Microphone VAD history reset due to sustained silence");
            }
            else if (!isMic && _speakerSilenceCounter >= VAD_HISTORY_RESET_THRESHOLD)
            {
                ResetVadHistory(false); // Reset speaker history
                //LoggingService.Info("[VAD] Speaker VAD history reset due to sustained silence");
            }
            
            // Enhanced decision logic with pitch-based bypass for consecutive frame requirements
            bool finalVadState;
            
            if (isMic)
            {
                // Microphone VAD - check if pitch analysis detected voice first
                if (currentVadState && _enablePitchAnalysis)
                {
                    // Pitch analysis confirmed voice - bypass consecutive frame requirements
                    finalVadState = true;
                    //LoggingService.Info($"[VAD] Microphone: Pitch analysis confirmed voice - bypassing consecutive frame check ({_micConsecutiveVoiceFrames}/{_vadMinVoiceDuration})");
                }
                else if (_micConsecutiveVoiceFrames >= _vadMinVoiceDuration)
                {
                    finalVadState = true; // Confirmed voice activity through consecutive frames
                }
                else if (_micConsecutiveSilenceFrames >= _vadMinSilenceDuration)
                {
                    finalVadState = false; // Confirmed silence
                }
                else
                {
                    // Keep previous state until we have enough consecutive frames
                    finalVadState = lastVadState;
                }
            }
            else
            {
                // Speaker VAD - check if pitch analysis detected voice first
                if (currentVadState && _enablePitchAnalysis)
                {
                    // Pitch analysis confirmed voice - bypass consecutive frame requirements
                    finalVadState = true;
                    // LoggingService.Info($"[VAD] Speaker: Pitch analysis confirmed voice - bypassing consecutive frame check ({_speakerConsecutiveVoiceFrames}/{_vadMinVoiceDuration})");
                }
                else if (_speakerConsecutiveVoiceFrames >= _vadMinVoiceDuration)
                {
                    finalVadState = true; // Confirmed voice activity through consecutive frames
                }
                else if (_speakerConsecutiveSilenceFrames >= _vadMinSilenceDuration)
                {
                    finalVadState = false; // Confirmed silence
                }
                else
                {
                    finalVadState = lastVadState;
                }
            }
            
            // Track VAD decisions for visualization
            if (isMic)
            {
                _micVadDecisions.Enqueue(finalVadState);
                while (_micVadDecisions.Count > VAD_WAVEFORM_HISTORY_SIZE)
                    _micVadDecisions.TryDequeue(out _);
                _lastMicVadState = finalVadState;
            }
            else
            {
                _speakerVadDecisions.Enqueue(finalVadState);
                while (_speakerVadDecisions.Count > VAD_WAVEFORM_HISTORY_SIZE)
                    _speakerVadDecisions.TryDequeue(out _);
                _lastSpeakerVadState = finalVadState;
            }
            
            // Debug VAD hysteresis (only when verbose logging is enabled)
            if (_enableVerboseVadLogging)
            {
                if (isMic)
                {
                    // VAD hysteresis debug for microphone
                }
                else
                {
                    // VAD hysteresis debug for speaker
                }
            }
            
            return finalVadState;
        }

        /// <summary>
        /// Clear speaker audio buffers to reduce delays
        /// </summary>
        public void ClearSpeakerAudioBuffers()
        {
            try
            {
                // ConcurrentQueues are thread-safe, no lock needed
                while (_speakerLookbackBuffer.TryDequeue(out _)) { }
                while (_speakerVadHistory.TryDequeue(out _)) { }
                
                // Clear accumulated speaker audio
                lock (_accumulatedSpeakerAudio)
                {
                    _accumulatedSpeakerAudio.Clear();
                }
            }
            catch (Exception ex)
            {
                if (Globals.EnableVerboseLogging)
                {
                    LoggingService.Info($"[VAD] Error clearing speaker buffers: {ex.Message}");
                }
            }
        }
        
        /// <summary>
        /// Send speaker audio to the backend via the audio chunking service.
        /// Audio is buffered and chunked before being routed to BackendAudioStreamingService.
        /// </summary>
        public void SendSpeakerAudioDirectly(byte[] audioData)
        {
            try
            {
                if (audioData == null || audioData.Length == 0)
                    return;

                // Always use chunking service for backend streaming - ensures proper chunk sizes
                _audioChunkingService?.AddSpeakerAudioData(audioData, true);
            }
            catch (Exception ex)
            {
                if (Globals.EnableVerboseLogging)
                {
                    LoggingService.Info($"[VAD DIRECT] Error sending speaker audio: {ex.Message}");
                }
            }
        }
        
        /// <summary>
        /// Completely disable the legacy 10-second timer system
        /// </summary>
        public void DisableLegacyTimerSystem()
        {
            try
            {
                _disableLegacyTimerSystem = true;
                
                // Clear all accumulated audio buffers
                lock (_accumulatedMicAudio)
                {
                    _accumulatedMicAudio.Clear();
                }
                
                lock (_accumulatedSpeakerAudio)
                {
                    _accumulatedSpeakerAudio.Clear();
                }
                
                // Clear lookback chunks
                while (_micLookbackChunks.TryDequeue(out _)) { }
                while (_speakerLookbackChunks.TryDequeue(out _)) { }
            }
            catch (Exception ex)
            {
                if (Globals.EnableVerboseLogging)
                {
                    LoggingService.Info($"[VAD] Error disabling legacy timer system: {ex.Message}");
                }
            }
        }
        
        /// <summary>
        /// Reset VAD state and history
        /// </summary>
        public void ResetVadState()
        {
            // Reset VAD history queues
            while (_micVadHistory.TryDequeue(out _)) { }
            while (_speakerVadHistory.TryDequeue(out _)) { }
            
            // Reset consecutive frame counters
            _micConsecutiveVoiceFrames = 0;
            _micConsecutiveSilenceFrames = 0;
            _speakerConsecutiveVoiceFrames = 0;
            _speakerConsecutiveSilenceFrames = 0;
            
            // Reset last VAD states
            _lastMicVadState = false;
            _lastSpeakerVadState = false;
            
            // Reset silence counters
            _micSilenceCounter = 0;
            _speakerSilenceCounter = 0;
            
            // Reset VAD waveform history
            ClearVadWaveformHistory();
            
            // Reinitialize VAD history queues
            for (int i = 0; i < VAD_HISTORY_SIZE; i++)
            {
                _micVadHistory.Enqueue(false);
                _speakerVadHistory.Enqueue(false);
            }
            
            LoggingService.Info("[VAD] VAD state reset");
        }
        
        /// <summary>
        /// Clear VAD waveform visualization history
        /// </summary>
        public void ClearVadWaveformHistory()
        {
            // ConcurrentQueues are thread-safe, no locks needed
            while (_micVadFilteredAudio.TryDequeue(out _)) { }
            while (_speakerVadFilteredAudio.TryDequeue(out _)) { }
            while (_micVadDecisions.TryDequeue(out _)) { }
            while (_speakerVadDecisions.TryDequeue(out _)) { }
            while (_micLookbackBuffer.TryDequeue(out _)) { }
            while (_speakerLookbackBuffer.TryDequeue(out _)) { }
            
            LoggingService.Info("[VAD] VAD waveform history and look-back buffers cleared");
        }

        /// <summary>
        /// Reset VAD history for specific audio source
        /// </summary>
        /// <param name="isMic">True for microphone, false for speaker</param>
        private void ResetVadHistory(bool isMic)
        {
            if (isMic)
            {
                while (_micVadHistory.TryDequeue(out _)) { }
                _micSilenceCounter = 0;
                _micConsecutiveVoiceFrames = 0;
                _micConsecutiveSilenceFrames = 0;
                
                // Reinitialize microphone VAD history
                for (int i = 0; i < VAD_HISTORY_SIZE; i++)
                {
                    _micVadHistory.Enqueue(false);
                }
            }
            else
            {
                while (_speakerVadHistory.TryDequeue(out _)) { }
                _speakerSilenceCounter = 0;
                _speakerConsecutiveVoiceFrames = 0;
                _speakerConsecutiveSilenceFrames = 0;
                
                // Reinitialize speaker VAD history
                for (int i = 0; i < VAD_HISTORY_SIZE; i++)
                {
                    _speakerVadHistory.Enqueue(false);
                }
            }
        }

        /// <summary>
        /// Get current VAD decision status for both mic and speaker
        /// </summary>
        /// <returns>Current VAD decisions</returns>
        public (bool micVadActive, bool speakerVadActive) GetCurrentVadStatus()
        {
            // ConcurrentQueues are thread-safe, no lock needed
            // Use TryPeek to get the last item (FIFO queue, so peek at oldest)
            // Actually we want the LAST item, so convert to array first
            bool micActive = _micVadDecisions.Count > 0 ? _micVadDecisions.ToArray().LastOrDefault() : false;
            bool speakerActive = _speakerVadDecisions.Count > 0 ? _speakerVadDecisions.ToArray().LastOrDefault() : false;
                
                return (micActive, speakerActive);
        }
        
       
        /// <summary>
        /// Calculate RMS for audio data with specified length
        /// </summary>
        /// <param name="audioData">Audio data</param>
        /// <param name="length">Length of audio data to process</param>
        /// <returns>RMS value</returns>
        private double CalculateRMS(byte[] audioData, int length)
        {
            try
            {
                int sampleCount = length / 2;
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
                return 0;
            }
        }
        
        /// <summary>
        /// Calculate RMS for audio data
        /// </summary>
        /// <param name="audioData">Audio data</param>
        /// <returns>RMS value</returns>
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
        /// Initialize audio capture devices
        /// </summary>
        private void InitializeAudioCaptureDevices()
        {
            try
            {
                LoggingService.Info("[AudioRecorder] === AUDIO DEVICE DIAGNOSTIC START ===");
                LoggingService.Info($"[AudioRecorder] OS Version: {Environment.OSVersion}");
                LoggingService.Info($"[AudioRecorder] Machine Name: {Environment.MachineName}");
                LoggingService.Info($"[AudioRecorder] User Name: {Environment.UserName}");
                // Initialize common wave format for audio processing
                if (_CommonFormat == null)
                {
                    _CommonFormat = new WaveFormat(16000, 16, 1); // Mono 16-bit at 16kHz
                    LoggingService.Info("[AudioRecorder] Common wave format initialized: 16kHz, 16-bit, Mono");
                }

                // Initialize microphone capture with error handling
                // Skip mic initialization if DevMode System Audio Only is enabled
                if (_MicCapture == null && !_devModeSystemAudioOnly)
                {
                    try
                    {
                        // Log available microphone devices for diagnostics
                        LoggingService.Info($"[AudioRecorder] Available microphone devices: {WaveInEvent.DeviceCount}");
                        for (int i = 0; i < WaveInEvent.DeviceCount; i++)
                        {
                            try
                            {
                                var deviceInfo = WaveInEvent.GetCapabilities(i);
                                LoggingService.Info($"[AudioRecorder] Mic Device {i}: {deviceInfo.ProductName} (Channels: {deviceInfo.Channels})");
                            }
                            catch (Exception ex)
                            {
                                LoggingService.Warn($"[AudioRecorder] Error getting mic device {i} info: {ex.Message}");
                            }
                        }
                        
                        if (WaveInEvent.DeviceCount == 0)
                        {
                            LoggingService.Warn("[AudioRecorder] No microphone devices found - microphone recording will be disabled");
                            LoggingService.Info("[AudioRecorder] This may be due to audio driver issues, permissions, or no audio devices connected");
                            LoggingService.Info("[AudioRecorder] The application will continue to work with speaker audio only");
                        }
                        else
                        {
                            // Validate selected device index and fallback to default if invalid
                            int deviceIndexToUse = _selectedMicDeviceIndex;
                            if (_selectedMicDeviceIndex >= WaveInEvent.DeviceCount || _selectedMicDeviceIndex < 0)
                            {
                                LoggingService.Warn($"[AudioRecorder] Selected microphone device index {_selectedMicDeviceIndex} is invalid (available devices: {WaveInEvent.DeviceCount}), falling back to default device (index 0)");
                                deviceIndexToUse = 0; // Use default device
                                _selectedMicDeviceIndex = 0; // Update stored index
                                _selectedMicDeviceName = WaveInEvent.GetCapabilities(0).ProductName; // Update stored name
                                
                                // Save the fallback device settings to appsettings.json
                                SaveDeviceSettingsToAppSettings();
                            }
                            
                            _MicCapture = new WaveInEvent();
                            _MicCapture.DeviceNumber = deviceIndexToUse;
                            _MicCapture.WaveFormat = new WaveFormat(16000, 16, 1); // Mono 16-bit at 16kHz
                            _MicCapture.DataAvailable += MicSourceDataAvailable;
                            _MicCapture.RecordingStopped += MicSourceRecordingStopped;
                            
                            // Get detailed device info to check for system audio capture devices
                            var micCaps = WaveInEvent.GetCapabilities(deviceIndexToUse);
                            var deviceName = micCaps.ProductName.ToLower();
                            LoggingService.Info($"[AudioRecorder] Microphone capture device initialized: {_selectedMicDeviceName} (Index: {deviceIndexToUse})");
                            LoggingService.Info($"[AudioRecorder] Device capabilities: Channels={micCaps.Channels}, ProductName='{micCaps.ProductName}'");
                            
                            // Warn if device might be a system audio loopback (Stereo Mix, What U Hear, etc.)
                            if (deviceName.Contains("stereo mix") || deviceName.Contains("wave out") || 
                                deviceName.Contains("what u hear") || deviceName.Contains("loopback") ||
                                deviceName.Contains("what you hear"))
                            {
                                LoggingService.Warn($"[AudioRecorder] ⚠️ WARNING: Selected microphone device '{_selectedMicDeviceName}' appears to be a SYSTEM AUDIO LOOPBACK device!");
                                LoggingService.Warn($"[AudioRecorder] This will capture ALL system audio (including speaker output) and send it to the MIC/AGENT side.");
                                LoggingService.Warn($"[AudioRecorder] SOLUTION: Select an actual physical microphone device instead.");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LoggingService.Error($"[AudioRecorder] Failed to initialize microphone capture: {ex.Message}");
                        LoggingService.Info("[AudioRecorder] Continuing without microphone - speaker recording will still work");
                        _MicCapture = null; // Ensure it's null so we know mic failed
                    }
                }
                else if (_devModeSystemAudioOnly)
                {
                    LoggingService.Info("[AudioRecorder] 🔧 DevMode System Audio Only enabled - Skipping microphone initialization");
                    LoggingService.Info("[AudioRecorder] Only system audio (speaker output) will be captured for testing purposes");
                    _MicCapture = null; // Ensure mic is null
                }

                // Initialize loopback capture (system audio) with error handling
                if (_LoopbackCapture == null)
                {
                    try
                    {
                        // Get selected playback device or default if none selected
                        MMDeviceEnumerator deviceEnumerator = new MMDeviceEnumerator();
                        MMDevice selectedDevice = null;
                        
                        // Log available speaker devices for diagnostics
                        var devices = deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
                        LoggingService.Info($"[AudioRecorder] Available speaker devices: {devices.Count}");
                        for (int i = 0; i < devices.Count; i++)
                        {
                            LoggingService.Info($"[AudioRecorder] Speaker Device {i}: {devices[i].FriendlyName}");
                        }
                        
                        
                        if (devices.Count == 0)
                        {
                            LoggingService.Warn("[AudioRecorder] No active speaker devices found - attempting fallback to default device");
                            LoggingService.Info("[AudioRecorder] This could be due to:");
                            LoggingService.Info("[AudioRecorder] 1. No audio devices connected");
                            LoggingService.Info("[AudioRecorder] 2. Audio devices are disabled in Windows");
                            LoggingService.Info("[AudioRecorder] 3. Audio drivers not installed or corrupted");
                            LoggingService.Info("[AudioRecorder] 4. Windows audio service not running");
                            LoggingService.Info("[AudioRecorder] 5. Application lacks audio device permissions");
                            
                            // Try to get the default device even if it's not in active state
                            try
                            {
                                LoggingService.Info("[AudioRecorder] Attempting to use default device as fallback...");
                                selectedDevice = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                                if (selectedDevice != null)
                                {
                                    LoggingService.Info($"[AudioRecorder] Fallback successful - using default device: {selectedDevice.FriendlyName} (State: {selectedDevice.State})");
                                }
                                else
                                {
                                    LoggingService.Error("[AudioRecorder] Fallback failed - no default device available");
                                }
                            }
                            catch (Exception fallbackEx)
                            {
                                LoggingService.Error($"[AudioRecorder] Fallback to default device failed: {fallbackEx.Message}");
                                selectedDevice = null;
                            }
                        }
                        else
                        {
                            if (!string.IsNullOrEmpty(_selectedSpeakerDeviceName) && _selectedSpeakerDeviceIndex >= 0)
                            {
                                // Use the same enumeration method as the UI to get devices by index
                                if (_selectedSpeakerDeviceIndex < devices.Count)
                                {
                                    selectedDevice = devices[_selectedSpeakerDeviceIndex];
                                    LoggingService.Info($"[AudioRecorder] Using speaker device by index: {selectedDevice.FriendlyName} (Index: {_selectedSpeakerDeviceIndex})");
                                }
                                else
                                {
                                    // Selected device index is invalid, try name-based selection first
                                    LoggingService.Warn($"[AudioRecorder] Selected speaker device index {_selectedSpeakerDeviceIndex} is invalid (available devices: {devices.Count}), trying name-based selection");
                                    selectedDevice = devices.FirstOrDefault(d => d.FriendlyName == _selectedSpeakerDeviceName);
                                    if (selectedDevice == null)
                                    {
                                        LoggingService.Warn($"[AudioRecorder] Selected speaker device '{_selectedSpeakerDeviceName}' not found, falling back to default device");
                                        selectedDevice = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                                        // Update stored values to reflect the fallback
                                        if (selectedDevice != null)
                                        {
                                            _selectedSpeakerDeviceIndex = devices.IndexOf(selectedDevice);
                                            _selectedSpeakerDeviceName = selectedDevice.FriendlyName;
                                            
                                            // Save the fallback device settings to appsettings.json
                                            SaveDeviceSettingsToAppSettings();
                                        }
                                    }
                                    else
                                    {
                                        // Update stored index to match the found device
                                        _selectedSpeakerDeviceIndex = devices.IndexOf(selectedDevice);
                                        LoggingService.Info($"[AudioRecorder] Found speaker device by name, updated index to: {_selectedSpeakerDeviceIndex}");
                                    }
                                }
                            }
                            else
                            {
                                LoggingService.Info("[AudioRecorder] No speaker device selected, using default device");
                                selectedDevice = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                                // Update stored values to reflect the default device
                                if (selectedDevice != null)
                                {
                                    _selectedSpeakerDeviceIndex = devices.IndexOf(selectedDevice);
                                    _selectedSpeakerDeviceName = selectedDevice.FriendlyName;
                                    
                                    // Save the default device settings to appsettings.json
                                    SaveDeviceSettingsToAppSettings();
                                }
                            }
                            
                            if (selectedDevice != null)
                            {
                                try
                                {
                                    LoggingService.Info($"[AudioRecorder] Attempting to initialize WasapiLoopbackCapture with device: {selectedDevice.FriendlyName}");
                                    LoggingService.Info($"[AudioRecorder] Device state: {selectedDevice.State}");
                                    LoggingService.Info($"[AudioRecorder] Device ID: {selectedDevice.ID}");
                                    
                                    // Test if device supports loopback capture
                                    LoggingService.Info("[AudioRecorder] Testing device for loopback capture compatibility...");
                                    bool isCompatible = TestDeviceLoopbackCapability(selectedDevice);
                                    
                                    if (!isCompatible)
                                    {
                                        LoggingService.Error("[AudioRecorder] ❌ Selected device does not support loopback capture");
                                        LoggingService.Info("[AudioRecorder] This device cannot be used for speaker audio capture");
                                        _LoopbackCapture = null;
                                        return; // Skip creating WasapiLoopbackCapture
                                    }
                                    else
                                    {
                                        LoggingService.Info("[AudioRecorder]Device is compatible with loopback capture");
                                    }
                                    
                                _LoopbackCapture = new WasapiLoopbackCapture(selectedDevice);
                                
                                // WasapiLoopbackCapture automatically uses the device's native format
                                // We can only read it, not set it (WaveFormat is read-only)
                                if (Globals.EnableAudioFormatDiagnostics)
                                {
                                    LoggingService.Info($"[AudioRecorder] Loopback capture initialized with device native format: {_LoopbackCapture.WaveFormat.SampleRate}Hz, {_LoopbackCapture.WaveFormat.BitsPerSample}-bit, {_LoopbackCapture.WaveFormat.Channels} channels");
                                }
                                
                                _LoopbackCapture.DataAvailable += LoopBackSourceDataAvailable;
                                _LoopbackCapture.RecordingStopped += LoopBackSourceRecordingStopped;
                                    LoggingService.Info($"[AudioRecorder] ✅ Loopback capture device initialized successfully: {selectedDevice.FriendlyName} (Using format: {_LoopbackCapture.WaveFormat.SampleRate}Hz, {_LoopbackCapture.WaveFormat.BitsPerSample}-bit, {_LoopbackCapture.WaveFormat.Channels} channels)");
                                }
                                catch (Exception loopbackEx)
                                {
                                    LoggingService.Error($"[AudioRecorder] ❌ Failed to create WasapiLoopbackCapture: {loopbackEx.Message}");
                                    LoggingService.Error($"[AudioRecorder] Exception type: {loopbackEx.GetType().Name}");
                                    LoggingService.Error($"[AudioRecorder] Stack trace: {loopbackEx.StackTrace}");
                                    LoggingService.Info("[AudioRecorder] This could be due to:");
                                    LoggingService.Info("[AudioRecorder] 1. Device is not currently playing audio");
                                    LoggingService.Info("[AudioRecorder] 2. Device is in exclusive mode");
                                    LoggingService.Info("[AudioRecorder] 3. Insufficient permissions for audio capture");
                                    LoggingService.Info("[AudioRecorder] 4. Audio driver issues");
                                    LoggingService.Info("[AudioRecorder] 5. Device format not supported by WasapiLoopbackCapture");
                                    _LoopbackCapture = null;
                                }
                            }
                            else
                            {
                                LoggingService.Warn("[AudioRecorder] No speaker device selected or found - speaker recording will be disabled");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LoggingService.Error($"[AudioRecorder] Failed to initialize speaker capture: {ex.Message}");
                        LoggingService.Info("[AudioRecorder] Continuing without speaker recording - microphone will still work");
                        _LoopbackCapture = null; // Ensure it's null so we know speaker failed
                        
                        // Run diagnostic to help identify the issue
                        LoggingService.Info("[AudioRecorder] Running speaker audio diagnostic...");
                        DiagnoseSpeakerAudioIssues();
                    }
                }

                // CRITICAL: Initialize audio queues BEFORE starting processing thread
                if (_MicQueue == null)
                {
                    _MicQueue = new ConcurrentQueue<DataBuffer>();
                }
                if (_LoopBackFloatQueue == null)
                {
                    _LoopBackFloatQueue = new ConcurrentQueue<DataBuffer>();
                }

                // Initialize audio processing thread AFTER queues are ready
                if (_AudioProcessingThread == null || !_AudioProcessingThread.IsAlive)
                {
                try
                {
                    _AudioProcessingThread = new Thread(ProcessAndWriteAudio);
                    _AudioProcessingThread.IsBackground = true;
                    _AudioProcessingThread.Start();
                    LoggingService.Info("[AudioRecorder] Audio processing thread started successfully");
                    
                    // CRITICAL: Verify thread is actually running
                    Thread.Sleep(100); // Give thread time to start
                    if (!_AudioProcessingThread.IsAlive)
                    {
                        LoggingService.Error("[AudioRecorder] Audio processing thread failed to start!");
                    }
                }
                catch (Exception ex)
                {
                    LoggingService.Error($"[AudioRecorder] Error starting audio processing thread: {ex.Message}");
                }
                }

                // Initialize spectrum data lists
                if (_MicSpectrumdata == null)
                {
                    _MicSpectrumdata = new List<byte>();
                }
                if (_SpeakerSpectrumdata == null)
                {
                    _SpeakerSpectrumdata = new List<byte>();
                }
                
                // Final status check
                bool hasAtLeastOneDevice = (_MicCapture != null) || (_LoopbackCapture != null);
                if (hasAtLeastOneDevice)
                {
                    LoggingService.Info("[AudioRecorder] === AUDIO DEVICE DIAGNOSTIC END - At least one device available ===");
                }
                else
                {
                    LoggingService.Warn("[AudioRecorder] === AUDIO DEVICE DIAGNOSTIC END - No devices available ===");
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[AudioRecorder] Critical error during audio device initialization: {ex.Message}");
                LoggingService.Info("[AudioRecorder] === AUDIO DEVICE DIAGNOSTIC END - Initialization failed ===");
                // Don't throw - let individual device failures be handled gracefully
            }
        }

        /// <summary>
        /// Start Recording....
        /// NOTE: Waveform visibility is controlled by the UI (VoiceSessionPage) based on call lifecycle,
        /// not by this audio recording state. Waveforms should be visible from call start to call end,
        /// independent of temporary audio device issues or service errors.
        /// </summary>
        public void StartRecording()
        {
            try
            {
                if (_IsRecording)
                {
                    LoggingService.Info("[AudioRecorder] Already recording");
                    return;
                }

                LoggingService.Info("[AudioRecorder] Starting recording...");
                
                // CRITICAL: Set recording flag BEFORE initializing devices to prevent race condition
                _IsRecording = true;
                
                // Initialize audio capture devices if not already done
                InitializeAudioCaptureDevices();
                
                // Initialize the 10-second voice detection timer
                InitializeVoiceDetectionTimer();
                
                // Start the existing audio capture system - handle partial failures gracefully
                bool micStarted = false;
                bool speakerStarted = false;
                
                if (_MicCapture != null)
                {
                    try
                    {
                        _MicCapture.StartRecording();
                        micStarted = true;
                        LoggingService.Info("[AudioRecorder] Microphone capture started successfully");
                    }
                    catch (Exception ex)
                    {
                        LoggingService.Error($"[AudioRecorder]  Failed to start microphone capture: {ex.Message}");
                        _MicCapture = null; // Clear failed device
                    }
                }
                else
                {
                    LoggingService.Warn("[AudioRecorder] ⚠️ Microphone capture not available - continuing with speaker only");
                }
                
                if (_LoopbackCapture != null)
                {
                    try
                    {
                        _LoopbackCapture.StartRecording();
                        speakerStarted = true;
                        LoggingService.Info("[AudioRecorder]Speaker capture started successfully");
                    }
                    catch (Exception ex)
                    {
                        LoggingService.Error($"[AudioRecorder] ❌ Failed to start speaker capture: {ex.Message}");
                        _LoopbackCapture = null; // Clear failed device
                    }
                }
                else
                {
                    LoggingService.Warn("[AudioRecorder] ⚠ Speaker capture not available - continuing with microphone only");
                }
                
                // Log final status
                if (micStarted && speakerStarted)
                {
                    LoggingService.Info("[AudioRecorder]  Recording started successfully - Both microphone and speaker active");
                }
                else if (micStarted)
                {
                    LoggingService.Info("[AudioRecorder]  Recording started with microphone only - Speaker not available");
                }
                else if (speakerStarted)
                {
                    LoggingService.Info("[AudioRecorder]  Recording started with speaker only - Microphone not available");
                    LoggingService.Info("[AudioRecorder]  Speaker-only mode is fully functional - waveforms and transcription will work");
                }
                else
                {
                    LoggingService.Error("[AudioRecorder] ❌ No audio devices available - Recording cannot function");
                    _IsRecording = false;
                    throw new InvalidOperationException("No audio devices available for recording");
                }
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[AudioRecorder] Error starting recording: {ex.Message}");
                _IsRecording = false;
            }
        }

        /// <summary>
        /// Loop back recording Stop event
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void LoopBackSourceRecordingStopped(object? sender, StoppedEventArgs e)
        {
            try
            {
                LoggingService.Info("[AudioRecorder] LoopBackSourceRecordingStopped called - audio device may have disconnected");
                LoggingService.Info($"[AudioRecorder] Recording status: IsRecording={_IsRecording}");
                LoggingService.Info($"[AudioRecorder] StoppedEventArgs: {e?.Exception?.Message ?? "No exception"}");
                
                // Clean up the stopped loopback capture
                if (_LoopbackCapture != null)
                {
                    _LoopbackCapture.StopRecording();
                    _LoopbackCapture.Dispose();
                    _LoopbackCapture = null;
                }
                LoggingService.Info("[AudioRecorder] Loopback capture stopped and disposed");
                
                // NOTE: Do NOT set _IsRecording = false here
                // This is just a device disconnection, not a user-initiated stop
                // Waveforms should remain visible until user explicitly ends the call
                LoggingService.Info("[AudioRecorder] Loopback device disconnected but call remains active - waveforms stay visible");
                
                // Try to reinitialize loopback capture if still recording
                if (_IsRecording)
                {
                    Task.Run(async () =>
                    {
                        await Task.Delay(2000); // Wait 2 seconds before retry
                        try
                        {
                            LoggingService.Info("[AudioRecorder] Attempting to reinitialize loopback capture...");
                            // Attempt to reinitialize audio devices
                            InitializeAudioCaptureDevices();
                            LoggingService.Info("[AudioRecorder] Loopback capture reinitialization attempted");
                        }
                        catch (Exception reinitEx)
                        {
                            LoggingService.Info($"[AudioRecorder] Failed to reinitialize loopback capture: {reinitEx.Message}");
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[AudioRecorder] Error in LoopBackSourceRecordingStopped: {ex.Message}");
            }
        }

        /// <summary>
        /// Loop back data available.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void LoopBackSourceDataAvailable(object? sender, WaveInEventArgs e)
        {
            try
            {
           
                DataBuffer dataBuffer = new DataBuffer();
                dataBuffer.SecondsDataReceived = DateTime.Now.Second;
                dataBuffer.Data = new byte[e.BytesRecorded];
                Array.ConstrainedCopy(e.Buffer, 0, dataBuffer.Data, 0, e.BytesRecorded);
                _LoopBackFloatQueue.Enqueue(dataBuffer);
                dataBuffer = null;
                
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[AudioRecorder] Error in LoopBackSourceDataAvailable: {ex.Message}");
            }
        }

        /// <summary>
        /// Mic source recording stpped.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void MicSourceRecordingStopped(object? sender, StoppedEventArgs e)
        {
            try
            {
                LoggingService.Info("[AudioRecorder] MicSourceRecordingStopped called - audio device may have disconnected");
                LoggingService.Info($"[AudioRecorder] Recording status: IsRecording={_IsRecording}");
                LoggingService.Info($"[AudioRecorder] StoppedEventArgs: {e?.Exception?.Message ?? "No exception"}");
                
                // Clean up the stopped microphone capture
                if (_MicCapture != null)
                {
                    _MicCapture.StopRecording();
                    _MicCapture.Dispose();
                    _MicCapture = null;
                }
                LoggingService.Info("[AudioRecorder] Microphone capture stopped and disposed");
                
                // NOTE: Do NOT set _IsRecording = false here
                // This is just a device disconnection, not a user-initiated stop
                // Waveforms should remain visible until user explicitly ends the call
                LoggingService.Info("[AudioRecorder] Microphone device disconnected but call remains active - waveforms stay visible");
                
                // Try to reinitialize microphone capture if still recording
                if (_IsRecording)
                {
                    Task.Run(async () =>
                    {
                        await Task.Delay(2000); // Wait 2 seconds before retry
                        try
                        {
                            LoggingService.Info("[AudioRecorder] Attempting to reinitialize microphone capture...");
                            // Attempt to reinitialize audio devices
                            InitializeAudioCaptureDevices();
                            LoggingService.Info("[AudioRecorder] Microphone capture reinitialization attempted");
                        }
                        catch (Exception reinitEx)
                        {
                            LoggingService.Info($"[AudioRecorder] Failed to reinitialize microphone capture: {reinitEx.Message}");
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[AudioRecorder] Error in MicSourceRecordingStopped: {ex.Message}");
            }
        }

        /// <summary>
        /// Mic source data available.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void MicSourceDataAvailable(object? sender, WaveInEventArgs e)
        {
            try
            {
                              
                DataBuffer dataBuffer = new DataBuffer();
                dataBuffer.SecondsDataReceived = DateTime.Now.Second;
                dataBuffer.Data = new byte[e.BytesRecorded];
                Array.ConstrainedCopy(e.Buffer, 0, dataBuffer.Data, 0, e.BytesRecorded);
                _MicQueue.Enqueue(dataBuffer);
                dataBuffer = null;
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[AudioRecorder] Error in MicSourceDataAvailable: {ex.Message}");
            }
        }

        /// <summary>
        /// Capturing Stopped - USER-INITIATED call end
        /// This should ONLY be called when the user explicitly ends the call.
        /// Do NOT call this for temporary service errors or device disconnections.
        /// </summary>
        public void StopCapture()
        {
            try
            {
                LoggingService.Info("[AudioRecorder] StopCapture called - this will hide waveforms");
                LoggingService.Info($"[AudioRecorder] Current recording status: IsRecording={_IsRecording}");
                
                _IsRecording = false;

            // Stop capturing from both the loopback and microphone
            if (_LoopbackCapture != null)
            {
                try
                {
                    _LoopbackCapture.StopRecording();
                    LoggingService.Info("[AudioRecorder] Loopback capture stopped");
                }
                catch (Exception ex)
                {
                    LoggingService.Info($"[AudioRecorder] ?? Error stopping loopback capture: {ex.Message}");
                }
            }
            else
            {
                LoggingService.Info("[AudioRecorder] Loopback capture was already null");
            }

            if (_MicCapture != null)
            {
                try
                {
                    _MicCapture.StopRecording();
                    LoggingService.Info("[AudioRecorder] Microphone capture stopped");
                }
                catch (Exception ex)
                {
                    LoggingService.Info($"[AudioRecorder] ?? Error stopping microphone capture: {ex.Message}");
                }
            }
            else
            {
                LoggingService.Info("[AudioRecorder] Microphone capture was already null");
            }

            // Stop WAV recording if active
            if (_isWavRecording)
            {
                StopWavRecording();
            }

            //Clear Spectrum Analyzer
            ResetSpectrumData();
            LoggingService.Info("[AudioRecorder] StopCapture completed");
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[AudioRecorder] ? Error during StopCapture: {ex.Message}");
                LoggingService.Info($"[AudioRecorder] Stack trace: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// Process audio from Mic and speaker separately (no longer mixing)
        /// </summary>
        private void ProcessAndWriteAudio()
        {
            try
            {
                // Add safety check for audio processing - allow processing if at least one device is available
                if (_CommonFormat == null || (_MicCapture == null && _LoopbackCapture == null))
                {
                    LoggingService.Warn($"[AudioRecorder] No audio devices available, skipping processing cycle - Mic: {_MicCapture != null}, Speaker: {_LoopbackCapture != null}, Format: {_CommonFormat != null}");
                    return;
                }
             
                // Log device status for debugging
                LoggingService.Info($"[AudioRecorder] Audio processing started - Mic: {_MicCapture != null}, Speaker: {_LoopbackCapture != null}, Format: {_CommonFormat != null}");
                
                // Log speaker-only mode status
                if (_MicCapture == null && _LoopbackCapture != null)
                {
                    LoggingService.Info("[AudioRecorder] 🔊 Running in speaker-only mode - microphone not available");
                }

               // Safety check: ensure audio devices are initialized before processing
               if (_CommonFormat == null)
               {
                   LoggingService.Info("[AudioRecorder] Warning: _CommonFormat is null in ProcessAndWriteAudio, initializing audio devices...");
                   InitializeAudioCaptureDevices();
                   
                   // If still null after initialization, wait a bit and try again
                   if (_CommonFormat == null)
                   {
                       LoggingService.Info("[AudioRecorder] Error: Failed to initialize _CommonFormat in ProcessAndWriteAudio, waiting for initialization...");
                       Thread.Sleep(1000); // Wait 1 second
                       return; // Skip this processing cycle
                   }
               }

               Dictionary<int, byte[]> loopbackDataPCMBuffer = new Dictionary<int, byte[]>();
               Dictionary<int, byte[]> micDataPCMBuffer = new Dictionary<int, byte[]>();

               try
               {
                   int processedChunks = 0;
                   
                   while (_IsRecording)
               {
                   DataBuffer micDataFromBuffer = null;
                   DataBuffer loopBackDataFromBuffer = null;

                   // Check for next buffers for both mic and loopback
                   _MicQueue.TryDequeue(out micDataFromBuffer);  // Dequeue microphone data
                   _LoopBackFloatQueue.TryDequeue(out loopBackDataFromBuffer);

                   // If both data are null, wait a bit to prevent tight loop
                   if (micDataFromBuffer == null && loopBackDataFromBuffer == null)
                   {
                       // When NO audio data is coming in (e.g., YouTube paused), decay the waveform
                       CheckAndResetWaveformsOnSilence();
                       
                       Thread.Sleep(10); // 10ms delay when no data available
                       continue;
                   }

                    if ((micDataFromBuffer?.Data?.Length == 0 || micDataFromBuffer?.Data?.Length == null) &&
                        (loopBackDataFromBuffer?.Data?.Length == 0 || loopBackDataFromBuffer?.Data?.Length == null))
                    {
                        // When empty data is received, also decay the waveform
                        CheckAndResetWaveformsOnSilence();
                        Thread.Sleep(10); // 10ms delay when data is empty
                        continue;
                    }

                    // Process microphone data - SIMPLIFIED
                    if (micDataFromBuffer != null && micDataFromBuffer.Data != null && _MicCapture != null)
                    {
                        // Store data for transcription
                        if (micDataPCMBuffer.ContainsKey(micDataFromBuffer.SecondsDataReceived))
                            micDataPCMBuffer[micDataFromBuffer.SecondsDataReceived] = ConcatByteArrays(micDataPCMBuffer[micDataFromBuffer.SecondsDataReceived], micDataFromBuffer.Data);
                        else
                            micDataPCMBuffer.Add(micDataFromBuffer.SecondsDataReceived, micDataFromBuffer.Data);

                        // SIMPLIFIED: Always update waveform - no complex VAD processing
                        SetAndDisplayMicDataAmplitude(micDataFromBuffer.Data, micDataFromBuffer.Data.Length);
                    }

                    // Process loopback data
                    if (loopBackDataFromBuffer != null && loopBackDataFromBuffer.Data != null && _LoopbackCapture != null)
                    {
                        // Create the input stream from the loopback data
                        var inputStream2 = new RawSourceWaveStream(loopBackDataFromBuffer.Data, 0, loopBackDataFromBuffer.Data.Length, _LoopbackCapture.WaveFormat);

                        // DEBUG: Log the input format for troubleshooting
                        if (Globals.EnableVerboseLogging)
                        {
                            LoggingService.Debug($"[AudioRecorder] Speaker input format: {_LoopbackCapture.WaveFormat.SampleRate}Hz, {_LoopbackCapture.WaveFormat.BitsPerSample}-bit, {_LoopbackCapture.WaveFormat.Channels} channels");
                        }

                        IWaveProvider currentStream = inputStream2;

                        // Handle different audio formats dynamically - format can vary between devices
                        try
                        {
                            // Check the actual format and convert accordingly
                            var inputFormat = _LoopbackCapture.WaveFormat;
                            
                            // Log format changes for debugging (DISABLED - too verbose)
                            //if (Globals.EnableAudioFormatDiagnostics && Globals.LogAudioFormatChanges)
                            //{
                            //    LoggingService.Info($"[AudioRecorder] Processing speaker audio: {inputFormat.SampleRate}Hz, {inputFormat.BitsPerSample}-bit, {inputFormat.Channels} channels");
                            //}
                            
                            // Use a universal approach that works with any format
                            // Convert to samples first (handles any bit depth), then resample, then convert to 16-bit
                            var mainSampleStream = new NAudio.Wave.SampleProviders.WaveToSampleProvider(currentStream);
                            var mainResamplingProvider = new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(mainSampleStream, 16000);
                            var mainPcm16Provider = new NAudio.Wave.SampleProviders.SampleToWaveProvider16(mainResamplingProvider);
                            currentStream = new Wave16ToFloatProvider(mainPcm16Provider);
                            
                            if (Globals.EnableAudioFormatDiagnostics && Globals.VerboseAudioLogging)
                            {
                                LoggingService.Debug("[AudioRecorder] Speaker audio conversion pipeline: Raw -> Samples -> 16kHz -> 16-bit -> Float");
                            }
                        }
                        catch (Exception ex)
                        {
                            LoggingService.Error($"[AudioRecorder] Error converting speaker audio to float: {ex.Message}");
                            LoggingService.Error($"[AudioRecorder] Speaker format: {_LoopbackCapture.WaveFormat.SampleRate}Hz, {_LoopbackCapture.WaveFormat.BitsPerSample}-bit, {_LoopbackCapture.WaveFormat.Channels} channels");
                            
                            // Try a more basic conversion as fallback using universal approach
                            try
                            {
                                LoggingService.Info("[AudioRecorder] Attempting fallback audio conversion using universal pipeline...");
                                
                                // Use the same universal approach that works with any format
                                var fallbackSampleStream = new NAudio.Wave.SampleProviders.WaveToSampleProvider(inputStream2);
                                var fallbackResamplingProvider = new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(fallbackSampleStream, 16000);
                                var fallbackPcm16Provider = new NAudio.Wave.SampleProviders.SampleToWaveProvider16(fallbackResamplingProvider);
                                currentStream = new Wave16ToFloatProvider(fallbackPcm16Provider);
                                
                                LoggingService.Info("[AudioRecorder] Fallback conversion successful using universal pipeline");
                            }
                            catch (Exception fallbackEx)
                            {
                                LoggingService.Error($"[AudioRecorder] Fallback conversion also failed: {fallbackEx.Message}");
                                // Skip this audio chunk if all conversions fail
                                continue;
                            }
                        }

                        // Convert the data to floating point format
                        IWaveProvider stream32 = currentStream;

                        // Convert the floating point stream to samples
                        var sampleStream = new NAudio.Wave.SampleProviders.WaveToSampleProvider(stream32);

                        // Resample the data if needed (e.g., changing sample rate)
                        var resamplingProvider = new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(sampleStream, _CommonFormat.SampleRate);

                        // Convert back from samples to 16-bit PCM format
                        var ieeeToPCM = new NAudio.Wave.SampleProviders.SampleToWaveProvider16(resamplingProvider);

                        // Convert stereo to mono, reducing data by halving channels
                        var stereoToPCM = new StereoToMonoProvider16(ieeeToPCM);

                        // Read the data from the stream
                        // Calculate expected output size: original size / channels / (original bits per sample / 16 bits)
                        int channels = _LoopbackCapture.WaveFormat.Channels;
                        int bitsPerSample = _LoopbackCapture.WaveFormat.BitsPerSample;
                        int bytesToRead = loopBackDataFromBuffer.Data.Length / channels / (bitsPerSample / 16);
                        
                        // DEBUG: Log buffer size calculation
                        if (Globals.EnableVerboseLogging)
                        {
                            LoggingService.Debug($"[AudioRecorder] Buffer size calculation: Original={loopBackDataFromBuffer.Data.Length} bytes, Channels={channels}, BitsPerSample={bitsPerSample}, Expected output={bytesToRead} bytes");
                        }

                        byte[] loopbackDataPCM = readStream(stereoToPCM, bytesToRead);
                        
                        // DEBUG: Log audio conversion details for speaker audio
                        if (Globals.EnableVerboseLogging && loopbackDataPCM.Length > 0)
                        {
                            double rmsBefore = CalculateRMS(loopBackDataFromBuffer.Data);
                            double rmsAfter = CalculateRMS(loopbackDataPCM);
                            LoggingService.Debug($"[AudioRecorder] Speaker audio conversion: Original={loopBackDataFromBuffer.Data.Length} bytes (48kHz stereo), Converted={loopbackDataPCM.Length} bytes (16kHz mono), RMS before={rmsBefore:F4}, RMS after={rmsAfter:F4}");
                        }

                        if (loopbackDataPCMBuffer.ContainsKey(loopBackDataFromBuffer.SecondsDataReceived))
                            loopbackDataPCMBuffer[loopBackDataFromBuffer.SecondsDataReceived] = ConcatByteArrays(loopbackDataPCMBuffer[loopBackDataFromBuffer.SecondsDataReceived], loopbackDataPCM);
                        else
                            loopbackDataPCMBuffer.Add(loopBackDataFromBuffer.SecondsDataReceived, loopbackDataPCM);

                        // SIMPLIFIED: Always update waveform - no complex VAD processing
                        SetAndDisplaySpeakerDataAmplitude(loopbackDataPCM, loopbackDataPCM.Length);
                        
                        // OPTIMIZATION: Process speaker audio immediately without waiting for accumulation
                        // This reduces delays significantly
                        // VAD FILTERING REMOVED: Always process speaker audio regardless of VAD decision
                        if (loopbackDataPCM.Length > 0)
                        {
                            // DEBUG: Log speaker audio processing
                            // Processing all speaker audio without VAD filtering
                            
                            // Update last voice time - DISABLED
                            // _lastSpeakerVoiceTime = DateTime.Now;
                            
                            // OPTIMIZATION: Send directly to backend to bypass ALL delays
                            SendSpeakerAudioDirectly(loopbackDataPCM);
                            
                            // DISABLED: Dual processing path removed to eliminate redundancy and delays
                            // _audioChunkingService?.AddSpeakerAudioData(loopbackDataPCM, true);
                        }
                        
                        // OPTIMIZATION: Periodically clean up speaker audio buffers to prevent accumulation delays
                        var timeSinceLastCleanup = DateTime.Now - _lastSpeakerBufferCleanup;
                        if (timeSinceLastCleanup.TotalSeconds >= SPEAKER_BUFFER_CLEANUP_INTERVAL_SECONDS)
                        {
                            ClearSpeakerAudioBuffers();
                            _lastSpeakerBufferCleanup = DateTime.Now;
                        }
                    }

                    // OPTIMIZATION: Process only microphone audio here - speaker audio is handled immediately via VAD
                    if (micDataPCMBuffer.Count > 0)
                    {
                        // Process only agent (microphone) audio - customer audio is handled immediately
                        List<byte> agentAudioList = new List<byte>();

                        // Loop through the microphone data
                        foreach (var key in micDataPCMBuffer.Keys)
                        {
                            byte[] micData = micDataPCMBuffer[key];
                            
                            // Agent audio (microphone) - ROLE SWAPPED
                            if (micData != null)
                            {
                                agentAudioList.AddRange(micData);
                                
                                // Use VAD for transcription but keep waveform independent
                                // VAD controls what gets sent to backend, not the waveform display
                                bool hasVoice = DetectVoiceActivity(micData);
                                _audioChunkingService?.AddMicAudioData(micData, hasVoice);
                            }
                        }
                        
                        AudioChunkholderUtility.CheckOverFlow();

                        // Create agent audio chunk only
                        if (agentAudioList.Count > 0)
                        {
                            var agentAudioChunk = agentAudioList.ToArray();
                            AudioChunkholderUtility.AudioChunkHolderForSpecifiedSec.Enqueue(agentAudioChunk);
                            
                            // Track audio for visualization (sending to backend is handled by AudioChunkingService)
                            if (_enableVadFiltering)
                            {
                                // ConcurrentQueue is thread-safe, no lock needed
                                    _micVadFilteredAudio.Enqueue(agentAudioChunk);
                                    while (_micVadFilteredAudio.Count > VAD_WAVEFORM_HISTORY_SIZE)
                                    _micVadFilteredAudio.TryDequeue(out _);
                            }
                        }
                        
                        // Write to WAV files if recording is enabled
                        if (_isWavRecording)
                        {
                            try
                            {
                                if (agentAudioList.Count > 0)
                                {
                                    _micWavWriter?.Write(agentAudioList.ToArray(), 0, agentAudioList.Count);  // Agent is on mic
                                }
                            }
                            catch (Exception ex)
                            {
                                LoggingService.Info($"[AudioRecorder] Error writing to WAV files: {ex.Message}");
                            }
                        }

                        // Clear the buffers to start counting again
                        micDataPCMBuffer.Clear();
                        loopbackDataPCMBuffer.Clear();
                    }
                    
                    // Small delay to prevent high CPU usage while maintaining responsiveness
                    Thread.Sleep(5); // 5ms delay for CPU efficiency
                }
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[AudioRecorder] Error in audio processing loop: {ex.Message}");
                //??MessageBox.Show("Error during audio mixing: " + ex.Message);
            }
        }
        catch (Exception outerEx)
        {
            LoggingService.Error($"[AudioRecorder] Audio processing thread crashed: {outerEx.Message}");
        }
        }

        /// <summary>
        /// Read stream as byte array
        /// </summary>
        /// <param name="waveStream"></param>
        /// <param name="length"></param>
        /// <returns></returns>
        private byte[] readStream(IWaveProvider waveStream, int length)
        {
            byte[] buffer = new byte[length];
            using (var stream = new MemoryStream())
            {
                int read;
                while ((read = waveStream.Read(buffer, 0, length)) > 0)
                {
                    stream.Write(buffer, 0, read);
                }
                return stream.ToArray();
            }
        }

        /// <summary>
        /// Concat two byte arrays
        /// </summary>
        /// <param name="array1"></param>
        /// <param name="array2"></param>
        /// <returns></returns>
        private static byte[] ConcatByteArrays(byte[] array1, byte[] array2)
        {
            // Create a new array to hold the concatenated result
            byte[] result = new byte[array1.Length + array2.Length];

            // Copy the first array into the result
            Array.Copy(array1, 0, result, 0, array1.Length);

            // Copy the second array into the result
            Array.Copy(array2, 0, result, array1.Length, array2.Length);

            return result;
        }

        /// <summary>
        /// Reset Spectrum and Spectrum Data.
        /// </summary>
        internal void ResetSpectrumData()
        {
            try
            {
                _Spectrumdata.Clear();
                _MicSpectrumdata.Clear();
                _SpeakerSpectrumdata.Clear();
                _Spectrumdata = Enumerable.Repeat((byte)0.00, NumberOfLines).ToList();
                _MicSpectrumdata = Enumerable.Repeat((byte)0.00, NumberOfLines).ToList();
                _SpeakerSpectrumdata = Enumerable.Repeat((byte)0.00, NumberOfLines).ToList();
                SpectrumVisualizer?.ClearDisplay();
                MicSpectrumVisualizer?.ClearDisplay();
                SpeakerSpectrumVisualizer?.ClearDisplay();
                
                // Clear avatar visualizers
                _agentAvatarVisualizer?.ClearDisplay();
                _customerAvatarVisualizer?.ClearDisplay();
                
                // Also reset VAD state when clearing spectrum data
                ResetVadState();
            }
            catch { }
        }

        /// <summary>
        /// Check if the analyzer is currently recording
        /// </summary>
        /// <returns>True if recording, false otherwise</returns>
        public bool IsRecording()
        {
            return _IsRecording;
        }
       
        /// <summary>
        /// Set and display microphone data amplitude for separate visualization.
        /// </summary>
        /// <param name="inpBuffer"></param>
        /// <param name="inpBufferLength"></param>
        private void SetAndDisplayMicDataAmplitude(byte[] inpBuffer, int inpBufferLength)
        {
            try
            {
                // Basic safety checks
                if (inpBuffer == null || inpBufferLength <= 0 || MicSpectrumVisualizer == null)
                    return;

                // Initialize spectrum data if needed
                if (_MicSpectrumdata == null)
                {
                    _MicSpectrumdata = new List<byte>();
                }

                // REALISTIC: Calculate actual audio level without artificial gain
                double amplitude = 0.0;
                
                if (inpBufferLength > 0)
                {
                    // Calculate actual audio level from buffer data
                    long sumSquares = 0;
                    int sampleCount = 0;
                    
                    // Sample every 8th byte for performance (16-bit samples)
                    for (int i = 0; i < inpBufferLength - 1; i += 16) // Every 8th sample
                    {
                        if (i + 1 < inpBufferLength)
                        {
                            short sample = BitConverter.ToInt16(inpBuffer, i);
                            sumSquares += (long)sample * sample;
                            sampleCount++;
                        }
                    }
                    
                    if (sampleCount > 0)
                    {
                        // Calculate RMS amplitude
                        double rms = Math.Sqrt((double)sumSquares / sampleCount);
                        
                        // Convert to percentage (0-100) with very sensitive scaling for low audio
                        amplitude = Math.Min(100.0, (rms / 8192.0) * 100.0); // Use 8192 for high sensitivity to low audio
                        
                        // Apply minimal noise floor - show even very quiet audio
                        if (amplitude < 0.05) amplitude = 0.0; // Only filter out extremely quiet noise
                    }
                }
                
                // Always update last audio time when we receive data (even if silent)
                // This prevents waveform from decaying during active capture
                _lastMicAudioTime = DateTime.Now;

                // Update spectrum data
                if (_MicSpectrumdata.Count >= NumberOfLines)
                {
                    _MicSpectrumdata.RemoveAt(0);
                }

                // Convert to display value
                int amplitudeForDisplay = (int)(amplitude * 2.55); // 0-255 range
                _MicSpectrumdata.Add((byte)amplitudeForDisplay);

                // Update UI (throttle disabled for smooth animation)
                if (_syncContext != null /* && DateTime.Now.Ticks % 3 == 0 */) // Throttle commented for smoother waveform
                {
                    _syncContext.Post(_ =>
                    {
                        try
                        {
                            MicSpectrumVisualizer?.Set(_MicSpectrumdata.ToArray());
                            _agentAvatarVisualizer?.Set(_MicSpectrumdata.ToArray());
                        }
                        catch (Exception uiEx)
                        {
                            // Silent fail for UI updates
                        }
                    }, null);
                }
            }
            catch (Exception ex)
            {
                // Silent fail - don't spam logs
            }
        }

        /// <summary>
        /// Test if a specific device supports loopback capture
        /// </summary>
        /// <param name="device">The audio device to test</param>
        /// <returns>True if device supports loopback capture, false otherwise</returns>
        public static bool TestDeviceLoopbackCapability(MMDevice device)
        {
            try
            {
                LoggingService.Info($"[LoopbackTest] Testing device: {device.FriendlyName}");
                LoggingService.Info($"[LoopbackTest] Device State: {device.State}");
                LoggingService.Info($"[LoopbackTest] Device ID: {device.ID}");
                
                // Test 1: Check if device is active
                if (device.State != DeviceState.Active)
                {
                    LoggingService.Warn($"[LoopbackTest] ❌ Device is not active (State: {device.State})");
                    return false;
                }
                
                // Test 2: Check if device has audio client
                try
                {
                    var audioClient = device.AudioClient;
                    if (audioClient == null)
                    {
                        LoggingService.Warn("[LoopbackTest] ❌ Device has no AudioClient");
                        return false;
                    }
                    LoggingService.Info("[LoopbackTest] ✅ Device has AudioClient");
                }
                catch (Exception ex)
                {
                    LoggingService.Warn($"[LoopbackTest] ❌ Failed to get AudioClient: {ex.Message}");
                    return false;
                }
                
                // Test 3: Check if device supports loopback format
                try
                {
                    var mixFormat = device.AudioClient?.MixFormat;
                    if (mixFormat == null)
                    {
                        LoggingService.Warn("[LoopbackTest] ❌ Device has no MixFormat");
                        return false;
                    }
                    LoggingService.Info($"[LoopbackTest] ✅ Device format: {mixFormat.SampleRate}Hz, {mixFormat.BitsPerSample}-bit, {mixFormat.Channels} channels");
                }
                catch (Exception ex)
                {
                    LoggingService.Warn($"[LoopbackTest] ❌ Failed to get MixFormat: {ex.Message}");
                    return false;
                }
                
                // Test 4: Try to create WasapiLoopbackCapture (this is the real test)
                try
                {
                    using (var testCapture = new WasapiLoopbackCapture(device))
                    {
                        LoggingService.Info("[LoopbackTest] ✅ WasapiLoopbackCapture created successfully");
                        LoggingService.Info($"[LoopbackTest] ✅ Capture format: {testCapture.WaveFormat.SampleRate}Hz, {testCapture.WaveFormat.BitsPerSample}-bit, {testCapture.WaveFormat.Channels} channels");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    LoggingService.Warn($"[LoopbackTest] ❌ Failed to create WasapiLoopbackCapture: {ex.Message}");
                    LoggingService.Warn($"[LoopbackTest] Exception type: {ex.GetType().Name}");
                    
                    // Provide specific guidance based on exception type
                    if (ex.Message.Contains("exclusive"))
                    {
                        LoggingService.Warn("[LoopbackTest] 💡 Device is in exclusive mode - close other audio applications");
                    }
                    else if (ex.Message.Contains("access"))
                    {
                        LoggingService.Warn("[LoopbackTest] 💡 Access denied - check Windows privacy settings for microphone access");
                    }
                    else if (ex.Message.Contains("format"))
                    {
                        LoggingService.Warn("[LoopbackTest] 💡 Format not supported - device may not support loopback capture");
                    }
                    else if (ex.Message.Contains("driver"))
                    {
                        LoggingService.Warn("[LoopbackTest] 💡 Driver issue - update audio drivers or try different device");
                    }
                    
                    return false;
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[LoopbackTest] ❌ Test failed with exception: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Test all available devices for loopback capture capability
        /// </summary>
        public static void TestAllDevicesForLoopback()
        {
            try
            {
                LoggingService.Info("=== LOOPBACK CAPABILITY TEST START ===");
                
                MMDeviceEnumerator deviceEnumerator = new MMDeviceEnumerator();
                var allDevices = deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.All).ToList();
                
                LoggingService.Info($"[LoopbackTest] Testing {allDevices.Count} devices for loopback capability...");
                
                int compatibleDevices = 0;
                int incompatibleDevices = 0;
                
                for (int i = 0; i < allDevices.Count; i++)
                {
                    var device = allDevices[i];
                    LoggingService.Info($"[LoopbackTest] --- Testing Device {i + 1}/{allDevices.Count} ---");
                    
                    bool isCompatible = TestDeviceLoopbackCapability(device);
                    
                    if (isCompatible)
                    {
                        compatibleDevices++;
                        LoggingService.Info($"[LoopbackTest] ✅ Device {i + 1} is COMPATIBLE: {device.FriendlyName}");
                    }
                    else
                    {
                        incompatibleDevices++;
                        LoggingService.Warn($"[LoopbackTest] ❌ Device {i + 1} is INCOMPATIBLE: {device.FriendlyName}");
                    }
                }
                
                LoggingService.Info($"[LoopbackTest] === SUMMARY ===");
                LoggingService.Info($"[LoopbackTest] Compatible devices: {compatibleDevices}");
                LoggingService.Info($"[LoopbackTest] Incompatible devices: {incompatibleDevices}");
                
                if (compatibleDevices == 0)
                {
                    LoggingService.Warn("[LoopbackTest] ⚠️ NO COMPATIBLE DEVICES FOUND!");
                    LoggingService.Info("[LoopbackTest] Common solutions:");
                    LoggingService.Info("[LoopbackTest] 1. Try playing audio through speakers to activate devices");
                    LoggingService.Info("[LoopbackTest] 2. Update audio drivers");
                    LoggingService.Info("[LoopbackTest] 3. Check Windows privacy settings");
                    LoggingService.Info("[LoopbackTest] 4. Close other audio applications");
                    LoggingService.Info("[LoopbackTest] 5. Try different audio device in Windows settings");
                        }
                        else
                        {
                    LoggingService.Info($"[LoopbackTest] ✅ Found {compatibleDevices} compatible device(s) for loopback capture");
                }
                
                LoggingService.Info("=== LOOPBACK CAPABILITY TEST END ===");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[LoopbackTest] Test failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Comprehensive diagnostic method for speaker audio capture issues
        /// </summary>
        public static void DiagnoseSpeakerAudioIssues()
        {
            try
            {
                LoggingService.Info("=== SPEAKER AUDIO DIAGNOSTIC START ===");
                
                // Check Windows Audio Service (using alternative method)
                try
                {
                    LoggingService.Info("[Diagnostic] Checking Windows Audio Service...");
                    // Try to enumerate audio devices as a way to test if audio service is working
                    MMDeviceEnumerator testEnumerator = new MMDeviceEnumerator();
                    var testDevices = testEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.All).ToList();
                    LoggingService.Info($"[Diagnostic] ✅ Windows Audio Service appears to be running (found {testDevices.Count} devices)");
                }
                catch (Exception ex)
                {
                    LoggingService.Warn($"[Diagnostic] ⚠️ Windows Audio Service may not be running: {ex.Message}");
                }
                
                // Check available audio devices
                try
                {
                    MMDeviceEnumerator deviceEnumerator = new MMDeviceEnumerator();
                    
                    // Check all device states
                    var activeDevices = deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
                    var disabledDevices = deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Disabled).ToList();
                    var unpluggedDevices = deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Unplugged).ToList();
                    var notPresentDevices = deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.NotPresent).ToList();
                    
                    LoggingService.Info($"[Diagnostic] Active devices: {activeDevices.Count}");
                    LoggingService.Info($"[Diagnostic] Disabled devices: {disabledDevices.Count}");
                    LoggingService.Info($"[Diagnostic] Unplugged devices: {unpluggedDevices.Count}");
                    LoggingService.Info($"[Diagnostic] Not present devices: {notPresentDevices.Count}");
                    
                    // List all devices with their states
                    var allDevices = deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.All).ToList();
                    for (int i = 0; i < allDevices.Count; i++)
                    {
                        var device = allDevices[i];
                        LoggingService.Info($"[Diagnostic] Device {i}: {device.FriendlyName} (State: {device.State}, ID: {device.ID})");
                    }
                    
                    // Check default device
                    try
                    {
                        var defaultDevice = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                        if (defaultDevice != null)
                        {
                            LoggingService.Info($"[Diagnostic] Default device: {defaultDevice.FriendlyName} (State: {defaultDevice.State})");
                }
                else
                {
                            LoggingService.Warn("[Diagnostic] ⚠️ No default audio device found");
                        }
                    }
                    catch (Exception ex)
                    {
                        LoggingService.Warn($"[Diagnostic] Could not get default device: {ex.Message}");
                    }
                }
                catch (Exception ex)
                {
                    LoggingService.Error($"[Diagnostic] Failed to enumerate audio devices: {ex.Message}");
                }
                
                // Run loopback capability test
                LoggingService.Info("[Diagnostic] Running loopback capability test...");
                TestAllDevicesForLoopback();
                
                // Check for common issues
                LoggingService.Info("[Diagnostic] Common issues to check:");
                LoggingService.Info("[Diagnostic] 1. Ensure audio devices are enabled in Windows Sound settings");
                LoggingService.Info("[Diagnostic] 2. Check if Windows Audio Service is running");
                LoggingService.Info("[Diagnostic] 3. Verify audio drivers are installed and up to date");
                LoggingService.Info("[Diagnostic] 4. Check Windows privacy settings for microphone access");
                LoggingService.Info("[Diagnostic] 5. Ensure no other application is using exclusive audio mode");
                LoggingService.Info("[Diagnostic] 6. Try playing audio through speakers to activate the device");
                LoggingService.Info("[Diagnostic] 7. Check Windows Event Viewer for audio-related errors");
                LoggingService.Info("[Diagnostic] 8. Some devices (USB, Bluetooth, HDMI) may not support loopback capture");
                
                LoggingService.Info("=== SPEAKER AUDIO DIAGNOSTIC END ===");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[Diagnostic] Diagnostic failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Set and display speaker data amplitude for separate visualization.
        /// </summary>
        /// <param name="inpBuffer"></param>
        /// <param name="inpBufferLength"></param>
        private void SetAndDisplaySpeakerDataAmplitude(byte[] inpBuffer, int inpBufferLength)
        {
            try
            {
                // Basic safety checks
                if (inpBuffer == null || inpBufferLength <= 0 || SpeakerSpectrumVisualizer == null)
                    return;

                // Initialize spectrum data if needed
                if (_SpeakerSpectrumdata == null)
                {
                    _SpeakerSpectrumdata = new List<byte>();
                }

                // REALISTIC: Calculate actual audio level without artificial gain
                double amplitude = 0.0;

                if (inpBufferLength > 0)
                {
                    // Calculate actual audio level from buffer data
                    long sumSquares = 0;
                    int sampleCount = 0;
                    
                    // Sample every 8th byte for performance (16-bit samples)
                    for (int i = 0; i < inpBufferLength - 1; i += 16) // Every 8th sample
                    {
                        if (i + 1 < inpBufferLength)
                        {
                            short sample = BitConverter.ToInt16(inpBuffer, i);
                            sumSquares += (long)sample * sample;
                            sampleCount++;
                        }
                    }
                    
                    if (sampleCount > 0)
                    {
                        // Calculate RMS amplitude
                        double rms = Math.Sqrt((double)sumSquares / sampleCount);
                        
                        // Convert to percentage (0-100) with very sensitive scaling for low audio
                        amplitude = Math.Min(100.0, (rms / 8192.0) * 100.0); // Use 8192 for high sensitivity to low audio
                        
                        // Apply minimal noise floor - show even very quiet audio
                        if (amplitude < 0.05) amplitude = 0.0; // Only filter out extremely quiet noise
                    }
                }
                
                // Always update last audio time when we receive data (even if silent)
                // This prevents waveform from decaying during active capture
                _lastSpeakerAudioTime = DateTime.Now;
                _lastSpeakerWaveformUpdate = DateTime.Now; // Track waveform update time

                // Update spectrum data
                if (_SpeakerSpectrumdata.Count >= NumberOfLines)
                {
                    _SpeakerSpectrumdata.RemoveAt(0);
                }

                // Convert to display value
                int amplitudeForDisplay = (int)(amplitude * 2.55); // 0-255 range
                _SpeakerSpectrumdata.Add((byte)amplitudeForDisplay);

                // Update UI (throttle disabled for smooth animation)
                if (_syncContext != null /* && DateTime.Now.Ticks % 5 == 0 */) // Throttle commented for smoother waveform
                {
                    _syncContext.Post(_ =>
                    {
                        try
                    {
                        SpeakerSpectrumVisualizer?.Set(_SpeakerSpectrumdata.ToArray());
                        _customerAvatarVisualizer?.Set(_SpeakerSpectrumdata.ToArray());
                            
                            // Debug logging for speaker waveform updates
                            if (Globals.EnableVerboseLogging)
                            {
                                LoggingService.Debug($"[AudioRecorder] Speaker waveform updated - Data points: {_SpeakerSpectrumdata.Count}, Visualizer: {SpeakerSpectrumVisualizer != null}");
                            }
                        }
                        catch (Exception uiEx)
                        {
                            LoggingService.Warn($"[AudioRecorder] UI update error for speaker waveform: {uiEx.Message}");
                        }
                    }, null);
                }
            }
            catch (Exception ex)
            {
                // Silent fail - don't spam logs
            }
        }

        /// <summary>
        /// Check if waveforms need to be reset due to silence (no audio activity)
        /// </summary>
        private void CheckAndResetWaveformsOnSilence()
        {
            try
            {
                var now = DateTime.Now;
                
                // Check microphone waveform - instant reset to straight line after 3 seconds
                if ((now - _lastMicAudioTime).TotalMilliseconds > _waveformResetDelayMs)
                {
                    // Reset mic waveform to STRAIGHT LINE (zero amplitude) immediately
                    if (_MicSpectrumdata != null && _MicSpectrumdata.Count > 0)
                    {
                        // INSTANT reset to zero - make it a straight flat line
                        for (int i = 0; i < _MicSpectrumdata.Count; i++)
                        {
                            _MicSpectrumdata[i] = 0; // Flat line
                        }
                        
                        // Update UI
                        if (_syncContext != null)
                        {
                            _syncContext.Post(_ =>
                            {
                                try
                                {
                                    MicSpectrumVisualizer?.Set(_MicSpectrumdata.ToArray());
                                    _agentAvatarVisualizer?.Set(_MicSpectrumdata.ToArray());
                                }
                                catch { }
                            }, null);
                        }
                    }
                }
                
                // Check speaker waveform - instant reset to straight line after 3 seconds
                if ((now - _lastSpeakerWaveformUpdate).TotalMilliseconds > _waveformResetDelayMs)
                {
                    // Reset speaker waveform to STRAIGHT LINE (zero amplitude) immediately
                    if (_SpeakerSpectrumdata != null && _SpeakerSpectrumdata.Count > 0)
                    {
                        // INSTANT reset to zero - make it a straight flat line
                        for (int i = 0; i < _SpeakerSpectrumdata.Count; i++)
                        {
                            _SpeakerSpectrumdata[i] = 0; // Flat line
                        }
                        
                        // Update UI
                if (_syncContext != null)
                {
                    _syncContext.Post(_ =>
                            {
                                try
                    {
                        SpeakerSpectrumVisualizer?.Set(_SpeakerSpectrumdata.ToArray());
                        _customerAvatarVisualizer?.Set(_SpeakerSpectrumdata.ToArray());
                                }
                                catch { }
                    }, null);
                }
                }
                }
            }
            catch (Exception ex)
            {
                // Silent fail - don't spam logs
            }
        }
      
        /// <summary>
        /// Analyze pitch to distinguish human speech from fan/AC noise
        /// </summary>
        /// <param name="audioData">Audio data to analyze</param>
        /// <returns>True if pitch suggests human speech, false if fan/AC noise</returns>
        private bool AnalyzePitchForSpeech(byte[] audioData)
        {
            try
            {
                if (!_enablePitchAnalysis || audioData == null || audioData.Length == 0)
                    return true; // Default to speech if pitch analysis is disabled
                
                // Convert bytes to 16-bit samples
                int sampleCount = audioData.Length / 2;
                short[] samples = new short[sampleCount];
                
                for (int i = 0; i < sampleCount; i++)
                {
                    samples[i] = BitConverter.ToInt16(audioData, i * 2);
                }
                
                // For very short audio (like "hello"), be more lenient
                if (sampleCount < 256) // Less than 16ms of audio
                {
                    // Short audio chunks - use energy-based detection instead of strict pitch analysis
                    double rms = CalculateRMSFromSamples(samples);
                    double normalizedRms = rms / 32768.0;
                    
                    // If it's quiet, it's probably silence
                    if (normalizedRms < _vadSilenceThreshold * 0.5)
                        return false;
                    
                    // If it has reasonable energy, assume it's speech (don't reject short words)
                    if (normalizedRms > _vadSilenceThreshold)
                        return true;
                    
                    // For very short audio, default to speech unless clearly noise
                    return true;
                }
                
                // For longer audio, use full pitch analysis
                double pitch = DetectPitchUsingAutocorrelation(samples);
                
                // Update pitch history
                if (pitch > 0)
                {
                    _micPitchHistory.Enqueue(pitch);
                    while (_micPitchHistory.Count > PITCH_HISTORY_SIZE)
                        _micPitchHistory.Dequeue();
                }
                
                // Analyze pitch characteristics
                bool isHumanSpeech = AnalyzePitchCharacteristics(pitch);
                
                if (!isHumanSpeech)
                {
                    LoggingService.Info($"[VAD PITCH] Fan/AC noise detected - Pitch: {pitch:F1} Hz (below human speech threshold: {MIN_HUMAN_PITCH} Hz)");
                }
                
                return isHumanSpeech;
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[VAD PITCH] Error in pitch analysis: {ex.Message}");
                return true; // Default to speech on error
            }
        }
        
        /// <summary>
        /// Calculate RMS from samples array
        /// </summary>
        /// <param name="samples">Audio samples</param>
        /// <returns>RMS value</returns>
        private double CalculateRMSFromSamples(short[] samples)
        {
            try
            {
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
        /// Detect pitch using autocorrelation method
        /// </summary>
        /// <param name="samples">Audio samples</param>
        /// <returns>Detected pitch in Hz</returns>
        private double DetectPitchUsingAutocorrelation(short[] samples)
        {
            try
            {
                if (samples.Length < 512) // Need sufficient samples for pitch detection
                    return 0.0;
                
                // Sample rate is 16kHz
                const int sampleRate = 16000;
                const int minPitchPeriod = (int)(sampleRate / MAX_HUMAN_PITCH); // ~20 samples
                const int maxPitchPeriod = (int)(sampleRate / MIN_HUMAN_PITCH); // ~200 samples
                
                double maxCorrelation = 0.0;
                int bestPitchPeriod = 0;
                
                // Autocorrelation for pitch detection
                for (int lag = minPitchPeriod; lag <= maxPitchPeriod; lag++)
                {
                    double correlation = 0.0;
                    int correlationLength = samples.Length - lag;
                    
                    if (correlationLength <= 0) break;
                    
                    for (int i = 0; i < correlationLength; i++)
                    {
                        correlation += samples[i] * samples[i + lag];
                    }
                    
                    // Normalize correlation
                    correlation /= correlationLength;
                    
                    if (correlation > maxCorrelation)
                    {
                        maxCorrelation = correlation;
                        bestPitchPeriod = lag;
                    }
                }
                
                // Convert period to frequency
                if (bestPitchPeriod > 0 && maxCorrelation > 0.1) // Minimum correlation threshold
                {
                    double pitch = (double)sampleRate / bestPitchPeriod;
                    return pitch;
                }
                
                return 0.0; // No pitch detected
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[VAD PITCH] Error in autocorrelation: {ex.Message}");
                return 0.0;
            }
        }
        
        /// <summary>
        /// Analyze pitch characteristics to determine if it's human speech
        /// </summary>
        /// <param name="pitch">Detected pitch in Hz</param>
        /// <returns>True if pitch suggests human speech</returns>
        private bool AnalyzePitchCharacteristics(double pitch)
        {
            if (pitch <= 0) return true; // No pitch detected, default to speech
            
            // Human speech typically has pitch between 80-800 Hz
            if (pitch >= MIN_HUMAN_PITCH && pitch <= MAX_HUMAN_PITCH)
            {
                return true; // Likely human speech
            }
            
            // Fan/AC noise typically has pitch below 60 Hz
            if (pitch <= FAN_NOISE_PITCH_THRESHOLD)
            {
                return false; // Likely fan/AC noise
            }
            
            // Check pitch history for consistency (fan noise is very consistent)
            if (_micPitchHistory.Count >= 3)
            {
                // Get last 3 pitch values manually to avoid LINQ compatibility issues
                var pitchArray = _micPitchHistory.ToArray();
                int startIndex = Math.Max(0, pitchArray.Length - 3);
                int count = Math.Min(3, pitchArray.Length - startIndex);
                
                if (count > 0)
                {
                    double maxPitch = pitchArray[startIndex];
                    double minPitch = pitchArray[startIndex];
                    
                    for (int i = startIndex + 1; i < startIndex + count; i++)
                    {
                        if (pitchArray[i] > maxPitch) maxPitch = pitchArray[i];
                        if (pitchArray[i] < minPitch) minPitch = pitchArray[i];
                    }
                    
                    double pitchVariation = maxPitch - minPitch;
                    
                    // Very low pitch variation suggests mechanical noise
                    if (pitchVariation < 5.0) // Less than 5 Hz variation
                    {
                        return false; // Likely mechanical noise (fan/AC)
                    }
                }
            }
            
            return true; // Default to speech
        }

        /// <summary>
        /// Check if pitch analysis recently detected voice activity
        /// This allows bypassing consecutive frame requirements when pitch analysis confirms voice
        /// </summary>
        /// <param name="isMic">True for microphone, false for speaker</param>
        /// <returns>True if pitch analysis recently detected voice</returns>
        private bool HasRecentPitchAnalysisVoice(bool isMic)
        {
            try
            {
                // Check if pitch analysis is enabled
                if (!_enablePitchAnalysis)
                    return false;
                
                // Check the last few VAD decisions to see if pitch analysis bypassed consecutive frame requirements
                ConcurrentQueue<bool> vadDecisions = isMic ? _micVadDecisions : _speakerVadDecisions;
                
                if (vadDecisions.Count == 0)
                    return false;
                
                // Look at the last 5 VAD decisions to see if pitch analysis detected voice
                int recentFrameCount = Math.Min(5, vadDecisions.Count);
                var vadArray = vadDecisions.ToArray();
                int startIndex = Math.Max(0, vadArray.Length - recentFrameCount);
                int count = Math.Min(recentFrameCount, vadArray.Length - startIndex);
                
                if (count == 0)
                    return false;
                
                var recentFrames = new bool[count];
                Array.Copy(vadArray, startIndex, recentFrames, 0, count);
                
                // Count recent voice frames
                int recentVoiceFrames = recentFrames.Count(x => x);
                
                // For short words like "hello", be very aggressive - if we have ANY recent voice frames, bypass
                // This ensures that even a single "hello" gets through
                bool hasRecentVoice = recentVoiceFrames >= 1;
                
                if (hasRecentVoice)
                {
                    // Pitch analysis bypassed consecutive frame check
                }
                else
                {
                    // No recent pitch analysis voice detected
                }
                
                return hasRecentVoice;
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[VAD PITCH BYPASS] Error checking recent pitch analysis: {ex.Message}");
                return false;
            }
        }
      
        /// <summary>
        /// Check if pitch analysis detects voice in the current audio buffer
        /// This provides immediate voice detection without waiting for VAD decisions
        /// </summary>
        /// <param name="isMic">True for microphone, false for speaker</param>
        /// <returns>True if pitch analysis detects voice in current frame</returns>
        private bool CheckImmediatePitchAnalysisVoice(bool isMic)
        {
            try
            {
                // FIRST: Check if pitch analysis just detected voice in recent frames
                // This is the most reliable method - if pitch analysis says there's voice, trust it
                if (HasRecentPitchAnalysisVoice(isMic))
                {
                    LoggingService.Debug($"[VAD BYPASS] {(isMic ? "Microphone" : "Speaker")}: RECENT PITCH ANALYSIS VOICE DETECTED - bypassing immediately");
                    return true;
                }

                // SECOND: Analyze the current 10-second audio buffer directly for voice activity
                // This bypasses VAD decision history and checks the actual audio content
                bool hasVoiceInCurrentBuffer = CheckCurrentAudioBufferForVoice(isMic);
                if (hasVoiceInCurrentBuffer)
                {
                    LoggingService.Debug($"[VAD BYPASS] {(isMic ? "Microphone" : "Speaker")}: CURRENT BUFFER VOICE DETECTED - bypassing immediately");
                    return true;
                }

                // THIRD: Check the most recent audio chunk for immediate voice detection
                var lookbackBuffer = isMic ? _micLookbackBuffer : _speakerLookbackBuffer;
                if (lookbackBuffer.Count == 0)
                {
                    LoggingService.Debug($"[VAD BYPASS] {(isMic ? "Microphone" : "Speaker")}: No audio data in lookback buffer");
                    return false;
                }

                // Check the most recent audio chunk for immediate voice detection
                var mostRecentAudio = lookbackBuffer.LastOrDefault();
                if (mostRecentAudio == null || mostRecentAudio.Length == 0)
                {
                    LoggingService.Debug($"[VAD BYPASS] {(isMic ? "Microphone" : "Speaker")}: Most recent audio chunk is null or empty");
                    return false;
                }

                // Use ultra-sensitive thresholds for immediate detection
                double immediateThreshold = 0.003; // Even lower than VAD threshold
                double immediateSilenceThreshold = 0.001; // Very low silence threshold

                // Calculate RMS of current buffer
                double rms = CalculateRMS(mostRecentAudio);
                double normalizedAmplitude = rms / 32768.0; // Normalize to 0-1 range

                // Check if amplitude exceeds immediate threshold
                bool hasVoice = normalizedAmplitude > immediateThreshold;
                
                if (hasVoice)
                {
                    LoggingService.Debug($"[VAD BYPASS] {(isMic ? "Microphone" : "Speaker")}: IMMEDIATE PITCH CHECK - Amplitude: {normalizedAmplitude:F6} > {immediateThreshold:F6} = VOICE DETECTED");
                }
                else
                {
                    LoggingService.Debug($"[VAD BYPASS] {(isMic ? "Microphone" : "Speaker")}: IMMEDIATE PITCH CHECK - Amplitude: {normalizedAmplitude:F6} <= {immediateThreshold:F6} = No voice");
                }

                return hasVoice;
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[VAD BYPASS] Error in immediate pitch check: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Check the current 10-second audio buffer directly for voice activity
        /// This bypasses VAD decision history and analyzes the actual audio content
        /// </summary>
        /// <param name="isMic">True for microphone, false for speaker</param>
        /// <returns>True if voice is detected in the current audio buffer</returns>
        private bool CheckCurrentAudioBufferForVoice(bool isMic)
        {
            try
            {
                // Get the current accumulated audio buffer (the one being processed)
                // This is the 320,000+ byte buffer that contains the last 10 seconds of audio
                var currentAudioBuffer = GetCurrentAccumulatedAudioBuffer(isMic);
                if (currentAudioBuffer == null || currentAudioBuffer.Length == 0)
                {
                    LoggingService.Debug($"[VAD BYPASS] {(isMic ? "Microphone" : "Speaker")}: No current accumulated audio buffer available");
                    return false;
                }

                // Analyzing current audio buffer

                // Use ultra-sensitive thresholds for current buffer analysis
                double currentBufferThreshold = 0.002; // Very low threshold for 10-second buffer
                
                // Calculate RMS of the entire current buffer
                double rms = CalculateRMS(currentAudioBuffer);
                double normalizedAmplitude = rms / 32768.0; // Normalize to 0-1 range

                // Check if amplitude exceeds current buffer threshold
                bool hasVoice = normalizedAmplitude > currentBufferThreshold;
                
                if (hasVoice)
                {
                    LoggingService.Debug($"[VAD BYPASS] {(isMic ? "Microphone" : "Speaker")}: CURRENT BUFFER CHECK - Amplitude: {normalizedAmplitude:F6} > {currentBufferThreshold:F6} = VOICE DETECTED");
                }
                else
                {
                    LoggingService.Debug($"[VAD BYPASS] {(isMic ? "Microphone" : "Speaker")}: CURRENT BUFFER CHECK - Amplitude: {normalizedAmplitude:F6} <= {currentBufferThreshold:F6} = No voice");
                }

                return hasVoice;
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[VAD BYPASS] Error in current buffer voice check: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Get the current accumulated audio buffer that's being processed
        /// This is the 10-second buffer that contains the audio being sent to the backend
        /// </summary>
        /// <param name="isMic">True for microphone, false for speaker</param>
        /// <returns>The current accumulated audio buffer</returns>
        private byte[] GetCurrentAccumulatedAudioBuffer(bool isMic)
        {
            try
            {
                // This method should return the current audio buffer that's being processed
                // For now, we'll use the lookback buffer as a fallback
                var lookbackBuffer = isMic ? _micLookbackBuffer : _speakerLookbackBuffer;
                
                if (lookbackBuffer.Count == 0)
                    return null;

                // Combine all recent audio chunks to simulate the accumulated buffer
                var allAudioChunks = lookbackBuffer.ToArray();
                int totalLength = allAudioChunks.Sum(chunk => chunk?.Length ?? 0);
                
                if (totalLength == 0)
                    return null;

                var combinedBuffer = new byte[totalLength];
                int offset = 0;
                
                foreach (var chunk in allAudioChunks)
                {
                    if (chunk != null && chunk.Length > 0)
                    {
                        Array.Copy(chunk, 0, combinedBuffer, offset, chunk.Length);
                        offset += chunk.Length;
                    }
                }

                // Combined audio chunks into buffer
                return combinedBuffer;
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[VAD BYPASS] Error getting current accumulated audio buffer: {ex.Message}");
                return null;
            }
        }
      
        /// <summary>
        /// Initialize the 10-second voice detection timer (DISABLED - using new AudioChunkingService instead)
        /// </summary>
        private void InitializeVoiceDetectionTimer()
        {
            try
            {
                // DISABLED: Old 10-second timer system replaced with new configurable AudioChunkingService
                // _voiceDetectionTimer = new System.Timers.Timer(VOICE_DETECTION_INTERVAL_MS);
                // _voiceDetectionTimer.Elapsed += OnVoiceDetectionTimerElapsed;
                // _voiceDetectionTimer.AutoReset = true;
                // _voiceDetectionTimer.Start();
                
                LoggingService.Info($"[VAD TIMER] 10-second voice detection timer DISABLED - using new AudioChunkingService instead");
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[VAD TIMER] Error initializing voice detection timer: {ex.Message}");
            }
        }

        /// <summary>
        /// Check accumulated audio for voice activity and send to backend if voice is detected
        /// </summary>
        private void CheckAndSendAccumulatedAudio()
        {
            try
            {
                LoggingService.Info($"[VAD TIMER] === 10-SECOND VOICE DETECTION CHECK ===");
                
                // Check microphone audio
                bool micHasVoice = CheckAudioBufferForVoice(_accumulatedMicAudio, true);
                if (micHasVoice && _accumulatedMicAudio.Count > 0)
                {
                    // Include look-back chunks to capture speech onset
                    var micAudioWithLookback = CombineAudioWithLookback(_accumulatedMicAudio, _micLookbackChunks, true);
                    RouteAudioToProvider(micAudioWithLookback, true); // true = mic/agent
                }
                else
                {
                    // No voice detected in microphone audio
                }

                // Check speaker audio
                // VAD FILTERING REMOVED: Always process accumulated speaker audio regardless of VAD decision
                if (_accumulatedSpeakerAudio.Count > 0)
                {
                    // Include look-back chunks to capture speech onset
                    var speakerAudioWithLookback = CombineAudioWithLookback(_accumulatedSpeakerAudio, _speakerLookbackChunks, false);
                    RouteAudioToProvider(speakerAudioWithLookback, false); // false = speaker/customer
                }

                // Reset accumulated audio buffers
                _accumulatedMicAudio.Clear();
                _accumulatedSpeakerAudio.Clear();
                _lastVoiceDetectionCheck = DateTime.Now;
                
                LoggingService.Info($"[VAD TIMER] Audio buffers reset - next check in 10 seconds");
                LoggingService.Info($"[VAD TIMER] ==========================================");
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[VAD TIMER] Error checking accumulated audio: {ex.Message}");
            }
        }

        /// <summary>
        /// Check if an audio buffer contains voice activity with improved noise filtering
        /// </summary>
        /// <param name="audioBuffer">Audio buffer to check</param>
        /// <param name="isMic">True for microphone, false for speaker</param>
        /// <returns>True if voice is detected</returns>
        private bool CheckAudioBufferForVoice(List<byte> audioBuffer, bool isMic)
        {
            try
            {
                if (audioBuffer == null || audioBuffer.Count == 0)
                {
                    LoggingService.Debug($"[VAD TIMER] {(isMic ? "Microphone" : "Speaker")}: Empty audio buffer");
                    return false;
                }

                // Convert to byte array for analysis
                byte[] audioData = audioBuffer.ToArray();
                
                // IMPROVED NOISE FILTERING: Multiple analysis methods
                
                // 1. AMPLITUDE THRESHOLD (much higher to filter out background noise)
                double amplitudeThreshold = isMic ? Globals.VADAmplitudeThresholdMic : Globals.VADAmplitudeThresholdSpeaker;
                double rms = CalculateRMS(audioData);
                double normalizedAmplitude = rms / 32768.0;
                
                // If amplitude is too low, definitely no voice
                if (normalizedAmplitude < amplitudeThreshold)
                {
                    LoggingService.Debug($"[VAD TIMER] {(isMic ? "Microphone" : "Speaker")}: Amplitude too low - {normalizedAmplitude:F6} < {amplitudeThreshold:F6}");
                    return false;
                }
                
                // 2. TEMPORAL VARIANCE CHECK (speech varies, constant noise doesn't)
                bool hasTemporalVariance = CheckTemporalVariance(audioData);
                if (!hasTemporalVariance)
                {
                    LoggingService.Debug($"[VAD TIMER] {(isMic ? "Microphone" : "Speaker")}: No temporal variance detected - likely constant noise");
                    return false;
                }
                
                // 3. SPECTRAL ANALYSIS (human speech has specific frequency characteristics)
                bool hasSpeechSpectrum = CheckSpeechSpectrum(audioData);
                if (!hasSpeechSpectrum)
                {
                    LoggingService.Debug($"[VAD TIMER] {(isMic ? "Microphone" : "Speaker")}: No speech spectrum detected - likely non-speech audio");
                    return false;
                }
                
                // 4. SILENCE RATIO CHECK (speech has natural pauses, constant noise doesn't)
                bool hasNaturalSilence = CheckNaturalSilenceRatio(audioData);
                if (!hasNaturalSilence)
                {
                    LoggingService.Debug($"[VAD TIMER] {(isMic ? "Microphone" : "Speaker")}: No natural silence detected - likely constant noise");
                    return false;
                }
                
                // All checks passed - likely contains voice
                // Voice detected with all checks passed
                return true;
                
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[VAD TIMER] Error checking audio buffer for voice: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Check if audio has temporal variance (speech varies, constant noise doesn't)
        /// </summary>
        /// <param name="audioData">Audio data to analyze</param>
        /// <returns>True if temporal variance is detected</returns>
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
                if (meanEnergy < 1000) return false; // Too quiet overall
                
                double variance = windowEnergies.Select(e => Math.Pow(e - meanEnergy, 2)).Average();
                double stdDev = Math.Sqrt(variance);
                double coefficientOfVariation = stdDev / meanEnergy;
                
                // Speech typically has CV > 0.3, constant noise has CV < 0.1
                bool hasVariance = coefficientOfVariation > Globals.VADTemporalVarianceThreshold;
                
                return hasVariance;
            }
            catch
            {
                return true; // Default to true on error
            }
        }
        
        /// <summary>
        /// Check if audio has speech-like spectral characteristics
        /// </summary>
        /// <param name="audioData">Audio data to analyze</param>
        /// <returns>True if speech spectrum is detected</returns>
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
                
                // Simple spectral analysis: check for human speech frequency range
                // Human speech is typically 85-255 Hz for fundamental frequency
                // and 300-3400 Hz for formants
                
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
                
                // Human speech typically has ZCR between 0.1 and 0.3
                // Constant noise (like fan) typically has very low ZCR
                bool hasSpeechZCR = zeroCrossingRate > Globals.VADZeroCrossingRateMin && zeroCrossingRate < Globals.VADZeroCrossingRateMax;
                
                return hasSpeechZCR;
            }
            catch
            {
                return true; // Default to true on error
            }
        }
        
        /// <summary>
        /// Check if audio has natural silence patterns (speech has pauses, constant noise doesn't)
        /// </summary>
        /// <param name="audioData">Audio data to analyze</param>
        /// <returns>True if natural silence patterns are detected</returns>
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
                
                // Human speech typically has 20-60% silence (natural pauses)
                // Constant noise typically has very low silence ratio (< 10%)
                bool hasNaturalSilence = silenceRatio > Globals.VADSilenceRatioMin && silenceRatio < Globals.VADSilenceRatioMax;
                
                return hasNaturalSilence;
            }
            catch
            {
                return true; // Default to true on error
            }
        }
        
        /// <summary>
        /// Store audio chunk in look-back buffer for speech onset detection
        /// </summary>
        /// <param name="audioData">Audio data to store</param>
        /// <param name="lookbackQueue">Look-back queue to store in</param>
        private void StoreChunkInLookbackBuffer(byte[] audioData, ConcurrentQueue<byte[]> lookbackQueue)
        {
            try
            {
                // Check if look-back is enabled
                if (!Globals.VADEnableLookback)
                    return;
                
                // Only store chunks that are reasonably sized (not too small, not too large)
                if (audioData.Length < 1000 || audioData.Length > Globals.VADMaxLookbackChunkSize)
                    return;
                
                // Create a copy of the audio data
                var chunkCopy = new byte[audioData.Length];
                Array.Copy(audioData, chunkCopy, audioData.Length);
                
                // Add to look-back queue
                lookbackQueue.Enqueue(chunkCopy);
                
                // Maintain maximum number of look-back chunks
                while (lookbackQueue.Count > Globals.VADLookbackChunkCount)
                {
                    lookbackQueue.TryDequeue(out _);
                    // Clean up removed chunk
                }
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[VAD TIMER] Error storing chunk in look-back buffer: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Combine current audio buffer with look-back chunks to capture speech onset
        /// </summary>
        /// <param name="currentBuffer">Current accumulated audio buffer</param>
        /// <param name="lookbackQueue">Queue of previous audio chunks</param>
        /// <param name="isMic">True for microphone, false for speaker</param>
        /// <returns>Combined audio data with look-back chunks</returns>
        private byte[] CombineAudioWithLookback(List<byte> currentBuffer, ConcurrentQueue<byte[]> lookbackQueue, bool isMic)
        {
            try
            {
                // Check if look-back is enabled
                if (!Globals.VADEnableLookback || lookbackQueue.Count == 0)
                {
                    // Look-back disabled or no chunks available, return current buffer only
                    return currentBuffer.ToArray();
                }
                
                // Calculate total size needed
                int totalSize = currentBuffer.Count;
                var lookbackChunks = lookbackQueue.ToArray();
                
                foreach (var chunk in lookbackChunks)
                {
                    totalSize += chunk.Length;
                }
                
                // Create combined audio buffer
                var combinedAudio = new byte[totalSize];
                int currentIndex = 0;
                
                // Add look-back chunks first (oldest first)
                foreach (var chunk in lookbackChunks)
                {
                    Array.Copy(chunk, 0, combinedAudio, currentIndex, chunk.Length);
                    currentIndex += chunk.Length;
                }
                
                // Add current buffer last
                Array.Copy(currentBuffer.ToArray(), 0, combinedAudio, currentIndex, currentBuffer.Count);
                
                // Combined audio with look-back chunks
                
                return combinedAudio;
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[VAD TIMER] Error combining audio with look-back: {ex.Message}");
                // Fallback to current buffer only
                return currentBuffer.ToArray();
            }
        }

        /// <summary>
        /// Stop the 10-second voice detection timer
        /// </summary>
        private void StopVoiceDetectionTimer()
        {
            try
            {
                if (_voiceDetectionTimer != null)
                {
                    _voiceDetectionTimer.Stop();
                    _voiceDetectionTimer.Dispose();
                    _voiceDetectionTimer = null;
                    
                    // Clear accumulated audio buffers
                    _accumulatedMicAudio.Clear();
                    _accumulatedSpeakerAudio.Clear();
                    
                    LoggingService.Info("[VAD TIMER] 10-second voice detection timer stopped and cleaned up");
                }
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[VAD TIMER] Error stopping voice detection timer: {ex.Message}");
            }
        }

        public void StopRecording()
        {
            try
            {
                if (!_IsRecording)
                {
                    LoggingService.Info("[AudioRecorder] Not recording");
                    return;
                }

                LoggingService.Info("[AudioRecorder] Stopping recording...");
                
                // Stop the 10-second voice detection timer
                StopVoiceDetectionTimer();
                
                _IsRecording = false;
                
                // Stop the existing audio capture system
                if (_MicCapture != null)
                {
                    _MicCapture.StopRecording();
                }
                if (_LoopbackCapture != null)
                {
                    _LoopbackCapture.StopRecording();
                }
                
                // Flush any remaining audio chunks from the AudioChunkingService
                if (_audioChunkingService != null)
                {
                    _audioChunkingService.FlushRemainingAudio();
                    LoggingService.Info("[AudioRecorder] AudioChunkingService flushed remaining audio");
                }
                
                LoggingService.Info("[AudioRecorder] Recording stopped successfully");
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[AudioRecorder] Error stopping recording: {ex.Message}");
            }
        }

        /// <summary>
        /// Clear all audio buffers and queues to prevent old audio from being processed
        /// </summary>
        public void ClearBuffers()
        {
            try
            {
                LoggingService.Info("[AudioRecorder] 🗑️ Clearing all audio buffers and queues...");
                
                // Clear main audio queues
                int micQueueCount = 0;
                int speakerQueueCount = 0;
                
                if (_MicQueue != null)
                {
                    while (_MicQueue.TryDequeue(out _)) micQueueCount++;
                }
                
                if (_LoopBackFloatQueue != null)
                {
                    while (_LoopBackFloatQueue.TryDequeue(out _)) speakerQueueCount++;
                }
                
                // Clear look-back buffers
                // Clear lookback buffers - ConcurrentQueues are thread-safe
                while (_micLookbackBuffer.TryDequeue(out _)) { }
                while (_speakerLookbackBuffer.TryDequeue(out _)) { }
                
                // Clear VAD filtered audio queues - ConcurrentQueues are thread-safe
                while (_micVadFilteredAudio.TryDequeue(out _)) { }
                while (_speakerVadFilteredAudio.TryDequeue(out _)) { }
                
                // Clear VAD decision queues - ConcurrentQueues are thread-safe
                while (_micVadDecisions.TryDequeue(out _)) { }
                while (_speakerVadDecisions.TryDequeue(out _)) { }
                
                // Clear amplitude history - ConcurrentQueues are thread-safe
                while (_micAmplitudeHistory.TryDequeue(out _)) { }
                while (_speakerAmplitudeHistory.TryDequeue(out _)) { }
                
                // Clear accumulated audio buffers
                _accumulatedMicAudio.Clear();
                _accumulatedSpeakerAudio.Clear();
                
                // Clear look-back chunks
                while (_micLookbackChunks.TryDequeue(out _)) { }
                while (_speakerLookbackChunks.TryDequeue(out _)) { }
                
                // Clear spectrum data
                _Spectrumdata?.Clear();
                _MicSpectrumdata?.Clear();
                _SpeakerSpectrumdata?.Clear();
                
                // Clear audio chunking service buffers
                _audioChunkingService?.Clear();
                
                LoggingService.Info($"[AudioRecorder] Cleared {micQueueCount} mic chunks and {speakerQueueCount} speaker chunks");
                LoggingService.Info("[AudioRecorder] Cleared all look-back buffers, VAD queues, and spectrum data");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[AudioRecorder] Error clearing buffers: {ex.Message}");
            }
        }

        /// <summary>
        /// Event handler for when audio chunks are ready from the AudioChunkingService
        /// </summary>
        private void OnAudioChunkReady(object sender, AudioChunk chunk)
        {
            try
            {
                // VAD FILTERING REMOVED: Process all audio chunks regardless of voice activity
                // This ensures speaker audio is always processed even when no voice is detected

                // Check minimum duration (50ms = 1600 bytes at 16kHz 16-bit mono)
                var minBytes = 1600; // 50ms * 16kHz * 2 bytes per sample
                if (chunk.AudioData.Length < minBytes)
                {
                    return;
                }

                // Only log first chunk and every 100th to reduce noise
                _chunkDebugCount++;
                if (_chunkDebugCount == 1 || _chunkDebugCount % 100 == 0)
                {
                    LoggingService.Debug($"[AudioRecorder] Chunk #{_chunkDebugCount} - Source: {chunk.Source}, Size: {chunk.AudioData.Length} bytes, Provider: {_currentSpeechToTextProvider}");
                }

                // Send the audio chunk to the selected speech-to-text provider based on source
                bool isMic = chunk.Source == "Mic";
                RouteAudioToProvider(chunk.AudioData, isMic);
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[AudioRecorder] Error processing audio chunk: {ex.Message}");
            }
        }

        /// <summary>
        /// Set the microphone device for audio recording
        /// </summary>
        /// <param name="deviceIndex">Device index from WaveInEvent</param>
        /// <param name="deviceName">Device name for logging</param>
        public void SetMicrophoneDevice(int deviceIndex, string deviceName)
        {
            try
            {
                LoggingService.Info($"[AudioRecorder] Setting microphone device: {deviceName} (Index: {deviceIndex})");
                
                // Store the device index for use when initializing audio capture
                _selectedMicDeviceIndex = deviceIndex;
                _selectedMicDeviceName = deviceName;
                
                // If currently recording, restart with new device
                if (_IsRecording)
                {
                    LoggingService.Info("[AudioRecorder] Restarting recording with new microphone device");
                    StopRecording();
                    StartRecording();
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[AudioRecorder] Error setting microphone device: {ex.Message}");
            }
        }

        /// <summary>
        /// Set the speaker device for audio recording
        /// </summary>
        /// <param name="deviceIndex">Device index from MMDeviceEnumerator</param>
        /// <param name="deviceName">Device name for logging</param>
        public void SetSpeakerDevice(int deviceIndex, string deviceName)
        {
            try
            {
                LoggingService.Info($"[AudioRecorder] Setting speaker device: {deviceName} (Index: {deviceIndex})");
                
                // Store the device index for use when initializing audio capture
                _selectedSpeakerDeviceIndex = deviceIndex;
                _selectedSpeakerDeviceName = deviceName;
                
                // If currently recording, restart with new device
                if (_IsRecording)
                {
                    LoggingService.Info("[AudioRecorder] Restarting recording with new speaker device");
                    StopRecording();
                    StartRecording();
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[AudioRecorder] Error setting speaker device: {ex.Message}");
            }
        }

        /// <summary>
        /// Sets DevMode System Audio Only flag - when enabled, microphone capture is disabled and only system audio is captured
        /// </summary>
        public void SetDevModeSystemAudioOnly(bool enabled)
        {
            _devModeSystemAudioOnly = enabled;
            LoggingService.Info($"[AudioRecorder] DevMode System Audio Only set to: {enabled}");
            
            // If currently recording and DevMode changed, restart recording
            if (_IsRecording)
            {
                LoggingService.Info("[AudioRecorder] Restarting recording with new DevMode setting");
                StopRecording();
                StartRecording();
            }
        }

        /// <summary>
        /// Saves the current device settings to appsettings.json
        /// </summary>
        private void SaveDeviceSettingsToAppSettings()
        {
            try
            {
                string configFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
                
                if (File.Exists(configFilePath))
                {
                    var json = File.ReadAllText(configFilePath);
                    var config = Newtonsoft.Json.Linq.JObject.Parse(json);
                    
                    // Update AudioDevices section
                    if (config["AudioDevices"] == null)
                        config["AudioDevices"] = new Newtonsoft.Json.Linq.JObject();
                    
                    var audioDevicesSection = (Newtonsoft.Json.Linq.JObject)config["AudioDevices"];
                    audioDevicesSection["SelectedMicrophoneDevice"] = _selectedMicDeviceName ?? "";
                    audioDevicesSection["SelectedSpeakerDevice"] = _selectedSpeakerDeviceName ?? "";
                    audioDevicesSection["MicrophoneDeviceIndex"] = _selectedMicDeviceIndex;
                    audioDevicesSection["SpeakerDeviceIndex"] = _selectedSpeakerDeviceIndex;
                    
                    // Save the updated configuration
                    var updatedJson = config.ToString(Newtonsoft.Json.Formatting.Indented);
                    File.WriteAllText(configFilePath, updatedJson);
                    
                    LoggingService.Info($"[AudioRecorder] Device settings saved to appsettings.json - Mic: {_selectedMicDeviceName} (Index: {_selectedMicDeviceIndex}), Speaker: {_selectedSpeakerDeviceName} (Index: {_selectedSpeakerDeviceIndex})");
                }
                else
                {
                    LoggingService.Warn("[AudioRecorder] appsettings.json file not found, cannot save device settings");
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[AudioRecorder] Error saving device settings to appsettings.json: {ex.Message}");
            }
        }

    }
}
