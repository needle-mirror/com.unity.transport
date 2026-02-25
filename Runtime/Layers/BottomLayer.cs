using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace Unity.Networking.Transport
{
    /// <summary>
    /// The BottomLayer is the first one executed on receiving,
    /// and the last one executed on sending.
    /// </summary>
    internal struct BottomLayer : INetworkLayer
    {
        public int Initialize(ref NetworkSettings settings, ref ConnectionList connectionList, ref int packetPadding) => 0;

        public void Dispose() { }

        public JobHandle ScheduleReceive(ref ReceiveJobArguments arguments, JobHandle dependency) => dependency;

        public JobHandle ScheduleSend(ref SendJobArguments arguments, JobHandle dependency)
        {
            return new ClearJob
            {
                SendQueue = arguments.SendQueue,
            }.Schedule(dependency);
        }

        [BurstCompile]
        private struct ClearJob : IJob
        {
            public PacketsQueue SendQueue;

            public void Execute()
            {
                SendQueue.Clear();
            }
        }
    }
}
