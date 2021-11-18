using System;
using AOT;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Networking.Transport.Protocols;
using UnityEngine.Assertions;

namespace Unity.Networking.Transport
{
    [BurstCompile]
    internal struct UnityTransportProtocol : INetworkProtocol
    {
        public void Initialize(NetworkSettings settings) {}
        public void Dispose() {}

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
                needsUpdate: false, // Update is no-op
                userData: IntPtr.Zero,
                maxHeaderSize: UdpCHeader.Length,
                maxFooterSize: SessionIdToken.k_Length
            );
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(NetworkProtocol.ComputePacketOverheadDelegate))]
        public static int ComputePacketOverhead(ref NetworkDriver.Connection connection, out int dataOffset)
        {
            dataOffset = UdpCHeader.Length;
            var footerSize = connection.DidReceiveData == 0 ? SessionIdToken.k_Length : 0;
            return dataOffset + footerSize;
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(NetworkProtocol.ProcessReceiveDelegate))]
        public static void ProcessReceive(IntPtr stream, ref NetworkInterfaceEndPoint endpoint, int size, ref NetworkSendInterface sendInterface, ref NetworkSendQueueHandle queueHandle, IntPtr userData, ref ProcessPacketCommand command)
        {
            unsafe
            {
                var data = (byte*)stream;
                var header = *(UdpCHeader*)data;

                if (size < UdpCHeader.Length)
                {
                    UnityEngine.Debug.LogError("Received an invalid message header");
                    command.Type = ProcessPacketCommandType.Drop;
                    return;
                }

                var type = (UdpCProtocol)header.Type;

                command.Address = endpoint;
                command.SessionId = header.SessionToken;

                if (type != UdpCProtocol.Data && (header.Flags & UdpCHeader.HeaderFlags.HasPipeline) != 0)
                {
                    UnityEngine.Debug.LogError("Received an invalid non-data message with a pipeline");
                    command.Type = ProcessPacketCommandType.Drop;
                    return;
                }

                switch (type)
                {
                    case UdpCProtocol.ConnectionAccept:
                        if ((header.Flags & UdpCHeader.HeaderFlags.HasConnectToken) == 0)
                        {
                            UnityEngine.Debug.LogError("Received an invalid ConnectionAccept without a token");
                            command.Type = ProcessPacketCommandType.Drop;
                            return;
                        }

                        if (size != UdpCHeader.Length + SessionIdToken.k_Length)
                        {
                            UnityEngine.Debug.LogError("Received an invalid ConnectionAccept with wrong length");
                            command.Type = ProcessPacketCommandType.Drop;
                            return;
                        }

                        command.Type = ProcessPacketCommandType.ConnectionAccept;
                        command.As.ConnectionAccept.ConnectionToken = *(SessionIdToken*)(stream + UdpCHeader.Length);
                        return;

                    case UdpCProtocol.ConnectionReject:
                        command.Type = ProcessPacketCommandType.ConnectionReject;
                        return;

                    case UdpCProtocol.ConnectionRequest:
                        command.Type = ProcessPacketCommandType.ConnectionRequest;
                        return;

                    case UdpCProtocol.Disconnect:
                        command.Type = ProcessPacketCommandType.Disconnect;
                        return;

                    case UdpCProtocol.Ping:
                        command.Type = ProcessPacketCommandType.Ping;
                        return;

                    case UdpCProtocol.Pong:
                        command.Type = ProcessPacketCommandType.Pong;
                        return;

                    case UdpCProtocol.Data:
                        var payloadLength = size - UdpCHeader.Length;
                        var hasPipeline = (header.Flags & UdpCHeader.HeaderFlags.HasPipeline) != 0 ? (byte)1 : (byte)0;
                        var hasConnectionToken = (header.Flags & UdpCHeader.HeaderFlags.HasConnectToken) != 0;

                        if (hasConnectionToken)
                        {
                            payloadLength -= SessionIdToken.k_Length;
                            command.Type = ProcessPacketCommandType.DataWithImplicitConnectionAccept;
                            command.As.DataWithImplicitConnectionAccept.Offset = UdpCHeader.Length;
                            command.As.DataWithImplicitConnectionAccept.Length = payloadLength;
                            command.As.DataWithImplicitConnectionAccept.HasPipelineByte = hasPipeline;
                            command.As.DataWithImplicitConnectionAccept.ConnectionToken = *(SessionIdToken*)(stream + UdpCHeader.Length + payloadLength);
                            return;
                        }
                        else
                        {
                            command.Type = ProcessPacketCommandType.Data;
                            command.As.Data.Offset = UdpCHeader.Length;
                            command.As.Data.Length = payloadLength;
                            command.As.Data.HasPipelineByte = hasPipeline;
                            return;
                        }
                }

                command.Type = ProcessPacketCommandType.Drop;
            }
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(NetworkProtocol.ProcessSendDelegate))]
        public static int ProcessSend(ref NetworkDriver.Connection connection, bool hasPipeline, ref NetworkSendInterface sendInterface, ref NetworkInterfaceSendHandle sendHandle, ref NetworkSendQueueHandle queueHandle, IntPtr userData)
        {
            WriteSendMessageHeader(ref connection, hasPipeline, ref sendHandle, 0);
            return sendInterface.EndSendMessage.Ptr.Invoke(ref sendHandle, ref connection.Address, sendInterface.UserData, ref queueHandle);
        }

        internal static unsafe int WriteSendMessageHeader(ref NetworkDriver.Connection connection, bool hasPipeline, ref NetworkInterfaceSendHandle sendHandle, int offset)
        {
            unsafe
            {
                var flags = default(UdpCHeader.HeaderFlags);
                var capacity = sendHandle.capacity - offset;
                var size = sendHandle.size - offset;

                if (connection.DidReceiveData == 0)
                {
                    flags |= UdpCHeader.HeaderFlags.HasConnectToken;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    if (size + SessionIdToken.k_Length > capacity)
                        throw new InvalidOperationException("SendHandle capacity overflow");
#endif
                    SessionIdToken* connectionToken = (SessionIdToken*)((byte*)sendHandle.data + sendHandle.size);
                    *connectionToken = connection.ReceiveToken;
                    sendHandle.size += SessionIdToken.k_Length;
                }

                if (hasPipeline)
                {
                    flags |= UdpCHeader.HeaderFlags.HasPipeline;
                }

                UdpCHeader* header = (UdpCHeader*)(sendHandle.data + offset);
                *header = new UdpCHeader
                {
                    Type = (byte)UdpCProtocol.Data,
                    SessionToken = connection.SendToken,
                    Flags = flags
                };

                return sendHandle.size - offset;
            }
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(NetworkProtocol.ProcessSendConnectionAcceptDelegate))]
        public static void ProcessSendConnectionAccept(ref NetworkDriver.Connection connection, ref NetworkSendInterface sendInterface, ref NetworkSendQueueHandle queueHandle, IntPtr userData)
        {
            unsafe
            {
                NetworkInterfaceSendHandle sendHandle;
                if (sendInterface.BeginSendMessage.Ptr.Invoke(out sendHandle, sendInterface.UserData, UdpCHeader.Length + SessionIdToken.k_Length) != 0)
                {
                    UnityEngine.Debug.LogError("Failed to send a ConnectionAccept packet");
                    return;
                }

                byte* packet = (byte*)sendHandle.data;
                var size = WriteConnectionAcceptMessage(ref connection, packet, sendHandle.capacity);

                if (size < 0)
                {
                    sendInterface.AbortSendMessage.Ptr.Invoke(ref sendHandle, sendInterface.UserData);
                    UnityEngine.Debug.LogError("Failed to send a ConnectionAccept packet");
                    return;
                }

                sendHandle.size = size;

                if (sendInterface.EndSendMessage.Ptr.Invoke(ref sendHandle, ref connection.Address, sendInterface.UserData, ref queueHandle) < 0)
                {
                    UnityEngine.Debug.LogError("Failed to send a ConnectionAccept packet");
                    return;
                }
            }
        }

        internal static int GetConnectionAcceptMessageMaxLength() => UdpCHeader.Length + SessionIdToken.k_Length;

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
            *header = new UdpCHeader
            {
                Type = (byte)UdpCProtocol.ConnectionAccept,
                SessionToken = connection.SendToken,
                Flags = 0
            };

            if (connection.DidReceiveData == 0)
            {
                header->Flags |= UdpCHeader.HeaderFlags.HasConnectToken;
                *(SessionIdToken*)(packet + UdpCHeader.Length) = connection.ReceiveToken;
            }

            Assert.IsTrue(size <= GetConnectionAcceptMessageMaxLength());

            return size;
        }

        private static unsafe int SendHeaderOnlyMessage(UdpCProtocol type, SessionIdToken token, ref NetworkDriver.Connection connection, ref NetworkSendInterface sendInterface, ref NetworkSendQueueHandle queueHandle)
        {
            NetworkInterfaceSendHandle sendHandle;
            if (sendInterface.BeginSendMessage.Ptr.Invoke(out sendHandle, sendInterface.UserData, UdpCHeader.Length) != 0)
            {
                return -1;
            }

            byte* packet = (byte*)sendHandle.data;
            sendHandle.size = UdpCHeader.Length;
            if (sendHandle.size > sendHandle.capacity)
            {
                sendInterface.AbortSendMessage.Ptr.Invoke(ref sendHandle, sendInterface.UserData);
                return -1;
            }

            var header = (UdpCHeader*)packet;
            *header = new UdpCHeader
            {
                Type = (byte)type,
                SessionToken = token,
                Flags = 0
            };


            if (sendInterface.EndSendMessage.Ptr.Invoke(ref sendHandle, ref connection.Address, sendInterface.UserData, ref queueHandle) < 0)
            {
                return -1;
            }

            return UdpCHeader.Length;
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(NetworkProtocol.ConnectDelegate))]
        public static void Connect(ref NetworkDriver.Connection connection, ref NetworkSendInterface sendInterface, ref NetworkSendQueueHandle queueHandle, IntPtr userData)
        {
            unsafe
            {
                var type = UdpCProtocol.ConnectionRequest;
                var token = connection.ReceiveToken;
                var res = SendHeaderOnlyMessage(type, token, ref connection, ref sendInterface, ref queueHandle);
                if (res == -1)
                {
                    UnityEngine.Debug.LogError("Failed to send ConnectionRequest message");
                }
            }
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(NetworkProtocol.DisconnectDelegate))]
        public static void Disconnect(ref NetworkDriver.Connection connection, ref NetworkSendInterface sendInterface, ref NetworkSendQueueHandle queueHandle, IntPtr userData)
        {
            unsafe
            {
                var type = UdpCProtocol.Disconnect;
                var token = connection.SendToken;
                var res = SendHeaderOnlyMessage(type, token, ref connection, ref sendInterface, ref queueHandle);
                if (res == -1)
                {
                    UnityEngine.Debug.LogError("Failed to send Disconnect message");
                }
            }
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(NetworkProtocol.ProcessSendPingDelegate))]
        public static void ProcessSendPing(ref NetworkDriver.Connection connection, ref NetworkSendInterface sendInterface, ref NetworkSendQueueHandle queueHandle, IntPtr userData)
        {
            unsafe
            {
                var type = UdpCProtocol.Ping;
                var token = connection.SendToken;
                var res = SendHeaderOnlyMessage(type, token, ref connection, ref sendInterface, ref queueHandle);
                if (res == -1)
                {
                    UnityEngine.Debug.LogError("Failed to send Ping message");
                }
            }
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(NetworkProtocol.ProcessSendPongDelegate))]
        public static void ProcessSendPong(ref NetworkDriver.Connection connection, ref NetworkSendInterface sendInterface, ref NetworkSendQueueHandle queueHandle, IntPtr userData)
        {
            unsafe
            {
                var type = UdpCProtocol.Pong;
                var token = connection.SendToken;
                var res = SendHeaderOnlyMessage(type, token, ref connection, ref sendInterface, ref queueHandle);
                if (res == -1)
                {
                    UnityEngine.Debug.LogError("Failed to send Pong message");
                }
            }
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(NetworkProtocol.UpdateDelegate))]
        public static void Update(long updateTime, ref NetworkSendInterface sendInterface, ref NetworkSendQueueHandle queueHandle, IntPtr userData)
        {
            // No-op
        }
    }
}
