using AOT;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Networking.Transport.Utilities;
using Unity.Burst;

namespace Unity.Networking.Transport
{
    /// <summary>
    /// The SimulatorPipelineStage could be added on either the client or server to simulate bad network conditions.
    /// It's best to add it as the last stage in the pipeline, then it will either drop the packet or add a delay right
    /// before it would go on the wire.
    /// </summary>
    [BurstCompile]
    public unsafe struct SimulatorPipelineStage : INetworkPipelineStage
    {
        static TransportFunctionPointer<NetworkPipelineStage.ReceiveDelegate> ReceiveFunctionPointer = new TransportFunctionPointer<NetworkPipelineStage.ReceiveDelegate>(Receive);
        static TransportFunctionPointer<NetworkPipelineStage.SendDelegate> SendFunctionPointer = new TransportFunctionPointer<NetworkPipelineStage.SendDelegate>(Send);
        static TransportFunctionPointer<NetworkPipelineStage.InitializeConnectionDelegate> InitializeConnectionFunctionPointer = new TransportFunctionPointer<NetworkPipelineStage.InitializeConnectionDelegate>(InitializeConnection);

        /// <summary>
        /// Statics the initialize using the specified static instance buffer
        /// </summary>
        /// <param name="staticInstanceBuffer">The static instance buffer</param>
        /// <param name="staticInstanceBufferLength">The static instance buffer length</param>
        /// <param name="settings">The net params</param>
        /// <returns>The network pipeline stage</returns>
        public NetworkPipelineStage StaticInitialize(byte* staticInstanceBuffer, int staticInstanceBufferLength, NetworkSettings settings)
        {
            SimulatorUtility.Parameters param = settings.GetSimulatorStageParameters();

            UnsafeUtility.MemCpy(staticInstanceBuffer, &param, UnsafeUtility.SizeOf<SimulatorUtility.Parameters>());

            return new NetworkPipelineStage(
                Receive: ReceiveFunctionPointer,
                Send: SendFunctionPointer,
                InitializeConnection: InitializeConnectionFunctionPointer,
                ReceiveCapacity: param.MaxPacketCount * (param.MaxPacketSize + UnsafeUtility.SizeOf<SimulatorUtility.DelayedPacket>()),
                SendCapacity: 0,
                HeaderCapacity: 0,
                SharedStateCapacity: UnsafeUtility.SizeOf<SimulatorUtility.Context>()
            );
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(NetworkPipelineStage.InitializeConnectionDelegate))]
        private static void InitializeConnection(byte* staticInstanceBuffer, int staticInstanceBufferLength,
            byte* sendProcessBuffer, int sendProcessBufferLength, byte* recvProcessBuffer, int recvProcessBufferLength,
            byte* sharedProcessBuffer, int sharedProcessBufferLength)
        {
            SimulatorUtility.Parameters param = default;

            UnsafeUtility.MemCpy(&param, staticInstanceBuffer, UnsafeUtility.SizeOf<SimulatorUtility.Parameters>());
            if (sharedProcessBufferLength >= UnsafeUtility.SizeOf<SimulatorUtility.Parameters>())
            {
                SimulatorUtility.InitializeContext(param, sharedProcessBuffer);
            }
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(NetworkPipelineStage.SendDelegate))]
        private static int Send(ref NetworkPipelineContext ctx, ref InboundSendBuffer inboundBuffer, ref NetworkPipelineStage.Requests requests, int systemHeaderSize)
        {
            return (int)Error.StatusCode.Success;
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(NetworkPipelineStage.ReceiveDelegate))]
        private static void Receive(ref NetworkPipelineContext ctx, ref InboundRecvBuffer inboundBuffer, ref NetworkPipelineStage.Requests requests, int systemHeaderSize)
        {
            var context = (SimulatorUtility.Context*)ctx.internalSharedProcessBuffer;
            var param = *(SimulatorUtility.Parameters*)ctx.staticInstanceBuffer;
            var simulator = new SimulatorUtility(param.MaxPacketCount, param.MaxPacketSize, param.PacketDelayMs, param.PacketJitterMs);

            if (inboundBuffer.bufferLength > param.MaxPacketSize)
            {
                //UnityEngine.Debug.LogWarning("Incoming packet too large for internal storage buffer. Passing through. [buffer=" + inboundBuffer.Length + " packet=" + param->MaxPacketSize + "]");
                // TODO: Add error code for this
                return;
            }

            var timestamp = ctx.timestamp;

            // Inbound buffer is empty if this is a resumed receive
            if (inboundBuffer.bufferLength > 0)
            {
                context->PacketCount++;

                if (simulator.ShouldDropPacket(context, param, timestamp))
                {
                    context->PacketDropCount++;
                    inboundBuffer = default;
                    return;
                }

                var bufferVec = default(InboundSendBuffer);
                bufferVec.bufferWithHeaders = inboundBuffer.buffer;
                bufferVec.bufferWithHeadersLength = inboundBuffer.bufferLength;
                bufferVec.buffer = inboundBuffer.buffer;
                bufferVec.bufferLength = inboundBuffer.bufferLength;
                bufferVec.headerPadding = 0;
                if (context->PacketDelayMs == 0 ||
                    !simulator.DelayPacket(ref ctx, bufferVec, ref requests, timestamp))
                {
                    return;
                }
            }

            InboundSendBuffer returnPacket = default;
            if (simulator.GetDelayedPacket(ref ctx, ref returnPacket, ref requests, timestamp))
            {
                inboundBuffer.buffer = returnPacket.bufferWithHeaders;
                inboundBuffer.bufferLength = returnPacket.bufferWithHeadersLength;
                return;
            }

            inboundBuffer = default;
        }

        public int StaticSize => UnsafeUtility.SizeOf<SimulatorUtility.Parameters>();
    }

    /// <summary>
    /// The simulator pipeline stage in send
    /// </summary>
    [BurstCompile]
    public unsafe struct SimulatorPipelineStageInSend : INetworkPipelineStage
    {
        static TransportFunctionPointer<NetworkPipelineStage.ReceiveDelegate> ReceiveFunctionPointer = new TransportFunctionPointer<NetworkPipelineStage.ReceiveDelegate>(Receive);
        static TransportFunctionPointer<NetworkPipelineStage.SendDelegate> SendFunctionPointer = new TransportFunctionPointer<NetworkPipelineStage.SendDelegate>(Send);
        static TransportFunctionPointer<NetworkPipelineStage.InitializeConnectionDelegate> InitializeConnectionFunctionPointer = new TransportFunctionPointer<NetworkPipelineStage.InitializeConnectionDelegate>(InitializeConnection);
        /// <summary>
        /// Statics the initialize using the specified static instance buffer
        /// </summary>
        /// <param name="staticInstanceBuffer">The static instance buffer</param>
        /// <param name="staticInstanceBufferLength">The static instance buffer length</param>
        /// <param name="settings">The net params</param>
        /// <returns>The network pipeline stage</returns>
        public NetworkPipelineStage StaticInitialize(byte* staticInstanceBuffer, int staticInstanceBufferLength, NetworkSettings settings)
        {
            SimulatorUtility.Parameters param = settings.GetSimulatorStageParameters();

            UnsafeUtility.MemCpy(staticInstanceBuffer, &param, UnsafeUtility.SizeOf<SimulatorUtility.Parameters>());

            return new NetworkPipelineStage(
                Receive: ReceiveFunctionPointer,
                Send: SendFunctionPointer,
                InitializeConnection: InitializeConnectionFunctionPointer,
                ReceiveCapacity: 0,
                SendCapacity: param.MaxPacketCount * (param.MaxPacketSize + UnsafeUtility.SizeOf<SimulatorUtility.DelayedPacket>()),
                HeaderCapacity: 0,
                SharedStateCapacity: UnsafeUtility.SizeOf<SimulatorUtility.Context>()
            );
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(NetworkPipelineStage.InitializeConnectionDelegate))]
        private static void InitializeConnection(byte* staticInstanceBuffer, int staticInstanceBufferLength,
            byte* sendProcessBuffer, int sendProcessBufferLength, byte* recvProcessBuffer, int recvProcessBufferLength,
            byte* sharedProcessBuffer, int sharedProcessBufferLength)
        {
            SimulatorUtility.Parameters param = default;

            UnsafeUtility.MemCpy(&param, staticInstanceBuffer, UnsafeUtility.SizeOf<SimulatorUtility.Parameters>());
            if (sharedProcessBufferLength >= UnsafeUtility.SizeOf<SimulatorUtility.Parameters>())
            {
                SimulatorUtility.InitializeContext(param, sharedProcessBuffer);
            }
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(NetworkPipelineStage.SendDelegate))]
        private static int Send(ref NetworkPipelineContext ctx, ref InboundSendBuffer inboundBuffer, ref NetworkPipelineStage.Requests requests, int systemHeaderSize)
        {
            var context = (SimulatorUtility.Context*)ctx.internalSharedProcessBuffer;
            var param = *(SimulatorUtility.Parameters*)ctx.staticInstanceBuffer;

            var simulator = new SimulatorUtility(param.MaxPacketCount, param.MaxPacketSize, param.PacketDelayMs, param.PacketJitterMs);
            if (inboundBuffer.headerPadding + inboundBuffer.bufferLength > param.MaxPacketSize)
            {
                //UnityEngine.Debug.LogWarning("Incoming packet too large for internal storage buffer. Passing through. [buffer=" + (inboundBuffer.headerPadding+inboundBuffer.buffer.Length) + " packet=" + param.MaxPacketSize + "]");
                return (int)Error.StatusCode.NetworkPacketOverflow;
            }

            var timestamp = ctx.timestamp;

            if (inboundBuffer.bufferLength > 0)
            {
                context->PacketCount++;

                if (simulator.ShouldDropPacket(context, param, timestamp))
                {
                    context->PacketDropCount++;
                    inboundBuffer = default;
                    return (int)Error.StatusCode.Success;
                }

                if (context->FuzzFactor > 0)
                {
                    simulator.FuzzPacket(context, ref inboundBuffer);
                }

                if (context->PacketDelayMs == 0 ||
                    !simulator.DelayPacket(ref ctx, inboundBuffer, ref requests, timestamp))
                {
                    return (int)Error.StatusCode.Success;
                }
            }

            InboundSendBuffer returnPacket = default;
            if (simulator.GetDelayedPacket(ref ctx, ref returnPacket, ref requests, timestamp))
            {
                inboundBuffer = returnPacket;
                return (int)Error.StatusCode.Success;
            }
            inboundBuffer = default;
            return (int)Error.StatusCode.Success;
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(NetworkPipelineStage.ReceiveDelegate))]
        private static void Receive(ref NetworkPipelineContext ctx, ref InboundRecvBuffer inboundBuffer,
            ref NetworkPipelineStage.Requests requests, int systemHeaderSize)
        {
        }

        /// <summary>
        /// Gets the value of the static size
        /// </summary>
        public int StaticSize => UnsafeUtility.SizeOf<SimulatorUtility.Parameters>();
    }
}
