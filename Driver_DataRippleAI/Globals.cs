using Microsoft.Extensions.Configuration;

namespace DataRippleAIDesktop
{
    public static class Globals
    {
        // Application mode: "Production" or "Demo" — set by DevMode checkbox on login page
        public static string AppMode { get; set; } = "Production";
        public static bool IsProductionMode => string.Equals(AppMode, "Production", System.StringComparison.OrdinalIgnoreCase);
        public static bool IsDemoMode => !IsProductionMode;

        public static float MinAudioChunkSizeInSeconds { get; set; } = 0.2f; // Minimum chunk size
        public static float MaxAudioChunkSizeInSeconds { get; set; } = 1.0f; // Maximum chunk size
    
        
        // VAD (Voice Activity Detection) configuration - Default values from removed UI settings
    
        public static int VADSilenceThresholdSeconds { get; set; } = 0; // Seconds of silence to end a chunk - 0 means immediate processing
        public static int VADVoiceThresholdSeconds { get; set; } = 0; // Seconds of voice to start a chunk - 0 means immediate
        
        // Advanced noise filtering configuration - Default values from removed UI settings
        public static double VADAmplitudeThresholdMic { get; set; } = 0.015; // Microphone amplitude threshold (was 0.015 in UI)
        public static double VADAmplitudeThresholdSpeaker { get; set; } = 0.001; // Speaker amplitude threshold (was 0.001 in UI)
        public static double VADTemporalVarianceThreshold { get; set; } = 0.10; // Temporal variance threshold for speech detection (was 0.10 in UI)
        public static double VADZeroCrossingRateMin { get; set; } = 0.02; // Minimum zero-crossing rate for speech (was 0.02 in UI)
        public static double VADZeroCrossingRateMax { get; set; } = 0.30; // Maximum zero-crossing rate for speech (was 0.30 in UI)
        public static double VADSilenceRatioMin { get; set; } = 0.05; // Minimum silence ratio for natural speech (was 0.05 in UI)
        public static double VADSilenceRatioMax { get; set; } = 0.55; // Maximum silence ratio for natural speech (was 0.55 in UI)
        
        // Speech onset detection (look-back) configuration - Default values from removed UI settings
        public static int VADLookbackChunkCount { get; set; } = 2; // Number of previous chunks to include (was 2 in UI)
        public static int VADMaxLookbackChunkSize { get; set; } = 32000; // Maximum size per look-back chunk (was 32000 in UI)
        public static bool VADEnableLookback { get; set; } = true; // Enable/disable look-back functionality (was true in UI)
    
        
        // Performance optimization - disable excessive logging
        public static bool EnableVerboseLogging { get; set; } = false; // Disable verbose logging to reduce log noise
        
        // Audio format diagnostic settings
        public static bool EnableAudioFormatDiagnostics { get; set; } = true; // Enable audio format diagnostics
        public static bool LogAudioFormatChanges { get; set; } = true; // Log when audio formats change
        public static bool VerboseAudioLogging { get; set; } = false; // Enable verbose audio logging
        
        // Configuration and other settings
        public static IConfiguration ConfigurationInfo { get; set; } = null;
        
        // Backend authentication/session state
        public static string BackendAccessToken { get; set; } = string.Empty;
        public static string BackendRefreshToken { get; set; } = string.Empty;
        public static string FrontendSocketUrl { get; set; } = string.Empty; // wss url with ?role=Driver appended
        public static string UserId { get; set; } = string.Empty; // User ID extracted from JWT sub claim

        // Enable/Disable call logging (writes raw messages to logs)
        public static bool EnableCallLogging { get; set; } = false; // Disabled by default to reduce disk I/O

        // Network statistics service (initialized in MainWindow)
        public static Services.NetworkStatsService NetworkStatsService { get; set; } = null;

    }
}
