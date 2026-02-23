using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DataRippleAIDesktop.Utilities
{
    public static class AudioChunkholderUtility
    {
        public static int MaxNumberOfChunksToHold { get; set; } = 10; 

        public static ConcurrentQueue<byte[]> AudioChunkHolderForSpecifiedSec = new ConcurrentQueue<byte[]>();

        /// <summary>
        /// Check for overflow
        /// </summary>
        public static void CheckOverFlow()
        {
            if (AudioChunkHolderForSpecifiedSec.Count > MaxNumberOfChunksToHold)
            {
                byte[] removedBytes = null;
                AudioChunkHolderForSpecifiedSec.TryDequeue(out removedBytes);
            }
        }

        /// <summary>
        /// Clear Chunk Holder
        /// Call this function on web socket start and end and on recording start and end.
        /// </summary>
        public static void ClearChunkHolder()
        {
            AudioChunkHolderForSpecifiedSec.Clear();
            AudioChunkHolderForSpecifiedSec = new ConcurrentQueue<byte[]>();
        }
    }
}
