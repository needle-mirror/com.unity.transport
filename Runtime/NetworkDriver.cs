#if UNITY_2020_1_OR_NEWER
#define UNITY_TRANSPORT_ENABLE_BASELIB
#endif
using System;
using System.Diagnostics;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Networking.Transport.Protocols;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using UnityEngine.Assertions;
using Random = System.Random;

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
    /// var driver = new NetworkDriver.Create();
    /// </code>
    /// </summary>
    /// </typeparam>
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
                m_EventQueue = m_EventQueue.ToConcurrent(),
                m_ConnectionList = m_ConnectionList,
                m_DataStream = m_DataStream,
                m_PipelineProcessor = m_PipelineProcessor.ToConcurrent(),
                m_DefaultHeaderFlags = m_DefaultHeaderFlags,
                m_Logger = m_Logger.ToConcurrent(),
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
                m_EventQueue = default,
                m_ConnectionList = m_ConnectionList,
                m_DataStream = m_DataStream,
                m_PipelineProcessor = m_PipelineProcessor.ToConcurrent(),
                m_DefaultHeaderFlags = m_DefaultHeaderFlags,
                m_Logger = m_Logger.ToConcurrent(),
                m_ConcurrentParallelSendQueue = m_ParallelSendQueue.AsParallelWriter(),
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                m_ThreadIndex = 0,
                m_PendingBeginSend = m_PendingBeginSend
#endif
            };
        }

        /// <summary>
        /// The Concurrent struct is used to create an Concurrent instance of the GenericNetworkDriver.
        /// </summary>
        public struct Concurrent
        {
            public NetworkLogger.Concurrent m_Logger;
            public NetworkEvent.Type PopEventForConnection(NetworkConnection connectionId, out DataStreamReader reader)
            {
                int offset, size;
                reader = default(DataStreamReader);
                if (connectionId.m_NetworkId < 0 || connectionId.m_NetworkId >= m_ConnectionList.Length ||
                    m_ConnectionList[connectionId.m_NetworkId].Version != connectionId.m_NetworkVersion)
                    return (int) NetworkEvent.Type.Empty;
                var type = m_EventQueue.PopEventForConnection(connectionId.m_NetworkId, out offset, out size);
                if (size > 0)
                    reader = new DataStreamReader(((NativeArray<byte>)m_DataStream).GetSubArray(offset, size));
                return type;
            }

            struct PendingSend
            {
                public NetworkPipeline Pipeline;
                public NetworkConnection Connection;
                public NetworkInterfaceSendHandle SendHandle;
                public int headerSize;
            }
            public DataStreamWriter BeginSend(NetworkConnection id, int requiredPayloadSize = 0)
            {
                return BeginSend(NetworkPipeline.Null, id, requiredPayloadSize);
            }
            public unsafe DataStreamWriter BeginSend(NetworkPipeline pipe, NetworkConnection id, int requiredPayloadSize = 0)
            {
                if (id.m_NetworkId < 0 || id.m_NetworkId >= m_ConnectionList.Length)
                    return default;
                var connection = m_ConnectionList[id.m_NetworkId];
                if (connection.Version != id.m_NetworkVersion)
                    return default;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (connection.State == NetworkConnection.State.Connecting)
                    throw new InvalidOperationException("Cannot send data while connecting");
#endif

                var flags = m_DefaultHeaderFlags;
                var headerSize = UnsafeUtility.SizeOf<UdpCHeader>();
                if (pipe.Id > 0)
                {
                    flags |= UdpCHeader.HeaderFlags.HasPipeline;
                    // All headers plus one byte for pipeline id
                    headerSize += m_PipelineProcessor.SendHeaderCapacity(pipe) + 1;
                }
                var footerSize = 0;
                if (connection.DidReceiveData == 0)
                {
                    flags |= UdpCHeader.HeaderFlags.HasConnectToken;
                    footerSize = 2;
                }

                // Make sure there is space for headers and footers too
                if (requiredPayloadSize == 0)
                    requiredPayloadSize = NetworkParameterConstants.MTU;
                else
                {
                    requiredPayloadSize += headerSize + footerSize;
                    if (requiredPayloadSize > NetworkParameterConstants.MTU)
                        throw new InvalidOperationException("It is not possible to send packages larger than the MTU");
                }


                NetworkInterfaceSendHandle sendHandle;
                if (m_NetworkSendInterface.BeginSendMessage.Ptr.Invoke(out sendHandle, m_NetworkSendInterface.UserData, requiredPayloadSize) != 0)
                    return default;
                var header = (UdpCHeader*) sendHandle.data;
                *header = new UdpCHeader
                {
                    Type = (byte) UdpCProtocol.Data,
                    SessionToken = connection.SendToken,
                    Flags = flags
                };

                if (headerSize + footerSize >= sendHandle.capacity)
                    throw new InvalidOperationException("Package could not fit any data");

                var slice = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<byte>((byte*) sendHandle.data+headerSize, sendHandle.capacity - headerSize - footerSize, Allocator.Invalid);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                var safety = AtomicSafetyHandle.GetTempMemoryHandle();
                NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref slice, safety);
#endif
                var writer = new DataStreamWriter(slice);
                writer.m_SendHandleData = (IntPtr)UnsafeUtility.Malloc(UnsafeUtility.SizeOf<PendingSend>(), UnsafeUtility.AlignOf<PendingSend>(), Allocator.Temp);
                *(PendingSend*)writer.m_SendHandleData = new PendingSend
                {
                    Pipeline = pipe,
                    Connection = id,
                    SendHandle = sendHandle,
                    headerSize = headerSize,
                };
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                m_PendingBeginSend[m_ThreadIndex * JobsUtility.CacheLineSize/4] = m_PendingBeginSend[m_ThreadIndex * JobsUtility.CacheLineSize/4] + 1;
#endif
                return writer;
            }
            public unsafe int EndSend(DataStreamWriter writer)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                // Just here to trigger a safety check on the writer
                if (writer.Capacity == 0)
                    throw new InvalidOperationException("EndSend without matching BeginSend");
