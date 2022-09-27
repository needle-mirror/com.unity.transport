using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Networking.Transport.Protocols;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Networking.Transport.Error;
using Unity.Networking.Transport.Utilities;

namespace Unity.Networking.Transport
{
    public unsafe struct QueuedSendMessage
    {
        public fixed byte Data[NetworkParameterConstants.MTU];
        public NetworkInterfaceEndPoint Dest;
        public int DataLength;
    }

    /// <summary>
    /// The NetworkDriver is an implementation of Virtual Connections over any transport.
    ///
    /// Basic usage:
    /// <code>
    /// var driver = NetworkDriver.Create();
    /// </code>
    /// </summary>
    public struct NetworkDriver : IDisposable
    {
        /// <summary>
        /// Create a Concurrent Copy of the NetworkDriver.
        /// </summary>
        public Concurrent ToConcurrent()
        {
            return new Concurrent
            {
                m_NetworkSendInterface = m_NetworkSendInterface,
                m_NetworkProtocolInterface = m_NetworkProtocolInterface,
                m_EventQueue = m_EventQueue.ToConcurrent(),
                m_ConnectionList = m_ConnectionList,
                m_DataStream = m_DataStream,
                m_DisconnectReasons = m_DisconnectReasons,
                m_PipelineProcessor = m_PipelineProcessor.ToConcurrent(),
                m_DefaultHeaderFlags = m_DefaultHeaderFlags,
                m_ConcurrentParallelSendQueue = m_ParallelSendQueue.AsParallelWriter(),
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                m_ThreadIndex = 0,
                m_PendingBeginSend = m_PendingBeginSend
#endif
            };
        }

        private Concurrent ToConcurrentSendOnly()
        {
            return new Concurrent
            {
                m_NetworkSendInterface = m_NetworkSendInterface,
                m_NetworkProtocolInterface = m_NetworkProtocolInterface,
                m_EventQueue = default,
                m_ConnectionList = m_ConnectionList,
                m_DataStream = m_DataStream,
                m_DisconnectReasons = m_DisconnectReasons,
                m_PipelineProcessor = m_PipelineProcessor.ToConcurrent(),
                m_DefaultHeaderFlags = m_DefaultHeaderFlags,
                m_ConcurrentParallelSendQueue = m_ParallelSendQueue.AsParallelWriter(),
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                m_ThreadIndex = 0,
                m_PendingBeginSend = m_PendingBeginSend
#endif
            };
        }

        /// <summary>
        /// The Concurrent struct is used to create an Concurrent instance of the NetworkDriver.
        /// </summary>
        public struct Concurrent
        {
            /// <summary>
            /// Pops events for a connection using the specified connection id
            /// </summary>
            /// <param name="connectionId">The connection id</param>
            /// <param name="reader">Stream reader for the event's data.</param>
            /// <returns>The network event type</returns>
            public NetworkEvent.Type PopEventForConnection(NetworkConnection connectionId, out DataStreamReader reader)
            {
                return PopEventForConnection(connectionId, out reader, out var _);
            }

            /// <summary>
            /// Pops events for a connection using the specified connection id
            /// </summary>
            /// <param name="connectionId">The connection id</param>
            /// <param name="reader">Stream reader for the event's data.</param>
            /// <param name="pipeline">Pipeline on which the data event was received.</param>
            /// <returns>The type</returns>
            public NetworkEvent.Type PopEventForConnection(NetworkConnection connectionId, out DataStreamReader reader, out NetworkPipeline pipeline)
            {
                pipeline = default;

                reader = default;
                if (connectionId.m_NetworkId < 0 || connectionId.m_NetworkId >= m_ConnectionList.Length ||
                    m_ConnectionList[connectionId.m_NetworkId].Version != connectionId.m_NetworkVersion)
                    return (int)NetworkEvent.Type.Empty;

                var type = m_EventQueue.PopEventForConnection(connectionId.m_NetworkId, out var offset, out var size, out var pipelineId);
                pipeline = new NetworkPipeline { Id = pipelineId };

                if (type == NetworkEvent.Type.Disconnect && offset < 0)
                    reader = new DataStreamReader(m_DisconnectReasons.GetSubArray(math.abs(offset), 1));
                else if (size > 0)
                    reader = new DataStreamReader(((NativeArray<byte>)m_DataStream).GetSubArray(offset, size));

                return type;
            }

            /// <summary>
            /// Max headersize including a <see cref="NetworkPipeline"/>
            /// </summary>
            /// <param name="pipe">The pipeline with which to get the maximum header size.</param>
            /// <returns>The header size</returns>
            public int MaxHeaderSize(NetworkPipeline pipe)
            {
                var headerSize = m_NetworkProtocolInterface.PaddingSize;
                if (pipe.Id > 0)
                {
                    // All headers plus one byte for pipeline id
                    headerSize += m_PipelineProcessor.SendHeaderCapacity(pipe) + 1;
                }

                return headerSize;
            }

            internal int MaxProtocolHeaderSize()
            {
                return m_NetworkProtocolInterface.PaddingSize;
            }

            struct PendingSend
            {
                public NetworkPipeline Pipeline;
                public NetworkConnection Connection;
                public NetworkInterfaceSendHandle SendHandle;
                public int headerSize;
            }

            /// <summary>
            /// Acquires a DataStreamWriter for starting a asynchronous send.
            /// </summary>
            /// <param name="id">The NetworkConnection id to write through</param>
            /// <param name="writer">A DataStreamWriter to write to</param>
            /// <param name="requiredPayloadSize">If you require the payload to be of certain size</param>
            /// <value>Returns <see cref="StatusCode.Success"/> on a successful acquire. Otherwise returns an <see cref="StatusCode"/> indicating the error.</value>
            /// <remarks> Will throw a <exception cref="InvalidOperationException"></exception> if the connection is in a Connecting state.</remarks>
            public unsafe int BeginSend(NetworkConnection id, out DataStreamWriter writer, int requiredPayloadSize = 0)
            {
                return BeginSend(NetworkPipeline.Null, id, out writer, requiredPayloadSize);
            }

            /// <summary>
            /// Acquires a DataStreamWriter for starting a asynchronous send.
            /// </summary>
            /// <param name="pipe">The NetworkPipeline to write through</param>
            /// <param name="id">The NetworkConnection id to write through</param>
            /// <param name="writer">A DataStreamWriter to write to</param>
            /// <param name="requiredPayloadSize">If you require the payload to be of certain size</param>
            /// <value>Returns <see cref="StatusCode.Success"/> on a successful acquire. Otherwise returns an <see cref="StatusCode"/> indicating the error.</value>
            /// <remarks> Will throw a <exception cref="InvalidOperationException"></exception> if the connection is in a Connecting state.</remarks>
            public unsafe int BeginSend(NetworkPipeline pipe, NetworkConnection id,
                out DataStreamWriter writer, int requiredPayloadSize = 0)
            {
                writer = default;

                if (id.m_NetworkId < 0 || id.m_NetworkId >= m_ConnectionList.Length)
                    return (int)Error.StatusCode.NetworkIdMismatch;

                var connection = m_ConnectionList[id.m_NetworkId];
                if (connection.Version != id.m_NetworkVersion)
                    return (int)Error.StatusCode.NetworkVersionMismatch;

                if (connection.State != NetworkConnection.State.Connected)
                    return (int)Error.StatusCode.NetworkStateMismatch;

                var pipelineHeader = (pipe.Id > 0) ? m_PipelineProcessor.SendHeaderCapacity(pipe) + 1 : 0;
                var pipelinePayloadCapacity = m_PipelineProcessor.PayloadCapacity(pipe);

                var protocolOverhead = m_NetworkProtocolInterface.ComputePacketOverhead.Ptr.Invoke(ref connection, out var payloadOffset);

                // If the pipeline doesn't have an explicity payload capacity, then use whatever
                // will fit inside the MTU (considering protocol and pipeline overhead). If there is
                // an explicity pipeline payload capacity we use that directly. Right now only
                // fragmented pipelines have an explicity capacity, and we want users to be able to
                // rely on this configured value.
                var payloadCapacity = pipelinePayloadCapacity == 0
                    ? NetworkParameterConstants.MTU - protocolOverhead - pipelineHeader
                    : pipelinePayloadCapacity;

                // Total capacity is the full size of the buffer we'll allocate. Without an explicit
                // pipeline payload capacity, this is the MTU. Otherwise it's the pipeline payload
                // capacity plus whatever overhead we need to transmit the packet.
                var totalCapacity = pipelinePayloadCapacity == 0
                    ? NetworkParameterConstants.MTU
                    : pipelinePayloadCapacity + protocolOverhead + pipelineHeader;

                // Check if we can accomodate the user's required payload size.
                if (payloadCapacity < requiredPayloadSize)
                {
                    return (int)Error.StatusCode.NetworkPacketOverflow;
                }

                // Allocate less memory if user doesn't require our full capacity.
                if (requiredPayloadSize > 0 && payloadCapacity > requiredPayloadSize)
                {
                    var extraCapacity = payloadCapacity - requiredPayloadSize;
                    payloadCapacity -= extraCapacity;
                    totalCapacity -= extraCapacity;
                }

                var result = 0;
                if ((result = m_NetworkSendInterface.BeginSendMessage.Ptr.Invoke(out var sendHandle, m_NetworkSendInterface.UserData, totalCapacity)) != 0)
                {
                    sendHandle.data = (IntPtr)UnsafeUtility.Malloc(totalCapacity, 8, Allocator.Temp);
                    sendHandle.capacity = totalCapacity;
                    sendHandle.id = 0;
                    sendHandle.size = 0;
                    sendHandle.flags = SendHandleFlags.AllocatedByDriver;
                }

                if (sendHandle.capacity < totalCapacity)
                    return (int)Error.StatusCode.NetworkPacketOverflow;

                var slice = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<byte>((byte*)sendHandle.data + payloadOffset + pipelineHeader, payloadCapacity, Allocator.Invalid);

#if ENABLE_UNITY_COLLECTIONS_CHECKS
                var safety = AtomicSafetyHandle.GetTempMemoryHandle();
                NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref slice, safety);
#endif
                writer = new DataStreamWriter(slice);
                writer.m_SendHandleData = (IntPtr)UnsafeUtility.Malloc(UnsafeUtility.SizeOf<PendingSend>(), UnsafeUtility.AlignOf<PendingSend>(), Allocator.Temp);
                *(PendingSend*)writer.m_SendHandleData = new PendingSend
                {
                    Pipeline = pipe,
                    Connection = id,
                    SendHandle = sendHandle,
                    headerSize = payloadOffset,
                };
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                m_PendingBeginSend[m_ThreadIndex * JobsUtility.CacheLineSize / 4] = m_PendingBeginSend[m_ThreadIndex * JobsUtility.CacheLineSize / 4] + 1;
#endif
                return (int)Error.StatusCode.Success;
            }

