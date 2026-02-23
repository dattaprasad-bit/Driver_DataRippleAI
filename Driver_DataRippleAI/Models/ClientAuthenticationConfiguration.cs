namespace DataRippleAIDesktop.Models
{
    public class ClientIntegrationConfiguration
    {
        public string WebSocketUrl { get; set; } = "wss://frontend.clientdomain.com/ws";
        public string DemoFrontWebSocket { get; set; } = "ws://localhost:3006/ws/";
        public bool EnableFrontendIntegration { get; set; } = false;  // Single setting to enable/disable all frontend features
    }
}