#endif
                PendingSend* pendingSendPtr = (PendingSend*)writer.m_SendHandleData;
                if (pendingSendPtr == null || pendingSendPtr->Connection == default)
                    throw new InvalidOperationException("EndSend without matching BeginSend");
                if (m_ConnectionList[pendingSendPtr->Connection.m_NetworkId].Version != pendingSendPtr->Connection.m_NetworkVersion)
                    throw new InvalidOperationException("Connection closed between begin and end send");

                PendingSend pendingSend = *(PendingSend*)writer.m_SendHandleData;
                pendingSendPtr->Connection = default;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                m_PendingBeginSend[m_ThreadIndex * JobsUtility.CacheLineSize/4] = m_PendingBeginSend[m_ThreadIndex * JobsUtility.CacheLineSize/4] - 1;
#endif

                pendingSend.SendHandle.size = pendingSend.headerSize + writer.Length;
                int retval = 0;
                if (pendingSend.Pipeline.Id > 0)
                {
                    var oldHeaderFlags = m_DefaultHeaderFlags;
                    m_DefaultHeaderFlags = UdpCHeader.HeaderFlags.HasPipeline;
                    retval = m_PipelineProcessor.Send(this, pendingSend.Pipeline, pendingSend.Connection, pendingSend.SendHandle, pendingSend.headerSize);
                    m_DefaultHeaderFlags = oldHeaderFlags;
                }
                else
                    retval = CompleteSend(pendingSend.Connection, pendingSend.SendHandle);
                if (retval <= 0)
                    return retval;
                return writer.Length;
            }
            public unsafe void AbortSend(DataStreamWriter writer)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                // Just here to trigger a safety check on the writer
                if (writer.Capacity == 0)
                    throw new InvalidOperationException("EndSend without matching BeginSend");
#endif
                PendingSend* pendingSendPtr = (PendingSend*)writer.m_SendHandleData;
                if (pendingSendPtr == null || pendingSendPtr->Connection == default)
                    throw new InvalidOperationException("EndSend without matching BeginSend");
                PendingSend pendingSend = *(PendingSend*)writer.m_SendHandleData;
                pendingSendPtr->Connection = default;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                m_PendingBeginSend[m_ThreadIndex * JobsUtility.CacheLineSize/4] = m_PendingBeginSend[m_ThreadIndex * JobsUtility.CacheLineSize/4] - 1;
#endif
                AbortSend(pendingSend.SendHandle);
            }
            internal unsafe int CompleteSend(NetworkConnection sendConnection, NetworkInterfaceSendHandle sendHandle)
            {
                var connection = m_ConnectionList[sendConnection.m_NetworkId];
                if ((((UdpCHeader*)sendHandle.data)->Flags & UdpCHeader.HeaderFlags.HasConnectToken) != 0)
                {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    if (sendHandle.size + 2 > sendHandle.capacity)
                        throw new InvalidOperationException("SendHandle capacity overflow");
#endif
                    UnsafeUtility.MemCpy((byte*)sendHandle.data + sendHandle.size, &connection.ReceiveToken, 2);
                    sendHandle.size += 2;
                }
                var queueHandle = NetworkSendQueueHandle.ToTempHandle(m_ConcurrentParallelSendQueue);
                return m_NetworkSendInterface.EndSendMessage.Ptr.Invoke(ref sendHandle, ref connection.Address, m_NetworkSendInterface.UserData, ref queueHandle);
            }
            internal void AbortSend(NetworkInterfaceSendHandle sendHandle)
            {
                m_NetworkSendInterface.AbortSendMessage.Ptr.Invoke(ref sendHandle, m_NetworkSendInterface.UserData);
            }
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
            internal NetworkEventQueue.Concurrent m_EventQueue;
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
            public long LastAttempt;
            public int Id;
            public int Version;
            public int Attempts;
            public NetworkConnection.State State;
            public ushort ReceiveToken;
            public ushort SendToken;
            public byte DidReceiveData;

            public static bool operator ==(Connection lhs, Connection rhs)
            {
                return lhs.Id == rhs.Id && lhs.Version == rhs.Version && lhs.Address == rhs.Address;
            }

            public static bool operator !=(Connection lhs, Connection rhs)
            {
                return lhs.Id != rhs.Id || lhs.Version != rhs.Version || lhs.Address != rhs.Address;
            }

            public override bool Equals(object compare)
            {
                return this == (Connection) compare;
            }

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
        int m_NetworkInterfaceIndex;
        NetworkSendInterface m_NetworkSendInterface;
        NativeQueue<QueuedSendMessage> m_ParallelSendQueue;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
        NativeArray<int> m_PendingBeginSend;
#endif

        NetworkEventQueue m_EventQueue;

        NetworkLogger m_Logger;

        NativeQueue<int> m_FreeList;
        NativeQueue<int> m_NetworkAcceptQueue;
        NativeList<Connection> m_ConnectionList;
        NativeArray<int> m_InternalState;
        NativeQueue<int> m_PendingFree;
        NativeArray<ushort> m_SessionIdCounter;
        NativeArray<int> m_ErrorCodes;
        enum ErrorCodeType
        {
            ReceiveError = 0,
            NumErrorCodes
        }

#pragma warning disable 649
        struct Parameters
        {
            public NetworkDataStreamParameter dataStream;
            public NetworkConfigParameter config;

            public Parameters(params INetworkParameter[] param)
            {
                config = new NetworkConfigParameter {
                    maxConnectAttempts = NetworkParameterConstants.MaxConnectAttempts,
                    connectTimeoutMS = NetworkParameterConstants.ConnectTimeoutMS,
                    disconnectTimeoutMS = NetworkParameterConstants.DisconnectTimeoutMS,
                    maxFrameTimeMS = 0
                };
                dataStream = default(NetworkDataStreamParameter);

                for (int i = 0; i < param.Length; ++i)
                {
                    if (param[i] is NetworkConfigParameter)
                        config = (NetworkConfigParameter)param[i];
                    else if (param[i] is NetworkDataStreamParameter)
                        dataStream = (NetworkDataStreamParameter)param[i];
                }
            }
        }
