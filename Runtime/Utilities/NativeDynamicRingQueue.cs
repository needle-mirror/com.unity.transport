using System;
using System.Diagnostics;
using Unity.Collections;

namespace Unity.Networking.Transport.Utilities
{
    /// <summary>
    /// A ring queue implementation that extends its capacity automatically when full, unlike the
    /// <see cref="NativeRingQueue{T}"/> from the Collections package which has a fixed capacity.
    /// </summary>
    /// <typeparam name="T">Type of elements to store in the queue.</typeparam>
    internal struct NativeDynamicRingQueue<T> : IDisposable where T : unmanaged
    {
        // We don't actually use this as a list, it's just a convenient dynamic container.
        private NativeList<T> m_Data;

        private int m_Head;
        private int m_Count;

        /// <summary>Create a new dynamic ring queue with the given initial capacity.</summary>
        /// <param name="initialCapacity">Initial capacity of the queue.</param>
        /// <param name="allocator">Allocator to use for the queue's memory.</param>
        public NativeDynamicRingQueue(int initialCapacity, Allocator allocator)
        {
            m_Data = new NativeList<T>(initialCapacity, allocator);
            m_Data.Length = initialCapacity;

            m_Head = 0;
            m_Count = 0;
        }

        /// <summary>Whether the current queue is a valid object.</summary>
        public bool IsCreated => m_Data.IsCreated;

        /// <summary>Enqueue an item at the end of the queue.</summary>
        /// <param name="item">Item to enqueue.</param>
        public void Enqueue(T item)
        {
            CheckIsCreated();

            // Check if we need to grow our internal buffer.
            if (m_Count == m_Data.Length)
            {
                if (m_Head == 0)
                {
                    // If the head is at the start of the buffer, we can just resize it.
                    m_Data.ResizeUninitialized(m_Data.Length * 2);
                }
                else
                {
                    // It's not clear from the name, but InsertRange basically shifts everything
                    // from the given index to the right by the given count. So by shifting the head
                    // by the length of the current buffer, we're doubling its capacity while
                    // creating a "hole" in the middle of the buffer where we can keep enqueueing.
                    var originalLength = m_Data.Length;
                    m_Data.InsertRange(m_Head, originalLength);
                    m_Head += originalLength;
                }
            }

            var index = (m_Head + m_Count) % m_Data.Length;
            m_Data[index] = item;
            m_Count++;
        }

        /// <summary>Try to dequeue an item from the front of the queue.</summary>
        /// <param name="item">Dequeued item (default if no items in the queue).</param>
        /// <returns>True if there was an item to dequeue, false otherwise.</returns>
        public bool TryDequeue(out T item)
        {
            CheckIsCreated();

            if (m_Count == 0)
            {
                item = default;
                return false;
            }
            else
            {
                item = m_Data[m_Head];
                m_Head = (m_Head + 1) % m_Data.Length;
                m_Count--;
                return true;
            }
        }

        /// <summary>Try to peek at the front of the queue (don't dequeue it).</summary>
        /// <param name="item">Peeked item (default if no items in the queue).</param>
        /// <returns>True if there was an item in the queue, false otherwise.</returns>
        public bool TryPeek(out T item)
        {
            CheckIsCreated();

            if (m_Count == 0)
            {
                item = default;
                return false;
            }
            else
            {
                item = m_Data[m_Head];
                return true;
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (m_Data.IsCreated)
                m_Data.Dispose();
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        private void CheckIsCreated()
        {
            if (!IsCreated)
                throw new ObjectDisposedException("NativeDynamicRingQueue is not created or has been disposed.");
        }
    }
}