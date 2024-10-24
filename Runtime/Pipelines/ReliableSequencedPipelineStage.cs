using System;
using AOT;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Networking.Transport.Utilities;
using Unity.Burst;

namespace Unity.Networking.Transport
{
    /// <summary>
    /// <para>
    /// This pipeline stage can be used to ensure that packets sent through it will be delivered,
    /// and will be delivered in order. This is done by sending acknowledgements for received
    /// packets, and resending packets that have not been acknowledged in a while.
    /// </para>
    /// <para>
    /// Note that a consequence of these guarantees is that if a packet is lost, subsequent packets
    /// will not be delivered until the lost packet has been resent and delivered. This is called
    /// <see href="https://en.wikipedia.org/wiki/Head-of-line_blocking">head-of-line blocking</see>
    /// and can add significant latency to delivered packets when it occurs. For this reason, only
    /// send through this pipeline traffic which must absolutely be delivered in order (e.g. RPCs
    /// or player actions). State updates that will be resent later anyway (e.g. snapshots) should
    /// not be sent through this pipeline stage.
    /// </para>
    /// <para>
    /// Another reason to limit the amount of traffic sent through this pipeline is because it has
    /// limited bandwidth. Because of the need to keep packets around in case they need to be
    /// resent, only a limited number of packets can be in-flight at a time. This limit, called the
    /// window size, is 32 by default and can be increased to 64. See the documentation on pipelines
    /// for further details.
    /// </para>
    /// </summary>
    [BurstCompile]
    public unsafe struct ReliableSequencedPipelineStage : INetworkPipelineStage
    {
        static TransportFunctionPointer<NetworkPipelineStage.ReceiveDelegate> ReceiveFunctionPointer = new TransportFunctionPointer<NetworkPipelineStage.ReceiveDelegate>(Receive);
        static TransportFunctionPointer<NetworkPipelineStage.SendDelegate> SendFunctionPointer = new TransportFunctionPointer<NetworkPipelineStage.SendDelegate>(Send);
        static TransportFunctionPointer<NetworkPipelineStage.InitializeConnectionDelegate> InitializeConnectionFunctionPointer = new TransportFunctionPointer<NetworkPipelineStage.InitializeConnectionDelegate>(InitializeConnection);

