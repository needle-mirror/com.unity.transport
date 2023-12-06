using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Networking.Transport.Logging;
using Unity.Networking.Transport.Utilities;

namespace Unity.Networking.Transport
{
    internal struct SimpleConnectionLayer : INetworkLayer
    {
        internal const byte k_ProtocolVersion = 1;
        internal const int k_HeaderSize = 1 + ConnectionToken.k_Length;
        internal const int k_HandshakeSize = 4 + k_HeaderSize;
        internal const uint k_ProtocolSignatureAndVersion = 0x00505455 | (k_ProtocolVersion << 24); // Reversed for endianness
        private const int k_DeferredSendsQueueSize = 64;

        internal enum ConnectionState
        {
            Default = 0,
            AwaitingAccept,
            Established,
        }

        internal enum HandshakeType : byte
        {
            ConnectionRequest = 1,
            ConnectionAccept = 2,
        }

        internal enum MessageType : byte
        {
            Data = 1,
            Disconnect = 2,
            Heartbeat = 3,
        }

        internal struct SimpleConnectionData
        {
            public ConnectionId UnderlyingConnection;
            public ConnectionToken Token;
            public ConnectionState State;
            public long LastReceiveTime;
            public long LastSendTime;
            public int ConnectionAttempts;
        }

        private ConnectionList m_ConnectionList;
        private ConnectionList m_UnderlyingConnectionList;
        private ConnectionDataMap<SimpleConnectionData> m_ConnectionsData;
        private NativeHashMap<ConnectionToken, ConnectionId> m_TokensHashMap;
        private PacketsQueue m_DeferredSends;

        private int m_ConnectTimeout;
        private int m_DisconnectTimeout;
        private int m_HeartbeatTimeout;
        private int m_MaxConnectionAttempts;

        public int Initialize(ref NetworkSettings settings, ref ConnectionList connectionList, ref int packetPadding)
        {
            packetPadding += k_HeaderSize;

            var networkConfigParameters = settings.GetNetworkConfigParameters();

            if (connectionList.IsCreated)
                m_UnderlyingConnectionList = connectionList;

            m_ConnectTimeout = networkConfigParameters.connectTimeoutMS;
            m_DisconnectTimeout = networkConfigParameters.disconnectTimeoutMS;
            m_HeartbeatTimeout = networkConfigParameters.heartbeatTimeoutMS;
            m_MaxConnectionAttempts = networkConfigParameters.maxConnectAttempts;

            connectionList = m_ConnectionList = ConnectionList.Create();
            m_ConnectionsData = new ConnectionDataMap<SimpleConnectionData>(1, default(SimpleConnectionData), Collections.Allocator.Persistent);
            m_TokensHashMap = new NativeHashMap<ConnectionToken, ConnectionId>(1, Allocator.Persistent);
            m_DeferredSends = new PacketsQueue(k_DeferredSendsQueueSize, networkConfigParameters.maxMessageSize);

            return 0;
        }

        public void Dispose()
        {
            m_ConnectionList.Dispose();
            m_ConnectionsData.Dispose();
            m_TokensHashMap.Dispose();
            m_DeferredSends.Dispose();
        }

        public JobHandle ScheduleReceive(ref ReceiveJobArguments arguments, JobHandle dependency)
        {
            if (m_UnderlyingConnectionList.IsCreated)
            {
                var underlyingConnections = new UnderlyingConnectionList(ref m_UnderlyingConnectionList);
                return ScheduleReceive(new ReceiveJob<UnderlyingConnectionList>(), underlyingConnections, ref arguments, dependency);
            }
            else
            {
                return ScheduleReceive(new ReceiveJob<NullUnderlyingConnectionList>(), default, ref arguments, dependency);
            }
        }

