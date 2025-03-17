using System;
using Unity.Collections;
using Unity.Networking.Transport.Logging;
using Unity.Baselib.LowLevel;
using Unity.Networking.Transport.Error;
using UnityEngine;

namespace Unity.Networking.Transport
{
    /// <summary>
    /// Provides an API for managing the NetworkDriver connections.
    /// </summary>
    internal struct ConnectionList : IDisposable
    {

#if HOSTNAME_RESOLUTION_AVAILABLE
        internal unsafe struct HostnameLookupTask
        {
            private Binding.Baselib_NetworkAddress_HostnameLookupHandle* m_LookupHandle;

            private FixedString512Bytes m_Address;

            private int RetryCount;

            public bool IsCreated => m_LookupHandle != null;

            public static bool Create(FixedString512Bytes address, out HostnameLookupTask task, out Binding.Baselib_NetworkAddress result, out Binding.Baselib_ErrorState error)
            {
                task = new HostnameLookupTask();
                return task.Initialize(address, out result, out error);
            }

            private bool Initialize(FixedString512Bytes address, out Binding.Baselib_NetworkAddress result, out Binding.Baselib_ErrorState error)
            {
                var addressBytes = address.GetUnsafePtr();
                var errorState = default(Binding.Baselib_ErrorState);
                var localResult = default(Binding.Baselib_NetworkAddress);
                Binding.Baselib_NetworkAddress_HostnameLookupHandle* handle;

                do
                {
                    if (RetryCount > 10)
                    {
                        errorState.code = Binding.Baselib_ErrorCode.NoSupportedAddressFound;
                        error = errorState;
                        result = default;
                        return true;
                    }
                    handle = Binding.Baselib_NetworkAddress_HostnameLookup(addressBytes, &localResult, &errorState);
                    ++RetryCount;
                } while (errorState.code == Binding.Baselib_ErrorCode.TryAgain);

                error = errorState;
                if (errorState.code == Binding.Baselib_ErrorCode.Success)
                {
                    result = localResult;
                    m_LookupHandle = handle;
                    m_Address = address;
                    return true;
                }

                if (handle != null)
                {
                    result = default;
                    return true;
                }

                result = default;
                return false;
            }

            public bool CheckStatus(out Binding.Baselib_NetworkAddress result, out Binding.Baselib_ErrorState error)
            {
                var errorState = default(Binding.Baselib_ErrorState);
                var localResult = default(Binding.Baselib_NetworkAddress);
                var finished = Binding.Baselib_NetworkAddress_HostnameLookupCheckStatus(m_LookupHandle, &localResult, &errorState);
                if (finished)
                {
                    if (errorState.code == Binding.Baselib_ErrorCode.TryAgain)
                    {
                        if (RetryCount > 10)
                        {
                            errorState.code = Binding.Baselib_ErrorCode.NoSupportedAddressFound;
                            error = errorState;
                            result = default;
                            return true;
                        }
                        return Initialize(m_Address, out result, out error);
                    }
                    error = errorState;
                    if (errorState.code == Binding.Baselib_ErrorCode.Success)
                    {
                        result = localResult;
                    }
                    else
                    {
                        result = default;
                    }

                    m_LookupHandle = null;
                    return true;
                }

                result = default;
                error = default;
                return false;
            }

        }

        private struct HostnameLookupData
        {
            public HostnameLookupTask Task;
            public ushort Port;
        }
#endif

        private struct ConnectionData
        {
            public NetworkEndpoint Endpoint;
            public NetworkConnection.State State;
            // TODO: Eventually this field needs to be moved out of ConnectionData and become part of a ConnectionDataMap
            // in a new PathMTULayer that will sit above SimpleConnectionLayer.
            // At the current time, this field is populated by SimpleConnectionLayer as the last step of the connection
            // handshake, and shouldn't be accessed by any other layers. Additionally if any new layer is added above
            // SimpleConnectionLayer that replaces NetworkDriver's ConnectionList, the data in this field needs to be
            // propagated from SimpleConnectionList's ConnectionList up to NetworkDriver's.
            public int PathMtu;
        }

        internal struct IncomingDisconnection
        {
            public ConnectionId Connection;
            public Error.DisconnectReason Reason;
        }

        private ConnectionDataMap<ConnectionData> m_Connections;

#if HOSTNAME_RESOLUTION_AVAILABLE
        private NativeHashMap<int, HostnameLookupData> m_HostnameLookupTasks;
#endif

