#if !UNITY_WEBGL
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Baselib.LowLevel;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Networking.Transport.Utilities.LowLevel.Unsafe;
using Unity.Networking.Transport.Protocols;
using Unity.Networking.Transport.Utilities;
using ErrorState = Unity.Baselib.LowLevel.Binding.Baselib_ErrorState;
using ErrorCode = Unity.Baselib.LowLevel.Binding.Baselib_ErrorCode;

namespace Unity.Networking.Transport
{
    using NetworkRequest = Binding.Baselib_RegisteredNetwork_Request;
    using NetworkEndpoint = Binding.Baselib_RegisteredNetwork_Endpoint;
    using NetworkSocket = Binding.Baselib_RegisteredNetwork_Socket_UDP;

    public static class BaselibNetworkParameterExtensions
    {
        internal const int k_defaultRxQueueSize = 64;
        internal const int k_defaultTxQueueSize = 64;
        internal const uint k_defaultMaximumPayloadSize = 2000;

        /// <summary>
        /// Sets the <see cref="BaselibNetworkParameter"/> values for the <see cref="NetworkSettings"/>
        /// </summary>
        /// <param name="receiveQueueCapacity"><seealso cref="BaselibNetworkParameter.receiveQueueCapacity"/></param>
        /// <param name="sendQueueCapacity"><seealso cref="BaselibNetworkParameter.sendQueueCapacity"/></param>
        /// <param name="maximumPayloadSize"><seealso cref="BaselibNetworkParameter.maximumPayloadSize"/></param>
        public static ref NetworkSettings WithBaselibNetworkInterfaceParameters(
            ref this NetworkSettings settings,
            int receiveQueueCapacity    = k_defaultRxQueueSize,
            int sendQueueCapacity       = k_defaultTxQueueSize,
            uint maximumPayloadSize     = k_defaultMaximumPayloadSize
        )
        {
            var parameter = new BaselibNetworkParameter
            {
                receiveQueueCapacity = receiveQueueCapacity,
                sendQueueCapacity = sendQueueCapacity,
                maximumPayloadSize = maximumPayloadSize,
            };

            settings.AddRawParameterStruct(ref parameter);

            return ref settings;
        }

        /// <summary>
        /// Gets the <see cref="BaselibNetworkParameter"/>
        /// </summary>
        /// <returns>Returns the <see cref="BaselibNetworkParameter"/> values for the <see cref="NetworkSettings"/></returns>
        public static BaselibNetworkParameter GetBaselibNetworkInterfaceParameters(ref this NetworkSettings settings)
        {
            if (!settings.TryGet<BaselibNetworkParameter>(out var baselibParameters))
            {
                baselibParameters.receiveQueueCapacity  = k_defaultRxQueueSize;
                baselibParameters.sendQueueCapacity     = k_defaultTxQueueSize;
                baselibParameters.maximumPayloadSize    = k_defaultMaximumPayloadSize;
            }

            return baselibParameters;
        }
    }

    /// <summary>
    /// Network Parameters used to set queue and payload sizes for <see cref="BaselibNetworkInterface"/>
    /// </summary>
    public struct BaselibNetworkParameter : INetworkParameter, IValidatableNetworkParameter
    {
        /// <summary>
        /// The maximum number of receiving packets that the <see cref="BaselibNetworkInterface"/> can process in a single update iteration.
        /// </summary>
        public int receiveQueueCapacity;
        /// <summary>
        /// The maximum number of sending packets that the <see cref="BaselibNetworkInterface"/> can process in a single update iteration.
        /// </summary>
        public int sendQueueCapacity;
        /// <summary>
        /// The maximum payload size.
        /// </summary>
        public uint maximumPayloadSize;

        public bool Validate()
        {
            var valid = true;

            if (receiveQueueCapacity <= 0)
            {
                valid = false;
                UnityEngine.Debug.LogError($"{nameof(receiveQueueCapacity)} value ({receiveQueueCapacity}) must be greater than 0");
            }
            if (sendQueueCapacity <= 0)
            {
                valid = false;
                UnityEngine.Debug.LogError($"{nameof(sendQueueCapacity)} value ({sendQueueCapacity}) must be greater than 0");
            }
            if (maximumPayloadSize <= 0)
            {
                valid = false;
                UnityEngine.Debug.LogError($"{nameof(maximumPayloadSize)} value ({maximumPayloadSize}) must be greater than 0");
            }

            return valid;
        }
    }

