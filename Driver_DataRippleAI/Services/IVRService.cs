using System;
using System.Media;
using System.Threading;
using System.Threading.Tasks;
using DataRippleAIDesktop.Services;
using DataRippleAIDesktop.Models;

namespace DataRippleAIDesktop.Services
{
    public class IVRService
    {
        private bool _ivrAvailable = false;
        private string _defaultPhoneNumber = "+919876543210"; // Default E164 format
        private string _lastKnownPhoneNumber = "+919876543210";
        
        // Ringing state management
        private bool _isRinging = false;
        private string _currentCallId;
        private CancellationTokenSource _ringingCancellationTokenSource;
        private SoundPlayer _ringingTonePlayer;
        
        // Customer information for current call
        private string _currentCustomerName = "John Doe";
        private string _currentCustomerId = "CUST-1024";
        private string _currentCustomerEmail = "john.doe@example.com";
        
        // Public getters for customer info (used by BackendAudioStreamingService)
        public string CurrentCustomerName => _currentCustomerName;
        public string CurrentCustomerId => _currentCustomerId;
        public string CurrentCustomerEmail => _currentCustomerEmail;
        public string CurrentCustomerPhone => _lastKnownPhoneNumber;

        // Events
        public event EventHandler<IncomingCallRingingEvent> IncomingCallRinging;
        public event EventHandler<string> CallAnswered;
        public event EventHandler<string> CallRejected;

        /// <summary>
        /// Get phone number from IVR with fallback to default
        /// </summary>
        public async Task<string> GetCallerPhoneNumberAsync()
        {
            try
            {
                // Try to get phone number from IVR
                var phoneNumber = await TryGetPhoneFromIVRAsync();
                
                if (!string.IsNullOrEmpty(phoneNumber))
                {
                    _lastKnownPhoneNumber = phoneNumber;
                    LoggingService.Debug($"[IVR] Retrieved phone number: {phoneNumber}");
                    return phoneNumber;
                }
                else
                {
                    LoggingService.Debug($"[IVR] No phone number available, using default: {_defaultPhoneNumber}");
                    return _defaultPhoneNumber;
                }
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[IVR] Error getting phone number: {ex.Message}");
                return _defaultPhoneNumber;
            }
        }

