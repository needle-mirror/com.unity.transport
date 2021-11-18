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
    /// <summary>
    /// The ipc network interface
    /// </summary>
    [BurstCompile]
    public struct IPCNetworkInterface : INetworkInterface
    {
        /// <summary>
        /// The localendpoint
        /// </summary>
        [ReadOnly] private NativeArray<NetworkInterfaceEndPoint> m_LocalEndPoint;

        /// <summary>
        /// Gets the value of the local end point
        /// </summary>
        public NetworkInterfaceEndPoint LocalEndPoint => m_LocalEndPoint[0];

        /// <summary>
        /// Creates an interface end point. Only available for loopback addresses.
        /// </summary>
        /// <param name="address">Loopback address</param>
        /// <param name="endpoint">The endpoint</param>
        /// <returns>The status code of the result, 0 being a success.</returns>
        public int CreateInterfaceEndPoint(NetworkEndPoint address, out NetworkInterfaceEndPoint endpoint)
        {
            if (!address.IsLoopback && !address.IsAny)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                throw new ArgumentException("IPC network driver can only handle loopback addresses");
#else
                endpoint = default(NetworkInterfaceEndPoint);
                return (int)Error.StatusCode.NetworkArgumentMismatch;
#endif
            }

            endpoint = IPCManager.Instance.CreateEndPoint(address.Port);
            return (int)Error.StatusCode.Success;
        }

        /// <summary>
        /// Retrieves an already created endpoint with port or creates one.
        /// </summary>
        /// <param name="endpoint">The loopback endpoint</param>
        /// <returns>NetworkEndPoint</returns>
        public NetworkEndPoint GetGenericEndPoint(NetworkInterfaceEndPoint endpoint)
        {
            if (!IPCManager.Instance.GetEndPointPort(endpoint, out var port))
                return default;
            return NetworkEndPoint.LoopbackIpv4.WithPort(port);
        }

        /// <summary>
        /// Initializes the interface passing in optional <see cref="INetworkParameter"/>
        /// </summary>
        /// <param name="param">The param</param>
        /// <returns>The status code of the result, 0 being a success.</returns>
        public int Initialize(NetworkSettings settings)
        {
            IPCManager.Instance.AddRef();
            m_LocalEndPoint = new NativeArray<NetworkInterfaceEndPoint>(1, Allocator.Persistent);

            var ep = default(NetworkInterfaceEndPoint);
            var result = 0;

            if ((result = CreateInterfaceEndPoint(NetworkEndPoint.LoopbackIpv4, out ep)) != (int)Error.StatusCode.Success)
                return result;

            m_LocalEndPoint[0] = ep;
            return 0;
        }

        /// <summary>
        /// Cleans up both the local end point and the IPCManager instance.
        /// </summary>
        public void Dispose()
        {
            m_LocalEndPoint.Dispose();
            IPCManager.Instance.Release();
        }

        [BurstCompile]
        struct SendUpdate : IJob
        {
            public IPCManager ipcManager;
            public NativeQueue<QueuedSendMessage> ipcQueue;
            [ReadOnly] public NativeArray<NetworkInterfaceEndPoint> localEndPoint;

            public void Execute()
            {
                ipcManager.Update(localEndPoint[0], ipcQueue);
            }
        }

        [BurstCompile]
        struct ReceiveJob : IJob
        {
            public NetworkPacketReceiver receiver;
            public IPCManager ipcManager;
            public NetworkInterfaceEndPoint localEndPoint;

            public unsafe void Execute()
            {
                receiver.ReceiveErrorCode = 0;

                while (true)
                {
                    var size = NetworkParameterConstants.MTU;
                    var ptr = receiver.AllocateMemory(ref size);
                    if (ptr == IntPtr.Zero)
                        return;

                    var endpoint = default(NetworkInterfaceEndPoint);
                    var resultReceive = NativeReceive((byte*)ptr.ToPointer(), size, ref endpoint);
                    if (resultReceive <= 0)
                    {
                        if (resultReceive != 0)
                            receiver.ReceiveErrorCode = -resultReceive;
                        return;
                    }

                    var resultAppend = receiver.AppendPacket(ptr, ref endpoint, resultReceive);
                    if (resultAppend == false)
                        return;
                }
            }

            /// <summary>
            /// Reads incoming data using native methods.
            /// </summary>
            /// <param name="data">The data</param>
            /// <param name="length">The length</param>
            /// <param name="address">The address</param>
            /// <returns>The int</returns>
            unsafe int NativeReceive(void* data, int length, ref NetworkInterfaceEndPoint address)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (length <= 0)
                    throw new ArgumentException("Can't receive into 0 bytes or less of buffer memory");
#endif
                return ipcManager.ReceiveMessageEx(localEndPoint, data, length, ref address);
            }
        }

        /// <summary>
        /// Schedule a ReceiveJob. This is used to read data from your supported medium and pass it to the AppendData function
        /// supplied by <see cref="NetworkDriver"/>
        /// </summary>
        /// <param name="receiver">A <see cref="NetworkDriver"/> used to parse the data received.</param>
        /// <param name="dep">A <see cref="JobHandle"/> to any dependency we might have.</param>
        /// <returns>A <see cref="JobHandle"/> to our newly created ScheduleReceive Job.</returns>
        public JobHandle ScheduleReceive(NetworkPacketReceiver receiver, JobHandle dep)
        {
            var job = new ReceiveJob
            {
                receiver = receiver,
                ipcManager = IPCManager.Instance,
                localEndPoint = LocalEndPoint
            };
            dep = job.Schedule(JobHandle.CombineDependencies(dep, IPCManager.ManagerAccessHandle));
            IPCManager.ManagerAccessHandle = dep;
            return dep;
        }

        /// <summary>
        /// Schedule a SendJob. This is used to flush send queues to your supported medium
        /// </summary>
        /// <param name="sendQueue">The send queue which can be used to emulate parallel send.</param>
        /// <param name="dep">A <see cref="JobHandle"/> to any dependency we might have.</param>
        /// <returns>A <see cref="JobHandle"/> to our newly created ScheduleSend Job.</returns>
        public JobHandle ScheduleSend(NativeQueue<QueuedSendMessage> sendQueue, JobHandle dep)
        {
            var sendJob = new SendUpdate {ipcManager = IPCManager.Instance, ipcQueue = sendQueue, localEndPoint = m_LocalEndPoint};
            dep = sendJob.Schedule(JobHandle.CombineDependencies(dep, IPCManager.ManagerAccessHandle));
            IPCManager.ManagerAccessHandle = dep;
            return dep;
        }

        /// <summary>
        /// Binds the medium to a specific endpoint.
        /// </summary>
        /// <param name="endpoint">
        /// A valid <see cref="NetworkInterfaceEndPoint"/>.
        /// </param>
        /// <returns>0 on Success</returns>
        public unsafe int Bind(NetworkInterfaceEndPoint endpoint)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (endpoint.dataLength != 4 || *(int*)endpoint.data == 0)
                throw new InvalidOperationException();