    /// <summary>
    /// Default NetworkInterface implementation based on Unity's internal Baselib UDP sockets this is ensure to work on all
    /// platforms except for Unity's WebGL.
    /// </summary>
    [BurstCompile]
    public struct BaselibNetworkInterface : INetworkInterface
    {
        /// <summary>
        /// Default Parameters for <see cref="BaselibNetworkInterface"/>
        /// </summary>
        public static BaselibNetworkParameter DefaultParameters = new BaselibNetworkParameter
        {
            receiveQueueCapacity = k_defaultRxQueueSize,
            sendQueueCapacity = k_defaultTxQueueSize,
            maximumPayloadSize = k_defaultMaximumPayloadSize
        };

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        private class SocketList
        {
            public struct SocketId
            {
                public NetworkSocket socket;
            }
            public List<SocketId> OpenSockets = new List<SocketId>();

            ~SocketList()
            {
                foreach (var socket in OpenSockets)
                {
                    Binding.Baselib_RegisteredNetwork_Socket_UDP_Close(socket.socket);
                }
            }
        }
        private static SocketList AllSockets = new SocketList();
#endif
        struct Payloads : IDisposable
        {
            public UnsafeAtomicFreeList m_Handles;
            public UnsafeBaselibNetworkArray m_PayloadArray;
            public UnsafeBaselibNetworkArray m_EndpointArray;
            private uint m_PayloadSize;

            public int InUse => m_Handles.InUse;
            public int Capacity => m_Handles.Capacity;

            public Payloads(int capacity, uint maxPayloadSize)
            {
                m_PayloadSize = maxPayloadSize;
                m_Handles = new UnsafeAtomicFreeList(capacity, Allocator.Persistent);
                m_PayloadArray = new UnsafeBaselibNetworkArray(capacity, (int)maxPayloadSize);
                m_EndpointArray = new UnsafeBaselibNetworkArray(capacity, (int)Binding.Baselib_RegisteredNetwork_Endpoint_MaxSize);
            }

            public bool IsCreated => m_Handles.IsCreated;
            public void Dispose()
            {
                m_Handles.Dispose();
                m_PayloadArray.Dispose();
                m_EndpointArray.Dispose();
            }

            public NetworkRequest GetRequestFromHandle(int handle)
            {
                return new NetworkRequest
                {
                    payload = m_PayloadArray.AtIndexAsSlice(handle, m_PayloadSize),
                    remoteEndpoint = new NetworkEndpoint
                    {
                        slice = m_EndpointArray.AtIndexAsSlice(handle, (uint)Binding.Baselib_RegisteredNetwork_Endpoint_MaxSize)
                    }
                };
            }

            public int AcquireHandle()
            {
                return m_Handles.Pop();
            }

            public void ReleaseHandle(int handle)
            {
                m_Handles.Push(handle);
            }
        }

        private BaselibNetworkParameter configuration;

        private const int k_defaultRxQueueSize = 64;
        private const int k_defaultTxQueueSize = 64;
        private const int k_defaultMaximumPayloadSize = 2000;

        unsafe struct BaselibData
        {
            public NetworkSocket m_Socket;
            public Payloads m_PayloadsTx;
        }

        [ReadOnly]
        private NativeArray<BaselibData> m_Baselib;

        [NativeDisableContainerSafetyRestriction]
        private Payloads m_PayloadsRx;
        [NativeDisableContainerSafetyRestriction]
        private Payloads m_PayloadsTx;

        private UnsafeBaselibNetworkArray m_LocalAndTempEndpoint;

