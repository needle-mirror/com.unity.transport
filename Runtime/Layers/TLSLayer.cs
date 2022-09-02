#if ENABLE_MANAGED_UNITYTLS

using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Networking.Transport.TLS;
using Unity.TLS.LowLevel;
using UnityEngine;

namespace Unity.Networking.Transport
{
    internal unsafe struct TLSLayer : INetworkLayer
    {
        // The deferred queue has to be quite large because if we fill it up, a bug in UnityTLS
        // causes the session to fail. Once MTT-3971 is fixed, we can lower this value. Although
        // unlike with DTLS, we likely don't want to decrease it too much since with TLS the
        // certificate chains could be much larger and require more deferred sends.
        private const int k_DeferredSendsQueueSize = 64;

        // Padding used by TLS records. Since UnityTLS creates a record everytime we call
        // unitytls_client_send_data, each packet in the send queue must be ready to accomodate the
        // overhead of a TLS record. The overhead is 5 bytes for the record header, 32 bytes for the
        // MAC (typically 20 bytes, but can be 32 in TLS 1.2), and up to 31 bytes to pad to the
        // block size (for block ciphers, which are commonly 16 bytes but can be up to 32 for the
        // ciphers supported by UnityTLS).
        //
        // TODO See if we can limit what UnityTLS is willing to support in terms of ciphers so that
        //      we can reduce this value (we should normally be fine with a value of 40).
        private const int k_TLSPadding = 5 + 32 + 31;

        private struct TLSConnectionData
        {
            [NativeDisableUnsafePtrRestriction]
            public Binding.unitytls_client* UnityTLSClientPtr;

            public ConnectionId UnderlyingConnection;
            public Error.DisconnectReason DisconnectReason;

            // Used to delete old half-open connections.
            public long LastHandshakeUpdate;
        }

        internal ConnectionList m_ConnectionList;
        private ConnectionList m_UnderlyingConnectionList;
        private ConnectionDataMap<TLSConnectionData> m_ConnectionsData;
        private NativeParallelHashMap<ConnectionId, ConnectionId> m_UnderlyingIdToCurrentIdMap;
        private UnityTLSConfiguration m_UnityTLSConfiguration;
        private PacketsQueue m_DeferredSends;
        private long m_HalfOpenDisconnectTimeout;

        public int Initialize(ref NetworkSettings settings, ref ConnectionList connectionList, ref int packetPadding)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (!connectionList.IsCreated)
                throw new InvalidOperationException("TLS layer expects to have an underlying connection list.");
#endif
            m_UnderlyingConnectionList = connectionList;
            connectionList = m_ConnectionList = ConnectionList.Create();
            m_ConnectionsData = new ConnectionDataMap<TLSConnectionData>(1, default(TLSConnectionData), Allocator.Persistent);
            m_UnderlyingIdToCurrentIdMap = new NativeParallelHashMap<ConnectionId, ConnectionId>(1, Allocator.Persistent);
            m_UnityTLSConfiguration = new UnityTLSConfiguration(ref settings, SecureTransportProtocol.TLS);
            m_DeferredSends = new PacketsQueue(k_DeferredSendsQueueSize);

            m_DeferredSends.SetDefaultDataOffset(packetPadding);

            var netConfig = settings.GetNetworkConfigParameters();
            // We pick the maximum handshake timeout as our half-open disconnect timeout since after
            // that point, there is no progress possible anymore on the handshake.
            m_HalfOpenDisconnectTimeout = netConfig.maxConnectAttempts * netConfig.connectTimeoutMS;

            packetPadding += k_TLSPadding;

            return 0;
        }

        public void Dispose() 
        {
            // Destroy any remaining UnityTLS clients (their memory is managed by UnityTLS).
            for (int i = 0; i < m_ConnectionsData.Length; i++)
            {
                var data = m_ConnectionsData.DataAt(i);
                if (data.UnityTLSClientPtr != null)
                    Binding.unitytls_client_destroy(data.UnityTLSClientPtr);
            }

            m_ConnectionList.Dispose();
            m_ConnectionsData.Dispose();
            m_UnderlyingIdToCurrentIdMap.Dispose();
            m_UnityTLSConfiguration.Dispose();
            m_DeferredSends.Dispose();
        }