            /// <summary>
            /// Ends a asynchronous send.
            /// </summary>
            /// <param name="writer">If you require the payload to be of certain size.</param>
            /// <value>The length of the buffer sent if nothing went wrong.</value>
            /// <exception cref="InvalidOperationException">If endsend is called with a matching BeginSend call.</exception>
            /// <exception cref="InvalidOperationException">If the connection got closed between the call of being and end send.</exception>
            public unsafe int EndSend(DataStreamWriter writer)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                // Just here to trigger a safety check on the writer
                if (writer.Capacity == 0)
                    throw new InvalidOperationException("EndSend without matching BeginSend");
#endif
                PendingSend* pendingSendPtr = (PendingSend*)writer.m_SendHandleData;
                if (pendingSendPtr == null || pendingSendPtr->Connection == default)
                {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    throw new InvalidOperationException("EndSend without matching BeginSend");
#else
                    return (int)Error.StatusCode.NetworkSendHandleInvalid;
#endif
                }

                if (m_ConnectionList[pendingSendPtr->Connection.m_NetworkId].Version != pendingSendPtr->Connection.m_NetworkVersion)
                {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    throw new InvalidOperationException("Connection closed between begin and end send");
#else
                    return (int)Error.StatusCode.NetworkVersionMismatch;
#endif
                }

                if (writer.HasFailedWrites)
                {
                    AbortSend(writer);
                    // DataStreamWriter can only have failed writes if we overflow its capacity.
                    return (int)Error.StatusCode.NetworkPacketOverflow;
                }

                PendingSend pendingSend = *(PendingSend*)writer.m_SendHandleData;
                pendingSendPtr->Connection = default;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                m_PendingBeginSend[m_ThreadIndex * JobsUtility.CacheLineSize / 4] = m_PendingBeginSend[m_ThreadIndex * JobsUtility.CacheLineSize / 4] - 1;
#endif

                pendingSend.SendHandle.size = pendingSend.headerSize + writer.Length;
                int retval = 0;
                if (pendingSend.Pipeline.Id > 0)
                {
                    pendingSend.SendHandle.size += m_PipelineProcessor.SendHeaderCapacity(pendingSend.Pipeline) + 1;
                    var oldHeaderFlags = m_DefaultHeaderFlags;
                    m_DefaultHeaderFlags = UdpCHeader.HeaderFlags.HasPipeline;
                    retval = m_PipelineProcessor.Send(this, pendingSend.Pipeline, pendingSend.Connection, pendingSend.SendHandle, pendingSend.headerSize);
                    m_DefaultHeaderFlags = oldHeaderFlags;
                }
                else
                    // TODO: Is there a better way we could set the hasPipeline value correctly?
                    // this case is when the message is sent from the pipeline directly, "without a pipeline" so the hasPipeline flag is set in m_DefaultHeaderFlags
                    // allowing us to capture it here
                    retval = CompleteSend(pendingSend.Connection, pendingSend.SendHandle, (m_DefaultHeaderFlags & UdpCHeader.HeaderFlags.HasPipeline) != 0);
                if (retval <= 0)
                    return retval;
                return writer.Length;
            }

            /// <summary>
            /// Aborts a asynchronous send.
            /// </summary>
            /// <param name="writer">If you require the payload to be of certain size.</param>
            /// <value>The length of the buffer sent if nothing went wrong.</value>
            /// <exception cref="InvalidOperationException">If endsend is called with a matching BeginSend call.</exception>
            /// <exception cref="InvalidOperationException">If the connection got closed between the call of being and end send.</exception>
            public unsafe void AbortSend(DataStreamWriter writer)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                // Just here to trigger a safety check on the writer
                if (writer.Capacity == 0)
                    throw new InvalidOperationException("EndSend without matching BeginSend");
#endif
                PendingSend* pendingSendPtr = (PendingSend*)writer.m_SendHandleData;
                if (pendingSendPtr == null || pendingSendPtr->Connection == default)
                {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    throw new InvalidOperationException("AbortSend without matching BeginSend");
#else
                    UnityEngine.Debug.LogError("AbortSend without matching BeginSend");
                    return;
#endif
                }
                PendingSend pendingSend = *(PendingSend*)writer.m_SendHandleData;
                pendingSendPtr->Connection = default;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                m_PendingBeginSend[m_ThreadIndex * JobsUtility.CacheLineSize / 4] = m_PendingBeginSend[m_ThreadIndex * JobsUtility.CacheLineSize / 4] - 1;
#endif
                AbortSend(pendingSend.SendHandle);
            }

            internal unsafe int CompleteSend(NetworkConnection sendConnection, NetworkInterfaceSendHandle sendHandle, bool hasPipeline)
            {
                if (0 != (sendHandle.flags & SendHandleFlags.AllocatedByDriver))
                {
                    var ret = 0;
                    NetworkInterfaceSendHandle originalHandle = sendHandle;
                    if ((ret = m_NetworkSendInterface.BeginSendMessage.Ptr.Invoke(out sendHandle, m_NetworkSendInterface.UserData, originalHandle.size)) != 0)
                    {
                        return ret;
                    }
                    UnsafeUtility.MemCpy((void*)sendHandle.data, (void*)originalHandle.data, originalHandle.size);
                    sendHandle.size = originalHandle.size;
                }

                var connection = m_ConnectionList[sendConnection.m_NetworkId];
                var queueHandle = NetworkSendQueueHandle.ToTempHandle(m_ConcurrentParallelSendQueue);
                return m_NetworkProtocolInterface.ProcessSend.Ptr.Invoke(ref connection, hasPipeline, ref m_NetworkSendInterface, ref sendHandle, ref queueHandle, m_NetworkProtocolInterface.UserData);
            }

            internal void AbortSend(NetworkInterfaceSendHandle sendHandle)
            {
                if (0 == (sendHandle.flags & SendHandleFlags.AllocatedByDriver))
                {
                    m_NetworkSendInterface.AbortSendMessage.Ptr.Invoke(ref sendHandle, m_NetworkSendInterface.UserData);
                }
            }

            /// <summary>
            /// Gets the connection state using the specified id
            /// </summary>
            /// <param name="id">The connection id</param>
            /// <returns>The network connection state</returns>
            public NetworkConnection.State GetConnectionState(NetworkConnection id)
            {
                if (id.m_NetworkId < 0 || id.m_NetworkId >= m_ConnectionList.Length)
                    return NetworkConnection.State.Disconnected;
                var connection = m_ConnectionList[id.m_NetworkId];
                if (connection.Version != id.m_NetworkVersion)
                    return NetworkConnection.State.Disconnected;
                return connection.State;
            }

            internal NetworkSendInterface m_NetworkSendInterface;
            internal NetworkProtocol m_NetworkProtocolInterface;
            internal NetworkEventQueue.Concurrent m_EventQueue;
            internal NativeArray<byte> m_DisconnectReasons;

            [ReadOnly] internal NativeList<Connection> m_ConnectionList;
            [ReadOnly] internal NativeList<byte> m_DataStream;
            internal NetworkPipelineProcessor.Concurrent m_PipelineProcessor;
            internal UdpCHeader.HeaderFlags m_DefaultHeaderFlags;
            internal NativeQueue<QueuedSendMessage>.ParallelWriter m_ConcurrentParallelSendQueue;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            [NativeSetThreadIndex] internal int m_ThreadIndex;
            [NativeDisableParallelForRestriction] internal NativeArray<int> m_PendingBeginSend;
