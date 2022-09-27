using AOT;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Networking.Transport.Utilities;
using Unity.Burst;

namespace Unity.Networking.Transport
{
    /// <summary>
    /// The ReliableSequencedPipelineStage is used to send packets reliably and retain the order in which they are sent.
    /// This PipelineStage has a hardcoded WindowSize of 32 inflight packets and will drop packets if its unable to
    /// track them.
    /// </summary>
    [BurstCompile]
    public unsafe struct ReliableSequencedPipelineStage : INetworkPipelineStage
    {
        static TransportFunctionPointer<NetworkPipelineStage.ReceiveDelegate> ReceiveFunctionPointer = new TransportFunctionPointer<NetworkPipelineStage.ReceiveDelegate>(Receive);
        static TransportFunctionPointer<NetworkPipelineStage.SendDelegate> SendFunctionPointer = new TransportFunctionPointer<NetworkPipelineStage.SendDelegate>(Send);
        static TransportFunctionPointer<NetworkPipelineStage.InitializeConnectionDelegate> InitializeConnectionFunctionPointer = new TransportFunctionPointer<NetworkPipelineStage.InitializeConnectionDelegate>(InitializeConnection);
        public NetworkPipelineStage StaticInitialize(byte* staticInstanceBuffer, int staticInstanceBufferLength, NetworkSettings settings)
        {
            ReliableUtility.Parameters param = settings.GetReliableStageParameters();

            UnsafeUtility.MemCpy(staticInstanceBuffer, &param, UnsafeUtility.SizeOf<ReliableUtility.Parameters>());
            return new NetworkPipelineStage(
                Receive: ReceiveFunctionPointer,
                Send: SendFunctionPointer,
                InitializeConnection: InitializeConnectionFunctionPointer,
                ReceiveCapacity: ReliableUtility.ProcessCapacityNeeded(param),
                SendCapacity: ReliableUtility.ProcessCapacityNeeded(param),
                HeaderCapacity: UnsafeUtility.SizeOf<ReliableUtility.PacketHeader>(),
                SharedStateCapacity: ReliableUtility.SharedCapacityNeeded(param)
            );
        }