        /// <summary>
        /// Returns the local endpoint the <see cref="BaselibNetworkInterface"/> is bound to.
        /// </summary>
        /// <value>NetworkInterfaceEndPoint</value>
        public unsafe NetworkInterfaceEndPoint LocalEndPoint
        {
            // error handling: handle the errors...
            get
            {
                var error = default(ErrorState);
                Binding.Baselib_NetworkAddress local;
                Binding.Baselib_RegisteredNetwork_Socket_UDP_GetNetworkAddress(m_Baselib[0].m_Socket, &local, &error);
                var ep = default(NetworkInterfaceEndPoint);
                if (error.code != ErrorCode.Success)
                    return ep;

                CreateInterfaceEndPoint(local, out ep);

                return ep;
            }
        }

        /// <summary>
        /// Gets if the interface has been created.
        /// </summary>
        public bool IsCreated => m_Baselib.IsCreated;

        /// <summary>
        /// Converts a generic <see cref="NetworkEndPoint"/> to its <see cref="NetworkInterfaceEndPoint"/> version for the <see cref="BaselibNetworkInterface"/>.
        /// </summary>
        /// <param name="address">The <see cref="NetworkEndPoint"/> endpoint to convert.</param>
        /// <returns>returns 0 on success and sets the converted endpoint value</returns>
        public unsafe int CreateInterfaceEndPoint(NetworkEndPoint address, out NetworkInterfaceEndPoint endpoint)
        {
            return CreateInterfaceEndPoint(address.rawNetworkAddress, out endpoint);
        }

        private unsafe int CreateInterfaceEndPoint(Binding.Baselib_NetworkAddress address, out NetworkInterfaceEndPoint endpoint)
        {
            var slice = m_LocalAndTempEndpoint.AtIndexAsSlice(0, (uint)Binding.Baselib_RegisteredNetwork_Endpoint_MaxSize);
            NetworkEndpoint local;
            var error = default(ErrorState);
            endpoint = default;

            local = Binding.Baselib_RegisteredNetwork_Endpoint_Create(
                &address,
                slice,
                &error);
            if (error.code != ErrorCode.Success)
                return (int)error.code;

            endpoint.dataLength = (int)local.slice.size;
            fixed(void* ptr = endpoint.data)
            {
                UnsafeUtility.MemCpy(ptr, (void*)local.slice.data, endpoint.dataLength);
            }
            return (int)Error.StatusCode.Success;
        }

        /// <summary>
        /// Converts a <see cref="NetworkInterfaceEndPoint"/> to its generic <see cref="NetworkEndPoint"/> version.
        /// </summary>
        /// <param name="endpoint">The <see cref="NetworkInterfaceEndPoint"/> endpoint to convert.</param>
        /// <returns>Returns the converted endpoint value.</returns>
        public unsafe NetworkEndPoint GetGenericEndPoint(NetworkInterfaceEndPoint endpoint)
        {
            var address = default(NetworkEndPoint);
            var error = default(ErrorState);
            var slice = m_LocalAndTempEndpoint.AtIndexAsSlice(0, (uint)Binding.Baselib_RegisteredNetwork_Endpoint_MaxSize);
            NetworkEndpoint local;
            local.slice = slice;
            local.slice.size = (uint)endpoint.dataLength;
            UnsafeUtility.MemCpy((void*)local.slice.data, endpoint.data, endpoint.dataLength);
            Binding.Baselib_RegisteredNetwork_Endpoint_GetNetworkAddress(local, &address.rawNetworkAddress, &error);
            if (error.code != ErrorCode.Success)
                return default;
            return address;
        }

        /// <summary>
        /// Initializes a instance of the <see cref="BaselibNetworkInterface"/> struct.
        /// </summary>
        /// <param name="param">An array of INetworkParameter. If there is no <see cref="BaselibNetworkParameter"/> present, the default values are used.</param>
        /// <returns>Returns 0 on succees.</returns>
        public unsafe int Initialize(NetworkSettings settings)
        {
            configuration = settings.GetBaselibNetworkInterfaceParameters();

            m_Baselib = new NativeArray<BaselibData>(1, Allocator.Persistent);
            var baselib = default(BaselibData);

            m_PayloadsTx = new Payloads(configuration.sendQueueCapacity, configuration.maximumPayloadSize);
            m_PayloadsRx = new Payloads(configuration.receiveQueueCapacity, configuration.maximumPayloadSize);
            m_LocalAndTempEndpoint = new UnsafeBaselibNetworkArray(2,  (int)Binding.Baselib_RegisteredNetwork_Endpoint_MaxSize);

            baselib.m_PayloadsTx = m_PayloadsTx;

            m_Baselib[0] = baselib;

            return 0;
        }

