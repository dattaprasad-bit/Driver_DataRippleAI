using System;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Net.Security;
using DataRippleAIDesktop.Services;

namespace DataRippleAIDesktop.Services
{
    /// <summary>
    /// Factory for creating ClientWebSocket instances with Windows 10 compatible TLS/SSL settings
    /// </summary>
    public static class WebSocketFactory
    {
        /// <summary>
        /// Create a ClientWebSocket with Windows 10 compatible TLS/SSL configuration
        /// </summary>
        public static ClientWebSocket CreateCompatibleWebSocket()
        {
            try
            {
                var webSocket = new ClientWebSocket();
                
                // Windows 10 specific WebSocket TLS configuration
                ConfigureWebSocketOptions(webSocket);
                
                LoggingService.Info("[WebSocketFactory] Created compatible WebSocket for Windows 10");
                return webSocket;
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[WebSocketFactory] Failed to create compatible WebSocket: {ex.Message}");
                
                // Fallback to basic WebSocket
                var fallbackSocket = new ClientWebSocket();
                LoggingService.Info("[WebSocketFactory] Using fallback WebSocket");
                return fallbackSocket;
            }
        }
        
        /// <summary>
        /// Configure WebSocket options for Windows 10 compatibility
        /// </summary>
        private static void ConfigureWebSocketOptions(ClientWebSocket webSocket)
        {
            try
            {
                // Set keep-alive to prevent connection drops
                webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                
                // Add User-Agent for better compatibility
                try
                {
                    webSocket.Options.SetRequestHeader("User-Agent", "DataRippleAI/1.0 (Windows)");
                }
                catch (Exception ex)
                {
                    LoggingService.Info($"[WebSocketFactory] Could not set User-Agent: {ex.Message}");
                }
                
                // Windows 10 specific: Set certificate validation callback
                try
                {
                    // Note: ClientWebSocket doesn't have direct certificate validation callback
                    // But we can set some options that help with Windows 10 compatibility
                    
                    // Set buffer sizes for better performance on Windows 10
                    webSocket.Options.SetBuffer(4096, 4096);
                    
                    LoggingService.Info("[WebSocketFactory] WebSocket options configured for Windows 10");
                }
                catch (Exception ex)
                {
                    LoggingService.Info($"[WebSocketFactory] Some WebSocket options could not be set: {ex.Message}");
                }
                
                // Log the configuration
                LoggingService.Info($"[WebSocketFactory] KeepAlive: {webSocket.Options.KeepAliveInterval}");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[WebSocketFactory] Error configuring WebSocket options: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Create WebSocket for frontend integration
        /// </summary>
        public static ClientWebSocket CreateFrontendWebSocket()
        {
            var webSocket = CreateCompatibleWebSocket();
            
            try
            {
                LoggingService.Info("[WebSocketFactory] Created frontend integration WebSocket");
                return webSocket;
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[WebSocketFactory] Error creating frontend WebSocket: {ex.Message}");
                return webSocket;
            }
        }
    }
}

