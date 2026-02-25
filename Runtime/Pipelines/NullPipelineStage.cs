using AOT;
using Unity.Burst;

namespace Unity.Networking.Transport
{
    /// <summary>
    /// A pipeline stage that does nothing. This can be useful to create different "channels" of
    /// communication for different types of messages, even when these messages do not otherwise
    /// require any other guarantees provided by other pipeline stages.
    /// </summary>
    [BurstCompile]
    public unsafe struct NullPipelineStage : INetworkPipelineStage
    {
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
        }

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(NetworkPipelineStage.InitializeConnectionDelegate))]
        private static void InitializeConnection(byte* staticInstanceBuffer, int staticInstanceBufferLength,
            byte* sendProcessBuffer, int sendProcessBufferLength, byte* recvProcessBuffer, int recvProcessBufferLength,
            byte* sharedProcessBuffer, int sharedProcessBufferLength)
        {
        }

        /// <inheritdoc/>
        public NetworkPipelineStage StaticInitialize(byte* staticInstanceBuffer, int staticInstanceBufferLength, NetworkSettings netParams)
        {
            return new NetworkPipelineStage(
                Receive: new TransportFunctionPointer<NetworkPipelineStage.ReceiveDelegate>(Receive),
                Send: new TransportFunctionPointer<NetworkPipelineStage.SendDelegate>(Send),
                InitializeConnection: new TransportFunctionPointer<NetworkPipelineStage.InitializeConnectionDelegate>(InitializeConnection),
                ReceiveCapacity: 0,
                SendCapacity: 0,
                HeaderCapacity: 0,
                SharedStateCapacity: 0
            );
        }

        /// <inheritdoc/>
        public int StaticSize => 0;
    }
}
