using System;
using System.Threading;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Networking.Transport.Protocols;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Networking.Transport.Error;
using Unity.Networking.Transport.Logging;
using Unity.Networking.Transport.Utilities;

namespace Unity.Networking.Transport
{
    public unsafe struct QueuedSendMessage
    {
        public fixed byte Data[NetworkParameterConstants.MTU];
        public NetworkEndpoint Dest;
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
    public struct NetworkDriver : IDisposable
    {
        /// <summary>
        /// Create a Concurrent Copy of the NetworkDriver.
        /// </summary>
        public Concurrent ToConcurrent()
        {
            return new Concurrent
            {
                m_EventQueue = m_EventQueue.ToConcurrent(),
                m_ConnectionList = m_NetworkStack.Connections,
                m_DisconnectReasons = m_DisconnectReasons,
                m_PipelineProcessor = m_PipelineProcessor.ToConcurrent(),
                m_DefaultHeaderFlags = m_DefaultHeaderFlags,
                m_DriverSender = m_DriverSender.ToConcurrent(),
                m_DriverReceiver = m_DriverReceiver,
                m_PacketPadding = m_NetworkStack.PacketPadding,
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
                m_EventQueue = default,
                m_ConnectionList = m_NetworkStack.Connections,
                m_DisconnectReasons = m_DisconnectReasons,
                m_PipelineProcessor = m_PipelineProcessor.ToConcurrent(),
                m_DefaultHeaderFlags = m_DefaultHeaderFlags,
                m_DriverSender = m_DriverSender.ToConcurrent(),
                m_DriverReceiver = m_DriverReceiver,
                m_PacketPadding = m_NetworkStack.PacketPadding,
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
            public NetworkEvent.Type PopEventForConnection(NetworkConnection connectionId, out DataStreamReader reader)
            {
                return PopEventForConnection(connectionId, out reader, out var _);
            }

            public NetworkEvent.Type PopEventForConnection(NetworkConnection connection, out DataStreamReader reader, out NetworkPipeline pipeline)
            {
                pipeline = default;

                reader = default;
                if (m_ConnectionList.ConnectionAt(connection.InternalId) != connection.m_ConnectionId)
                    return (int)NetworkEvent.Type.Empty;

                var type = m_EventQueue.PopEventForConnection(connection.InternalId, out var offset, out var size, out var pipelineId);
                pipeline = new NetworkPipeline { Id = pipelineId };

                if (type == NetworkEvent.Type.Disconnect && offset < 0)
                    reader = new DataStreamReader(m_DisconnectReasons.GetSubArray(math.abs(offset), 1));
                else if (size > 0)
                    reader = new DataStreamReader(m_DriverReceiver.GetDataStreamSubArray(offset, size));

                return type;
            }

            public int MaxHeaderSize(NetworkPipeline pipe)
            {
                var headerSize = m_PipelineProcessor.m_MaxPacketHeaderSize;
                if (pipe.Id > 0)
                {
                    // All headers plus one byte for pipeline id
                    headerSize += m_PipelineProcessor.SendHeaderCapacity(pipe) + 1;
                }

                return headerSize;
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

                if (id.InternalId < 0 || id.InternalId >= m_ConnectionList.Count)
                    return (int)Error.StatusCode.NetworkIdMismatch;

                var connection = m_ConnectionList.ConnectionAt(id.InternalId);
                if (connection.Version != id.Version)
                    return (int)Error.StatusCode.NetworkVersionMismatch;

                if (m_ConnectionList.GetConnectionState(connection) != NetworkConnection.State.Connected)
                    return (int)Error.StatusCode.NetworkStateMismatch;

                var pipelineHeader = (pipe.Id > 0) ? m_PipelineProcessor.SendHeaderCapacity(pipe) + 1 : 0;
                var pipelinePayloadCapacity = m_PipelineProcessor.PayloadCapacity(pipe);

                // If the pipeline doesn't have an explicity payload capacity, then use whatever
                // will fit inside the MTU (considering protocol and pipeline overhead). If there is
                // an explicity pipeline payload capacity we use that directly. Right now only
                // fragmented pipelines have an explicity capacity, and we want users to be able to
                // rely on this configured value.
                var payloadCapacity = pipelinePayloadCapacity == 0
                    ? NetworkParameterConstants.MTU - m_PacketPadding - pipelineHeader
                    : pipelinePayloadCapacity;

                // Total capacity is the full size of the buffer we'll allocate. Without an explicit
                // pipeline payload capacity, this is the MTU. Otherwise it's the pipeline payload
                // capacity plus whatever overhead we need to transmit the packet.
                var totalCapacity = pipelinePayloadCapacity == 0
                    ? NetworkParameterConstants.MTU
                    : pipelinePayloadCapacity + m_PacketPadding + pipelineHeader;

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

                var sendHandle = default(NetworkInterfaceSendHandle);
                if (totalCapacity > NetworkParameterConstants.MTU)
                {
                    sendHandle.data = (IntPtr)UnsafeUtility.Malloc(totalCapacity, 8, Allocator.Temp);
                    sendHandle.capacity = totalCapacity;
                    sendHandle.id = 0;
                    sendHandle.size = 0;
                    sendHandle.flags = SendHandleFlags.AllocatedByDriver;
                }
                else
                {
                    var result = 0;
                    if ((result = m_DriverSender.BeginSend(out sendHandle, (uint)totalCapacity)) != 0)
                    {
                        return result;
                    }
                }

                if (sendHandle.capacity < totalCapacity)
                    return (int)Error.StatusCode.NetworkPacketOverflow;

                var slice = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<byte>((byte*)sendHandle.data + m_PacketPadding + pipelineHeader, payloadCapacity, Allocator.Invalid);

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
                    headerSize = m_PacketPadding,
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
            /// <exception cref="InvalidOperationException">If EndSend is called without a matching BeginSend call.</exception>
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

                if (m_ConnectionList.ConnectionAt(pendingSendPtr->Connection.InternalId).Version != pendingSendPtr->Connection.Version)
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
            /// Aborts an asynchronous send. If calling this, there is not need to call <see cref="EndSend"/>.
            /// </summary>
            /// <param name="writer">If you require the payload to be of certain size.</param>
            /// <value>The length of the buffer sent if nothing went wrong.</value>
            /// <exception cref="InvalidOperationException">If AbortSend is called without a matching BeginSend call.</exception>
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
                    DebugLog.LogError("AbortSend without matching BeginSend");
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
                    if ((ret = m_DriverSender.BeginSend(out sendHandle, (uint)math.max(NetworkParameterConstants.MTU, originalHandle.size))) != 0)
                    {
                        return ret;
                    }
                    UnsafeUtility.MemCpy((void*)sendHandle.data, (void*)originalHandle.data, originalHandle.size);
                    sendHandle.size = originalHandle.size;
                }

                var endpoint = m_ConnectionList.GetConnectionEndpoint(sendConnection.m_ConnectionId);
                sendHandle.size -= m_PacketPadding;
                var result = m_DriverSender.EndSend(ref endpoint, ref sendHandle, m_PacketPadding, sendConnection.m_ConnectionId);

                // TODO: We temporarily add always a pipeline id (even if it's 0) when using new Layers
                if (!hasPipeline)
                {
                    var packetProcessor = m_DriverSender.m_SendQueue.GetPacketProcessor(sendHandle.id);
                    packetProcessor.PrependToPayload((byte)0);
                }
                return result;
            }

            internal void AbortSend(NetworkInterfaceSendHandle sendHandle)
            {
                if (0 == (sendHandle.flags & SendHandleFlags.AllocatedByDriver))
                {
                    m_DriverSender.AbortSend(ref sendHandle);
                }
            }

            public NetworkConnection.State GetConnectionState(NetworkConnection id)
            {
                if (id.InternalId < 0 || id.InternalId >= m_ConnectionList.Count)
                    return NetworkConnection.State.Disconnected;
                var connection = m_ConnectionList.ConnectionAt(id.InternalId);
                if (connection.Version != id.Version)
                    return NetworkConnection.State.Disconnected;

                var state = m_ConnectionList.GetConnectionState(connection);
                return state == NetworkConnection.State.Disconnecting ? NetworkConnection.State.Disconnected : state;
            }

            internal NetworkEventQueue.Concurrent m_EventQueue;
            internal NativeArray<byte> m_DisconnectReasons;

            [ReadOnly] internal ConnectionList m_ConnectionList;
            internal NetworkPipelineProcessor.Concurrent m_PipelineProcessor;
            internal UdpCHeader.HeaderFlags m_DefaultHeaderFlags;
            internal NetworkDriverSender.Concurrent m_DriverSender;
            [ReadOnly] internal NetworkDriverReceiver m_DriverReceiver;
            internal int m_PacketPadding;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            [NativeSetThreadIndex] internal int m_ThreadIndex;
            [NativeDisableParallelForRestriction] internal NativeArray<int> m_PendingBeginSend;
#endif
        }

