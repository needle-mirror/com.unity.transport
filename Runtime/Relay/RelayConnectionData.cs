using System;
using Unity.Collections.LowLevel.Unsafe;

namespace Unity.Networking.Transport.Relay
{
    /// <summary>
    /// This is the encrypted data that the Relay server uses for describing a connection.
    /// Used mainly in the connection establishing process (Binding).
    /// </summary>
    public unsafe struct RelayConnectionData
    {
        /// <summary>
        /// The length in bytes of the Connection Data.
        /// </summary>
        public const int k_Length = 255;
        /// <summary>
        /// The raw data of the Connection Data
        /// </summary>
        public fixed byte Value[k_Length];

        // Used by Relay SDK
        /// <summary>
        /// Converts a byte pointer to a RelayConnectionData.
        /// </summary>
        /// <param name="dataPtr">The pointer to the data of the Connection Data.</param>
        /// <param name="length">The length of the data.</param>
        /// <exception cref="ArgumentException">Provided byte array length is invalid, must be {k_Length} but got {length}.</exception>
        /// <returns>Returns a RelayConnectionData constructed from the provided data.</returns>
        public static RelayConnectionData FromBytePointer(byte* dataPtr, int length)
        {
            if (length > k_Length)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                throw new ArgumentException($"Provided byte array length is invalid, must be less or equal to {k_Length} but got {length}.");
#else
                UnityEngine.Debug.LogError($"Provided byte array length is invalid, must be less or equal to {k_Length} but got {length}.");
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
            fixed (byte* ptr = data)
            {
                return RelayConnectionData.FromBytePointer(ptr, data.Length);
            }
        }
    }
}