        /// <summary>
        /// Stores all connections with an impending disconnection.
        /// </summary>
        private NativeQueue<IncomingDisconnection> m_IncomingDisconnections;

        /// <summary>
        /// Stores all connections (not requested by the remote endpoint) that completed the connection.
        /// </summary>
        private NativeQueue<ConnectionId> m_FinishedConnections;

        /// <summary>
        /// Stores all connections (requested by the remote endpoint) that completed the connection.
        /// </summary>
        private NativeQueue<ConnectionId> m_IncomingConnections;

        /// <summary>
        /// Stores all connections that can be created by reusing a previously released slot.
        /// </summary>
        private NativeQueue<ConnectionId> m_FreeList;

        /// <summary>
        /// The current count of connections.
        /// </summary>
        public int Count => m_Connections.Length;

        public bool IsCreated => m_Connections.IsCreated;

        internal NativeQueue<ConnectionId> FreeList => m_FreeList;

        internal ConnectionId ConnectionAt(int index) => m_Connections.ConnectionAt(index);
        internal NetworkEndpoint GetConnectionEndpoint(ConnectionId connectionId) => m_Connections[connectionId].Endpoint;
        internal NetworkConnection.State GetConnectionState(ConnectionId connectionId) => m_Connections[connectionId].State;
        
        internal int GetConnectionPathMtu(ConnectionId connectionId) => m_Connections[connectionId].PathMtu;
        internal void SetConnectionPathMtu(ConnectionId connectionId, int pathMtu)
        {
            var data = m_Connections[connectionId];
            data.PathMtu = pathMtu;
            m_Connections[connectionId] = data;
        }

        internal NativeArray<ConnectionId> QueryFinishedConnections(Allocator allocator) => m_FinishedConnections.ToArray(allocator);
        internal NativeArray<ConnectionId> QueryIncomingConnections(Allocator allocator) => m_IncomingConnections.ToArray(allocator);
        internal NativeArray<IncomingDisconnection> QueryIncomingDisconnections(Allocator allocator) => m_IncomingDisconnections.ToArray(allocator);

        public static ConnectionList Create()
        {
            return new ConnectionList(Allocator.Persistent);
        }

        private ConnectionList(Allocator allocator)
        {
            var defaultConnectionData = new ConnectionData { State = NetworkConnection.State.Disconnected, PathMtu = NetworkParameterConstants.AbsoluteMinimumMtuSize };
            m_Connections = new ConnectionDataMap<ConnectionData>(1, defaultConnectionData, allocator);
#if HOSTNAME_RESOLUTION_AVAILABLE
            m_HostnameLookupTasks = new NativeHashMap<int, HostnameLookupData>(1, allocator);
#endif
            m_IncomingDisconnections = new NativeQueue<IncomingDisconnection>(allocator);
            m_FinishedConnections = new NativeQueue<ConnectionId>(allocator);
            m_FreeList = new NativeQueue<ConnectionId>(allocator);
            m_IncomingConnections = new NativeQueue<ConnectionId>(allocator);
        }

        public void Dispose()
        {
            m_Connections.Dispose();
#if HOSTNAME_RESOLUTION_AVAILABLE
            m_HostnameLookupTasks.Dispose();
#endif
            m_IncomingDisconnections.Dispose();
            m_FinishedConnections.Dispose();
            m_IncomingConnections.Dispose();
            m_FreeList.Dispose();
        }

        private ConnectionId GetNewConnection()
        {
            if (m_FreeList.TryDequeue(out var connectionId))
            {
                // There is one free connection slot that we can reuse
                // its version has been already increased.
                return connectionId;
            }
            else
            {
                return new ConnectionId
                {
                    Id = m_Connections.Length,
                    Version = 1,
                };
            }
        }