        // internal variables :::::::::::::::::::::::::::::::::::::::::::::::::

        internal NetworkStack m_NetworkStack;

        NetworkDriverSender m_DriverSender;
        NetworkDriverReceiver m_DriverReceiver;

        internal NetworkDriverReceiver Receiver => m_DriverReceiver;
        internal NetworkEventQueue EventQueue => m_EventQueue;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        NativeArray<int> m_PendingBeginSend;
#endif

        NetworkEventQueue m_EventQueue;
        private NativeArray<byte> m_DisconnectReasons;

        [ReadOnly] NativeArray<int> m_InternalState;

        [NativeDisableContainerSafetyRestriction]
        NativeArray<long> m_UpdateTime;

        private NetworkConfigParameter m_NetworkParams;
        private NetworkPipelineProcessor m_PipelineProcessor;
        private UdpCHeader.HeaderFlags m_DefaultHeaderFlags;

        internal NetworkSettings m_NetworkSettings;

        /// <summary>Current settings used by the driver.</summary>
        /// <remarks>
        /// Current settings are read-only and can't be modified except through methods like
        /// <see cref="ModifySimulatorStageParameters" />.
        /// </remarks>
        public NetworkSettings CurrentSettings => m_NetworkSettings.AsReadOnly();

        private const int k_InternalStateListening = 0;
        private const int k_InternalStateBound = 1;

