using System;
using System.Threading;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Burst;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Networking.Transport.Protocols;
using Unity.Networking.Transport.Utilities;
using System.Runtime.InteropServices;

namespace Unity.Networking.Transport
{
    /// <summary>
    /// The inbound send buffer
    /// </summary>
    public unsafe struct InboundSendBuffer
    {
        /// <summary>
        /// The buffer
        /// </summary>
        public byte* buffer;

        /// <summary>
        /// The buffer with headers
        /// </summary>
        public byte* bufferWithHeaders;

        /// <summary>
        /// The buffer length
        /// </summary>
        public int bufferLength;

        /// <summary>
        /// The buffer with headers length
        /// </summary>
        public int bufferWithHeadersLength;

        /// <summary>
        /// The header padding
        /// </summary>
        public int headerPadding;

        /// <summary>
        /// Sets the buffer frombuffer with headers
        /// </summary>
        public void SetBufferFrombufferWithHeaders()
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (bufferWithHeadersLength < headerPadding)
                throw new IndexOutOfRangeException("Buffer is too small to fit headers");
#endif
            buffer = bufferWithHeaders + headerPadding;
            bufferLength = bufferWithHeadersLength - headerPadding;
        }
    }
    /// <summary>
    /// The inbound recv buffer
    /// </summary>
    public unsafe struct InboundRecvBuffer
    {
        /// <summary>
        /// The buffer
        /// </summary>
        public byte* buffer;

        /// <summary>
        /// The buffer length
        /// </summary>
        public int bufferLength;

        /// <summary>
        /// Slices the offset
        /// </summary>
        /// <param name="offset">The offset</param>
        /// <returns>The slice</returns>
        public InboundRecvBuffer Slice(int offset)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (bufferLength < offset)
                throw new ArgumentOutOfRangeException("Buffer does not contain enough data");
#endif
            InboundRecvBuffer slice;
            slice.buffer = buffer + offset;
            slice.bufferLength = bufferLength - offset;
            return slice;
        }
    }
    /// <summary>
    /// The network pipeline context
    /// </summary>
    public unsafe struct NetworkPipelineContext
    {
        /// <summary>
        /// The static instance buffer
        /// </summary>
        public byte* staticInstanceBuffer;

        /// <summary>
        /// The internal shared process buffer
        /// </summary>
        public byte* internalSharedProcessBuffer;

        /// <summary>
        /// The internal process buffer
        /// </summary>
        public byte* internalProcessBuffer;

        /// <summary>
        /// The header
        /// </summary>
        public DataStreamWriter header;

        /// <summary>
        /// The timestamp
        /// </summary>
        public long timestamp;

        /// <summary>
        /// The static instance buffer length
        /// </summary>
        public int staticInstanceBufferLength;

        /// <summary>
        /// The internal shared process buffer length
        /// </summary>
        public int internalSharedProcessBufferLength;

        /// <summary>
        /// The internal process buffer length
        /// </summary>
        public int internalProcessBufferLength;

        /// <summary>
        /// The accumulated header capacity
        /// </summary>
        public int accumulatedHeaderCapacity;
    }

    /// <summary>
    /// The network pipeline stage interface
    /// </summary>
    public unsafe interface INetworkPipelineStage
    {
        /// <summary>
        /// Statics the initialize using the specified static instance buffer
        /// </summary>
        /// <param name="staticInstanceBuffer">The static instance buffer</param>
        /// <param name="staticInstanceBufferLength">The static instance buffer length</param>
        /// <param name="param">The param</param>
        /// <returns>The network pipeline stage</returns>
        NetworkPipelineStage StaticInitialize(byte* staticInstanceBuffer, int staticInstanceBufferLength, NetworkSettings settings);
        /// <summary>
        /// Gets the value of the static size
        /// </summary>
        int StaticSize { get; }
    }

    /// <summary>
    /// The network pipeline stage
    /// </summary>
    public unsafe struct NetworkPipelineStage
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="NetworkPipelineStage"/> class
        /// </summary>
        /// <param name="Receive">The receive</param>
        /// <param name="Send">The send</param>
        /// <param name="InitializeConnection">The initialize connection</param>
        /// <param name="ReceiveCapacity">The receive capacity</param>
        /// <param name="SendCapacity">The send capacity</param>
        /// <param name="HeaderCapacity">The header capacity</param>
        /// <param name="SharedStateCapacity">The shared state capacity</param>
        /// <param name="PayloadCapacity">The payload capacity</param>
        public NetworkPipelineStage(TransportFunctionPointer<ReceiveDelegate> Receive,
                                    TransportFunctionPointer<SendDelegate> Send,
                                    TransportFunctionPointer<InitializeConnectionDelegate> InitializeConnection,
                                    int ReceiveCapacity,
                                    int SendCapacity,
                                    int HeaderCapacity,
                                    int SharedStateCapacity,
                                    int PayloadCapacity = 0) // 0 means any size
        {
            this.Receive = Receive;
            this.Send = Send;
            this.InitializeConnection = InitializeConnection;
            this.ReceiveCapacity = ReceiveCapacity;
            this.SendCapacity = SendCapacity;
            this.HeaderCapacity = HeaderCapacity;
            this.SharedStateCapacity = SharedStateCapacity;
            this.PayloadCapacity = PayloadCapacity;
            StaticStateStart = StaticStateCapcity = 0;
        }

        /// <summary>
        /// The requests enum
        /// </summary>
        [Flags]
        public enum Requests
        {
            /// <summary>
            /// The none requests
            /// </summary>
            None = 0,
            /// <summary>
            /// The resume requests
            /// </summary>
            Resume = 1,
            /// <summary>
            /// The update requests
            /// </summary>
            Update = 2,
            /// <summary>
            /// The send update requests
            /// </summary>
            SendUpdate = 4,
            /// <summary>
            /// The error requests
            /// </summary>
            Error = 8
        }

        /// <summary>
        /// The receive delegate
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void ReceiveDelegate(ref NetworkPipelineContext ctx, ref InboundRecvBuffer inboundBuffer, ref Requests requests, int systemHeadersSize);

        /// <summary>
        /// The send delegate
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int SendDelegate(ref NetworkPipelineContext ctx, ref InboundSendBuffer inboundBuffer, ref Requests requests, int systemHeadersSize);

        /// <summary>
        /// The initialize connection delegate
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void InitializeConnectionDelegate(byte* staticInstanceBuffer, int staticInstanceBufferLength,
            byte* sendProcessBuffer, int sendProcessBufferLength, byte* recvProcessBuffer, int recvProcessBufferLength,
            byte* sharedProcessBuffer, int sharedProcessBufferLength);

        /// <summary>
        /// Receive function pointer
        /// </summary>
        public TransportFunctionPointer<ReceiveDelegate> Receive;

        /// <summary>
        /// Send function pointer
        /// </summary>
        public TransportFunctionPointer<SendDelegate> Send;

        /// <summary>
        /// InitializeConnection function pointer
        /// </summary>
        public TransportFunctionPointer<InitializeConnectionDelegate> InitializeConnection;

        /// <summary>
        /// The receive capacity
        /// </summary>
        public readonly int ReceiveCapacity;

        /// <summary>
        /// The send capacity
        /// </summary>
        public readonly int SendCapacity;

        /// <summary>
        /// The header capacity
        /// </summary>
        public readonly int HeaderCapacity;

        /// <summary>
        /// The shared state capacity
        /// </summary>
        public readonly int SharedStateCapacity;

        /// <summary>
        /// The payload capacity
        /// </summary>
        public readonly int PayloadCapacity;

        internal int StaticStateStart;
        internal int StaticStateCapcity;
    }

    public struct NetworkPipelineStageId
    {
        internal int Index;
        internal int IsValid;
    }

    /// <summary>
    /// The network pipeline stage collection class
    /// </summary>
    public static class NetworkPipelineStageCollection
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="NetworkPipelineStageCollection"/> class
        /// </summary>
        static NetworkPipelineStageCollection()
        {
            m_stages = new List<INetworkPipelineStage>();
            RegisterPipelineStage(new NullPipelineStage());
            RegisterPipelineStage(new FragmentationPipelineStage());
            RegisterPipelineStage(new ReliableSequencedPipelineStage());
            RegisterPipelineStage(new UnreliableSequencedPipelineStage());
            RegisterPipelineStage(new SimulatorPipelineStage());
            RegisterPipelineStage(new SimulatorPipelineStageInSend());
        }

        /// <summary>
        /// Registers the pipeline stage using the specified stage
        /// </summary>
        /// <param name="stage">The stage</param>
        public static void RegisterPipelineStage(INetworkPipelineStage stage)
        {
            for (int i = 0; i < m_stages.Count; ++i)
            {
                if (m_stages[i].GetType() == stage.GetType())
                {
                    // TODO: should this be an error?
                    m_stages[i] = stage;
                    return;
                }
            }
            m_stages.Add(stage);
        }

        /// <summary>
        /// Gets the stage id using the specified stage type
        /// </summary>
        /// <param name="stageType">The stage type</param>
        /// <exception cref="InvalidOperationException">Pipeline stage {stageType} is not registered</exception>
        /// <returns>The network pipeline stage id</returns>
        public static NetworkPipelineStageId GetStageId(Type stageType)
        {
            for (int i = 0; i < m_stages.Count; ++i)
            {
                if (stageType == m_stages[i].GetType())
                    return new NetworkPipelineStageId {Index = i, IsValid = 1};
            }
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            throw new InvalidOperationException($"Pipeline stage {stageType} is not registered");
#else
            UnityEngine.Debug.LogError($"Pipeline stage {stageType} is not registered");
            return default;
#endif
        }

        internal static List<INetworkPipelineStage> m_stages;
    }

    /// <summary>
    /// The network pipeline
    /// </summary>
    public struct NetworkPipeline
    {
        internal int Id;
        /// <summary>
        /// Returns default unreliable Null <see cref="NetworkPipeline"/>
        /// </summary>
        public static NetworkPipeline Null => default;

        public static bool operator==(NetworkPipeline lhs, NetworkPipeline rhs)
        {
            return lhs.Id == rhs.Id;
        }

        public static bool operator!=(NetworkPipeline lhs, NetworkPipeline rhs)
        {
            return lhs.Id != rhs.Id;
        }

        public override bool Equals(object compare)
        {
            return this == (NetworkPipeline)compare;
        }

        public override int GetHashCode()
        {
            return Id;
        }

        public bool Equals(NetworkPipeline connection)
        {
            return connection.Id == Id;
        }
    }


    public static class NetworkPipelineParametersExtensions
    {
        /// <summary>
        /// Sets the <see cref="NetworkPipelineParams"/> values for the <see cref="NetworkSettings"/>
        /// </summary>
        /// <param name="initialCapacity"><seealso cref="NetworkPipelineParams.initialCapacity"/></param>
        public static ref NetworkSettings WithPipelineParameters(
            ref this NetworkSettings settings,
            int initialCapacity = NetworkPipelineParams.k_DefaultInitialCapacity
        )
        {
            var parameter = new NetworkPipelineParams
            {
                initialCapacity = initialCapacity,
            };

            settings.AddRawParameterStruct(ref parameter);

            return ref settings;
        }

        /// <summary>
        /// Gets the <see cref="NetworkPipelineParams"/>
        /// </summary>
        /// <returns>Returns the <see cref="NetworkPipelineParams"/> values for the <see cref="NetworkSettings"/></returns>
        public static NetworkPipelineParams GetPipelineParameters(ref this NetworkSettings settings)
        {
            if (!settings.TryGet<NetworkPipelineParams>(out var parameters))
            {
                parameters.initialCapacity = NetworkPipelineParams.k_DefaultInitialCapacity;
            }

            return parameters;
        }
    }

    /// <summary>
    /// The network pipeline params
    /// </summary>
    public struct NetworkPipelineParams : INetworkParameter, IValidatableNetworkParameter
    {
        internal const int k_DefaultInitialCapacity = 0;

        /// <summary>
        /// The initial capacity
        /// </summary>
        public int initialCapacity;

        public bool Validate()
        {
            var valid = true;

            if (initialCapacity < 0)
            {
                valid = false;
                UnityEngine.Debug.LogError($"{nameof(initialCapacity)} value ({initialCapacity}) must be greater or equal to 0");
            }

            return valid;
        }
    }

    internal struct NetworkPipelineProcessor : IDisposable
    {
        public const int Alignment = 8;
        public const int AlignmentMinusOne = Alignment - 1;

        public int PayloadCapacity(NetworkPipeline pipeline)
        {
            if (pipeline.Id > 0)
            {
                var p = m_Pipelines[pipeline.Id - 1];
                return p.payloadCapacity;
            }
            return 0;
        }

        public Concurrent ToConcurrent()
        {
            var concurrent = new Concurrent
            {
                m_StageCollection = m_StageCollection,
                m_StaticInstanceBuffer = m_StaticInstanceBuffer,
                m_Pipelines = m_Pipelines,
                m_StageList = m_StageList,
                m_AccumulatedHeaderCapacity = m_AccumulatedHeaderCapacity,
                m_SendStageNeedsUpdateWrite = m_SendStageNeedsUpdateRead.AsParallelWriter(),
                sizePerConnection = sizePerConnection,
                sendBuffer = m_SendBuffer,
                sharedBuffer = m_SharedBuffer,
                m_timestamp = m_timestamp,
            };
            return concurrent;
        }

        public struct Concurrent
        {
            [ReadOnly] internal NativeArray<NetworkPipelineStage> m_StageCollection;
            [ReadOnly] internal NativeArray<byte> m_StaticInstanceBuffer;
            [ReadOnly] internal NativeList<PipelineImpl> m_Pipelines;
            [ReadOnly] internal NativeList<int> m_StageList;
            [ReadOnly] internal NativeList<int> m_AccumulatedHeaderCapacity;
            internal NativeQueue<UpdatePipeline>.ParallelWriter m_SendStageNeedsUpdateWrite;
            [ReadOnly] internal NativeArray<int> sizePerConnection;
            // TODO: not really read-only, just hacking the safety system
            [ReadOnly] internal NativeList<byte> sharedBuffer;
            [ReadOnly] internal NativeList<byte> sendBuffer;
            [ReadOnly] internal NativeArray<long> m_timestamp;

            public int SendHeaderCapacity(NetworkPipeline pipeline)
            {
                var p = m_Pipelines[pipeline.Id - 1];
                return p.headerCapacity;
            }

            public int PayloadCapacity(NetworkPipeline pipeline)
            {
                if (pipeline.Id > 0)
                {
                    var p = m_Pipelines[pipeline.Id - 1];
                    return p.payloadCapacity;
                }
                return 0;
            }

            public unsafe int Send(NetworkDriver.Concurrent driver, NetworkPipeline pipeline, NetworkConnection connection, NetworkInterfaceSendHandle sendHandle, int headerSize)
            {
                if (sendHandle.data == IntPtr.Zero)
                {
                    return (int)Error.StatusCode.NetworkSendHandleInvalid;
                }

                var connectionId = connection.m_NetworkId;

                // TODO: not really read-only, just hacking the safety system
                NativeArray<byte> tmpBuffer = sendBuffer;
                int* sendBufferLock = (int*)tmpBuffer.GetUnsafeReadOnlyPtr();
                sendBufferLock += connectionId * sizePerConnection[SendSizeOffset] / 4;

                if (Interlocked.CompareExchange(ref *sendBufferLock, 1, 0) != 0)
                {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    UnityEngine.Debug.LogError("The parallel network driver needs to process a single unique connection per job, processing a single connection multiple times in a parallel for is not supported.");
                    return (int)Error.StatusCode.NetworkDriverParallelForErr;
#else
                    return (int)Error.StatusCode.NetworkDriverParallelForErr;
#endif
                }
                NativeList<UpdatePipeline> currentUpdates = new NativeList<UpdatePipeline>(128, Allocator.Temp);

                int retval = ProcessPipelineSend(driver, 0, pipeline, connection, sendHandle, headerSize, currentUpdates);

                Interlocked.Exchange(ref *sendBufferLock, 0);
                // Move the updates requested in this iteration to the concurrent queue so it can be read/parsed in update routine
                for (int i = 0; i < currentUpdates.Length; ++i)
                    m_SendStageNeedsUpdateWrite.Enqueue(currentUpdates[i]);

                return retval;
            }

            internal unsafe int ProcessPipelineSend(NetworkDriver.Concurrent driver, int startStage, NetworkPipeline pipeline, NetworkConnection connection,
                NetworkInterfaceSendHandle sendHandle, int headerSize, NativeList<UpdatePipeline> currentUpdates)
            {
                int initialHeaderSize = headerSize;
                int retval = sendHandle.size;
                NetworkPipelineContext ctx = default(NetworkPipelineContext);
                ctx.timestamp = m_timestamp[0];
                var p = m_Pipelines[pipeline.Id - 1];
                var connectionId = connection.m_NetworkId;
                var systemHeaderSize = driver.MaxProtocolHeaderSize();

                // If the call comes from update, the sendHandle is set to default.
                bool inUpdateCall = sendHandle.data == IntPtr.Zero;

                var resumeQ = new NativeList<int>(16, Allocator.Temp);
                int resumeQStart = 0;

                var inboundBuffer = default(InboundSendBuffer);
                if (!inUpdateCall)
                {
                    inboundBuffer.bufferWithHeaders = (byte*)sendHandle.data + initialHeaderSize + 1;
                    inboundBuffer.bufferWithHeadersLength = sendHandle.size - initialHeaderSize - 1;
                    inboundBuffer.buffer = inboundBuffer.bufferWithHeaders + p.headerCapacity;
                    inboundBuffer.bufferLength = inboundBuffer.bufferWithHeadersLength - p.headerCapacity;
                }

                while (true)
                {
                    headerSize = p.headerCapacity;

                    int internalBufferOffset = p.sendBufferOffset + sizePerConnection[SendSizeOffset] * connectionId;
                    int internalSharedBufferOffset = p.sharedBufferOffset + sizePerConnection[SharedSizeOffset] * connectionId;

                    // If this is not the first stage we need to fast forward the buffer offset to the correct place
                    if (startStage > 0)
                    {
                        if (inboundBuffer.bufferWithHeadersLength > 0)
                        {
                            UnityEngine.Debug.LogError("Can't start from a stage with a buffer");
                            return (int)Error.StatusCode.NetworkStateMismatch;
                        }
                        for (int i = 0; i < startStage; ++i)
                        {
                            internalBufferOffset += (m_StageCollection[m_StageList[p.FirstStageIndex + i]].SendCapacity + AlignmentMinusOne) & (~AlignmentMinusOne);
                            internalSharedBufferOffset += (m_StageCollection[m_StageList[p.FirstStageIndex + i]].SharedStateCapacity + AlignmentMinusOne) & (~AlignmentMinusOne);
                            headerSize -= m_StageCollection[m_StageList[p.FirstStageIndex + i]].HeaderCapacity;
                        }
                    }

                    for (int i = startStage; i < p.NumStages; ++i)
                    {
                        int stageHeaderCapacity = m_StageCollection[m_StageList[p.FirstStageIndex + i]].HeaderCapacity;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        if (stageHeaderCapacity > headerSize)
                            throw new InvalidOperationException("The stage does not contain enough header space to send the message");
#endif
                        inboundBuffer.headerPadding = headerSize;
                        headerSize -= stageHeaderCapacity;
                        if (stageHeaderCapacity > 0 && inboundBuffer.bufferWithHeadersLength > 0)
                        {
                            var headerArray = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<byte>(inboundBuffer.bufferWithHeaders + headerSize, stageHeaderCapacity, Allocator.Invalid);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                            NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref headerArray, AtomicSafetyHandle.GetTempMemoryHandle());
#endif
                            ctx.header = new DataStreamWriter(headerArray);
                        }
                        else
                            ctx.header = new DataStreamWriter(stageHeaderCapacity, Allocator.Temp);
                        var prevInbound = inboundBuffer;
                        NetworkPipelineStage.Requests requests = NetworkPipelineStage.Requests.None;

                        var sendResult = ProcessSendStage(i, internalBufferOffset, internalSharedBufferOffset, p, ref resumeQ, ref ctx, ref inboundBuffer, ref requests, systemHeaderSize);

                        if ((requests & NetworkPipelineStage.Requests.Update) != 0)
                            AddSendUpdate(connection, i, pipeline, currentUpdates);

                        if (inboundBuffer.bufferWithHeadersLength == 0)
                        {
                            if ((requests & NetworkPipelineStage.Requests.Error) != 0 && !inUpdateCall)
                                retval = sendResult;
                            break;
                        }

#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        if (inboundBuffer.headerPadding != prevInbound.headerPadding)
                            throw new InvalidOperationException("Changing the header padding in a pipeline is not supported");
#endif
                        if (inboundBuffer.buffer != prevInbound.buffer)
                        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                            if (inboundBuffer.buffer != inboundBuffer.bufferWithHeaders + inboundBuffer.headerPadding ||
                                inboundBuffer.bufferLength + inboundBuffer.headerPadding > inboundBuffer.bufferWithHeadersLength)
                                throw new InvalidOperationException("When creating an internal buffer in pipelines the buffer must be a subset of the buffer with headers");
#endif
                            // Copy header to new buffer so it is part of the payload
                            UnsafeUtility.MemCpy(inboundBuffer.bufferWithHeaders + headerSize, ctx.header.AsNativeArray().GetUnsafeReadOnlyPtr(), ctx.header.Length);
                        }
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        else
                        {
                            if (inboundBuffer.bufferWithHeaders != prevInbound.bufferWithHeaders)
                                throw new InvalidOperationException("Changing the send buffer with headers without changing the buffer is not supported");
                        }
#endif
                        if (ctx.header.Length < stageHeaderCapacity)
                        {
                            int wastedSpace = stageHeaderCapacity - ctx.header.Length;
                            // Remove wasted space in the header
                            UnsafeUtility.MemMove(inboundBuffer.buffer - wastedSpace, inboundBuffer.buffer, inboundBuffer.bufferLength);
                        }

                        // Update the inbound buffer for next iteration
                        inboundBuffer.buffer = inboundBuffer.bufferWithHeaders + headerSize;
                        inboundBuffer.bufferLength = ctx.header.Length + inboundBuffer.bufferLength;


                        internalBufferOffset += (ctx.internalProcessBufferLength + AlignmentMinusOne) & (~AlignmentMinusOne);
                        internalSharedBufferOffset += (ctx.internalSharedProcessBufferLength + AlignmentMinusOne) & (~AlignmentMinusOne);
                    }

                    if (inboundBuffer.bufferLength != 0)
                    {
                        if (sendHandle.data != IntPtr.Zero && inboundBuffer.bufferWithHeaders == (byte*)sendHandle.data + initialHeaderSize + 1)
                        {
                            // Actually send the data - after collapsing it again
                            if (inboundBuffer.buffer != inboundBuffer.bufferWithHeaders)
                            {
                                UnsafeUtility.MemMove(inboundBuffer.bufferWithHeaders, inboundBuffer.buffer, inboundBuffer.bufferLength);
                                inboundBuffer.buffer = inboundBuffer.bufferWithHeaders;
                            }
                            ((byte*)sendHandle.data)[initialHeaderSize] = (byte)pipeline.Id;
                            int sendSize = initialHeaderSize + 1 + inboundBuffer.bufferLength;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                            if (sendSize > sendHandle.size)
                                throw new InvalidOperationException("Pipeline increased the data in the buffer, this is not allowed");
#endif
                            sendHandle.size = sendSize;
                            if ((retval = driver.CompleteSend(connection, sendHandle, true)) < 0)
                            {
                                UnityEngine.Debug.LogWarning(FixedString.Format("CompleteSend failed with the following error code: {0}", retval));
                            }
                            sendHandle = default;
                        }
                        else
                        {
                            // TODO: This sends the packet directly, bypassing the pipeline process. The problem is that in that way
                            // we can't set the hasPipeline flag in the headers. There is a workaround for now.
                            // Sending without pipeline, the correct pipeline will be added by the default flags when this is called

                            if (driver.BeginSend(connection, out var writer) == 0)
                            {
                                writer.WriteByte((byte)pipeline.Id);
                                writer.WriteBytes(inboundBuffer.buffer, inboundBuffer.bufferLength);
                                if ((retval = driver.EndSend(writer)) <= 0)
                                {
                                    UnityEngine.Debug.Log(FixedString.Format("An error occurred during EndSend. ErrorCode: {0}", retval));
                                }
                            }
                        }
                    }

                    if (resumeQStart >= resumeQ.Length)
                    {
                        break;
                    }

                    startStage = resumeQ[resumeQStart++];

                    inboundBuffer = default;
                }
                if (sendHandle.data != IntPtr.Zero)
                    driver.AbortSend(sendHandle);
                return retval;
            }

            private unsafe int ProcessSendStage(int startStage, int internalBufferOffset, int internalSharedBufferOffset,
                PipelineImpl p, ref NativeList<int> resumeQ, ref NetworkPipelineContext ctx, ref InboundSendBuffer inboundBuffer, ref NetworkPipelineStage.Requests requests, int systemHeaderSize)
            {
                var stageIndex = p.FirstStageIndex + startStage;
                var pipelineStage = m_StageCollection[m_StageList[stageIndex]];
                ctx.accumulatedHeaderCapacity = m_AccumulatedHeaderCapacity[stageIndex];
                ctx.staticInstanceBuffer = (byte*)m_StaticInstanceBuffer.GetUnsafeReadOnlyPtr() + pipelineStage.StaticStateStart;
                ctx.staticInstanceBufferLength = pipelineStage.StaticStateCapcity;
                ctx.internalProcessBuffer = (byte*)sendBuffer.GetUnsafeReadOnlyPtr() + internalBufferOffset;
                ctx.internalProcessBufferLength = pipelineStage.SendCapacity;

                ctx.internalSharedProcessBuffer = (byte*)sharedBuffer.GetUnsafeReadOnlyPtr() + internalSharedBufferOffset;
                ctx.internalSharedProcessBufferLength = pipelineStage.SharedStateCapacity;

                requests = NetworkPipelineStage.Requests.None;
                var retval = pipelineStage.Send.Ptr.Invoke(ref ctx, ref inboundBuffer, ref requests, systemHeaderSize);
                if ((requests & NetworkPipelineStage.Requests.Resume) != 0)
                    resumeQ.Add(startStage);
                return retval;
            }
        }
        private NativeArray<NetworkPipelineStage> m_StageCollection;
        private NativeArray<byte> m_StaticInstanceBuffer;
        private NativeList<int> m_StageList;
        private NativeList<int> m_AccumulatedHeaderCapacity;
        private NativeList<PipelineImpl> m_Pipelines;
        private NativeList<byte> m_ReceiveBuffer;
        private NativeList<byte> m_SendBuffer;
        private NativeList<byte> m_SharedBuffer;
        private NativeList<UpdatePipeline> m_ReceiveStageNeedsUpdate;
        private NativeList<UpdatePipeline> m_SendStageNeedsUpdate;
        private NativeQueue<UpdatePipeline> m_SendStageNeedsUpdateRead;

        private NativeArray<int> sizePerConnection;

        private NativeArray<long> m_timestamp;

        private const int SendSizeOffset = 0;
        private const int RecveiveSizeOffset = 1;
        private const int SharedSizeOffset = 2;

        internal struct PipelineImpl
        {
            public int FirstStageIndex;
            public int NumStages;

            public int receiveBufferOffset;
            public int sendBufferOffset;
            public int sharedBufferOffset;
            public int headerCapacity;
            public int payloadCapacity;
        }

        public unsafe NetworkPipelineProcessor(NetworkSettings settings)
        {
            NetworkPipelineParams config = settings.GetPipelineParameters();

            int staticBufferSize = 0;
            for (int i = 0; i < NetworkPipelineStageCollection.m_stages.Count; ++i)
            {
                staticBufferSize += NetworkPipelineStageCollection.m_stages[i].StaticSize;
                staticBufferSize = (staticBufferSize + 15) & (~15);
            }
            m_StaticInstanceBuffer = new NativeArray<byte>(staticBufferSize, Allocator.Persistent);
            m_StageCollection = new NativeArray<NetworkPipelineStage>(NetworkPipelineStageCollection.m_stages.Count, Allocator.Persistent);
            staticBufferSize = 0;
            for (int i = 0; i < NetworkPipelineStageCollection.m_stages.Count; ++i)
            {
                var stageStruct = NetworkPipelineStageCollection.m_stages[i].StaticInitialize((byte*)m_StaticInstanceBuffer.GetUnsafePtr() + staticBufferSize, NetworkPipelineStageCollection.m_stages[i].StaticSize, settings);
                stageStruct.StaticStateStart = staticBufferSize;
                stageStruct.StaticStateCapcity = NetworkPipelineStageCollection.m_stages[i].StaticSize;
                m_StageCollection[i] = stageStruct;
                staticBufferSize += NetworkPipelineStageCollection.m_stages[i].StaticSize;
                staticBufferSize = (staticBufferSize + 15) & (~15);
            }

            m_StageList = new NativeList<int>(16, Allocator.Persistent);
            m_AccumulatedHeaderCapacity = new NativeList<int>(16, Allocator.Persistent);
            m_Pipelines = new NativeList<PipelineImpl>(16, Allocator.Persistent);
            m_ReceiveBuffer = new NativeList<byte>(config.initialCapacity, Allocator.Persistent);
            m_SendBuffer = new NativeList<byte>(config.initialCapacity, Allocator.Persistent);
            m_SharedBuffer = new NativeList<byte>(config.initialCapacity, Allocator.Persistent);
            sizePerConnection = new NativeArray<int>(3, Allocator.Persistent);
            // Store an int for the spinlock first in each connections send buffer, round up to alignment of 8
            sizePerConnection[SendSizeOffset] = Alignment;
            m_ReceiveStageNeedsUpdate = new NativeList<UpdatePipeline>(128, Allocator.Persistent);
            m_SendStageNeedsUpdate = new NativeList<UpdatePipeline>(128, Allocator.Persistent);
            m_SendStageNeedsUpdateRead = new NativeQueue<UpdatePipeline>(Allocator.Persistent);
            m_timestamp = new NativeArray<long>(1, Allocator.Persistent);
        }

        public void Dispose()
        {
            m_StageList.Dispose();
            m_AccumulatedHeaderCapacity.Dispose();
            m_ReceiveBuffer.Dispose();
            m_SendBuffer.Dispose();
            m_SharedBuffer.Dispose();
            m_Pipelines.Dispose();
            sizePerConnection.Dispose();
            m_ReceiveStageNeedsUpdate.Dispose();
            m_SendStageNeedsUpdate.Dispose();
            m_SendStageNeedsUpdateRead.Dispose();
            m_timestamp.Dispose();
            m_StageCollection.Dispose();
            m_StaticInstanceBuffer.Dispose();
        }

        public long Timestamp
        {
            get { return m_timestamp[0]; }
            internal set { m_timestamp[0] = value; }
        }

        public unsafe void initializeConnection(NetworkConnection con)
        {
            var requiredReceiveSize = (con.m_NetworkId + 1) * sizePerConnection[RecveiveSizeOffset];
            var requiredSendSize = (con.m_NetworkId + 1) * sizePerConnection[SendSizeOffset];
            var requiredSharedSize = (con.m_NetworkId + 1) * sizePerConnection[SharedSizeOffset];
            if (m_ReceiveBuffer.Length < requiredReceiveSize)
                m_ReceiveBuffer.ResizeUninitialized(requiredReceiveSize);
            if (m_SendBuffer.Length < requiredSendSize)
                m_SendBuffer.ResizeUninitialized(requiredSendSize);
            if (m_SharedBuffer.Length < requiredSharedSize)
                m_SharedBuffer.ResizeUninitialized(requiredSharedSize);

            UnsafeUtility.MemClear((byte*)m_ReceiveBuffer.GetUnsafePtr() + con.m_NetworkId * sizePerConnection[RecveiveSizeOffset], sizePerConnection[RecveiveSizeOffset]);
            UnsafeUtility.MemClear((byte*)m_SendBuffer.GetUnsafePtr() + con.m_NetworkId * sizePerConnection[SendSizeOffset], sizePerConnection[SendSizeOffset]);
            UnsafeUtility.MemClear((byte*)m_SharedBuffer.GetUnsafePtr() + con.m_NetworkId * sizePerConnection[SharedSizeOffset], sizePerConnection[SharedSizeOffset]);

            InitializeStages(con.m_NetworkId);
        }

        unsafe void InitializeStages(int networkId)
        {
            var connectionId = networkId;

            for (int i = 0; i < m_Pipelines.Length; i++)
            {
                var pipeline = m_Pipelines[i];

                int recvBufferOffset = pipeline.receiveBufferOffset + sizePerConnection[RecveiveSizeOffset] * connectionId;
                int sendBufferOffset = pipeline.sendBufferOffset + sizePerConnection[SendSizeOffset] * connectionId;
                int sharedBufferOffset = pipeline.sharedBufferOffset + sizePerConnection[SharedSizeOffset] * connectionId;

                for (int stage = pipeline.FirstStageIndex;
                     stage < pipeline.FirstStageIndex + pipeline.NumStages;
                     stage++)
                {
                    var pipelineStage = m_StageCollection[m_StageList[stage]];
                    var sendProcessBuffer = (byte*)m_SendBuffer.GetUnsafePtr() + sendBufferOffset;
                    var sendProcessBufferLength = pipelineStage.SendCapacity;
                    var recvProcessBuffer = (byte*)m_ReceiveBuffer.GetUnsafePtr() + recvBufferOffset;
                    var recvProcessBufferLength = pipelineStage.ReceiveCapacity;
                    var sharedProcessBuffer = (byte*)m_SharedBuffer.GetUnsafePtr() + sharedBufferOffset;
                    var sharedProcessBufferLength = pipelineStage.SharedStateCapacity;

                    var staticInstanceBuffer = (byte*)m_StaticInstanceBuffer.GetUnsafePtr() + pipelineStage.StaticStateStart;
                    var staticInstanceBufferLength = pipelineStage.StaticStateCapcity;
                    pipelineStage.InitializeConnection.Ptr.Invoke(staticInstanceBuffer, staticInstanceBufferLength,
                        sendProcessBuffer, sendProcessBufferLength, recvProcessBuffer, recvProcessBufferLength,
                        sharedProcessBuffer, sharedProcessBufferLength);

                    sendBufferOffset += (sendProcessBufferLength + AlignmentMinusOne) & (~AlignmentMinusOne);
                    recvBufferOffset += (recvProcessBufferLength + AlignmentMinusOne) & (~AlignmentMinusOne);
                    sharedBufferOffset += (sharedProcessBufferLength + AlignmentMinusOne) & (~AlignmentMinusOne);
                }
            }
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        private void ValidateStages(params Type[] stages)
        {
            var reliableIndex = Array.IndexOf(stages, typeof(ReliableSequencedPipelineStage));
            var fragmentedIndex = Array.IndexOf(stages, typeof(FragmentationPipelineStage));

            // Check that fragmentation doesn't follow the reliability pipeline. This order is not
            // supported since the reliability pipeline can't handle packets larger than the MTU.
            if (reliableIndex >= 0 && fragmentedIndex >= 0 && fragmentedIndex > reliableIndex)
                throw new InvalidOperationException("Cannot create pipeline with ReliableSequenced followed by Fragmentation stage. Should reverse their order.");
        }

        /// <summary>
        /// Create a new NetworkPipeline.
        /// </summary>
        /// <param name="stages">The stages we want the pipeline to contain.</param>
        /// <value>A valid pipeline is returned.</value>
        /// <exception cref="InvalidOperationException">Thrown if you try to create more then 255 pipelines.</exception>
        /// <exception cref="InvalidOperationException">Thrown if you try to use a invalid pipeline stage.</exception>
        public NetworkPipeline CreatePipeline(params Type[] stages)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (m_Pipelines.Length > 255)
                throw new InvalidOperationException("Cannot create more than 255 pipelines on a single driver");
            ValidateStages(stages);
#endif
            var receiveCap = 0;
            var sharedCap = 0;
            var sendCap = 0;
            var headerCap = 0;
            var payloadCap = 0;
            var pipeline = new PipelineImpl();
            pipeline.FirstStageIndex = m_StageList.Length;
            pipeline.NumStages = stages.Length;
            for (int i = 0; i < stages.Length; i++)
            {
                var stageId = NetworkPipelineStageCollection.GetStageId(stages[i]).Index;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (stageId < 0)
                    throw new InvalidOperationException("Trying to create pipeline with invalid stage " + stages[i]);
#endif
                m_StageList.Add(stageId);
                m_AccumulatedHeaderCapacity.Add(headerCap);    // For every stage, compute how much header space has already bee used by other stages when sending
                // Make sure all data buffers are aligned
                receiveCap += (m_StageCollection[stageId].ReceiveCapacity + AlignmentMinusOne) & (~AlignmentMinusOne);
                sendCap += (m_StageCollection[stageId].SendCapacity + AlignmentMinusOne) & (~AlignmentMinusOne);
                headerCap += m_StageCollection[stageId].HeaderCapacity;
                sharedCap += (m_StageCollection[stageId].SharedStateCapacity + AlignmentMinusOne) & (~AlignmentMinusOne);
                if (payloadCap == 0)
                {
                    payloadCap = m_StageCollection[stageId].PayloadCapacity; // The first non-zero stage determines the pipeline capacity
                }
            }

            pipeline.receiveBufferOffset = sizePerConnection[RecveiveSizeOffset];
            sizePerConnection[RecveiveSizeOffset] = sizePerConnection[RecveiveSizeOffset] + receiveCap;

            pipeline.sendBufferOffset = sizePerConnection[SendSizeOffset];
            sizePerConnection[SendSizeOffset] = sizePerConnection[SendSizeOffset] + sendCap;

            pipeline.sharedBufferOffset = sizePerConnection[SharedSizeOffset];
            sizePerConnection[SharedSizeOffset] = sizePerConnection[SharedSizeOffset] + sharedCap;

            pipeline.headerCapacity = headerCap;
            pipeline.payloadCapacity = payloadCap;

            m_Pipelines.Add(pipeline);
            return new NetworkPipeline {Id = m_Pipelines.Length};
        }

        public void GetPipelineBuffers(NetworkPipeline pipelineId, NetworkPipelineStageId stageId, NetworkConnection connection,
            out NativeArray<byte> readProcessingBuffer, out NativeArray<byte> writeProcessingBuffer,
            out NativeArray<byte> sharedBuffer)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (pipelineId.Id < 1)
                throw new InvalidOperationException("The specified pipeline is not valid");
            if (stageId.IsValid == 0)
                throw new InvalidOperationException("The specified pipeline stage is not valid");