        /// <summary>
        /// Creates a new connection to the provided address and sets its state to Connecting.
        /// </summary>
        /// <param name="address">The endpoint to connect to.</param>
        /// <returns>Returns the ConnectionId identifier for the new created connection.</returns>
        /// <remarks>The connection is going to be fully connected only when FinishConnecting() is called.</remarks>
        internal ConnectionId StartConnecting(ref NetworkEndpoint address)
        {
            var connection = GetNewConnection();

            m_Connections[connection] = new ConnectionData
            {
                Endpoint = address,
                State = NetworkConnection.State.Connecting,
                PathMtu = NetworkParameterConstants.AbsoluteMinimumMtuSize
            };

            return connection;
        }

#if HOSTNAME_RESOLUTION_AVAILABLE
        /// <summary>
        /// Creates a new connection to the provided address and sets its state to Connecting.
        /// </summary>
        /// <param name="address">The endpoint to connect to.</param>
        /// <returns>Returns the ConnectionId identifier for the new created connection.</returns>
        /// <remarks>The connection is going to be fully connected only when FinishConnecting() is called.</remarks>
        internal ConnectionId StartConnecting(FixedString512Bytes address, ushort port, out bool hostnameLookupFinished, out NetworkEndpoint resolvedEndpoint, out DisconnectReason disconnectReason)
        {
            var connection = GetNewConnection();

            var success = HostnameLookupTask.Create(address, out var task, out var result, out var error);
            if (!success)
            {
                // TODO: Figure out how to handle errors.
            }

            if (task.IsCreated)
            {
                resolvedEndpoint = default;
                m_Connections[connection] = new ConnectionData
                {
                    State = NetworkConnection.State.Connecting,
                    PathMtu = NetworkParameterConstants.AbsoluteMinimumMtuSize
                };
                m_HostnameLookupTasks[connection.Id] = new HostnameLookupData
                {
                    Task = task,
                    Port = port
                };
                hostnameLookupFinished = false;
                disconnectReason = DisconnectReason.HostNotFound;
            }
            else
            {
                resolvedEndpoint = new NetworkEndpoint(result);
                resolvedEndpoint.Port = port;
                m_Connections[connection] = new ConnectionData
                {
                    Endpoint = resolvedEndpoint,
                    State = NetworkConnection.State.Connecting,
                    PathMtu = NetworkParameterConstants.AbsoluteMinimumMtuSize
                };
                hostnameLookupFinished = true;
                disconnectReason = DisconnectReason.Default;
            }

            return connection;
        }

        internal bool CheckHostnameLookupStatus(ref ConnectionId connectionId, out NetworkEndpoint resolvedEndpoint, out DisconnectReason disconnectReason)
        {
            var connectionData = m_Connections[connectionId];
            var hostnameLookupData = m_HostnameLookupTasks[connectionId.Id];
            if (connectionData.State != NetworkConnection.State.Connecting || !hostnameLookupData.Task.IsCreated)
            {
                resolvedEndpoint = default;
                disconnectReason = DisconnectReason.Default;
                return true;
            }

            var finished = hostnameLookupData.Task.CheckStatus(out var result, out var error);
            if (finished)
            {
                if (error.code != Binding.Baselib_ErrorCode.Success)
                {
                    disconnectReason = DisconnectReason.HostNotFound;
                    resolvedEndpoint = default;
                    return true;
                }

                connectionData.Endpoint = new NetworkEndpoint(result);
                connectionData.Endpoint.Port = hostnameLookupData.Port;
                m_Connections[connectionId] = connectionData;

                m_HostnameLookupTasks.Remove(connectionId.Id);
                resolvedEndpoint = connectionData.Endpoint;
                disconnectReason = DisconnectReason.Default;
                return true;
            }

            disconnectReason = DisconnectReason.Default;
            resolvedEndpoint = default;
            return false;
        }
#endif

        /// <summary>
        /// Completes a connection started by the local endpoint in Connecting state by setting it to Connected.
        /// </summary>
        /// <param name="connectionId">The connecting connection to be completed.</param>
        internal void FinishConnectingFromLocal(ref ConnectionId connectionId)
        {
            // TODO: we might want to restric the connection completion to the layer that
            // owns the connection list.

            CompleteConnecting(ref connectionId);
            m_FinishedConnections.Enqueue(connectionId);
        }

        /// <summary>
        /// Completes a connection started by the remote endpoint in Connecting state by setting it to Connected.
        /// </summary>
        /// <param name="connectionId">The connecting connection to be completed.</param>
        internal void FinishConnectingFromRemote(ref ConnectionId connectionId)
        {
            // TODO: we might want to restric the connection completion to the layer that
            // owns the connection list.

            CompleteConnecting(ref connectionId);
            m_IncomingConnections.Enqueue(connectionId);
        }

        private void CompleteConnecting(ref ConnectionId connectionId)
        {
            var connectionData = m_Connections[connectionId];

            if (connectionData.State != NetworkConnection.State.Connecting)
                return;

            connectionData.State = NetworkConnection.State.Connected;
            m_Connections[connectionId] = connectionData;
        }

