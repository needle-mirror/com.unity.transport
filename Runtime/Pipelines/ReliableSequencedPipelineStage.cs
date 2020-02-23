using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Networking.Transport.Utilities;
using Unity.Burst;

namespace Unity.Networking.Transport
{
    [BurstCompile]
    public unsafe struct ReliableSequencedPipelineStage : INetworkPipelineStage
    {
        static TransportFunctionPointer<NetworkPipelineStage.ReceiveDelegate> ReceiveFunctionPointer = new TransportFunctionPointer<NetworkPipelineStage.ReceiveDelegate>(Receive);
        static TransportFunctionPointer<NetworkPipelineStage.SendDelegate> SendFunctionPointer = new TransportFunctionPointer<NetworkPipelineStage.SendDelegate>(Send);
        static TransportFunctionPointer<NetworkPipelineStage.InitializeConnectionDelegate> InitializeConnectionFunctionPointer = new TransportFunctionPointer<NetworkPipelineStage.InitializeConnectionDelegate>(InitializeConnection);
        public NetworkPipelineStage StaticInitialize(byte* staticInstanceBuffer, int staticInstanceBufferLength, INetworkParameter[] netParams)
        {
            ReliableUtility.Parameters param = default;
            foreach (var netParam in netParams)
            {
                if (netParam.GetType() == typeof(ReliableUtility.Parameters))
                    param = (ReliableUtility.Parameters)netParam;
            }
            if (param.WindowSize == 0)
                param = new ReliableUtility.Parameters{WindowSize = ReliableUtility.ParameterConstants.WindowSize};
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

        [BurstCompile]
        private static void Receive(ref NetworkPipelineContext ctx, ref InboundRecvBuffer inboundBuffer, ref NetworkPipelineStage.Requests requests)
        {
            // Request a send update to see if a queued packet needs to be resent later or if an ack packet should be sent
            requests = NetworkPipelineStage.Requests.SendUpdate;
            bool needsResume = false;

            var header = default(ReliableUtility.PacketHeader);
            var slice = default(InboundRecvBuffer);
            ReliableUtility.Context* reliable = (ReliableUtility.Context*) ctx.internalProcessBuffer;
            ReliableUtility.SharedContext* shared = (ReliableUtility.SharedContext*) ctx.internalSharedProcessBuffer;
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
                    var nextExpectedSequenceId = (ushort) (reliable->Delivered + 1);
                    if (result == nextExpectedSequenceId)
                    {
                        reliable->Delivered = result;
                        slice = inboundBuffer.Slice(UnsafeUtility.SizeOf<ReliableUtility.PacketHeader>());

                        if (needsResume = SequenceHelpers.GreaterThan16((ushort) shared->ReceivedPackets.Sequence,
                            (ushort) result))
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

        [BurstCompile]
        private static void Send(ref NetworkPipelineContext ctx, ref InboundSendBuffer inboundBuffer, ref NetworkPipelineStage.Requests requests)
        {
            // Request an update to see if a queued packet needs to be resent later or if an ack packet should be sent
            requests = NetworkPipelineStage.Requests.Update;
            bool needsResume = false;

            var header = new ReliableUtility.PacketHeader();
            var reliable = (ReliableUtility.Context*) ctx.internalProcessBuffer;

            needsResume = ReliableUtility.ReleaseOrResumePackets(ctx);
            if (needsResume)
                requests |= NetworkPipelineStage.Requests.Resume;

            if (inboundBuffer.bufferLength > 0)
            {
                reliable->LastSentTime = ctx.timestamp;

                ReliableUtility.Write(ctx, inboundBuffer, ref header);
                ctx.header.WriteBytes((byte*)&header, UnsafeUtility.SizeOf<ReliableUtility.PacketHeader>());
                if (reliable->Resume != ReliableUtility.NullEntry)
                    requests |= NetworkPipelineStage.Requests.Resume;

                reliable->PreviousTimestamp = ctx.timestamp;
                return;
            }

            if (reliable->Resume != ReliableUtility.NullEntry)
            {
                reliable->LastSentTime = ctx.timestamp;
                inboundBuffer = ReliableUtility.ResumeSend(ctx, out header, ref needsResume);
                if (needsResume)
                    requests |= NetworkPipelineStage.Requests.Resume;
                ctx.header.Clear();
                ctx.header.WriteBytes((byte*)&header, UnsafeUtility.SizeOf<ReliableUtility.PacketHeader>());
                reliable->PreviousTimestamp = ctx.timestamp;
                return;
            }

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
                return;
            }
            reliable->PreviousTimestamp = ctx.timestamp;
        }

        [BurstCompile]
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