#endif
        }

        internal struct Connection
        {
            public NetworkInterfaceEndPoint Address;
            public long LastNonDataSend;
            public long LastReceive;
            public int Id;
            public int Version;
            public int ConnectAttempts;
            public NetworkConnection.State State;
            public SessionIdToken ReceiveToken;
            public SessionIdToken SendToken;
            public byte DidReceiveData;
            public byte IsAccepted;

            public static bool operator==(Connection lhs, Connection rhs)
            {
                return lhs.Id == rhs.Id && lhs.Version == rhs.Version && lhs.Address == rhs.Address;
            }

            public static bool operator!=(Connection lhs, Connection rhs)
            {
                return lhs.Id != rhs.Id || lhs.Version != rhs.Version || lhs.Address != rhs.Address;
            }

            public override bool Equals(object compare)
            {
                return this == (Connection)compare;
            }

            /// <summary>
            /// Null Connection
            /// </summary>
            public static Connection Null => new Connection() {Id = 0, Version = 0};

            public override int GetHashCode()
            {
                return Id;
            }

            public bool Equals(Connection connection)
            {
                return connection.Id == Id && connection.Version == Version && connection.Address == Address;
            }
        }

        // internal variables :::::::::::::::::::::::::::::::::::::::::::::::::
        static List<INetworkInterface> s_NetworkInterfaces = new List<INetworkInterface>();
        static List<INetworkProtocol> s_NetworkProtocols = new List<INetworkProtocol>();

        int m_NetworkInterfaceIndex;
        NetworkSendInterface m_NetworkSendInterface;

        internal INetworkInterface NetworkInterface => s_NetworkInterfaces[m_NetworkInterfaceIndex];

        int m_NetworkProtocolIndex;
        NetworkProtocol m_NetworkProtocolInterface;

        internal INetworkProtocol NetworkProtocol => s_NetworkProtocols[m_NetworkProtocolIndex];

        NativeQueue<QueuedSendMessage> m_ParallelSendQueue;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
        NativeArray<int> m_PendingBeginSend;
#endif

        NetworkEventQueue m_EventQueue;
        private NativeArray<byte> m_DisconnectReasons;

        NativeQueue<int> m_FreeList;
        NativeQueue<int> m_NetworkAcceptQueue;
        NativeList<Connection> m_ConnectionList;

        [NativeDisableContainerSafetyRestriction]
        NativeArray<int> m_InternalState;

        private NativeReference<int> m_ProtocolStatus;
        internal int ProtocolStatus => m_ProtocolStatus.Value;

        NativeQueue<int> m_PendingFree;
        NativeArray<int> m_ErrorCodes;

        enum ErrorCodeType
        {
            ReceiveError = 0,
            SendError = 1,
            NumErrorCodes
        }

#pragma warning disable 649
        struct Parameters
        {
            public NetworkDataStreamParameter dataStream;
            public NetworkConfigParameter config;

            public Parameters(NetworkSettings settings)
            {
                dataStream = settings.GetDataStreamParameters();
                config = settings.GetNetworkConfigParameters();
            }
        }
#pragma warning restore 649

        private Parameters m_NetworkParams;
        private NativeList<byte> m_DataStream;
        private NativeArray<int> m_DataStreamHead;
        private NetworkPipelineProcessor m_PipelineProcessor;
        private UdpCHeader.HeaderFlags m_DefaultHeaderFlags;

        private long m_UpdateTime;
        private long m_UpdateTimeAdjustment;

        private Unity.Mathematics.Random m_Rand;

        /// <summary>
        /// Gets the value of the last update time
        /// </summary>
        public long LastUpdateTime => m_UpdateTime;

        // properties :::::::::::::::::::::::::::::::::::::::::::::::::::::::::
        private const int InternalStateListening = 0;
        private const int InternalStateBound = 1;

        /// <summary>
        /// Gets or sets if the driver is Listening
        /// </summary>
        public bool Listening
        {
            get { return (m_InternalState[InternalStateListening] != 0); }
            private set { m_InternalState[InternalStateListening] = value ? 1 : 0; }
        }

        public bool Bound => m_InternalState[InternalStateBound] == 1;

        /// <summary>
        /// Helper function for creating a NetworkDriver.
        /// </summary>
        /// <param name="param">
        /// The <see cref="NewtorkSettings"/> for the new NetworkDriver.
        /// </param>
        /// <exception cref="InvalidOperationException"></exception>
        public static NetworkDriver Create(NetworkSettings settings)
        {
#if UNITY_WEBGL
            return new NetworkDriver(new IPCNetworkInterface(), settings);
#else
            return new NetworkDriver(new BaselibNetworkInterface(), settings);
#endif
        }

        /// <summary>
        /// Helper function for creating a NetworkDriver.
        /// </summary>
        public static NetworkDriver Create() => Create(new NetworkSettings(Allocator.Temp));

        /// <summary>
        /// Helper function for creating a NetworkDriver.
        /// </summary>
        /// <typeparam name="N"></typeparam>
        /// <param name="networkInterface">The custom interface to use.</param>
        public static NetworkDriver Create<N>(N networkInterface) where N : INetworkInterface
            => Create(networkInterface, new NetworkSettings(Allocator.Temp));

        /// <summary>
        /// Helper function for creating a NetworkDriver.
        /// </summary>
        /// <typeparam name="N"></typeparam>
        /// <param name="networkInterface">The custom interface to use.</param>
        /// <param name="settings">The <see cref="NewtorkSettings"/> for the new NetworkDriver.</param>
        public static NetworkDriver Create<N>(N networkInterface, NetworkSettings settings) where N : INetworkInterface
            => new NetworkDriver(networkInterface, settings);

        public NetworkDriver(INetworkInterface netIf)
            : this(netIf, new NetworkSettings()) {}

#if !UNITY_DOTSRUNTIME
        [Obsolete("Use Create(NetworkSettings) instead", false)]
        public static NetworkDriver Create(params INetworkParameter[] param)
        {
            return Create(NetworkSettings.FromArray(param));
        }

        [Obsolete("Use NetworkDriver(INetworkInterface, NetworkSettings) instead", false)]
        public NetworkDriver(INetworkInterface netIf, params INetworkParameter[] param)
            : this(netIf, NetworkSettings.FromArray(param)) {}

        [Obsolete("Use NetworkDriver(INetworkInterface, NetworkSettings) instead", false)]
        internal NetworkDriver(INetworkInterface netIf, INetworkProtocol netProtocol, params INetworkParameter[] param)
            : this(netIf, netProtocol, NetworkSettings.FromArray(param)) {}
#endif

        private static int InsertInAvailableIndex<T>(List<T> list, T element)
        {
            var n = list.Count;
            for (var i = 0; i < n; ++i)
            {
                if (list[i] == null)
                {
                    list[i] = element;
                    return i;
                }
            }

            list.Add(element);
            return n;
        }

        private static INetworkProtocol GetProtocolForParameters(NetworkSettings settings)
        {
            if (settings.TryGet<Relay.RelayNetworkParameter>(out _))
                return new Relay.RelayNetworkProtocol();
#if ENABLE_MANAGED_UNITYTLS
            if (settings.TryGet<TLS.SecureNetworkProtocolParameter>(out _))
                return new TLS.SecureNetworkProtocol();
#endif
            return new UnityTransportProtocol();
        }

        public NetworkDriver(INetworkInterface netIf, NetworkSettings settings)
            : this(netIf, GetProtocolForParameters(settings), settings) {}

        /// <summary>
        /// Constructor for NetworkDriver.
        /// </summary>
        /// <param name="param">
        /// An array of INetworkParameter. There are currently only two <see cref="INetworkParameter"/>,
        /// the <see cref="NetworkDataStreamParameter"/> and the <see cref="NetworkConfigParameter"/>.
        /// </param>
        /// <exception cref="ArgumentException">Thrown if the value for NetworkDataStreamParameter.size is smaller then zero.</exception>
        /// <exception cref="InvalidOperationException">Thrown if network interface couldn't be initialized.</exception>
        internal NetworkDriver(INetworkInterface netIf, INetworkProtocol netProtocol, NetworkSettings settings)
        {
#if UNITY_WEBGL
            UnityEngine.Debug.LogWarning("Unity Transport is not currently supported in WebGL. NetworkDriver will likely not work as intended.");
#endif

            m_NetworkParams = new Parameters(settings);

            netProtocol.Initialize(settings);
            m_NetworkProtocolIndex = InsertInAvailableIndex(s_NetworkProtocols, netProtocol);
            m_NetworkProtocolInterface = netProtocol.CreateProtocolInterface();

            m_NetworkInterfaceIndex = InsertInAvailableIndex(s_NetworkInterfaces, netIf);

            var result = netIf.Initialize(settings);
            if (0 != result)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                throw new InvalidOperationException($"Failed to initialize the NetworkInterface. Error Code: {result}.");
#else
                UnityEngine.Debug.LogError($"Failed to initialize the NetworkInterface. Error Code: {result}.");
#endif
            }

            m_NetworkSendInterface = netIf.CreateSendInterface();

            m_PipelineProcessor = new NetworkPipelineProcessor(settings);
            m_ParallelSendQueue = new NativeQueue<QueuedSendMessage>(Allocator.Persistent);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            m_PendingBeginSend = new NativeArray<int>(JobsUtility.MaxJobThreadCount * JobsUtility.CacheLineSize / 4, Allocator.Persistent);