        [BurstCompile]
        private struct ReceiveJob : IJob
        {
            public ConnectionList Connections;
            public ConnectionList UnderlyingConnections;
            public ConnectionDataMap<TLSConnectionData> ConnectionsData;
            public NativeParallelHashMap<ConnectionId, ConnectionId> UnderlyingIdToCurrentId;
            public PacketsQueue DeferredSends;
            public PacketsQueue ReceiveQueue;
            public long Time;
            public long HalfOpenDisconnectTimeout;
            [NativeDisableUnsafePtrRestriction]
            public Binding.unitytls_client_config* UnityTLSConfig;
            [NativeDisableUnsafePtrRestriction]
            public UnityTLSCallbacks.CallbackContext* UnityTLSCallbackContext;

            public void Execute()
            {
                UnityTLSCallbackContext->ReceivedPacket = default;
                UnityTLSCallbackContext->SendQueue = DeferredSends;
                UnityTLSCallbackContext->SendQueueIndex = -1;
                UnityTLSCallbackContext->PacketPadding = 0;

                ProcessReceivedMessages();
                ProcessUnderlyingConnectionList();
                ProcessConnectionList();
            }

            private void ProcessReceivedMessages()
            {
                var count = ReceiveQueue.Count;
                for (int i = 0; i < count; i++)
                {
                    var packetProcessor = ReceiveQueue[i];

                    if (packetProcessor.Length == 0)
                        continue;

                    UnityTLSCallbackContext->ReceivedPacket = packetProcessor;

                    // If we don't know about the underlying connection, then assume this is the
                    // first message from a client (a client hello). If it turns out it isn't, then
                    // the UnityTLS client will just fail, eventually causing a disconnection.
                    if (!UnderlyingIdToCurrentId.TryGetValue(packetProcessor.ConnectionRef, out var connectionId))
                    {
                        connectionId = ProcessClientHello(ref packetProcessor);
                        // Don't drop the packet, handshake check below will cover that.
                    }

                    packetProcessor.ConnectionRef = connectionId;

                    // If in initial or handshake state, process everything as a handshake message.
                    var clientPtr = ConnectionsData[connectionId].UnityTLSClientPtr;
                    var clientState = Binding.unitytls_client_get_state(clientPtr);
                    if (clientState == Binding.UnityTLSClientState_Init || clientState == Binding.UnityTLSClientState_Handshake)
                    {
                        ProcessHandshakeMessage(ref packetProcessor);

                        // TODO Can this even happen? If so, need to handle this better.
                        if (packetProcessor.Length > 0)
                            Debug.LogError("TLS handshake message was not entirely processed. Dropping leftovers.");

                        continue;
                    }

                    // If we get here then we must be dealing with an actual data message.
                    ProcessDataMessage(ref packetProcessor);
                }
            }

            private ConnectionId ProcessClientHello(ref PacketProcessor packetProcessor)
            {
                var connectionId = Connections.StartConnecting(ref packetProcessor.EndpointRef);
                UnderlyingIdToCurrentId.Add(packetProcessor.ConnectionRef, connectionId);

                var clientPtr = Binding.unitytls_client_create(Binding.UnityTLSRole_Server, UnityTLSConfig);
                Binding.unitytls_client_init(clientPtr);

                ConnectionsData[connectionId] = new TLSConnectionData
                {
                    UnityTLSClientPtr = clientPtr,
                    UnderlyingConnection = packetProcessor.ConnectionRef,
                };

                return connectionId;
            }

