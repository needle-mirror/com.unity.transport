using System;
using System.Threading;
using AOT;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Networking.Transport.Protocols;
#if ENABLE_MANAGED_UNITYTLS
using Unity.Networking.Transport.TLS;
using Unity.TLS.LowLevel;
#endif
using UnityEngine;
using UnityEngine.Assertions;

namespace Unity.Networking.Transport.Relay
{
    internal static class ConnectionAddressExtensions
    {
        /// <summary>
        /// Converts the relay allocation id using the specified address
        /// </summary>
        /// <param name="address">The address</param>
        /// <returns>The ref relay allocation id</returns>
        public static unsafe ref RelayAllocationId AsRelayAllocationId(this ref NetworkInterfaceEndPoint address)
        {
            fixed(byte* addressPtr = address.data)
            {
                return ref *(RelayAllocationId*)addressPtr;
            }
        }
    }

    public static class RelayParameterExtensions
    {
        /// <summary>
        /// Sets the <see cref="RelayNetworkParameter"/> values for the <see cref="NetworkSettings"/>
        /// </summary>
        /// <param name="serverData"><seealso cref="RelayNetworkParameter.ServerData"/></param>
        /// <param name="relayConnectionTimeMS"><seealso cref="RelayNetworkParameter.RelayConnectionTimeMS"/></param>
        public static ref NetworkSettings WithRelayParameters(
            ref this NetworkSettings settings,
            ref RelayServerData serverData,
            int relayConnectionTimeMS = RelayNetworkParameter.k_DefaultConnectionTimeMS
        )
        {
            var parameter = new RelayNetworkParameter
            {
                ServerData = serverData,
                RelayConnectionTimeMS = relayConnectionTimeMS,
            };

            settings.AddRawParameterStruct(ref parameter);

            return ref settings;
        }

        /// <summary>
        /// Gets the <see cref="RelayNetworkParameter"/>
        /// </summary>
        /// <returns>Returns the <see cref="RelayNetworkParameter"/> values for the <see cref="NetworkSettings"/></returns>
        public static RelayNetworkParameter GetRelayParameters(ref this NetworkSettings settings)
        {
            if (!settings.TryGet<RelayNetworkParameter>(out var parameters))
            {
                throw new System.InvalidOperationException($"Can't extract Relay parameters: {nameof(RelayNetworkParameter)} must be provided to the {nameof(NetworkSettings)}");
            }

            return parameters;
        }
    }

    /// <summary>
    /// Relay protocol network parementers used to connect to the Unity Relay service. This data must be provided to
    /// the <see cref="NetworkDriver.Create"/> function in order to be able to use connect to Relay.
    /// </summary>
    public struct RelayNetworkParameter : INetworkParameter
    {
        internal const int k_DefaultConnectionTimeMS = 3000;

        /// <summary>
        /// The data that is used to describe the connection to the Relay Server.
        /// </summary>
        public RelayServerData ServerData;
        /// <summary>
        /// The timeout in milliseconds after which a ping message is sent to the Relay Server
        /// to keep the connection alive.
        /// </summary>
        public int RelayConnectionTimeMS;

        public unsafe bool Validate()
        {
            var valid = true;

            if (ServerData.Endpoint == default)
            {
                valid = false;
                UnityEngine.Debug.LogError($"{nameof(ServerData.Endpoint)} value ({ServerData.Endpoint}) must be a valid value");
            }
            if (ServerData.AllocationId == default)
            {
                valid = false;
                UnityEngine.Debug.LogError($"{nameof(ServerData.AllocationId)} value ({ServerData.AllocationId}) must be a valid value");
            }
            if (RelayConnectionTimeMS < 0)
            {
                valid = false;
                UnityEngine.Debug.LogError($"{nameof(RelayConnectionTimeMS)} value({RelayConnectionTimeMS}) must be greater or equal to 0");
            }

            return valid;
        }
    }

    [BurstCompile]
    internal struct RelayNetworkProtocol : INetworkProtocol
    {
        public static ushort SwitchEndianness(ushort value)
        {
            if (DataStreamWriter.IsLittleEndian)
                return (ushort)((value << 8) | (value >> 8));

            return value;
        }

        private enum RelayConnectionState : byte
        {
            Unbound = 0,
            Handshake = 1,
            Binding = 2,
            Bound = 3,
            Connected = 4,
        }

        private enum SecuredRelayConnectionState : byte
        {
            Unsecure = 0,
            Secured = 1
        }

        private struct RelayProtocolData
        {
            public RelayConnectionState ConnectionState;
            public SecuredRelayConnectionState SecureState;
            public SessionIdToken ConnectionReceiveToken;
            public long LastConnectAttempt;
            public long LastUpdateTime;
            public long LastSentTime;
            public int ConnectTimeoutMS;
            public int RelayConnectionTimeMS;
            public RelayAllocationId HostAllocationId;
            public NetworkInterfaceEndPoint ServerEndpoint;
            public RelayServerData ServerData;
#if ENABLE_MANAGED_UNITYTLS
            public SecureClientState SecureClientState;
#endif
            // Used by clients to indicate that we should attempt to connect as soon as we're bound.
            // We can't just connect unconditionnally on bind since the user might not have called
            // Connect yet at that point.
            public bool ConnectOnBind;
        }

        public IntPtr UserData;