#endif

            var stopwatchTime = Stopwatch.GetTimestamp();
            var time = stopwatchTime / (Stopwatch.Frequency / 1000);
            m_UpdateTime = m_NetworkParams.config.fixedFrameTimeMS > 0 ? 1 : time;
            m_UpdateTimeAdjustment = 0;

            m_Rand = new Unity.Mathematics.Random((uint)stopwatchTime);

            int initialStreamSize = m_NetworkParams.dataStream.size;
            if (initialStreamSize == 0)
                initialStreamSize = NetworkParameterConstants.DriverDataStreamSize;

            m_DataStream = new NativeList<byte>(initialStreamSize, Allocator.Persistent);
            m_DataStream.ResizeUninitialized(initialStreamSize);
            m_DataStreamHead = new NativeArray<int>(1, Allocator.Persistent);

            m_DefaultHeaderFlags = 0;

            m_NetworkAcceptQueue = new NativeQueue<int>(Allocator.Persistent);

            m_ConnectionList = new NativeList<Connection>(1, Allocator.Persistent);

            m_FreeList = new NativeQueue<int>(Allocator.Persistent);
            m_EventQueue = new NetworkEventQueue(NetworkParameterConstants.InitialEventQueueSize);

            const int reasons = (int)DisconnectReason.Count;
            m_DisconnectReasons = new NativeArray<byte>(reasons, Allocator.Persistent);
            for (var idx = 0; idx < reasons; ++idx)
                m_DisconnectReasons[idx] = (byte)idx;

            m_InternalState = new NativeArray<int>(2, Allocator.Persistent);
            m_PendingFree = new NativeQueue<int>(Allocator.Persistent);

            m_ProtocolStatus = new NativeReference<int>(Allocator.Persistent);
            m_ProtocolStatus.Value = 0;

            m_ErrorCodes = new NativeArray<int>((int)ErrorCodeType.NumErrorCodes, Allocator.Persistent);
            Listening = false;
        }

        // interface implementation :::::::::::::::::::::::::::::::::::::::::::
        public void Dispose()
        {
            if (!IsCreated)
                return;

            s_NetworkProtocols[m_NetworkProtocolIndex].Dispose();
            s_NetworkProtocols[m_NetworkProtocolIndex] = null;

            s_NetworkInterfaces[m_NetworkInterfaceIndex].Dispose();
            s_NetworkInterfaces[m_NetworkInterfaceIndex] = null;

            m_NetworkProtocolIndex = -1;
            m_NetworkInterfaceIndex = -1;

            m_DataStream.Dispose();
            m_DataStreamHead.Dispose();
            m_PipelineProcessor.Dispose();

            m_EventQueue.Dispose();
            m_DisconnectReasons.Dispose();

            m_NetworkAcceptQueue.Dispose();
            m_ConnectionList.Dispose();
            m_FreeList.Dispose();
            m_InternalState.Dispose();
            m_PendingFree.Dispose();
            m_ProtocolStatus.Dispose();
            m_ErrorCodes.Dispose();
            m_ParallelSendQueue.Dispose();
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            m_PendingBeginSend.Dispose();
#endif
        }

        /// <summary>
        /// Returns if NetworkDriver has been created
        /// </summary>
        public bool IsCreated => m_InternalState.IsCreated;

        [BurstCompile]
        struct UpdateJob : IJob
        {
            public NetworkDriver driver;

            public void Execute()
            {
                driver.InternalUpdate();
            }
        }

        [BurstCompile]
        struct ClearEventQueue : IJob
        {
            public NativeList<byte> dataStream;
            public NativeArray<int> dataStreamHead;
            public NetworkEventQueue eventQueue;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            public NativeArray<int> pendingSend;
            [ReadOnly] public NativeList<Connection> connectionList;
            [ReadOnly] public NativeArray<int> internalState;
#endif
            public void Execute()
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                for (int i = 0; i < connectionList.Length; ++i)
                {
                    int conCount = eventQueue.GetCountForConnection(i);
                    if (conCount != 0 && connectionList[i].State != NetworkConnection.State.Disconnected)
                    {
                        UnityEngine.Debug.LogError($"Resetting event queue with pending events (Count={conCount}, ConnectionID={i}) Listening: {internalState[InternalStateListening]}");
                    }
                }
                bool didPrint = false;
                for (int i = 0; i < JobsUtility.MaxJobThreadCount; ++i)
                {
                    if (pendingSend[i * JobsUtility.CacheLineSize / 4] > 0)
                    {
                        pendingSend[i * JobsUtility.CacheLineSize / 4] = 0;
                        if (!didPrint)
                        {
                            UnityEngine.Debug.LogError(
                                "Missing EndSend, calling BeginSend without calling EndSend will result in a memory leak");
                            didPrint = true;
                        }
                    }
                }
#endif
                eventQueue.Clear();
                dataStreamHead[0] = 0;
            }
        }

        private SessionIdToken GenerateRandomSessionIdToken(ref SessionIdToken token)
        {
            //SessionIdToken token = new SessionIdToken();
            for (uint i = 0; i < SessionIdToken.k_Length; ++i)
            {
                unsafe
                {
                    token.Value[i] = (byte)(m_Rand.NextUInt() & 0xFF);
                }
            }
            return token;
        }

        private void UpdateLastUpdateTime()
        {
            var stopwatchTime = Stopwatch.GetTimestamp();
            long now = m_NetworkParams.config.fixedFrameTimeMS > 0
                ? m_UpdateTime + m_NetworkParams.config.fixedFrameTimeMS
                : stopwatchTime / (Stopwatch.Frequency / 1000) - m_UpdateTimeAdjustment;

            m_Rand.InitState((uint)stopwatchTime);

            long frameTime = now - m_UpdateTime;
            if (m_NetworkParams.config.maxFrameTimeMS > 0 && frameTime > m_NetworkParams.config.maxFrameTimeMS)
            {
                m_UpdateTimeAdjustment += frameTime - m_NetworkParams.config.maxFrameTimeMS;
                now = m_UpdateTime + m_NetworkParams.config.maxFrameTimeMS;
            }

            m_UpdateTime = now;
        }

        /// <summary>
        /// Schedules update for driver. This should be called once a frame.
        /// </summary>
        /// <param name="dep">Job on which to depend.</param>
        /// <returns>The update job's handle</returns>
        public JobHandle ScheduleUpdate(JobHandle dep = default)
        {
            UpdateLastUpdateTime();

            var updateJob = new UpdateJob {driver = this};

            // Clearing the event queue and receiving/sending data only makes sense if we're bound.
            if (Bound)
            {
                var clearJob = new ClearEventQueue
                {
                    dataStream = m_DataStream,
                    dataStreamHead = m_DataStreamHead,
                    eventQueue = m_EventQueue,
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    pendingSend = m_PendingBeginSend,
                    connectionList = m_ConnectionList,
                    internalState = m_InternalState
#endif
                };

                var handle = clearJob.Schedule(dep);
                handle = updateJob.Schedule(handle);
                handle = s_NetworkInterfaces[m_NetworkInterfaceIndex].ScheduleReceive(new NetworkPacketReceiver {m_Driver = this}, handle);
                handle = s_NetworkInterfaces[m_NetworkInterfaceIndex].ScheduleSend(m_ParallelSendQueue, handle);

                return handle;
            }
            else
            {
                return updateJob.Schedule(dep);
            }
        }

        /// <summary>
        /// Schedules flushing the sendqueue. Should be called in cases where you want the driver to send before the
        /// next <see cref="ScheduleUpdate"/> is called.
        /// </summary>
        /// <param name="dep">Job on which to depend.</param>
        /// <returns>The job handle</returns>
        public JobHandle ScheduleFlushSend(JobHandle dep)
        {
            return s_NetworkInterfaces[m_NetworkInterfaceIndex].ScheduleSend(m_ParallelSendQueue, dep);
        }

        void InternalUpdate()
        {
            m_PipelineProcessor.Timestamp = m_UpdateTime;
            while (m_PendingFree.TryDequeue(out var free))
            {
                int ver = m_ConnectionList[free].Version + 1;
                if (ver == 0)
                    ver = 1;
                m_ConnectionList[free] = new Connection {Id = free, Version = ver, IsAccepted = 0};
                m_FreeList.Enqueue(free);
            }

            CheckTimeouts();

            if (m_NetworkProtocolInterface.NeedsUpdate)
            {
                var queueHandle = NetworkSendQueueHandle.ToTempHandle(m_ParallelSendQueue.AsParallelWriter());
                m_NetworkProtocolInterface.Update.Ptr.Invoke(m_UpdateTime, ref m_NetworkSendInterface, ref queueHandle, m_NetworkProtocolInterface.UserData);
            }

            m_PipelineProcessor.UpdateReceive(this, out var updateCount);

            // TODO: Find a good way to establish a good limit (connections*pipelines/2?)
            var updateLimit = math.max(0, (m_ConnectionList.Length - m_FreeList.Count) * 64);

            if (updateCount > updateLimit)
            {
                UnityEngine.Debug.LogWarning(
                    FixedString.Format("A lot of pipeline updates have been queued, possibly too many being scheduled in pipeline logic, queue count: {0}", updateCount));
            }

            m_DefaultHeaderFlags = UdpCHeader.HeaderFlags.HasPipeline;
            m_PipelineProcessor.UpdateSend(ToConcurrentSendOnly(), out updateCount);
            if (updateCount > updateLimit)
            {
                UnityEngine.Debug.LogWarning(
                    FixedString.Format("A lot of pipeline updates have been queued, possibly too many being scheduled in pipeline logic, queue count: {0}", updateCount));
            }

            m_DefaultHeaderFlags = 0;
        }

        /// <summary>
        /// Create a new pipeline.
        /// </summary>
        /// <param name="stages">
        /// An array of stages the pipeline should contain.
        /// </param>
        /// <exception cref="InvalidOperationException">If the driver is not created properly</exception>
        /// <exception cref="InvalidOperationException">A connection has already been established</exception>
        public NetworkPipeline CreatePipeline(params Type[] stages)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (!m_InternalState.IsCreated)
                throw new InvalidOperationException(
                    "Driver must be constructed with a populated or empty INetworkParameter params list");
            if (m_ConnectionList.Length > 0)
                throw new InvalidOperationException(
                    "Pipelines cannot be created after establishing connections");
