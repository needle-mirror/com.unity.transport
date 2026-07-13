using System;
using System.Diagnostics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Unity.Networking.Transport.Utilities
{
    /// <summary>
    /// A ring queue implementation that extends its capacity automatically when full, unlike the
    /// <see cref="UnsafeRingQueue{T}"/> from the Collections package which has a fixed capacity.
    /// </summary>
    /// <typeparam name="T">Type of elements to store in the queue.</typeparam>
    internal unsafe struct UnsafeDynamicRingQueue<T> : IDisposable where T : unmanaged
    {
        private struct RingQueueData
        {
            // We don't actually use this as a list, it's just a convenient dynamic container.
            public UnsafeList<T> Elements;

            public int Head;
            public int Count;

            public Allocator Allocator;
        }
        
        [NativeDisableUnsafePtrRestriction]
        private RingQueueData* m_Data;

        /// <summary>Create a new dynamic ring queue with the given initial capacity.</summary>
        /// <param name="initialCapacity">Initial capacity of the queue.</param>
        /// <param name="allocator">Allocator to use for the queue's memory.</param>
        public UnsafeDynamicRingQueue(int initialCapacity, Allocator allocator)
        {
            m_Data = (RingQueueData*)UnsafeUtility.Malloc(
                UnsafeUtility.SizeOf<RingQueueData>(),
                UnsafeUtility.AlignOf<RingQueueData>(),
                allocator);
            
            m_Data->Elements = new UnsafeList<T>(initialCapacity, allocator);
            m_Data->Elements.Length = initialCapacity;
            m_Data->Head = 0;
            m_Data->Count = 0;
            m_Data->Allocator = allocator;
        }

        /// <summary>Whether the current queue is a valid object.</summary>
        public bool IsCreated => m_Data != null;

        /// <summary>Enqueue an item at the end of the queue.</summary>
        /// <param name="item">Item to enqueue.</param>
        public void Enqueue(T item)
        {
            CheckIsCreated();

            // Check if we need to grow our internal buffer.
            if (m_Data->Count == m_Data->Elements.Length)
            {
                if (m_Data->Head == 0)
                {
                    // If the head is at the start of the buffer, we can just resize it.
                    m_Data->Elements.Resize(m_Data->Elements.Length * 2);
                }
                else
                {
                    // It's not clear from the name, but InsertRange basically shifts everything
                    // from the given index to the right by the given count. So by shifting the head
                    // by the length of the current buffer, we're doubling its capacity while
                    // creating a "hole" in the middle of the buffer where we can keep enqueueing.
                    var originalLength = m_Data->Elements.Length;
                    m_Data->Elements.InsertRange(m_Data->Head, originalLength);
                    m_Data->Head += originalLength;
                }
            }

            var index = (m_Data->Head + m_Data->Count) % m_Data->Elements.Length;
            m_Data->Elements[index] = item;
            m_Data->Count++;
        }

        /// <summary>Try to dequeue an item from the front of the queue.</summary>
        /// <param name="item">Dequeued item (default if no items in the queue).</param>
        /// <returns>True if there was an item to dequeue, false otherwise.</returns>
        public bool TryDequeue(out T item)
        {
            CheckIsCreated();

            if (m_Data->Count == 0)
            {
                item = default;
                return false;
            }
            else
            {
                item = m_Data->Elements[m_Data->Head];
                m_Data->Head = (m_Data->Head + 1) % m_Data->Elements.Length;
                m_Data->Count--;
                return true;
            }
        }

        /// <summary>Try to peek at the front of the queue (don't dequeue it).</summary>
        /// <param name="item">Peeked item (default if no items in the queue).</param>
        /// <returns>True if there was an item in the queue, false otherwise.</returns>
        public bool TryPeek(out T item)
        {
            CheckIsCreated();

            if (m_Data->Count == 0)
            {
                item = default;
                return false;
            }
            else
            {
                item = m_Data->Elements[m_Data->Head];
                return true;
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (IsCreated)
            {
                m_Data->Elements.Dispose();
                UnsafeUtility.Free((void*)m_Data, m_Data->Allocator);
                m_Data = null;
            }
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        private void CheckIsCreated()
        {
            if (!IsCreated)
                throw new ObjectDisposedException("NativeDynamicRingQueue is not created or has been disposed.");
        }
    }
}