        public void Initialize(NetworkSettings settings)
        {
            var relayConfig = settings.GetRelayParameters();
            var config = settings.GetNetworkConfigParameters();

#if ENABLE_MANAGED_UNITYTLS
            if (relayConfig.ServerData.IsSecure == 1)
            {
                ManagedSecureFunctions.Initialize();
            }
#endif
            unsafe
            {
                UserData = (IntPtr)UnsafeUtility.Malloc(UnsafeUtility.SizeOf<RelayProtocolData>(), UnsafeUtility.AlignOf<RelayProtocolData>(), Allocator.Persistent);
                *(RelayProtocolData*)UserData = new RelayProtocolData
                {
                    ServerData = relayConfig.ServerData,
                    ConnectionState = RelayConnectionState.Unbound,
                    ConnectTimeoutMS = config.connectTimeoutMS,
                    RelayConnectionTimeMS = relayConfig.RelayConnectionTimeMS,
                    SecureState = SecuredRelayConnectionState.Unsecure
                };
            }
        }

        public void Dispose()
        {
            unsafe
            {
#if ENABLE_MANAGED_UNITYTLS
                // cleanup the SecureData
                var protocolData = (RelayProtocolData*)UserData;
                if (protocolData->SecureClientState.ClientPtr != null)
                    SecureNetworkProtocol.DisposeSecureClient(ref protocolData->SecureClientState);
#endif
                if (UserData != default)
                    UnsafeUtility.Free(UserData.ToPointer(), Allocator.Persistent);

                UserData = default;
            }
        }

        bool TryExtractParameters<T>(out T config, params INetworkParameter[] param)
        {
            for (var i = 0; i < param.Length; ++i)
            {
                if (param[i] is T)
                {
                    config = (T)param[i];
                    return true;
                }
            }

            config = default;
            return false;
        }

        public int Bind(INetworkInterface networkInterface, ref NetworkInterfaceEndPoint localEndPoint)
        {
            if (networkInterface.Bind(localEndPoint) != 0)
                return -1;

            unsafe
            {
                var protocolData = (RelayProtocolData*)UserData;
                // Relay protocol will stablish only one physical connection using the interface (to Relay server).
                // All client connections are virtual. Here we initialize that connection.
                networkInterface.CreateInterfaceEndPoint(protocolData->ServerData.Endpoint, out protocolData->ServerEndpoint);

                if (protocolData->ServerData.IsSecure == 1)
                {
                    protocolData->ConnectionState = RelayConnectionState.Handshake;
                }
                else
                {
                    // The Relay protocol binding process requires to exchange some messages to stablish the connection
                    // with the Relay server, so we set the state to "binding" until the connection with server is confirm.
                    protocolData->ConnectionState = RelayConnectionState.Binding;
                }

                return 0;
            }
        }

        public int CreateConnectionAddress(INetworkInterface networkInterface, NetworkEndPoint endPoint, out NetworkInterfaceEndPoint address)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (UnsafeUtility.SizeOf<NetworkInterfaceEndPoint>() < UnsafeUtility.SizeOf<RelayAllocationId>())
                throw new InvalidOperationException("RelayAllocationId does not fit in NetworkInterfaceEndPoint");
#endif
            unsafe
            {
                var protocolData = (RelayProtocolData*)UserData;

                // The remote endpoint for clients is always the host allocation ID.
                // Note that there's a good chance the host allocation ID is not known yet. That's
                // fine; we update the connection's address (allocation ID) once connected.
                address = default;
                fixed(byte* addressPtr = address.data)
                {
                    *(RelayAllocationId*)addressPtr = protocolData->HostAllocationId;
                }

                return 0;
            }
        }

        public NetworkEndPoint GetRemoteEndPoint(INetworkInterface networkInterface, NetworkInterfaceEndPoint address)
        {
            unsafe
            {
                var protocolData = (RelayProtocolData*)UserData;
                return networkInterface.GetGenericEndPoint(protocolData->ServerEndpoint);
            }
        }

