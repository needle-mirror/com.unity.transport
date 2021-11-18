using System;
using System.Runtime.InteropServices;
using System.Threading;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Unity.Networking.Transport
{
    /// <summary>Represents an event on a connection.</summary>
    [StructLayout(LayoutKind.Explicit)]
    public struct NetworkEvent
    {
        /// <summary>The different types of events that can be returned for a connection.</summary>
        public enum Type : short
        {
            /// <summary>No event actually occured. Should be ignored.</summary>
            Empty = 0,
            /// <summary>Data was received on the connection.</summary>
            Data,
            /// <summary>The connection is now established.</summary>
            Connect,
            /// <summary>The connection is now closed.</summary>
            Disconnect
        }

        /// <summary>The type of the event.</summary>
        [FieldOffset(0)] internal Type type;
        
        /// <summary>The pipeline on which the event was received (for Data events).</summary>
        [FieldOffset(2)] internal short pipelineId;
        
        /// <summary>Internal ID of the connection.</summary>
        [FieldOffset(4)] internal int connectionId;

        /// <summary>Status of the event. Used to store the Disconnect reason.</summary>
        [FieldOffset(8)] internal int status;

        /// <summary>Offset of the event's data in the internal data stream.</summary>
        [FieldOffset(8)] internal int offset;
        
        /// <summary>Size of the event's data.</summary>
        [FieldOffset(12)] internal int size;
    }

    /// <summary>A queue to store <see cref="NetworkEvent"> per connection.</summary>
    internal struct NetworkEventQueue : IDisposable
    {
        private int MaxEvents
        {
            get { return m_ConnectionEventQ.Length / (m_ConnectionEventHeadTail.Length / 2); }
        }
        
        /// <summary>
        /// Initializes a new instance of a <see cref="NetworkEventQueue"/>.
        /// </summary>
        /// <param name="queueSizePerConnection">The queue size per connection.</param>
        public NetworkEventQueue(int queueSizePerConnection)
        {
            m_MasterEventQ = new NativeQueue<SubQueueItem>(Allocator.Persistent);
            m_ConnectionEventQ = new NativeList<NetworkEvent>(queueSizePerConnection, Allocator.Persistent);
            m_ConnectionEventHeadTail = new NativeList<int>(2, Allocator.Persistent);
            m_ConnectionEventQ.ResizeUninitialized(queueSizePerConnection);
            m_ConnectionEventHeadTail.Add(0);
            m_ConnectionEventHeadTail.Add(0);
        }

        /// <summary>
        /// Disposes of the queue.
        /// </summary>
        public void Dispose()
        {
            m_MasterEventQ.Dispose();
            m_ConnectionEventQ.Dispose();
            m_ConnectionEventHeadTail.Dispose();
        }

        /// <summary>
        /// Pops an event from the queue. The returned stream is only valid until the call to the
        /// method or until the main driver updates.
        /// </summary>
        /// <param name="id">ID of the connection the event is on.</param>
        /// <param name="offset">Offset of the event's data in the stream.</param>
        /// <param name="size">Size of the event's data in the stream.</param>
        /// <returns>The <see cref="NetworkEvent.Type"> of the event.</returns>
        public NetworkEvent.Type PopEvent(out int id, out int offset, out int size)
        {
            return PopEvent(out id, out offset, out size, out var _);
        }

        /// <summary>
        /// Pops an event from the queue. The returned stream is only valid until the call to the
        /// method or until the main driver updates.
        /// </summary>
        /// <param name="id">ID of the connection the event is on.</param>
        /// <param name="offset">Offset of the event's data in the stream.</param>
        /// <param name="size">Size of the event's data in the stream.</param>
        /// <param name="pipelineId">Pipeline on which the data event was received.</param>
        /// <returns>The <see cref="NetworkEvent.Type"> of the event.</returns>
        public NetworkEvent.Type PopEvent(out int id, out int offset, out int size, out int pipelineId)
        {
            offset = 0;
            size = 0;
            id = -1;
            pipelineId = 0;

            while (true)
            {
                SubQueueItem ev;
                if (!m_MasterEventQ.TryDequeue(out ev))
                {
                    return NetworkEvent.Type.Empty;
                }

                if (m_ConnectionEventHeadTail[ev.connection * 2] == ev.idx)
                {
                    id = ev.connection;
                    return PopEventForConnection(ev.connection, out offset, out size, out pipelineId);
                }
            }
        }

        /// <summary>
        /// Pops an event from the queue for a specific connection. The returned stream is only
        /// valid until the call to the method or until the main driver updates.
        /// </summary>
        /// <param name="connectionId">ID of the connection we want the event from.</param>
        /// <param name="offset">Offset of the event's data in the stream.</param>
        /// <param name="size">Size of the event's data in the stream.</param>
        /// <returns>The <see cref="NetworkEvent.Type"> of the event.</returns>
        public NetworkEvent.Type PopEventForConnection(int connectionId, out int offset, out int size)
        {
            return PopEventForConnection(connectionId, out offset, out size, out var _);
        }

        /// <summary>
        /// Pops an event from the queue for a specific connection. The returned stream is only
        /// valid until the call to the method or until the main driver updates.
        /// </summary>
        /// <param name="connectionId">ID of the connection we want the event from.</param>
        /// <param name="offset">Offset of the event's data in the stream.</param>
        /// <param name="size">Size of the event's data in the stream.</param>
        /// <param name="pipelineId">Pipeline on which the data event was received.</param>
        /// <returns>The <see cref="NetworkEvent.Type"> of the event.</returns>
        public NetworkEvent.Type PopEventForConnection(int connectionId, out int offset, out int size, out int pipelineId)
        {
            offset = 0;
            size = 0;
            pipelineId = 0;

            if (connectionId < 0 || connectionId >= m_ConnectionEventHeadTail.Length / 2)
                return NetworkEvent.Type.Empty;

            int idx = m_ConnectionEventHeadTail[connectionId * 2];

            if (idx >= m_ConnectionEventHeadTail[connectionId * 2 + 1])
                return NetworkEvent.Type.Empty;

            m_ConnectionEventHeadTail[connectionId * 2] = idx + 1;
            NetworkEvent ev = m_ConnectionEventQ[connectionId * MaxEvents + idx];
            pipelineId = ev.pipelineId;

            if (ev.type == NetworkEvent.Type.Data)
            {
                offset = ev.offset;
                size = ev.size;
            }
            else if (ev.type == NetworkEvent.Type.Disconnect && ev.status != (int)Error.DisconnectReason.Default)
            {
                offset = -ev.status;
            }
            return ev.type;
        }

        /// <summary>
        /// Get the number of events in the queue for a given connection.
        /// </summary>
        /// <param name="connectionId">The ID of the connection to get event count of.</param>
        /// <returns>The number of events for the connection.</returns>
        public int GetCountForConnection(int connectionId)
        {
            if (connectionId < 0 || connectionId >= m_ConnectionEventHeadTail.Length / 2)
                return 0;
            return m_ConnectionEventHeadTail[connectionId * 2 + 1] - m_ConnectionEventHeadTail[connectionId * 2];
        }

        /// ::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::
        /// internal helper functions ::::::::::::::::::::::::::::::::::::::::::
        public void PushEvent(NetworkEvent ev)
        {
            int curMaxEvents = MaxEvents;
            if (ev.connectionId >= m_ConnectionEventHeadTail.Length / 2)
            {
                // Connection id out of range, grow the number of connections in the queue
                int oldSize = m_ConnectionEventHeadTail.Length;
                m_ConnectionEventHeadTail.ResizeUninitialized((ev.connectionId + 1) * 2);
                for (; oldSize < m_ConnectionEventHeadTail.Length; ++oldSize)
                    m_ConnectionEventHeadTail[oldSize] = 0;
                m_ConnectionEventQ.ResizeUninitialized((m_ConnectionEventHeadTail.Length / 2) * curMaxEvents);
            }
            int idx = m_ConnectionEventHeadTail[ev.connectionId * 2 + 1];
            if (idx >= curMaxEvents)
            {
                // Grow the max items per queue and remap the queues
                int oldMax = curMaxEvents;
                while (idx >= curMaxEvents)
                    curMaxEvents *= 2;
                int maxConnections = m_ConnectionEventHeadTail.Length / 2;
                m_ConnectionEventQ.ResizeUninitialized(maxConnections * curMaxEvents);
                for (int con = maxConnections - 1; con >= 0; --con)
                {
                    for (int i = m_ConnectionEventHeadTail[con * 2 + 1] - 1; i >= m_ConnectionEventHeadTail[con * 2]; --i)
                    {
                        m_ConnectionEventQ[con * curMaxEvents + i] = m_ConnectionEventQ[con * oldMax + i];
                    }
                }
            }

            m_ConnectionEventQ[ev.connectionId * curMaxEvents + idx] = ev;
            m_ConnectionEventHeadTail[ev.connectionId * 2 + 1] = idx + 1;

            m_MasterEventQ.Enqueue(new SubQueueItem {connection = ev.connectionId, idx = idx});
        }

        internal void Clear()
        {
            m_MasterEventQ.Clear();
            for (int i = 0; i < m_ConnectionEventHeadTail.Length; ++i)
            {
                m_ConnectionEventHeadTail[i] = 0;
            }
        }

        struct SubQueueItem
        {
            public int connection;
            public int idx;
        }

        private NativeQueue<SubQueueItem> m_MasterEventQ;
        private NativeList<NetworkEvent> m_ConnectionEventQ;
        private NativeList<int> m_ConnectionEventHeadTail;
        
        public Concurrent ToConcurrent()
        {
            Concurrent concurrent;
            concurrent.m_ConnectionEventQ = m_ConnectionEventQ;
            concurrent.m_ConnectionEventHeadTail = new Concurrent.ConcurrentConnectionQueue(m_ConnectionEventHeadTail);
            return concurrent;
        }
        
        public struct Concurrent
        {
            [NativeContainer]
            [NativeContainerIsAtomicWriteOnly]
            internal unsafe struct ConcurrentConnectionQueue
            {
                [NativeDisableUnsafePtrRestriction] private UnsafeList<int>* m_ConnectionEventHeadTail;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                private AtomicSafetyHandle m_Safety;
#endif
                public ConcurrentConnectionQueue(NativeList<int> queue)
                {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    m_Safety = NativeListUnsafeUtility.GetAtomicSafetyHandle(ref queue);
                    AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
#endif
                    m_ConnectionEventHeadTail = (UnsafeList<int>*)NativeListUnsafeUtility.GetInternalListDataPtrUnchecked(ref queue);
                }

                public int Length
                {
                    get { return m_ConnectionEventHeadTail->Length; }
                }

                public int Dequeue(int connectionId)
                {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
#endif
                    int idx = -1;
                    if (connectionId < 0 || connectionId >= m_ConnectionEventHeadTail->Length / 2)
                        return -1;
                    while (idx < 0)
                    {
                        idx = ((int*)m_ConnectionEventHeadTail->Ptr)[connectionId * 2];
                        if (idx >= ((int*)m_ConnectionEventHeadTail->Ptr)[connectionId * 2 + 1])
                            return -1;
                        if (Interlocked.CompareExchange(ref ((int*)m_ConnectionEventHeadTail->Ptr)[connectionId * 2], idx + 1,
                            idx) != idx)
                            idx = -1;
                    }

                    return idx;
                }
            }
            private int MaxEvents
            {
                get { return m_ConnectionEventQ.Length / (m_ConnectionEventHeadTail.Length / 2); }
            }

            /// <summary>
            /// Pops an event from the queue for a specific connection. The returned stream is only
            /// valid until the call to the method or until the main driver updates.
            /// </summary>
            /// <param name="connectionId">ID of the connection we want the event from.</param>
            /// <param name="offset">Offset of the event's data in the stream.</param>
            /// <param name="size">Size of the event's data in the stream.</param>
            /// <returns>The <see cref="NetworkEvent.Type"> of the event.</returns>
            public NetworkEvent.Type PopEventForConnection(int connectionId, out int offset, out int size)
            {
                return PopEventForConnection(connectionId, out offset, out size, out var _);
            }

            /// <summary>
            /// Pops an event from the queue for a specific connection. The returned stream is only
            /// valid until the call to the method or until the main driver updates.
            /// </summary>
            /// <param name="connectionId">ID of the connection we want the event from.</param>
            /// <param name="offset">Offset of the event's data in the stream.</param>
            /// <param name="size">Size of the event's data in the stream.</param>
            /// <param name="pipelineId">Pipeline on which the data event was received.</param>
            /// <returns>The <see cref="NetworkEvent.Type"> of the event.</returns>
            public NetworkEvent.Type PopEventForConnection(int connectionId, out int offset, out int size, out int pipelineId)
            {
                offset = 0;
                size = 0;
                pipelineId = 0;

                int idx = m_ConnectionEventHeadTail.Dequeue(connectionId);
                if (idx < 0)
                    return NetworkEvent.Type.Empty;
                NetworkEvent ev = m_ConnectionEventQ[connectionId * MaxEvents + idx];
                pipelineId = ev.pipelineId;

                if (ev.type == NetworkEvent.Type.Data)
                {
                    offset = ev.offset;
                    size = ev.size;
                }
                else if (ev.type == NetworkEvent.Type.Disconnect && ev.status != (int)Error.DisconnectReason.Default)
                {
                    offset = -ev.status;
                }

                return ev.type;
            }

            [ReadOnly] internal NativeList<NetworkEvent> m_ConnectionEventQ;
            internal ConcurrentConnectionQueue m_ConnectionEventHeadTail;
        }
    }
}
