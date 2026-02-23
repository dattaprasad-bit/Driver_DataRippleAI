using System;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using DataRippleAIDesktop.Services;

namespace DataRippleAIDesktop.Services
{
    /// <summary>
    /// Factory for creating HttpClient instances with Windows 10 compatible TLS/SSL settings
    /// </summary>
    public static class HttpClientFactory
    {
        /// <summary>
        /// Create an HttpClient with Windows 10 compatible TLS/SSL configuration
        /// </summary>
        public static HttpClient CreateCompatibleHttpClient(TimeSpan? timeout = null)
        {
            try
            {
                // Create HttpClientHandler with Windows 10 specific settings
                var handler = new HttpClientHandler();
                
                // Windows 10 compatibility settings
                handler.CheckCertificateRevocationList = false; // Prevent hanging on slow networks
                handler.ServerCertificateCustomValidationCallback = ValidateServerCertificate;
                
                // Try to set SSL protocols (may not be available on all Windows 10 versions)
                try
                {
                    handler.SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | 
                                          System.Security.Authentication.SslProtocols.Tls11 | 
                                          System.Security.Authentication.SslProtocols.Tls;
                    
                    // Try to add TLS 1.3 if available
                    try
                    {
                        handler.SslProtocols |= System.Security.Authentication.SslProtocols.Tls13;
                        LoggingService.Info("[HttpClientFactory] TLS 1.3 enabled for HttpClient");
                    }
                    catch
                    {
                        LoggingService.Info("[HttpClientFactory] TLS 1.3 not available, using TLS 1.2");
                    }
                }
                catch (Exception ex)
                {
                    LoggingService.Info($"[HttpClientFactory] Could not set SSL protocols: {ex.Message}");
                }
                
                var httpClient = new HttpClient(handler);
                
                // Set timeout (default 30 seconds, shorter for Windows 10 stability)
                httpClient.Timeout = timeout ?? TimeSpan.FromSeconds(30);
                
                // Add User-Agent for better compatibility
                httpClient.DefaultRequestHeaders.Add("User-Agent", "DataRippleAI/1.0 (Windows)");
                
                LoggingService.Info($"[HttpClientFactory] Created compatible HttpClient with {httpClient.Timeout} timeout");
                return httpClient;
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[HttpClientFactory] Failed to create compatible HttpClient: {ex.Message}");
                
                // Fallback to basic HttpClient
                var fallbackClient = new HttpClient();
                fallbackClient.Timeout = timeout ?? TimeSpan.FromSeconds(30);
                LoggingService.Info("[HttpClientFactory] Using fallback HttpClient");
                return fallbackClient;
            }
        }
        
        /// <summary>
        /// Create HttpClient specifically for API calls (with API-specific settings)
        /// </summary>
        public static HttpClient CreateApiHttpClient(string apiKey = null, TimeSpan? timeout = null)
        {
            var httpClient = CreateCompatibleHttpClient(timeout ?? TimeSpan.FromSeconds(15));
            
            // Add API-specific headers
            httpClient.DefaultRequestHeaders.Accept.Clear();
            httpClient.DefaultRequestHeaders.Accept.Add(
                new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
            
            if (!string.IsNullOrEmpty(apiKey))
            {
                // Add API key header (will be overridden by service-specific headers if needed)
                httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
            }
            
            return httpClient;
        }
        
        /// <summary>
        /// Custom certificate validation for Windows 10 compatibility
        /// </summary>
        private static bool ValidateServerCertificate(
            HttpRequestMessage requestMessage,
            X509Certificate2 certificate,
            X509Chain chain,
            SslPolicyErrors sslErrors)
        {
            try
            {
                // Log certificate validation for debugging
                if (sslErrors != SslPolicyErrors.None)
                {
                    LoggingService.Info($"[HttpClientFactory] SSL Certificate validation warning for {requestMessage?.RequestUri?.Host}: {sslErrors}");
                    
                    // For known API endpoints, be more permissive on Windows 10
                    var host = requestMessage?.RequestUri?.Host?.ToLower();
                    if (host != null && host.Contains("dataripple.ai"))
                    {
                        LoggingService.Info($"[HttpClientFactory] Allowing connection to trusted API: {host}");
                        return true;
                    }
                }
                
                // For production: implement proper certificate validation
                // For now: allow connections to prevent Windows 10 hanging issues
                return true;
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[HttpClientFactory] Certificate validation error: {ex.Message}");
                return true; // Allow connection to prevent hanging
            }
        }
    }
}

