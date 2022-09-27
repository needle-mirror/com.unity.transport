using System;
using Unity.Collections.LowLevel.Unsafe;

namespace Unity.Networking.Transport.Relay
{
    /// <summary>
    /// Used to represent the HMACKey for the Relay Service
    /// </summary>
    public unsafe struct RelayHMACKey
    {
        /// <summary>
        /// The length in bytes of the RelayHMACKey.
        /// </summary>
        public const int k_Length = 64;

        /// <summary>
        /// The raw data of the HMAC key.
        /// </summary>
        public fixed byte Value[k_Length];

        // Used by Relay SDK
        /// <summary>
        /// Converts a byte pointer to a RelayHMACKey.
        /// </summary>
        /// <param name="data">The pointer to the data of the Allocation Id.</param>
        /// <param name="length">The length of the data.</param>
        /// <exception cref="ArgumentException">Provided byte array length is invalid, must be {k_Length} but got {length}.</exception>
        /// <returns>Returns a RelayHMACKey constructed from the provided data.</returns>
        public static RelayHMACKey FromBytePointer(byte* data, int length)
        {
            if (length != k_Length)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                throw new ArgumentException($"Provided byte array length is invalid, must be {k_Length} but got {length}.");
#else
                UnityEngine.Debug.LogError($"Provided byte array length is invalid, must be {k_Length} but got {length}.");
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
            fixed (byte* ptr = data)
            {
                return RelayHMACKey.FromBytePointer(ptr, data.Length);
            }
        }
    }
}