#else
            if (pipelineId.Id < 1 || stageId.IsValid == 0)
            {
                writeProcessingBuffer = default;
                readProcessingBuffer = default;
                sharedBuffer = default;
                return;
            }
#endif
            var pipeline = m_Pipelines[pipelineId.Id - 1];

            int recvBufferOffset = pipeline.receiveBufferOffset + sizePerConnection[RecveiveSizeOffset] * connection.InternalId;
            int sendBufferOffset = pipeline.sendBufferOffset + sizePerConnection[SendSizeOffset] * connection.InternalId;
            int sharedBufferOffset = pipeline.sharedBufferOffset + sizePerConnection[SharedSizeOffset] * connection.InternalId;

            int stageIndexInList;
            bool stageNotFound = true;
            for (stageIndexInList = pipeline.FirstStageIndex;
                 stageIndexInList < pipeline.FirstStageIndex + pipeline.NumStages;
                 stageIndexInList++)
            {
                if (m_StageList[stageIndexInList] == stageId.Index)
                {
                    stageNotFound = false;
                    break;
                }
                sendBufferOffset += (m_StageCollection[m_StageList[stageIndexInList]].SendCapacity + AlignmentMinusOne) & (~AlignmentMinusOne);
                recvBufferOffset += (m_StageCollection[m_StageList[stageIndexInList]].ReceiveCapacity + AlignmentMinusOne) & (~AlignmentMinusOne);
                sharedBufferOffset += (m_StageCollection[m_StageList[stageIndexInList]].SharedStateCapacity + AlignmentMinusOne) & (~AlignmentMinusOne);
            }

            if (stageNotFound)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                throw new InvalidOperationException($"Could not find stage ID {stageId} make sure the type for this stage ID is added when the pipeline is created.");
