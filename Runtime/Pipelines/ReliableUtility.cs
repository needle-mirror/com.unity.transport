using System;
using System.Runtime.InteropServices;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Networking.Transport.Logging;

namespace Unity.Networking.Transport.Utilities
{
    /// <summary>Extensions for <see cref="ReliableUtility.Parameters"/>.</summary>
    public static class ReliableStageParameterExtensions
    {
        private const int k_DefaultWindowSize = 32;

        /// <summary>
        /// Sets the <see cref="ReliableUtility.Parameters"/> in the settings.
        /// </summary>
        /// <param name="settings">Settings to modify.</param>
        /// <param name="windowSize">
        /// Maximum number in-flight packets per pipeline/connection combination. Default value
        /// is 32 but can be increased to 64 at the cost of slightly larger packet headers.
        /// </param>
        /// <param name="minimumResendTime">
        /// Minimum amount of time to wait before a reliable packet is resent if it's not been
        /// acknowledged. Default value is 64ms, which should be set lower if all connections
        /// are expected to be very good (RTT of 25ms or less).
        /// </param>
        /// <param name="maximumResendTime">
        /// Maximum amount of time to wait before a reliable packet is resent if it's not been
        /// acknowledged. Default value is 200ms, which should be set higher if all connections
        /// are expected to have a higher latency than that.
        /// </param>
        /// <returns>Settings structure with modified values.</returns>
        public static ref NetworkSettings WithReliableStageParameters(
            ref this NetworkSettings settings,
            int windowSize = k_DefaultWindowSize,
            int minimumResendTime = ReliableUtility.DefaultMinimumResendTime,
            int maximumResendTime = ReliableUtility.DefaultMaximumResendTime
        )
        {
            var parameter = new ReliableUtility.Parameters
            {
                WindowSize = windowSize,
                MinimumResendTime = minimumResendTime,
                MaximumResendTime = maximumResendTime,
            };

            settings.AddRawParameterStruct(ref parameter);

            return ref settings;
        }

        /// <summary>
        /// Gets the <see cref="ReliableUtility.Parameters"/> in the settings.
        /// </summary>
        /// <param name="settings">Settings to get parameters from.</param>
        /// <returns>Structure containing the reliable parameters.</returns>
        public static ReliableUtility.Parameters GetReliableStageParameters(ref this NetworkSettings settings)
        {
            if (!settings.TryGet<ReliableUtility.Parameters>(out var parameters))
            {
                parameters.WindowSize = k_DefaultWindowSize;
                parameters.MinimumResendTime = ReliableUtility.DefaultMinimumResendTime;
                parameters.MaximumResendTime = ReliableUtility.DefaultMaximumResendTime;
            }

            return parameters;
        }
    }

    /// <summary>
    /// Utility types, methods, and constants for the <see cref="ReliableSequencedPipelineStage"/>.
    /// </summary>
    public struct ReliableUtility
    {
        /// <summary>Statistics tracked internally by the reliable pipeline stage.</summary>
        public struct Statistics
        {
            /// <summary>Number of packets received by the pipeline stage.</summary>
            /// <value>Number of packets.</value>
            public int PacketsReceived;

            /// <summary>Number of packets sent by the pipeline stage.</summary>
            /// <value>Number of packets.</value>
            public int PacketsSent;

            /// <summary>Number of packets that were dropped in transit.</summary>
            /// <value>Number of packets.</value>
            public int PacketsDropped;

            /// <summary>Number of packets that arrived out of order.</summary>
            /// <value>Number of packets.</value>
            public int PacketsOutOfOrder;

            /// <summary>Number of duplicated packets received by the pipeline stage.</summary>
            /// <remarks>Doesn't differentiate between real duplicates and resends.</remarks>
            /// <value>Number of packets.</value>
            public int PacketsDuplicated;

            /// <summary>Number of stale packets received by the pipeline stage.</summary>
            /// <value>Number of packets.</value>
            public int PacketsStale;

            /// <summary>Number of packets resent by the pipeline stage.</summary>
            /// <value>Number of packets.</value>
            public int PacketsResent;
        }

        /// <summary>
        /// RTT (round-trip time) information tracked internally by the reliable pipeline stage. Can
        /// be read from the <see cref="SharedContext"/>, which itself can be obtained through the
        /// <see cref="NetworkDriver.GetPipelineBuffers"/> method.
        /// </summary>
        public struct RTTInfo
        {
            /// <summary>RTT of the last packet acknowledged.</summary>
            /// <value>RTT in milliseconds.</value>
            public int LastRtt;

            /// <summary>Smoothed RTT of the last packets acknowledged.</summary>
            /// <value>Smoothed RTT in milliseconds.</value>
            public float SmoothedRtt;

            /// <summary>Variance of the smoothed RTT.</summary>
            /// <value>Smoothed RTT variance in milliseconds.</value>
            public float SmoothedVariance;

            /// <summary>Timeout used to resend unacknowledged packets.</summary>
            /// <value>Resend timeout in milliseconds.</value>
            public int ResendTimeout;
        }

        internal const long NullEntry = -1;