        public bool Listening
        {
            get => m_InternalState[k_InternalStateListening] != 0;
            private set => m_InternalState[k_InternalStateListening] = value ? 1 : 0;
        }

        public bool Bound => m_InternalState[k_InternalStateBound] == 1;

        private const int k_LastUpdateTime = 0;
        private const int k_UpdateTimeAdjustment = 1;

        internal long LastUpdateTime => m_UpdateTime[k_LastUpdateTime];

        /// <summary>
        /// Helper function for creating a NetworkDriver.
        /// </summary>
        /// <param name="settings">Configuration for the driver.</param>
        /// <exception cref="InvalidOperationException"></exception>
        public static NetworkDriver Create(NetworkSettings settings)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return Create(new IPCNetworkInterface(), settings);
#else
            return Create(new UDPNetworkInterface(), settings);
#endif
        }

        public static NetworkDriver Create() => Create(new NetworkSettings(Allocator.Temp));

        public static NetworkDriver Create<N>(N networkInterface) where N : unmanaged, INetworkInterface
            => Create(ref networkInterface);

        public static NetworkDriver Create<N>(ref N networkInterface) where N : unmanaged, INetworkInterface
            => Create(ref networkInterface, new NetworkSettings(Allocator.Temp));

        public static NetworkDriver Create<N>(N networkInterface, NetworkSettings settings) where N : unmanaged, INetworkInterface
            => Create(ref networkInterface, settings);

        public static NetworkDriver Create<N>(ref N networkInterface, NetworkSettings settings) where N : unmanaged, INetworkInterface
        {
            var driver = default(NetworkDriver);

            driver.m_NetworkSettings = new NetworkSettings(settings, Allocator.Persistent);
            driver.m_NetworkParams = settings.GetNetworkConfigParameters();
#if !UNITY_WEBGL || UNITY_EDITOR
            // Legacy support for baselib queue capacity parameters
            #pragma warning disable 618
            if (settings.TryGet<BaselibNetworkParameter>(out var baselibParameter))
            {
                if (driver.m_NetworkParams.sendQueueCapacity == NetworkParameterConstants.SendQueueCapacity &&
                    driver.m_NetworkParams.receiveQueueCapacity == NetworkParameterConstants.ReceiveQueueCapacity)
                {
                    driver.m_NetworkParams.sendQueueCapacity = baselibParameter.sendQueueCapacity;
                    driver.m_NetworkParams.receiveQueueCapacity = baselibParameter.receiveQueueCapacity;
                }
            }
            #pragma warning restore 618
#endif
            NetworkStack.InitializeForSettings(out driver.m_NetworkStack, ref networkInterface, ref settings, out var sendQueue, out var receiveQueue);

            driver.m_PipelineProcessor = new NetworkPipelineProcessor(settings, driver.m_NetworkStack.PacketPadding);

            driver.m_DriverSender = new NetworkDriverSender(sendQueue);
            driver.m_DriverReceiver = new NetworkDriverReceiver(receiveQueue);

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            driver.m_PendingBeginSend = new NativeArray<int>(JobsUtility.MaxJobThreadCount * JobsUtility.CacheLineSize / 4, Allocator.Persistent);
#endif

            driver.m_DefaultHeaderFlags = 0;

            driver.m_EventQueue = new NetworkEventQueue(NetworkParameterConstants.InitialEventQueueSize);

            const int reasons = (int)DisconnectReason.Count;
            driver.m_DisconnectReasons = new NativeArray<byte>(reasons, Allocator.Persistent);
            for (var idx = 0; idx < reasons; ++idx)
                driver.m_DisconnectReasons[idx] = (byte)idx;

            driver.m_InternalState = new NativeArray<int>(2, Allocator.Persistent);

            driver.m_UpdateTime = new NativeArray<long>(2, Allocator.Persistent);

            var time = TimerHelpers.GetCurrentTimestampMS();
            driver.m_UpdateTime[k_LastUpdateTime] = driver.m_NetworkParams.fixedFrameTimeMS > 0 ? 1 : time;
            driver.m_UpdateTime[k_UpdateTimeAdjustment] = 0;

            driver.Listening = false;

            return driver;
        }