#else
                writeProcessingBuffer = default;
                readProcessingBuffer = default;
                sharedBuffer = default;
                return;
#endif
            }

            writeProcessingBuffer = ((NativeArray<byte>)m_SendBuffer).GetSubArray(sendBufferOffset, m_StageCollection[m_StageList[stageIndexInList]].SendCapacity);
            readProcessingBuffer = ((NativeArray<byte>)m_ReceiveBuffer).GetSubArray(recvBufferOffset, m_StageCollection[m_StageList[stageIndexInList]].ReceiveCapacity);
            sharedBuffer = ((NativeArray<byte>)m_SharedBuffer).GetSubArray(sharedBufferOffset, m_StageCollection[m_StageList[stageIndexInList]].SharedStateCapacity);
        }

        internal struct UpdatePipeline
        {
            public NetworkPipeline pipeline;
            public int stage;
            public NetworkConnection connection;
        }

        internal unsafe void UpdateSend(NetworkDriver.Concurrent driver, out int updateCount)
        {
            // Clear the send lock since it cannot be kept here and can be lost if there are exceptions in send
            NativeArray<byte> tmpBuffer = m_SendBuffer;
            int* sendBufferLock = (int*)tmpBuffer.GetUnsafePtr();
            for (int connectionOffset = 0; connectionOffset < m_SendBuffer.Length; connectionOffset += sizePerConnection[SendSizeOffset])
                sendBufferLock[connectionOffset / 4] = 0;

            NativeArray<UpdatePipeline> sendUpdates = new NativeArray<UpdatePipeline>(m_SendStageNeedsUpdateRead.Count + m_SendStageNeedsUpdate.Length, Allocator.Temp);

            UpdatePipeline updateItem;
            updateCount = 0;
            while (m_SendStageNeedsUpdateRead.TryDequeue(out updateItem))
            {
                if (driver.GetConnectionState(updateItem.connection) == NetworkConnection.State.Connected)
                    sendUpdates[updateCount++] = updateItem;
            }

            int startLength = updateCount;
            for (int i = 0; i < m_SendStageNeedsUpdate.Length; i++)
            {
                if (driver.GetConnectionState(m_SendStageNeedsUpdate[i].connection) == NetworkConnection.State.Connected)
                    sendUpdates[updateCount++] = m_SendStageNeedsUpdate[i];
            }

            NativeList<UpdatePipeline> currentUpdates = new NativeList<UpdatePipeline>(128, Allocator.Temp);
            // Move the updates requested in this iteration to the concurrent queue so it can be read/parsed in update routine
            for (int i = 0; i < updateCount; ++i)
            {
                updateItem = sendUpdates[i];
                var result = ToConcurrent().ProcessPipelineSend(driver, updateItem.stage, updateItem.pipeline, updateItem.connection, default, 0, currentUpdates);
                if (result < 0)
                {
                    UnityEngine.Debug.LogWarning(FixedString.Format("ProcessPipelineSend failed with the following error code {0}.", result));
                }
            }
            for (int i = 0; i < currentUpdates.Length; ++i)
                m_SendStageNeedsUpdateRead.Enqueue(currentUpdates[i]);
        }

        private static void AddSendUpdate(NetworkConnection connection, int stageId, NetworkPipeline pipelineId, NativeList<UpdatePipeline> currentUpdates)
        {
            var newUpdate = new UpdatePipeline
            {connection = connection, stage = stageId, pipeline = pipelineId};
            bool uniqueItem = true;
            for (int j = 0; j < currentUpdates.Length; ++j)
            {
                if (currentUpdates[j].stage == newUpdate.stage &&
                    currentUpdates[j].pipeline.Id == newUpdate.pipeline.Id &&
                    currentUpdates[j].connection == newUpdate.connection)
                    uniqueItem = false;
            }
            if (uniqueItem)
                currentUpdates.Add(newUpdate);
        }

        public void UpdateReceive(NetworkDriver driver, out int updateCount)
        {
            NativeArray<UpdatePipeline> receiveUpdates = new NativeArray<UpdatePipeline>(m_ReceiveStageNeedsUpdate.Length, Allocator.Temp);

            // Move current update requests to a new queue
            updateCount = 0;
            for (int i = 0; i < m_ReceiveStageNeedsUpdate.Length; ++i)
            {
                if (driver.GetConnectionState(m_ReceiveStageNeedsUpdate[i].connection) == NetworkConnection.State.Connected)
                    receiveUpdates[updateCount++] = m_ReceiveStageNeedsUpdate[i];
            }
            m_ReceiveStageNeedsUpdate.Clear();

            // Process all current requested updates, new update requests will (possibly) be generated from the pipeline stages
            for (int i = 0; i < updateCount; ++i)
            {
                UpdatePipeline updateItem = receiveUpdates[i];
                ProcessReceiveStagesFrom(driver, updateItem.stage, updateItem.pipeline, updateItem.connection, default);
            }
        }

        public unsafe void Receive(NetworkDriver driver, NetworkConnection connection, NativeArray<byte> buffer)
        {
            byte pipelineId = buffer[0];
            if (pipelineId == 0 || pipelineId > m_Pipelines.Length)
            {
                UnityEngine.Debug.LogError("Received a packet with an invalid pipeline.");
                return;
            }
            var p = m_Pipelines[pipelineId - 1];
            int startStage = p.NumStages - 1;

            InboundRecvBuffer inBuffer;
            inBuffer.buffer = (byte*)buffer.GetUnsafePtr() + 1;
            inBuffer.bufferLength = buffer.Length - 1;
            ProcessReceiveStagesFrom(driver, startStage, new NetworkPipeline {Id = pipelineId}, connection, inBuffer);
        }

        private unsafe void ProcessReceiveStagesFrom(NetworkDriver driver, int startStage, NetworkPipeline pipeline,
            NetworkConnection connection, InboundRecvBuffer buffer)
        {
            var p = m_Pipelines[pipeline.Id - 1];
            var connectionId = connection.m_NetworkId;
            var resumeQ = new NativeList<int>(16, Allocator.Temp);
            int resumeQStart = 0;

            var systemHeaderSize = driver.MaxProtocolHeaderSize();

            var inboundBuffer = buffer;

            var ctx = new NetworkPipelineContext
            {
                timestamp = Timestamp,
                header = default
            };

            while (true)
            {
                bool needsUpdate = false;
                bool needsSendUpdate = false;
                int internalBufferOffset = p.receiveBufferOffset + sizePerConnection[RecveiveSizeOffset] * connectionId;
                int internalSharedBufferOffset = p.sharedBufferOffset + sizePerConnection[SharedSizeOffset] * connectionId;

                // Adjust offset accounting for stages in front of the starting stage, since we're parsing the stages in reverse order
                for (int st = 0; st < startStage; ++st)
                {
                    internalBufferOffset += (m_StageCollection[m_StageList[p.FirstStageIndex + st]].ReceiveCapacity + AlignmentMinusOne) & (~AlignmentMinusOne);
                    internalSharedBufferOffset += (m_StageCollection[m_StageList[p.FirstStageIndex + st]].SharedStateCapacity + AlignmentMinusOne) & (~AlignmentMinusOne);
                }

                for (int i = startStage; i >= 0; --i)
                {
                    ProcessReceiveStage(i, pipeline, internalBufferOffset, internalSharedBufferOffset, ref ctx, ref inboundBuffer, ref resumeQ, ref needsUpdate, ref needsSendUpdate, systemHeaderSize);
                    if (needsUpdate)
                    {
                        var newUpdate = new UpdatePipeline
                        {connection = connection, stage = i, pipeline = pipeline};
                        bool uniqueItem = true;
                        for (int j = 0; j < m_ReceiveStageNeedsUpdate.Length; ++j)
                        {
                            if (m_ReceiveStageNeedsUpdate[j].stage == newUpdate.stage &&
                                m_ReceiveStageNeedsUpdate[j].pipeline.Id == newUpdate.pipeline.Id &&
                                m_ReceiveStageNeedsUpdate[j].connection == newUpdate.connection)
                                uniqueItem = false;
                        }
                        if (uniqueItem)
                            m_ReceiveStageNeedsUpdate.Add(newUpdate);
                    }

                    if (needsSendUpdate)
                        AddSendUpdate(connection, i, pipeline, m_SendStageNeedsUpdate);

                    if (inboundBuffer.bufferLength == 0)
                        break;

                    // Offset needs to be adjusted for the next pipeline (the one in front of this one)
                    if (i > 0)
                    {
                        internalBufferOffset -=
                            (m_StageCollection[m_StageList[p.FirstStageIndex + i - 1]].ReceiveCapacity + AlignmentMinusOne) & (~AlignmentMinusOne);
                        internalSharedBufferOffset -=
                            (m_StageCollection[m_StageList[p.FirstStageIndex + i - 1]].SharedStateCapacity + AlignmentMinusOne) & (~AlignmentMinusOne);
                    }

                    needsUpdate = false;
                }

                if (inboundBuffer.bufferLength != 0)
                    driver.PushDataEvent(connection, pipeline.Id, inboundBuffer.buffer, inboundBuffer.bufferLength);

                if (resumeQStart >= resumeQ.Length)
                {
                    return;
                }

                startStage = resumeQ[resumeQStart++];
                inboundBuffer = default;
            }
        }

        private unsafe void ProcessReceiveStage(int stage, NetworkPipeline pipeline, int internalBufferOffset,
            int internalSharedBufferOffset, ref NetworkPipelineContext ctx, ref InboundRecvBuffer inboundBuffer,
            ref NativeList<int> resumeQ, ref bool needsUpdate, ref bool needsSendUpdate, int systemHeadersSize)
        {
            var p = m_Pipelines[pipeline.Id - 1];

            var stageId = m_StageList[p.FirstStageIndex + stage];
            var pipelineStage = m_StageCollection[stageId];
            ctx.staticInstanceBuffer = (byte*)m_StaticInstanceBuffer.GetUnsafePtr() + pipelineStage.StaticStateStart;
            ctx.staticInstanceBufferLength = pipelineStage.StaticStateCapcity;
            ctx.internalProcessBuffer = (byte*)m_ReceiveBuffer.GetUnsafePtr() + internalBufferOffset;
            ctx.internalProcessBufferLength = pipelineStage.ReceiveCapacity;
            ctx.internalSharedProcessBuffer = (byte*)m_SharedBuffer.GetUnsafePtr() + internalSharedBufferOffset;
            ctx.internalSharedProcessBufferLength = pipelineStage.SharedStateCapacity;
            NetworkPipelineStage.Requests requests = NetworkPipelineStage.Requests.None;

            pipelineStage.Receive.Ptr.Invoke(ref ctx, ref inboundBuffer, ref requests, systemHeadersSize);

            if ((requests & NetworkPipelineStage.Requests.Resume) != 0)
                resumeQ.Add(stage);
            needsUpdate = (requests & NetworkPipelineStage.Requests.Update) != 0;
            needsSendUpdate = (requests & NetworkPipelineStage.Requests.SendUpdate) != 0;
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public static void ValidateSendHandle(NetworkInterfaceSendHandle handle)
        {
            if (handle.data == IntPtr.Zero)
                throw new ArgumentException($"Value for NetworkDataStreamParameter.size must be larger then zero.");
        }
    }
}