        private JobHandle ScheduleReceive<T>(ReceiveJob<T> job, T underlyingConnectionList, ref ReceiveJobArguments arguments, JobHandle dependency)
            where T : unmanaged, IUnderlyingConnectionList
        {
            job.Connections = m_ConnectionList;
            job.ConnectionsData = m_ConnectionsData;
            job.UnderlyingConnections = underlyingConnectionList;
            job.ReceiveQueue = arguments.ReceiveQueue;
            job.DeferredSends = m_DeferredSends;
            job.TokensHashMap = m_TokensHashMap;
            job.ConnectionPayloads = arguments.ConnectionPayloads;
            job.Time = arguments.Time;
            job.ConnectTimeout = m_ConnectTimeout;
            job.MaxConnectionAttempts = m_MaxConnectionAttempts;
            job.DisconnectTimeout = m_DisconnectTimeout;
            job.HeartbeatTimeout = m_HeartbeatTimeout;

            return job.Schedule(dependency);
        }

        public JobHandle ScheduleSend(ref SendJobArguments arguments, JobHandle dependency)
        {
            return new SendJob
            {
                Connections = m_ConnectionList,
                ConnectionsData = m_ConnectionsData,
                SendQueue = arguments.SendQueue,
                DeferredSends = m_DeferredSends,
                Time = arguments.Time,
            }.Schedule(dependency);
        }

        [BurstCompile]
        private struct SendJob : IJob
        {
            public ConnectionList Connections;
            public ConnectionDataMap<SimpleConnectionData> ConnectionsData;
            public PacketsQueue SendQueue;
            public PacketsQueue DeferredSends;
            public long Time;

            public void Execute()
            {
                // Process all data messages
                var count = SendQueue.Count;
                for (int i = 0; i < count; i++)
                {
                    var packetProcessor = SendQueue[i];
                    if (packetProcessor.Length == 0)
                        continue;

                    var connection = packetProcessor.ConnectionRef;
                    var connectionData = ConnectionsData[connection];
                    var connectionToken = connectionData.Token;

                    packetProcessor.PrependToPayload(connectionToken);
                    packetProcessor.PrependToPayload((byte)MessageType.Data);

                    packetProcessor.ConnectionRef = connectionData.UnderlyingConnection;

                    connectionData.LastSendTime = Time;
                    ConnectionsData[connection] = connectionData;
                }

                // Send all control messages
                var deferredCount = DeferredSends.Count;
                for (int i = 0; i < deferredCount; i++)
                {
                    if (SendQueue.EnqueuePacket(out var packetProcessor))
                    {
                        var deferredPacketProcessor = DeferredSends[i];
                        var connection = deferredPacketProcessor.ConnectionRef;
                        var connectionData = ConnectionsData[connection];

                        packetProcessor.ConnectionRef = connectionData.UnderlyingConnection;
                        packetProcessor.EndpointRef = Connections.GetConnectionEndpoint(connection);
                        packetProcessor.AppendToPayload(deferredPacketProcessor);

                        connectionData.LastSendTime = Time;
                        ConnectionsData[connection] = connectionData;
                    }
                }
                DeferredSends.Clear();
            }
        }

        [BurstCompile]
        internal struct ReceiveJob<T> : IJob where T : unmanaged, IUnderlyingConnectionList
        {
            public ConnectionList Connections;
            public ConnectionDataMap<SimpleConnectionData> ConnectionsData;
            public T UnderlyingConnections;
            public PacketsQueue ReceiveQueue;
            public PacketsQueue DeferredSends;
            public NativeHashMap<ConnectionToken, ConnectionId> TokensHashMap;
            public NativeHashMap<ConnectionId, ConnectionPayload> ConnectionPayloads;
            public long Time;
            public int ConnectTimeout;
            public int DisconnectTimeout;
            public int HeartbeatTimeout;
            public int MaxConnectionAttempts;

            public void Execute()
            {
                ProcessReceivedMessages();
                ProcessConnectionStates();
            }