            private void ProcessHandshakeMessage(ref PacketProcessor packetProcessor)
            {
                var connectionId = packetProcessor.ConnectionRef;
                var data = ConnectionsData[connectionId];

                UnityTLSCallbackContext->NewPacketsEndpoint = packetProcessor.EndpointRef;
                UnityTLSCallbackContext->NewPacketsConnection = data.UnderlyingConnection;

                AdvanceHandshake(data.UnityTLSClientPtr);

                // Update the last handshake update time.
                data.LastHandshakeUpdate = Time;
                ConnectionsData[connectionId] = data;

                // Check if the handshake is over.
                var clientState = Binding.unitytls_client_get_state(data.UnityTLSClientPtr);
                if (clientState == Binding.UnityTLSClientState_Messaging)
                {
                    var role = Binding.unitytls_client_get_role(data.UnityTLSClientPtr);
                    if (role == Binding.UnityTLSRole_Client)
                        Connections.FinishConnectingFromLocal(ref connectionId);
                    else
                        Connections.FinishConnectingFromRemote(ref connectionId);
                }
            }

            private void ProcessDataMessage(ref PacketProcessor packetProcessor)
            {
                var connectionId = packetProcessor.ConnectionRef;
                var originalPacketOffset = packetProcessor.Offset;

                // UnityTLS is going to read the encrypted packet directly from the packet
                // processor. Since it can't write the decrypted data in the same place, we need to
                // create a temporary buffer to store that data.
                var tempBuffer = new NativeArray<byte>(NetworkParameterConstants.MTU, Allocator.Temp);
                var tempBufferLength = 0;

                // The function unitytls_client_read_data exits once it has read a single record
                // (and presumably also if it can't even do that without blocking on I/O). So if the
                // packet contains multiple records, we need to call it multiple times, hence the
                // looping until the packet is empty.
                while (packetProcessor.Length > 0)
                {
                    // Adjust the buffer pointer/length to where the data should be written next.
                    var bufferPtr = (byte*)tempBuffer.GetUnsafePtr() + tempBufferLength;
                    var bufferLength = NetworkParameterConstants.MTU - tempBufferLength;

                    var decryptedLength = new UIntPtr();
                    var result = Binding.unitytls_client_read_data(ConnectionsData[connectionId].UnityTLSClientPtr,
                        bufferPtr, new UIntPtr((uint)bufferLength), &decryptedLength);

                    // Decrypted length shouldn't be 0, but we're playing it safe.
                    if (result != Binding.UNITYTLS_SUCCESS || decryptedLength.ToUInt32() == 0)
                    {
                        // Bad record? Unknown what could be causing this, but in any case don't log
                        // an error since this could be used to flood the logs. Just stop processing
                        // and return what we were able to decrypt (if anything). If the situation
                        // is bad enough, UnityTLS client will have failed and we'll clean up later.
                        break;
                    }

                    tempBufferLength += (int)decryptedLength.ToUInt32();
                }

                packetProcessor.SetUnsafeMetadata(0, originalPacketOffset);
                packetProcessor.AppendToPayload(tempBuffer.GetUnsafePtr(), tempBufferLength);
            }

            private void ProcessUnderlyingConnectionList()
            {
                // The only thing we care about in the underlying connection list is disconnections.
                var disconnects = UnderlyingConnections.QueryFinishedDisconnections(Allocator.Temp);

                var count = disconnects.Length;
                for (int i = 0; i < count; i++)
                {
                    var underlyingConnectionId = disconnects[i].Connection;

                    // Shouldn't really happen. If it does, it means we didn't even know about the
                    // underlying connection, so the best thing to do is just ignore it.
                    if (!UnderlyingIdToCurrentId.TryGetValue(underlyingConnectionId, out var connectionId))
                        continue;

                    // If our connection is not disconnecting, then it means the layer below
                    // triggered the disconnection on its own, so start disconnecting.
                    if (Connections.GetConnectionState(connectionId) != NetworkConnection.State.Disconnecting)
                        Connections.StartDisconnecting(ref connectionId);

                    Disconnect(connectionId, disconnects[i].Reason);
                }
            }

            private void ProcessConnectionList()
            {
                var count = Connections.Count;
                for (int i = 0; i < count; i++)
                {
                    var connectionId = Connections.ConnectionAt(i);
                    var connectionState = Connections.GetConnectionState(connectionId);

                    switch (connectionState)
                    {
                        case NetworkConnection.State.Connecting:
                            HandleConnectingState(connectionId);
                            break;
                        case NetworkConnection.State.Disconnecting:
                            HandleDisconnectingState(connectionId);
                            break;
                    }

                    CheckForFailedClient(connectionId);
                    CheckForHalfOpenConnection(connectionId);
                }
            }