        /// <inheritdoc/>
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
                HeaderCapacity: ReliableUtility.PacketHeaderWireSize(param.WindowSize),
                SharedStateCapacity: ReliableUtility.SharedCapacityNeeded(param)
            );
        }

        /// <inheritdoc/>
        public int StaticSize => UnsafeUtility.SizeOf<ReliableUtility.Parameters>();

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(NetworkPipelineStage.ReceiveDelegate))]
        private static void Receive(ref NetworkPipelineContext ctx, ref InboundRecvBuffer inboundBuffer, ref NetworkPipelineStage.Requests requests, int systemHeaderSize)
        {
            // Request a send update to see if a queued packet needs to be resent later or if an ack packet should be sent
            requests = NetworkPipelineStage.Requests.SendUpdate;

            var header = new ReliableUtility.PacketHeader();
            var reliable = (ReliableUtility.Context*)ctx.internalProcessBuffer;
            var shared = (ReliableUtility.SharedContext*)ctx.internalSharedProcessBuffer;

            // We've received a packet (i.e. not a resume call).
            if (inboundBuffer.buffer != null)
            {
                var inboundArray = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<byte>(inboundBuffer.buffer, inboundBuffer.bufferLength, Allocator.Invalid);

#if ENABLE_UNITY_COLLECTIONS_CHECKS
                var safetyHandle = AtomicSafetyHandle.GetTempMemoryHandle();
                NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref inboundArray, safetyHandle);
#endif

                var reader = new DataStreamReader(inboundArray);
                reader.ReadBytesUnsafe((byte*)&header, ReliableUtility.PacketHeaderWireSize(ctx));
                var packetWithoutHeader = inboundBuffer.Slice(ReliableUtility.PacketHeaderWireSize(ctx));

                // Drop the packet. We'll set it back if it was expected.
                inboundBuffer = default;

                if (header.Type == (ushort)ReliableUtility.PacketType.Ack)
                {
                    // Packet is just an ACK.
                    ReliableUtility.ReadAckPacket(ctx, header);
                }
                else
                {
                    // Packet contains a payload.
                    var receivedSequenceId = ReliableUtility.Read(ctx, header);
                    if (receivedSequenceId >= 0)
                    {
                        var nextExpectedSequenceId = (ushort)(reliable->Delivered + 1);

                        if (receivedSequenceId == nextExpectedSequenceId)
                        {
                            // Received packet is the next one in the sequence. Return it.
                            reliable->Delivered = receivedSequenceId;
                            inboundBuffer = packetWithoutHeader;
                        }
                        else
                        {
                            // Received packet is later in the sequence. Save it for later.
                            ReliableUtility.SetPacket(ctx.internalProcessBuffer, receivedSequenceId, packetWithoutHeader);
                        }
                    }
                }
            }

            // If in a resume call or if the inbound buffer has been cleared while processing a
            // packet (i.e. it was an ACK or an out-of-order packet), then try to resume the next
            // packet in the sequence.
            if (inboundBuffer.buffer == null)
                inboundBuffer = ReliableUtility.ResumeReceive(ctx);

            // Check if we need to resume.
            if (ReliableUtility.NeedResumeReceive(ctx))
                requests |= NetworkPipelineStage.Requests.Resume;
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

            if (inboundBuffer.buffer != null)
            {
                if (ReliableUtility.Write(ctx, inboundBuffer, ref header) < 0)
                {
                    // We failed to store the packet for possible later resends, abort and report this as a send error
                    inboundBuffer = default;
                    requests |= NetworkPipelineStage.Requests.Error;
                    return (int)Error.StatusCode.NetworkSendQueueFull;
                }

                ctx.header.Clear();
                ctx.header.WriteBytesUnsafe((byte*)&header, ReliableUtility.PacketHeaderWireSize(ctx));
                return (int)Error.StatusCode.Success;
            }

            // At this point we know we're either in a resume or update call.

            if (reliable->Resume != ReliableUtility.NullEntry)
            {
                inboundBuffer = ReliableUtility.ResumeSend(ctx, out header);

                // Check if we need to resume again after this packet.
                reliable->Resume = ReliableUtility.GetNextSendResumeSequence(ctx);
                if (reliable->Resume != ReliableUtility.NullEntry)
                    requests |= NetworkPipelineStage.Requests.Resume;

                ctx.header.Clear();
                ctx.header.WriteBytesUnsafe((byte*)&header, ReliableUtility.PacketHeaderWireSize(ctx));
                return (int)Error.StatusCode.Success;
            }

            // At this point we know we're in an update call.

            // Check if we need to resume (e.g. resend packets).
            reliable->Resume = ReliableUtility.GetNextSendResumeSequence(ctx);
            if (reliable->Resume != ReliableUtility.NullEntry)
                requests |= NetworkPipelineStage.Requests.Resume;

            if (ReliableUtility.ShouldSendAck(ctx))
            {
                ReliableUtility.WriteAckPacket(ctx, ref header);

                ctx.header.WriteBytesUnsafe((byte*)&header, ReliableUtility.PacketHeaderWireSize(ctx));

                // TODO: Sending dummy byte over since the pipeline won't send an empty payload (ignored on receive)
                inboundBuffer.bufferWithHeadersLength = inboundBuffer.headerPadding + 1;
                inboundBuffer.bufferWithHeaders = (byte*)UnsafeUtility.Malloc(inboundBuffer.bufferWithHeadersLength, 8, Allocator.Temp);
                inboundBuffer.SetBufferFromBufferWithHeaders();
                return (int)Error.StatusCode.Success;
            }

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

            if (sharedProcessBufferLength != ReliableUtility.SharedCapacityNeeded(param))
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                throw new InvalidOperationException("sharedProcessBufferLength is wrong length for ReliableUtility.Parameters!");
#else
                return;
#endif
            }

            if (sendProcessBufferLength + recvProcessBufferLength < ReliableUtility.ProcessCapacityNeeded(param) * 2)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                throw new InvalidOperationException("sendProcessBufferLength + recvProcessBufferLength is wrong length for ReliableUtility.ProcessCapacityNeeded!");
#else
                return;
#endif
            }

            ReliableUtility.InitializeContext(sharedProcessBuffer, sharedProcessBufferLength, sendProcessBuffer, sendProcessBufferLength, recvProcessBuffer, recvProcessBufferLength, param);
        }
    }
}