        /// <summary>
        /// Try to get phone number from IVR system
        /// </summary>
        private async Task<string> TryGetPhoneFromIVRAsync()
        {
            try
            {
                // Check if IVR is available
                if (!await CheckIVRAvailabilityAsync())
                {
                    LoggingService.Debug("[IVR] IVR system not available");
                    return null;
                }

                // TODO: Implement actual IVR integration based on client's system
                // This is where you'd integrate with the specific IVR software
                // Examples:
                // - Read from shared memory
                // - Call IVR API endpoint
                // - Read from file system
                // - Listen to IVR events
                
                // For now, simulate IVR lookup
                return await SimulateIVRLookupAsync();
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[IVR] Error accessing IVR system: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Check if IVR system is available
        /// </summary>
        private async Task<bool> CheckIVRAvailabilityAsync()
        {
            try
            {
                // TODO: Implement actual IVR availability check
                // This depends on the client's IVR system:
                // - Check if IVR service is running
                // - Test IVR API endpoint
                // - Check if IVR shared resources are accessible
                
                // For now, simulate availability check
                await Task.Delay(100); // Simulate check time
                
                // Return false for now since no real IVR is connected
                _ivrAvailable = false;
                return _ivrAvailable;
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[IVR] IVR availability check failed: {ex.Message}");
                _ivrAvailable = false;
                return false;
            }
        }

        /// <summary>
        /// Simulate IVR phone number lookup (replace with actual implementation)
        /// </summary>
        private async Task<string> SimulateIVRLookupAsync()
        {
            try
            {
                // Simulate IVR processing time
                await Task.Delay(200);

                // TODO: Replace with actual IVR integration
                // Examples of real implementations:
                
                // Option 1: Read from IVR API
                // var response = await _ivrApiClient.GetCurrentCallInfoAsync();
                // return response?.CallerPhoneNumber;

                // Option 2: Read from shared memory/file
                // var callInfo = ReadIVRSharedMemory();
                // return callInfo?.PhoneNumber;

                // Option 3: Parse IVR event logs
                // var latestEvent = ParseIVREventLog();
                // return latestEvent?.CallerID;

                // For now, return null to indicate no IVR data available
                return null;
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[IVR] Simulated IVR lookup failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Set custom default phone number
        /// </summary>
        public void SetDefaultPhoneNumber(string phoneNumber)
        {
            if (!string.IsNullOrEmpty(phoneNumber))
            {
                _defaultPhoneNumber = phoneNumber;
                LoggingService.Info($"[IVR] Default phone number set to: {phoneNumber}");
            }
        }

        /// <summary>
        /// Get the current default phone number
        /// </summary>
        public string GetDefaultPhoneNumber()
        {
            return _defaultPhoneNumber;
        }

        /// <summary>
        /// Check if IVR system is currently available
        /// </summary>
        public bool IsIVRAvailable()
        {
            return _ivrAvailable;
        }

        /// <summary>
        /// Get the last known phone number (useful for repeat calls)
        /// </summary>
        public string GetLastKnownPhoneNumber()
        {
            return _lastKnownPhoneNumber;
        }

        /// <summary>
        /// Manual phone number override (for testing or manual entry)
        /// </summary>
        public void SetManualPhoneNumber(string phoneNumber)
        {
            if (!string.IsNullOrEmpty(phoneNumber))
            {
                _lastKnownPhoneNumber = phoneNumber;
                LoggingService.Info($"[IVR] Manual phone number set: {phoneNumber}");
            }
        }

        /// <summary>
        /// Reset to default phone number
        /// </summary>
        public void ResetToDefault()
        {
            _lastKnownPhoneNumber = _defaultPhoneNumber;
            LoggingService.Info($"[IVR] Reset to default phone number: {_defaultPhoneNumber}");
        }

        /// <summary>
        /// Simulate an incoming call with ringing
        /// </summary>
        public async Task<string> SimulateIncomingCallAsync()
        {
            try
            {
                if (_isRinging)
                {
                    LoggingService.Warn("[IVR] Call already ringing, ignoring new incoming call");
                    return null;
                }

                // Generate unique call ID
                _currentCallId = $"call_{Guid.NewGuid().ToString("N").Substring(0, 8)}";
                _isRinging = true;

                LoggingService.Info($"[IVR] 📞 Incoming call - Call ID: {_currentCallId}");

                // Create incoming call event
                var incomingCallEvent = new IncomingCallRingingEvent
                {
                    CallId = _currentCallId,
                    Customer = new CustomerInfo
                    {
                        PhoneE164 = _lastKnownPhoneNumber,
                        Name = _currentCustomerName,
                        CustomerId = _currentCustomerId,
                        Email = _currentCustomerEmail
                    },
                    ReceivedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
                };

                // Fire incoming call event
                IncomingCallRinging?.Invoke(this, incomingCallEvent);

                // Start ringing tone
                StartRingingTone();

                LoggingService.Info($"[IVR] 🔔 Call ringing - waiting for answer/reject");

                return _currentCallId;
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[IVR] Error simulating incoming call: {ex.Message}");
                _isRinging = false;
                return null;
            }
        }

        /// <summary>
        /// Answer the ringing call
        /// </summary>
        public async Task<bool> AnswerCallAsync()
        {
            try
            {
                if (!_isRinging)
                {
                    LoggingService.Warn("[IVR] No call is ringing to answer");
                    return false;
                }

                LoggingService.Info($"[IVR] ✅ Call answered - Call ID: {_currentCallId}");

                // Stop ringing
                StopRingingTone();
                _isRinging = false;

                // Fire call answered event
                CallAnswered?.Invoke(this, _currentCallId);

                return true;
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[IVR] Error answering call: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Reject the ringing call
        /// </summary>
        public async Task<bool> RejectCallAsync()
        {
            try
            {
                if (!_isRinging)
                {
                    LoggingService.Warn("[IVR] No call is ringing to reject");
                    return false;
                }

                LoggingService.Info($"[IVR] ❌ Call rejected - Call ID: {_currentCallId}");

                // Stop ringing
                StopRingingTone();
                _isRinging = false;

                // Fire call rejected event
                CallRejected?.Invoke(this, _currentCallId);

                // Clear current call
                _currentCallId = null;

                return true;
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[IVR] Error rejecting call: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Start playing ringing tone
        /// </summary>
        private void StartRingingTone()
        {
            try
            {
                // Cancel any existing ringing
                _ringingCancellationTokenSource?.Cancel();
                _ringingCancellationTokenSource = new CancellationTokenSource();

                // Start ringing tone in background
                Task.Run(async () =>
                {
                    try
                    {
                        while (!_ringingCancellationTokenSource.Token.IsCancellationRequested && _isRinging)
                        {
                            // Play system beep as ringing tone
                            // You can replace this with a proper WAV file if needed
                            Console.Beep(800, 300); // 800Hz for 300ms
                            await Task.Delay(500, _ringingCancellationTokenSource.Token); // Pause
                            Console.Beep(800, 300);
                            await Task.Delay(2000, _ringingCancellationTokenSource.Token); // 2 second between rings

                            LoggingService.Debug("[IVR] 🔔 Ring...");
                        }
                    }
                    catch (TaskCanceledException)
                    {
                        // Ringing cancelled - this is expected
                    }
                    catch (Exception ex)
                    {
                        LoggingService.Error($"[IVR] Error playing ringing tone: {ex.Message}");
                    }
                }, _ringingCancellationTokenSource.Token);

                LoggingService.Info("[IVR] 🔔 Ringing tone started");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[IVR] Error starting ringing tone: {ex.Message}");
            }
        }

        /// <summary>
        /// Stop playing ringing tone
        /// </summary>
        private void StopRingingTone()
        {
            try
            {
                _ringingCancellationTokenSource?.Cancel();
                _ringingTonePlayer?.Stop();
                _ringingTonePlayer?.Dispose();
                _ringingTonePlayer = null;

                LoggingService.Info("[IVR] 🔕 Ringing tone stopped");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[IVR] Error stopping ringing tone: {ex.Message}");
            }
        }

        /// <summary>
        /// Publicly stop ringing tone and clear ringing state without firing call events.
        /// Used when the backend accepts/rejects/ends the call and the local IVR ringing
        /// needs to be silenced without triggering CallAnswered or CallRejected events.
        /// </summary>
        public void StopRinging()
        {
            try
            {
                if (_isRinging)
                {
                    LoggingService.Info("[IVR] StopRinging called - stopping ringing tone from external trigger");
                    StopRingingTone();
                    _isRinging = false;
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[IVR] Error in StopRinging: {ex.Message}");
            }
        }

        /// <summary>
        /// Check if currently ringing
        /// </summary>
        public bool IsRinging()
        {
            return _isRinging;
        }

        /// <summary>
        /// Get current call ID
        /// </summary>
        public string GetCurrentCallId()
        {
            return _currentCallId;
        }

        /// <summary>
        /// Set customer information for simulation
        /// </summary>
        public void SetCustomerInfo(string name, string customerId, string email, string phone)
        {
            _currentCustomerName = name ?? "John Doe";
            _currentCustomerId = customerId ?? "CUST-1024";
            _currentCustomerEmail = email ?? "john.doe@example.com";
            _lastKnownPhoneNumber = phone ?? _defaultPhoneNumber;

            LoggingService.Info($"[IVR] Customer info set - Name: {_currentCustomerName}, ID: {_currentCustomerId}, Phone: {_lastKnownPhoneNumber}");
        }

        /// <summary>
        /// Cleanup resources
        /// </summary>
        public void Dispose()
        {
            StopRingingTone();
            _ringingCancellationTokenSource?.Dispose();
            _ringingTonePlayer?.Dispose();
        }
    }
}