            private void HandleConnectingState(ConnectionId connection)
            {
                var endpoint = Connections.GetConnectionEndpoint(connection);
                var underlyingId = ConnectionsData[connection].UnderlyingConnection;

                // First we hear of this connection, start connecting on the underlying layer and
                // create the UnityTLS context (we won't use it now, but it'll be there already).
                if (underlyingId == default)
                {
                    underlyingId = UnderlyingConnections.StartConnecting(ref endpoint);
                    UnderlyingIdToCurrentId.Add(underlyingId, connection);

                    var clientPtr = Binding.unitytls_client_create(Binding.UnityTLSRole_Client, UnityTLSConfig);
                    Binding.unitytls_client_init(clientPtr);

                    ConnectionsData[connection] = new TLSConnectionData
                    {
                        UnityTLSClientPtr = clientPtr,
                        UnderlyingConnection = underlyingId,
                        LastHandshakeUpdate = Time,
                    };
                }

                // Make progress on the handshake if underlying connection is completed.
                if (UnderlyingConnections.GetConnectionState(underlyingId) == NetworkConnection.State.Connected)
                {
                    UnityTLSCallbackContext->NewPacketsEndpoint = endpoint;
                    UnityTLSCallbackContext->NewPacketsConnection = underlyingId;
                    AdvanceHandshake(ConnectionsData[connection].UnityTLSClientPtr);
                }
            }

            private void HandleDisconnectingState(ConnectionId connection)
            {
                var underlyingId = ConnectionsData[connection].UnderlyingConnection;
                var underlyingState = UnderlyingConnections.GetConnectionState(underlyingId);

                if (underlyingState == NetworkConnection.State.Disconnected)
                {
                    Disconnect(connection, ConnectionsData[connection].DisconnectReason);
                }
                else if (underlyingState != NetworkConnection.State.Disconnecting)
                {
                    UnderlyingConnections.StartDisconnecting(ref underlyingId);
                }
            }

            private void CheckForFailedClient(ConnectionId connection)
            {
                var data = ConnectionsData[connection];

                if (data.UnityTLSClientPtr == null)
                    return;

                if (Binding.unitytls_client_get_state(data.UnityTLSClientPtr) == Binding.UnityTLSClientState_Fail)
                {
                    UnderlyingConnections.StartDisconnecting(ref data.UnderlyingConnection);
                    Connections.StartDisconnecting(ref connection);

                    // TODO Not the ideal disconnect reason. If we ever have a better one, use it.
                    data.DisconnectReason = Error.DisconnectReason.ClosedByRemote;
                    ConnectionsData[connection] = data;
                }
            }

            private void CheckForHalfOpenConnection(ConnectionId connection)
            {
                var data = ConnectionsData[connection];

                if (data.UnityTLSClientPtr == null)
                    return;

                var clientState = Binding.unitytls_client_get_state(data.UnityTLSClientPtr);
                if (clientState != Binding.UnityTLSClientState_Init && clientState != Binding.UnityTLSClientState_Handshake)
                    return;

                if (Time - data.LastHandshakeUpdate > HalfOpenDisconnectTimeout)
                {
                    UnderlyingConnections.StartDisconnecting(ref data.UnderlyingConnection);
                    Connections.StartDisconnecting(ref connection);

                    data.DisconnectReason = Error.DisconnectReason.Timeout;
                    ConnectionsData[connection] = data;
                }
            }

            private void AdvanceHandshake(Binding.unitytls_client* clientPtr)
            {
                while (Binding.unitytls_client_handshake(clientPtr) == Binding.UNITYTLS_HANDSHAKE_STEP);
            }

            private void Disconnect(ConnectionId connection, Error.DisconnectReason reason)
            {
                var data = ConnectionsData[connection];
                if (data.UnityTLSClientPtr != null)
                    Binding.unitytls_client_destroy(data.UnityTLSClientPtr);

                UnderlyingIdToCurrentId.Remove(data.UnderlyingConnection);
                ConnectionsData.ClearData(ref connection);
                Connections.FinishDisconnecting(ref connection, reason);
            }
        }

