using System;
using Unity.Collections.LowLevel.Unsafe;

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

        // Used by Relay SDK
        public static RelayConnectionData FromBytePointer(byte* dataPtr, int length)
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

            var connectionData = new RelayConnectionData();
            UnsafeUtility.MemCpy(connectionData.Value, dataPtr, length);
            return connectionData;
        }
    }
}