            private void ProcessConnectionStates()
            {
                // Disconnect if underlying connection is disconnecting.
                var underlyingDisconnections = UnderlyingConnections.QueryIncomingDisconnections(Allocator.Temp);
                var count = underlyingDisconnections.Length;
                for (int i = 0; i < count; i++)
                {
                    var disconnection = underlyingDisconnections[i];

                    var connectionId = FindConnectionByUnderlyingConnection(ref disconnection.Connection);
                    if (!connectionId.IsCreated)
                        continue;

                    var connectionState = Connections.GetConnectionState(connectionId);
                    if (connectionState == NetworkConnection.State.Disconnected ||
                        connectionState == NetworkConnection.State.Disconnecting)
                    {
                        continue;
                    }

                    var connectionData = ConnectionsData[connectionId];

                    Connections.StartDisconnecting(ref connectionId, disconnection.Reason);
                    connectionData.State = ConnectionState.Default;
                    ConnectionsData[connectionId] = connectionData;
                }

                count = Connections.Count;
                for (int i = 0; i < count; i++)
                {
                    var connectionId = Connections.ConnectionAt(i);
                    var connectionState = Connections.GetConnectionState(connectionId);

                    switch (connectionState)
                    {
                        case NetworkConnection.State.Disconnecting:
                            ProcessDisconnecting(ref connectionId);
                            break;
                        case NetworkConnection.State.Connecting:
                            ProcessConnecting(ref connectionId);
                            break;
                        case NetworkConnection.State.Connected:
                            ProcessConnected(ref connectionId);
                            break;
                    }
                }
            }

            private void ProcessDisconnecting(ref ConnectionId connectionId)
            {
                var connectionData = ConnectionsData[connectionId];

                // If we're disconnecting on an established connection, send a disconnect.
                if (connectionData.State == ConnectionState.Established)
                {
                    EnqueueDeferredMessage(connectionId, MessageType.Disconnect, ref connectionData.Token);
                    connectionData.State = ConnectionState.Default;
                    ConnectionsData[connectionId] = connectionData;
                }
                else
                {
                    UnderlyingConnections.Disconnect(ref connectionData.UnderlyingConnection);
                    Connections.FinishDisconnecting(ref connectionId);
                    TokensHashMap.Remove(connectionData.Token);
                }
            }

            private void ProcessConnecting(ref ConnectionId connectionId)
            {
                var connectionData = ConnectionsData[connectionId];
                var connectionState = connectionData.State;

                if (connectionState == ConnectionState.Default)
                {
                    var endpoint = Connections.GetConnectionEndpoint(connectionId);

                    if (UnderlyingConnections.TryConnect(ref endpoint, ref connectionData.UnderlyingConnection))
                    {
                        // The connection was just created, we need to initialize it.
                        connectionData.State = ConnectionState.AwaitingAccept;
                        connectionData.Token = RandomHelpers.GetRandomConnectionToken();
                        connectionData.LastSendTime = Time;
                        connectionData.ConnectionAttempts++;

                        TokensHashMap.Add(connectionData.Token, connectionId);

                        EnqueueDeferredMessage(connectionId, HandshakeType.ConnectionRequest, ref connectionData.Token);

                        ConnectionsData[connectionId] = connectionData;
                        return;
                    }

                    ConnectionsData[connectionId] = connectionData;
                }

                // Check for connect timeout and connection attempts.
                // Note that while connecting, LastSendTime can only track connection requests.
                if (Time - connectionData.LastSendTime > ConnectTimeout)
                {
                    if (connectionData.ConnectionAttempts >= MaxConnectionAttempts)
                    {
                        Connections.StartDisconnecting(ref connectionId, Error.DisconnectReason.MaxConnectionAttempts);
                        ProcessDisconnecting(ref connectionId);
                    }
                    else
                    {
                        connectionData.ConnectionAttempts++;
                        connectionData.LastSendTime = Time;

                        ConnectionsData[connectionId] = connectionData;

                        // Send connect request only if underlying connection has been fully established.
                        if (connectionState == ConnectionState.AwaitingAccept)
                            EnqueueDeferredMessage(connectionId, HandshakeType.ConnectionRequest, ref connectionData.Token);
                    }
                }
            }

            private void ProcessConnected(ref ConnectionId connectionId)
            {
                var connectionData = ConnectionsData[connectionId];

                // Check for the disconnect timeout.
                if (Time - connectionData.LastReceiveTime > DisconnectTimeout)
                {
                    Connections.StartDisconnecting(ref connectionId, Error.DisconnectReason.Timeout);
                    ProcessDisconnecting(ref connectionId);
                }

                // Check for the heartbeat timeout.
                if (HeartbeatTimeout > 0 && Time - connectionData.LastSendTime > HeartbeatTimeout)
                {
                    EnqueueDeferredMessage(connectionId, MessageType.Heartbeat, ref connectionData.Token);
                }
            }

