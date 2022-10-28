using System;
using AOT;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Networking.Transport.Protocols;
using Unity.Networking.Transport.Utilities;

namespace Unity.Networking.Transport
{
    [BurstCompile]
    public struct IPCNetworkInterface : INetworkInterface
    {
        [ReadOnly] private NativeArray<NetworkEndpoint> m_LocalEndpoint;

        public NetworkEndpoint LocalEndpoint => m_LocalEndpoint[0];

        public int Initialize(ref NetworkSettings settings, ref int packetPadding)
        {
            IPCManager.Instance.AddRef();
            m_LocalEndpoint = new NativeArray<NetworkEndpoint>(1, Allocator.Persistent);
            return 0;
        }

        public void Dispose()
        {
            m_LocalEndpoint.Dispose();
            IPCManager.Instance.Release();
        }

        [BurstCompile]
        struct SendUpdate : IJob
        {
            public IPCManager ipcManager;
            public PacketsQueue SendQueue;
            public NetworkEndpoint localEndPoint;

            public void Execute()
            {
                ipcManager.Update(localEndPoint, ref SendQueue);
            }
        }

        [BurstCompile]
        struct ReceiveJob : IJob
        {
            public PacketsQueue ReceiveQueue;
            public OperationResult ReceiveResult;
            public IPCManager ipcManager;
            public NetworkEndpoint localEndPoint;

            public unsafe void Execute()
            {
                while (ipcManager.HasDataAvailable(localEndPoint))
                {
                    if (!ReceiveQueue.EnqueuePacket(out var packetProcessor))
                    {
                        ReceiveResult.ErrorCode = (int)Error.StatusCode.NetworkReceiveQueueFull;
                        return;
                    }

                    var ptr = packetProcessor.GetUnsafePayloadPtr();
                    var endpoint = default(NetworkEndpoint);
                    var result = NativeReceive(ptr, packetProcessor.Capacity, ref endpoint);

                    packetProcessor.EndpointRef = endpoint;

                    if (result <= 0)
                    {
                        if (result != 0)
                            ReceiveResult.ErrorCode = -result;
                        return;
                    }

                    packetProcessor.SetUnsafeMetadata(result);
                }
            }

            unsafe int NativeReceive(void* data, int length, ref NetworkEndpoint address)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (length <= 0)
                    throw new ArgumentException("Can't receive into 0 bytes or less of buffer memory");
#endif
                return ipcManager.ReceiveMessageEx(localEndPoint, data, length, ref address);
            }
        }

        public JobHandle ScheduleReceive(ref ReceiveJobArguments arguments, JobHandle dep)
        {
            var job = new ReceiveJob
            {
                ReceiveQueue = arguments.ReceiveQueue,
                ipcManager = IPCManager.Instance,
                localEndPoint = m_LocalEndpoint[0],
                ReceiveResult = arguments.ReceiveResult,
            };
            dep = job.Schedule(JobHandle.CombineDependencies(dep, IPCManager.ManagerAccessHandle));
            IPCManager.ManagerAccessHandle = dep;
            return dep;
        }

        public JobHandle ScheduleSend(ref SendJobArguments arguments, JobHandle dep)
        {
            var sendJob = new SendUpdate {ipcManager = IPCManager.Instance, SendQueue = arguments.SendQueue, localEndPoint = m_LocalEndpoint[0]};
            dep = sendJob.Schedule(JobHandle.CombineDependencies(dep, IPCManager.ManagerAccessHandle));
            IPCManager.ManagerAccessHandle = dep;
            return dep;
        }

        public unsafe int Bind(NetworkEndpoint endpoint)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (!endpoint.IsLoopback && !endpoint.IsAny)
                throw new InvalidOperationException($"Trying to bind IPC interface to a non-loopback endpoint ({endpoint})");
#endif

            m_LocalEndpoint[0] = IPCManager.Instance.CreateEndpoint(endpoint.Port);
            return 0;
        }

        public int Listen()
        {
            return 0;
        }
    }
}