        /// <summary>
        /// Disposes this instance
        /// </summary>
        public void Dispose()
        {
            if (m_Baselib[0].m_Socket.handle != IntPtr.Zero)
            {
                #if ENABLE_UNITY_COLLECTIONS_CHECKS
                AllSockets.OpenSockets.Remove(new SocketList.SocketId
                    {socket = m_Baselib[0].m_Socket});
                #endif
                Binding.Baselib_RegisteredNetwork_Socket_UDP_Close(m_Baselib[0].m_Socket);
            }

            m_LocalAndTempEndpoint.Dispose();
            if (m_PayloadsTx.IsCreated)
                m_PayloadsTx.Dispose();
            if (m_PayloadsRx.IsCreated)
                m_PayloadsRx.Dispose();
            m_Baselib.Dispose();
        }

        #region ReceiveJob

        [BurstCompile]
        struct FlushSendJob : IJob
        {
            public Payloads Tx;
            [NativeDisableContainerSafetyRestriction]
            public NativeArray<BaselibData> Baselib;
            public unsafe void Execute()
            {
                // We process the results in batches because if we allocate a big number of results here,
                // it can cause a stack overflow
                const uint k_ResultsBufferSize = 64;
                const int  k_MaxIterations = 500;
                var results = stackalloc Binding.Baselib_RegisteredNetwork_CompletionResult[(int)k_ResultsBufferSize];

                var error = default(ErrorState);
                var pollCount = 0;
                var pendingSend = Tx.InUse > 0;
                var maxIterations = k_MaxIterations;

                while (pendingSend)
                {
                    // We ensure we never process more than the actual capacity to prevent unexpected deadlocks
                    while (pollCount++ < Tx.Capacity)
                    {
                        if (Binding.Baselib_RegisteredNetwork_Socket_UDP_ProcessSend(Baselib[0].m_Socket, &error) != Binding.Baselib_RegisteredNetwork_ProcessStatus.Pending)
                            break;
                    }

                    // At this point all the packets have been processed or the network can't send a packet right now
                    // so we yield execution to give the opportunity to other threads to process as the potential network
                    // pressure releases.
                    Binding.Baselib_Thread_YieldExecution();
                    // Next step we wait until processing sends complete.
                    // The timeout was arbitrarily chosen as it seems to be enough for sending 5000 packets in our tests.
                    Binding.Baselib_RegisteredNetwork_Socket_UDP_WaitForCompletedSend(Baselib[0].m_Socket, 20, &error);

                    var count = 0;
                    var resultBatchesCount = Tx.Capacity / k_ResultsBufferSize + 1;
                    while ((count = (int)Binding.Baselib_RegisteredNetwork_Socket_UDP_DequeueSend(Baselib[0].m_Socket, results, k_ResultsBufferSize, &error)) > 0)
                    {
                        if (error.code != ErrorCode.Success)
                        {
                            // copy recv flow? e.g. pass
                            return;
                        }
                        for (int i = 0; i < count; ++i)
                        {
                            // return results[i].status through userdata, mask? or allocate space at beginning?
                            // pass through a new NetworkPacketSender.?
                            Tx.ReleaseHandle((int)results[i].requestUserdata - 1);
                        }

                        if (resultBatchesCount-- < 0) // Deadlock guard
                            break;
                    }

                    // We set the pollCount to zero that way we try and process any previous packets that may have failed because
                    // internal buffers were full via a EAGAIN error which we don't actually know happened but assume it may have happen
                    pollCount = 0;

                    // InUse is not thread safe, needs to be called in a single threaded flush job
                    // We ensure we never process more than the actual capacity to prevent unexpected deadlocks
                    pendingSend = Tx.InUse > 0 && maxIterations-- > 0;
                }

#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (Tx.InUse > 0)
                {
                    UnityEngine.Debug.LogWarning(string.Format("There are {0} pending send packets after the baselib process send", Tx.InUse));
                }
#endif
            }
        }
        [BurstCompile]
        struct ReceiveJob : IJob
        {
            public NetworkPacketReceiver Receiver;
            public Payloads Rx;
            [NativeDisableContainerSafetyRestriction]
            public NativeArray<BaselibData> Baselib;