#endif
            return m_PipelineProcessor.CreatePipeline(stages);
        }

        /// <summary>
        /// Bind the driver to a endpoint.
        /// </summary>
        /// <param name="endpoint">The endpoint to bind to.</param>
        /// <value>Returns 0 on success. And a negative value if a error occured.</value>
        /// <exception cref="InvalidOperationException">If the driver is not created properly</exception>
        /// <exception cref="InvalidOperationException">If bind is called more then once on the driver</exception>
        /// <exception cref="InvalidOperationException">If bind is called after a connection has already been established</exception>
        public int Bind(NetworkEndPoint endpoint)
        {
            if (s_NetworkInterfaces[m_NetworkInterfaceIndex].CreateInterfaceEndPoint(endpoint, out var ifEndPoint) != 0)
            {
                return -1;
            }

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (!m_InternalState.IsCreated)
                throw new InvalidOperationException(
                    "Driver must be constructed with a populated or empty INetworkParameter params list");
            // question: should this really be an error?
            if (m_InternalState[InternalStateBound] != 0)
                throw new InvalidOperationException(
                    "Bind can only be called once per NetworkDriver");
            if (m_ConnectionList.Length > 0)
                throw new InvalidOperationException(
                    "Bind cannot be called after establishing connections");
#endif
            var protocolBind = s_NetworkProtocols[m_NetworkProtocolIndex].Bind(s_NetworkInterfaces[m_NetworkInterfaceIndex], ref ifEndPoint);

            m_InternalState[InternalStateBound] = protocolBind == 0 ? 1 : 0;

            return protocolBind;
        }

        /// <summary>
        /// Set the driver to Listen for incoming connections
        /// </summary>
        /// <value>Returns 0 on success.</value>
        /// <exception cref="InvalidOperationException">If the driver is not created properly</exception>
        /// <exception cref="InvalidOperationException">If listen is called more then once on the driver</exception>
        /// <exception cref="InvalidOperationException">If bind has not been called before calling Listen.</exception>
        public int Listen()
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (!m_InternalState.IsCreated)
                throw new InvalidOperationException(
                    "Driver must be constructed with a populated or empty INetworkParameter params list");

            if (Listening)
                throw new InvalidOperationException(
                    "Listen can only be called once per NetworkDriver");
            if (!Bound)
                throw new InvalidOperationException(
                    "Listen can only be called after a successful call to Bind");
#endif
            if (!Bound)
                return -1;
            var ret = s_NetworkInterfaces[m_NetworkInterfaceIndex].Listen();
            if (ret == 0)
                Listening = true;
            return ret;
        }

        /// <summary>
        /// Checks to see if there are any new connections to Accept.
        /// </summary>
        /// <value>If accept fails it returns a default NetworkConnection.</value>
        public NetworkConnection Accept()
        {
            if (!Listening)
                return default;

            if (!m_NetworkAcceptQueue.TryDequeue(out var id))
                return default;

            var connection = m_ConnectionList[id];
            connection.State = NetworkConnection.State.Connected;
            connection.IsAccepted = 1;
            SetConnection(connection);

            return new NetworkConnection {m_NetworkId = id, m_NetworkVersion = m_ConnectionList[id].Version};
        }

        /// <summary>
        /// Connects the driver to a endpoint
        /// </summary>
        /// <value>If connect fails it returns a default NetworkConnection.</value>
        /// <exception cref="InvalidOperationException">If the driver is not created properly</exception>
        public NetworkConnection Connect(NetworkEndPoint endpoint)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (!m_InternalState.IsCreated)
                throw new InvalidOperationException(
                    "Driver must be constructed with a populated or empty INetworkParameter params list");
