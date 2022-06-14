#if ENABLE_MANAGED_UNITYTLS

using System;
using System.Runtime.InteropServices;
using AOT;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Networking.Transport.Protocols;

using Unity.TLS.LowLevel;
using UnityEngine;
using size_t = System.UIntPtr;

namespace Unity.Networking.Transport.TLS
{
    internal struct SecureClientState
    {
        public unsafe Binding.unitytls_client* ClientPtr;
        public unsafe Binding.unitytls_client_config* ClientConfig;

        // Only set (and used) by the client when sending the Connection Request message after
        // successfully completing the handshake.
        public SessionIdToken ReceiveToken;

        // During the handshake, tracks the last time UpdateSecureHandshakeState was called.
        // Used to prune old half-open connections (handshake not completed).
        public long LastHandshakeUpdate;
    }

    struct SecureNetworkProtocolData
    {
        public UnsafeHashMap<NetworkInterfaceEndPoint, SecureClientState> SecureClients;
        public FixedString4096Bytes  Pem;
        public FixedString4096Bytes  Rsa;
        public FixedString4096Bytes  RsaKey;
        public FixedString32Bytes    Hostname;
        public uint                  Protocol;
        public uint                  SSLReadTimeoutMs;
        public uint                  SSLHandshakeTimeoutMax;
        public uint                  SSLHandshakeTimeoutMin;
        public uint                  ClientAuth;
        public long                  LastUpdate;
        public long                  LastHalfOpenPrune;
    }

    internal struct SecureUserData
    {
        public IntPtr StreamData;
        public NetworkSendInterface Interface;
        public NetworkInterfaceEndPoint Remote;
        public NetworkSendQueueHandle QueueHandle;
        public int Size;
        public int BytesProcessed;
    }

    internal static class ManagedSecureFunctions
    {
        private const int UNITYTLS_ERR_SSL_WANT_READ = -0x6900;
        private const int UNITYTLS_ERR_SSL_WANT_WRITE = -0x6880;

        private static Binding.unitytls_client_data_send_callback s_sendCallback;
        private static Binding.unitytls_client_data_receive_callback s_recvCallback;

        private static bool IsInitialized;

        private struct ManagedSecureFunctionsKey {}

        internal static readonly SharedStatic<FunctionPointer<Binding.unitytls_client_data_send_callback>>
        s_SendCallback = SharedStatic<FunctionPointer<Binding.unitytls_client_data_send_callback>>
            .GetOrCreate<FunctionPointer<Binding.unitytls_client_data_send_callback>, ManagedSecureFunctionsKey>();

        internal static readonly SharedStatic<FunctionPointer<Binding.unitytls_client_data_receive_callback>>
        s_RecvMethod =
            SharedStatic<FunctionPointer<Binding.unitytls_client_data_receive_callback>>
                .GetOrCreate<FunctionPointer<Binding.unitytls_client_data_receive_callback>, ManagedSecureFunctionsKey>();

