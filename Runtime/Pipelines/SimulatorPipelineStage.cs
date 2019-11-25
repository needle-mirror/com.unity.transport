using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Networking.Transport.Utilities;

namespace Unity.Networking.Transport
{
    [NetworkPipelineInitilize(typeof(SimulatorUtility.Parameters))]
    public struct SimulatorPipelineStage : INetworkPipelineStage
    {
        private SimulatorUtility.Parameters m_SimulatorParams;

        // Setup simulation parameters which get capacity function depends on, so buffer size can be correctly allocated
        public void Initialize(SimulatorUtility.Parameters param)
        {
            m_SimulatorParams = param;
        }

        public unsafe NativeSlice<byte> Receive(NetworkPipelineContext ctx, NativeSlice<byte> inboundBuffer, ref bool needsResume, ref bool needsUpdate, ref bool needsSendUpdate)
        {
            var param = (SimulatorUtility.Context*) ctx.internalSharedProcessBuffer.GetUnsafePtr();
            var simulator = new SimulatorUtility(m_SimulatorParams.MaxPacketCount, m_SimulatorParams.MaxPacketSize, m_SimulatorParams.PacketDelayMs, m_SimulatorParams.PacketJitterMs);
            if (inboundBuffer.Length > m_SimulatorParams.MaxPacketSize)
            {
                //UnityEngine.Debug.LogWarning("Incoming packet too large for internal storage buffer. Passing through. [buffer=" + inboundBuffer.Length + " packet=" + param->MaxPacketSize + "]");
                // TODO: Add error code for this
                return inboundBuffer;
            }

            var timestamp = ctx.timestamp;

            // Inbound buffer is empty if this is a resumed receive
            if (inboundBuffer.Length > 0)
            {
                param->PacketCount++;

                if (simulator.ShouldDropPacket(param, m_SimulatorParams, timestamp))
                {
                    param->PacketDropCount++;
                    return default;
                }

                var bufferVec = default(InboundBufferVec);
                bufferVec.buffer1 = inboundBuffer;
                if (param->PacketDelayMs == 0 ||
                    !simulator.DelayPacket(ref ctx, bufferVec, ref needsUpdate, timestamp))
                {
                    return inboundBuffer;
                }
            }

            NativeSlice<byte> returnPacket = default(NativeSlice<byte>);
            if (simulator.GetDelayedPacket(ref ctx, ref returnPacket, ref needsResume, ref needsUpdate, timestamp))
                return returnPacket;

            return default;
        }

        public InboundBufferVec Send(NetworkPipelineContext ctx, InboundBufferVec inboundBuffer, ref bool needsResume, ref bool needsUpdate)
        {
            return inboundBuffer;
        }

        public unsafe void InitializeConnection(NativeSlice<byte> sendProcessBuffer, NativeSlice<byte> recvProcessBuffer,
            NativeSlice<byte> sharedProcessBuffer)
        {
            if (sharedProcessBuffer.Length >= UnsafeUtility.SizeOf<SimulatorUtility.Parameters>())
                SimulatorUtility.InitializeContext(m_SimulatorParams, sharedProcessBuffer);
        }

        public int ReceiveCapacity => m_SimulatorParams.MaxPacketCount * (m_SimulatorParams.MaxPacketSize+UnsafeUtility.SizeOf<SimulatorUtility.DelayedPacket>());
        public int SendCapacity => 0;
        public int HeaderCapacity => 0;
        public int SharedStateCapacity => UnsafeUtility.SizeOf<SimulatorUtility.Context>();
    }

    [NetworkPipelineInitilize(typeof(SimulatorUtility.Parameters))]
    public struct SimulatorPipelineStageInSend : INetworkPipelineStage
    {
        private SimulatorUtility.Parameters m_SimulatorParams;

        // Setup simulation parameters which get capacity function depends on, so buffer size can be correctly allocated
        public void Initialize(SimulatorUtility.Parameters param)
        {
            m_SimulatorParams = param;
        }

        public NativeSlice<byte> Receive(NetworkPipelineContext ctx, NativeSlice<byte> inboundBuffer, ref bool needsResume, ref bool needsUpdate, ref bool needsSendUpdate)
        {
            return new NativeSlice<byte>(inboundBuffer, 0, inboundBuffer.Length);
        }

        public unsafe InboundBufferVec Send(NetworkPipelineContext ctx, InboundBufferVec inboundBuffer, ref bool needsResume, ref bool needsUpdate)
        {
            var param = (SimulatorUtility.Context*) ctx.internalSharedProcessBuffer.GetUnsafePtr();
            var simulator = new SimulatorUtility(m_SimulatorParams.MaxPacketCount, m_SimulatorParams.MaxPacketSize, m_SimulatorParams.PacketDelayMs, m_SimulatorParams.PacketJitterMs);
            if (inboundBuffer.buffer1.Length+inboundBuffer.buffer2.Length > m_SimulatorParams.MaxPacketSize)
            {
                //UnityEngine.Debug.LogWarning("Incoming packet too large for internal storage buffer. Passing through. [buffer=" + (inboundBuffer.buffer1.Length+inboundBuffer.buffer2.Length) + " packet=" + param->MaxPacketSize + "]");
                return inboundBuffer;
            }

            var timestamp = ctx.timestamp;

            if (inboundBuffer.buffer1.Length > 0)
            {
                param->PacketCount++;

                if (simulator.ShouldDropPacket(param, m_SimulatorParams, timestamp))
                {
                    param->PacketDropCount++;
                    return default;
                }

                if (param->PacketDelayMs == 0 ||
                    !simulator.DelayPacket(ref ctx, inboundBuffer, ref needsUpdate, timestamp))
                {
                    return inboundBuffer;
                }
            }

            NativeSlice<byte> returnPacket = default;
            if (simulator.GetDelayedPacket(ref ctx, ref returnPacket, ref needsResume, ref needsUpdate, timestamp))
            {
                inboundBuffer.buffer1 = returnPacket;
                inboundBuffer.buffer2 = default;
                return inboundBuffer;
            }

            return default;
        }

        public void InitializeConnection(NativeSlice<byte> sendProcessBuffer, NativeSlice<byte> recvProcessBuffer,
            NativeSlice<byte> sharedProcessBuffer)
        {
            if (sharedProcessBuffer.Length >= UnsafeUtility.SizeOf<SimulatorUtility.Parameters>())
                SimulatorUtility.InitializeContext(m_SimulatorParams, sharedProcessBuffer);
        }

        public int ReceiveCapacity => 0;
        public int SendCapacity => m_SimulatorParams.MaxPacketCount * (m_SimulatorParams.MaxPacketSize+UnsafeUtility.SizeOf<SimulatorUtility.DelayedPacket>());
        public int HeaderCapacity => 0;
        public int SharedStateCapacity => UnsafeUtility.SizeOf<SimulatorUtility.Context>();
    }
}