        public JobHandle ScheduleReceive(ref ReceiveJobArguments arguments, JobHandle dependency)
        {
            return new ReceiveJob
            {
                Connections = m_ConnectionList,
                UnderlyingConnections = m_UnderlyingConnectionList,
                ConnectionsData = m_ConnectionsData,
                UnderlyingIdToCurrentId = m_UnderlyingIdToCurrentIdMap,
                DeferredSends = m_DeferredSends,
                ReceiveQueue = arguments.ReceiveQueue,
                Time = arguments.Time,
                HalfOpenDisconnectTimeout = m_HalfOpenDisconnectTimeout,
                UnityTLSConfig = m_UnityTLSConfiguration.ConfigPtr,
                UnityTLSCallbackContext = m_UnityTLSConfiguration.CallbackContextPtr,
            }.Schedule(dependency);
        }

        [BurstCompile]
        private struct SendJob : IJob
        {
            public ConnectionDataMap<TLSConnectionData> ConnectionsData;
            public PacketsQueue SendQueue;
            public PacketsQueue DeferredSends;
            [NativeDisableUnsafePtrRestriction]
            public UnityTLSCallbacks.CallbackContext* UnityTLSCallbackContext;

            public void Execute()
            {
                UnityTLSCallbackContext->SendQueue = SendQueue;
                UnityTLSCallbackContext->PacketPadding = k_TLSPadding;

                // Encrypt all the packets in the send queue.
                var count = SendQueue.Count;
                for (int i = 0; i < count; i++)
                {
                    var packetProcessor = SendQueue[i];
                    if (packetProcessor.Length == 0)
                        continue;

                    UnityTLSCallbackContext->SendQueueIndex = i;

                    var connectionId = packetProcessor.ConnectionRef;
                    var clientPtr = ConnectionsData[connectionId].UnityTLSClientPtr;

                    // Only way this can happen is if a previous send caused a failure.
                    var clientState = Binding.unitytls_client_get_state(clientPtr);
                    if (clientState != Binding.UnityTLSClientState_Messaging)
                    {
                        packetProcessor.Drop();
                        continue;
                    }

                    packetProcessor.ConnectionRef = ConnectionsData[connectionId].UnderlyingConnection;

                    var packetPtr = (byte*)packetProcessor.GetUnsafePayloadPtr() + packetProcessor.Offset;
                    var result = Binding.unitytls_client_send_data(clientPtr, packetPtr, new UIntPtr((uint)packetProcessor.Length));
                    if (result != Binding.UNITYTLS_SUCCESS)
                    {
                        Debug.LogError($"Failed to encrypt packet (TLS error: {result}). Packet will not be sent.");
                        packetProcessor.Drop();
                    }
                }

                // Add all the deferred sends to the send queue (they're already encrypted).
                var deferredCount = DeferredSends.Count;
                for (int i = 0; i < deferredCount; i++)
                {
                    var deferredPacketProcessor = DeferredSends[i];
                    if (deferredPacketProcessor.Length == 0)
                        continue;

                    if (SendQueue.EnqueuePacket(out var packetProcessor))
                    {
                        packetProcessor.EndpointRef = deferredPacketProcessor.EndpointRef;
                        packetProcessor.ConnectionRef = deferredPacketProcessor.ConnectionRef;

                        // Remove the TLS padding from the offset.
                        packetProcessor.SetUnsafeMetadata(0, packetProcessor.Offset - k_TLSPadding);

                        packetProcessor.AppendToPayload(deferredPacketProcessor);
                    }
                }

                DeferredSends.Clear();
            }
        }

        public JobHandle ScheduleSend(ref SendJobArguments arguments, JobHandle dependency)
        {
            return new SendJob
            {
                ConnectionsData = m_ConnectionsData,
                SendQueue = arguments.SendQueue,
                DeferredSends = m_DeferredSends,
                UnityTLSCallbackContext = m_UnityTLSConfiguration.CallbackContextPtr,
            }.Schedule(dependency);
        }
    }
}

#endif