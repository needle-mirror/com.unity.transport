using System;
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
        public static unsafe ref RelayAllocationId AsRelayAllocationId(this NetworkInterfaceEndPoint address)
        {
            return ref *(RelayAllocationId*)address.data;
        }
    }

    public struct RelayNetworkParameter : INetworkParameter
    {
        public RelayServerData ServerData;
        public int RelayConnectionTimeMS;
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
            Connecting = 4,
            Connected = 5,
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
            public ushort ConnectionReceiveToken;
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
        }

        public IntPtr UserData;

        public void Initialize(INetworkParameter[] parameters)
        {
            if (!TryExtractParameters<RelayNetworkParameter>(out var relayConfig, parameters))
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                UnityEngine.Debug.LogWarning("No Relay Protocol configuration parameters were provided");
#endif
            }

            var connectTimeoutMS = NetworkParameterConstants.ConnectTimeoutMS;
            if (TryExtractParameters<NetworkConfigParameter>(out var config, parameters))
            {
                connectTimeoutMS = config.connectTimeoutMS;
            }
            var relayConnectionTimeMS = 9000;
            if (relayConfig.RelayConnectionTimeMS != 0)
            {
                relayConnectionTimeMS = relayConfig.RelayConnectionTimeMS;
            }

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
                    ConnectTimeoutMS = connectTimeoutMS,
                    RelayConnectionTimeMS = relayConnectionTimeMS,
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

                return 1; // 1 = Binding for the NetworkDriver, a full stablished bind is 2
            }
        }

        public unsafe int Connect(INetworkInterface networkInterface, NetworkEndPoint endPoint, out NetworkInterfaceEndPoint address)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (UnsafeUtility.SizeOf<NetworkInterfaceEndPoint>() < UnsafeUtility.SizeOf<RelayAllocationId>())
                throw new InvalidOperationException("RelayAllocationId does not fit the ConnectionAddress size");