#endif

            if (!Bound)
            {
                var nep = endpoint.Family == NetworkFamily.Ipv6 ? NetworkEndPoint.AnyIpv6 : NetworkEndPoint.AnyIpv4;
                if (Bind(nep) != 0)
                    return default;
            }

            var result = s_NetworkProtocols[m_NetworkProtocolIndex].CreateConnectionAddress(
                s_NetworkInterfaces[m_NetworkInterfaceIndex], endpoint, out var address);
            if (result != 0)
                return default;

            if (!m_FreeList.TryDequeue(out var id))
            {
                id = m_ConnectionList.Length;
                m_ConnectionList.Add(new Connection {Id = id, Version = 1});
            }

            int ver = m_ConnectionList[id].Version;
            var receiveToken = new SessionIdToken();
            GenerateRandomSessionIdToken(ref receiveToken);
            var c = new Connection
            {
                Id = id,
                Version = ver,
                State = NetworkConnection.State.Connecting,
                Address = address,
                ConnectAttempts = 1,
                LastNonDataSend = m_UpdateTime,
                LastReceive = 0,
                SendToken = default,
                ReceiveToken = receiveToken,
                IsAccepted = 0
            };

            SetConnection(c);
            var netcon = new NetworkConnection {m_NetworkId = id, m_NetworkVersion = ver};

            var queueHandle = NetworkSendQueueHandle.ToTempHandle(m_ParallelSendQueue.AsParallelWriter());
            m_NetworkProtocolInterface.Connect.Ptr.Invoke(ref c, ref m_NetworkSendInterface, ref queueHandle, m_NetworkProtocolInterface.UserData);

            m_PipelineProcessor.initializeConnection(netcon);

            return netcon;
        }

        /// <summary>
        /// Disconnects a NetworkConnection
        /// </summary>
        /// <param name="id">The NetworkConnection we want to Disconnect.</param>
        /// <value>Return 0 on success.</value>
        public int Disconnect(NetworkConnection id)
        {
            Connection connection;
            if ((connection = GetConnection(id)) == Connection.Null)
                return 0;

            if (connection.State == NetworkConnection.State.Connected)
            {
                var queueHandle = NetworkSendQueueHandle.ToTempHandle(m_ParallelSendQueue.AsParallelWriter());
                m_NetworkProtocolInterface.Disconnect.Ptr.Invoke(ref connection, ref m_NetworkSendInterface, ref queueHandle, m_NetworkProtocolInterface.UserData);
            }
            RemoveConnection(connection);

            return 0;
        }

        /// <summary>
        /// Returns the PipelineBuffers for a specific pipeline and stage.
        /// </summary>
        /// <param name="pipeline">Pipeline for which to get the buffers.</param>
        /// <param name="stageId">Pipeline for which to get the buffers.</param>
        /// <param name="connection">Connection for which to the buffers.</param>
        /// <param name="readProcessingBuffer">The buffer used to process read (receive) operations.</param>
        /// <param name="writeProcessingBuffer">The buffer used to process write (send) operations.</param>
        /// <param name="sharedBuffer">The buffer containing the internal state of the pipeline stage.</param>
        /// <exception cref="InvalidOperationException">If the the connection is invalid.</exception>
        public void GetPipelineBuffers(NetworkPipeline pipeline, NetworkPipelineStageId stageId, NetworkConnection connection, out NativeArray<byte> readProcessingBuffer, out NativeArray<byte> writeProcessingBuffer, out NativeArray<byte> sharedBuffer)
        {
            if (connection.m_NetworkId < 0 || connection.m_NetworkId >= m_ConnectionList.Length ||
                m_ConnectionList[connection.m_NetworkId].Version != connection.m_NetworkVersion)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                throw new InvalidOperationException("Invalid connection");
#else
                UnityEngine.Debug.LogError("Trying to get pipeline buffers for invalid connection.");
                readProcessingBuffer = default;
                writeProcessingBuffer = default;
                sharedBuffer = default;
                return;
#endif
            }
            m_PipelineProcessor.GetPipelineBuffers(pipeline, stageId, connection, out readProcessingBuffer, out writeProcessingBuffer, out sharedBuffer);
        }

        /// <summary>
        /// Gets the connection state using the specified <see cref="NetworkConnection"/>
        /// </summary>
        /// <param name="con">The connection</param>
        /// <returns>The network connection state</returns>
        public NetworkConnection.State GetConnectionState(NetworkConnection con)
        {
            Connection connection;
            if ((connection = GetConnection(con)) == Connection.Null)
                return NetworkConnection.State.Disconnected;
            return connection.State;
        }

        public NetworkEndPoint RemoteEndPoint(NetworkConnection id)
        {
            if (id == default)
                return default;

            Connection connection;
            if ((connection = GetConnection(id)) == Connection.Null)
                return default;
            return s_NetworkProtocols[m_NetworkProtocolIndex].GetRemoteEndPoint(s_NetworkInterfaces[m_NetworkInterfaceIndex], connection.Address);
        }

        /// <summary>
        /// Returns local <see cref="NetworkEndPoint"/>
        /// </summary>
        /// <returns>The network end point</returns>
        public NetworkEndPoint LocalEndPoint()
        {
            var ep = s_NetworkInterfaces[m_NetworkInterfaceIndex].LocalEndPoint;
            return s_NetworkInterfaces[m_NetworkInterfaceIndex].GetGenericEndPoint(ep);
        }

        /// <summary>
        /// Max headersize including optional <see cref="NetworkPipeline"/>
        /// </summary>
        /// <param name="pipe">The pipeline for which to get the maximum header size.</param>
        /// <returns>The maximum header size.</returns>
        public int MaxHeaderSize(NetworkPipeline pipe)
        {
            return ToConcurrentSendOnly().MaxHeaderSize(pipe);
        }

        internal int MaxProtocolHeaderSize()
        {
            return m_NetworkProtocolInterface.PaddingSize;
        }

        /// <summary>
        /// Acquires a DataStreamWriter for starting a asynchronous send.
        /// </summary>
        /// <param name="pipe">The NetworkPipeline to write through</param>
        /// <param name="id">The NetworkConnection id to write through</param>
        /// <param name="writer">A DataStreamWriter to write to</param>
        /// <param name="requiredPayloadSize">If you require the payload to be of certain size</param>
        /// <value>Returns <see cref="StatusCode.Success"/> on a successful acquire. Otherwise returns an <see cref="StatusCode"/> indicating the error.</value>
        /// <remarks> Will throw a <exception cref="InvalidOperationException"></exception> if the connection is in a Connecting state.</remarks>
        public int BeginSend(NetworkPipeline pipe, NetworkConnection id, out DataStreamWriter writer, int requiredPayloadSize = 0)
        {
            return ToConcurrentSendOnly().BeginSend(pipe, id, out writer, requiredPayloadSize);
        }

        /// <summary>
        /// Acquires a DataStreamWriter for starting a asynchronous send.
        /// </summary>
        /// <param name="id">The NetworkConnection id to write through</param>
        /// <param name="writer">A DataStreamWriter to write to</param>
        /// <param name="requiredPayloadSize">If you require the payload to be of certain size</param>
        /// <value>Returns <see cref="StatusCode.Success"/> on a successful acquire. Otherwise returns an <see cref="StatusCode"/> indicating the error.</value>
        /// <remarks> Will throw a <exception cref="InvalidOperationException"></exception> if the connection is in a Connecting state.</remarks>
        public int BeginSend(NetworkConnection id, out DataStreamWriter writer, int requiredPayloadSize = 0)
        {
            return ToConcurrentSendOnly().BeginSend(NetworkPipeline.Null, id, out writer, requiredPayloadSize);
        }

        /// <summary>
        /// Ends a asynchronous send.
        /// </summary>
        /// <param name="writer">If you require the payload to be of certain size.</param>
        /// <value>The length of the buffer sent if nothing went wrong.</value>
        /// <exception cref="InvalidOperationException">If endsend is called with a matching BeginSend call.</exception>
        /// <exception cref="InvalidOperationException">If the connection got closed between the call of being and end send.</exception>
        public int EndSend(DataStreamWriter writer)
        {
            return ToConcurrentSendOnly().EndSend(writer);
        }

        /// <summary>
        /// Aborts a asynchronous send.
        /// </summary>
        /// <param name="writer">If you require the payload to be of certain size.</param>
        /// <value>The length of the buffer sent if nothing went wrong.</value>
        /// <exception cref="InvalidOperationException">If endsend is called with a matching BeginSend call.</exception>
        /// <exception cref="InvalidOperationException">If the connection got closed between the call of being and end send.</exception>
        public void AbortSend(DataStreamWriter writer)
        {
            ToConcurrentSendOnly().AbortSend(writer);
        }

        /// <summary>
        /// Pops an event
        /// </summary>
        /// <param name="con">Connection on which the event occured.</param>
        /// <param name="reader">Stream reader for the event's data.</param>
        /// <returns>The event's type</returns>
        /// <value>Returns the type of event received, if the value is a <see cref="NetworkEvent.Type.Disconnect"/> event
        /// then the DataStreamReader will contain the disconnect reason. If a listening NetworkDriver has received Data
        /// events from a client, but the NetworkDriver has not Accepted the NetworkConnection yet, the Data event will
        /// be discarded.<value/>
        public NetworkEvent.Type PopEvent(out NetworkConnection con, out DataStreamReader reader)
        {
            return PopEvent(out con, out reader, out var _);
        }

        /// <summary>
        /// Pops an event
        /// </summary>
        /// <param name="con">Connection on which the event occured.</param>
        /// <param name="reader">Stream reader for the event's data.</param>
        /// <param name="pipeline">Pipeline on which the event was received.</param>
        /// <returns>The event's type</returns>
        public NetworkEvent.Type PopEvent(out NetworkConnection con, out DataStreamReader reader, out NetworkPipeline pipeline)
        {
            reader = default;

            NetworkEvent.Type type = default;
            int id = default;
            int offset = default;
            int size = default;
            int pipelineId = default;

            while (true)
            {
                type = m_EventQueue.PopEvent(out id, out offset, out size, out pipelineId);

                //This is in service of not providing any means for a server's / listening NetworkDriver's user-level code to obtain a NetworkConnection handle
                //that corresponds to an underlying Connection that lives in m_ConnectionList without having obtained it from Accept() first. This is a stopgap
                //change for now to make NetworkDriver's API adhere to that idiom, but a more thorough and longer-term fix would be to properly implement some
                //sort of TCP-style SYN/SYN-ACK/ACK handshake, or at least have Accept() be necessary prior to the listener sending a ConnectionAccept packet.
                if (id >= 0 && type == NetworkEvent.Type.Data && m_ConnectionList[id].IsAccepted == 0)
                {
                    UnityEngine.Debug.LogWarning("A NetworkEvent.Data event was discarded for a connection that had not been accepted yet. To avoid this, consider calling Accept()" +
                        " prior to PopEvent() in your project's network update loop, or only use PopEventForConnection() in conjunction with Accept().");
                    continue;
                }

                break;
            }

            pipeline = new NetworkPipeline { Id = pipelineId };

            if (type == NetworkEvent.Type.Disconnect && offset < 0)
                reader = new DataStreamReader(m_DisconnectReasons.GetSubArray(math.abs(offset), 1));
            else if (size > 0)
                reader = new DataStreamReader(((NativeArray<byte>)m_DataStream).GetSubArray(offset, size));
            con = id < 0
                ? default
                : new NetworkConnection {m_NetworkId = id, m_NetworkVersion = m_ConnectionList[id].Version};

            return type;
        }

        /// <summary>
        /// Pops an event for a specific connection
        /// </summary>
        /// <param name="connectionId">Connection for which to pop the next event.</param>
        /// <param name="reader">Stream reader for the event's data.</param>
        /// <returns>The event's type</returns>
        /// <value>Returns the type of event received, if the value is a <see cref="NetworkEvent.Type.Disconnect"/> event
        /// then the DataStreamReader will contain the disconnect reason.<value/>
        public NetworkEvent.Type PopEventForConnection(NetworkConnection connectionId, out DataStreamReader reader)
        {
            return PopEventForConnection(connectionId, out reader, out var _);
        }

        /// <summary>
        /// Pops an event for a specific connection
        /// </summary>
        /// <param name="connectionId">Connection for which to pop the next event.</param>
        /// <param name="reader">Stream reader for the event's data.</param>
        /// <param name="pipeline">Pipeline on which the event was received.</param>
        /// <returns>The event's type</returns>
        public NetworkEvent.Type PopEventForConnection(NetworkConnection connectionId, out DataStreamReader reader, out NetworkPipeline pipeline)
        {
            reader = default;
            pipeline = default;

            if (connectionId.m_NetworkId < 0 || connectionId.m_NetworkId >= m_ConnectionList.Length ||
                m_ConnectionList[connectionId.m_NetworkId].Version != connectionId.m_NetworkVersion)
                return (int)NetworkEvent.Type.Empty;
            var type = m_EventQueue.PopEventForConnection(connectionId.m_NetworkId, out var offset, out var size, out var pipelineId);
            pipeline = new NetworkPipeline { Id = pipelineId };

            if (type == NetworkEvent.Type.Disconnect && offset < 0)
                reader = new DataStreamReader(m_DisconnectReasons.GetSubArray(math.abs(offset), 1));
            else if (size > 0)
                reader = new DataStreamReader(((NativeArray<byte>)m_DataStream).GetSubArray(offset, size));

            return type;
        }

        /// <summary>
        /// Returns the size of the EventQueue for a specific connection
        /// </summary>
        /// <param name="connectionId">Connection for which to get the event queue size.</param>
        /// <value>If the connection is valid it returns the size of the event queue otherwise it returns 0.</value>
        public int GetEventQueueSizeForConnection(NetworkConnection connectionId)
        {
            if (connectionId.m_NetworkId < 0 || connectionId.m_NetworkId >= m_ConnectionList.Length ||
                m_ConnectionList[connectionId.m_NetworkId].Version != connectionId.m_NetworkVersion)
                return 0;
            return m_EventQueue.GetCountForConnection(connectionId.m_NetworkId);
        }

        // internal helper functions ::::::::::::::::::::::::::::::::::::::::::
        void AddConnectEvent(int id)
        {
            m_EventQueue.PushEvent(new NetworkEvent {connectionId = id, type = NetworkEvent.Type.Connect});
        }

        void AddDisconnectEvent(int id, Error.DisconnectReason reason = DisconnectReason.Default)
        {
            m_EventQueue.PushEvent(new NetworkEvent { connectionId = id, type = NetworkEvent.Type.Disconnect, status = (int)reason });
        }

        Connection GetConnection(NetworkConnection id)
        {
            if (id.m_NetworkId < 0 || id.m_NetworkId >= m_ConnectionList.Length)
                return Connection.Null;

            var con = m_ConnectionList[id.m_NetworkId];
            if (con.Version != id.m_NetworkVersion)
                return Connection.Null;
            return con;
        }

        Connection GetConnection(NetworkInterfaceEndPoint address, SessionIdToken sessionId)
        {
            for (int i = 0; i < m_ConnectionList.Length; i++)
            {
                if (address == m_ConnectionList[i].Address && m_ConnectionList[i].ReceiveToken == sessionId)
                    return m_ConnectionList[i];
            }

            return Connection.Null;
        }

        Connection GetNewConnection(NetworkInterfaceEndPoint address, SessionIdToken sessionId)
        {
            for (int i = 0; i < m_ConnectionList.Length; i++)
            {
                if (address == m_ConnectionList[i].Address && m_ConnectionList[i].SendToken == sessionId)
                    return m_ConnectionList[i];
            }

            return Connection.Null;
        }

        void SetConnection(Connection connection)
        {
            m_ConnectionList[connection.Id] = connection;
        }

        bool RemoveConnection(Connection connection)
        {
            if (connection.State != NetworkConnection.State.Disconnected && connection == m_ConnectionList[connection.Id])
            {
                connection.State = NetworkConnection.State.Disconnected;
                m_ConnectionList[connection.Id] = connection;
                m_PendingFree.Enqueue(connection.Id);

                return true;
            }

            return false;
        }

        void UpdateConnection(Connection connection)
        {
            if (connection == m_ConnectionList[connection.Id])
                SetConnection(connection);
        }

        void CheckTimeouts()
        {
            for (int i = 0; i < m_ConnectionList.Length; ++i)
            {
                var connection = m_ConnectionList[i];
                if (connection == Connection.Null)
                    continue;

                long now = m_UpdateTime;

                var netcon = new NetworkConnection {m_NetworkId = connection.Id, m_NetworkVersion = connection.Version};

                // Check for connect timeout and connection attemps.
                // Note that while connecting, LastNonDataSend can only track connection requests.
                if (connection.State == NetworkConnection.State.Connecting &&
                    now - connection.LastNonDataSend > m_NetworkParams.config.connectTimeoutMS)
                {
                    if (connection.ConnectAttempts >= m_NetworkParams.config.maxConnectAttempts)
                    {
                        Disconnect(netcon);
                        AddDisconnectEvent(connection.Id, DisconnectReason.MaxConnectionAttempts);
                        continue;
                    }

                    connection.ConnectAttempts = ++connection.ConnectAttempts;
                    connection.LastNonDataSend = now;
                    SetConnection(connection);

                    var queueHandle = NetworkSendQueueHandle.ToTempHandle(m_ParallelSendQueue.AsParallelWriter());
                    m_NetworkProtocolInterface.Connect.Ptr.Invoke(
                        ref connection, ref m_NetworkSendInterface, ref queueHandle, m_NetworkProtocolInterface.UserData);
                }

                // Check for the disconnect timeout.
                if (connection.State == NetworkConnection.State.Connected &&
                    now - connection.LastReceive > m_NetworkParams.config.disconnectTimeoutMS)
                {
                    Disconnect(netcon);
                    AddDisconnectEvent(connection.Id, DisconnectReason.Timeout);
                }

                // Check for the heartbeat timeout.
                if (connection.State == NetworkConnection.State.Connected &&
                    connection.DidReceiveData != 0 &&
                    m_NetworkParams.config.heartbeatTimeoutMS > 0 &&
                    now - connection.LastReceive > m_NetworkParams.config.heartbeatTimeoutMS &&
                    now - connection.LastNonDataSend > m_NetworkParams.config.heartbeatTimeoutMS)
                {
                    connection.LastNonDataSend = now;
                    SetConnection(connection);

                    var queueHandle = NetworkSendQueueHandle.ToTempHandle(m_ParallelSendQueue.AsParallelWriter());
                    m_NetworkProtocolInterface.ProcessSendPing.Ptr.Invoke(ref connection, ref m_NetworkSendInterface, ref queueHandle, m_NetworkProtocolInterface.UserData);
                }
            }
        }

        /// <summary>
        /// Gets or sets Receive Error Code
        /// </summary>
        public int ReceiveErrorCode
        {
            get => m_ErrorCodes[(int)ErrorCodeType.ReceiveError];
            internal set
            {
                if (value != 0)
                {
                    UnityEngine.Debug.LogError(FixedString.Format("Error on receive, errorCode = {0}", value));
                }
                m_ErrorCodes[(int)ErrorCodeType.ReceiveError] = value;
            }
        }

        internal bool IsAddressUsed(NetworkInterfaceEndPoint address)
        {
            for (int i = 0; i < m_ConnectionList.Length; i++)
            {
                if (address == m_ConnectionList[i].Address)
                    return true;
            }
            return false;
        }

        internal void AppendPacket(IntPtr dataStream, ref NetworkInterfaceEndPoint endpoint, int dataLen)
        {
            var command = default(ProcessPacketCommand);

            var queueHandle = NetworkSendQueueHandle.ToTempHandle(m_ParallelSendQueue.AsParallelWriter());
            m_NetworkProtocolInterface.ProcessReceive.Ptr.Invoke(dataStream, ref endpoint, dataLen, ref m_NetworkSendInterface, ref queueHandle, m_NetworkProtocolInterface.UserData, ref command);

            switch (command.Type)
            {
                case ProcessPacketCommandType.AddressUpdate:
                {
                    for (int i = 0; i < m_ConnectionList.Length; i++)
                    {
                        if (command.Address == m_ConnectionList[i].Address && command.SessionId == m_ConnectionList[i].ReceiveToken)
                            m_ConnectionList.ElementAt(i).Address = command.As.AddressUpdate.NewAddress;
                    }
                } break;

                case ProcessPacketCommandType.ConnectionAccept:
                {
                    Connection c = GetConnection(command.Address, command.SessionId);

                    if (c != Connection.Null)
                    {
                        c.DidReceiveData = 1;
                        c.LastReceive = m_UpdateTime;
                        SetConnection(c);

                        if (c.State == NetworkConnection.State.Connecting)
                        {
                            c.SendToken = command.As.ConnectionAccept.ConnectionToken;

                            c.State = NetworkConnection.State.Connected;
                            c.IsAccepted = 1;
                            UpdateConnection(c);
                            AddConnectEvent(c.Id);
                        }
                    }
                } break;

                case ProcessPacketCommandType.ConnectionReject:
                    break;

                case ProcessPacketCommandType.ConnectionRequest:
                {
                    if (Listening == false)
                    {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        UnityEngine.Debug.LogError(string.Format("ConnectionRequest received, but Listening == false. Address: [{0}]", command.Address.ToFixedString()));
#endif
                        return;
                    }

                    Connection c = GetNewConnection(command.Address, command.SessionId);
                    if (c == Connection.Null || c.State == NetworkConnection.State.Disconnected)
                    {
                        var sessionId = new SessionIdToken();
                        GenerateRandomSessionIdToken(ref sessionId);
                        if (!m_FreeList.TryDequeue(out var id))
                        {
                            id = m_ConnectionList.Length;
                            m_ConnectionList.Add(new Connection {Id = id, Version = 1});
                        }

                        int ver = m_ConnectionList[id].Version;
                        c = new Connection
                        {
                            Id = id,
                            Version = ver,
                            ReceiveToken = sessionId,
                            SendToken = command.SessionId,
                            State = NetworkConnection.State.Connected,
                            Address = command.Address,
                            ConnectAttempts = 1,
                            LastReceive = m_UpdateTime,
                            IsAccepted = 0
                        };

                        m_PipelineProcessor.initializeConnection(new NetworkConnection {m_NetworkId = id, m_NetworkVersion = c.Version});
                        m_NetworkAcceptQueue.Enqueue(id);
                    }

                    c.LastNonDataSend = m_UpdateTime;
                    SetConnection(c);

                    m_NetworkProtocolInterface.ProcessSendConnectionAccept.Ptr.Invoke(ref c, ref m_NetworkSendInterface, ref queueHandle, m_NetworkProtocolInterface.UserData);
                } break;

                case ProcessPacketCommandType.Disconnect:
                {
                    Connection c = GetConnection(command.Address, command.SessionId);
                    if (c != Connection.Null)
                    {
                        if (RemoveConnection(c))
                            AddDisconnectEvent(c.Id, DisconnectReason.ClosedByRemote);
                    }
                } break;

                case ProcessPacketCommandType.Ping:
                {
                    Connection c = GetConnection(command.Address, command.SessionId);
                    if (c == Connection.Null || c.State != NetworkConnection.State.Connected)
                    {
                        return;
                    }

                    c.DidReceiveData = 1;
                    c.LastReceive = m_UpdateTime;
                    c.LastNonDataSend = m_UpdateTime;
                    UpdateConnection(c);

                    m_NetworkProtocolInterface.ProcessSendPong.Ptr.Invoke(ref c, ref m_NetworkSendInterface, ref queueHandle, m_NetworkProtocolInterface.UserData);
                } break;

                case ProcessPacketCommandType.Pong:
                {
                    Connection c = GetConnection(command.Address, command.SessionId);
                    if (c != Connection.Null)
                    {
                        c.DidReceiveData = 1;
                        c.LastReceive = m_UpdateTime;
                        UpdateConnection(c);
                    }
                } break;

                case ProcessPacketCommandType.DataWithImplicitConnectionAccept:
                {
                    Connection c = GetConnection(command.Address, command.SessionId);
                    if (c == Connection.Null)
                    {
                        //There used to be a LogError message here that read "DataWithImplicitConnectionAccept received, but GetConnection returned null."
                        //The log message would occur after a server forcibly disconnected a client while the client was still sending packets.
                        //There may be more scenarios where this could occur, but those are the expected results in that particular scenario.
                        //Such a log message does not seem to actually prompt the user to take any action or make any change, and serves as little
                        //more than noise.
                        return;
                    }

                    c.DidReceiveData = 1;
                    c.LastReceive = m_UpdateTime;
                    UpdateConnection(c);

                    if (c.State == NetworkConnection.State.Connecting)
                    {
                        c.SendToken = command.As.DataWithImplicitConnectionAccept.ConnectionToken;

                        c.State = NetworkConnection.State.Connected;
                        UpdateConnection(c);
                        UnityEngine.Assertions.Assert.IsTrue(!Listening);
                        AddConnectEvent(c.Id);
                    }

                    if (c.State == NetworkConnection.State.Connected)
                    {
                        var memoryOffset = PinMemoryTillUpdate(command.As.DataWithImplicitConnectionAccept.Offset + command.As.DataWithImplicitConnectionAccept.Length);
                        var sliceOffset = memoryOffset + command.As.DataWithImplicitConnectionAccept.Offset;

                        if (command.As.DataWithImplicitConnectionAccept.HasPipeline)
                        {
                            var netCon = new NetworkConnection {m_NetworkId = c.Id, m_NetworkVersion = c.Version};
                            m_PipelineProcessor.Receive(this, netCon, ((NativeArray<byte>)m_DataStream).GetSubArray(sliceOffset, command.As.DataWithImplicitConnectionAccept.Length));

                            return;
                        }

                        m_EventQueue.PushEvent(new NetworkEvent
                        {
                            connectionId = c.Id,
                            type = NetworkEvent.Type.Data,
                            offset = sliceOffset,
                            size = command.As.DataWithImplicitConnectionAccept.Length
                        });
                    }
                } break;

                case ProcessPacketCommandType.Data:
                {
                    Connection c = GetConnection(command.Address, command.SessionId);
                    if (c == Connection.Null)
                    {
                        //There used to be a LogError message here that read "DataWithImplicitConnectionAccept received, but GetConnection returned null."
                        //The log message would occur after a server forcibly disconnected a client while the client was still sending packets.
                        //There may be more scenarios where this could occur, but those are the expected results in that particular scenario.
                        //Such a log message does not seem to actually prompt the user to take any action or make any change, and serves as little
                        //more than noise.
                        return;
                    }

                    c.DidReceiveData = 1;
                    c.LastReceive = m_UpdateTime;
                    UpdateConnection(c);

                    if (c.State == NetworkConnection.State.Connected)
                    {
                        var memoryOffset = PinMemoryTillUpdate(command.As.Data.Offset + command.As.Data.Length);
                        var sliceOffset = memoryOffset + command.As.Data.Offset;

                        if (command.As.Data.HasPipeline)
                        {
                            var netCon = new NetworkConnection {m_NetworkId = c.Id, m_NetworkVersion = c.Version};
                            m_PipelineProcessor.Receive(this, netCon, ((NativeArray<byte>)m_DataStream).GetSubArray(sliceOffset, command.As.Data.Length));
                            return;
                        }

                        m_EventQueue.PushEvent(new NetworkEvent
                        {
                            connectionId = c.Id,
                            type = NetworkEvent.Type.Data,
                            offset = sliceOffset,
                            size = command.As.Data.Length
                        });
                    }
                } break;

                case ProcessPacketCommandType.ProtocolStatusUpdate:
                    m_ProtocolStatus.Value = command.As.ProtocolStatusUpdate.Status;
                    break;

                case ProcessPacketCommandType.Drop:
                    break;
            }
        }

        // Interface for receiving data from a pipeline
        internal unsafe void PushDataEvent(NetworkConnection con, int pipelineId, byte* dataPtr, int dataLength)
        {
            var isInsideOurReceiveBuffer = IsPointerInsideDataStream(dataPtr, dataLength, out var sliceOffset);
            if (isInsideOurReceiveBuffer == false)
            {
                // Pointer is NOT a subset of our receive buffer, we need to copy
                var allocatedLength = dataLength;
                var ptr = AllocateMemory(ref allocatedLength); // streamBasePtr is not valid after this call

                if (ptr == IntPtr.Zero || allocatedLength < dataLength)
                {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    UnityEngine.Debug.LogError("Out of memory in PushDataEvent");
#endif
                    return;
                }

                UnsafeUtility.MemCpy((byte*)ptr.ToPointer(), dataPtr, dataLength);

                sliceOffset = PinMemoryTillUpdate(dataLength);
            }

            m_EventQueue.PushEvent(new NetworkEvent
            {
                pipelineId = (short)pipelineId,
                connectionId = con.m_NetworkId,
                type = NetworkEvent.Type.Data,
                offset = sliceOffset,
                size = dataLength
            });
        }

        /// <summary>
        /// Moves 'head' of allocator for 'length' bytes. Use this to 'pin' memory in till the next update. If you don't call it - it is 'pinned' till the next call to <see cref="AllocateMemory"/>
        /// Means every time you call <see cref="AllocateMemory"/> without <see cref="PinMemoryTillUpdate"/> memory is overriden
        /// </summary>
        /// <param name="length">Bytes to move</param>
        /// <returns>Returns head of pinned memory</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal int PinMemoryTillUpdate(int length)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (m_DataStreamHead[0] + length > m_DataStream.Length)
                throw new ArgumentException("Can't pin allocation past the end of data stream");