        [Obsolete("Use NetworkDriver.Create(INetworkInterface networkInterface) instead", true)]
        public NetworkDriver(INetworkInterface netIf)
            => throw new NotImplementedException();

        [Obsolete("Use NetworkDriver.Create(INetworkInterface networkInterface, NetworkSettings settings) instead", true)]
        public NetworkDriver(INetworkInterface netIf, NetworkSettings settings)
            => throw new NotImplementedException();

        // interface implementation :::::::::::::::::::::::::::::::::::::::::::
        public void Dispose()
        {
            if (!IsCreated)
                return;

            m_NetworkStack.Dispose();

            m_DriverSender.Dispose();
            m_DriverReceiver.Dispose();

            m_NetworkSettings.Dispose();

            m_PipelineProcessor.Dispose();

            m_EventQueue.Dispose();
            m_DisconnectReasons.Dispose();

            m_InternalState.Dispose();
            m_UpdateTime.Dispose();
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

        [BurstCompile]
        struct ClearEventQueue : IJob
        {
            public NetworkEventQueue eventQueue;
            public NetworkDriverReceiver driverReceiver;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            public NativeArray<int> pendingSend;
            [ReadOnly] public ConnectionList connectionList;
            public long listenState;
#endif
            public void Execute()
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                for (int i = 0; i < connectionList.Count; ++i)
                {
                    int conCount = eventQueue.GetCountForConnection(i);
                    if (conCount != 0 && connectionList.GetConnectionState(connectionList.ConnectionAt(i)) != NetworkConnection.State.Disconnected)
                    {
                        DebugLog.ErrorResetNotEmptyEventQueue(conCount, i, listenState);
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
                            DebugLog.LogError("Missing EndSend, calling BeginSend without calling EndSend will result in a memory leak");
                            didPrint = true;
                        }
                    }
                }
#endif
                eventQueue.Clear();
                driverReceiver.ClearStream();
            }
        }

        private void UpdateLastUpdateTime()
        {
            long now = m_NetworkParams.fixedFrameTimeMS > 0
                ? LastUpdateTime + m_NetworkParams.fixedFrameTimeMS
                : TimerHelpers.GetCurrentTimestampMS() - m_UpdateTime[k_UpdateTimeAdjustment];

            long frameTime = now - LastUpdateTime;
            if (m_NetworkParams.maxFrameTimeMS > 0 && frameTime > m_NetworkParams.maxFrameTimeMS)
            {
                m_UpdateTime[k_UpdateTimeAdjustment] += frameTime - m_NetworkParams.maxFrameTimeMS;
                now = LastUpdateTime + m_NetworkParams.maxFrameTimeMS;
            }

            unsafe
            {
                var ptr = (long *)m_UpdateTime.GetUnsafePtr();
                Interlocked.Exchange(ref ptr[k_LastUpdateTime], now);
            }
        }