#pragma warning restore 649

        private Parameters m_NetworkParams;
        private NativeList<byte> m_DataStream;
        private NativeArray<int> m_DataStreamSize;
        private NetworkPipelineProcessor m_PipelineProcessor;
        private UdpCHeader.HeaderFlags m_DefaultHeaderFlags;

        private long m_updateTime;
        private long m_updateTimeAdjustment;

        // properties :::::::::::::::::::::::::::::::::::::::::::::::::::::::::
        private const int InternalStateListening = 0;
        private const int InternalStateBound = 1;

        public bool Listening
        {
            get { return (m_InternalState[InternalStateListening] != 0); }
            internal set { m_InternalState[InternalStateListening] = value ? 1 : 0; }
        }

        enum StringType
        {
            ReceiveError,
            ResetErrorCount,
            ResetErrorConnection,
            ResetErrorListening,
            PipelineOverflow,
            ConnectionRequestSendError,
            ConnectionAcceptSendError,
            DisconnectSendError,
            ConnectionRequestWithPipeline,
            ConnectionAcceptWithPipeline,
            DisconnectWithPipeline,
            AcceptWithoutToken,
            ImplicitAcceptWithoutToken,
            MissingEndSend,
            NumStrings
        }

        private static readonly string[] StringValue =
        {
            "Error on receive ",
            "Resetting event queue with pending events (Count=",
            ", ConnectionID=",
            ") Listening: ",
            "A lot of pipeline updates have been queued, possibly too many being scheduled in pipeline logic, queue count: ",
            "Failed to send a ConnectionRequest package",
            "Failed to send a ConnectionAccept package",
            "Failed to send a Disconnect package",
            "Received an invalid ConnectionRequest with pipeline",
            "Received an invalid ConnectionAccept with pipeline",
            "Received an invalid Disconnect with pipeline",
            "Received an invalid ConnectionAccept without a token",
            "Received an invalid implicit connection accept without a token",
            "Missing EndSend, calling BeginSend without calling EndSend will result in a memory leak",
        };

        [ReadOnly] private NativeArray<NativeString512> m_StringDB;

        public static NetworkDriver Create(params INetworkParameter[] param)
        {
#if UNITY_TRANSPORT_ENABLE_BASELIB
            return new NetworkDriver(new BaselibNetworkInterface(), param);
#else
            return new NetworkDriver(new UDPNetworkInterface(), param);
#endif
        }
        /// <summary>
        /// Constructor for GenericNetworkDriver.
        /// </summary>
        /// <param name="param">
        /// An array of INetworkParameter. There are currently only two <see cref="INetworkParameter"/>,
        /// the <see cref="NetworkDataStreamParameter"/> and the <see cref="NetworkConfigParameter"/>.
        /// </param>
        public NetworkDriver(INetworkInterface netIf, params INetworkParameter[] param)
        {
            m_NetworkInterfaceIndex = -1;
            for (int i = 0; i < s_NetworkInterfaces.Count; ++i)
            {
                if (s_NetworkInterfaces[i] == null)
                {
                    m_NetworkInterfaceIndex = i;
                    s_NetworkInterfaces[i] = netIf;
                    break;
                }
            }
            if (m_NetworkInterfaceIndex < 0)
            {
                m_NetworkInterfaceIndex = s_NetworkInterfaces.Count;
                s_NetworkInterfaces.Add(netIf);
            }
            netIf.Initialize(param);
            m_NetworkSendInterface = netIf.CreateSendInterface();
            m_ParallelSendQueue = new NativeQueue<QueuedSendMessage>(Allocator.Persistent);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            m_PendingBeginSend = new NativeArray<int>(JobsUtility.MaxJobThreadCount * JobsUtility.CacheLineSize/4, Allocator.Persistent);
#endif
            m_Logger = new NetworkLogger(NetworkLogger.LogLevel.Debug);
            m_StringDB = new NativeArray<NativeString512>((int)StringType.NumStrings, Allocator.Persistent);
            if (StringValue.Length != (int)StringType.NumStrings)
                throw new InvalidOperationException("Bad string database");
            for (int i = 0; i < (int) StringType.NumStrings; ++i)
            {
                m_StringDB[i] = new NativeString512(StringValue[i]);
            }
            m_NetworkParams = new Parameters(param);
            m_updateTime = m_NetworkParams.config.fixedFrameTimeMS > 0 ? 1 : Stopwatch.GetTimestamp() / TimeSpan.TicksPerMillisecond;
            m_updateTimeAdjustment = 0;

            int initialStreamSize = m_NetworkParams.dataStream.size;
            if (initialStreamSize == 0)
                initialStreamSize = NetworkParameterConstants.DriverDataStreamSize;

            m_DataStream = new NativeList<byte>(initialStreamSize, Allocator.Persistent);
            m_DataStream.ResizeUninitialized(initialStreamSize);
            m_DataStreamSize = new NativeArray<int>(1, Allocator.Persistent);
            m_PipelineProcessor = new NetworkPipelineProcessor(param); // Initial capacity might need to be bigger than 0
            m_DefaultHeaderFlags = 0;

            m_NetworkAcceptQueue = new NativeQueue<int>(Allocator.Persistent);

            m_ConnectionList = new NativeList<Connection>(1, Allocator.Persistent);

            m_FreeList = new NativeQueue<int>(Allocator.Persistent);
            m_EventQueue = new NetworkEventQueue(NetworkParameterConstants.InitialEventQueueSize);

            m_InternalState = new NativeArray<int>(2, Allocator.Persistent);
            m_PendingFree = new NativeQueue<int>(Allocator.Persistent);

            m_ReceiveCount = new NativeArray<int>(1, Allocator.Persistent);
            m_SessionIdCounter = new NativeArray<ushort>(1, Allocator.Persistent);
            m_SessionIdCounter[0] = (ushort)(new Random().Next() & 0xFFFF);
            m_ErrorCodes = new NativeArray<int>((int)ErrorCodeType.NumErrorCodes, Allocator.Persistent);
            ReceiveCount = 0;
            Listening = false;
        }

        // interface implementation :::::::::::::::::::::::::::::::::::::::::::
        public void Dispose()
        {
            s_NetworkInterfaces[m_NetworkInterfaceIndex].Dispose();
            s_NetworkInterfaces[m_NetworkInterfaceIndex] = null;
            m_Logger.Dispose();
            m_StringDB.Dispose();
            m_DataStream.Dispose();
            m_DataStreamSize.Dispose();
            m_PipelineProcessor.Dispose();

            m_EventQueue.Dispose();

            m_NetworkAcceptQueue.Dispose();
            m_ConnectionList.Dispose();
            m_FreeList.Dispose();
            m_InternalState.Dispose();
            m_PendingFree.Dispose();
            m_ReceiveCount.Dispose();
            m_SessionIdCounter.Dispose();
            m_ErrorCodes.Dispose();
            m_ParallelSendQueue.Dispose();
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            m_PendingBeginSend.Dispose();
#endif
        }

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
        struct LogJob : IJob
        {
            public NetworkLogger logger;

            public void Execute()
            {
                logger.FlushPending();
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                logger.DumpToConsole();
#else
                logger.Clear();
#endif
            }
        }

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        struct MissingClearMessage : INetworkLogMessage
        {
            public NativeArray<NativeString512> stringDB;
            public int count;
            public int connection;
            public int listening;
            public void Print(ref NativeString512 msg)
            {
                var str = stringDB[(int) StringType.ResetErrorCount];
                msg.AppendFrom(str);
                msg.Append(count);
                str = stringDB[(int) StringType.ResetErrorConnection];
                msg.AppendFrom(str);
                msg.Append(connection);
                str = stringDB[(int) StringType.ResetErrorListening];
                msg.AppendFrom(str);
                msg.Append(listening);
            }
        }
