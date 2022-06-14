using System;
using System.Runtime.InteropServices;
using Unity.Collections.LowLevel.Unsafe;

namespace Unity.Networking.Transport
{
    internal interface INetworkProtocol : IDisposable
    {
        /// <summary>
        /// This is call when initializing the NetworkDriver. If the protocol requires custom paramters, they can be passed
        /// to the NetworkDriver initialization.
        /// </summary>
        void Initialize(NetworkSettings settings);

        /// <summary>
        /// Returns a burst compatible NetworkProtocol struct containing the function pointers and custom UserData for the protocol.
        /// </summary>
        NetworkProtocol CreateProtocolInterface();

        /// <summary>
        /// This method should bind the NetworkInterface to the local endpoint and perform any
        /// custom binding behaviour for the protocol.
        /// </summary>
        int Bind(INetworkInterface networkInterface, ref NetworkInterfaceEndPoint localEndPoint);

        /// <summary>
        /// Create a new connection address for the endPoint using the passed NetworkInterface.
        /// Some protocols - as relay - could decide to use virtual addressed that not necessarily
        /// maps 1 - 1 to a endpoint.
        /// </summary>
        int CreateConnectionAddress(INetworkInterface networkInterface, NetworkEndPoint endPoint, out NetworkInterfaceEndPoint address);

        NetworkEndPoint GetRemoteEndPoint(INetworkInterface networkInterface, NetworkInterfaceEndPoint address);
    }

    /// <summary>
    /// This is a Burst compatible struct that contains all the function pointers that the NetworkDriver
    /// uses for processing messages with a particular protocol.
    /// </summary>
    internal struct NetworkProtocol
    {
        /// <summary>
        /// Computes the size required for allocating a packet for the connection with this protocol. The dataCapacity received
        /// can be modified to reflect the resulting payload capacity of the packet, if it gets reduced the NetworkDriver will
        /// return a NetworkPacketOverflow error. The payloadOffset return value is the possition where the payload data needs
        /// to be stored in the packet.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int ComputePacketOverheadDelegate(ref NetworkDriver.Connection connection, out int payloadOffset);

        /// <summary>
        /// Process a receiving packet and returns a ProcessPacketCommand that will indicate to the NetworkDriver what actions
        /// need to be performed and what to do with the message.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void ProcessReceiveDelegate(IntPtr stream, ref NetworkInterfaceEndPoint address, int size, ref NetworkSendInterface sendInterface, ref NetworkSendQueueHandle queueHandle, IntPtr userData, ref ProcessPacketCommand command);

        /// <summary>
        /// Process a sending packet. When this method is called, the packet is ready to be sent through the sendInterface.
        /// Here the protocol could perform some final steps as, for instance, filling some header fields.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int ProcessSendDelegate(ref NetworkDriver.Connection connection, bool hasPipeline, ref NetworkSendInterface sendInterface, ref NetworkInterfaceSendHandle sendHandle, ref NetworkSendQueueHandle queueHandle, IntPtr userData);

        /// <summary>
        /// This method should send a protocol specific connect confirmation message from a server to a client using the connection.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void ProcessSendConnectionAcceptDelegate(ref NetworkDriver.Connection connection, ref NetworkSendInterface sendInterface, ref NetworkSendQueueHandle queueHandle, IntPtr userData);

        /// <summary>
        /// Try to establish a connection (from a client to a server). May only be called by a
        /// client, and may be called multiple times for a connection (when retrying an attempt).
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void ConnectDelegate(ref NetworkDriver.Connection connection, ref NetworkSendInterface sendInterface, ref NetworkSendQueueHandle queueHandle, IntPtr userData);

        /// <summary>
        /// Close a connection. This method should notify the remote peer of the disconnection.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void DisconnectDelegate(ref NetworkDriver.Connection connection, ref NetworkSendInterface sendInterface, ref NetworkSendQueueHandle queueHandle, IntPtr userData);

        /// <summary>
        /// This method should send a protocol specific heartbeat request (ping) message on the connection.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void ProcessSendPingDelegate(ref NetworkDriver.Connection connection, ref NetworkSendInterface sendInterface, ref NetworkSendQueueHandle queueHandle, IntPtr userData);

        /// <summary>
        /// This method should send a protocol specific heartbeat response (pong) message on the connection.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void ProcessSendPongDelegate(ref NetworkDriver.Connection connection, ref NetworkSendInterface sendInterface, ref NetworkSendQueueHandle queueHandle, IntPtr userData);

        /// <summary>
        /// This method is called every NetworkDriver tick and can be used for performing protocol update operations.
        /// One common case is sending protocol specific packets for keeping the connections alive or retrying failed ones.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void UpdateDelegate(long updateTime, ref NetworkSendInterface sendInterface, ref NetworkSendQueueHandle queueHandle, IntPtr userData);

        public TransportFunctionPointer<ComputePacketOverheadDelegate> ComputePacketOverhead;
        public TransportFunctionPointer<ProcessReceiveDelegate> ProcessReceive;
        public TransportFunctionPointer<ProcessSendDelegate> ProcessSend;
        public TransportFunctionPointer<ProcessSendConnectionAcceptDelegate> ProcessSendConnectionAccept;
        public TransportFunctionPointer<ConnectDelegate> Connect;
        public TransportFunctionPointer<DisconnectDelegate> Disconnect;
        public TransportFunctionPointer<ProcessSendPingDelegate> ProcessSendPing;
        public TransportFunctionPointer<ProcessSendPongDelegate> ProcessSendPong;
        public TransportFunctionPointer<UpdateDelegate> Update;

