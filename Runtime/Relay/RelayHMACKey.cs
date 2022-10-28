using System;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Networking.Transport.Logging;

namespace Unity.Networking.Transport.Relay
{
    public unsafe struct RelayHMACKey
    {
        public const int k_Length = 64;

        public fixed byte Value[k_Length];

        /// <summary>Convert a raw buffer to a <see cref="RelayHMACKey"/></summary>
        /// <param name="dataPtr">Raw pointer to buffer to convert.</param>
        /// <param name="length">Length of the buffer to convert.</param>
        /// <returns>New <see cref="RelayHMACKey"/>.</returns>
        public static RelayHMACKey FromBytePointer(byte* data, int length)
        {
            if (length != k_Length)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                throw new ArgumentException($"Provided byte array length is invalid, must be {k_Length} but got {length}.");
#else
                DebugLog.ErrorRelayWrongBufferSize(k_Length, length);
                return default;
#endif
            }

            var hmacKey = new RelayHMACKey();
            UnsafeUtility.MemCpy(hmacKey.Value, data, length);
            return hmacKey;
        }

        /// <summary>Convert a byte array to a <see cref="RelayHMACKey"/></summary>
        /// <param name="data">Array to convert.</param>
        /// <returns>New <see cref="RelayHMACKey"/>.</returns>
        public static RelayHMACKey FromByteArray(byte[] data)
        {
            fixed(byte* ptr = data)
            {
                return RelayHMACKey.FromBytePointer(ptr, data.Length);
            }
        }
    }
}