#endif
            var result = m_DataStreamHead[0];
            m_DataStreamHead[0] = result + length;

            return result;
        }

        /// <summary>
        /// Returns true if the [dataPtr..dataPtr + dataLength) is inside the data stream.
        /// Also, if true, returns the slice to the dataPtr in 'local' coordinate of the dataStream.
        /// </summary>
        /// <param name="dataPtr">Start of the data</param>
        /// <param name="dataLength">Length of the data</param>
        /// <param name="sliceOffset">If true - offset since the data stream head. If false - 0</param>
        /// <returns>Returns true if the [dataPtr..dataPtr + dataLength) is inside the data stream.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe bool IsPointerInsideDataStream(byte* dataPtr, int dataLength, out int sliceOffset)
        {
            sliceOffset = 0;
            var streamBasePtr = (byte*)m_DataStream.GetUnsafePtr();
            var isInside = dataPtr >= streamBasePtr && dataPtr + dataLength <= streamBasePtr + m_DataStreamHead[0];
            if (isInside)
            {
                sliceOffset = (int)(dataPtr - streamBasePtr);
            }
            return isInside;
        }

        /// <summary>
        /// Allocates temporary memory in <see cref="NetworkDriver"/>'s data stream. You don't need to deallocate it
        /// If you need to call this function several times - use <see cref="PinMemoryTillUpdate"/> to move 'head'
        /// </summary>
        /// <param name="dataLen">Size of memory to allocate in bytes. Must be > 0</param>
        /// <returns>Pointer to allocated memory or IntPtr.Zero if there is no space left (this function doesn't set <see cref="ReceiveErrorCode"/>! caller should decide if this is Out of memory or something else)</returns>
        internal IntPtr AllocateMemory(ref int dataLen)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (dataLen <= 0)
                throw new ArgumentException("Can't allocate 0 bytes or less");
#endif

            var stream = m_DataStream;
            var dataStreamHead = m_DataStreamHead[0];
            if (m_NetworkParams.dataStream.size == 0) // if the data stream has dynamic size
            {
                stream.ResizeUninitializedTillPowerOf2(dataStreamHead + dataLen);
            }
            else if (dataStreamHead + dataLen > stream.Length)
            {
                dataLen = stream.Length - dataStreamHead;
                if (dataLen <= 0)
                {
                    dataLen = 0;
                    return IntPtr.Zero;
                }
            }

            unsafe
            {
                return new IntPtr((byte*)stream.GetUnsafePtr() + dataStreamHead);
            }
        }
    }
}