            private void ProcessReceivedMessages()
            {
                var count = ReceiveQueue.Count;
                for (int i = 0; i < count; i++)
                {
                    var packetProcessor = ReceiveQueue[i];

                    if (packetProcessor.Length == 0)
                        continue;

                    if (ProcessHandshakeReceive(ref packetProcessor))
                    {
                        packetProcessor.Drop();
                        continue;
                    }

                    if (packetProcessor.Length < k_HeaderSize)
                    {
                        packetProcessor.Drop();
                        continue;
                    }

                    var messageType = (MessageType)packetProcessor.RemoveFromPayloadStart<byte>();
                    var connectionToken = packetProcessor.RemoveFromPayloadStart<ConnectionToken>();
                    var connectionId = FindConnectionByToken(ref connectionToken);

                    if (!connectionId.IsCreated)
                    {
                        packetProcessor.Drop();
                        continue;
                    }

                    var connectionState = Connections.GetConnectionState(connectionId);
                    var connectionData = ConnectionsData[connectionId];

                    // If we were still waiting on an accept message and receive any message for the
                    // connection, consider the connection accepted. This way we won't drop messages
                    // sent by the server if the accept is lost.
                    if (connectionState == NetworkConnection.State.Connecting &&
                        connectionData.State == ConnectionState.AwaitingAccept)
                    {
                        connectionData.State = ConnectionState.Established;
                        Connections.FinishConnectingFromLocal(ref connectionId);
                        ConnectionPayloads.Remove(connectionId);
                        ConnectionsData[connectionId] = connectionData;
                    }
                    else if (connectionState != NetworkConnection.State.Connected)
                    {
                        packetProcessor.Drop();
                        continue;
                    }

                    switch (messageType)
                    {
                        case MessageType.Disconnect:
                        {
                            Connections.StartDisconnecting(ref connectionId, Error.DisconnectReason.ClosedByRemote);
                            connectionData.State = ConnectionState.Default;
                            ConnectionsData[connectionId] = connectionData;

                            ProcessDisconnecting(ref connectionId);

                            packetProcessor.Drop();
                            break;
                        }
                        case MessageType.Data:
                        {
                            PreprocessMessage(ref connectionId, ref packetProcessor.EndpointRef);
                            packetProcessor.ConnectionRef = connectionId;
                            break;
                        }
                        case MessageType.Heartbeat:
                        {
                            PreprocessMessage(ref connectionId, ref packetProcessor.EndpointRef);
                            packetProcessor.Drop();
                            break;
                        }
                        default:
                            DebugLog.ReceivedMessageWasNotProcessed(messageType);
                            packetProcessor.Drop();
                            break;
                    }
                }
            }

            private void PreprocessMessage(ref ConnectionId connectionId, ref NetworkEndpoint endpoint)
            {
                var connectionData = ConnectionsData[connectionId];

                // Update the endpoint for reconnection, but only if the connection was previously
                // fully establilshed.
                if (connectionData.State == ConnectionState.Established)
                    Connections.UpdateConnectionAddress(ref connectionId, ref endpoint);

                // Any valid message updates last receive time
                connectionData.LastReceiveTime = Time;

                ConnectionsData[connectionId] = connectionData;
            }

            private ConnectionId FindConnectionByToken(ref ConnectionToken token)
            {
                if (TokensHashMap.TryGetValue(token, out var connectionId))
                    return connectionId;

                return default;
            }

            private ConnectionId FindConnectionByUnderlyingConnection(ref ConnectionId underlyingConnection)
            {
                var count = ConnectionsData.Length;
                for (int i = 0; i < count; i++)
                {
                    var connectionData = ConnectionsData.DataAt(i);
                    if (connectionData.UnderlyingConnection == underlyingConnection)
                        return ConnectionsData.ConnectionAt(i);
                }

                return default;
            }