        internal ConnectionId AcceptConnection()
        {
            if (!m_IncomingConnections.TryDequeue(out var connectionId))
                return default;

            var connectionState = GetConnectionState(connectionId);
            if (connectionState != NetworkConnection.State.Connected)
            {
                DebugLog.ConnectionAcceptWrongState(connectionId, connectionState);
                return default;
            }

            return connectionId;
        }

        internal bool IsConnectionAccepted(ref ConnectionId connectionId)
        {
            if (m_IncomingConnections.Count == 0)
                return true;

            var unacceptedConnections = QueryIncomingConnections(Allocator.Temp);
            if (unacceptedConnections.Contains(connectionId))
                return false;

            return true;
        }

        /// <summary>
        /// Sets the state of the connection to Disconnecting.
        /// </summary>
        /// <param name="connectionId">The connection to disconnect.</param>
        /// <param name="reason">The disconnect reason.</param>
        /// <remarks>
        /// The connection is going to be fully disconnected only when FinishDisconnecting is
        /// called. A Disconnect event with the provided reason will be enqueued at the begining of
        /// the next ScheduleUpdate call.
        /// </remarks>
        internal void StartDisconnecting(ref ConnectionId connectionId, Error.DisconnectReason reason = Error.DisconnectReason.Default)
        {
            var connectionData = m_Connections[connectionId];

            if (connectionData.State == NetworkConnection.State.Disconnected ||
                connectionData.State == NetworkConnection.State.Disconnecting)
            {
                DebugLog.LogWarning("Attempting to disconnect an already disconnected connection");
                return;
            }

            m_IncomingDisconnections.Enqueue(new IncomingDisconnection
            {
                Connection = connectionId,
                Reason = reason,
            });

            connectionData.State = NetworkConnection.State.Disconnecting;
            m_Connections[connectionId] = connectionData;
        }

        /// <summary>
        /// Completes a disconnection by setting the state of the connection to Disconnected.
        /// </summary>
        /// <param name="connectionId">The disconnecting connection to be completed.</param>
        /// <remarks>
        /// The connection's ID will be freed up at the beginning of the next ScheduleUpdate call.
        /// </remarks>
        internal void FinishDisconnecting(ref ConnectionId connectionId)
        {
            var connectionData = m_Connections[connectionId];

            if (connectionData.State != NetworkConnection.State.Disconnecting)
            {
                DebugLog.ConnectionFinishWrongState(connectionData.State);
                return;
            }

            connectionData.State = NetworkConnection.State.Disconnected;
            m_Connections[connectionId] = connectionData;
        }

        /// <summary>
        /// Cleanup of queues for connections/disconnections that has been completed.
        /// </summary>
        internal void Cleanup()
        {
            m_FinishedConnections.Clear();
            m_IncomingDisconnections.Clear();

            // If the free list is empty, add all the connections that are disconnected. It would be
            // better to just always immediately add disconnected connections, but then we'd have to
            // be careful about not adding connections that are already in the free list. That could
            // get expensive so we just lazily add connections when we're likely to need new ones
            // (that is, when there's nothing in the free list).
            if (m_FreeList.Count == 0)
            {
                for (int i = 0; i < Count; i++)
                {
                    var connectionId = ConnectionAt(i);
                    var connectionData = m_Connections[connectionId];

                    if (connectionData.State == NetworkConnection.State.Disconnected)
                    {
                        connectionId.Version++;
                        m_Connections.ClearData(ref connectionId);
                        m_FreeList.Enqueue(connectionId);
                    }
                }
            }
        }

        internal void UpdateConnectionAddress(ref ConnectionId connection, ref NetworkEndpoint address)
        {
            var connectionData = m_Connections[connection];
            if (connectionData.Endpoint != address)
            {
                connectionData.Endpoint = address;
                m_Connections[connection] = connectionData;
            }
        }

        public override bool Equals(object obj)
        {
            return obj is ConnectionList list &&
                this == list;
        }

        public override int GetHashCode()
        {
            return m_Connections.GetHashCode();
        }

        public static unsafe bool operator==(ConnectionList a, ConnectionList b)
        {
            return a.m_Connections == b.m_Connections;
        }

        public static unsafe bool operator!=(ConnectionList a, ConnectionList b)
        {
            return !(a == b);
        }
    }
}