        public JobHandle ScheduleUpdate(JobHandle dependency = default)
        {
            UpdateLastUpdateTime();

            var updateJob = new UpdateJob {driver = this};

            // Clearing the event queue and receiving/sending data only makes sense if we're bound.
            if (Bound)
            {
                var connections = m_NetworkStack.Connections;

                var clearJob = new ClearEventQueue
                {
                    eventQueue = m_EventQueue,
                    driverReceiver = m_DriverReceiver,
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    pendingSend = m_PendingBeginSend,
                    connectionList = connections,
                    listenState = m_InternalState[k_InternalStateListening]
#endif
                };

                var handle = clearJob.Schedule(dependency);
                handle = updateJob.Schedule(handle);
                handle = m_NetworkStack.ScheduleReceive(ref m_DriverReceiver, ref connections, ref m_EventQueue, ref m_PipelineProcessor, LastUpdateTime, handle);
                handle = m_NetworkStack.ScheduleSend(ref m_DriverSender, LastUpdateTime, handle);

                return handle;
            }
            else
            {
                return updateJob.Schedule(dependency);
            }
        }

        public JobHandle ScheduleFlushSend(JobHandle dependency)
        {
            return m_NetworkStack.ScheduleSend(ref m_DriverSender, LastUpdateTime, dependency);
        }

        void InternalUpdate()
        {
            m_PipelineProcessor.Timestamp = LastUpdateTime;

            m_PipelineProcessor.UpdateReceive(ref this, out var updateCount);

            if (updateCount > m_NetworkStack.Connections.Count * 64)
            {
                DebugLog.DriverTooManyUpdates(updateCount);
            }

            m_DefaultHeaderFlags = UdpCHeader.HeaderFlags.HasPipeline;
            m_PipelineProcessor.UpdateSend(ToConcurrentSendOnly(), out updateCount);
            if (updateCount > m_NetworkStack.Connections.Count * 64)
            {
                DebugLog.DriverTooManyUpdates(updateCount);
            }

            m_DefaultHeaderFlags = 0;

            // Drop incoming connections if not listening. If we're bound but are not listening
            // (say because the user never called Listen or because they called StopListening),
            // clients can still establish connections and these connections will be perfectly
            // valid from their point of view, except that the server will never answer anything.
            if (!Listening)
            {
                ConnectionId connectionId;
                while ((connectionId = m_NetworkStack.Connections.AcceptConnection()) != default)
                {
                    Disconnect(new NetworkConnection(connectionId));
                }
            }
        }

        /// <summary>Register a custom pipeline stage.</summary>
        /// <remarks>
        /// Can only be called before a driver is bound (see <see cref="Bind" />).
        ///
        /// Note that the default pipeline stages (<see cref="FragmentationPipelineStage" />,
        /// <see cref="ReliableSequencedPipelineStage" />, <see cref="UnreliableSequencedPipelineStage" />,
        /// and <see cref="SimulatorPipelineStage" />) don't need to be registered. Registering a
        /// pipeline stage is only required for custom ones.
        /// </remarks>
        /// <typeparam name="T">The type of the pipeline stage (must be unmanaged).</typeparam>
        /// <param name="stage">An instance of the pipeline stage.</param>
        /// <exception cref="InvalidOperationException">
        /// If collections checks are enabled (ENABLE_UNITY_COLLECTIONS_CHECKS is defined), will be
        /// thrown if called after the driver is bound or before it is created.
        /// </exception>
        public void RegisterPipelineStage<T>(T stage) where T : unmanaged, INetworkPipelineStage
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (!IsCreated)
                throw new InvalidOperationException("Driver must be constructed before registering pipeline stages.");
            if (Bound)
                throw new InvalidOperationException("Can't register a pipeline stage after the driver is bound.");
#endif
            m_PipelineProcessor.RegisterPipelineStage<T>(stage, m_NetworkSettings);
        }

        /// <summary>Create a new pipeline from stage types.</summary>
        /// <remarks>
        /// The order of the different stages is important, as that is the order in which the stages
        /// will process a packet when sending messages (the reverse order is used when processing
        /// received packets).
        /// </remarks>
        /// <param name="stages">Array of stages the pipeline should contain.</param>
        /// <exception cref="InvalidOperationException">
        /// If collections checks are enabled (ENABLE_UNITY_COLLECTIONS_CHECKS is defined), will be
        /// thrown if called after the driver has established connections or before it is created.
        /// </exception>
        public NetworkPipeline CreatePipeline(params Type[] stages)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (!IsCreated)
                throw new InvalidOperationException("Driver must be constructed before creating pipelines.");
            if (m_NetworkStack.Connections.Count > 0)
                throw new InvalidOperationException("Pipelines can't be created after establishing connections.");
