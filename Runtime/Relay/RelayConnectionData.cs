using System;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Networking.Transport.Logging;

namespace Unity.Networking.Transport.Relay
{
    /// <summary>
    /// This is the encrypted data that the Relay server uses for describing a connection.
    /// Used mainly in the connection stablishing process (Binding)
    /// </summary>
    public unsafe struct RelayConnectionData
    {
        public const int k_Length = 255;
        public fixed byte Value[k_Length];

        /// <summary>Convert a raw buffer to a <see cref="RelayConnectionData"/></summary>
        /// <param name="dataPtr">Raw pointer to buffer to convert.</param>
        /// <param name="length">Length of the buffer to convert.</param>
        /// <returns>New <see cref="RelayConnectionData"/>.</returns>
        public static RelayConnectionData FromBytePointer(byte* dataPtr, int length)
        {
            if (length > k_Length)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                throw new ArgumentException($"Provided byte array length is invalid, must be less or equal to {k_Length} but got {length}.");
#else
                DebugLog.ErrorRelayWrongBufferSizeLess(k_Length, length);
                return default;
#endif
            }
            
            var connectionData = new RelayConnectionData();
            UnsafeUtility.MemCpy(connectionData.Value, dataPtr, length);
            return connectionData;
        }

        /// <summary>Convert a byte array to a <see cref="RelayConnectionData"/></summary>
        /// <param name="data">Array to convert.</param>
        /// <returns>New <see cref="RelayConnectionData"/>.</returns>
        public static RelayConnectionData FromByteArray(byte[] data)
        {
            fixed(byte* ptr = data)
            {
                return RelayConnectionData.FromBytePointer(ptr, data.Length);
            }
        }
    }
}
