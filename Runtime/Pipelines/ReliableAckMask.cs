using System;
using System.Diagnostics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Unity.Networking.Transport.Utilities
{
    /// <summary>
    /// Structure used to hold the bitmap of acknowledged packets for the reliable pipeline. It only
    /// tracks the acknowledgements based on the last acknowledged packet which is entirely implicit
    /// (this structure does not track the last acknowledged sequence number). Packets preceding
    /// this last acknowledged packet are identified by their distance to it.
    /// 
    /// For example, if the last acknowledged packet is sequence number 100, then distance 0 is
    /// packet 100, distance 1 is packet 99, distance 2 is packet 98, etc. Thus calling Ack(1)
    /// will mark packet 99 as acknowledged, and IsAcked(2) will return true if packet 98 has been
    /// acknowledged. Calling Shift() will move the window forward (left-shift the bitmap).
    /// </summary>
    internal unsafe struct ReliableAckMask : IEquatable<ReliableAckMask>
    {
        /// <summary>Number of acknowledgements that can be fit per byte in the mask.</summary>
        public const int AcksPerByte = 8;

        private const int k_ByteCount = byte.MaxValue;
        private const int k_BitCount = k_ByteCount * AcksPerByte;
        private const int k_ChunkCount = 32;

        // Representing the mask as ulongs instead of bytes allows more efficient bitwise operations
        // but it does mean that the structure is one byte larger than strictly necessary.
        private fixed ulong m_Chunks[k_ChunkCount];

        /// <summary>Mask with all packets acknowledged.</summary>
        public static readonly ReliableAckMask AllAcked;

        static ReliableAckMask()
        {
            // Initialize the static AllAcked mask.
            for (int i = 0; i < k_ChunkCount; i++)
            {
                AllAcked.m_Chunks[i] = ulong.MaxValue;
            }
        }

        /// <summary>Create a new mask from the given bytes.</summary>
        /// <remarks>Bytes are assumed to be the first in the mask, rest will be ones.</remarks>
        public static ReliableAckMask FromBytes(ReadOnlySpan<byte> bytes)
        {
            CheckBytes(bytes);

            var mask = AllAcked;
            var copyLength = math.min(bytes.Length, k_ByteCount);

            fixed (byte* ptr = bytes)
            {
                UnsafeUtility.MemCpy((void*)mask.m_Chunks, ptr, copyLength);
            }

            return mask;
        }

        /// <summary>Mark the packet at the given distance as acknowledged.</summary>
        public void Ack(int distance)
        {
            CheckDistance(distance);

            var chunk = distance / 64;
            var bit = distance % 64;
            m_Chunks[chunk] |= (1UL << bit);
        }

        /// <summary>Check if the packet at the given distance has been acknowledged.</summary>
        public bool IsAcked(int distance)
        {
            CheckDistance(distance);

            var chunk = distance / 64;
            var bit = distance % 64;
            return (m_Chunks[chunk] & (1UL << bit)) != 0;
        }

        /// <summary>Move the window forward by the given number of packets.</summary>
        public void Shift(int count)
        {
            CheckShiftCount(count);

            // Move all chunks left until we have a shift length less than 64 bits.
            while (count >= 64)
            {
                for (int i = k_ChunkCount - 1; i > 0; i--)
                {
                    m_Chunks[i] = m_Chunks[i - 1];
                }
                m_Chunks[0] = 0UL;
                count -= 64;
            }

            // If the shift length was a nice round number, we're done.
            if (count == 0)
                return;

            // Now shift the remaining bits left from each chunk to the next.
            for (int i = k_ChunkCount - 1; i > 0; i--)
            {
                var shifted = m_Chunks[i] << count;
                var previous = m_Chunks[i - 1] >> (64 - count);
                m_Chunks[i] = shifted | previous;
            }
            m_Chunks[0] <<= count;
        }

        /// <summary>Merge the given mask into this one (bitwise OR).</summary>
        public void Merge(ReliableAckMask other)
        {
            for (int i = 0; i < k_ChunkCount; i++)
            {
                m_Chunks[i] |= other.m_Chunks[i];
            }
        }

        /// <summary>Write the first bytes of the mask to the given writer.</summary>
        public void WriteTo(ref DataStreamWriter writer, int numBytes)
        {
            CheckWriteToCount(numBytes);

            fixed (ulong* maskPtr = m_Chunks)
            {
                writer.WriteBytesUnsafe((byte*)maskPtr, numBytes);
            }
        }

        /// <summary>Get the smallest mask size that covers all unacknowledged packets.</summary>
        public int MinimumWriteSize()
        {
            // We don't want a 0-bit in the very last byte to mess up our calculation.
            m_Chunks[k_ChunkCount - 1] |= 0xFF00000000000000;

            for (int i = k_ChunkCount - 1; i >= 0; i--)
            {
                if (m_Chunks[i] != ulong.MaxValue)
                {
                    var bytesWithUnack = 8 - (math.lzcnt(~m_Chunks[i]) / 8);
                    return (i * 8) + bytesWithUnack;
                }
            }

            // Always need at least one byte.
            return 1;
        }

        public bool Equals(ReliableAckMask other)
        {
            fixed (ulong* thisPtr = m_Chunks)
            {
                return UnsafeUtility.MemCmp(thisPtr, (void*)other.m_Chunks, k_ByteCount) == 0;
            }
        }

        public override int GetHashCode()
        {
            var hash = 0;
            for (int i = 0; i < k_ChunkCount - 1; i++)
            {
                hash ^= m_Chunks[i].GetHashCode();
            }
            return hash ^ (m_Chunks[k_ChunkCount - 1] & 0x00FFFFFFFFFFFFFF).GetHashCode();
        }

        public override bool Equals(object obj) => obj == null ? false : Equals((ReliableAckMask)obj);
        public static bool operator ==(ReliableAckMask left, ReliableAckMask right) => left.Equals(right);
        public static bool operator !=(ReliableAckMask left, ReliableAckMask right) => !left.Equals(right);

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        private static void CheckBytes(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length > k_ByteCount)
                throw new ArgumentException("Byte array is too large to read a mask from.");
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        private static void CheckDistance(int distance)
        {
            if (distance < 0 || distance >= k_BitCount)
                throw new ArgumentOutOfRangeException("Distance must be in the range [0, 63].");
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        private static void CheckShiftCount(int count)
        {
            if (count < 0 || count > k_BitCount)
                throw new ArgumentOutOfRangeException("Shift count must be in the range [0, 64].");
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        private static void CheckWriteToCount(int count)
        {
            if (count < 0 || count > k_ByteCount)
                throw new ArgumentOutOfRangeException("Invalid write length for reliable mask.");
        }
    }
}