#endif
            var stageIds = new NativeArray<NetworkPipelineStageId>(stages.Length, Allocator.Temp);
            for (int i = 0; i < stages.Length; i++)
                stageIds[i] = NetworkPipelineStageId.Get(stages[i]);
            return CreatePipeline(stageIds);
        }

        /// <summary>Create a new pipeline from stage IDs.</summary>
        /// <remarks>
        /// The order of the different stages is important, as that is the order in which the stages
        /// will process a packet when sending messages (the reverse order is used when processing
        /// received packets).
        ///
        /// Note that this method is Burst-compatible. Note also that no reference to the native
        /// array is kept internally by the driver. It is thus safe to dispose of it immediately
        /// after calling this method (or to use a temporary allocation for the array).
        /// </remarks>
        /// <param name="stages">Array of stage IDs the pipeline should contain.</param>
        /// <exception cref="InvalidOperationException">
        /// If collections checks are enabled (ENABLE_UNITY_COLLECTIONS_CHECKS is defined), will be
        /// thrown if called after the driver has established connections or before it is created.
        /// </exception>
        public NetworkPipeline CreatePipeline(NativeArray<NetworkPipelineStageId> stages)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (!IsCreated)
                throw new InvalidOperationException("Driver must be constructed before creating pipelines.");
            if (m_NetworkStack.Connections.Count > 0)
                throw new InvalidOperationException("Pipelines can't be created after establishing connections.");
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
        public int Bind(NetworkEndpoint endpoint)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (!IsCreated)
                throw new InvalidOperationException(
                    "Driver must be constructed with a populated or empty INetworkParameter params list");
            // question: should this really be an error?
            if (Bound)
                throw new InvalidOperationException(
                    "Bind can only be called once per NetworkDriver");
            if (m_NetworkStack.Connections.Count > 0)
                throw new InvalidOperationException(
                    "Bind cannot be called after establishing connections");
#endif
            var result = m_NetworkStack.Bind(ref endpoint);
            m_InternalState[k_InternalStateBound] = result == 0 ? 1 : 0;

            return result;
        }

        /// <summary>
        /// Set the driver to Listen for incomming connections
        /// </summary>
        /// <value>Returns 0 on success.</value>
        /// <exception cref="InvalidOperationException">If the driver is not created properly</exception>
        /// <exception cref="InvalidOperationException">If listen is called more then once on the driver</exception>
        /// <exception cref="InvalidOperationException">If bind has not been called before calling Listen.</exception>
        public int Listen()
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (!IsCreated)
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
            var ret = m_NetworkStack.Listen();
            if (ret == 0)
                Listening = true;
            return ret;
        }

        // Offered as a workaround for DOTS until we support Burst-compatible constructors/dispose.
        // This is not something we want users to be able to do normally. See MTT-2607.
        [Obsolete("The correct way to stop listening is disposing of the driver (and recreating a new one).")]
        internal void StopListening()
        {
            Listening = false;
        }

        /// <summary>
        /// Checks to see if there are any new connections to Accept.
        /// </summary>
        /// <value>If accept fails it returnes a default NetworkConnection.</value>
        public NetworkConnection Accept()
        {
            if (!Listening)
                return default;

            var connectionId = m_NetworkStack.Connections.AcceptConnection();
            return new NetworkConnection(connectionId);
        }

        /// <summary>
        /// Connects the driver to a endpoint
        /// </summary>
        /// <value>If connect fails it returns a default NetworkConnection.</value>
        /// <exception cref="InvalidOperationException">If the driver is not created properly</exception>
        public NetworkConnection Connect(NetworkEndpoint endpoint)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (!IsCreated)
                throw new InvalidOperationException(
                    "Driver must be constructed with a populated or empty INetworkParameter params list");
