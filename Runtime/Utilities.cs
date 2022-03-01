using System;
using System.Diagnostics;
using Unity.Collections;

namespace Unity.Networking.Transport.Utilities
{
    /// <summary>
    /// A NativeMultiQueue is a set of several FIFO queues split into buckets.
    /// Each bucket has its own first and last item, and each bucket can have
    /// items pushed and popped individually.
    /// </summary>
    internal struct NativeMultiQueue<T> : IDisposable where T : unmanaged
    {
        private NativeList<T> m_Queue;
        private NativeList<int> m_QueueHeadTail;
        private NativeArray<int> m_MaxItems;

        /// <summary>
        /// Whether this queue has been allocated (and not yet deallocated).
        /// </summary>
        /// <value>True if this queue has been allocated (and not yet deallocated).</value>
        public bool IsCreated => m_Queue.IsCreated;

        /// <summary>
        /// Instantiates a new NativeMultiQueue which has a single bucket and the
        /// specified capacity for the number of items for that bucket. Accessing buckets
        /// out of range will grow the number of buckets, and pushing more items than the
        /// initial capacity will increase the number of items for each bucket.
        /// </summary>
        public NativeMultiQueue(int initialMessageCapacity)
        {
            m_MaxItems = new NativeArray<int>(1, Allocator.Persistent);
            m_MaxItems[0] = initialMessageCapacity;
            m_Queue = new NativeList<T>(initialMessageCapacity, Allocator.Persistent);
            m_QueueHeadTail = new NativeList<int>(2, Allocator.Persistent);
        }

        /// <summary>
        /// Releases all resources (memory and safety handles).
        /// </summary>
        public void Dispose()
        {
            m_MaxItems.Dispose();
            m_Queue.Dispose();
            m_QueueHeadTail.Dispose();
        }

        /// <summary>
        /// Enqueue a new item to a specific bucket. If the specified bucket is larger
        /// than the current amount of buckets, the queue's number of buckets will be
        /// increased to match. If enqueueing the item would exceed the queue's capacity,
        /// the queue's capacity will be increased.
        /// </summary>
        public void Enqueue(int bucket, T value)
        {
            // Grow number of buckets to fit specified index
            if (bucket >= m_QueueHeadTail.Length / 2)
            {
                int oldSize = m_QueueHeadTail.Length;
                m_QueueHeadTail.ResizeUninitialized((bucket + 1) * 2);
                for (; oldSize < m_QueueHeadTail.Length; ++oldSize)
                    m_QueueHeadTail[oldSize] = 0;
                m_Queue.ResizeUninitialized((m_QueueHeadTail.Length / 2) * m_MaxItems[0]);
            }
            int idx = m_QueueHeadTail[bucket * 2 + 1];
            if (idx >= m_MaxItems[0])
            {
                // Grow number of items per bucket
                int oldMax = m_MaxItems[0];
                while (idx >= m_MaxItems[0])
                    m_MaxItems[0] = m_MaxItems[0] * 2;
                int maxBuckets = m_QueueHeadTail.Length / 2;
                m_Queue.ResizeUninitialized(maxBuckets * m_MaxItems[0]);
                for (int b = maxBuckets - 1; b >= 0; --b)
                {
                    for (int i = m_QueueHeadTail[b * 2 + 1] - 1; i >= m_QueueHeadTail[b * 2]; --i)
                    {
                        m_Queue[b * m_MaxItems[0] + i] = m_Queue[b * oldMax + i];
                    }
                }
            }
            m_Queue[m_MaxItems[0] * bucket + idx] = value;
            m_QueueHeadTail[bucket * 2 + 1] = idx + 1;
        }

        /// <summary>
        /// Dequeue an item from a specific bucket. If the bucket does not exist, or if the
        /// bucket is empty, the call will fail and return false.
        /// </summary>
        public bool Dequeue(int bucket, out T value)
        {
            if (bucket < 0 || bucket >= m_QueueHeadTail.Length / 2)
            {
                value = default;
                return false;
            }
            int idx = m_QueueHeadTail[bucket * 2];
            if (idx >= m_QueueHeadTail[bucket * 2 + 1])
            {
                m_QueueHeadTail[bucket * 2] = m_QueueHeadTail[bucket * 2 + 1] = 0;
                value = default;
                return false;
            }
            else if (idx + 1 == m_QueueHeadTail[bucket * 2 + 1])
            {
                m_QueueHeadTail[bucket * 2] = m_QueueHeadTail[bucket * 2 + 1] = 0;
            }
            else
            {
                m_QueueHeadTail[bucket * 2] = idx + 1;
            }

            value = m_Queue[m_MaxItems[0] * bucket + idx];
            return true;
        }