        /// <summary>
        /// Minimum amount of time to wait before a reliable packet is resent if it's not been
        /// acknowledged. Can be modified at runtime with <see cref="SetMinimumResendTime"/>, or
        /// configured when creating a driver through custom <see cref="NetworkSettings"/>.
        /// </summary>
        /// <value>Minimum resend time in milliseconds.</value>
        public const int DefaultMinimumResendTime = 64;

        /// <summary>
        /// Maximum amount of time to wait before a reliable packet is resent if it's not been
        /// acknowledged. That is, even with a high RTT the reliable pipeline will never wait
        /// longer than this value to resend a packet. Can be modified at runtime with
        /// <see cref="SetMaximumResendTime"/> or configured when creating a driver through
        /// custom <see cref="NetworkSettings"/>.
        /// </summary>
        /// <value>Maximum resend time in milliseconds.</value>
        public const int DefaultMaximumResendTime = 200;

        /// <summary>Obsolete. Renamed to <c>DefaultMaximumResendTime</c>.</summary>
        [Obsolete("Renamed to DefaultMaximumResendTime. (UnityUpgradable) -> DefaultMaximumResendTime")]
        public const int MaximumResendTime = DefaultMaximumResendTime;

        // If we receive 3 duplicates AFTER our last send, then it's more likely that one of our
        // ACKs was lost and the remote is trying to resend us a packet we won't acknowledge.
        internal const int MaxDuplicatesSinceLastAck = 3;

        internal enum PacketType : ushort
        {
            Payload = 0,
            Ack = 1
        }

        internal struct SequenceBufferContext
        {
            public long Sequence;
            public long Acked;
            public ulong AckMask;
            public ulong LastAckMask;

            internal ushort LastReceivedOverflowCycle => (ushort)(NumberOfOverflowsDetected & 0b11);
            internal long NumberOfOverflowsDetected;
        }

        /// <summary>
        /// Context shared by both the send and receive direction of the reliable pipeline. Can be
        /// obtained through the last parameter of <see cref="NetworkDriver.GetPipelineBuffers"/>.
        /// </summary>
        public struct SharedContext
        {
            // Context for sent packets: last sequence ID sent, last ID we sent that was acked by
            // the remote peer, and acknowlede mask of acknowledged packets. Used to determine if
            // resends are needed.
            internal SequenceBufferContext SentPackets;

            // Context for received packets: last sequence ID received, and acknowledge mask of
            // received packets. Sent back to the remote peer in header when sending.
            internal SequenceBufferContext ReceivedPackets;

            internal int DuplicatesSinceLastAck;

            /// <summary>
            /// Effective window size of the reliable pipeline. Can't be modified at runtime, but
            /// can be set at configuration time through <see cref="NetworkSettings"/>.
            /// </summary>
            /// <value>Window size in number of packets.</value>
            public int WindowSize;

            /// <summary>
            /// Effective minimum resend time for unacknowledged reliable packets. Can be modified
            /// through <see cref="ReliableUtility.SetMinimumResendTime"/>.
            /// </summary>
            /// <value>Minimum resend time in milliseconds.</value>
            public int MinimumResendTime;

            /// <summary>
            /// Effective maximum resend time for unacknowledged reliable packets. Can be modified
            /// through <see cref="ReliableUtility.SetMaximumResendTime"/>.
            /// </summary>
            /// <value>Minimum resend time in milliseconds.</value>
            public int MaximumResendTime;

            /// <summary>Statistics for the reliable pipeline.</summary>
            /// <value>Reliable pipeline statistics.</value>
            public Statistics stats;

            /// <summary>Timing information used to calculate resend timeouts.</summary>
            /// <value>RTT information.</value>
            public RTTInfo RttInfo;

            internal int TimerDataOffset;
            internal int TimerDataStride;
            internal int RemoteTimerDataOffset;
            internal int RemoteTimerDataStride;
        }

        internal struct Context
        {
            public int Capacity;
            public long Resume;
            public long Delivered;
            public int IndexStride;
            public int IndexPtrOffset;
            public int DataStride;
            public int DataPtrOffset;
        }

        /// <summary>Parameters for the <see cref="ReliableSequencedPipelineStage"/>.</summary>
        [Serializable]
        public struct Parameters : INetworkParameter
        {
            /// <summary>
            /// Maximum number in-flight packets per pipeline/connection combination. Default value
            /// is 32 but can be increased to 64 at the cost of slightly larger packet headers.
            /// </summary>
            /// <value>Window size in number of packets.</value>
            public int WindowSize;

            /// <summary>
            /// Minimum amount of time to wait before a reliable packet is resent if it's not been
            /// acknowledged. Default value is 64ms, which should be set lower if all connections
            /// are expected to be very good (RTT of 25ms or less).
            /// </summary>
            /// <value>Minimum resend time in milliseconds.</value>
            public int MinimumResendTime;

            /// <summary>
            /// Maximum amount of time to wait before a reliable packet is resent if it's not been
            /// acknowledged. Default value is 200ms, which should be set higher if all connections
            /// are expected to have a higher latency than that.
            /// </summary>
            /// <value>Maximum resend time in milliseconds.</value>
            public int MaximumResendTime;