#endif

            if (!Bound)
            {
                var nep = endpoint.Family == NetworkFamily.Ipv6 ? NetworkEndpoint.AnyIpv6 : NetworkEndpoint.AnyIpv4;
                if (Bind(nep) != 0)
                    return default;
            }

            var connectionId = m_NetworkStack.Connections.StartConnecting(ref endpoint);
            var networkConnection = new NetworkConnection(connectionId);

            m_PipelineProcessor.InitializeConnection(networkConnection);
            return networkConnection;
        }

        /// <summary>
        /// Disconnects a NetworkConnection
        /// </summary>
        /// <param name="id">The NetworkConnection we want to Disconnect.</param>
        /// <value>Return 0 on success.</value>
        public int Disconnect(NetworkConnection id)
        {
            var connectionState = GetConnectionState(id);

            if (connectionState != NetworkConnection.State.Disconnected)
            {
                m_NetworkStack.Connections.StartDisconnecting(ref id.m_ConnectionId);
            }

            return 0;
        }

        /// <summary>
        /// Returns the PipelineBuffers for a specific pipeline and stage.
        /// </summary>
        /// <param name="pipeline"></param>
        /// <param name="stageId"></param>
        /// <param name="connection"></param>
        /// <param name="readProcessingBuffer"></param>
        /// <param name="writeProcessingBuffer"></param>
        /// <param name="sharedBuffer"></param>
        /// <exception cref="InvalidOperationException">If the the connection is invalid.</exception>
        public void GetPipelineBuffers(NetworkPipeline pipeline, NetworkPipelineStageId stageId, NetworkConnection connection, out NativeArray<byte> readProcessingBuffer, out NativeArray<byte> writeProcessingBuffer, out NativeArray<byte> sharedBuffer)
        {
            if (m_NetworkStack.Connections.ConnectionAt(connection.InternalId) != connection.m_ConnectionId)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                throw new InvalidOperationException("Invalid connection");
#else
                DebugLog.LogError("Trying to get pipeline buffers for invalid connection.");
                readProcessingBuffer = default;
                writeProcessingBuffer = default;
                sharedBuffer = default;
                return;
#endif
            }
            m_PipelineProcessor.GetPipelineBuffers(pipeline, stageId, connection, out readProcessingBuffer, out writeProcessingBuffer, out sharedBuffer);
        }

        // Keeping the pipeline parameter there to avoid breaking DOTS.
        /// <inheritdoc cref="NetworkPipelineProcessor.GetWriteablePipelineParameter{T}"/>
        internal unsafe T* GetWriteablePipelineParameter<T>(NetworkPipeline pipeline, NetworkPipelineStageId stageId)
            where T : unmanaged, INetworkParameter
        {
            return m_PipelineProcessor.GetWriteablePipelineParameter<T>(stageId);
        }

        public NetworkConnection.State GetConnectionState(NetworkConnection con)
        {
            var state = m_NetworkStack.Connections.GetConnectionState(con.m_ConnectionId);
            return state == NetworkConnection.State.Disconnecting ? NetworkConnection.State.Disconnected : state;
        }

        [Obsolete("RemoteEndPoint has been renamed to GetRemoteEndpoint. (UnityUpgradable) -> GetRemoteEndpoint(*)", false)]
        public NetworkEndpoint RemoteEndPoint(NetworkConnection id)
        {
            return m_NetworkStack.Connections.GetConnectionEndpoint(id.m_ConnectionId);
        }

        /// <summary>
        /// Get the remote endpoint of a connection (the endpoint used to reach the remote peer on the connection).
        /// </summary>
        /// <param name="id">Connection to get the endpoint of.</param>
        /// <returns>The remote endpoint of the connection.</returns>
        public NetworkEndpoint GetRemoteEndpoint(NetworkConnection id)
        {
            return m_NetworkStack.Connections.GetConnectionEndpoint(id.m_ConnectionId);
        }

        [Obsolete("LocalEndPoint has been renamed to GetLocalEndpoint. (UnityUpgradable) -> GetLocalEndpoint()", false)]
        public NetworkEndpoint LocalEndPoint()
        {
            return m_NetworkStack.GetLocalEndpoint();
        }

        /// <summary>
        /// Get the local endpoint used by the driver (the endpoint remote peers will use to reach this driver).
        /// </summary>
        /// <returns>The local endpoint of the driver.</returns>
        public NetworkEndpoint GetLocalEndpoint()
        {
            return m_NetworkStack.GetLocalEndpoint();
        }

        public int MaxHeaderSize(NetworkPipeline pipe)
        {
            return ToConcurrentSendOnly().MaxHeaderSize(pipe);
        }

        public int BeginSend(NetworkPipeline pipe, NetworkConnection id, out DataStreamWriter writer, int requiredPayloadSize = 0)
        {
            return ToConcurrentSendOnly().BeginSend(pipe, id, out writer, requiredPayloadSize);
        }

        public int BeginSend(NetworkConnection id, out DataStreamWriter writer, int requiredPayloadSize = 0)
        {
            return ToConcurrentSendOnly().BeginSend(NetworkPipeline.Null, id, out writer, requiredPayloadSize);
        }

        public int EndSend(DataStreamWriter writer)
        {
            return ToConcurrentSendOnly().EndSend(writer);
        }

        public void AbortSend(DataStreamWriter writer)
        {
            ToConcurrentSendOnly().AbortSend(writer);
        }

        /// <summary>
        /// Pops an event
        /// </summary>
        /// <param name="con"></param>
        /// <param name="reader"></param>
        /// <value>Returns the type of event received, if the value is a <see cref="NetworkEvent.Type.Disconnect"/> event
        /// then the DataStreamReader will contain the disconnect reason. If a listening NetworkDriver has received Data
        /// events from a client, but the NetworkDriver has not Accepted the NetworkConnection yet, the Data event will
        /// be discarded.<value/>
        public NetworkEvent.Type PopEvent(out NetworkConnection con, out DataStreamReader reader)
        {
            return PopEvent(out con, out reader, out var _);
        }

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
                var connectionId = m_NetworkStack.Connections.ConnectionAt(id);

                //This is in service of not providing any means for a server's / listening NetworkDriver's user-level code to obtain a NetworkConnection handle
                //that corresponds to an underlying Connection that lives in m_NetworkStack.Connections without having obtained it from Accept() first.
                if (id >= 0 && type == NetworkEvent.Type.Data && !m_NetworkStack.Connections.IsConnectionAccepted(ref connectionId))
                {
                    DebugLog.LogWarning("A NetworkEvent.Data event was discarded for a connection that had not been accepted yet. To avoid this, consider calling Accept() prior to PopEvent() in your project's network update loop, or only use PopEventForConnection() in conjunction with Accept().");
                    continue;
                }

                break;
            }

            pipeline = new NetworkPipeline { Id = pipelineId };

            if (type == NetworkEvent.Type.Disconnect && offset < 0)
                reader = new DataStreamReader(m_DisconnectReasons.GetSubArray(math.abs(offset), 1));
            else if (size > 0)
                reader = new DataStreamReader(m_DriverReceiver.GetDataStreamSubArray(offset, size));
            con = id < 0
                ? default
                : new NetworkConnection(m_NetworkStack.Connections.ConnectionAt(id));

            return type;
        }

        /// <summary>
        /// Pops an event for a specific connection
        /// </summary>
        /// <param name="connectionId"></param>
        /// <param name="reader"></param>
        /// <value>Returns the type of event received, if the value is a <see cref="NetworkEvent.Type.Disconnect"/> event
        /// then the DataStreamReader will contain the disconnect reason.<value/>
        public NetworkEvent.Type PopEventForConnection(NetworkConnection connectionId, out DataStreamReader reader)
        {
            return PopEventForConnection(connectionId, out reader, out var _);
        }

        public NetworkEvent.Type PopEventForConnection(NetworkConnection connectionId, out DataStreamReader reader, out NetworkPipeline pipeline)
        {
            reader = default;
            pipeline = default;

            if (connectionId.InternalId < 0 || connectionId.InternalId >= m_NetworkStack.Connections.Count ||
                m_NetworkStack.Connections.ConnectionAt(connectionId.InternalId).Version != connectionId.Version)
                return (int)NetworkEvent.Type.Empty;
            var type = m_EventQueue.PopEventForConnection(connectionId.InternalId, out var offset, out var size, out var pipelineId);
            pipeline = new NetworkPipeline { Id = pipelineId };

            if (type == NetworkEvent.Type.Disconnect && offset < 0)
                reader = new DataStreamReader(m_DisconnectReasons.GetSubArray(math.abs(offset), 1));
            else if (size > 0)
                reader = new DataStreamReader(m_DriverReceiver.GetDataStreamSubArray(offset, size));

            return type;
        }

        /// <summary>
        /// Returns the size of the EventQueue for a specific connection
        /// </summary>
        /// <param name="connectionId"></param>
        /// <value>If the connection is valid it returns the size of the event queue otherwise it returns 0.</value>
        public int GetEventQueueSizeForConnection(NetworkConnection connectionId)
        {
            if (connectionId.InternalId < 0 || connectionId.InternalId >= m_NetworkStack.Connections.Count ||
                m_NetworkStack.Connections.ConnectionAt(connectionId.InternalId).Version != connectionId.Version)
                return 0;
            return m_EventQueue.GetCountForConnection(connectionId.InternalId);
        }

        public int ReceiveErrorCode
        {
            get => m_DriverReceiver.Result.ErrorCode;
        }
    }
}