        public NetworkProtocol CreateProtocolInterface()
        {
            return new NetworkProtocol(
                computePacketOverhead: new TransportFunctionPointer<NetworkProtocol.ComputePacketOverheadDelegate>(ComputePacketOverhead),
                processReceive: new TransportFunctionPointer<NetworkProtocol.ProcessReceiveDelegate>(ProcessReceive),
                processSend: new TransportFunctionPointer<NetworkProtocol.ProcessSendDelegate>(ProcessSend),
                processSendConnectionAccept: new TransportFunctionPointer<NetworkProtocol.ProcessSendConnectionAcceptDelegate>(ProcessSendConnectionAccept),
                connect: new TransportFunctionPointer<NetworkProtocol.ConnectDelegate>(Connect),
                disconnect: new TransportFunctionPointer<NetworkProtocol.DisconnectDelegate>(Disconnect),
                processSendPing: new TransportFunctionPointer<NetworkProtocol.ProcessSendPingDelegate>(ProcessSendPing),
                processSendPong: new TransportFunctionPointer<NetworkProtocol.ProcessSendPongDelegate>(ProcessSendPong),
                update: new TransportFunctionPointer<NetworkProtocol.UpdateDelegate>(Update),
                needsUpdate: true,
                userData: UserData,
                maxHeaderSize: RelayMessageRelay.Length + UdpCHeader.Length,
                maxFooterSize: SessionIdToken.k_Length
            );
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(NetworkProtocol.ComputePacketOverheadDelegate))]
        public static int ComputePacketOverhead(ref NetworkDriver.Connection connection, out int dataOffset)
        {
            var utpOverhead = UnityTransportProtocol.ComputePacketOverhead(ref connection, out dataOffset);
            dataOffset += RelayMessageRelay.Length;
            return utpOverhead + RelayMessageRelay.Length;
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(NetworkProtocol.ProcessReceiveDelegate))]
        public static void ProcessReceive(IntPtr stream, ref NetworkInterfaceEndPoint endpoint, int size, ref NetworkSendInterface sendInterface, ref NetworkSendQueueHandle queueHandle, IntPtr userData, ref ProcessPacketCommand command)
        {
            unsafe
            {
                var protocolData = (RelayProtocolData*)userData;

                if (endpoint != protocolData->ServerEndpoint)
                {
                    command.Type = ProcessPacketCommandType.Drop;
                    return;
                }

#if ENABLE_MANAGED_UNITYTLS
                if (protocolData->ConnectionState == RelayConnectionState.Handshake)
                {
                    var secureUserData = (SecureUserData*)protocolData->SecureClientState.ClientConfig->transportUserData;

                    SecureNetworkProtocol.SetSecureUserData(stream, size, ref endpoint, ref sendInterface, ref queueHandle, secureUserData);

                    var clientState = Binding.unitytls_client_get_state(protocolData->SecureClientState.ClientPtr);
                    uint handshakeResult = Binding.UNITYTLS_SUCCESS;

                    // check and see if we are still in the handshake :D
                    if (clientState == Binding.UnityTLSClientState_Handshake
                        || clientState == Binding.UnityTLSClientState_Init)
                    {
                        bool shouldRunAgain = false;
                        do
                        {
                            handshakeResult = SecureNetworkProtocol.SecureHandshakeStep(ref protocolData->SecureClientState);
                            clientState = Binding.unitytls_client_get_state(protocolData->SecureClientState.ClientPtr);
                            shouldRunAgain = (size != 0 && secureUserData->BytesProcessed == 0 && clientState == Binding.UnityTLSClientState_Handshake);
                        }
                        while (shouldRunAgain);
                    }

                    if (clientState == Binding.UnityTLSClientState_Messaging)
                    {
                        // we are moving to the binding state
                        protocolData->ConnectionState = RelayConnectionState.Binding;
                        protocolData->SecureState = SecuredRelayConnectionState.Secured;
                    }

                    command.Type = ProcessPacketCommandType.Drop;
                    return;
                }

                // Are we setup for a secure state? and if so
                if (protocolData->ServerData.IsSecure == 1 &&
                    (protocolData->SecureState != SecuredRelayConnectionState.Secured))
                {
                    // we should not get here ?
                    command.Type = ProcessPacketCommandType.Drop;
                    return;
                }

                if (protocolData->ServerData.IsSecure == 1 &&
                    (protocolData->SecureState == SecuredRelayConnectionState.Secured))
                {
                    var secureUserData = (SecureUserData*)protocolData->SecureClientState.ClientConfig->transportUserData;

                    SecureNetworkProtocol.SetSecureUserData(stream, size, ref endpoint, ref sendInterface, ref queueHandle, secureUserData);

                    var buffer = new NativeArray<byte>(NetworkParameterConstants.MTU, Allocator.Temp);
                    var bytesRead = new UIntPtr();
                    var result = Binding.unitytls_client_read_data(protocolData->SecureClientState.ClientPtr,
                        (byte*)buffer.GetUnsafePtr(), new UIntPtr(NetworkParameterConstants.MTU),
                        &bytesRead);

                    if (result == Binding.UNITYTLS_SUCCESS)
                    {
                        // when we have a proper read we need to copy that data into the stream. It should be noted
                        // that this copy does change data we don't technically own.
                        UnsafeUtility.MemCpy((void*)stream, buffer.GetUnsafePtr(), bytesRead.ToUInt32());

                        if (ProcessRelayData(stream, ref endpoint, (int)bytesRead.ToUInt32(), ref sendInterface, ref queueHandle, ref command, protocolData))
                            return;
                    }

                    command.Type = ProcessPacketCommandType.Drop;
                    return;
                }
#endif
                if (ProcessRelayData(stream, ref endpoint, size, ref sendInterface, ref queueHandle, ref command, protocolData))
                    return;

                command.Type = ProcessPacketCommandType.Drop;
            }
        }

        private static unsafe bool ProcessRelayData(IntPtr stream, ref NetworkInterfaceEndPoint endpoint, int size,
            ref NetworkSendInterface sendInterface, ref NetworkSendQueueHandle queueHandle, ref ProcessPacketCommand command,
            RelayProtocolData* protocolData)
        {
            var data = (byte*)stream;
            var header = *(RelayMessageHeader*)data;

            if (size < RelayMessageHeader.Length || !header.IsValid())
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                UnityEngine.Debug.LogError("Received an invalid Relay message header");
#endif
                command.Type = ProcessPacketCommandType.Drop;
                return true;
            }
#if ENABLE_MANAGED_UNITYTLS
            if (protocolData->ServerData.IsSecure == 1 &&
                (protocolData->SecureState == SecuredRelayConnectionState.Secured))
            {
                var secureUserData = (SecureUserData*)protocolData->SecureClientState.ClientConfig->transportUserData;
                SecureNetworkProtocol.SetSecureUserData(stream, size, ref endpoint, ref sendInterface, ref queueHandle, secureUserData);
            }
