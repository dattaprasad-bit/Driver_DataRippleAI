using System;
using System.IO;
using System.Threading;

namespace DataRippleAIDesktop.Services
{
    /// <summary>
    /// Service for logging call transcripts and responses to organized folder structure
    /// Structure: Call Logs/{DateTime_ConversationID-CallID}/
    ///   - MicTranscript.txt
    ///   - SpeakerTranscript.txt
    ///   - AgentResponses.txt
    /// </summary>
    public class CallLoggerService : IDisposable
    {
        private string _callLogFolder = string.Empty;
        private StreamWriter? _micTranscriptWriter;
        private StreamWriter? _speakerTranscriptWriter;
        private StreamWriter? _agentResponseWriter;
        private readonly object _micLock = new object();
        private readonly object _speakerLock = new object();
        private readonly object _agentResponseLock = new object();
        private bool _isLoggingActive = false;

        /// <summary>
        /// Starts a new call logging session
        /// </summary>
        /// <param name="conversationId">Conversation ID</param>
        /// <param name="callId">Call ID</param>
        public void StartCallLogging(string conversationId, string callId)
        {
            try
            {
                // Stop any existing logging session
                StopCallLogging();

                // Create base Call Logs directory in project root
                string projectRoot = AppDomain.CurrentDomain.BaseDirectory;
                string callLogsRoot = Path.Combine(projectRoot, "Call Logs");
                
                if (!Directory.Exists(callLogsRoot))
                {
                    Directory.CreateDirectory(callLogsRoot);
                    LoggingService.Info($"[CallLogger] Created Call Logs directory: {callLogsRoot}");
                }

                // Create folder name with readable DateTime and IDs
                // Format: yyyyMMdd-HHmmss_ConversationID
                // Note: callId contains timestamp in format yyyyMMdd-HHmmss
                string folderName = $"{callId}_{conversationId}";
                _callLogFolder = Path.Combine(callLogsRoot, folderName);

                // Create the call-specific folder
                Directory.CreateDirectory(_callLogFolder);
                LoggingService.Info($"[CallLogger] Created call log folder: {_callLogFolder}");

                // Initialize StreamWriters for each file
                string micTranscriptPath = Path.Combine(_callLogFolder, "MicTranscript.txt");
                string speakerTranscriptPath = Path.Combine(_callLogFolder, "SpeakerTranscript.txt");
                string agentResponsePath = Path.Combine(_callLogFolder, "AgentResponses.txt");

                _micTranscriptWriter = new StreamWriter(micTranscriptPath, append: true) { AutoFlush = true };
                _speakerTranscriptWriter = new StreamWriter(speakerTranscriptPath, append: true) { AutoFlush = true };
                _agentResponseWriter = new StreamWriter(agentResponsePath, append: true) { AutoFlush = true };

                // Write headers
                _micTranscriptWriter.WriteLine("=".PadRight(80, '='));
                _micTranscriptWriter.WriteLine($"MIC/AGENT TRANSCRIPT - Call Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                _micTranscriptWriter.WriteLine($"Conversation ID: {conversationId}");
                _micTranscriptWriter.WriteLine($"Call ID: {callId}");
                _micTranscriptWriter.WriteLine("=".PadRight(80, '='));
                _micTranscriptWriter.WriteLine();

                _speakerTranscriptWriter.WriteLine("=".PadRight(80, '='));
                _speakerTranscriptWriter.WriteLine($"SPEAKER/CUSTOMER TRANSCRIPT - Call Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                _speakerTranscriptWriter.WriteLine($"Conversation ID: {conversationId}");
                _speakerTranscriptWriter.WriteLine($"Call ID: {callId}");
                _speakerTranscriptWriter.WriteLine("=".PadRight(80, '='));
                _speakerTranscriptWriter.WriteLine();

                _agentResponseWriter.WriteLine("=".PadRight(80, '='));
                _agentResponseWriter.WriteLine($"AGENT RESPONSES - Call Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                _agentResponseWriter.WriteLine($"Conversation ID: {conversationId}");
                _agentResponseWriter.WriteLine($"Call ID: {callId}");
                _agentResponseWriter.WriteLine("=".PadRight(80, '='));
                _agentResponseWriter.WriteLine();

                _isLoggingActive = true;
                LoggingService.Info($"[CallLogger] ✅ Call logging started successfully");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[CallLogger] ❌ Failed to start call logging: {ex.Message}");
                // Clean up on failure
                CloseWriters();
            }
        }

        /// <summary>
        /// Logs a microphone/agent transcript with timestamp
        /// </summary>
        /// <param name="text">Transcript text</param>
        /// <param name="isFinal">Whether this is a final transcript or partial</param>
        /// <param name="rawJson">Optional raw JSON response from STT provider</param>
        public void LogMicTranscript(string text, bool isFinal = true, string? rawJson = null)
        {
            if (!_isLoggingActive || _micTranscriptWriter == null || string.IsNullOrWhiteSpace(text))
                return;

            try
            {
                lock (_micLock)
                {
                    string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    string type = isFinal ? "FINAL" : "PARTIAL";
                    
                    _micTranscriptWriter.WriteLine($"[{timestamp}] [{type}]");
                    _micTranscriptWriter.WriteLine(text);
                    
                    if (!string.IsNullOrEmpty(rawJson))
                    {
                        _micTranscriptWriter.WriteLine();
                        _micTranscriptWriter.WriteLine("Raw STT JSON:");
                        _micTranscriptWriter.WriteLine(rawJson);
                    }
                    
                    _micTranscriptWriter.WriteLine();
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[CallLogger] Error logging mic transcript: {ex.Message}");
            }
        }

        /// <summary>
        /// Logs a speaker/customer transcript with timestamp
        /// </summary>
        /// <param name="text">Transcript text</param>
        /// <param name="isFinal">Whether this is a final transcript or partial</param>
        /// <param name="rawJson">Optional raw JSON response from STT provider</param>
        public void LogSpeakerTranscript(string text, bool isFinal = true, string? rawJson = null)
        {
            if (!_isLoggingActive || _speakerTranscriptWriter == null || string.IsNullOrWhiteSpace(text))
                return;

            try
            {
                lock (_speakerLock)
                {
                    string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    string type = isFinal ? "FINAL" : "PARTIAL";
                    
                    _speakerTranscriptWriter.WriteLine($"[{timestamp}] [{type}]");
                    _speakerTranscriptWriter.WriteLine(text);
                    
                    if (!string.IsNullOrEmpty(rawJson))
                    {
                        _speakerTranscriptWriter.WriteLine();
                        _speakerTranscriptWriter.WriteLine("Raw STT JSON:");
                        _speakerTranscriptWriter.WriteLine(rawJson);
                    }
                    
                    _speakerTranscriptWriter.WriteLine();
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[CallLogger] Error logging speaker transcript: {ex.Message}");
            }
        }

        /// <summary>
        /// Logs an AI agent response with timestamp and optional XML
        /// </summary>
        /// <param name="response">Response text</param>
        /// <param name="xmlContent">Optional XML content from the response</param>
        /// <param name="isFromSpeaker">Whether this response is for speaker or mic</param>
        /// <param name="messageId">Optional message ID</param>
        public void LogAgentXmlResponse(string response, string? xmlContent = null, bool isFromSpeaker = false, string? messageId = null)
        {
            if (!_isLoggingActive || _agentResponseWriter == null || string.IsNullOrWhiteSpace(response))
                return;

            try
            {
                lock (_agentResponseLock)
                {
                    string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    string source = isFromSpeaker ? "SPEAKER" : "MIC";
                    
                    _agentResponseWriter.WriteLine($"[{timestamp}] [{source}]");
                    
                    if (!string.IsNullOrEmpty(messageId))
                    {
                        _agentResponseWriter.WriteLine($"Message ID: {messageId}");
                    }
                    
                    _agentResponseWriter.WriteLine("Response Text:");
                    _agentResponseWriter.WriteLine(response);
                    
                    if (!string.IsNullOrEmpty(xmlContent))
                    {
                        _agentResponseWriter.WriteLine();
                        _agentResponseWriter.WriteLine("XML Content:");
                        _agentResponseWriter.WriteLine(xmlContent);
                    }
                    
                    _agentResponseWriter.WriteLine();
                    _agentResponseWriter.WriteLine("-".PadRight(80, '-'));
                    _agentResponseWriter.WriteLine();
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[CallLogger] Error logging agent XML response: {ex.Message}");
            }
        }

        /// <summary>
        /// Logs an agent response with timestamp and optional raw JSON.
        /// Provider-agnostic wrapper that delegates to the agent response writer.
        /// </summary>
        /// <param name="response">Response text</param>
        /// <param name="rawJson">Optional raw JSON from the backend</param>
        /// <param name="isDelta">Whether this is a streaming delta chunk</param>
        /// <param name="isFinal">Whether this is the final response</param>
        /// <param name="conversationId">Optional conversation ID</param>
        public void LogAgentResponse(string response, string? rawJson = null, bool isDelta = false, bool isFinal = false, string? conversationId = null)
        {
            if (!_isLoggingActive || _agentResponseWriter == null || string.IsNullOrWhiteSpace(response))
                return;

            try
            {
                lock (_agentResponseLock)
                {
                    string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    string chunkType = isDelta ? "DELTA" : (isFinal ? "FINAL" : "RESPONSE");

                    _agentResponseWriter.WriteLine($"[{timestamp}] [AGENT] [{chunkType}]");

                    if (!string.IsNullOrEmpty(conversationId))
                    {
                        _agentResponseWriter.WriteLine($"Conversation ID: {conversationId}");
                    }

                    _agentResponseWriter.WriteLine("Response Text:");
                    _agentResponseWriter.WriteLine(response);

                    if (!string.IsNullOrEmpty(rawJson))
                    {
                        _agentResponseWriter.WriteLine();
                        _agentResponseWriter.WriteLine("Raw JSON:");
                        _agentResponseWriter.WriteLine(rawJson);
                    }

                    _agentResponseWriter.WriteLine();
                    _agentResponseWriter.WriteLine("-".PadRight(80, '-'));
                    _agentResponseWriter.WriteLine();
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[CallLogger] Error logging agent response: {ex.Message}");
            }
        }

        /// <summary>
        /// Logs a raw WebSocket message from AI agent (for debugging and full traceability)
        /// </summary>
        /// <param name="rawMessage">Raw WebSocket message JSON</param>
        public void LogAgentRawMessage(string rawMessage)
        {
            if (!_isLoggingActive || _agentResponseWriter == null || string.IsNullOrWhiteSpace(rawMessage))
                return;

            try
            {
                lock (_agentResponseLock)
                {
                    string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    _agentResponseWriter.WriteLine($"[{timestamp}] [RAW WebSocket Message]");
                    _agentResponseWriter.WriteLine(rawMessage);
                    _agentResponseWriter.WriteLine();
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[CallLogger] Error logging agent raw message: {ex.Message}");
            }
        }

        /// <summary>
        /// Stops the current call logging session
        /// </summary>
        public void StopCallLogging()
        {
            if (!_isLoggingActive)
                return;

            try
            {
                // Write footers
                if (_micTranscriptWriter != null)
                {
                    lock (_micLock)
                    {
                        _micTranscriptWriter.WriteLine();
                        _micTranscriptWriter.WriteLine("=".PadRight(80, '='));
                        _micTranscriptWriter.WriteLine($"Call Ended: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                        _micTranscriptWriter.WriteLine("=".PadRight(80, '='));
                    }
                }

                if (_speakerTranscriptWriter != null)
                {
                    lock (_speakerLock)
                    {
                        _speakerTranscriptWriter.WriteLine();
                        _speakerTranscriptWriter.WriteLine("=".PadRight(80, '='));
                        _speakerTranscriptWriter.WriteLine($"Call Ended: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                        _speakerTranscriptWriter.WriteLine("=".PadRight(80, '='));
                    }
                }

                if (_agentResponseWriter != null)
                {
                    lock (_agentResponseLock)
                    {
                        _agentResponseWriter.WriteLine();
                        _agentResponseWriter.WriteLine("=".PadRight(80, '='));
                        _agentResponseWriter.WriteLine($"Call Ended: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                        _agentResponseWriter.WriteLine("=".PadRight(80, '='));
                    }
                }

                CloseWriters();
                _isLoggingActive = false;

                LoggingService.Info($"[CallLogger] ✅ Call logging stopped. Logs saved to: {_callLogFolder}");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[CallLogger] Error stopping call logging: {ex.Message}");
            }
        }

        /// <summary>
        /// Closes all StreamWriters
        /// </summary>
        private void CloseWriters()
        {
            try
            {
                _micTranscriptWriter?.Flush();
                _micTranscriptWriter?.Close();
                _micTranscriptWriter?.Dispose();
                _micTranscriptWriter = null;

                _speakerTranscriptWriter?.Flush();
                _speakerTranscriptWriter?.Close();
                _speakerTranscriptWriter?.Dispose();
                _speakerTranscriptWriter = null;

                _agentResponseWriter?.Flush();
                _agentResponseWriter?.Close();
                _agentResponseWriter?.Dispose();
                _agentResponseWriter = null;
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[CallLogger] Error closing writers: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets the current call log folder path
        /// </summary>
        public string GetCurrentLogFolder()
        {
            return _callLogFolder;
        }

        /// <summary>
        /// Checks if logging is currently active
        /// </summary>
        public bool IsLoggingActive()
        {
            return _isLoggingActive;
        }

        public void Dispose()
        {
            StopCallLogging();
        }
    }
}