        /// <summary>
        /// Raw pointer that is going to be passed to the function pointer and that can contain protocol specific data.
        /// </summary>
        [NativeDisableUnsafePtrRestriction] public IntPtr UserData;

        /// <summary>
        /// The maximun size of the header of a data packet for this protocol.
        /// </summary>
        public int MaxHeaderSize;

        /// <summary>
        /// The maximun size of the footer of a data packet for this protocol.
        /// </summary>
        public int MaxFooterSize;

        /// <summary>
        /// The maximun amount of bytes that are not payload data for a packet for this protocol.
        /// </summary>
        public int PaddingSize => MaxHeaderSize + MaxFooterSize;

        /// <summary>
        /// If true - UpdateDelegate will be called
        /// </summary>
        public bool NeedsUpdate;

        public NetworkProtocol(
            TransportFunctionPointer<ComputePacketOverheadDelegate> computePacketOverhead,
            TransportFunctionPointer<ProcessReceiveDelegate> processReceive,
            TransportFunctionPointer<ProcessSendDelegate> processSend,
            TransportFunctionPointer<ProcessSendConnectionAcceptDelegate> processSendConnectionAccept,
            TransportFunctionPointer<ConnectDelegate> connect,
            TransportFunctionPointer<DisconnectDelegate> disconnect,
            TransportFunctionPointer<ProcessSendPingDelegate> processSendPing,
            TransportFunctionPointer<ProcessSendPongDelegate> processSendPong,
            TransportFunctionPointer<UpdateDelegate> update,
            bool needsUpdate,
            IntPtr userData,
            int maxHeaderSize,
            int maxFooterSize
        )
        {
            ComputePacketOverhead = computePacketOverhead;
            ProcessReceive = processReceive;
            ProcessSend = processSend;
            ProcessSendConnectionAccept = processSendConnectionAccept;
            Connect = connect;
            Disconnect = disconnect;
            ProcessSendPing = processSendPing;
            ProcessSendPong = processSendPong;
            Update = update;
            NeedsUpdate = needsUpdate;
            UserData = userData;
            MaxHeaderSize = maxHeaderSize;
            MaxFooterSize = maxFooterSize;
        }
    }

    /// <summary>
    /// The type of commands that the NetworkDriver can process from a received packet.
    /// </summary>
    internal enum ProcessPacketCommandType : byte
    {
        /// <summary>Do not perform any extra action.</summary>
        Drop = 0, // Keepit 0 to make it the default.

        /// <summary>Find and update the address for a connection.</summary>
        AddressUpdate,

        /// <summary>
        /// The connection has been accepted by the server and can be completed.
        /// </summary>
        ConnectionAccept,

        /// <summary>The connection has been rejected by the server.</summary>
        ConnectionReject,

        /// <summary>A connection request was received from a client.</summary>
        ConnectionRequest,

        /// <summary>A data message has been received for an stablished connection.</summary>
        Data,

        /// <summary>The connection is requesting to disconnect.</summary>
        Disconnect,

        /// <summary>Combination of ConnectionAccept and Data.</summary>
        DataWithImplicitConnectionAccept,

        /// <summary>
        /// A hearbeat request (ping) was received.
        /// </summary>
        Ping,

        /// <summary>
        /// A heartbeat response (pong) was received.
        /// </summary>
        Pong,

        /// <summary>
        /// A change in the protocol status occurred.
        /// </summary>
        ProtocolStatusUpdate,
    }

    /// <summary>
    /// Contains the command type and command data required by the NetworkDriver to process a packet.
    /// </summary>
    internal struct ProcessPacketCommand
    {
        /// <summary>
        /// The type of the command to process
        /// </summary>
        public ProcessPacketCommandType Type;

        /// <summary>
        /// The endpoint from which the packet was received
        /// </summary>
        public NetworkInterfaceEndPoint Address;

        /// <summary>
        /// The session token of the packet
        /// </summary>
        public SessionIdToken SessionId;

        /// <summary>
        /// Details specific to the packet type
        /// </summary>
        public ProcessPacketCommandAs As;

        // Acts like a C/C++ union type.
        [StructLayout(LayoutKind.Explicit)]
        public struct ProcessPacketCommandAs
        {
            [FieldOffset(0)] public AsAddressUpdate AddressUpdate;
            [FieldOffset(0)] public AsConnectionAccept ConnectionAccept;
            [FieldOffset(0)] public AsData Data;
            [FieldOffset(0)] public AsDataWithImplicitConnectionAccept DataWithImplicitConnectionAccept;
            [FieldOffset(0)] public AsProtocolStatusUpdate ProtocolStatusUpdate;

            public struct AsAddressUpdate
            {
                public NetworkInterfaceEndPoint NewAddress;
            }

            public struct AsConnectionAccept
            {
                public SessionIdToken ConnectionToken;
            }

            public struct AsData
            {
                public int Offset;
                public int Length;
                public byte HasPipelineByte;

                public bool HasPipeline => HasPipelineByte != 0;
            }

            public struct AsDataWithImplicitConnectionAccept
            {
                public int Offset;
                public int Length;
                public byte HasPipelineByte;
                public SessionIdToken ConnectionToken;

                public bool HasPipeline => HasPipelineByte != 0;
            }

            public struct AsProtocolStatusUpdate
            {
                public int Status;
            }
        }
    }
}