#endif
            switch (header.Type)
            {
                case RelayMessageType.BindReceived:
                    command.Type = ProcessPacketCommandType.Drop;

                    if (size != RelayMessageHeader.Length)
                    {
                        UnityEngine.Debug.LogError("Received an invalid Relay Bind Received message: Wrong length");
                        return true;
                    }

                    protocolData->ConnectionState = RelayConnectionState.Bound;

                    if (protocolData->ConnectOnBind)
                    {
                        SendConnectionRequestToRelay(protocolData, ref sendInterface, ref queueHandle);
                    }

                    command.Type = ProcessPacketCommandType.ProtocolStatusUpdate;
                    command.As.ProtocolStatusUpdate.Status = (int)RelayConnectionStatus.Established;

                    return true;

                case RelayMessageType.Accepted:
                    command.Type = ProcessPacketCommandType.Drop;

                    if (size != RelayMessageAccepted.Length)
                    {
                        UnityEngine.Debug.LogError("Received an invalid Relay Accepted message: Wrong length");
                        return true;
                    }

                    if (protocolData->HostAllocationId != default)
                        return true;

                    var acceptedMessage = *(RelayMessageAccepted*)data;
                    protocolData->HostAllocationId = acceptedMessage.FromAllocationId;

                    command.Type = ProcessPacketCommandType.AddressUpdate;
                    command.Address = default;
                    command.SessionId = protocolData->ConnectionReceiveToken;
                    command.As.AddressUpdate.NewAddress = default;

                    fixed(byte* addressPtr = command.As.AddressUpdate.NewAddress.data)
                    {
                        *(RelayAllocationId*)addressPtr = acceptedMessage.FromAllocationId;
                    }

                    var type = UdpCProtocol.ConnectionRequest;
                    var token = protocolData->ConnectionReceiveToken;
                    var result = SendHeaderOnlyHostMessage(
                        type, token, protocolData, ref acceptedMessage.FromAllocationId, ref sendInterface, ref queueHandle);
                    if (result < 0)
                    {
                        Debug.LogError("Failed to send Connection Request message to host.");
                        return false;
                    }

                    return true;

                case RelayMessageType.Relay:
                    var relayMessage = *(RelayMessageRelay*)data;
                    relayMessage.DataLength = RelayNetworkProtocol.SwitchEndianness(relayMessage.DataLength);
                    if (size < RelayMessageRelay.Length || size != RelayMessageRelay.Length + relayMessage.DataLength)
                    {
                        UnityEngine.Debug.LogError($"Received an invalid Relay Received message: Wrong length");
                        command.Type = ProcessPacketCommandType.Drop;
                        return true;
                    }

                    // TODO: Make sure UTP protocol is not sending any message back here as it wouldn't be using Relay
                    UnityTransportProtocol.ProcessReceive(stream + RelayMessageRelay.Length, ref endpoint,
                        size - RelayMessageRelay.Length, ref sendInterface, ref queueHandle, IntPtr.Zero, ref command);

                    switch (command.Type)
                    {
                        case ProcessPacketCommandType.ConnectionAccept:
                            protocolData->ConnectionState = RelayConnectionState.Connected;
                            break;

                        case ProcessPacketCommandType.Data:
                            command.As.Data.Offset += RelayMessageRelay.Length;
                            break;

                        case ProcessPacketCommandType.DataWithImplicitConnectionAccept:
                            command.As.DataWithImplicitConnectionAccept.Offset += RelayMessageRelay.Length;
                            break;

                        case ProcessPacketCommandType.Disconnect:
                            SendRelayDisconnect(
                                protocolData, ref relayMessage.FromAllocationId, ref sendInterface, ref queueHandle);
                            break;
                    }

                    command.Address = default;
                    fixed(byte* addressPtr = command.Address.data)
                    {
                        *(RelayAllocationId*)addressPtr = relayMessage.FromAllocationId;
                    }

                    return true;

                case RelayMessageType.Error:
                    command.Type = ProcessPacketCommandType.Drop;
                    ProcessRelayError(data, size, ref command);
                    return true;
            }

            command.Type = ProcessPacketCommandType.Drop;
            return true;
        }

        private static unsafe void ProcessRelayError(byte* data, int size, ref ProcessPacketCommand command)
        {
            if (size != RelayMessageError.Length)
            {
                Debug.LogError("Received an invalid Relay Error message (wrong length).");
                return;
            }

            var errorMessage = *(RelayMessageError*)data;

            switch (errorMessage.ErrorCode)
            {
                case 0:
                    Debug.LogError("Received error message from Relay: invalid protocol version. " +
                        "Make sure your Unity Transport package is up to date.");
                    break;
                case 1:
                    Debug.LogError("Received error message from Relay: player timed out due to inactivity.");
                    break;
                case 2:
                    Debug.LogError("Received error message from Relay: unauthorized.");
                    break;
                case 3:
                    Debug.LogError("Received error message from Relay: allocation ID client mismatch.");
                    break;
                case 4:
                    Debug.LogError("Received error message from Relay: allocation ID not found.");
                    break;
                case 5:
                    Debug.LogError("Received error message from Relay: not connected.");
                    break;
                case 6:
                    Debug.LogError("Received error message from Relay: self-connect not allowed.");
                    break;
                default:
                    Debug.LogError($"Received error message from Relay with unknown error code {errorMessage.ErrorCode}");
                    break;
            }

            // Allocation time outs and failure to find the allocation indicate that the allocation
            // is not valid anymore, and that users will need to recreate a new one.
            if (errorMessage.ErrorCode == 1 || errorMessage.ErrorCode == 4)
            {
                Debug.LogError("Relay allocation is invalid. See NetworkDriver.GetRelayConnectionStatus and " +
                    "RelayConnectionStatus.AllocationInvalid for details on how to handle this situation.");
                command.Type = ProcessPacketCommandType.ProtocolStatusUpdate;
                command.As.ProtocolStatusUpdate.Status = (int)RelayConnectionStatus.AllocationInvalid;
            }
        }

        private static unsafe int SendMessage(RelayProtocolData* protocolData, ref NetworkSendInterface sendInterface,
            ref NetworkInterfaceSendHandle sendHandle, ref NetworkSendQueueHandle queueHandle)
        {
#if ENABLE_MANAGED_UNITYTLS
            if (protocolData->ServerData.IsSecure == 1 && protocolData->SecureState == SecuredRelayConnectionState.Secured)
            {
                var secureUserData = (SecureUserData*)protocolData->SecureClientState.ClientConfig->transportUserData;
                SecureNetworkProtocol.SetSecureUserData(
                    IntPtr.Zero, 0, ref protocolData->ServerEndpoint, ref sendInterface, ref queueHandle, secureUserData);

                // we need to free up the current handle before we call send because we could be at capacity and thus
                // when we go and try to get a new handle it will fail.
                var buffer = new NativeArray<byte>(sendHandle.size, Allocator.Temp);
                UnsafeUtility.MemCpy(buffer.GetUnsafePtr(), (void*)sendHandle.data, sendHandle.size);

                // We end up having to abort this handle so we can free it up as DTLS will generate a
                // new one based on the encrypted buffer size.
                sendInterface.AbortSendMessage.Ptr.Invoke(ref sendHandle, sendInterface.UserData);

                var result = Binding.unitytls_client_send_data(
                    protocolData->SecureClientState.ClientPtr, (byte*)buffer.GetUnsafePtr(), new UIntPtr((uint)buffer.Length));

                if (result != Binding.UNITYTLS_SUCCESS)
                {
                    Debug.LogError($"Secure send failed with result: {result}.");
                    // Error is likely caused by a connection that's closed or not established yet.
                    return (int)Error.StatusCode.NetworkStateMismatch;
                }

                return buffer.Length;
            }
            else
#endif
            {
                return sendInterface.EndSendMessage.Ptr.Invoke(
                    ref sendHandle, ref protocolData->ServerEndpoint, sendInterface.UserData, ref queueHandle);
            }
        }

        private static unsafe void SendRelayDisconnect(RelayProtocolData* protocolData, ref RelayAllocationId hostAllocationId,
            ref NetworkSendInterface sendInterface, ref NetworkSendQueueHandle queueHandle)
        {
            var result = sendInterface.BeginSendMessage.Ptr.Invoke(
                out var sendHandle, sendInterface.UserData, RelayMessageDisconnect.Length);
            if (result != 0)
            {
                UnityEngine.Debug.LogError("Failed to send Disconnect message to relay.");
                return;
            }

            var packet = (byte*)sendHandle.data;
            sendHandle.size = RelayMessageDisconnect.Length;
            if (sendHandle.size > sendHandle.capacity)
            {
                sendInterface.AbortSendMessage.Ptr.Invoke(ref sendHandle, sendInterface.UserData);
                UnityEngine.Debug.LogError("Failed to send Disconnect message to relay.");
                return;
            }

            var disconnectMessage = (RelayMessageDisconnect*)packet;
            *disconnectMessage = RelayMessageDisconnect.Create(protocolData->ServerData.AllocationId, hostAllocationId);

            if (SendMessage(protocolData, ref sendInterface, ref sendHandle, ref queueHandle) < 0)
            {
                Debug.LogError("Failed to send Disconnect message to relay.");
                return;
            }
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(NetworkProtocol.ProcessSendDelegate))]
        public static unsafe int ProcessSend(ref NetworkDriver.Connection connection, bool hasPipeline, ref NetworkSendInterface sendInterface, ref NetworkInterfaceSendHandle sendHandle, ref NetworkSendQueueHandle queueHandle, IntPtr userData)
        {
            var relayProtocolData = (RelayProtocolData*)userData;

            var dataLength = (ushort)UnityTransportProtocol.WriteSendMessageHeader(ref connection, hasPipeline, ref sendHandle, RelayMessageRelay.Length);

            var relayMessage = (RelayMessageRelay*)sendHandle.data;
            fixed(byte* addressPtr = connection.Address.data)
            {
                *relayMessage = RelayMessageRelay.Create(relayProtocolData->ServerData.AllocationId, *(RelayAllocationId*)addressPtr, dataLength);
            }

            Interlocked.Exchange(ref relayProtocolData->LastSentTime, relayProtocolData->LastUpdateTime);

            return SendMessage(relayProtocolData, ref sendInterface, ref sendHandle, ref queueHandle);
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(NetworkProtocol.ProcessSendConnectionAcceptDelegate))]
        public static void ProcessSendConnectionAccept(ref NetworkDriver.Connection connection, ref NetworkSendInterface sendInterface, ref NetworkSendQueueHandle queueHandle, IntPtr userData)
        {
            unsafe
            {
                var relayProtocolData = (RelayProtocolData*)userData;

                var toAllocationId = default(RelayAllocationId);

                fixed(byte* addrPtr = connection.Address.data)
                toAllocationId = *(RelayAllocationId*)addrPtr;

                var maxLengthNeeded = RelayMessageRelay.Length + UnityTransportProtocol.GetConnectionAcceptMessageMaxLength();
                if (sendInterface.BeginSendMessage.Ptr.Invoke(out var sendHandle, sendInterface.UserData, maxLengthNeeded) != 0)
                {
                    UnityEngine.Debug.LogError("Failed to send a ConnectionRequest packet");
                    return;
                }

                if (sendHandle.capacity < maxLengthNeeded)
                {
                    sendInterface.AbortSendMessage.Ptr.Invoke(ref sendHandle, sendInterface.UserData);
                    UnityEngine.Debug.LogError("Failed to send a ConnectionAccept packet: size exceeds capacity");
                    return;
                }

                var packet = (byte*)sendHandle.data;
                var size = UnityTransportProtocol.WriteConnectionAcceptMessage(ref connection, packet + RelayMessageRelay.Length, sendHandle.capacity - RelayMessageRelay.Length);

                if (size < 0)
                {
                    sendInterface.AbortSendMessage.Ptr.Invoke(ref sendHandle, sendInterface.UserData);
                    UnityEngine.Debug.LogError("Failed to send a ConnectionAccept packet");
                    return;
                }

                sendHandle.size = RelayMessageRelay.Length + size;

                var relayMessage = (RelayMessageRelay*)packet;
                *relayMessage = RelayMessageRelay.Create(relayProtocolData->ServerData.AllocationId, toAllocationId, (ushort)size);
                Assert.IsTrue(sendHandle.size <= sendHandle.capacity);

                if (SendMessage(relayProtocolData, ref sendInterface, ref sendHandle, ref queueHandle) < 0)
                {
                    Debug.LogError("Failed to send Connection Accept message to host.");
                    return;
                }
            }
        }

        private static unsafe int SendHeaderOnlyHostMessage(UdpCProtocol type, SessionIdToken token, RelayProtocolData* relayProtocolData,
            ref RelayAllocationId hostAllocationId, ref NetworkSendInterface sendInterface, ref NetworkSendQueueHandle queueHandle)
        {
            var result = sendInterface.BeginSendMessage.Ptr.Invoke(
                out var sendHandle, sendInterface.UserData, RelayMessageRelay.Length + UdpCHeader.Length);
            if (result != 0)
            {
                return -1;
            }

            var packet = (byte*)sendHandle.data;
            sendHandle.size = RelayMessageRelay.Length + UdpCHeader.Length;
            if (sendHandle.size > sendHandle.capacity)
            {
                sendInterface.AbortSendMessage.Ptr.Invoke(ref sendHandle, sendInterface.UserData);
                return -1;
            }

            var relayMessage = (RelayMessageRelay*)packet;
            *relayMessage = RelayMessageRelay.Create(
                relayProtocolData->ServerData.AllocationId, hostAllocationId, UdpCHeader.Length);

            var header = (UdpCHeader*)(((byte*)relayMessage) + RelayMessageRelay.Length);
            *header = new UdpCHeader
            {
                Type = (byte)type,
                SessionToken = token,
                Flags = 0
            };

            return SendMessage(relayProtocolData, ref sendInterface, ref sendHandle, ref queueHandle);
        }

        private static unsafe void SendConnectionRequestToRelay(RelayProtocolData* relayProtocolData,
            ref NetworkSendInterface sendInterface, ref NetworkSendQueueHandle queueHandle)
        {
            var result = sendInterface.BeginSendMessage.Ptr.Invoke(
                out var sendHandle, sendInterface.UserData, RelayMessageConnectRequest.Length);
            if (result != 0)
            {
                Debug.LogError("Failed to send ConnectRequest to relay.");
                return;
            }

            var packet = (byte*)sendHandle.data;
            sendHandle.size = RelayMessageConnectRequest.Length;
            if (sendHandle.size > sendHandle.capacity)
            {
                sendInterface.AbortSendMessage.Ptr.Invoke(ref sendHandle, sendInterface.UserData);
                Debug.LogError("Failed to send ConnectRequest to relay.");
                return;
            }

            var message = (RelayMessageConnectRequest*)packet;
            *message = RelayMessageConnectRequest.Create(
                relayProtocolData->ServerData.AllocationId, relayProtocolData->ServerData.HostConnectionData);

            if (SendMessage(relayProtocolData, ref sendInterface, ref sendHandle, ref queueHandle) < 0)
            {
                Debug.LogError("Failed to send ConnectRequest to relay.");
                return;
            }
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(NetworkProtocol.ConnectDelegate))]
        public static void Connect(ref NetworkDriver.Connection connection, ref NetworkSendInterface sendInterface, ref NetworkSendQueueHandle queueHandle, IntPtr userData)
        {
            unsafe
            {
                var relayProtocolData = (RelayProtocolData*)userData;
                relayProtocolData->ConnectionReceiveToken = connection.ReceiveToken;

                // If we're not bound, either we're still binding (and we can't attempt to connect
                // yet), or we're connected (and connecting is thus useless).
                if (relayProtocolData->ConnectionState != RelayConnectionState.Bound)
                {
                    relayProtocolData->ConnectOnBind = true;
                    return;
                }

                if (relayProtocolData->HostAllocationId == default)
                {
                    SendConnectionRequestToRelay(relayProtocolData, ref sendInterface, ref queueHandle);
                }
                else
                {
                    var type = UdpCProtocol.ConnectionRequest;
                    var token = relayProtocolData->ConnectionReceiveToken;
                    var result = SendHeaderOnlyHostMessage(
                        type, token, relayProtocolData, ref relayProtocolData->HostAllocationId, ref sendInterface, ref queueHandle);
                    if (result < 0)
                    {
                        Debug.LogError("Failed to send Connection Request message to host.");
                        return;
                    }
                }
            }
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(NetworkProtocol.DisconnectDelegate))]
        public static unsafe void Disconnect(ref NetworkDriver.Connection connection, ref NetworkSendInterface sendInterface, ref NetworkSendQueueHandle queueHandle, IntPtr userData)
        {
            var relayProtocolData = (RelayProtocolData*)userData;

            var type = UdpCProtocol.Disconnect;
            var token = connection.SendToken;
            var result = SendHeaderOnlyHostMessage(
                type, token, relayProtocolData, ref connection.Address.AsRelayAllocationId(), ref sendInterface, ref queueHandle);
            if (result < 0)
            {
                Debug.LogError("Failed to send Disconnect message to host.");
                return;
            }
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(NetworkProtocol.ProcessSendPingDelegate))]
        public static unsafe void ProcessSendPing(ref NetworkDriver.Connection connection, ref NetworkSendInterface sendInterface, ref NetworkSendQueueHandle queueHandle, IntPtr userData)
        {
            var relayProtocolData = (RelayProtocolData*)userData;

            var type = UdpCProtocol.Ping;
            var token = connection.SendToken;
            var result = SendHeaderOnlyHostMessage(
                type, token, relayProtocolData, ref connection.Address.AsRelayAllocationId(), ref sendInterface, ref queueHandle);
            if (result < 0)
            {
                Debug.LogError("Failed to send Ping message to host.");
                return;
            }
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(NetworkProtocol.ProcessSendPingDelegate))]
        public static unsafe void ProcessSendPong(ref NetworkDriver.Connection connection, ref NetworkSendInterface sendInterface, ref NetworkSendQueueHandle queueHandle, IntPtr userData)
        {
            var relayProtocolData = (RelayProtocolData*)userData;

            var type = UdpCProtocol.Pong;
            var token = connection.SendToken;
            var result = SendHeaderOnlyHostMessage(
                type, token, relayProtocolData, ref connection.Address.AsRelayAllocationId(), ref sendInterface, ref queueHandle);
            if (result < 0)
            {
                Debug.LogError("Failed to send Pong message to host.");
                return;
            }
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(NetworkProtocol.UpdateDelegate))]
        public static void Update(long updateTime, ref NetworkSendInterface sendInterface, ref NetworkSendQueueHandle queueHandle, IntPtr userData)
        {
            unsafe
            {
                var protocolData = (RelayProtocolData*)userData;

                switch (protocolData->ConnectionState)
                {
#if ENABLE_MANAGED_UNITYTLS
                    case RelayConnectionState.Handshake:
                    {
                        if (protocolData->SecureClientState.ClientPtr == null)
                        {
                            // we need to create a config/client
                            var config = (Binding.unitytls_client_config*)UnsafeUtility.Malloc(
                                UnsafeUtility.SizeOf<Binding.unitytls_client_config>(),
                                UnsafeUtility.AlignOf<Binding.unitytls_client_config>(), Allocator.Persistent);

                            *config = new Binding.unitytls_client_config();

                            Binding.unitytls_client_init_config(config);

                            config->dataSendCB = ManagedSecureFunctions.s_SendCallback.Data.Value;
                            config->dataReceiveCB = ManagedSecureFunctions.s_RecvMethod.Data.Value;

                            config->clientAuth = Binding.UnityTLSRole_Client;
                            config->transportProtocol = Binding.UnityTLSTransportProtocol_Datagram;
                            config->clientAuth = Binding.UnityTLSClientAuth_Optional;

                            config->ssl_read_timeout_ms = SecureNetworkProtocol.DefaultParameters.SSLReadTimeoutMs;
                            config->ssl_handshake_timeout_min =
                                SecureNetworkProtocol.DefaultParameters.SSLHandshakeTimeoutMin;
                            config->ssl_handshake_timeout_max =
                                SecureNetworkProtocol.DefaultParameters.SSLHandshakeTimeoutMax;

                            FixedString32Bytes hostname = "relay";
                            config->hostname = hostname.GetUnsafePtr();

                            config->psk = new Binding.unitytls_dataRef()
                            {
                                dataPtr = protocolData->ServerData.HMACKey.Value,
                                dataLen = new UIntPtr(64)
                            };

                            config->pskIdentity = new Binding.unitytls_dataRef()
                            {
                                dataPtr = protocolData->ServerData.AllocationId.Value,
                                dataLen = new UIntPtr((uint)16)
                            };

                            protocolData->SecureClientState.ClientConfig = config;

                            protocolData->SecureClientState.ClientPtr = Binding.unitytls_client_create(Binding.UnityTLSRole_Client, protocolData->SecureClientState.ClientConfig);

                            IntPtr secureUserData = (IntPtr)UnsafeUtility.Malloc(UnsafeUtility.SizeOf<SecureUserData>(),
                                UnsafeUtility.AlignOf<SecureUserData>(), Allocator.Persistent);

                            *(SecureUserData*)secureUserData = new SecureUserData
                            {
                                Interface = default,
                                Remote = default,
                                QueueHandle = default,
                                StreamData = IntPtr.Zero,
                                Size = 0,
                                BytesProcessed = 0
                            };

                            protocolData->SecureClientState.ClientConfig->transportUserData = secureUserData;

                            Binding.unitytls_client_init(protocolData->SecureClientState.ClientPtr);
                        }

                        var currentState = Binding.unitytls_client_get_state(protocolData->SecureClientState.ClientPtr);
                        if (currentState == Binding.UnityTLSClientState_Handshake)
                            return;

                        var data = (SecureUserData*)protocolData->SecureClientState.ClientConfig->transportUserData;

                        SecureNetworkProtocol.SetSecureUserData(IntPtr.Zero, 0, ref protocolData->ServerEndpoint, ref sendInterface, ref queueHandle, data);
                        var handshakeResult = SecureNetworkProtocol.SecureHandshakeStep(ref protocolData->SecureClientState);
                    }
                    break;
#endif
                    case RelayConnectionState.Binding:
                        if (updateTime - protocolData->LastConnectAttempt > protocolData->ConnectTimeoutMS || protocolData->LastUpdateTime == 0)
                        {
                            protocolData->LastConnectAttempt = updateTime;
                            protocolData->LastSentTime = updateTime;

                            const int requirePayloadSize = RelayMessageBind.Length;

                            if (sendInterface.BeginSendMessage.Ptr.Invoke(out var sendHandle, sendInterface.UserData, requirePayloadSize) != 0)
                            {
                                UnityEngine.Debug.LogError("Failed to send a ConnectionRequest packet");
                                return;
                            }

                            var writeResult = WriteBindMessage(ref protocolData->ServerEndpoint, ref sendHandle, ref queueHandle, userData);

                            if (!writeResult)
                            {
                                sendInterface.AbortSendMessage.Ptr.Invoke(ref sendHandle, sendInterface.UserData);
                                return;
                            }

                            if (SendMessage(protocolData, ref sendInterface, ref sendHandle, ref queueHandle) < 0)
                            {
                                Debug.LogError("Failed to send Bind message to relay.");
                                return;
                            }
                        }
                        break;
                    case RelayConnectionState.Bound:
                    case RelayConnectionState.Connected:
                    {
                        if (updateTime - protocolData->LastSentTime >= protocolData->RelayConnectionTimeMS)
                        {
                            if (sendInterface.BeginSendMessage.Ptr.Invoke(out var sendHandle, sendInterface.UserData, RelayMessagePing.Length) != 0)
                            {
                                UnityEngine.Debug.LogError("Failed to send a RelayPingMessage packet");
                                return;
                            }

                            var writeResult = WriteRelayPingMessage(ref protocolData->ServerEndpoint, ref sendHandle, ref queueHandle, userData);

                            if (!writeResult)
                            {
                                sendInterface.AbortSendMessage.Ptr.Invoke(ref sendHandle, sendInterface.UserData);
                                return;
                            }

                            if (SendMessage(protocolData, ref sendInterface, ref sendHandle, ref queueHandle) < 0)
                            {
                                Debug.LogError("Failed to send Ping message to relay.");
                                return;
                            }

                            protocolData->LastSentTime = updateTime;
                        }
                    }
                    break;
                }

                protocolData->LastUpdateTime = updateTime;
            }
        }

        [BurstCompatible]
        private static unsafe bool WriteRelayPingMessage(ref NetworkInterfaceEndPoint serverEndpoint, ref NetworkInterfaceSendHandle sendHandle, ref NetworkSendQueueHandle queueHandle, IntPtr userData)
        {
            var protocolData = (RelayProtocolData*)userData;

            var packet = (byte*)sendHandle.data;
            sendHandle.size = RelayMessagePing.Length;
            if (sendHandle.size > sendHandle.capacity)
            {
                UnityEngine.Debug.LogError("Failed to send a RelayPingMessage packet");
                return false;
            }

            var message = (RelayMessagePing*)packet;
            *message = RelayMessagePing.Create(protocolData->ServerData.AllocationId, 0);

            return true;
        }

        [BurstCompatible]
        private static unsafe bool WriteBindMessage(ref NetworkInterfaceEndPoint serverEndpoint, ref NetworkInterfaceSendHandle sendHandle, ref NetworkSendQueueHandle queueHandle, IntPtr userData)
        {
            var writer = WriterForSendBuffer(RelayMessageBind.Length, ref sendHandle);
            if (writer.IsCreated == false)
            {
                UnityEngine.Debug.LogError("Failed to send a RelayBindMessage packet");
                return false;
            }

            // RelayMessageBind contains unaligned ushort, so we don't want to 'blit' the structure, instead we're using DataStreamWriter
            var protocolData = (RelayProtocolData*)userData;
            RelayMessageBind.Write(writer, 0, protocolData->ServerData.Nonce, protocolData->ServerData.ConnectionData.Value, protocolData->ServerData.HMAC);

            return true;
        }

        static DataStreamWriter WriterForSendBuffer(int requestSize, ref NetworkInterfaceSendHandle sendHandle)
        {
            unsafe
            {
                if (requestSize <= sendHandle.capacity)
                {
                    sendHandle.size = requestSize;
                    return new DataStreamWriter((byte*)sendHandle.data, sendHandle.size);
                }
            }

            return default;
        }
    }
}