#endif
        struct ClearEventQueue : IJob
        {
            public NativeList<byte> dataStream;
            public NativeArray<int> dataStreamSize;
            public NetworkEventQueue eventQueue;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            public NetworkLogger logger;
            public NativeArray<NativeString512> stringDB;
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
                        var msg = new MissingClearMessage { stringDB = stringDB, count = conCount, connection = i, listening = internalState[InternalStateListening]};
                        logger.Log(NetworkLogger.LogLevel.Error, msg);
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
                            logger.Log(NetworkLogger.LogLevel.Error, stringDB[(int)StringType.MissingEndSend]);
                            didPrint = true;
                        }
                    }
                }
#endif
                eventQueue.Clear();
                dataStream.ResizeUninitialized(dataStream.Length);
                dataStreamSize[0] = 0;
            }
        }

        public long LastUpdateTime => m_updateTime;
        public JobHandle ScheduleUpdate(JobHandle dep = default(JobHandle))
        {
            long timeNow = m_NetworkParams.config.fixedFrameTimeMS > 0 ? m_updateTime + m_NetworkParams.config.fixedFrameTimeMS :
                Stopwatch.GetTimestamp() / TimeSpan.TicksPerMillisecond - m_updateTimeAdjustment;
            if (m_NetworkParams.config.maxFrameTimeMS > 0 && (timeNow - m_updateTime) > m_NetworkParams.config.maxFrameTimeMS)
            {
                m_updateTimeAdjustment += (timeNow - m_updateTime) - m_NetworkParams.config.maxFrameTimeMS;
                timeNow = m_updateTime + m_NetworkParams.config.maxFrameTimeMS;
            }
            m_updateTime = timeNow;
            var job = new UpdateJob {driver = this};
            JobHandle handle;
            var clearJob = new ClearEventQueue
            {
                dataStream = m_DataStream,
                dataStreamSize = m_DataStreamSize,
                eventQueue = m_EventQueue,
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                logger = m_Logger,
                stringDB = m_StringDB,
                pendingSend = m_PendingBeginSend,
                connectionList = m_ConnectionList,
                internalState = m_InternalState
#endif
            };
            handle = clearJob.Schedule(dep);
            handle = job.Schedule(handle);
            handle = s_NetworkInterfaces[m_NetworkInterfaceIndex].ScheduleReceive(new NetworkPacketReceiver{m_Driver = this}, handle);
            handle = s_NetworkInterfaces[m_NetworkInterfaceIndex].ScheduleSend(m_ParallelSendQueue, handle);
            var logJob = new LogJob {logger = m_Logger};
            handle = logJob.Schedule(handle);
            return handle;
        }
        public JobHandle ScheduleFlushSend(JobHandle dep)
        {
            return s_NetworkInterfaces[m_NetworkInterfaceIndex].ScheduleSend(m_ParallelSendQueue, dep);
        }

        struct PipelineOverflowMessage : INetworkLogMessage
        {
            public NativeArray<NativeString512> stringDB;
            public int count;
            public void Print(ref NativeString512 msg)
            {
                var str = stringDB[(int)StringType.PipelineOverflow];
                msg.AppendFrom(str);
                msg.Append(count);
            }
        }
        void InternalUpdate()
        {
            m_PipelineProcessor.Timestamp = m_updateTime;
            int free;
            while (m_PendingFree.TryDequeue(out free))
            {
                int ver = m_ConnectionList[free].Version + 1;
                if (ver == 0)
                    ver = 1;
                m_ConnectionList[free] = new Connection {Id = free, Version = ver};
                m_FreeList.Enqueue(free);
            }

            CheckTimeouts();

            int updateCount;
            m_PipelineProcessor.UpdateReceive(this, out updateCount);

            // TODO: Find a good way to establish a good limit (connections*pipelines/2?)
            if (updateCount > (m_ConnectionList.Length - m_FreeList.Count) * 64)
            {
                var msg = new PipelineOverflowMessage { stringDB = m_StringDB, count = updateCount };
                m_Logger.Log(NetworkLogger.LogLevel.Warning, msg);
            }

            m_DefaultHeaderFlags = UdpCHeader.HeaderFlags.HasPipeline;
            m_PipelineProcessor.UpdateSend(ToConcurrentSendOnly(), out updateCount);
            if (updateCount > (m_ConnectionList.Length - m_FreeList.Count) * 64)
            {
                var msg = new PipelineOverflowMessage { stringDB = m_StringDB, count = updateCount };
                m_Logger.Log(NetworkLogger.LogLevel.Warning, msg);
            }

            m_DefaultHeaderFlags = 0;
        }

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

        public int Bind(NetworkEndPoint endpoint)
        {
            var ifEndPoint = s_NetworkInterfaces[m_NetworkInterfaceIndex].CreateInterfaceEndPoint(endpoint);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (!m_InternalState.IsCreated)
                throw new InvalidOperationException(
                    "Driver must be constructed with a populated or empty INetworkParameter params list");
            if (m_InternalState[InternalStateBound] != 0)
                throw new InvalidOperationException(
                    "Bind can only be called once per NetworkDriver");
            if (m_ConnectionList.Length > 0)
                throw new InvalidOperationException(
                    "Bind cannot be called after establishing connections");
#endif
            var ret = s_NetworkInterfaces[m_NetworkInterfaceIndex].Bind(ifEndPoint);
            if (ret == 0)
                m_InternalState[InternalStateBound] = 1;
            return ret;
        }

        public int Listen()
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (!m_InternalState.IsCreated)
                throw new InvalidOperationException(
                    "Driver must be constructed with a populated or empty INetworkParameter params list");
            if (Listening)
                throw new InvalidOperationException(
                    "Listen can only be called once per NetworkDriver");
            if (m_InternalState[InternalStateBound] == 0)
                throw new InvalidOperationException(
                    "Listen can only be called after a successful call to Bind");
