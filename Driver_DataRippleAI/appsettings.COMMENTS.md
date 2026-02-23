# appsettings.json Configuration Documentation

This file provides detailed explanations for all settings in `appsettings.json`.

## Backend Audio Streaming Configuration

Settings for streaming microphone and speaker audio to the DataRipple backend via WebSocket. The backend handles transcription (STT), speaker identification, and AI agent response generation.

```json
"BackendAudioStreaming": {
  "WebSocketUrl": "",
  "SampleRate": 16000,
  "BitDepth": 16,
  "Channels": 1,
  "ChunkingIntervalMs": 200,
  "MaxReconnectionAttempts": 10,
  "BaseReconnectionDelayMs": 1000,
  "PingIntervalSeconds": 30,
  "PongTimeoutSeconds": 10,
  "ConnectionTimeoutSeconds": 20
}
```

### Connection
- **WebSocketUrl**: Explicit WebSocket URL for the audio streaming endpoint. If left empty, the URL is automatically derived from `Backend.BaseUrl` by converting the HTTP(S) scheme to WS(S) and appending `/ws/audio-stream`. For example, `https://ghostagent-dev.dataripple.ai/api/` becomes `wss://ghostagent-dev.dataripple.ai/ws/audio-stream`.
- **ConnectionTimeoutSeconds**: Maximum time in seconds to wait for the initial WebSocket handshake to complete (default: 20)

### Audio Format
- **SampleRate**: Audio sample rate in Hz (default: 16000). Common values: 8000, 16000, 22050, 44100, 48000.
- **BitDepth**: Bits per sample (default: 16). Supported values: 8, 16, 24.
- **Channels**: Number of audio channels. 1 = mono, 2 = stereo (default: 1).
- **ChunkingIntervalMs**: Interval in milliseconds between audio chunks sent to the backend (default: 200). Lower values increase real-time responsiveness but generate more network traffic.

### Reconnection
- **MaxReconnectionAttempts**: Maximum number of reconnection attempts before giving up (default: 10). After exhaustion, the `ReconnectionFailed` event fires.
- **BaseReconnectionDelayMs**: Base delay in milliseconds for exponential backoff between reconnection attempts (default: 1000). The actual delay is `min(base * 2^(attempt-1), 60000)`.

### Health Monitoring
- **PingIntervalSeconds**: Interval in seconds between health ping messages sent to the backend (default: 30). The backend should respond with a `pong` message.
- **PongTimeoutSeconds**: Maximum time in seconds to wait for a pong response before considering the connection stale and triggering reconnection (default: 10).

---

## Audio Device Configuration

Settings for audio input/output devices. These are typically set through the UI and saved automatically.

- **SelectedMicrophoneDevice**: Name of selected microphone device (empty = system default)
- **SelectedSpeakerDevice**: Name of selected speaker/headphone device (empty = system default)
- **MicrophoneDeviceIndex**: Index of microphone device in system device list
- **SpeakerDeviceIndex**: Index of speaker device in system device list
- **DevModeSystemAudioOnly**: When `true`, enables system audio capture only (no microphone) for testing with PC audio playback (default: `false`)
- **EnableAudioFormatDiagnostics**: Enable detailed audio format logging (default: `true`)
- **LogAudioFormatChanges**: Log when audio format changes are detected (default: `true`)
- **VerboseAudioLogging**: Enable verbose audio processing logs. Can be noisy in production (default: `false`)

---

## Call Logging Configuration

Settings for call logging and recording.

- **EnableCallLogging**: Enable/disable call logging to files (default: `false`)

---

## Backend API Configuration

Settings for backend authentication and API communication.

- **BaseUrl**: Base URL for backend API (default: `https://ghostagent-dev.dataripple.ai/api/`)
- **AuthLoginPath**: Path for authentication endpoint (default: `/auth/login`)
- **RefreshIntervalInMinutes**: Token refresh interval in minutes (default: 15)
- **TokenExpirationPeriodInHours**: Token expiration period in hours (default: 24)

---

## Client Integration Configuration

Settings for frontend WebSocket integration. The driver relays transcripts, agent responses, and call lifecycle events to a browser-based frontend via WebSocket.

- **demoFrontWebSocket**: Frontend WebSocket URL for demo/testing (default: `ws://localhost:3006/ws/`)
- **EnableFrontendIntegration**: Enable/disable frontend WebSocket integration (default: `true`)

---

## Notes

- All time values are in milliseconds (ms) or seconds (s) as specified in the field name
- Audio sample rates: 8000, 16000, 22050, 44100, and 48000 Hz are supported
- The audio format sent to the backend is always raw PCM, base64-encoded in JSON messages
