using System;
using Unity.Collections;

namespace Unity.Networking.Transport
{
    /// <summary>
    /// A generic map where the key is a ConnectionId and stored values correspond to
    /// the last valid connection version. Disconnected connections return the defaultDataValue.
    /// </summary>
    /// <typeparam name="T">Type of the data to store per connection.</typeparam>
    internal struct ConnectionDataMap<T> : IDisposable where T : unmanaged
    {
        private struct ConnectionSlot
        {
            public int Version;
            public T Value;
        }

        private NativeList<ConnectionSlot> m_List;
        private T m_DefaultDataValue;

        public bool IsCreated => m_List.IsCreated;
        public int Length => m_List.Length;

        internal ConnectionDataMap(int initialCapacity, T defaultDataValue, Allocator allocator)
        {
            m_DefaultDataValue = defaultDataValue;
            m_List = new NativeList<ConnectionSlot>(initialCapacity, allocator);
        }

        public void Dispose()
        {
            m_List.Dispose();
        }

        internal T this[ConnectionId connection]
        {
            get
            {
                if (connection.Id >= m_List.Length || connection.Id < 0)
                    return m_DefaultDataValue;

                var slot = m_List[connection.Id];

                if (slot.Version != connection.Version)
                    return m_DefaultDataValue;

                return slot.Value;
            }
            set
            {
                if (connection.Id >= m_List.Length)
                    m_List.Resize(connection.Id + 1, NativeArrayOptions.ClearMemory);

                ref var slot = ref m_List.ElementAt(connection.Id);

                if (slot.Version > connection.Version)
                {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    throw new ArgumentOutOfRangeException("The provided connection is not valid");
#else
                    UnityEngine.Debug.LogError("The provided connection is not valid");
                    return;
#endif
                }

                slot.Version = connection.Version;
                slot.Value = value;
            }
        }

        internal void ClearData(ref ConnectionId connection)
        {
            this[connection] = m_DefaultDataValue;
        }

        internal ConnectionId ConnectionAt(int index)
        {
            if (index < 0 || index > m_List.Length)
                return default;

            return new ConnectionId
            {
                Id = index,
                Version = m_List[index].Version,
            };
        }

        internal T DataAt(int index)
        {
            if (index < 0 || index >= m_List.Length)
                return m_DefaultDataValue;

            return m_List[index].Value;
        }

        public override bool Equals(object obj)
        {
            return obj is ConnectionDataMap<T> map &&
                this == map;
        }

        public override unsafe int GetHashCode()
        {
            return ((int)m_List.GetUnsafeList()).GetHashCode();
        }

        public static unsafe bool operator==(ConnectionDataMap<T> a, ConnectionDataMap<T> b)
        {
            return a.m_List.GetUnsafeList() == b.m_List.GetUnsafeList();
        }

        public static unsafe bool operator!=(ConnectionDataMap<T> a, ConnectionDataMap<T> b)
        {
            return !(a == b);
        }
    }
}
