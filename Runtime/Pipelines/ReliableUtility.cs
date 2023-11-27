using System;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Unity.Networking.Transport.Utilities
{
    public struct SequenceBufferContext
    {
        public int Sequence;
        public int Acked;
        internal ulong AckedMask;
        internal ulong LastAckedMask;

        // Unused, only here to maintain the public API intact.
        public uint AckMask;
        public uint LastAckMask;
    }

    /// <summary>Extensions for <see cref="ReliableUtility.Parameters"/>.</summary>
    public static class ReliableStageParameterExtensions
    {
        /// <summary>
        /// Sets the <see cref="ReliableUtility.Parameters"/> values for the <see cref="NetworkSettings"/>
        /// </summary>
        /// <param name="settings"><see cref="NetworkSettings"/> to modify.</param>
        /// <param name="windowSize"><seealso cref="ReliableUtility.Parameters.WindowSize"/></param>
        /// <returns>Modified <see cref="NetworkSettings"/>.</returns>
        public static ref NetworkSettings WithReliableStageParameters(
            ref this NetworkSettings settings,
            int windowSize = ReliableUtility.ParameterConstants.WindowSize
        )
        {
            var parameter = new ReliableUtility.Parameters
            {
                WindowSize = windowSize,
            };

            settings.AddRawParameterStruct(ref parameter);

            return ref settings;
        }

        /// <summary>
        /// Gets the <see cref="ReliableUtility.Parameters"/>
        /// </summary>
        /// <param name="settings"><see cref="NetworkSettings"/> to get parameters from.</param>
        /// <returns>Returns the <see cref="ReliableUtility.Parameters"/> values for the <see cref="NetworkSettings"/></returns>
        public static ReliableUtility.Parameters GetReliableStageParameters(ref this NetworkSettings settings)
        {
            if (!settings.TryGet<ReliableUtility.Parameters>(out var parameters))
            {
                parameters.WindowSize = ReliableUtility.ParameterConstants.WindowSize;
            }

            return parameters;
        }
    }

    /// <summary>Utility methods and types for the reliable pipeline stage.</summary>
    /// <remarks>
    /// Most methods are meant for internal use only. It is recommended not to rely on anything in
    /// in structure since it is very likely to change in a future major version of the package.
    /// </remarks>
    public struct ReliableUtility
    {
        /// <summary>Statistics tracked internally by the reliable pipeline stage.</summary>
        public struct Statistics
        {
            /// <summary>Number of packets received by the pipeline stage.</summary>
            public int PacketsReceived;
            /// <summary>Number of packets sent by the pipeline stage.</summary>
            public int PacketsSent;
            /// <summary>Number of packets that were dropped in transit.</summary>
            public int PacketsDropped;
            /// <summary>Number of packets that arrived out of order.</summary>
            public int PacketsOutOfOrder;
            /// <summary>Number of duplicated packets received by the pipeline stage.</summary>
            public int PacketsDuplicated;
            /// <summary>Number of stale packets received by the pipeline stage.</summary>
            public int PacketsStale;
            /// <summary>Number of packets resent by the pipeline stage.</summary>
            public int PacketsResent;
        }

        /// <summary>RTT information tracked internally by the reliable pipeline stage.</summary>
        public struct RTTInfo
        {
            /// <summary>RTT of the last packet acknowledged.</summary>
            public int LastRtt;
            /// <summary>Smoothed RTT of the last packets acknowledged.</summary>
            public float SmoothedRtt;
            /// <summary>Variance of the smoothed RTT.</summary>
            public float SmoothedVariance;
            /// <summary>Timeout used to resend unacknowledged packets.</summary>
            public int ResendTimeout;
        }

        /// <summary>Internal value. Do not use.</summary>
        public const int NullEntry = -1;

        /// <summary>The least amount of time we'll wait until a packet resend is performed.</summary>
        public const int DefaultMinimumResendTime = 64; // This is 4x16ms (assumes a 60hz update rate).

        /// <summary>The maximum amount of time we'll wait to resend a packet.</summary>
        public const int MaximumResendTime = 200;

        // If we receive 3 duplicates AFTER our last send, then it's more likely that one of our
        // ACKs was lost and the remote is trying to resend us a packet we won't acknowledge.
        internal const int MaxDuplicatesSinceLastAck = 3;

        /// <summary>Internal error codes of the pipeline stage. Do not use.</summary>
        public enum ErrorCodes
        {
            Stale_Packet = -1,
            Duplicated_Packet = -2,

            OutgoingQueueIsFull = -7,
            InsufficientMemory = -8
        }

        /// <summary>Internal packet types used by the pipeline stage. Do not use.</summary>
        public enum PacketType : ushort
        {
            Payload = 0,
            Ack = 1
        }

        public struct SharedContext
        {
            public int WindowSize;
            public int MinimumResendTime;

            /// <summary>
            /// Context of sent packets, last sequence ID sent (-1), last ID of our sent packet acknowledged by
            /// remote peer, ackmask of acknowledged packets. This is used when determining if a resend
            /// is needed.
            /// </summary>
            public SequenceBufferContext SentPackets;

            /// <summary>
            /// Context of received packets, last sequence ID received, and ackmask of received packets. Acked is not used.
            /// This is sent back to the remote peer in the header when sending.
            /// </summary>
            public SequenceBufferContext ReceivedPackets;

            internal int DuplicatesSinceLastAck;

            public Statistics stats;
            public ErrorCodes errorCode;

            // Timing information for calculating resend times for packets
            public RTTInfo RttInfo;
            public int TimerDataOffset;
            public int TimerDataStride;
            public int RemoteTimerDataOffset;
            public int RemoteTimerDataStride;
        }

        /// <summary>Internal context of the reliable pipeline stage. Do not use.</summary>
        public struct Context
        {
            public int Capacity;
            public int Resume;
            public int Delivered;
            public int IndexStride;
            public int IndexPtrOffset;
            public int DataStride;
            public int DataPtrOffset;
            public long LastSentTime;
            public long PreviousTimestamp;
        }

        /// <summary>Parameters for the reliable pipeline stage.</summary>
        public struct Parameters : INetworkParameter
        {
            /// <summary>Maximum number of packets that can be in flight at a time.</summary>
            /// <remarks>
            /// Must be between 0 and 64. Default is 32. Note that using values higher than 32 will
            /// make reliable headers slightly larger, reducing the amount of space available for
            /// data. Most of the time the extra bandwidth offered by the larger window size is more
            /// than worth it, however.
            /// </remarks>
            public int WindowSize;

            /// <summary>Validate the settings.</summary>
            /// <returns>True if the settings are valid, false otherwise.</returns>
            public bool Validate()
            {
                var valid = true;

                if (WindowSize < 0 || WindowSize > 64)
                {
                    valid = false;
                    UnityEngine.Debug.LogError($"{nameof(WindowSize)} value ({WindowSize}) must be greater than 0 and smaller or equal to 32");
                }

                return valid;
            }
        }

        /// <summary>Default values for the reliable pipeline stage parameters.</summary>
        public struct ParameterConstants
        {
            /// <summary>Default window size.</summary>
            public const int WindowSize = 32;
        }

        [StructLayout(LayoutKind.Sequential)]
        [Obsolete("Will be removed in Unity Transport 2.0.")]
        public struct PacketHeader
        {
            public ushort Type;
            public ushort ProcessingTime;
            public ushort SequenceId;
            public ushort AckedSequenceId;
            public uint AckMask;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ReliableHeader
        {
            public ushort Type;
            public ushort ProcessingTime;
            public ushort SequenceId;
            public ushort AckedSequenceId;
            // This must be the last member in the packet header, since we truncate it for smaller window sizes.
            public ulong AckedMask;
        }

        /// <summary>Internal packet data structure. Do not use.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct PacketInformation
        {
            public int SequenceId;
            public ushort Size;
            public ushort HeaderPadding;
            public long SendTime;
        }

        [StructLayout(LayoutKind.Explicit)]
        [Obsolete("Will be removed in Unity Transport 2.0.")]
        public unsafe struct Packet
        {
            internal const int Length = NetworkParameterConstants.MaxPacketBufferSize;
            [FieldOffset(0)] public PacketHeader Header;
            [FieldOffset(0)] public fixed byte Buffer[Length];
        }

        // Header is inside the total packet length (Buffer size)
        [StructLayout(LayoutKind.Explicit)]
        internal unsafe struct ReliablePacket
        {
            // Have to add an extra 4 bytes in there to account for the fact that parts of the
            // header will be unused if window size is 32 or less. We could do away with this hack
            // by correcting the offsets everywhere else in the code, but that's tricky.
            internal const int Length = NetworkParameterConstants.MaxPacketBufferSize + sizeof(uint);
            [FieldOffset(0)] public ReliableHeader Header;
            [FieldOffset(0)] public fixed byte Buffer[Length];
        }

        /// <summary>Internal packet data structure. Do not use.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct PacketTimers
        {
            public ushort ProcessingTime;
            public ushort Padding;
            public int SequenceId;
            public long SentTime;
            public long ReceiveTime;
        }

        private static int AlignedSizeOf<T>() where T : struct
        {
            return (UnsafeUtility.SizeOf<T>() + NetworkPipelineProcessor.AlignmentMinusOne) & (~NetworkPipelineProcessor.AlignmentMinusOne);
        }

        internal static int PacketHeaderWireSize(int windowSize)
        {
            var fullHeaderSize = UnsafeUtility.SizeOf<ReliableHeader>();
            return windowSize > 32 ? fullHeaderSize : fullHeaderSize - sizeof(uint);
        }

        internal static unsafe int PacketHeaderWireSize(NetworkPipelineContext ctx)
        {
            var reliable = (SharedContext*)ctx.internalSharedProcessBuffer;
            var windowSize = reliable->WindowSize;
            return PacketHeaderWireSize(windowSize);
        }

        public static int SharedCapacityNeeded(Parameters param)
        {
            // Timers are stored for both remote packets (processing time) and local packets (round trip time)
            // The amount of timestamps needed in the queues is the same as the window size capacity
            var timerDataSize = AlignedSizeOf<PacketTimers>() * param.WindowSize * 2;
            var capacityNeeded = AlignedSizeOf<SharedContext>() + timerDataSize;

            return capacityNeeded;
        }

        public static int ProcessCapacityNeeded(Parameters param)
        {
            var infoSize = AlignedSizeOf<PacketInformation>();
            var dataSize = (ReliablePacket.Length + NetworkPipelineProcessor.AlignmentMinusOne) & (~NetworkPipelineProcessor.AlignmentMinusOne);
            infoSize *= param.WindowSize;
            dataSize *= param.WindowSize;

            var capacityNeeded = AlignedSizeOf<Context>() + infoSize + dataSize;

            return capacityNeeded;
        }

        public static unsafe SharedContext InitializeContext(byte* sharedBuffer, int sharedBufferLength,
            byte* sendBuffer, int sendBufferLength, byte* recvBuffer, int recvBufferLength, Parameters param)
        {
            InitializeProcessContext(sendBuffer, sendBufferLength, param);
            InitializeProcessContext(recvBuffer, recvBufferLength, param);

            SharedContext* notifier = (SharedContext*)sharedBuffer;
            *notifier = new SharedContext
            {
                WindowSize = param.WindowSize,
                SentPackets = new SequenceBufferContext { Acked = NullEntry, AckedMask = 0ul },
                MinimumResendTime = DefaultMinimumResendTime,
                ReceivedPackets = new SequenceBufferContext { Sequence = NullEntry, AckedMask = 0ul, LastAckedMask = 0ul },
                RttInfo = new RTTInfo { SmoothedVariance = 5, SmoothedRtt = 50, ResendTimeout = 50, LastRtt = 50 },
                TimerDataOffset = AlignedSizeOf<SharedContext>(),
                TimerDataStride = AlignedSizeOf<PacketTimers>(),
                RemoteTimerDataOffset = AlignedSizeOf<SharedContext>() + AlignedSizeOf<PacketTimers>() * param.WindowSize,
                RemoteTimerDataStride = AlignedSizeOf<PacketTimers>()
            };
            return *notifier;
        }

        public static unsafe int InitializeProcessContext(byte* buffer, int bufferLength, Parameters param)
        {
            int totalCapacity = ProcessCapacityNeeded(param);
            if (bufferLength != totalCapacity)
            {
                return (int)ErrorCodes.InsufficientMemory;
            }
            Context* ctx = (Context*)buffer;

            ctx->Capacity = param.WindowSize;
            ctx->IndexStride = AlignedSizeOf<PacketInformation>();
            ctx->IndexPtrOffset = AlignedSizeOf<Context>();
            ctx->DataStride = (ReliablePacket.Length + NetworkPipelineProcessor.AlignmentMinusOne) & (~NetworkPipelineProcessor.AlignmentMinusOne);
            ctx->DataPtrOffset = ctx->IndexPtrOffset + (ctx->IndexStride * ctx->Capacity);
            ctx->Resume = NullEntry;
            ctx->Delivered = NullEntry;

            Release(buffer, 0, param.WindowSize);
            return 0;
        }

        public static unsafe void SetPacket(byte* self, int sequence, InboundRecvBuffer data)
        {
            SetPacket(self, sequence, data.buffer, data.bufferLength);
        }

        public static unsafe void SetPacket(byte* self, int sequence, void* data, int length)
        {
            Context* ctx = (Context*)self;

            if (length > ctx->DataStride)
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                throw new OverflowException();
#else
                return;
#endif

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
        [Obsolete("Internal API that shouldn't be used. Will be removed in Unity Transport 2.0.")]
        public static unsafe void SetHeaderAndPacket(byte* self, int sequence, PacketHeader header, InboundSendBuffer data, long timestamp)
        {
            throw new NotImplementedException("Implementation was moved to other internal APIs.");
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
        internal static unsafe void SetHeaderAndPacket(byte* self, int sequence, ReliableHeader header, InboundSendBuffer data, long timestamp)
        {
            Context* ctx = (Context*)self;
            int totalSize = data.bufferLength + data.headerPadding;

            if (totalSize > ctx->DataStride)
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                throw new OverflowException();
#else
                return;
#endif
            var index = sequence % ctx->Capacity;

            PacketInformation* info = GetPacketInformation(self, sequence);
            info->SequenceId = sequence;
            info->Size = (ushort)totalSize;
            info->HeaderPadding = (ushort)data.headerPadding;
            info->SendTime = timestamp;

            ReliablePacket* packet = GetReliablePacket(self, sequence);
            packet->Header = header;
            var offset = (ctx->DataPtrOffset + (index * ctx->DataStride));
            void* dataPtr = (self + offset);

            if (data.bufferLength > 0)
                UnsafeUtility.MemCpy((byte*)dataPtr + data.headerPadding, data.buffer, data.bufferLength);
        }

        public static unsafe PacketInformation* GetPacketInformation(byte* self, int sequence)
        {
            Context* ctx = (Context*)self;
            var index = sequence % ctx->Capacity;

            return (PacketInformation*)((self + ctx->IndexPtrOffset) + (index * ctx->IndexStride));
        }

        [Obsolete("Internal API that shouldn't be used. Will be removed in Unity Transport 2.0.")]
        public static unsafe Packet* GetPacket(byte* self, int sequence)
        {
            throw new NotImplementedException("Implementation was moved to other internal APIs.");
        }

        internal static unsafe ReliablePacket* GetReliablePacket(byte* self, int sequence)
        {
            Context* ctx = (Context*)self;
            var index = sequence % ctx->Capacity;

            var offset = ctx->DataPtrOffset + (index * ctx->DataStride);
            return (ReliablePacket*)(self + offset);
        }

        public static unsafe bool TryAquire(byte* self, int sequence)
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

        public static unsafe void Release(byte* self, int sequence)
        {
            Release(self, sequence, 1);
        }

        public static unsafe void Release(byte* self, int start_sequence, int count)
        {
            Context* ctx = (Context*)self;
            for (int i = 0; i < count; i++)
            {
                SetIndex(self, (start_sequence + i) % ctx->Capacity, NullEntry);
            }
        }

        static unsafe void SetIndex(byte* self, int index, int sequence)
        {
            Context* ctx = (Context*)self;

            int* value = (int*)((self + ctx->IndexPtrOffset) + (index * ctx->IndexStride));
            *value = sequence;
        }

        static unsafe int GetIndex(byte* self, int index)
        {
            Context* ctx = (Context*)self;

            int* value = (int*)((self + ctx->IndexPtrOffset) + (index * ctx->IndexStride));
            return *value;
        }

        /// <summary>
        /// Acknowledge the reception of packets which have been sent. The reliability
        /// shared context/state is updated when packets are received from the other end
        /// of the connection. The other side will update it's ackmask with which packets
        /// have been received (starting from last received sequence ID) each time it sends
        /// a packet back. This checks the resend timers on each non-acknowledged packet
        /// and notifies if it's time to resend yet.
        /// </summary>
        /// <remarks>Internal API. Shouldn't be used.</remarks>
        /// <param name="context">Pipeline context, contains the buffer slices this pipeline connection owns.</param>
        /// <returns>If packets needed to be resumed.</returns>
        [Obsolete("Internal API that shouldn't be used. Will be removed in Unity Transport 2.0.")]
        public static unsafe bool ReleaseOrResumePackets(NetworkPipelineContext context)
        {
            throw new NotImplementedException("Implementation was moved to other internal APIs.");
        }

        /// <summary>Release packets which have been acknowledged by the remote.</summary>
        internal static unsafe void ReleaseAcknowledgedPackets(NetworkPipelineContext context)
        {
            SharedContext* reliable = (SharedContext*)context.internalSharedProcessBuffer;

            // Last sequence ID and ackmask we received from the remote peer.
            var lastReceivedAckedMask = reliable->SentPackets.AckedMask;
            var lastOwnSequenceIdAckedByRemote = (ushort)reliable->SentPackets.Acked;

            // Check each slot in the window for acknowledged packets.
            for (int i = 0; i < reliable->WindowSize; i++)
            {
                var info = GetPacketInformation(context.internalProcessBuffer, i);
                if (info->SequenceId >= 0)
                {
                    // Don't release anything greater than the last packet acknowledged.
                    if (SequenceHelpers.GreaterThan16((ushort)info->SequenceId, lastOwnSequenceIdAckedByRemote))
                        continue;

                    var distance = SequenceHelpers.AbsDistance(lastOwnSequenceIdAckedByRemote, (ushort)info->SequenceId);

                    // Distance being greater than window size shouldn't happen, but release the
                    // packet anyway since it must have been acknowledged by now. Otherwise check
                    // the ackmask to see if the packet was acknowledged.
                    if (distance >= reliable->WindowSize || ((1ul << distance) & lastReceivedAckedMask) != 0)
                    {
                        Release(context.internalProcessBuffer, info->SequenceId);
                        info->SendTime = -1;
                    }
                }
            }
        }

        /// <summary>Get the next sequence ID that needs to be resumed (NullEntry if none).</summary>
        internal static unsafe int GetNextSendResumeSequence(NetworkPipelineContext context)
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

                    if (needsResend && (resume == NullEntry || SequenceHelpers.LessThan16((ushort)info->SequenceId, (ushort)resume)))
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
            var nextExpectedSequenceId = (ushort)(reliable->Delivered + 1);
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
        /// <param name="startSequence">Ignored.</param>
        /// <param name="needsResume">Ignored.</param>
        /// <returns></returns>
        public static unsafe InboundRecvBuffer ResumeReceive(NetworkPipelineContext context, int startSequence, ref bool needsResume)
        {
            var shared = (SharedContext*)context.internalSharedProcessBuffer;
            var reliable = (Context*)context.internalProcessBuffer;

            var nextExpectedSequenceId = (ushort)(reliable->Delivered + 1);
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
        /// <param name="needsResume">Indicates if a pipeline resume is needed again. Unused.</param>
        /// <returns>Buffer slice to packet payload.</returns>
        [Obsolete("Will be removed in Unity Transport 2.0.")]
        public static unsafe InboundSendBuffer ResumeSend(NetworkPipelineContext context, out PacketHeader header, ref bool needsResume)
        {
            throw new NotImplementedException("Implementation moved to an internal method. Shouldn't be used anymore.");
        }

        /// <summary>
        /// Resend a packet which we have not received an acknowledgement for in time. Pipeline resume
        /// will be enabled if there are more packets which we need to resend. The send reliability context
        /// will then also be updated to track the next packet we need to resume.
        /// </summary>
        /// <param name="context">Pipeline context, we'll use both the shared reliability context and send context.</param>
        /// <param name="header">Packet header for the packet payload we're resending.</param>
        /// <param name="needsResume">Indicates if a pipeline resume is needed again. Unused.</param>
        /// <returns>Buffer slice to packet payload.</returns>
        /// <exception cref="InvalidOperationException"></exception>
        internal static unsafe InboundSendBuffer ResumeSend(NetworkPipelineContext context, out ReliableHeader header, ref bool needsResume)
        {
            SharedContext* reliable = (SharedContext*)context.internalSharedProcessBuffer;
            Context* ctx = (Context*)context.internalProcessBuffer;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (ctx->Resume == NullEntry)
                throw new InvalidOperationException("This function should not be called unless there is data in resume");
#endif

            var sequence = (ushort)ctx->Resume;

            PacketInformation* information;
            information = GetPacketInformation(context.internalProcessBuffer, sequence);
            // Reset the resend timer
            information->SendTime = context.timestamp;

            ReliablePacket *packet = GetReliablePacket(context.internalProcessBuffer, sequence);
            header = packet->Header;

            // Update acked/ackmask to latest values
            header.AckedSequenceId = (ushort)reliable->ReceivedPackets.Sequence;
            header.AckedMask = reliable->ReceivedPackets.AckedMask;

            var offset = (ctx->DataPtrOffset + ((sequence % ctx->Capacity) * ctx->DataStride));

            var inbound = default(InboundSendBuffer);
            inbound.bufferWithHeaders = context.internalProcessBuffer + offset;
            inbound.bufferWithHeadersLength = information->Size;
            inbound.headerPadding = information->HeaderPadding;
            inbound.SetBufferFrombufferWithHeaders();
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
        [Obsolete("Will be removed in Unity Transport 2.0.")]
        public static unsafe int Write(NetworkPipelineContext context, InboundSendBuffer inboundBuffer, ref PacketHeader header)
        {
            throw new NotImplementedException("Implementation moved to an internal method. Shouldn't be used anymore.");
        }

        /// <summary>
        /// Store the packet for possible later resends, and fill in the header we'll use to send it (populate with
        /// sequence ID, last acknowledged ID from remote with ackmask.
        /// </summary>
        /// <param name="context">Pipeline context, the reliability shared state is used here.</param>
        /// <param name="inboundBuffer">Buffer with packet data.</param>
        /// <param name="header">Packet header which will be populated.</param>
        /// <returns>Sequence ID assigned to this packet.</returns>
        internal static unsafe int Write(NetworkPipelineContext context, InboundSendBuffer inboundBuffer, ref ReliableHeader header)
        {
            SharedContext* reliable = (SharedContext*)context.internalSharedProcessBuffer;

            var sequence = (ushort)reliable->SentPackets.Sequence;

            if (!TryAquire(context.internalProcessBuffer, sequence))
            {
                reliable->errorCode = ErrorCodes.OutgoingQueueIsFull;
                return (int)ErrorCodes.OutgoingQueueIsFull;
            }
            reliable->stats.PacketsSent++;

            header.SequenceId = sequence;
            header.AckedSequenceId = (ushort)reliable->ReceivedPackets.Sequence;
            header.AckedMask = reliable->ReceivedPackets.AckedMask;

            reliable->ReceivedPackets.Acked = reliable->ReceivedPackets.Sequence;
            reliable->ReceivedPackets.LastAckedMask = header.AckedMask;
            reliable->DuplicatesSinceLastAck = 0;

            // Attach our processing time of the packet we're acknowledging (time between receiving it and sending this ack)
            header.ProcessingTime =
                CalculateProcessingTime(context.internalSharedProcessBuffer, header.AckedSequenceId, context.timestamp);

            reliable->SentPackets.Sequence = (ushort)(reliable->SentPackets.Sequence + 1);
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
        [Obsolete("Will be removed in Unity Transport 2.0.")]
        public static unsafe void WriteAckPacket(NetworkPipelineContext context, ref PacketHeader header)
        {
            throw new NotImplementedException("Implementation moved to an internal method. Shouldn't be used anymore.");
        }

        /// <summary>
        /// Write an ack packet, only the packet header is used and this doesn't advance the sequence ID.
        /// The packet is not stored away for resend routine.
        /// </summary>
        /// <param name="context">Pipeline context, the reliability shared state is used here.</param>
        /// <param name="header">Packet header which will be populated.</param>
        /// <returns></returns>
        internal static unsafe void WriteAckPacket(NetworkPipelineContext context, ref ReliableHeader header)
        {
            SharedContext* reliable = (SharedContext*)context.internalSharedProcessBuffer;

            header.Type = (ushort)PacketType.Ack;
            header.AckedSequenceId = (ushort)reliable->ReceivedPackets.Sequence;
            header.AckedMask = reliable->ReceivedPackets.AckedMask;
            header.ProcessingTime =
                CalculateProcessingTime(context.internalSharedProcessBuffer, header.AckedSequenceId, context.timestamp);
            reliable->ReceivedPackets.Acked = reliable->ReceivedPackets.Sequence;
            reliable->ReceivedPackets.LastAckedMask = header.AckedMask;
            reliable->DuplicatesSinceLastAck = 0;
        }

        public static unsafe void StoreTimestamp(byte* sharedBuffer, ushort sequenceId, long timestamp)
        {
            var timerData = GetLocalPacketTimer(sharedBuffer, sequenceId);
            timerData->SequenceId = sequenceId;
            timerData->SentTime = timestamp;
            timerData->ProcessingTime = 0;
            timerData->ReceiveTime = 0;
        }

        public static unsafe void StoreReceiveTimestamp(byte* sharedBuffer, ushort sequenceId, long timestamp, ushort processingTime)
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

        public static unsafe void StoreRemoteReceiveTimestamp(byte* sharedBuffer, ushort sequenceId, long timestamp)
        {
            var timerData = GetRemotePacketTimer(sharedBuffer, sequenceId);
            timerData->SequenceId = sequenceId;
            timerData->ReceiveTime = timestamp;
        }

        static unsafe int CurrentResendTime(byte* sharedBuffer)
        {
            var sharedCtx = (SharedContext*)sharedBuffer;
            if (sharedCtx->RttInfo.ResendTimeout > MaximumResendTime)
                return MaximumResendTime;
            return Math.Max(sharedCtx->RttInfo.ResendTimeout, sharedCtx->MinimumResendTime);
        }

        public static unsafe ushort CalculateProcessingTime(byte* sharedBuffer, ushort sequenceId, long timestamp)
        {
            // Look up previously recorded receive timestamp, subtract that from current timestamp and return as processing time
            var timerData = GetRemotePacketTimer(sharedBuffer, sequenceId);
            if (timerData != null && timerData->SequenceId == sequenceId)
                return Math.Min((ushort)(timestamp - timerData->ReceiveTime), ushort.MaxValue);
            return 0;
        }

        public static unsafe PacketTimers* GetLocalPacketTimer(byte* sharedBuffer, ushort sequenceId)
        {
            var sharedCtx = (SharedContext*)sharedBuffer;
            var index = sequenceId % sharedCtx->WindowSize;
            var timerPtr = (long)sharedBuffer + sharedCtx->TimerDataOffset + sharedCtx->TimerDataStride * index;
            return (PacketTimers*)timerPtr;
        }

        public static unsafe PacketTimers* GetRemotePacketTimer(byte* sharedBuffer, ushort sequenceId)
        {
            var sharedCtx = (SharedContext*)sharedBuffer;
            var index = sequenceId % sharedCtx->WindowSize;
            var timerPtr = (long)sharedBuffer + sharedCtx->RemoteTimerDataOffset + sharedCtx->RemoteTimerDataStride * index;
            return (PacketTimers*)timerPtr;
        }

        /// <summary>
        /// Read header data and update reliability tracking information in the shared context.
        /// - If the packets sequence ID is lower than the last received ID+1, then it's stale
        /// - If the packets sequence ID is higher, then we'll process it and update tracking info in the shared context
        /// </summary>
        /// <param name="context">Pipeline context, the reliability shared state is used here.</param>
        /// <param name="header">Packet header of a new received packet.</param>
        /// <returns>Sequence ID of the received packet.</returns>
        [Obsolete("Will be removed in Unity Transport 2.0.")]
        public static unsafe int Read(NetworkPipelineContext context, PacketHeader header)
        {
            throw new NotImplementedException("Implementation moved to an internal method. Shouldn't be used anymore.");
        }

        internal static unsafe int Read(NetworkPipelineContext context, ReliableHeader header)
        {
            var reliable = (SharedContext*)context.internalSharedProcessBuffer;

            var newerThanMostRecent = SequenceHelpers.GreaterThan16(header.SequenceId, (ushort)reliable->ReceivedPackets.Sequence);
            var distance = SequenceHelpers.AbsDistance(header.SequenceId, (ushort)reliable->ReceivedPackets.Sequence);

            reliable->stats.PacketsReceived++;

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
                reliable->ReceivedPackets.Sequence = header.SequenceId;
                reliable->ReceivedPackets.AckedMask <<= distance;
                reliable->ReceivedPackets.AckedMask |= 1;

                // Count the number of dropped packets.
                for (int i = 0; i < distance; i++)
                {
                    if ((reliable->ReceivedPackets.AckedMask & 1ul << i) == 0)
                        reliable->stats.PacketsDropped++;
                }
            }
            else
            {
                if ((reliable->ReceivedPackets.AckedMask & 1ul << distance) != 0)
                {
                    // Still valuable to check ACKs for duplicates, since there might be more
                    // information than in the original packet if it's a resend.
                    ReadAckPacket(context, header);

                    reliable->stats.PacketsDuplicated++;
                    reliable->DuplicatesSinceLastAck++;

                    return NullEntry;
                }

                reliable->ReceivedPackets.AckedMask |= 1ul << distance;
                reliable->stats.PacketsOutOfOrder++;
            }

            StoreRemoteReceiveTimestamp(context.internalSharedProcessBuffer, header.SequenceId, context.timestamp);

            ReadAckPacket(context, header);

            return header.SequenceId;
        }

        [Obsolete("Will be removed in Unity Transport 2.0.")]
        public static unsafe void ReadAckPacket(NetworkPipelineContext context, PacketHeader header)
        {
            throw new NotImplementedException("Implementation moved to an internal method. Shouldn't be used anymore.");
        }

        internal static unsafe void ReadAckPacket(NetworkPipelineContext context, ReliableHeader header)
        {
            SharedContext* reliable = (SharedContext*)context.internalSharedProcessBuffer;

            // Store receive timestamp for our acked sequence ID with remote processing time
            StoreReceiveTimestamp(context.internalSharedProcessBuffer, header.AckedSequenceId, context.timestamp, header.ProcessingTime);

            // Check the distance of the acked seqId in the header, if it's too far away from last acked packet we
            // can't process it and add it to the ack mask
            if (SequenceHelpers.GreaterThan16((ushort)reliable->SentPackets.Acked, header.AckedSequenceId))
            {
                // No new acks;
                return;
            }

            if (reliable->SentPackets.Acked == header.AckedSequenceId)
            {
                // If the current packet is the same as the last one we acked we do not know which one is newer, but it is safe to keep any packet acked by either ack since we never un-ack
                reliable->SentPackets.AckedMask |= header.AckedMask;
            }
            else
            {
                reliable->SentPackets.Acked = header.AckedSequenceId;
                reliable->SentPackets.AckedMask = header.AckedMask;
            }
        }

        public static unsafe bool ShouldSendAck(NetworkPipelineContext ctx)
        {
            var reliable = (Context*)ctx.internalProcessBuffer;
            var shared = (SharedContext*)ctx.internalSharedProcessBuffer;

            // If more than one full frame (timestamp - prevTimestamp = one frame) has elapsed then send ack packet
            // and if the last received sequence ID has not been acked yet, or the set of acked packet in the window
            // changed without the sequence ID updating (can happen when receiving out of order packets), or we've
            // received a lot of duplicates since last sending a ACK.
            if (reliable->LastSentTime < reliable->PreviousTimestamp &&
                (SequenceHelpers.LessThan16((ushort)shared->ReceivedPackets.Acked, (ushort)shared->ReceivedPackets.Sequence) ||
                 shared->ReceivedPackets.AckedMask != shared->ReceivedPackets.LastAckedMask ||
                 shared->DuplicatesSinceLastAck >= MaxDuplicatesSinceLastAck))
                return true;
            return false;
        }

        public static unsafe void SetMinimumResendTime(int value, NetworkDriver driver,
            NetworkPipeline pipeline, NetworkConnection con)
        {
            driver.GetPipelineBuffers(pipeline, NetworkPipelineStageCollection.GetStageId(typeof(ReliableSequencedPipelineStage)), con, out var receiveBuffer, out var sendBuffer, out var sharedBuffer);
            var sharedCtx = (ReliableUtility.SharedContext*)sharedBuffer.GetUnsafePtr();
            sharedCtx->MinimumResendTime = value;
        }
    }
}