        public int StaticSize => UnsafeUtility.SizeOf<ReliableUtility.Parameters>();

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(NetworkPipelineStage.ReceiveDelegate))]
        private static void Receive(ref NetworkPipelineContext ctx, ref InboundRecvBuffer inboundBuffer, ref NetworkPipelineStage.Requests requests, int systemHeaderSize)
        {
            // Request a send update to see if a queued packet needs to be resent later or if an ack packet should be sent
            requests = NetworkPipelineStage.Requests.SendUpdate;
            bool needsResume = false;

            var header = default(ReliableUtility.PacketHeader);
            var slice = default(InboundRecvBuffer);
            ReliableUtility.Context* reliable = (ReliableUtility.Context*)ctx.internalProcessBuffer;
            ReliableUtility.SharedContext* shared = (ReliableUtility.SharedContext*)ctx.internalSharedProcessBuffer;
            shared->errorCode = 0;
            if (reliable->Resume == ReliableUtility.NullEntry)
            {
                if (inboundBuffer.bufferLength <= 0)
                {
                    inboundBuffer = slice;
                    return;
                }
                var inboundArray = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<byte>(inboundBuffer.buffer, inboundBuffer.bufferLength, Allocator.Invalid);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                var safetyHandle = AtomicSafetyHandle.GetTempMemoryHandle();
                NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref inboundArray, safetyHandle);
#endif
                var reader = new DataStreamReader(inboundArray);
                reader.ReadBytes((byte*)&header, UnsafeUtility.SizeOf<ReliableUtility.PacketHeader>());

                if (header.Type == (ushort)ReliableUtility.PacketType.Ack)
                {
                    ReliableUtility.ReadAckPacket(ctx, header);
                    inboundBuffer = default;
                    return;
                }

                var result = ReliableUtility.Read(ctx, header);

                if (result >= 0)
                {
                    var nextExpectedSequenceId = (ushort)(reliable->Delivered + 1);
                    if (result == nextExpectedSequenceId)
                    {
                        reliable->Delivered = result;
                        slice = inboundBuffer.Slice(UnsafeUtility.SizeOf<ReliableUtility.PacketHeader>());

                        if (needsResume = SequenceHelpers.GreaterThan16((ushort)shared->ReceivedPackets.Sequence, (ushort)result))
                        {
                            reliable->Resume = (ushort)(result + 1);
                        }
                    }
                    else
                    {
                        ReliableUtility.SetPacket(ctx.internalProcessBuffer, result, inboundBuffer.Slice(UnsafeUtility.SizeOf<ReliableUtility.PacketHeader>()));
                        slice = ReliableUtility.ResumeReceive(ctx, reliable->Delivered + 1, ref needsResume);
                    }
                }
            }
            else
            {
                slice = ReliableUtility.ResumeReceive(ctx, reliable->Resume, ref needsResume);
            }
            if (needsResume)
                requests |= NetworkPipelineStage.Requests.Resume;
            inboundBuffer = slice;
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(NetworkPipelineStage.SendDelegate))]
        private static int Send(ref NetworkPipelineContext ctx, ref InboundSendBuffer inboundBuffer, ref NetworkPipelineStage.Requests requests, int systemHeaderSize)
        {
            // Request an update to see if a queued packet needs to be resent later or if an ack packet should be sent
            requests = NetworkPipelineStage.Requests.Update;

            var header = new ReliableUtility.PacketHeader();
            var reliable = (ReliableUtility.Context*)ctx.internalProcessBuffer;

            // Release any packets that might have been acknowledged since the last call.
            ReliableUtility.ReleaseAcknowledgedPackets(ctx);

            if (inboundBuffer.bufferLength > 0)
            {
                reliable->LastSentTime = ctx.timestamp;

                if (ReliableUtility.Write(ctx, inboundBuffer, ref header) < 0)
                {
                    // We failed to store the packet for possible later resends, abort and report this as a send error
                    inboundBuffer = default;
                    requests |= NetworkPipelineStage.Requests.Error;
                    return (int)Error.StatusCode.NetworkSendQueueFull;
                }

                ctx.header.Clear();
                ctx.header.WriteBytes((byte*)&header, UnsafeUtility.SizeOf<ReliableUtility.PacketHeader>());
                reliable->PreviousTimestamp = ctx.timestamp;
                return (int)Error.StatusCode.Success;
            }

            // At this point we know we're either in a resume or update call.

            if (reliable->Resume != ReliableUtility.NullEntry)
            {
                reliable->LastSentTime = ctx.timestamp;

                bool dummy = false;
                inboundBuffer = ReliableUtility.ResumeSend(ctx, out header, ref dummy);

                // Check if we need to resume again after this packet.
                reliable->Resume = ReliableUtility.GetNextSendResumeSequence(ctx);
                if (reliable->Resume != ReliableUtility.NullEntry)
                    requests |= NetworkPipelineStage.Requests.Resume;

                ctx.header.Clear();
                ctx.header.WriteBytes((byte*)&header, UnsafeUtility.SizeOf<ReliableUtility.PacketHeader>());
                reliable->PreviousTimestamp = ctx.timestamp;
                return (int)Error.StatusCode.Success;
            }

            // At this point we know we're in an update call.

            // Check if we need to resume (e.g. resent packets).
            reliable->Resume = ReliableUtility.GetNextSendResumeSequence(ctx);
            if (reliable->Resume != ReliableUtility.NullEntry)
                requests |= NetworkPipelineStage.Requests.Resume;

            if (ReliableUtility.ShouldSendAck(ctx))
            {
                reliable->LastSentTime = ctx.timestamp;

                ReliableUtility.WriteAckPacket(ctx, ref header);
                ctx.header.WriteBytes((byte*)&header, UnsafeUtility.SizeOf<ReliableUtility.PacketHeader>());
                reliable->PreviousTimestamp = ctx.timestamp;

                // TODO: Sending dummy byte over since the pipeline won't send an empty payload (ignored on receive)
                inboundBuffer.bufferWithHeadersLength = inboundBuffer.headerPadding + 1;
                inboundBuffer.bufferWithHeaders = (byte*)UnsafeUtility.Malloc(inboundBuffer.bufferWithHeadersLength, 8, Allocator.Temp);
                inboundBuffer.SetBufferFrombufferWithHeaders();
                return (int)Error.StatusCode.Success;
            }

            reliable->PreviousTimestamp = ctx.timestamp;
            return (int)Error.StatusCode.Success;
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(NetworkPipelineStage.InitializeConnectionDelegate))]
        private static void InitializeConnection(byte* staticInstanceBuffer, int staticInstanceBufferLength,
            byte* sendProcessBuffer, int sendProcessBufferLength, byte* recvProcessBuffer, int recvProcessBufferLength,
            byte* sharedProcessBuffer, int sharedProcessBufferLength)
        {
            ReliableUtility.Parameters param;
            UnsafeUtility.MemCpy(&param, staticInstanceBuffer, UnsafeUtility.SizeOf<ReliableUtility.Parameters>());
            if (sharedProcessBufferLength >= ReliableUtility.SharedCapacityNeeded(param) &&
                (sendProcessBufferLength + recvProcessBufferLength) >= ReliableUtility.ProcessCapacityNeeded(param) * 2)
            {
                ReliableUtility.InitializeContext(sharedProcessBuffer, sharedProcessBufferLength, sendProcessBuffer, sendProcessBufferLength, recvProcessBuffer, recvProcessBufferLength, param);
            }
        }
    }
}