            private unsafe bool ProcessHandshakeReceive(ref PacketProcessor packetProcessor)
            {
                if (packetProcessor.Length < SimpleConnectionLayer.k_HandshakeSize)
                    return false;

                if ((packetProcessor.GetPayloadDataRef<uint>(0) & 0x00FFFFFF) == (k_ProtocolSignatureAndVersion & 0x00FFFFFF))
                {
                    var signatureAndVersion = packetProcessor.RemoveFromPayloadStart<uint>();
                    var protocolVersion = (byte)(signatureAndVersion >> 24);
                    if (protocolVersion != k_ProtocolVersion)
                    {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        DebugLog.ProtocolMismatch(k_ProtocolVersion, protocolVersion);
#endif
                        return true;
                    }

                    var handshakeSeq = (HandshakeType)packetProcessor.RemoveFromPayloadStart<byte>();
                    var connectionToken = packetProcessor.RemoveFromPayloadStart<ConnectionToken>();
                    var connectionId = FindConnectionByToken(ref connectionToken);
                    var connectionData = ConnectionsData[connectionId];
                    switch (handshakeSeq)
                    {
                        case HandshakeType.ConnectionRequest:
                        {
                            // Whole new connection request for a new connection.
                            if (!connectionId.IsCreated)
                            {
                                connectionId = Connections.StartConnecting(ref packetProcessor.EndpointRef);
                                Connections.FinishConnectingFromRemote(ref connectionId);
                                connectionData = new SimpleConnectionData
                                {
                                    State = ConnectionState.Established,
                                    Token = connectionToken,
                                    UnderlyingConnection = packetProcessor.ConnectionRef,
                                };
                                TokensHashMap.Add(connectionToken, connectionId);
                            }

                            // Get the connect payload, if any.
                            if (packetProcessor.Length > SimpleConnectionLayer.k_HandshakeSize + sizeof(ushort))
                            {
                                var length = packetProcessor.RemoveFromPayloadStart<ushort>();
                                if (length != packetProcessor.Length)
                                    break; // Ignore connect payload if length is off.

                                var connectPayload = new ConnectionPayload { Length = length };
                                packetProcessor.CopyPayload(connectPayload.Data, length);

                                ConnectionPayloads[connectionId] = connectPayload;
                            }

                            connectionData.LastSendTime = Time;
                            EnqueueDeferredMessage(connectionId, HandshakeType.ConnectionAccept, ref connectionToken);
                            break;
                        }

                        case HandshakeType.ConnectionAccept:
                        {
                            if (connectionId.IsCreated && connectionData.State == ConnectionState.AwaitingAccept)
                            {
                                connectionData.State = ConnectionState.Established;
                                Connections.FinishConnectingFromLocal(ref connectionId);
                                ConnectionPayloads.Remove(connectionId);
                            }
                            else
                            {
                                // Received a connection accept for an unknown connection
                                return true;
                            }

                            break;
                        }

                        // We got a malformed packet
                        default:
                            return true;
                    }

                    connectionData.LastReceiveTime = Time;
                    ConnectionsData[connectionId] = connectionData;
                    return true;
                }

                return false;
            }

            private unsafe void EnqueueDeferredMessage(ConnectionId connection, HandshakeType type, ref ConnectionToken token)
            {
                if (DeferredSends.EnqueuePacket(out var packetProcessor))
                {
                    packetProcessor.ConnectionRef = connection;
                    packetProcessor.AppendToPayload(k_ProtocolSignatureAndVersion);
                    packetProcessor.AppendToPayload(type);
                    packetProcessor.AppendToPayload(token);

                    if (type == HandshakeType.ConnectionRequest &&
                        ConnectionPayloads.TryGetValue(connection, out var payload) &&
                        payload.Length > 0)
                    {
                        packetProcessor.AppendToPayload((ushort)payload.Length);
                        packetProcessor.AppendToPayload(payload.Data, payload.Length);
                    }
                }
            }

            private void EnqueueDeferredMessage(ConnectionId connection, MessageType type, ref ConnectionToken token)
            {
                if (DeferredSends.EnqueuePacket(out var packetProcessor))
                {
                    packetProcessor.ConnectionRef = connection;
                    packetProcessor.AppendToPayload(type);
                    packetProcessor.AppendToPayload(token);
                }
            }
        }
    }
}