            /// <inheritdoc/>
            public bool Validate()
            {
                var valid = true;

                if (WindowSize < 0 || WindowSize > 64)
                {
                    valid = false;
                    DebugLog.LogError($"{nameof(WindowSize)} value ({WindowSize}) must be between 0 and 64.");
                }

                if (MinimumResendTime <= 0)
                {
                    valid = false;
                    DebugLog.LogError($"{nameof(MinimumResendTime)} value ({MinimumResendTime}) must be positive.");
                }

                if (MaximumResendTime <= 0)
                {
                    valid = false;
                    DebugLog.LogError($"{nameof(MaximumResendTime)} value ({MaximumResendTime}) must be positive.");
                }

                if (MaximumResendTime <= MinimumResendTime)
                {
                    valid = false;
                    DebugLog.LogError($"{nameof(MaximumResendTime)} must be greater than {nameof(MinimumResendTime)}");
                }

                return valid;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PacketHeader
        {
            public ushort Type;
            public ushort ProcessingTime;
            public ushort SequenceId;
            public ushort AckedSequenceId;
            // This must be the last member in the packet header, since we truncate it for smaller window sizes.
            public ulong AckMask;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PacketInformation
        {
            public long SequenceId;
            public ushort Size;
            public ushort HeaderPadding;
            public long SendTime;
        }

        // Header is inside the total packet length (Buffer size)
        [StructLayout(LayoutKind.Explicit)]
        internal unsafe struct Packet
        {
            // Have to add an extra 4 bytes in there to account for the fact that parts of the
            // header will be unused if window size is 32 or less. We could do away with this hack
            // by correcting the offsets everywhere else in the code, but that's tricky.
            internal const int Length = NetworkParameterConstants.AbsoluteMaxMessageSize + sizeof(uint);
            [FieldOffset(0)] public PacketHeader Header;
            [FieldOffset(0)] public fixed byte Buffer[Length];
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PacketTimers
        {
            public ushort ProcessingTime;
            public ushort Padding;
            public long SequenceId;
            public long SentTime;
            public long ReceiveTime;
        }

        private static int AlignedSizeOf<T>() where T : struct
        {
            return (UnsafeUtility.SizeOf<T>() + NetworkPipelineProcessor.AlignmentMinusOne) & (~NetworkPipelineProcessor.AlignmentMinusOne);
        }

        internal static int PacketHeaderWireSize(int windowSize)
        {
            var fullHeaderSize = UnsafeUtility.SizeOf<PacketHeader>();
            return windowSize > 32 ? fullHeaderSize : fullHeaderSize - sizeof(uint);
        }

        internal static unsafe int PacketHeaderWireSize(NetworkPipelineContext ctx)
        {
            var reliable = (SharedContext*)ctx.internalSharedProcessBuffer;
            var windowSize = reliable->WindowSize;
            return PacketHeaderWireSize(windowSize);
        }

        internal static int SharedCapacityNeeded(Parameters param)
        {
            // Timers are stored for both remote packets (processing time) and local packets (round trip time)
            // The amount of timestamps needed in the queues is the same as the window size capacity
            var timerDataSize = AlignedSizeOf<PacketTimers>() * param.WindowSize * 2;
            var capacityNeeded = AlignedSizeOf<SharedContext>() + timerDataSize;

            return capacityNeeded;
        }

        internal static int ProcessCapacityNeeded(Parameters param)
        {
            var infoSize = AlignedSizeOf<PacketInformation>();
            var dataSize = (Packet.Length + NetworkPipelineProcessor.AlignmentMinusOne) & (~NetworkPipelineProcessor.AlignmentMinusOne);
            infoSize *= param.WindowSize;
            dataSize *= param.WindowSize;

            var capacityNeeded = AlignedSizeOf<Context>() + infoSize + dataSize;

            return capacityNeeded;
        }

        internal static unsafe SharedContext InitializeContext(byte* sharedBuffer, int sharedBufferLength,
            byte* sendBuffer, int sendBufferLength, byte* recvBuffer, int recvBufferLength, Parameters param)
        {
            InitializeProcessContext(sendBuffer, sendBufferLength, param);
            InitializeProcessContext(recvBuffer, recvBufferLength, param);

            SharedContext* notifier = (SharedContext*)sharedBuffer;
            *notifier = new SharedContext
            {
                WindowSize = param.WindowSize,
                SentPackets = new SequenceBufferContext { Acked = NullEntry, AckMask = 0ul },
                MinimumResendTime = param.MinimumResendTime,
                MaximumResendTime = param.MaximumResendTime,
                ReceivedPackets = new SequenceBufferContext { Sequence = NullEntry, AckMask = 0ul, LastAckMask = 0ul },
                RttInfo = new RTTInfo { SmoothedVariance = 5, SmoothedRtt = 50, ResendTimeout = 50, LastRtt = 50 },
                TimerDataOffset = AlignedSizeOf<SharedContext>(),
                TimerDataStride = AlignedSizeOf<PacketTimers>(),
                RemoteTimerDataOffset = AlignedSizeOf<SharedContext>() + AlignedSizeOf<PacketTimers>() * param.WindowSize,
                RemoteTimerDataStride = AlignedSizeOf<PacketTimers>()
            };
            return *notifier;
        }

        internal static unsafe int InitializeProcessContext(byte* buffer, int bufferLength, Parameters param)
        {
            int totalCapacity = ProcessCapacityNeeded(param);
            if (bufferLength != totalCapacity)
            {
                // That should really never happen. Don't know why we even bother checking it.
                throw new InvalidOperationException("Insufficient memory to initialize reliable pipeline.");
            }
            Context* ctx = (Context*)buffer;

            ctx->Capacity = param.WindowSize;
            ctx->IndexStride = AlignedSizeOf<PacketInformation>();
            ctx->IndexPtrOffset = AlignedSizeOf<Context>();
            ctx->DataStride = (Packet.Length + NetworkPipelineProcessor.AlignmentMinusOne) & (~NetworkPipelineProcessor.AlignmentMinusOne);
            ctx->DataPtrOffset = ctx->IndexPtrOffset + (ctx->IndexStride * ctx->Capacity);
            ctx->Resume = NullEntry;
            ctx->Delivered = NullEntry;

            Release(buffer, 0, param.WindowSize);
            return 0;
        }

        internal static unsafe void SetPacket(byte* self, long sequence, InboundRecvBuffer data)
        {
            SetPacket(self, sequence, data.buffer, data.bufferLength);
        }

        internal static unsafe void SetPacket(byte* self, long sequence, void* data, int length)
        {
            Context* ctx = (Context*)self;

            if (length > ctx->DataStride)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                throw new OverflowException();
#else
                return;
#endif
            }

            var index = sequence % ctx->Capacity;

            PacketInformation* info = GetPacketInformation(self, sequence);
            info->SequenceId = sequence;
            info->Size = (ushort)length;
            info->HeaderPadding = 0;      // Not used for packets queued for resume receive
            info->SendTime = -1;          // Not used for packets queued for resume receive

            var offset = ctx->DataPtrOffset + (index * ctx->DataStride);
            void* dataPtr = (self + offset);

            UnsafeUtility.MemCpy(dataPtr, data, length);
        }

        /// <summary>
        /// Write packet, packet header and tracking information to the given buffer space. This buffer
        /// should contain the reliability Context at the front, that contains the capacity of the buffer
        /// and pointer offsets needed to find the slots we can copy the packet to.
        /// </summary>
        /// <param name="self">Buffer space where we can store packets.</param>
        /// <param name="sequence">The sequence ID of the packet, this is used to find a slot inside the buffer.</param>
        /// <param name="header">The packet header which we'll store with the packet payload.</param>
        /// <param name="data">The packet data which we're storing.</param>
        /// <exception cref="OverflowException"></exception>
        internal static unsafe void SetHeaderAndPacket(byte* self, long sequence, PacketHeader header, InboundSendBuffer data, long timestamp)
        {
            Context* ctx = (Context*)self;
            int totalSize = data.bufferLength + data.headerPadding;

            if (totalSize > ctx->DataStride)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                throw new OverflowException();
#else
                return;
#endif
            }

            var index = sequence % ctx->Capacity;

            PacketInformation* info = GetPacketInformation(self, sequence);
            info->SequenceId = sequence;
            info->Size = (ushort)totalSize;
            info->HeaderPadding = (ushort)data.headerPadding;
            info->SendTime = timestamp;

            Packet* packet = GetPacket(self, sequence);
            packet->Header = header;
            var offset = (ctx->DataPtrOffset + (index * ctx->DataStride));
            void* dataPtr = (self + offset);

            if (data.bufferLength > 0)
                UnsafeUtility.MemCpy((byte*)dataPtr + data.headerPadding, data.buffer, data.bufferLength);
        }

        internal static unsafe PacketInformation* GetPacketInformation(byte* self, long sequence)
        {
            Context* ctx = (Context*)self;
            var index = sequence % ctx->Capacity;

            return (PacketInformation*)((self + ctx->IndexPtrOffset) + (index * ctx->IndexStride));
        }

        internal static unsafe Packet* GetPacket(byte* self, long sequence)
        {
            Context* ctx = (Context*)self;
            var index = sequence % ctx->Capacity;

            var offset = ctx->DataPtrOffset + (index * ctx->DataStride);
            return (Packet*)(self + offset);
        }

        internal static unsafe bool TryAquire(byte* self, long sequence)
        {
            Context* ctx = (Context*)self;

            var index = sequence % ctx->Capacity;

            var currentSequenceId = GetIndex(self, index);
            if (currentSequenceId == NullEntry)
            {
                SetIndex(self, index, sequence);
                return true;
            }
            return false;
        }

        internal static unsafe void Release(byte* self, long sequence)
        {
            Release(self, sequence, 1);
        }

        internal static unsafe void Release(byte* self, long start_sequence, int count)
        {
            Context* ctx = (Context*)self;
            for (int i = 0; i < count; i++)
            {
                SetIndex(self, (start_sequence + i) % ctx->Capacity, NullEntry);
            }
        }

        private static unsafe void SetIndex(byte* self, long index, long sequence)
        {
            Context* ctx = (Context*)self;

            long* value = (long*)((self + ctx->IndexPtrOffset) + (index * ctx->IndexStride));
            *value = sequence;
        }

        private static unsafe long GetIndex(byte* self, long index)
        {
            Context* ctx = (Context*)self;

            long* value = (long*)((self + ctx->IndexPtrOffset) + (index * ctx->IndexStride));
            return *value;
        }

        /// <summary>Release packets which have been acknowledged by the remote.</summary>
        internal static unsafe void ReleaseAcknowledgedPackets(NetworkPipelineContext context)
        {
            SharedContext* reliable = (SharedContext*)context.internalSharedProcessBuffer;

            // Last sequence ID and ackmask we received from the remote peer.
            var lastReceivedAckMask = reliable->SentPackets.AckMask;
            var lastOwnSequenceIdAckedByRemote = reliable->SentPackets.Acked;

            // Check each slot in the window for acknowledged packets.
            for (int i = 0; i < reliable->WindowSize; i++)
            {
                var info = GetPacketInformation(context.internalProcessBuffer, i);
                if (info->SequenceId >= 0)
                {
                    // Don't release anything greater than the last packet acknowledged.
                    if (info->SequenceId > lastOwnSequenceIdAckedByRemote)
                        continue;

                    var distance = math.abs(lastOwnSequenceIdAckedByRemote - info->SequenceId);

                    // Distance being greater than window size shouldn't happen, but release the
                    // packet anyway since it must have been acknowledged by now. Otherwise check
                    // the ackmask to see if the packet was acknowledged.
                    if (distance >= reliable->WindowSize || ((1ul << (int)distance) & lastReceivedAckMask) != 0)
                    {
                        Release(context.internalProcessBuffer, info->SequenceId);
                        info->SendTime = -1;
                    }
                }
            }
        }

        /// <summary>Get the next sequence ID that needs to be resumed (NullEntry if none).</summary>
        internal static unsafe long GetNextSendResumeSequence(NetworkPipelineContext context)
        {
            SharedContext* reliable = (SharedContext*)context.internalSharedProcessBuffer;

            var resume = NullEntry;

            // Check each slot in the window and find the unacknowledged packet with the smallest
            // sequence number that has an expired resend timeout (if any).
            for (int i = 0; i < reliable->WindowSize; i++)
            {
                var info = GetPacketInformation(context.internalProcessBuffer, i);
                if (info->SequenceId >= 0)
                {
                    var resendTimeout = CurrentResendTime(context.internalSharedProcessBuffer);
                    var needsResend = context.timestamp > info->SendTime + resendTimeout;

                    if (needsResend && (resume == NullEntry || info->SequenceId < resume))
                    {
                        resume = info->SequenceId;
                    }
                }
            }

            return resume;
        }

        /// <summary>Check if the next packet in the sequence can be delivered.</summary>
        internal static unsafe bool NeedResumeReceive(NetworkPipelineContext context)
        {
            var reliable = (Context*)context.internalProcessBuffer;
            var nextExpectedSequenceId = reliable->Delivered + 1;
            var info = GetPacketInformation(context.internalProcessBuffer, nextExpectedSequenceId);
            return info->SequenceId == nextExpectedSequenceId;
        }

        /// <summary>
        /// Resume or play back a packet we had received earlier out of order. When an out of order packet is received
        /// it is stored since we need to first return the packet with the next sequence ID. When that packet finally
        /// arrives it is returned but a pipeline resume is requested since we already have the next packet stored
        /// and it can be processed immediately after.
        /// </summary>
        /// <param name="context">Pipeline context, we'll use both the shared reliability context and receive context.</param>
        /// <param name="startSequence">The first packet which we need to retrieve now, there could be more after that.</param>
        /// <returns></returns>
        internal static unsafe InboundRecvBuffer ResumeReceive(NetworkPipelineContext context)
        {
            var shared = (SharedContext*)context.internalSharedProcessBuffer;
            var reliable = (Context*)context.internalProcessBuffer;

            var nextExpectedSequenceId = (reliable->Delivered + 1);
            var info = GetPacketInformation(context.internalProcessBuffer, nextExpectedSequenceId);

            if (info->SequenceId == nextExpectedSequenceId)
            {
                var offset = reliable->DataPtrOffset + ((nextExpectedSequenceId % reliable->Capacity) * reliable->DataStride);
                var inBuffer = default(InboundRecvBuffer);
                inBuffer.buffer = context.internalProcessBuffer + offset;
                inBuffer.bufferLength = info->Size;

                reliable->Delivered = nextExpectedSequenceId;

                return inBuffer;
            }

            return default;
        }

        /// <summary>
        /// Resend a packet which we have not received an acknowledgement for in time. Pipeline resume
        /// will be enabled if there are more packets which we need to resend. The send reliability context
        /// will then also be updated to track the next packet we need to resume.
        /// </summary>
        /// <param name="context">Pipeline context, we'll use both the shared reliability context and send context.</param>
        /// <param name="header">Packet header for the packet payload we're resending.</param>
        /// <returns>Buffer slice to packet payload.</returns>
        /// <exception cref="InvalidOperationException"></exception>
        internal static unsafe InboundSendBuffer ResumeSend(NetworkPipelineContext context, out PacketHeader header)
        {
            SharedContext* reliable = (SharedContext*)context.internalSharedProcessBuffer;
            Context* ctx = (Context*)context.internalProcessBuffer;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (ctx->Resume == NullEntry)
                throw new InvalidOperationException("This function should not be called unless there is data in resume");
#endif

            var sequence = ctx->Resume;

            PacketInformation* information;
            information = GetPacketInformation(context.internalProcessBuffer, sequence);
            // Reset the resend timer
            information->SendTime = context.timestamp;

            Packet *packet = GetPacket(context.internalProcessBuffer, sequence);
            header = packet->Header;

            // Update acked/ackmask to latest values
            header.AckedSequenceId = (ushort)reliable->ReceivedPackets.Sequence;
            header.AckMask = reliable->ReceivedPackets.AckMask;

            var offset = (ctx->DataPtrOffset + ((sequence % ctx->Capacity) * ctx->DataStride));

            var inbound = default(InboundSendBuffer);
            inbound.bufferWithHeaders = context.internalProcessBuffer + offset;
            inbound.bufferWithHeadersLength = information->Size;
            inbound.headerPadding = information->HeaderPadding;
            inbound.SetBufferFromBufferWithHeaders();
            reliable->stats.PacketsResent++;

            return inbound;
        }

        /// <summary>
        /// Store the packet for possible later resends, and fill in the header we'll use to send it (populate with
        /// sequence ID, last acknowledged ID from remote with ackmask.
        /// </summary>
        /// <param name="context">Pipeline context, the reliability shared state is used here.</param>
        /// <param name="inboundBuffer">Buffer with packet data.</param>
        /// <param name="header">Packet header which will be populated.</param>
        /// <returns>Sequence ID assigned to this packet.</returns>
        internal static unsafe long Write(NetworkPipelineContext context, InboundSendBuffer inboundBuffer, ref PacketHeader header)
        {
            SharedContext* reliable = (SharedContext*)context.internalSharedProcessBuffer;

            var sequence = reliable->SentPackets.Sequence;

            if (!TryAquire(context.internalProcessBuffer, sequence))
            {
                return (int)Error.StatusCode.NetworkSendQueueFull;
            }
            reliable->stats.PacketsSent++;

            header.SequenceId = (ushort)sequence;
            header.AckedSequenceId = (ushort)reliable->ReceivedPackets.Sequence;
            header.AckMask = reliable->ReceivedPackets.AckMask;

            reliable->ReceivedPackets.Acked = reliable->ReceivedPackets.Sequence;
            reliable->ReceivedPackets.LastAckMask = header.AckMask;
            reliable->DuplicatesSinceLastAck = 0;

            // Attach our processing time of the packet we're acknowledging (time between receiving it and sending this ack)
            header.ProcessingTime =
                CalculateProcessingTime(context.internalSharedProcessBuffer, header.AckedSequenceId, context.timestamp);

            reliable->SentPackets.Sequence += 1;
            SetHeaderAndPacket(context.internalProcessBuffer, sequence, header, inboundBuffer, context.timestamp);

            StoreTimestamp(context.internalSharedProcessBuffer, sequence, context.timestamp);

            return sequence;
        }

        /// <summary>
        /// Write an ack packet, only the packet header is used and this doesn't advance the sequence ID.
        /// The packet is not stored away for resend routine.
        /// </summary>
        /// <param name="context">Pipeline context, the reliability shared state is used here.</param>
        /// <param name="header">Packet header which will be populated.</param>
        /// <returns></returns>
        internal static unsafe void WriteAckPacket(NetworkPipelineContext context, ref PacketHeader header)
        {
            SharedContext* reliable = (SharedContext*)context.internalSharedProcessBuffer;

            header.Type = (ushort)PacketType.Ack;
            header.AckedSequenceId = (ushort)reliable->ReceivedPackets.Sequence;
            header.AckMask = reliable->ReceivedPackets.AckMask;
            header.ProcessingTime =
                CalculateProcessingTime(context.internalSharedProcessBuffer, header.AckedSequenceId, context.timestamp);
            reliable->ReceivedPackets.Acked = reliable->ReceivedPackets.Sequence;
            reliable->ReceivedPackets.LastAckMask = header.AckMask;
            reliable->DuplicatesSinceLastAck = 0;
        }

        internal static unsafe void StoreTimestamp(byte* sharedBuffer, long sequenceId, long timestamp)
        {
            var timerData = GetLocalPacketTimer(sharedBuffer, sequenceId);
            timerData->SequenceId = sequenceId;
            timerData->SentTime = timestamp;
            timerData->ProcessingTime = 0;
            timerData->ReceiveTime = 0;
        }

        internal static unsafe void StoreReceiveTimestamp(byte* sharedBuffer, long sequenceId, long timestamp, ushort processingTime)
        {
            var sharedCtx = (SharedContext*)sharedBuffer;
            var rttInfo = sharedCtx->RttInfo;
            var timerData = GetLocalPacketTimer(sharedBuffer, sequenceId);
            if (timerData != null && timerData->SequenceId == sequenceId)
            {
                // Ignore the receive time if we've already received it (remote doesn't have new acks)
                if (timerData->ReceiveTime > 0)
                    return;
                timerData->ReceiveTime = timestamp;
                timerData->ProcessingTime = processingTime;

                rttInfo.LastRtt = (int)Math.Max(timerData->ReceiveTime - timerData->SentTime - timerData->ProcessingTime, 1);
                var delta = rttInfo.LastRtt - rttInfo.SmoothedRtt;
                rttInfo.SmoothedRtt += delta / 8;
                rttInfo.SmoothedVariance += (math.abs(delta) - rttInfo.SmoothedVariance) / 4;
                rttInfo.ResendTimeout = (int)(rttInfo.SmoothedRtt + 4 * rttInfo.SmoothedVariance);
                sharedCtx->RttInfo = rttInfo;
            }
        }

        internal static unsafe void StoreRemoteReceiveTimestamp(byte* sharedBuffer, long sequenceId, long timestamp)
        {
            var timerData = GetRemotePacketTimer(sharedBuffer, sequenceId);
            timerData->SequenceId = sequenceId;
            timerData->ReceiveTime = timestamp;
        }

        private static unsafe int CurrentResendTime(byte* sharedBuffer)
        {
            var sharedCtx = (SharedContext*)sharedBuffer;
            if (sharedCtx->RttInfo.ResendTimeout > sharedCtx->MaximumResendTime)
                return sharedCtx->MaximumResendTime;
            return Math.Max(sharedCtx->RttInfo.ResendTimeout, sharedCtx->MinimumResendTime);
        }

        internal static unsafe ushort CalculateProcessingTime(byte* sharedBuffer, long sequenceId, long timestamp)
        {
            // Look up previously recorded receive timestamp, subtract that from current timestamp and return as processing time
            var timerData = GetRemotePacketTimer(sharedBuffer, sequenceId);
            if (timerData != null && timerData->SequenceId == sequenceId)
                return Math.Min((ushort)(timestamp - timerData->ReceiveTime), ushort.MaxValue);
            return 0;
        }

        internal static unsafe PacketTimers* GetLocalPacketTimer(byte* sharedBuffer, long sequenceId)
        {
            var sharedCtx = (SharedContext*)sharedBuffer;
            var index = sequenceId % sharedCtx->WindowSize;
            var timerPtr = (long)sharedBuffer + sharedCtx->TimerDataOffset + sharedCtx->TimerDataStride * index;
            return (PacketTimers*)timerPtr;
        }

        internal static unsafe PacketTimers* GetRemotePacketTimer(byte* sharedBuffer, long sequenceId)
        {
            var sharedCtx = (SharedContext*)sharedBuffer;
            var index = sequenceId % sharedCtx->WindowSize;
            var timerPtr = (long)sharedBuffer + sharedCtx->RemoteTimerDataOffset + sharedCtx->RemoteTimerDataStride * index;
            return (PacketTimers*)timerPtr;
        }

        internal static long GetSequenceId64Bits(ref SequenceBufferContext context, ushort sequenceId16Bits)
        {
            // Divide the sequence ID into two components: 2 bits overflow cycle, 14 bits sequence ID
            var overflowCycle = (ushort)(sequenceId16Bits >> 14);
            var sequence = (ushort)(sequenceId16Bits & 0x3FFF);

            // If we've entered a new overflow cycle, the upper 50 bits of the 64 bit sequence ID have changed.
            // We only consider this if the overflow cycle has incremented by exactly one. Any other case is an out of order packet.
            if (overflowCycle == context.LastReceivedOverflowCycle + 1 || (overflowCycle == 0 && context.LastReceivedOverflowCycle == 3))
            {
                ++context.NumberOfOverflowsDetected;
            }

            long upper50Bits;
            // If we're in the same overflow cycle as the last received (even if we just updated the last received), our upper 50
            // bits are exactly the number of overflows detected.
            if (overflowCycle == context.LastReceivedOverflowCycle)
            {
                upper50Bits = context.NumberOfOverflowsDetected;
            }
            // If we're exactly one overflow cycle behind for this packet, this is an out of order packet that may still be valid to process.
            // The upper 50 bits are exactly one less than the number of overflows detected.
            else if (overflowCycle == context.LastReceivedOverflowCycle - 1 || (overflowCycle == 3 && context.LastReceivedOverflowCycle == 0))
            {
                upper50Bits = context.NumberOfOverflowsDetected - 1;
            }
            // Otherwise we definitely can't process this packet, it's too stale.
            else
            {
                return NullEntry;
            }

            // Now we combine the upper 50 bits we've been counting with the 14 bit sequence ID we've just received
            // to get the actual 64 bit sequence ID for this packet.
            return (upper50Bits << 14) | sequence;
        }

        internal static unsafe long Read(NetworkPipelineContext context, PacketHeader header)
        {
            var reliable = (SharedContext*)context.internalSharedProcessBuffer;

            reliable->stats.PacketsReceived++;

            var sequenceId64Bits = GetSequenceId64Bits(ref reliable->ReceivedPackets, header.SequenceId);

            if (sequenceId64Bits == NullEntry)
            {
                reliable->stats.PacketsStale++;
                return NullEntry;
            }

            var newerThanMostRecent = sequenceId64Bits > reliable->ReceivedPackets.Sequence;
            var distance = (int)math.abs(sequenceId64Bits - reliable->ReceivedPackets.Sequence);


            // If the packet is older than the most recent sequence number, and if it doesn't fit
            // in the window, then the packet is stale and should be ignored.
            if (!newerThanMostRecent && distance >= reliable->WindowSize)
            {
                reliable->stats.PacketsStale++;
                return NullEntry;
            }

            // Packets more than the window size in the future are invalid since the peer should
            // never send such packets. These packets should be ignored too.
            if (newerThanMostRecent && distance > reliable->WindowSize)
            {
                return NullEntry;
            }

            // At this point we know we're dealing with a valid packet.

            if (newerThanMostRecent)
            {
                // Update the most recent sequence number and ackmask.
                reliable->ReceivedPackets.Sequence = sequenceId64Bits;
                reliable->ReceivedPackets.AckMask <<= distance;
                reliable->ReceivedPackets.AckMask |= 1;

                // Count the number of dropped packets.
                for (int i = 0; i < distance; i++)
                {
                    if ((reliable->ReceivedPackets.AckMask & 1ul << i) == 0)
                        reliable->stats.PacketsDropped++;
                }
            }
            else
            {
                if ((reliable->ReceivedPackets.AckMask & 1ul << distance) != 0)
                {
                    // Still valuable to check ACKs for duplicates, since there might be more
                    // information than in the original packet if it's a resend.
                    ReadAckPacket(context, header);

                    reliable->stats.PacketsDuplicated++;
                    reliable->DuplicatesSinceLastAck++;

                    return NullEntry;
                }

                reliable->ReceivedPackets.AckMask |= 1ul << distance;
                reliable->stats.PacketsOutOfOrder++;
            }

            StoreRemoteReceiveTimestamp(context.internalSharedProcessBuffer, sequenceId64Bits, context.timestamp);

            ReadAckPacket(context, header);

            return sequenceId64Bits;
        }

        internal static unsafe void ReadAckPacket(NetworkPipelineContext context, PacketHeader header)
        {
            SharedContext* reliable = (SharedContext*)context.internalSharedProcessBuffer;

            // Store receive timestamp for our acked sequence ID with remote processing time
            StoreReceiveTimestamp(context.internalSharedProcessBuffer, header.AckedSequenceId, context.timestamp, header.ProcessingTime);


            var sequenceId64Bits = GetSequenceId64Bits(ref reliable->SentPackets, header.AckedSequenceId);

            // Check the distance of the acked seqId in the header, if it's too far away from last acked packet we
            // can't process it and add it to the ack mask
            if (sequenceId64Bits == NullEntry || reliable->SentPackets.Acked > sequenceId64Bits)
            {
                // No new acks;
                return;
            }

            if (reliable->SentPackets.Acked == sequenceId64Bits)
            {
                // If the current packet is the same as the last one we acked we do not know which one is newer, but it is safe to keep any packet acked by either ack since we never un-ack
                reliable->SentPackets.AckMask |= header.AckMask;
            }
            else
            {
                reliable->SentPackets.Acked = sequenceId64Bits;
                reliable->SentPackets.AckMask = header.AckMask;
            }
        }

        internal static unsafe bool ShouldSendAck(NetworkPipelineContext ctx)
        {
            var reliable = (Context*)ctx.internalProcessBuffer;
            var shared = (SharedContext*)ctx.internalSharedProcessBuffer;

            // If the last received sequence ID has not been acked yet, or the set of acked packet in the window
            // changed without the sequence ID updating (can happen when receiving out of order packets), or we've
            // received a lot of duplicates since last sending a ACK.
            return shared->ReceivedPackets.Acked < shared->ReceivedPackets.Sequence ||
                shared->ReceivedPackets.AckMask != shared->ReceivedPackets.LastAckMask ||
                shared->DuplicatesSinceLastAck >= MaxDuplicatesSinceLastAck;
        }

        /// <summary>
        /// Modify the minimum resend time used by the reliable pipeline stage for the given
        /// pipeline and connection. The default value should be good for most use cases, unless the
        /// connection is extremely good (less than 25ms RTT), in which case lowering this value
        /// could potentially reduce latency when packet loss occurs.
        /// </summary>
        /// <param name="value">New minimum resend time.</param>
        /// <param name="driver">Driver to modify.</param>
        /// <param name="pipeline">Pipeline to modify.</param>
        /// <param name="connection">Connection to modify the value for.</param>
        public static unsafe void SetMinimumResendTime(int value, NetworkDriver driver, NetworkPipeline pipeline, NetworkConnection connection)
        {
            var stageId = NetworkPipelineStageId.Get<ReliableSequencedPipelineStage>();
            driver.GetPipelineBuffers(pipeline, stageId, connection, out _, out _, out var sharedBuffer);
            var sharedCtx = (SharedContext*)sharedBuffer.GetUnsafePtr();
            sharedCtx->MinimumResendTime = value;
        }

        /// <summary>
        /// Modify the maximum resend time used by the reliable pipeline stage for the given
        /// pipeline and connection. The default value should be good for most use cases, unless the
        /// connection is rather poor (more than 200ms RTT), in which case this value should be
        /// increased to be slightly above the RTT (otherwise the pipeline will useless resend
        /// packets, increasing bandwidth usage).
        /// </summary>
        /// <param name="value">New maximum resend time.</param>
        /// <param name="driver">Driver to modify.</param>
        /// <param name="pipeline">Pipeline to modify.</param>
        /// <param name="connection">Connection to modify the value for.</param>
        public static unsafe void SetMaximumResendTime(int value, NetworkDriver driver, NetworkPipeline pipeline, NetworkConnection connection)
        {
            var stageId = NetworkPipelineStageId.Get<ReliableSequencedPipelineStage>();
            driver.GetPipelineBuffers(pipeline, stageId, connection, out _, out _, out var sharedBuffer);
            var sharedCtx = (SharedContext*)sharedBuffer.GetUnsafePtr();
            sharedCtx->MaximumResendTime = value;
        }
    }
}
