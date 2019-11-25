using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Networking.Transport.LowLevel.Unsafe;
using Unity.Networking.Transport.Protocols;

namespace Unity.Networking.Transport
{
    public struct IPCSocket : INetworkInterface
    {
        [NativeDisableContainerSafetyRestriction] private NativeQueue<IPCManager.IPCQueuedMessage> m_IPCQueue;
        [ReadOnly] private NativeArray<NetworkEndPoint> m_LocalEndPoint;

        public NetworkEndPoint LocalEndPoint => m_LocalEndPoint[0];
        public NetworkEndPoint RemoteEndPoint { get; }

        public NetworkFamily Family { get; }

        public void Initialize()
        {
            m_LocalEndPoint = new NativeArray<NetworkEndPoint>(1, Allocator.Persistent);
            m_LocalEndPoint[0] = IPCManager.Instance.CreateEndPoint();
            m_IPCQueue = new NativeQueue<IPCManager.IPCQueuedMessage>(Allocator.Persistent);
        }

        public void Dispose()
        {
            IPCManager.Instance.ReleaseEndPoint(m_LocalEndPoint[0]);
            m_LocalEndPoint.Dispose();
            m_IPCQueue.Dispose();
        }

        [BurstCompile]
        struct SendUpdate : IJob
        {
            public IPCManager ipcManager;
            public NativeQueue<IPCManager.IPCQueuedMessage> ipcQueue;

            public void Execute()
            {
                ipcManager.Update(ipcQueue);
            }
        }

        [BurstCompile]
        struct ReceiveJob<T> : IJob where T : struct, INetworkPacketReceiver
        {
            public T receiver;
            public IPCManager ipcManager;
            public NetworkEndPoint localEndPoint;

            public unsafe void Execute()
            {
                var address = new NetworkEndPoint {length = sizeof(NetworkEndPoint)};
                var header = new UdpCHeader();
                var stream = receiver.GetDataStream();
                receiver.ReceiveCount = 0;
                receiver.ReceiveErrorCode = 0;

                while (true)
                {
                    if (receiver.DynamicDataStreamSize())
                    {
                        while (stream.Length+NetworkParameterConstants.MTU >= stream.Capacity)
                            stream.Capacity *= 2;
                    }
                    else if (stream.Length >= stream.Capacity)
                        return;
                    var sliceOffset = stream.Length;
                    var result = NativeReceive(ref header, stream.GetUnsafePtr() + sliceOffset,
                        Math.Min(NetworkParameterConstants.MTU, stream.Capacity - stream.Length), ref address);
                    if (result <= 0)
                    {
                        // FIXME: handle error
                        if (result < 0)
                            receiver.ReceiveErrorCode = 10040;
                        return;
                    }

                    receiver.ReceiveCount += receiver.AppendPacket(address, header, result);
                }
            }

            unsafe int NativeReceive(ref UdpCHeader header, void* data, int length, ref NetworkEndPoint address)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (length <= 0)
                    throw new ArgumentException("Can't receive into 0 bytes or less of buffer memory");
#endif
                var iov = stackalloc network_iovec[2];

                fixed (byte* ptr = header.Data)
                {
                    iov[0].buf = ptr;
                    iov[0].len = UdpCHeader.Length;

                    iov[1].buf = data;
                    iov[1].len = length;
                }

#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (localEndPoint.Family != NetworkFamily.IPC || localEndPoint.nbo_port == 0)
                    throw new InvalidOperationException();
#endif
                return ipcManager.ReceiveMessageEx(localEndPoint, iov, 2, ref address);
            }
        }

        public JobHandle ScheduleReceive<T>(T receiver, JobHandle dep) where T : struct, INetworkPacketReceiver
        {
            var sendJob = new SendUpdate {ipcManager = IPCManager.Instance, ipcQueue = m_IPCQueue};
            var job = new ReceiveJob<T>
                {receiver = receiver, ipcManager = IPCManager.Instance, localEndPoint = m_LocalEndPoint[0]};
            dep = job.Schedule(JobHandle.CombineDependencies(dep, IPCManager.ManagerAccessHandle));
            dep = sendJob.Schedule(dep);
            IPCManager.ManagerAccessHandle = dep;
            return dep;
        }

        public int Bind(NetworkEndPoint endpoint)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (endpoint.Family != NetworkFamily.IPC || endpoint.nbo_port == 0)
                throw new InvalidOperationException();
#endif
            IPCManager.Instance.ReleaseEndPoint(m_LocalEndPoint[0]);
            m_LocalEndPoint[0] = endpoint;
            return 0;
        }

        public unsafe int SendMessage(network_iovec* iov, int iov_len, ref NetworkEndPoint address)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (m_LocalEndPoint[0].Family != NetworkFamily.IPC || m_LocalEndPoint[0].nbo_port == 0)
                throw new InvalidOperationException();
#endif
            return IPCManager.SendMessageEx(m_IPCQueue.AsParallelWriter(), m_LocalEndPoint[0], iov, iov_len, ref address);
        }
    }
}