            public unsafe void Execute()
            {
                var count = 0;
                var outstanding = Rx.InUse;
                var error = default(ErrorState);
                var requests = stackalloc Binding.Baselib_RegisteredNetwork_Request[Rx.Capacity];

                if (outstanding > 0)
                {
                    var pollCount = 0;
                    var status = default(Binding.Baselib_RegisteredNetwork_ProcessStatus);
                    while ((status = Binding.Baselib_RegisteredNetwork_Socket_UDP_ProcessRecv(Baselib[0].m_Socket, &error)) == Binding.Baselib_RegisteredNetwork_ProcessStatus.Pending
                           && pollCount++ < Rx.Capacity) {}

#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    if (status == Binding.Baselib_RegisteredNetwork_ProcessStatus.Pending)
                    {
                        UnityEngine.Debug.LogWarning("There are pending receive packets after the baselib process receive");
                    }
#endif

                    var results = stackalloc Binding.Baselib_RegisteredNetwork_CompletionResult[outstanding];

                    // Pop Completed Requests off the CompletionQ
                    count = (int)Binding.Baselib_RegisteredNetwork_Socket_UDP_DequeueRecv(Baselib[0].m_Socket, results, (uint)outstanding, &error);
                    if (error.code != ErrorCode.Success)
                    {
                        Receiver.ReceiveErrorCode = (int)error.code;
                        return;
                    }

                    // Copy and run Append on each Packet.

                    var address = default(NetworkInterfaceEndPoint);

                    var indices = stackalloc int[count];
                    for (int i = 0; i < count; i++)
                    {
                        var index = (int)results[i].requestUserdata - 1;
                        indices[i] = index;

                        if (results[i].status == Binding.Baselib_RegisteredNetwork_CompletionStatus.Failed)
                            continue;

                        var receivedBytes = (int)results[i].bytesTransferred;
                        if (receivedBytes <= 0)
                            continue;

                        var packet = Rx.GetRequestFromHandle(index);

                        var remote = packet.remoteEndpoint.slice;
                        address.dataLength = (int)remote.size;
                        UnsafeUtility.MemCpy(address.data, (void*)remote.data, (int)remote.size);

                        Receiver.AppendPacket(packet.payload.data, ref address, receivedBytes);
                    }

                    // Reuse the requests after they have been processed.
                    for (int i = 0; i < count; i++)
                    {
                        requests[i] = Rx.GetRequestFromHandle(indices[i]);
                        requests[i].requestUserdata = (IntPtr)indices[i] + 1;
                    }
                }

                while (Rx.InUse < Rx.Capacity)
                {
                    int handle = Rx.AcquireHandle();
                    requests[count] = Rx.GetRequestFromHandle(handle);
                    requests[count].requestUserdata = (IntPtr)handle + 1;
                    ++count;
                }
                if (count > 0)
                {
                    Binding.Baselib_RegisteredNetwork_Socket_UDP_ScheduleRecv(Baselib[0].m_Socket, requests, (uint)count, &error);
                    if (error.code != ErrorCode.Success)
                        Receiver.ReceiveErrorCode = (int)error.code;
                }
            }
        }
        #endregion

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
                Baselib = m_Baselib,
                Rx = m_PayloadsRx,
                Receiver = receiver
            };
            return job.Schedule(dep);
        }

        /// <summary>
        /// Schedule a SendJob. This is used to flush send queues to your supported medium
        /// </summary>
        /// <param name="sendQueue">The send queue which can be used to emulate parallel send.</param>
        /// <param name="dep">A <see cref="JobHandle"/> to any dependency we might have.</param>
        /// <returns>A <see cref="JobHandle"/> to our newly created ScheduleSend Job.</returns>
        public JobHandle ScheduleSend(NativeQueue<QueuedSendMessage> sendQueue, JobHandle dep)
        {
            var job = new FlushSendJob
            {
                Baselib = m_Baselib,
                Tx = m_PayloadsTx
            };
            return job.Schedule(dep);
        }

        /// <summary>
        /// Binds the medium to a specific endpoint.
        /// </summary>
        /// <param name="endpoint">
        /// A valid <see cref="NetworkInterfaceEndPoint"/>.
        /// </param>
        /// <returns>Returns 0 on success.</returns>
        public unsafe int Bind(NetworkInterfaceEndPoint endpoint)
        {
            var baselib = m_Baselib[0];
            if (m_Baselib[0].m_Socket.handle != IntPtr.Zero)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                AllSockets.OpenSockets.Remove(new SocketList.SocketId
                    {socket = m_Baselib[0].m_Socket});
#endif
                Binding.Baselib_RegisteredNetwork_Socket_UDP_Close(m_Baselib[0].m_Socket);
                baselib.m_Socket.handle = IntPtr.Zero;

                // Recreate the payloads to make sure we do not loose any items from the queue
                m_PayloadsRx.Dispose();
                m_PayloadsRx = new Payloads(configuration.receiveQueueCapacity, configuration.maximumPayloadSize);
            }

            var slice = m_LocalAndTempEndpoint.AtIndexAsSlice(0, (uint)Binding.Baselib_RegisteredNetwork_Endpoint_MaxSize);
            UnsafeUtility.MemCpy((void*)slice.data, endpoint.data, endpoint.dataLength);

            var error = default(ErrorState);

            NetworkEndpoint local;
            local.slice = slice;

            Binding.Baselib_NetworkAddress localAddress;
            Binding.Baselib_RegisteredNetwork_Endpoint_GetNetworkAddress(local, &localAddress, &error);

            baselib.m_Socket = Binding.Baselib_RegisteredNetwork_Socket_UDP_Create(
                &localAddress,
                Binding.Baselib_NetworkAddress_AddressReuse.Allow,
                checked((uint)configuration.sendQueueCapacity),
                checked((uint)configuration.receiveQueueCapacity),
                &error);
            if (error.code != ErrorCode.Success)
            {
                m_Baselib[0] = baselib;
                return (int)error.code == -1 ? -1 : -(int)error.code;
            }

            // Schedule receive right away so we do not loose packets received before the first call to update
            int count = 0;
            var requests = stackalloc Binding.Baselib_RegisteredNetwork_Request[m_PayloadsRx.Capacity];
            while (m_PayloadsRx.InUse < m_PayloadsRx.Capacity)
            {
                int handle = m_PayloadsRx.AcquireHandle();
                requests[count] = m_PayloadsRx.GetRequestFromHandle(handle);
                requests[count].requestUserdata = (IntPtr)handle + 1;
                ++count;
            }
            if (count > 0)
            {
                Binding.Baselib_RegisteredNetwork_Socket_UDP_ScheduleRecv(
                    baselib.m_Socket,
                    requests,
                    (uint)count,
                    &error);
                // how should this be handled? what are the cases?
                if (error.code != ErrorCode.Success)
                    return (int)error.code == -1 ? -1 : -(int)error.code;
            }
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AllSockets.OpenSockets.Add(new SocketList.SocketId
                {socket = baselib.m_Socket});