        /// <summary>
        /// Peek the next item in a specific bucket. If the bucket does not exist, or if the
        /// bucket is empty, the call will fail and return false.
        /// </summary>
        public bool Peek(int bucket, out T value)
        {
            if (bucket < 0 || bucket >= m_QueueHeadTail.Length / 2)
            {
                value = default;
                return false;
            }
            int idx = m_QueueHeadTail[bucket * 2];
            if (idx >= m_QueueHeadTail[bucket * 2 + 1])
            {
                value = default;
                return false;
            }

            value = m_Queue[m_MaxItems[0] * bucket + idx];
            return true;
        }

        /// <summary>
        /// Remove all items from a specific bucket. If the bucket does not exist,
        /// the call will not do anything.
        /// </summary>
        public void Clear(int bucket)
        {
            if (bucket < 0 || bucket >= m_QueueHeadTail.Length / 2)
                return;
            m_QueueHeadTail[bucket * 2] = 0;
            m_QueueHeadTail[bucket * 2 + 1] = 0;
        }
    }

    /// <summary>
    /// Utility class used when dealing with sequenced pipeline stages.
    /// </summary>
    /// <seealso cref="ReliableUtility">
    public static class SequenceHelpers
    {
        /// <summary>
        /// Calculate the difference between two sequence IDs, taking integer overflow/underflow into account.
        /// For example, both AbsDistance(65535, 0) and AbsDistance(0, 65535) will return 1, not 65535.
        /// </summary>
        /// <param name="lhs">The first sequence ID. Compared against the second.</param>
        /// <param name="rhs">The second sequence ID. Compared against the first.</param>
        /// <returns>An integer value equal to the distance between the sequence IDs.</returns>
        public static int AbsDistance(ushort lhs, ushort rhs)
        {
            int distance;
            if (lhs < rhs)
                distance = lhs + ushort.MaxValue + 1 - rhs;
            else
                distance = lhs - rhs;
            return distance;
        }

        /// <summary>
        /// This method was originally added in February 2019, but does not seem to be used anywhere currently.
        /// Its original context seems to have been intended for a very simple version of checking whether a
        /// packet's sequence ID was equal to or newer than the last received packet.
        /// </summary>
        /// <param name="current">The sequence ID of a newly-arrived packet to check</param>
        /// <param name="old">The sequence ID of a previously received packet</param>
        /// <returns>true if current is newer than old</returns>
        public static bool IsNewer(uint current, uint old)
        {
            // Invert the check so same does not count as newer
            return !(old - current < (1u << 31));
        }

        /// <summary>
        /// Describes whether the non-wrapping difference between two sequenceIDs is
        /// less than 2^15 (or 0x8000, or 32768). (The "16" seems to be the 16th bit
        /// in a 16-bit integer.)
        /// </summary>
        /// <param name="lhs">The first operand.</param>
        /// <param name="rhs">The second operand.</param>
        /// <returns>Whether or not the non-wrapping difference between the two operands is less than or equal to unsigned 0x7FFF.</returns>
        public static bool GreaterThan16(ushort lhs, ushort rhs)
        {
            const uint max_sequence_divide_2 = 0x7FFF;
            return lhs > rhs && lhs - rhs <= (ushort)max_sequence_divide_2 ||
                lhs < rhs && rhs - lhs > (ushort)max_sequence_divide_2;
        }

        /// <summary>
        /// Describes whether the non-absolute difference between two sequenceIDs is
        /// greater than or equal to 2^15 (or 0x8000, or 32768). (The "16" seems to
        /// be the 16th bit in a 16-bit integer.)
        /// </summary>
        /// <param name="lhs">The first operand.</param>
        /// <param name="rhs">The second operand.</param>
        /// <returns>Whether or not the non-wrapping difference between the two operands is greater than unsigned 0x7FFF.</returns>
        public static bool LessThan16(ushort lhs, ushort rhs)
        {
            return GreaterThan16(rhs, lhs);
        }

        /// <summary>
        /// Describes whether a packet is stale in the context of sequenced pipelines.
        /// </summary>
        /// <param name="sequence">The more recent sequence ID.</param>
        /// <param name="oldSequence">The older sequence ID.</param>
        /// <param name="windowSize">The window size</param>
        /// <returns>A boolean value containing the results of <see cref="LessThan16(ushort, ushort)"/> where lhs = sequence and rhs = oldSequence - windowSize.</returns>
        public static bool StalePacket(ushort sequence, ushort oldSequence, ushort windowSize)
        {
            return LessThan16(sequence, (ushort)(oldSequence - windowSize));
        }

