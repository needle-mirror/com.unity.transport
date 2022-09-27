using System;
using Unity.Collections.LowLevel.Unsafe;

namespace Unity.Networking.Transport.Relay
{
    /// <summary>
    /// Allocation Id is a unique identifier for a connected client/host to a Relay server.
    /// This identifier is used by the Relay protocol as the address of the client.
    /// </summary>
    public unsafe struct RelayAllocationId : IEquatable<RelayAllocationId>, IComparable<RelayAllocationId>
    {
        /// <summary>
        /// The length in bytes of the Allocation Id.
        /// </summary>
        public const int k_Length = 16;
        /// <summary>
        /// The raw data of the Allocation Id.
        /// </summary>
        public fixed byte Value[k_Length];

        // Used by Relay SDK
        /// <summary>
        /// Converts a byte pointer to a RelayAllocationId.
        /// </summary>
        /// <param name="dataPtr">The pointer to the data of the Allocation Id.</param>
        /// <param name="length">The length of the data.</param>
        /// <exception cref="ArgumentException">Provided byte array length is invalid, must be {k_Length} but got {length}.</exception>
        /// <returns>Returns a RelayAllocationId constructed from the provided data.</returns>
        public static RelayAllocationId FromBytePointer(byte* dataPtr, int length)
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

            var allocationId = new RelayAllocationId();
            UnsafeUtility.MemCpy(allocationId.Value, dataPtr, k_Length);
            return allocationId;
        }

        /// <summary>Convert a byte array to a <see cref="RelayAllocationId"/></summary>
        /// <param name="data">Array to convert.</param>
        /// <returns>New <see cref="RelayAllocationId"/>.</returns>
        public static RelayAllocationId FromByteArray(byte[] data)
        {
            fixed (byte* ptr = data)
            {
                return RelayAllocationId.FromBytePointer(ptr, data.Length);
            }
        }

        public static bool operator==(RelayAllocationId lhs, RelayAllocationId rhs)
        {
            return lhs.Compare(rhs) == 0;
        }

        public static bool operator!=(RelayAllocationId lhs, RelayAllocationId rhs)
        {
            return lhs.Compare(rhs) != 0;
        }

        public bool Equals(RelayAllocationId other)
        {
            return Compare(other) == 0;
        }

        public int CompareTo(RelayAllocationId other)
        {
            return Compare(other);
        }

        public override bool Equals(object other)
        {
            return other != null && this == (RelayAllocationId)other;
        }

        public override int GetHashCode()
        {
            fixed(byte* p = Value)
            unchecked
            {
                var result = 0;

                for (int i = 0; i < k_Length; i++)
                {
                    result = (result * 31) ^ (int)p[i];
                }

                return result;
            }
        }

        int Compare(RelayAllocationId other)
        {
            fixed(void* p = Value)
            {
                return UnsafeUtility.MemCmp(p, other.Value, k_Length);
            }
        }
    }
}