        internal static void Initialize()
        {
            if (IsInitialized) return;
            IsInitialized = true;

            unsafe
            {
                s_sendCallback = SecureDataSendCallback;
                s_recvCallback = SecureDataReceiveCallback;

                s_SendCallback.Data = new FunctionPointer<Binding.unitytls_client_data_send_callback>(Marshal.GetFunctionPointerForDelegate(s_sendCallback));
                s_RecvMethod.Data = new FunctionPointer<Binding.unitytls_client_data_receive_callback>(Marshal.GetFunctionPointerForDelegate(s_recvCallback));
            }
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(Binding.unitytls_client_data_send_callback))]
        static unsafe int SecureDataSendCallback(
            IntPtr userData,
            byte* data,
            UIntPtr dataLen,
            uint status)
        {
            var protocolData = (SecureUserData*)userData;
            if (protocolData->Interface.BeginSendMessage.Ptr.Invoke(out var sendHandle,
                protocolData->Interface.UserData, (int)dataLen.ToUInt32()) != 0)
            {
                return UNITYTLS_ERR_SSL_WANT_WRITE;
            }

            sendHandle.size = (int)dataLen.ToUInt32();
            byte* packet = (byte*)sendHandle.data;
            UnsafeUtility.MemCpy(packet, data, (long)dataLen.ToUInt64());

            return protocolData->Interface.EndSendMessage.Ptr.Invoke(ref sendHandle, ref protocolData->Remote,
                protocolData->Interface.UserData, ref protocolData->QueueHandle);
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(Binding.unitytls_client_data_receive_callback))]
        static unsafe int SecureDataReceiveCallback(
            IntPtr userData,
            byte* data,
            UIntPtr dataLen,
            uint status)
        {
            var protocolData = (SecureUserData*)userData;
            var packet = (byte*)protocolData->StreamData;
            if (packet == null || protocolData->Size <= 0)
            {
                return UNITYTLS_ERR_SSL_WANT_READ;
            }

            // This is a case where we process an invalid record
            // and the internal statemachine trys to read the next record
            // and we don't have any data. Eventually one side will timeout
            // and resend
            if (protocolData->BytesProcessed != 0)
            {
                return UNITYTLS_ERR_SSL_WANT_READ;
            }

            UnsafeUtility.MemCpy(data, packet, protocolData->Size);
            protocolData->BytesProcessed = protocolData->Size;
            return protocolData->Size;
        }
    }

    [BurstCompile]
    internal unsafe struct SecureNetworkProtocol : INetworkProtocol
    {
        public IntPtr UserData;

        public static readonly SecureNetworkProtocolParameter DefaultParameters = new SecureNetworkProtocolParameter
        {
            Protocol = SecureTransportProtocol.DTLS,
            SSLReadTimeoutMs = 0,
            SSLHandshakeTimeoutMin = 1000,
            SSLHandshakeTimeoutMax = 60000,
            ClientAuthenticationPolicy = SecureClientAuthPolicy.Optional
        };

        private static void CreateSecureClient(uint role, SecureClientState* state)
        {
            var client = Binding.unitytls_client_create(role, state->ClientConfig);
            state->ClientPtr = client;
        }

        private static Binding.unitytls_client_config* GetSecureClientConfig(SecureNetworkProtocolData * protocolData)
        {
            var config = (Binding.unitytls_client_config*)UnsafeUtility.Malloc(
                UnsafeUtility.SizeOf<Binding.unitytls_client_config>(),
                UnsafeUtility.AlignOf<Binding.unitytls_client_config>(), Allocator.Persistent);

            *config = new Binding.unitytls_client_config();

            Binding.unitytls_client_init_config(config);

            config->dataSendCB = ManagedSecureFunctions.s_SendCallback.Data.Value;
            config->dataReceiveCB = ManagedSecureFunctions.s_RecvMethod.Data.Value;
            config->logCallback = IntPtr.Zero;

            // Going to set this for None for now
            config->clientAuth = Binding.UnityTLSRole_None;

            config->transportProtocol = protocolData->Protocol;
            config->clientAuth = protocolData->ClientAuth;

            config->ssl_read_timeout_ms = protocolData->SSLReadTimeoutMs;
            config->ssl_handshake_timeout_min = protocolData->SSLHandshakeTimeoutMin;
            config->ssl_handshake_timeout_max = protocolData->SSLHandshakeTimeoutMax;

            return config;
        }

        public void Initialize(NetworkSettings settings)
        {
            unsafe
            {
                ManagedSecureFunctions.Initialize();

                // we need Secure Transport related configs because we need the user to pass int he keys?
                var secureConfig = settings.GetSecureParameters();

                //TODO: We need to validate that you have a config that makes sense for what you are trying to do
                // should this be something we allow for expressing in the config? like which role you are?

#if ENABLE_UNITY_COLLECTIONS_CHECKS && !UNITY_WEBGL
                // If we have baselib configs we need to make sure they are of proper size
                if (settings.TryGet<BaselibNetworkParameter>(out var baselibConfig))
                {
                    // TODO: We do not support fragmented messages at the moment :(
                    // and the largest packet that mbedTLS sends is 1800 which is the key
                    // exchange..
                    if (baselibConfig.maximumPayloadSize < 2000)
                    {
                        UnityEngine.Debug.LogWarning(
                            "Secure Protocol Requires the payload size for the Baselib Interface to be at least 2000KB");
                    }
                }
#endif

                UserData = (IntPtr)UnsafeUtility.Malloc(UnsafeUtility.SizeOf<SecureNetworkProtocolData>(),
                    UnsafeUtility.AlignOf<SecureNetworkProtocolData>(), Allocator.Persistent);
                *(SecureNetworkProtocolData*)UserData = new SecureNetworkProtocolData
                {
                    SecureClients = new UnsafeHashMap<NetworkInterfaceEndPoint, SecureClientState>(1, Allocator.Persistent),
                    Rsa = secureConfig.Rsa,
                    RsaKey = secureConfig.RsaKey,
                    Pem = secureConfig.Pem,
                    Hostname = secureConfig.Hostname,
                    Protocol = (uint)secureConfig.Protocol,
                    SSLReadTimeoutMs = secureConfig.SSLReadTimeoutMs,
                    SSLHandshakeTimeoutMin = secureConfig.SSLHandshakeTimeoutMin,
                    SSLHandshakeTimeoutMax = secureConfig.SSLHandshakeTimeoutMax,
                    ClientAuth = (uint)secureConfig.ClientAuthenticationPolicy
                };
            }
        }

        public static void DisposeSecureClient(ref SecureClientState state)
        {
            if (state.ClientConfig->transportUserData.ToPointer() != null)
                UnsafeUtility.Free(state.ClientConfig->transportUserData.ToPointer(), Allocator.Persistent);

            if (state.ClientConfig != null)
                UnsafeUtility.Free((void*)state.ClientConfig, Allocator.Persistent);

            state.ClientConfig = null;

            if (state.ClientPtr != null)
                Binding.unitytls_client_destroy(state.ClientPtr);
        }

        public void Dispose()
        {
            unsafe
            {
                var protocolData = (SecureNetworkProtocolData*)UserData;
                var keys = protocolData->SecureClients.GetKeyArray(Allocator.Temp);
                for (int connectionIndex = 0; connectionIndex < keys.Length; ++connectionIndex)
                {
                    var connection = protocolData->SecureClients[keys[connectionIndex]];

                    DisposeSecureClient(ref connection);

                    protocolData->SecureClients.Remove(keys[connectionIndex]);
                }

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

            return 0;
        }

        public int CreateConnectionAddress(INetworkInterface networkInterface, NetworkEndPoint remoteEndpoint, out NetworkInterfaceEndPoint remoteAddress)
        {
            remoteAddress = default;
            return networkInterface.CreateInterfaceEndPoint(remoteEndpoint, out remoteAddress);
        }

        public NetworkEndPoint GetRemoteEndPoint(INetworkInterface networkInterface, NetworkInterfaceEndPoint address)
        {
            return networkInterface.GetGenericEndPoint(address);
        }

        public int Listen(INetworkInterface networkInterface)
        {
            return networkInterface.Listen();
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
                maxHeaderSize: UdpCHeader.Length,
                maxFooterSize: SessionIdToken.k_Length
            );
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(NetworkProtocol.ComputePacketOverheadDelegate))]
        public static int ComputePacketOverhead(ref NetworkDriver.Connection connection, out int dataOffset)
        {
            return UnityTransportProtocol.ComputePacketOverhead(ref connection, out dataOffset);
        }

        public static bool ServerShouldStep(uint currentState)
        {
            // these are the initial states from the server ?
            switch (currentState)
            {
                case Binding.UNITYTLS_SSL_HANDSHAKE_HELLO_REQUEST:
                case Binding.UNITYTLS_SSL_HANDSHAKE_CLIENT_HELLO:
                case Binding.UNITYTLS_SSL_HANDSHAKE_SERVER_HELLO:
                case Binding.UNITYTLS_SSL_HANDSHAKE_SERVER_CERTIFICATE:
                case Binding.UNITYTLS_SSL_HANDSHAKE_SERVER_KEY_EXCHANGE:
                case Binding.UNITYTLS_SSL_HANDSHAKE_CERTIFICATE_REQUEST:
                case Binding.UNITYTLS_SSL_HANDSHAKE_SERVER_HELLO_DONE:
                case Binding.UNITYTLS_SSL_HANDSHAKE_SERVER_CHANGE_CIPHER_SPEC:
                case Binding.UNITYTLS_SSL_HANDSHAKE_SERVER_FINISHED:
                case Binding.UNITYTLS_SSL_HANDSHAKE_HANDSHAKE_WRAPUP:
                case Binding.UNITYTLS_SSL_HANDSHAKE_HANDSHAKE_OVER:
                case Binding.UNITYTLS_SSL_HANDSHAKE_HANDSHAKE_FLUSH_BUFFERS:
                    return true;
            }

            return false;
        }

        private static bool ClientShouldStep(uint currentState)
        {
            // these are the initial states from the server ?
            switch (currentState)
            {
                case Binding.UNITYTLS_SSL_HANDSHAKE_HELLO_REQUEST:
                case Binding.UNITYTLS_SSL_HANDSHAKE_CLIENT_HELLO:
                    return true;
                case Binding.UNITYTLS_SSL_HANDSHAKE_SERVER_HELLO:
                case Binding.UNITYTLS_SSL_HANDSHAKE_SERVER_CERTIFICATE:
                case Binding.UNITYTLS_SSL_HANDSHAKE_SERVER_KEY_EXCHANGE:
                case Binding.UNITYTLS_SSL_HANDSHAKE_CERTIFICATE_REQUEST:
                    return false;
                case Binding.UNITYTLS_SSL_HANDSHAKE_SERVER_HELLO_DONE:
                case Binding.UNITYTLS_SSL_HANDSHAKE_CLIENT_CERTIFICATE:
                case Binding.UNITYTLS_SSL_HANDSHAKE_CLIENT_KEY_EXCHANGE:
                case Binding.UNITYTLS_SSL_HANDSHAKE_CERTIFICATE_VERIFY:
                case Binding.UNITYTLS_SSL_HANDSHAKE_CLIENT_CHANGE_CIPHER_SPEC:
                case Binding.UNITYTLS_SSL_HANDSHAKE_CLIENT_FINISHED:
                case Binding.UNITYTLS_SSL_HANDSHAKE_HANDSHAKE_WRAPUP:
                case Binding.UNITYTLS_SSL_HANDSHAKE_HANDSHAKE_OVER:
                case Binding.UNITYTLS_SSL_HANDSHAKE_HANDSHAKE_FLUSH_BUFFERS:
                    return true;
            }

            return false;
        }

        internal static void SetSecureUserData(
            IntPtr inStream,
            int size,
            ref NetworkInterfaceEndPoint remote,
            ref NetworkSendInterface networkSendInterface,
            ref NetworkSendQueueHandle queueHandle,
            SecureUserData* secureUserData)
        {
            secureUserData->Interface = networkSendInterface;
            secureUserData->Remote = remote;
            secureUserData->QueueHandle = queueHandle;
            secureUserData->Size = size;
            secureUserData->StreamData = inStream;
            secureUserData->BytesProcessed = 0;
        }

        private static bool CreateNewSecureClientState(
            ref NetworkInterfaceEndPoint endpoint,
            uint tlsRole,
            SecureNetworkProtocolData* protocolData,
            SessionIdToken receiveToken = default)
        {
            if (protocolData->SecureClients.TryAdd(endpoint, new SecureClientState()))
            {
                var secureClient = protocolData->SecureClients[endpoint];
                secureClient.ClientConfig = GetSecureClientConfig(protocolData);
                secureClient.ReceiveToken = receiveToken;

                CreateSecureClient(tlsRole, &secureClient);

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

                secureClient.ClientConfig->transportUserData = secureUserData;

                if (protocolData->Hostname != default)
                {
                    secureClient.ClientConfig->hostname = protocolData->Hostname.GetUnsafePtr();
                }
                else
                {
                    secureClient.ClientConfig->hostname = null;
                }

                if (protocolData->Pem != default)
                {
                    secureClient.ClientConfig->caPEM = new Binding.unitytls_dataRef()
                    {
                        dataPtr = protocolData->Pem.GetUnsafePtr(),
                        dataLen = new UIntPtr((uint)protocolData->Pem.Length)
                    };
                }
                else
                {
                    secureClient.ClientConfig->caPEM = new Binding.unitytls_dataRef()
                    {
                        dataPtr = null,
                        dataLen = new UIntPtr(0)
                    };
                }

                if (protocolData->Rsa != default && protocolData->RsaKey != default)
                {
                    secureClient.ClientConfig->serverPEM = new Binding.unitytls_dataRef()
                    {
                        dataPtr = protocolData->Rsa.GetUnsafePtr(),
                        dataLen = new UIntPtr((uint)protocolData->Rsa.Length)
                    };

                    secureClient.ClientConfig->privateKeyPEM = new Binding.unitytls_dataRef()
                    {
                        dataPtr = protocolData->RsaKey.GetUnsafePtr(),
                        dataLen = new UIntPtr((uint)protocolData->RsaKey.Length)
                    };
                }
                else
                {
                    secureClient.ClientConfig->serverPEM = new Binding.unitytls_dataRef()
                    {
                        dataPtr = null,
                        dataLen = new UIntPtr(0)
                    };

                    secureClient.ClientConfig->privateKeyPEM = new Binding.unitytls_dataRef()
                    {
                        dataPtr = null,
                        dataLen = new UIntPtr(0)
                    };
                }

                Binding.unitytls_client_init(secureClient.ClientPtr);

                protocolData->SecureClients[endpoint] = secureClient;
            }

            return false;
        }

        internal static uint SecureHandshakeStep(ref SecureClientState clientAgent)
        {
            // So now we need to check which role we are ?
            var isServer = Binding.unitytls_client_get_role(clientAgent.ClientPtr) == Binding.UnityTLSRole_Server;
            // we should do server things
            bool shouldStep = true;
            uint result = Binding.UNITYTLS_HANDSHAKE_STEP;
            do
            {
                shouldStep = false;
                result = Binding.unitytls_client_handshake(
                    clientAgent.ClientPtr);

                // this was a case where properly stepped handshake
                if (result == Binding.UNITYTLS_HANDSHAKE_STEP)
                {
                    uint currentState = Binding.unitytls_client_get_handshake_state(clientAgent.ClientPtr);
                    shouldStep = isServer ? ServerShouldStep(currentState) : ClientShouldStep(currentState);
                }
            }
            while (shouldStep);

            return result;
        }

        private unsafe static uint UpdateSecureHandshakeState(
            SecureNetworkProtocolData* protocolData, ref NetworkInterfaceEndPoint endpoint)
        {
            var secureClient = protocolData->SecureClients[endpoint];

            secureClient.LastHandshakeUpdate = protocolData->LastUpdate;
            protocolData->SecureClients[endpoint] = secureClient;

            return SecureHandshakeStep(ref secureClient);
        }

        private unsafe static void PruneHalfOpenConnections(SecureNetworkProtocolData* protocolData)
        {
            var endpoints = protocolData->SecureClients.GetKeyArray(Allocator.Temp);
            bool pruned = false;

            for (int i = 0; i < endpoints.Length; i++)
            {
                var secureClient = protocolData->SecureClients[endpoints[i]];
                var state = Binding.unitytls_client_get_state(secureClient.ClientPtr);

                // After the maximum handshake timeout is a good time to prune half-open connections
                // since at that point there is no progress possible on the handshake. By default
                // it's also a very generous timeout (a minute).
                //
                // The check on LastHandshakeUpdate being greater than 0 is required in the case
                // where a client hello is received before the very first update of the protocol.
                // Unlikely to occur in practice (the client hello would have to come in right
                // between the server's bind and its first update), but common in automated tests.
                if (state == Binding.UnityTLSClientState_Handshake &&
                    secureClient.LastHandshakeUpdate > 0 &&
                    protocolData->LastUpdate - secureClient.LastHandshakeUpdate > protocolData->SSLHandshakeTimeoutMax)
                {
                    DisposeSecureClient(ref secureClient);
                    protocolData->SecureClients.Remove(endpoints[i]);

                    pruned = true;
                }
            }

            // It would be more useful to log each client that has been pruned (we could show the
            // details of the client's endpoint), but a malicious actor could abuse that to spam our
            // logs with many half-open connections. At worst here we'll only log once every second
            // (the default value of SSLHandshakeTimeoutMin).
            if (pruned)
            {
                Debug.LogError("Had to prune half-open connections (clients with unfinished TLS handshakes).");
            }

            endpoints.Dispose();
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(NetworkProtocol.ProcessReceiveDelegate))]
        public static void ProcessReceive(IntPtr stream, ref NetworkInterfaceEndPoint endpoint, int size,
            ref NetworkSendInterface sendInterface, ref NetworkSendQueueHandle queueHandle, IntPtr userData,
            ref ProcessPacketCommand command)
        {
            unsafe
            {
                var protocolData = (SecureNetworkProtocolData*)userData;

                // We assume this is a server if we need to create a new SecureClientState and the reason
                // for this is because the client always sends the Connection Request message and we process that
                // and then we check if we have heard from this client before and if not then we need to create one
                // and its assume that client is in the server role and would be validating all incoming connections
                CreateNewSecureClientState(ref endpoint, Binding.UnityTLSRole_Server, protocolData);

                var secureClient = protocolData->SecureClients[endpoint];
                var secureUserData = (SecureUserData*)secureClient.ClientConfig->transportUserData;

                SetSecureUserData(stream, size, ref endpoint, ref sendInterface, ref queueHandle, secureUserData);
                var clientState = Binding.unitytls_client_get_state(secureClient.ClientPtr);
                uint handshakeResult = Binding.UNITYTLS_SUCCESS;

                // check and see if we are still in the handshake :D
                if (clientState == Binding.UnityTLSClientState_Handshake
                    || clientState == Binding.UnityTLSClientState_Init)
                {
                    bool shouldRunAgain = false;
                    do
                    {
                        handshakeResult = UpdateSecureHandshakeState(protocolData, ref endpoint);
                        clientState = Binding.unitytls_client_get_state(secureClient.ClientPtr);
                        shouldRunAgain = (size != 0 && secureUserData->BytesProcessed == 0 && clientState == Binding.UnityTLSClientState_Handshake);
                    }
                    while (shouldRunAgain);

                    // If the handshake has completed and we're a client, immediately send the
                    // Connection Request message. This avoids having to wait for the driver to
                    // timeout and resend it (which could be a while).
                    var role = Binding.unitytls_client_get_role(secureClient.ClientPtr);
                    if (role == Binding.UnityTLSRole_Client && clientState == Binding.UnityTLSClientState_Messaging)
                    {
                        SendConnectionRequest(
                            secureClient.ReceiveToken, secureClient, ref endpoint, ref sendInterface, ref queueHandle);
                    }

                    command.Type = ProcessPacketCommandType.Drop;
                }
                else if (clientState == Binding.UnityTLSClientState_Messaging)
                {
                    var buffer = new NativeArray<byte>(NetworkParameterConstants.MTU, Allocator.Temp);
                    var bytesRead = new UIntPtr();
                    var result = Binding.unitytls_client_read_data(secureClient.ClientPtr,
                        (byte*)buffer.GetUnsafePtr(), new UIntPtr(NetworkParameterConstants.MTU),
                        &bytesRead);

                    if (result != Binding.UNITYTLS_SUCCESS)
                    {
                        // Don't log an error here. There are normal situations where we hit this
                        // situation. For example if we get a retransmitted handshake message after
                        // the handshake is completed (if the handshake minimum timeout is set too
                        // low for the network conditions, this can occur frequently).
                        command.Type = ProcessPacketCommandType.Drop;
                        return;
                    }

                    // when we have a proper read we need to copy that data into the stream. It should be noted
                    // that this copy does change data we don't technically own.
                    UnsafeUtility.MemCpy((void*)stream, buffer.GetUnsafePtr(), bytesRead.ToUInt32());

                    UnityTransportProtocol.ProcessReceive(stream,
                        ref endpoint,
                        (int)bytesRead.ToUInt32(),
                        ref sendInterface,
                        ref queueHandle,
                        IntPtr.Zero,
                        ref command);

                    if (command.Type == ProcessPacketCommandType.Disconnect)
                    {
                        // So we got a disconnect message we need to clean up the agent
                        DisposeSecureClient(ref secureClient);
                        protocolData->SecureClients.Remove(endpoint);
                        return;
                    }
                }

                clientState = Binding.unitytls_client_get_state(secureClient.ClientPtr);
                if (clientState == Binding.UnityTLSClientState_Fail)
                {
                    // Failed client on server finish state means the handshake has failed, likely
                    // due to a certificate validation failure.
                    if (handshakeResult == Binding.UNITYTLS_SSL_HANDSHAKE_SERVER_FINISHED)
                    {
                        UnityEngine.Debug.LogError("Secure handshake failure (likely caused by certificate validation failure).");
                    }

                    command.Type = ProcessPacketCommandType.Drop;

                    // we should cleanup the connection state ?
                    DisposeSecureClient(ref secureClient);

                    protocolData->SecureClients.Remove(endpoint);
                }
            }
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(NetworkProtocol.ProcessSendDelegate))]
        public static int ProcessSend(ref NetworkDriver.Connection connection, bool hasPipeline, ref NetworkSendInterface sendInterface, ref NetworkInterfaceSendHandle sendHandle, ref NetworkSendQueueHandle queueHandle, IntPtr userData)
        {
            var protocolData = (SecureNetworkProtocolData*)userData;

            CreateNewSecureClientState(ref connection.Address, Binding.UnityTLSRole_Server, protocolData);

            var secureClient = protocolData->SecureClients[connection.Address];
            var secureUserData = (SecureUserData*)secureClient.ClientConfig->transportUserData;

            SetSecureUserData(IntPtr.Zero, 0, ref connection.Address, ref sendInterface, ref queueHandle, secureUserData);

            UnityTransportProtocol.WriteSendMessageHeader(ref connection, hasPipeline, ref sendHandle, 0);

            // we need to free up the current handle before we call send because we could be at capacity and thus
            // when we go and try to get a new handle it will fail.
            var buffer = new NativeArray<byte>(sendHandle.size, Allocator.Temp);
            UnsafeUtility.MemCpy(buffer.GetUnsafePtr(), (void*)sendHandle.data, sendHandle.size);

            // We end up having to abort this handle so we can free it up as DTLS will generate a
            // new one based on the encrypted buffer size.
            sendInterface.AbortSendMessage.Ptr.Invoke(ref sendHandle, sendInterface.UserData);

            var result = Binding.unitytls_client_send_data(secureClient.ClientPtr, (byte*)buffer.GetUnsafePtr(), new UIntPtr((uint)buffer.Length));

            if (result != Binding.UNITYTLS_SUCCESS)
            {
                Debug.LogError($"Secure Send failed with result {result}");
                // Error is likely caused by a connection that's closed or not established yet.
                return (int)Error.StatusCode.NetworkStateMismatch;
            }

            return buffer.Length;
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(NetworkProtocol.ProcessSendConnectionAcceptDelegate))]
        public static void ProcessSendConnectionAccept(ref NetworkDriver.Connection connection,
            ref NetworkSendInterface sendInterface, ref NetworkSendQueueHandle queueHandle, IntPtr userData)
        {
            unsafe
            {
                var protocolData = (SecureNetworkProtocolData*)userData;
                var secureClient = protocolData->SecureClients[connection.Address];

                var packet = new NativeArray<byte>(UdpCHeader.Length + SessionIdToken.k_Length, Allocator.Temp);
                var size = WriteConnectionAcceptMessage(ref connection, (byte*)packet.GetUnsafePtr(), packet.Length);

                if (size < 0)
                {
                    UnityEngine.Debug.LogError("Failed to send a ConnectionAccept packet");
                    return;
                }

                var secureUserData = (SecureUserData*)secureClient.ClientConfig->transportUserData;
                SetSecureUserData(IntPtr.Zero, 0, ref connection.Address, ref sendInterface, ref queueHandle, secureUserData);

                var result = Binding.unitytls_client_send_data(secureClient.ClientPtr, (byte*)packet.GetUnsafePtr(), new UIntPtr((uint)packet.Length));
                if (result != Binding.UNITYTLS_SUCCESS)
                {
                    Debug.LogError($"Secure Send failed with result {result}");
                }
            }
        }

        [BurstCompile(DisableDirectCall = true)]
        internal static unsafe int WriteConnectionAcceptMessage(ref NetworkDriver.Connection connection, byte* packet, int capacity)
        {
            var size = UdpCHeader.Length;

            if (connection.DidReceiveData == 0)
                size += SessionIdToken.k_Length;

            if (size > capacity)
            {
                UnityEngine.Debug.LogError("Failed to create a ConnectionAccept packet: size exceeds capacity");
                return -1;
            }

            var header = (UdpCHeader*)packet;
            //Avoid use of 'new' keyword for UdpCHeader now until Mono fix backport gets in; it ignores the StructLayout.Size parameter
            //and allocates 16 bytes instead of 10
            header->Type = (byte)UdpCProtocol.ConnectionAccept;
            header->SessionToken = connection.SendToken;
            header->Flags = 0;

            if (connection.DidReceiveData == 0)
            {
                header->Flags |= UdpCHeader.HeaderFlags.HasConnectToken;
                *(SessionIdToken*)(packet + UdpCHeader.Length) = connection.ReceiveToken;
            }

            return size;
        }

        private static unsafe void SendConnectionRequest(SessionIdToken token, SecureClientState secureClient,
            ref NetworkInterfaceEndPoint address, ref NetworkSendInterface sendInterface, ref NetworkSendQueueHandle queueHandle)
        {
            var packet = new NativeArray<byte>(UdpCHeader.Length, Allocator.Temp);
            var header = (UdpCHeader*)packet.GetUnsafePtr();
            header->Type = (byte)UdpCProtocol.ConnectionRequest;
            header->SessionToken = token;
            header->Flags = 0;

            var secureUserData = (SecureUserData*)secureClient.ClientConfig->transportUserData;
            SetSecureUserData(IntPtr.Zero, 0, ref address, ref sendInterface, ref queueHandle, secureUserData);

            var result = Binding.unitytls_client_send_data(secureClient.ClientPtr,
                (byte*)packet.GetUnsafePtr(), new UIntPtr((uint)packet.Length));
            if (result != Binding.UNITYTLS_SUCCESS)
            {
                Debug.LogError("We have failed to Send Encrypted SendConnectionRequest");
            }
        }

        private static unsafe uint SendHeaderOnlyMessage(UdpCProtocol type, SessionIdToken token, SecureClientState secureClient,
            ref NetworkDriver.Connection connection, ref NetworkSendInterface sendInterface, ref NetworkSendQueueHandle queueHandle)
        {
            var packet = new NativeArray<byte>(UdpCHeader.Length, Allocator.Temp);

            var header = (UdpCHeader*)packet.GetUnsafePtr();
            header->Type = (byte)type;
            header->SessionToken = token;
            header->Flags = 0;

            var secureUserData = (SecureUserData*)secureClient.ClientConfig->transportUserData;
            SetSecureUserData(IntPtr.Zero, 0, ref connection.Address, ref sendInterface, ref queueHandle, secureUserData);

            return Binding.unitytls_client_send_data(secureClient.ClientPtr, (byte*)packet.GetUnsafePtr(), new UIntPtr((uint)packet.Length));
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(NetworkProtocol.ConnectDelegate))]
        public static void Connect(ref NetworkDriver.Connection connection,
            ref NetworkSendInterface sendInterface, ref NetworkSendQueueHandle queueHandle, IntPtr userData)
        {
            unsafe
            {
                var protocolData = (SecureNetworkProtocolData*)userData;
                CreateNewSecureClientState(
                    ref connection.Address, Binding.UnityTLSRole_Client, protocolData, connection.ReceiveToken);

                var secureClient = protocolData->SecureClients[connection.Address];

                var secureUserData = (SecureUserData*)secureClient.ClientConfig->transportUserData;
                SetSecureUserData(IntPtr.Zero, 0, ref connection.Address, ref sendInterface, ref queueHandle, secureUserData);

                var currentState = Binding.unitytls_client_get_state(secureClient.ClientPtr);

                if (currentState == Binding.UnityTLSClientState_Messaging)
                {
                    // this is the case we are now with a proper handshake!
                    // we now need to send the proper connection request!
                    // FIXME: If using DTLS we should just make that handshake accept the connection
                    SendConnectionRequest(
                        connection.ReceiveToken, secureClient, ref connection.Address, ref sendInterface, ref queueHandle);

                    return;
                }

                var handshakeResult = UpdateSecureHandshakeState(protocolData, ref connection.Address);
                currentState = Binding.unitytls_client_get_state(secureClient.ClientPtr);
                if (currentState == Binding.UnityTLSClientState_Fail)
                {
                    Debug.LogError($"Handshake failed with result {handshakeResult}");

                    // so we are in an error state which likely means the handshake failed in some
                    // way. We dispose of the connection state so when we attempt to connect again
                    // we can try again
                    DisposeSecureClient(ref secureClient);

                    protocolData->SecureClients.Remove(connection.Address);
                }
            }
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(NetworkProtocol.DisconnectDelegate))]
        public static void Disconnect(ref NetworkDriver.Connection connection,
            ref NetworkSendInterface sendInterface, ref NetworkSendQueueHandle queueHandle, IntPtr userData)
        {
            unsafe
            {
                var protocolData = (SecureNetworkProtocolData*)userData;
                var secureClient = protocolData->SecureClients[connection.Address];

                if (connection.State == NetworkConnection.State.Connected)
                {
                    var type = UdpCProtocol.Disconnect;
                    var token = connection.SendToken;
                    var res = SendHeaderOnlyMessage(type, token, secureClient, ref connection, ref sendInterface, ref queueHandle);
                    if (res != Binding.UNITYTLS_SUCCESS)
                    {
                        Debug.LogError($"Failed to send secure Disconnect message (result: {res})");
                    }
                }

                // we should cleanup the connection state ?
                DisposeSecureClient(ref secureClient);

                protocolData->SecureClients.Remove(connection.Address);
            }
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(NetworkProtocol.ProcessSendPingDelegate))]
        public static void ProcessSendPing(ref NetworkDriver.Connection connection,
            ref NetworkSendInterface sendInterface, ref NetworkSendQueueHandle queueHandle, IntPtr userData)
        {
            unsafe
            {
                var protocolData = (SecureNetworkProtocolData*)userData;
                var secureClient = protocolData->SecureClients[connection.Address];

                var type = UdpCProtocol.Ping;
                var token = connection.SendToken;
                var res = SendHeaderOnlyMessage(type, token, secureClient, ref connection, ref sendInterface, ref queueHandle);
                if (res != Binding.UNITYTLS_SUCCESS)
                {
                    Debug.LogError($"Failed to send secure Ping message (result: {res})");
                }
            }
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(NetworkProtocol.ProcessSendPongDelegate))]
        public static void ProcessSendPong(ref NetworkDriver.Connection connection,
            ref NetworkSendInterface sendInterface, ref NetworkSendQueueHandle queueHandle, IntPtr userData)
        {
            unsafe
            {
                var protocolData = (SecureNetworkProtocolData*)userData;
                var secureClient = protocolData->SecureClients[connection.Address];

                var type = UdpCProtocol.Pong;
                var token = connection.SendToken;
                var res = SendHeaderOnlyMessage(type, token, secureClient, ref connection, ref sendInterface, ref queueHandle);
                if (res != Binding.UNITYTLS_SUCCESS)
                {
                    Debug.LogError($"Failed to send secure Pong message (result: {res})");
                }
            }
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(NetworkProtocol.UpdateDelegate))]
        public static void Update(long updateTime, ref NetworkSendInterface sendInterface, ref NetworkSendQueueHandle queueHandle, IntPtr userData)
        {
            unsafe
            {
                var protocolData = (SecureNetworkProtocolData*)userData;
                protocolData->LastUpdate = updateTime;

                // No use pruning half-open connections more often than the handshake retry timeout.
                if (updateTime - protocolData->LastHalfOpenPrune > protocolData->SSLHandshakeTimeoutMin)
                {
                    PruneHalfOpenConnections(protocolData);
                    protocolData->LastHalfOpenPrune = updateTime;
                }
            }
        }
    }
}

#endif