#endif
            m_Baselib[0] = baselib;
            return 0;
        }

        /// <summary>
        /// Listens on the socket, currently this Interface doesn't support listening as its UDP based.
        /// </summary>
        /// <returns>Returns 0</returns>
        public int Listen()
        {
            return 0;
        }

        static TransportFunctionPointer<NetworkSendInterface.BeginSendMessageDelegate> BeginSendMessageFunctionPointer = new TransportFunctionPointer<NetworkSendInterface.BeginSendMessageDelegate>(BeginSendMessage);
        static TransportFunctionPointer<NetworkSendInterface.EndSendMessageDelegate> EndSendMessageFunctionPointer = new TransportFunctionPointer<NetworkSendInterface.EndSendMessageDelegate>(EndSendMessage);
        static TransportFunctionPointer<NetworkSendInterface.AbortSendMessageDelegate> AbortSendMessageFunctionPointer = new TransportFunctionPointer<NetworkSendInterface.AbortSendMessageDelegate>(AbortSendMessage);

        /// <summary>
        /// Creates the send interface
        /// </summary>
        /// <returns>The network send interface</returns>
        public unsafe NetworkSendInterface CreateSendInterface()
        {
            return new NetworkSendInterface
            {
                BeginSendMessage = BeginSendMessageFunctionPointer,
                EndSendMessage = EndSendMessageFunctionPointer,
                AbortSendMessage = AbortSendMessageFunctionPointer,
                UserData = (IntPtr)m_Baselib.GetUnsafePtr()
            };
        }

        [BurstCompile(DisableDirectCall = true)]
        [AOT.MonoPInvokeCallback(typeof(NetworkSendInterface.BeginSendMessageDelegate))]
        private static unsafe int BeginSendMessage(out NetworkInterfaceSendHandle handle, IntPtr userData, int requiredPayloadSize)
        {
            var baselib = (BaselibData*)userData;
            handle = default;
            int index = baselib->m_PayloadsTx.AcquireHandle();
            if (index < 0)
                return (int)Error.StatusCode.NetworkSendQueueFull;

            var message = baselib->m_PayloadsTx.GetRequestFromHandle(index);
            if ((int)message.payload.size < requiredPayloadSize)
            {
                baselib->m_PayloadsTx.ReleaseHandle(index);
                return (int)Error.StatusCode.NetworkPacketOverflow;
            }

            handle.id = index;
            handle.size = 0;
            handle.data = (IntPtr)message.payload.data;
            handle.capacity = (int)message.payload.size;
            return (int)Error.StatusCode.Success;
        }

        [BurstCompile(DisableDirectCall = true)]
        [AOT.MonoPInvokeCallback(typeof(NetworkSendInterface.EndSendMessageDelegate))]
        private static unsafe int EndSendMessage(ref NetworkInterfaceSendHandle handle, ref NetworkInterfaceEndPoint address, IntPtr userData, ref NetworkSendQueueHandle sendQueueHandle)
        {
            var baselib = (BaselibData*)userData;
            int index = handle.id;
            var message = baselib->m_PayloadsTx.GetRequestFromHandle(index);
            message.requestUserdata = (IntPtr)(index + 1);
            message.payload.size = (uint)handle.size;

            var addr = address;
            UnsafeUtility.MemCpy((void*)message.remoteEndpoint.slice.data, addr.data, address.dataLength);

            NetworkRequest* messagePtr = &message;

            var error = default(ErrorState);
            var count = (int)Binding.Baselib_RegisteredNetwork_Socket_UDP_ScheduleSend(
                baselib->m_Socket,
                messagePtr,
                1u,
                &error);
            if (error.code != ErrorCode.Success)
            {
                baselib->m_PayloadsTx.ReleaseHandle(index);
                return (int)error.code == -1 ? -1 : -(int)error.code;
            }
            return handle.size;
        }

        [BurstCompile(DisableDirectCall = true)]
        [AOT.MonoPInvokeCallback(typeof(NetworkSendInterface.AbortSendMessageDelegate))]
        private static unsafe void AbortSendMessage(ref NetworkInterfaceSendHandle handle, IntPtr userData)
        {
            var baselib = (BaselibData*)userData;
            var id = handle.id;
            baselib->m_PayloadsTx.ReleaseHandle(id);
        }

        bool ValidateParameters(BaselibNetworkParameter param)
        {
            if (param.receiveQueueCapacity <= 0)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                UnityEngine.Debug.LogWarning("Value for receiveQueueCapacity must be larger then zero.");
#endif
                return false;
            }
            if (param.sendQueueCapacity <= 0)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                UnityEngine.Debug.LogWarning("Value for sendQueueCapacity must be larger then zero.");
#endif
                return false;
            }
            return true;
        }

        bool TryExtractParameters(out BaselibNetworkParameter config, params INetworkParameter[] param)
        {
            for (int i = 0; i < param.Length; ++i)
            {
                if (param[i] is BaselibNetworkParameter && ValidateParameters((BaselibNetworkParameter)param[i]))
                {
                    config = (BaselibNetworkParameter)param[i];
                    return true;
                }
            }
            config = default;
            return false;
        }
    }
}
#endif // !UNITY_WEBGL