#endif
            if (m_InternalState[InternalStateBound] == 0)
                return -1;
            Listening = true;
            return 0;
        }

        public NetworkConnection Accept()
        {
            if (!Listening)
                return default(NetworkConnection);

            int id;
            if (!m_NetworkAcceptQueue.TryDequeue(out id))
                return default(NetworkConnection);
            return new NetworkConnection {m_NetworkId = id, m_NetworkVersion = m_ConnectionList[id].Version};
        }

        public NetworkConnection Connect(NetworkEndPoint endpoint)
        {
            var ifEndPoint = s_NetworkInterfaces[m_NetworkInterfaceIndex].CreateInterfaceEndPoint(endpoint);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (!m_InternalState.IsCreated)
                throw new InvalidOperationException(
                    "Driver must be constructed with a populated or empty INetworkParameter params list");
#endif
            int id;
            if (!m_FreeList.TryDequeue(out id))
            {
                id = m_ConnectionList.Length;
                m_ConnectionList.Add(new Connection{Id = id, Version = 1});
            }

            int ver = m_ConnectionList[id].Version;
            var c = new Connection
            {
                Id = id,
                Version = ver,
                State = NetworkConnection.State.Connecting,
                Address = ifEndPoint,
                Attempts = 1,
                LastAttempt = m_updateTime,
                SendToken = 0,
                ReceiveToken = m_SessionIdCounter[0]
            };
            m_SessionIdCounter[0] = (ushort)(m_SessionIdCounter[0] + 1);

            m_ConnectionList[id] = c;
            var netcon = new NetworkConnection {m_NetworkId = id, m_NetworkVersion = ver};
            SendConnectionRequest(c);
            m_PipelineProcessor.initializeConnection(netcon);

            return netcon;
        }

        void SendConnectionRequest(Connection c)
        {
            unsafe
            {
                NetworkInterfaceSendHandle sendHandle;
                if (m_NetworkSendInterface.BeginSendMessage.Ptr.Invoke(out sendHandle, m_NetworkSendInterface.UserData, UdpCHeader.Length) != 0)
                {
                    m_Logger.Log(NetworkLogger.LogLevel.Error, m_StringDB[(int) StringType.ConnectionRequestSendError]);
                    return;
                }

                byte* packet = (byte*) sendHandle.data;
                sendHandle.size = UdpCHeader.Length;
                if (sendHandle.size > sendHandle.capacity)
                {
                    m_Logger.Log(NetworkLogger.LogLevel.Error, m_StringDB[(int) StringType.ConnectionRequestSendError]);
                    return;
                }
                var header = (UdpCHeader*) packet;
                *header = new UdpCHeader
                {
                    Type = (byte) UdpCProtocol.ConnectionRequest,
                    SessionToken = c.ReceiveToken,
                    Flags = m_DefaultHeaderFlags
                };
                var queueHandle = NetworkSendQueueHandle.ToTempHandle(m_ParallelSendQueue.AsParallelWriter());
                m_NetworkSendInterface.EndSendMessage.Ptr.Invoke(ref sendHandle, ref c.Address, m_NetworkSendInterface.UserData, ref queueHandle);
            }
        }

        public int Disconnect(NetworkConnection id)
        {
            Connection connection;
            if ((connection = GetConnection(id)) == Connection.Null)
                return 0;

            if (connection.State == NetworkConnection.State.Connected)
            {
                if (SendPacket(UdpCProtocol.Disconnect, id) <= 0)
                {
                    m_Logger.Log(NetworkLogger.LogLevel.Error, m_StringDB[(int)StringType.DisconnectSendError]);
                }
            }
            RemoveConnection(connection);

            return 0;
        }

        public void GetPipelineBuffers(NetworkPipeline pipeline, NetworkPipelineStageId stageId, NetworkConnection connection, out NativeArray<byte> readProcessingBuffer, out NativeArray<byte> writeProcessingBuffer, out NativeArray<byte> sharedBuffer)
        {
            if (connection.m_NetworkId < 0 || connection.m_NetworkId >= m_ConnectionList.Length ||
                 m_ConnectionList[connection.m_NetworkId].Version != connection.m_NetworkVersion)
                throw new InvalidOperationException("Invalid connection");
            m_PipelineProcessor.GetPipelineBuffers(pipeline, stageId, connection, out readProcessingBuffer, out writeProcessingBuffer, out sharedBuffer);
        }

        public NetworkConnection.State GetConnectionState(NetworkConnection con)
        {
            Connection connection;
            if ((connection = GetConnection(con)) == Connection.Null)
                return NetworkConnection.State.Disconnected;
            return connection.State;
        }

        public NetworkEndPoint RemoteEndPoint(NetworkConnection id)
        {
            if (id == default(NetworkConnection))
                return default(NetworkEndPoint);

            Connection connection;
            if ((connection = GetConnection(id)) == Connection.Null)
                return default(NetworkEndPoint);
            return s_NetworkInterfaces[m_NetworkInterfaceIndex].GetGenericEndPoint(connection.Address);
        }

        public NetworkEndPoint LocalEndPoint()
        {
            var ep = s_NetworkInterfaces[m_NetworkInterfaceIndex].LocalEndPoint;
            return s_NetworkInterfaces[m_NetworkInterfaceIndex].GetGenericEndPoint(ep);
        }

        public DataStreamWriter BeginSend(NetworkPipeline pipe, NetworkConnection id, int requiredPayloadSize = 0)
        {
            return ToConcurrentSendOnly().BeginSend(pipe, id, requiredPayloadSize);
        }
        public DataStreamWriter BeginSend(NetworkConnection id, int requiredPayloadSize = 0)
        {
            return ToConcurrentSendOnly().BeginSend(NetworkPipeline.Null, id, requiredPayloadSize);
        }
        public int EndSend(DataStreamWriter writer)
        {
            return ToConcurrentSendOnly().EndSend(writer);
        }
        public void AbortSend(DataStreamWriter writer)
        {
            ToConcurrentSendOnly().AbortSend(writer);
        }

        public NetworkEvent.Type PopEvent(out NetworkConnection con, out DataStreamReader reader)
        {
            int offset, size;
            reader = default(DataStreamReader);
            int id;
            var type = m_EventQueue.PopEvent(out id, out offset, out size);
            if (size > 0)
                reader = new DataStreamReader(((NativeArray<byte>)m_DataStream).GetSubArray(offset, size));
            con = id < 0
                ? default(NetworkConnection)
                : new NetworkConnection {m_NetworkId = id, m_NetworkVersion = m_ConnectionList[id].Version};
            return type;
        }

        public NetworkEvent.Type PopEventForConnection(NetworkConnection connectionId, out DataStreamReader reader)
        {
            int offset, size;
            reader = default(DataStreamReader);
            if (connectionId.m_NetworkId < 0 || connectionId.m_NetworkId >= m_ConnectionList.Length ||
                 m_ConnectionList[connectionId.m_NetworkId].Version != connectionId.m_NetworkVersion)
                return (int) NetworkEvent.Type.Empty;
            var type = m_EventQueue.PopEventForConnection(connectionId.m_NetworkId, out offset, out size);
            if (size > 0)
                reader = new DataStreamReader(((NativeArray<byte>)m_DataStream).GetSubArray(offset, size));
            return type;
        }

        // internal helper functions ::::::::::::::::::::::::::::::::::::::::::
        void AddConnection(int id)
        {
            m_EventQueue.PushEvent(new NetworkEvent {connectionId = id, type = NetworkEvent.Type.Connect});
        }

        void AddDisconnection(int id)
        {
            m_EventQueue.PushEvent(new NetworkEvent {connectionId = id, type = NetworkEvent.Type.Disconnect});
        }

        Connection GetConnection(NetworkConnection id)
        {
            var con = m_ConnectionList[id.m_NetworkId];
            if (con.Version != id.m_NetworkVersion)
                return Connection.Null;
            return con;
        }

        Connection GetConnection(NetworkInterfaceEndPoint address, ushort sessionId)
        {
            for (int i = 0; i < m_ConnectionList.Length; i++)
            {
                if (address == m_ConnectionList[i].Address && m_ConnectionList[i].ReceiveToken == sessionId )
                    return m_ConnectionList[i];
            }

            return Connection.Null;
        }

        Connection GetNewConnection(NetworkInterfaceEndPoint address, ushort sessionId)
        {
            for (int i = 0; i < m_ConnectionList.Length; i++)
            {
                if (address == m_ConnectionList[i].Address && m_ConnectionList[i].SendToken == sessionId )
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

        bool UpdateConnection(Connection connection)
        {
            if (connection == m_ConnectionList[connection.Id])
            {
                SetConnection(connection);
                return true;
            }

            return false;
        }

        unsafe int SendPacket(UdpCProtocol type, Connection connection)
        {
            NetworkInterfaceSendHandle sendHandle;
            if (m_NetworkSendInterface.BeginSendMessage.Ptr.Invoke(out sendHandle, m_NetworkSendInterface.UserData, UdpCHeader.Length + 2) != 0)
                return -1;
            byte* packet = (byte*) sendHandle.data;
            sendHandle.size = UdpCHeader.Length;
            if (sendHandle.size > sendHandle.capacity)
                return -1;
            var header = (UdpCHeader*) packet;
            *header = new UdpCHeader
            {
                Type = (byte) type,
                SessionToken = connection.SendToken,
                Flags = m_DefaultHeaderFlags
            };
            if (connection.DidReceiveData == 0)
            {
                header->Flags |= UdpCHeader.HeaderFlags.HasConnectToken;
                packet += sendHandle.size;
                sendHandle.size += 2;
                if (sendHandle.size > sendHandle.capacity)
                    return -1;
                UnsafeUtility.MemCpy(packet, &connection.ReceiveToken, 2);
            }
            var queueHandle = NetworkSendQueueHandle.ToTempHandle(m_ParallelSendQueue.AsParallelWriter());
            return m_NetworkSendInterface.EndSendMessage.Ptr.Invoke(ref sendHandle, ref connection.Address, m_NetworkSendInterface.UserData, ref queueHandle);
        }

        int SendPacket(UdpCProtocol type, NetworkConnection id)
        {
            Connection connection;
            if ((connection = GetConnection(id)) == Connection.Null)
                return 0;

            return SendPacket(type, connection);
        }

        void CheckTimeouts()
        {
            for (int i = 0; i < m_ConnectionList.Length; ++i)
            {
                var connection = m_ConnectionList[i];
                if (connection == Connection.Null)
                    continue;

                long now = m_updateTime;

                var netcon = new NetworkConnection {m_NetworkId = connection.Id, m_NetworkVersion = connection.Version};
                if ((connection.State == NetworkConnection.State.Connecting ||
                     connection.State == NetworkConnection.State.AwaitingResponse) &&
                    now - connection.LastAttempt > m_NetworkParams.config.connectTimeoutMS)
                {
                    if (connection.Attempts >= m_NetworkParams.config.maxConnectAttempts)
                    {
                        RemoveConnection(connection);
                        AddDisconnection(connection.Id);
                        continue;
                    }

                    connection.Attempts = ++connection.Attempts;
                    connection.LastAttempt = now;
                    SetConnection(connection);

                    if (connection.State == NetworkConnection.State.Connecting)
                        SendConnectionRequest(connection);
                    else
                    {
                        if (SendPacket(UdpCProtocol.ConnectionAccept, netcon) <= 0)
                        {
                            m_Logger.Log(NetworkLogger.LogLevel.Error, m_StringDB[(int)StringType.ConnectionAcceptSendError]);
                        }
                    }
                }

                if (connection.State == NetworkConnection.State.Connected &&
                    now - connection.LastAttempt > m_NetworkParams.config.disconnectTimeoutMS)
                {
                    Disconnect(netcon);
                    AddDisconnection(connection.Id);
                }
            }
        }

        struct ReceiveErrorMessage : INetworkLogMessage
        {
            public NativeArray<NativeString512> stringDB;
            public int errorCode;
            public void Print(ref NativeString512 msg)
            {
                var str = stringDB[(int) StringType.ReceiveError];
                msg.AppendFrom(str);
                msg.Append(errorCode);
            }
        }
        public int ReceiveErrorCode
        {
            get { return m_ErrorCodes[(int)ErrorCodeType.ReceiveError]; }
            internal set
            {
                if (value != 0)
                {
                    var msg = new ReceiveErrorMessage {stringDB = m_StringDB, errorCode = value};
                    m_Logger.Log(NetworkLogger.LogLevel.Error, msg);
                }

                m_ErrorCodes[(int)ErrorCodeType.ReceiveError] = value;
            }
        }

        // Interface for receiving packages from a NetworkInterface
        internal NativeList<byte> GetDataStream()
        {
            return m_DataStream;
        }
        internal int GetDataStreamSize()
        {
            return m_DataStreamSize[0];
        }

        private NativeArray<int> m_ReceiveCount;
        internal int ReceiveCount {
            get { return m_ReceiveCount[0]; }
            set { m_ReceiveCount[0] = value; }
        }

        internal bool DynamicDataStreamSize()
        {
            return m_NetworkParams.dataStream.size == 0;
        }

        internal int AppendPacket(NetworkInterfaceEndPoint address, UdpCHeader header, int dataLen)
        {
            int count = 0;
            switch ((UdpCProtocol) header.Type)
            {
                case UdpCProtocol.ConnectionRequest:
                {
                    if (!Listening)
                        return 0;
                    if ((header.Flags&UdpCHeader.HeaderFlags.HasPipeline) != 0)
                    {
                        m_Logger.Log(NetworkLogger.LogLevel.Error, m_StringDB[(int)StringType.ConnectionRequestWithPipeline]);
                        return 0;
                    }

                    Connection c;
                    if ((c = GetNewConnection(address, header.SessionToken)) == Connection.Null || c.State == NetworkConnection.State.Disconnected)
                    {
                        int id;
                        var sessionId = m_SessionIdCounter[0];
                        m_SessionIdCounter[0] = (ushort) (m_SessionIdCounter[0] + 1);
                        if (!m_FreeList.TryDequeue(out id))
                        {
                            id = m_ConnectionList.Length;
                            m_ConnectionList.Add(new Connection{Id = id, Version = 1});
                        }

                        int ver = m_ConnectionList[id].Version;
                        c = new Connection
                        {
                            Id = id,
                            Version = ver,
                            ReceiveToken = sessionId,
                            SendToken = header.SessionToken,
                            State = NetworkConnection.State.Connected,
                            Address = address,
                            Attempts = 1,
                            LastAttempt = m_updateTime
                        };
                        SetConnection(c);
                        m_PipelineProcessor.initializeConnection(new NetworkConnection{m_NetworkId = id, m_NetworkVersion = c.Version});
                        m_NetworkAcceptQueue.Enqueue(id);
                        count++;
                    }
                    else
                    {
                        c.Attempts++;
                        c.LastAttempt = m_updateTime;
                        SetConnection(c);
                    }

                    if (SendPacket(UdpCProtocol.ConnectionAccept,
                            new NetworkConnection {m_NetworkId = c.Id, m_NetworkVersion = c.Version}) <= 0)
                    {
                        m_Logger.Log(NetworkLogger.LogLevel.Error, m_StringDB[(int)StringType.ConnectionAcceptSendError]);
                    }
                }
                    break;
                case UdpCProtocol.ConnectionReject:
                {
                    // m_EventQ.Enqueue(Id, (int)NetworkEvent.Connect);
                }
                    break;
                case UdpCProtocol.ConnectionAccept:
                {
                    if ((header.Flags&UdpCHeader.HeaderFlags.HasConnectToken) == 0)
                    {
                        m_Logger.Log(NetworkLogger.LogLevel.Error, m_StringDB[(int)StringType.AcceptWithoutToken]);
                        return 0;
                    }
                    if ((header.Flags&UdpCHeader.HeaderFlags.HasPipeline) != 0)
                    {
                        m_Logger.Log(NetworkLogger.LogLevel.Error, m_StringDB[(int)StringType.ConnectionAcceptWithPipeline]);
                        return 0;
                    }

                    Connection c = GetConnection(address, header.SessionToken);
                    if (c != Connection.Null)
                    {
                        c.DidReceiveData = 1;

                        if (c.State == NetworkConnection.State.Connected)
                        {
                            //DebugLog("Dropping connect request for an already connected endpoint [" + address + "]");
                            return 0;
                        }

                        if (c.State == NetworkConnection.State.Connecting)
                        {
                            var tokenOffset = m_DataStreamSize[0];
                            var dataStreamReader = new DataStreamReader(((NativeArray<byte>)m_DataStream).GetSubArray(tokenOffset, 2));
                            c.SendToken = dataStreamReader.ReadUShort();

                            c.State = NetworkConnection.State.Connected;
                            UpdateConnection(c);
                            AddConnection(c.Id);
                            count++;
                        }
                    }
                }
                    break;
                case UdpCProtocol.Disconnect:
                {
                    if ((header.Flags&UdpCHeader.HeaderFlags.HasPipeline) != 0)
                    {
                        m_Logger.Log(NetworkLogger.LogLevel.Error, m_StringDB[(int)StringType.DisconnectWithPipeline]);
                        return 0;
                    }
                    Connection c = GetConnection(address, header.SessionToken);
                    if (c != Connection.Null)
                    {
                        if (RemoveConnection(c))
                            AddDisconnection(c.Id);
                        count++;
                    }
                }
                    break;
                case UdpCProtocol.Data:
                {
                    Connection c = GetConnection(address, header.SessionToken);
                    if (c == Connection.Null)
                        return 0;

                    c.DidReceiveData = 1;
                    c.LastAttempt = m_updateTime;
                    UpdateConnection(c);

                    var length = dataLen - UdpCHeader.Length;

                    if (c.State == NetworkConnection.State.Connecting)
                    {
                        if ((header.Flags&UdpCHeader.HeaderFlags.HasConnectToken) == 0)
                        {
                            m_Logger.Log(NetworkLogger.LogLevel.Error, m_StringDB[(int)StringType.ImplicitAcceptWithoutToken]);
                            return 0;
                        }

                        var tokenOffset = m_DataStreamSize[0] + length - 2;
                        var dataStreamReader = new DataStreamReader(((NativeArray<byte>)m_DataStream).GetSubArray(tokenOffset, 2));
                        c.SendToken = dataStreamReader.ReadUShort();

                        c.State = NetworkConnection.State.Connected;
                        UpdateConnection(c);
                        Assert.IsTrue(!Listening);
                        AddConnection(c.Id);
                        count++;
                    }

                    if (c.State == NetworkConnection.State.Connected)
                    {
                        if ((header.Flags & UdpCHeader.HeaderFlags.HasConnectToken) != 0)
                            length -= 2;

                        var sliceOffset = m_DataStreamSize[0];
                        m_DataStreamSize[0] = sliceOffset + length;

                        if ((header.Flags & UdpCHeader.HeaderFlags.HasPipeline) != 0)
                        {
                            var netCon = new NetworkConnection {m_NetworkId = c.Id, m_NetworkVersion = c.Version};
                            m_PipelineProcessor.Receive(this, netCon, ((NativeArray<byte>)m_DataStream).GetSubArray(sliceOffset, length));
                            return 0;
                        }

                        m_EventQueue.PushEvent(new NetworkEvent
                        {
                            connectionId = c.Id,
                            type = NetworkEvent.Type.Data,
                            offset = sliceOffset,
                            size = length
                        });
                        count++;
                    }
                } break;
            }

            return count;
        }

        // Interface for receiving data from a pipeline
        internal unsafe void PushDataEvent(NetworkConnection con, byte* dataPtr, int dataLength)
        {
            byte* streamBasePtr = (byte*)m_DataStream.GetUnsafePtr();
            int sliceOffset = 0;
            if (dataPtr >= streamBasePtr && dataPtr + dataLength <= streamBasePtr + m_DataStreamSize[0])
            {
                // Pointer is a subset of our receive buffer, no need to copy
                sliceOffset = (int)(dataPtr - streamBasePtr);
            }
            else
            {
                if (DynamicDataStreamSize())
                {
                    while (m_DataStreamSize[0] + dataLength >= m_DataStream.Length)
                        m_DataStream.ResizeUninitialized(m_DataStream.Length * 2);
                }
                else if (m_DataStreamSize[0] + dataLength >= m_DataStream.Length)
                    return; // FIXME: how do we signal this error?

                sliceOffset = m_DataStreamSize[0];
                UnsafeUtility.MemCpy(streamBasePtr + sliceOffset, dataPtr, dataLength);
                m_DataStreamSize[0] = sliceOffset + dataLength;
            }

            m_EventQueue.PushEvent(new NetworkEvent
            {
                connectionId = con.m_NetworkId,
                type = NetworkEvent.Type.Data,
                offset = sliceOffset,
                size = dataLength
            });
        }
    }
}
