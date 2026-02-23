using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DataRippleAIDesktop.Models
{
    /// <summary>
    /// Represents a buffer of audio data received during recording or playback.
    /// Stores the data in a byte array along with a timestamp-seconds indicating when the data was received.
    /// </summary>
    public class DataBuffer
    {
        /// <summary>
        /// Gets or sets the second of the timestamp - second when the data was received.
        /// This is used to track the time of data capture relative to the system clock.
        /// Default value is -1, indicating an uninitialized or invalid timestamp.
        /// </summary>
        public int SecondsDataReceived { get; set; } = -1;

        /// <summary>
        /// Gets or sets the actual audio data as a byte array.
        /// This array holds the raw data captured from either the microphone or the loopback device.
        /// </summary>
        public byte[] Data { get; set; } = new byte[0];

        /// <summary>
        /// Initializes a new instance of the <see cref="DataBuffer"/> class.
        /// Optionally allows for the initialization of data and timestamp.
        /// </summary>
        /// <param name="data">The audio data to store.</param>
        /// <param name="secondsDataReceived">The timestamp in seconds when the data was captured.</param>
        public DataBuffer(byte[] data = null, int secondsDataReceived = -1)
        {
            // Assign values or use defaults
            Data = data ?? new byte[0];
            SecondsDataReceived = secondsDataReceived;
        }
    }
}