#endif

            // We need to convert a endpoint address to a allocation id address
            // For Relay that is always the host allocation id.
            var protocolData = (RelayProtocolData*)UserData;
            address = default;
            fixed(byte* addressPtr = address.data)
            {
                *(RelayAllocationId*)addressPtr = protocolData->HostAllocationId;
            }

            return 0;
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
                computePacketAllocationSize: new TransportFunctionPointer<NetworkProtocol.ComputePacketAllocationSizeDelegate>(ComputePacketAllocationSize),
                processReceive: new TransportFunctionPointer<NetworkProtocol.ProcessReceiveDelegate>(ProcessReceive),
                processSend: new TransportFunctionPointer<NetworkProtocol.ProcessSendDelegate>(ProcessSend),
                processSendConnectionAccept: new TransportFunctionPointer<NetworkProtocol.ProcessSendConnectionAcceptDelegate>(ProcessSendConnectionAccept),
                processSendConnectionRequest: new TransportFunctionPointer<NetworkProtocol.ProcessSendConnectionRequestDelegate>(ProcessSendConnectionRequest),
                processSendDisconnect: new TransportFunctionPointer<NetworkProtocol.ProcessSendDisconnectDelegate>(ProcessSendDisconnect),
                update: new TransportFunctionPointer<NetworkProtocol.UpdateDelegate>(Update),
                needsUpdate: true,
                userData: UserData,
                maxHeaderSize: RelayMessageRelay.Length + UdpCHeader.Length,
                maxFooterSize: 2
            );
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(NetworkProtocol.ComputePacketAllocationSizeDelegate))]
        public static int ComputePacketAllocationSize(ref NetworkDriver.Connection connection, IntPtr userData, ref int dataCapacity, out int dataOffset)
        {
            var capacityCost = dataCapacity == 0 ? RelayMessageRelay.Length : 0;
            var extraSize = dataCapacity == 0 ? 0 : RelayMessageRelay.Length;

            var size = UnityTransportProtocol.ComputePacketAllocationSize(ref connection, userData, ref dataCapacity, out dataOffset);

            dataOffset += RelayMessageRelay.Length;
            dataCapacity -= capacityCost;

            return size + extraSize;
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
                            handshakeResult = SecureNetworkProtocol.UpdateSecureHandshakeState(ref protocolData->SecureClientState);
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
                    if (size != RelayMessageHeader.Length)
                    {
                        UnityEngine.Debug.LogError("Received an invalid Relay Bind Received message: Wrong length");
                        command.Type = ProcessPacketCommandType.Drop;
                        return true;
                    }

                    protocolData->ConnectionState = RelayConnectionState.Bound;
                    command.Type = ProcessPacketCommandType.BindAccept;
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
                    command.AsAddressUpdate.Address = default;
                    command.AsAddressUpdate.NewAddress = default;
                    command.AsAddressUpdate.SessionToken = protocolData->ConnectionReceiveToken;

                    fixed(byte* addressPtr = command.AsAddressUpdate.NewAddress.data)
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
                            command.AsData.Offset += RelayMessageRelay.Length;
                            break;

                        case ProcessPacketCommandType.DataWithImplicitConnectionAccept:
                            command.AsDataWithImplicitConnectionAccept.Offset += RelayMessageRelay.Length;
                            break;

                        case ProcessPacketCommandType.Disconnect:
                            SendRelayDisconnect(
                                protocolData, ref relayMessage.FromAllocationId, ref sendInterface, ref queueHandle);
                            break;
                    }

                    command.ConnectionAddress = default;
                    fixed(byte* addressPtr = command.ConnectionAddress.data)
                    {
                        *(RelayAllocationId*)addressPtr = relayMessage.FromAllocationId;
                    }

                    return true;
            }

            command.Type = ProcessPacketCommandType.Drop;
            return true;
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
                    return -1;
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

        private static unsafe void SendRelayDisconnect(RelayProtocolData* relayProtocolData, ref RelayAllocationId hostAllocationId,
            ref NetworkSendInterface sendInterface, ref NetworkSendQueueHandle queueHandle)
        {
            if (sendInterface.BeginSendMessage.Ptr.Invoke(out var sendHandle, sendInterface.UserData, RelayMessageDisconnect.Length) != 0)
            {
                UnityEngine.Debug.LogError("Failed to send a Disconnect packet to host");
                return;
            }

            var packet = (byte*)sendHandle.data;
            sendHandle.size = RelayMessageDisconnect.Length;
            if (sendHandle.size > sendHandle.capacity)
            {
                sendInterface.AbortSendMessage.Ptr.Invoke(ref sendHandle, sendInterface.UserData);
                UnityEngine.Debug.LogError("Failed to send a Disconnect packet to host");
                return;
            }

            var disconnectMessage = (RelayMessageDisconnect*)packet;
            *disconnectMessage = RelayMessageDisconnect.Create(relayProtocolData->ServerData.AllocationId, hostAllocationId);
#if ENABLE_MANAGED_UNITYTLS
            if (relayProtocolData->ServerData.IsSecure == 1 &&
                (relayProtocolData->SecureState == SecuredRelayConnectionState.Secured))
            {
                var secureUserData =
                    (SecureUserData*)relayProtocolData->SecureClientState.ClientConfig->transportUserData;
                SecureNetworkProtocol.SetSecureUserData(IntPtr.Zero, 0, ref relayProtocolData->ServerEndpoint,
                    ref sendInterface, ref queueHandle, secureUserData);

                var result = Binding.unitytls_client_send_data(relayProtocolData->SecureClientState.ClientPtr,
                    (byte*)sendHandle.data,
                    new UIntPtr((uint)sendHandle.size));

                var sendSize = sendHandle.size;

                // we end up having to abort this handle so we can free it up as DTSL will generate a new one
                // based on the encrypted buffer size
                sendInterface.AbortSendMessage.Ptr.Invoke(ref sendHandle, sendInterface.UserData);

                if (result != Binding.UNITYTLS_SUCCESS)
                {
                    Debug.LogError($"Secure Send failed with result {result}");
                    return;
                }
            }
            else
#endif
            {
                sendInterface.EndSendMessage.Ptr.Invoke(ref sendHandle, ref relayProtocolData->ServerEndpoint, sendInterface.UserData, ref queueHandle);
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
            relayProtocolData->LastSentTime = relayProtocolData->LastUpdateTime;

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

        private static unsafe int SendHeaderOnlyHostMessage(UdpCProtocol type, ushort token, RelayProtocolData* relayProtocolData,
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

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(NetworkProtocol.ProcessSendConnectionRequestDelegate))]
        public static void ProcessSendConnectionRequest(ref NetworkDriver.Connection connection, ref NetworkSendInterface sendInterface, ref NetworkSendQueueHandle queueHandle, IntPtr userData)
        {
            unsafe
            {
                var relayProtocolData = (RelayProtocolData*)userData;

                relayProtocolData->ServerData.ConnectionSessionId = connection.ReceiveToken;
                relayProtocolData->ConnectionState = RelayConnectionState.Connecting;
                relayProtocolData->ConnectionReceiveToken = connection.ReceiveToken;

                if (relayProtocolData->HostAllocationId == default)
                {
                    if (sendInterface.BeginSendMessage.Ptr.Invoke(out var sendHandle, sendInterface.UserData, RelayMessageConnectRequest.Length) != 0)
                    {
                        UnityEngine.Debug.LogError("Failed to send a ConnectionRequest packet");
                        return;
                    }

                    var writeResult = WriteConnectionRequestToRelay(ref relayProtocolData->ServerData.AllocationId, ref relayProtocolData->ServerData.HostConnectionData,
                        ref relayProtocolData->ServerEndpoint, ref sendHandle, ref queueHandle);

                    if (!writeResult)
                    {
                        sendInterface.AbortSendMessage.Ptr.Invoke(ref sendHandle, sendInterface.UserData);
                        return;
                    }

                    if (SendMessage(relayProtocolData, ref sendInterface, ref sendHandle, ref queueHandle) < 0)
                    {
                        Debug.LogError("Failed to send Connection Request message to relay.");
                        return;
                    }
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

        [BurstCompatible]
        public static unsafe bool WriteConnectionRequestToRelay(ref RelayAllocationId allocationId, ref RelayConnectionData hostConnectionData, ref NetworkInterfaceEndPoint serverEndpoint, ref NetworkInterfaceSendHandle sendHandle, ref NetworkSendQueueHandle queueHandle)
        {
            var packet = (byte*)sendHandle.data;
            sendHandle.size = RelayMessageConnectRequest.Length;
            if (sendHandle.size > sendHandle.capacity)
            {
                UnityEngine.Debug.LogError("Failed to send a ConnectionRequest packet");
                return false;
            }

            var message = (RelayMessageConnectRequest*)packet;
            *message = RelayMessageConnectRequest.Create(
                allocationId,
                hostConnectionData
            );

            return true;
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(NetworkProtocol.ProcessSendDisconnectDelegate))]
        public static unsafe void ProcessSendDisconnect(ref NetworkDriver.Connection connection, ref NetworkSendInterface sendInterface, ref NetworkSendQueueHandle queueHandle, IntPtr userData)
        {
            var relayProtocolData = (RelayProtocolData*)userData;

            // Host Disconnect
            var type = UdpCProtocol.Disconnect;
            var token = connection.SendToken;
            var result = SendHeaderOnlyHostMessage(
                type, token, relayProtocolData, ref connection.Address.AsRelayAllocationId(), ref sendInterface, ref queueHandle);
            if (result < 0)
            {
                Debug.LogError("Failed to send Disconnect message to host.");
                return;
            }

            // Relay Disconnect
            if (sendInterface.BeginSendMessage.Ptr.Invoke(out var sendHandle, sendInterface.UserData, RelayMessageDisconnect.Length) != 0)
            {
                UnityEngine.Debug.LogError("Failed to send a Disconnect packet to host");
                return;
            }

            var packet = (byte*)sendHandle.data;
            sendHandle.size = RelayMessageDisconnect.Length;
            if (sendHandle.size > sendHandle.capacity)
            {
                sendInterface.AbortSendMessage.Ptr.Invoke(ref sendHandle, sendInterface.UserData);
                UnityEngine.Debug.LogError("Failed to send a Disconnect packet to host");
                return;
            }

            var disconnectMessage = (RelayMessageDisconnect*)packet;
            *disconnectMessage = RelayMessageDisconnect.Create(relayProtocolData->ServerData.AllocationId, connection.Address.AsRelayAllocationId());

            if (SendMessage(relayProtocolData, ref sendInterface, ref sendHandle, ref queueHandle) < 0)
            {
                Debug.LogError("Failed to send Disconnect message to relay.");
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
                        var handshakeResult = SecureNetworkProtocol.UpdateSecureHandshakeState(ref protocolData->SecureClientState);
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