#endif
            m_LocalEndPoint[0] = endpoint;
            return 0;
        }

        /// <summary>
        /// Start listening for incoming connections. This is normally a no-op for real UDP sockets.
        /// </summary>
        /// <returns>0 on Success</returns>
        public int Listen()
        {
            return 0;
        }

        /// <summary>
        /// Burst function pointer called at the beginning of the message sending routine.
        /// </summary>
        static TransportFunctionPointer<NetworkSendInterface.BeginSendMessageDelegate> BeginSendMessageFunctionPointer = new TransportFunctionPointer<NetworkSendInterface.BeginSendMessageDelegate>(BeginSendMessage);
        /// <summary>
        /// Burst function pointer called at the end of the message sending routine.
        /// </summary>
        static TransportFunctionPointer<NetworkSendInterface.EndSendMessageDelegate> EndSendMessageFunctionPointer = new TransportFunctionPointer<NetworkSendInterface.EndSendMessageDelegate>(EndSendMessage);
        /// <summary>
        /// Burst function pointer called if a scheduled message is aborted.
        /// </summary>
        static TransportFunctionPointer<NetworkSendInterface.AbortSendMessageDelegate> AbortSendMessageFunctionPointer = new TransportFunctionPointer<NetworkSendInterface.AbortSendMessageDelegate>(AbortSendMessage);

        /// <summary>
        /// Creates the send interface
        /// </summary>
        /// <returns>The network send interface</returns>
        public NetworkSendInterface CreateSendInterface()
        {
            return new NetworkSendInterface
            {
                BeginSendMessage = BeginSendMessageFunctionPointer,
                EndSendMessage = EndSendMessageFunctionPointer,
                AbortSendMessage = AbortSendMessageFunctionPointer,
            };
        }

        /// <summary>
        /// Begins the send message using the specified handle
        /// </summary>
        /// <param name="handle">The handle</param>
        /// <param name="userData">The user data</param>
        /// <param name="requiredPayloadSize">The required payload size</param>
        /// <returns>The int</returns>
        [BurstCompile(DisableDirectCall = true)]
        [AOT.MonoPInvokeCallback(typeof(NetworkSendInterface.BeginSendMessageDelegate))]
        private static unsafe int BeginSendMessage(out NetworkInterfaceSendHandle handle, IntPtr userData, int requiredPayloadSize)
        {
            handle.id = 0;
            handle.size = 0;
            handle.capacity = requiredPayloadSize;
            handle.data = (IntPtr)UnsafeUtility.Malloc(handle.capacity, 8, Allocator.Temp);
            handle.flags = default;
            return 0;
        }

        /// <summary>
        /// Ends the send message using the specified handle
        /// </summary>
        /// <param name="handle">The handle</param>
        /// <param name="address">The address</param>
        /// <param name="userData">The user data</param>
        /// <param name="sendQueueHandle">The send queue handle</param>
        /// <returns>The int</returns>
        [BurstCompile(DisableDirectCall = true)]
        [AOT.MonoPInvokeCallback(typeof(NetworkSendInterface.EndSendMessageDelegate))]
        private static unsafe int EndSendMessage(ref NetworkInterfaceSendHandle handle, ref NetworkInterfaceEndPoint address, IntPtr userData, ref NetworkSendQueueHandle sendQueueHandle)
        {
            var sendQueue = sendQueueHandle.FromHandle();
            var msg = default(QueuedSendMessage);
            msg.Dest = address;
            msg.DataLength = handle.size;
            UnsafeUtility.MemCpy(msg.Data, (void*)handle.data, handle.size);

            sendQueue.Enqueue(msg);
            return handle.size;
        }

        /// <summary>
        /// Aborts the send message using the specified handle
        /// </summary>
        /// <param name="handle">The handle</param>
        /// <param name="userData">The user data</param>
        [BurstCompile(DisableDirectCall = true)]
        [AOT.MonoPInvokeCallback(typeof(NetworkSendInterface.AbortSendMessageDelegate))]
        private static void AbortSendMessage(ref NetworkInterfaceSendHandle handle, IntPtr userData)
        {
        }
    }
}
