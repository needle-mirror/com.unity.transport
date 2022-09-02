using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace Unity.Networking.Transport
{
    internal struct LogLayer : INetworkLayer
    {
        static private int s_InstancesCount;

        private int m_InstanceId;

        public void Dispose() {}

        public int Initialize(ref NetworkSettings settings, ref ConnectionList connectionList, ref int packetPadding)
        {
            m_InstanceId = ++s_InstancesCount;
            return 0;
        }

        public JobHandle ScheduleReceive(ref ReceiveJobArguments arguments, JobHandle dependency)
        {
            return new LogJob
            {
                Label = $"[{m_InstanceId}] Received",
                Queue = arguments.ReceiveQueue,
            }.Schedule(dependency);
        }

        public JobHandle ScheduleSend(ref SendJobArguments arguments, JobHandle dependency)
        {
            return new LogJob
            {
                Label = $"[{m_InstanceId}] Sent",
                Queue = arguments.SendQueue,
            }.Schedule(dependency);
        }

        [BurstCompile]
        private struct LogJob : IJob
        {
            public FixedString64Bytes Label;
            public PacketsQueue Queue;

            public void Execute()
            {
                var count = Queue.Count;
                for (int i = 0; i < count; i++)
                {
                    var packetProcessor = Queue[i];

                    if (packetProcessor.Length <= 0)
                        continue;

                    var str = new FixedString4096Bytes(Label);
                    str.Append(FixedString.Format(" {0} bytes [Endpoint: {1}]: ", packetProcessor.Length, packetProcessor.EndpointRef.ToFixedString()));
                    if (AppendPayload(ref str, ref packetProcessor))
                    {
                        UnityEngine.Debug.Log(str);
                    }
                    else
                    {
                        UnityEngine.Debug.Log(str);
                        UnityEngine.Debug.Log("Message truncated");
                    }
                }
            }

            private bool AppendPayload(ref FixedString4096Bytes str, ref PacketProcessor packetProcessor)
            {
                var length = packetProcessor.Length;
                for (int i = 0; i < length; i++)
                {
                    var payloadStr = FixedString.Format("{0:X2} ", packetProcessor.GetPayloadDataRef<byte>(i));

                    if (str.Capacity - str.Length < payloadStr.Length)
                        return false;

                    str.Append(payloadStr);
                }
                return true;
            }
        }
    }
}