        /// <summary>
        /// Converts a bitmask integer to a string representation of its binary expression, e.g.
        /// a mask value of 4 will return a string with the 3rd bit set:
        /// 00000000000000000000000000000100
        /// </summary>
        /// <param name="mask">The bitmask in integer format.</param>
        /// <returns>A string that represents the bitmask.</returns>
        public static string BitMaskToString(uint mask)
        {
            //  31     24      16      8       0
            // |-------+-------+-------+--------|
            //  00000000000000000000000000000000

            const int bits = 4 * 8;
            var sb = new char[bits];

            for (var i = bits - 1; i >= 0; i--)
            {
                sb[i] = (mask & 1) != 0 ? '1' : '0';
                mask >>= 1;
            }

            return new string(sb);
        }
    }

    /// <summary>
    /// Provides Extension methods for FixedStrings
    /// </summary>
    public static class FixedStringHexExt
    {
        /// <summary>
        /// Appends the hex using the specified str
        /// </summary>
        /// <typeparam name="T">The string type. Has constraints where it must be a struct, an INativeList<byte> and IUTF8Bytes.</typeparam>
        /// <param name="str">The string of type T. Passed in by reference, and will be modified by this method.</param>
        /// <param name="val">The ushort representation of the hex value to convert to its string representation and append to T.</param>
        /// <returns>The <see cref="FormatError"/> from the attempt to modify <see cref="str"/>, either None or Overflow.</returns>
        public static FormatError AppendHex<T>(ref this T str, ushort val) where T : unmanaged, INativeList<byte>, IUTF8Bytes
        {
            int shamt = 12;
            // Find the first non-zero nibble
            while (shamt > 0)
            {
                if (((val >> shamt) & 0xf) != 0)
                    break;
                shamt -= 4;
            }
            var err = FormatError.None;
            while (shamt >= 0)
            {
                var nibble = (val >> shamt) & 0xf;
                if (nibble >= 10)
                    err |= str.AppendRawByte((byte)('a' + nibble - 10));
                else
                    err |= str.AppendRawByte((byte)('0' + nibble));
                shamt -= 4;
            }
            return err != FormatError.None ? FormatError.Overflow : FormatError.None;
        }
    }

    /// <summary>
    /// Provides Extension methods for the <see cref="NativeList"> class
    /// </summary>
    public static class NativeListExt
    {
        /// <summary>
        /// This function will make sure that <see cref="sizeToFit"/> can fit into <see cref="list"/>.
        /// If <see cref="sizeToFit"/> >= <see cref="list"/>'s Length then <see cref="list"/> will be ResizeUninitialized to a new length.
        /// New Length will be the next highest power of 2 of <see cref="sizeToFit"/>
        /// </summary>
        /// <param name="list">List that should be resized if sizeToFit >= its size</param>
        /// <param name="sizeToFit">Requested size that should fit into list</param>
        public static void ResizeUninitializedTillPowerOf2<T>(this NativeList<T> list, int sizeToFit) where T : unmanaged
        {
            var n = list.Length;

            if (sizeToFit >= n)
            {
                // https://graphics.stanford.edu/~seander/bithacks.html#RoundUpPowerOf2
                sizeToFit |= sizeToFit >> 1;
                sizeToFit |= sizeToFit >> 2;
                sizeToFit |= sizeToFit >> 4;
                sizeToFit |= sizeToFit >> 8;
                sizeToFit |= sizeToFit >> 16;
                sizeToFit++;
                //sizeToFit is now next power of 2 of initial sizeToFit

                list.ResizeUninitialized(sizeToFit);
            }
        }
    }


    /// <summary>
    /// A simple method to obtain a random ushort provided by the <see cref="Unity.Mathematics.Random"/> class.
    /// </summary>
    public static class RandomHelpers
    {
        /// <returns>a ushort in [1..ushort.MaxValue - 1] range</returns>
        public static ushort GetRandomUShort()
        {
            var rnd = new Unity.Mathematics.Random((uint)Stopwatch.GetTimestamp());
            return (ushort)rnd.NextUInt(1, ushort.MaxValue - 1);
        }

        /// <returns>a ushort in [1..uint.MaxValue - 1] range</returns>
        public static ulong GetRandomULong()
        {
            var rnd = new Unity.Mathematics.Random((uint)Stopwatch.GetTimestamp());
            var high = rnd.NextUInt(0, uint.MaxValue - 1);
            var low = rnd.NextUInt(1, uint.MaxValue - 1);
            return ((ulong)high << 32) | (ulong)low;
        }
    }
}
