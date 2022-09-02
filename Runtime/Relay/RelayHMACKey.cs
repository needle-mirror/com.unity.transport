using System;
using Unity.Collections.LowLevel.Unsafe;

namespace Unity.Networking.Transport.Relay
{
    public unsafe struct RelayHMACKey
    {
        public const int k_Length = 64;

        public fixed byte Value[k_Length];

        // Used by Relay SDK
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
    }
}
