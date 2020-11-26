using System;
using Unity.Networking.Transport.Protocols;
using Unity.Collections;
using Unity.Jobs;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;

namespace Unity.Networking.Transport
{
    /// <summary>
    /// The NetworkPacketReceiver is an interface for handling received packets, needed by the <see cref="INetworkInterface"/>
    /// </summary>
    public struct NetworkPacketReceiver
    {
        public int ReceiveCount { get {return m_Driver.ReceiveCount;} set{m_Driver.ReceiveCount = value;} }
        /// <summary>
        /// AppendPacket is where we parse the data from the network into easy to handle events.
        /// </summary>
        /// <param name="address">The address of the endpoint we received data from.</param>
        /// <param name="header">The header data indicating what type of packet it is. <see cref="UdpCHeader"/> for more information.</param>
        /// <param name="dataLen">The size of the payload, if any.</param>
        /// <returns></returns>
        public int AppendPacket(NetworkInterfaceEndPoint address, UdpCHeader header, int dataLen)
        {
            return m_Driver.AppendPacket(address, header, dataLen);
        }

        /// <summary>
        /// Get the datastream associated with this Receiver.
        /// </summary>
        /// <returns>Returns a NativeList of bytes</returns>
        public NativeList<byte> GetDataStream()
        {
            return m_Driver.GetDataStream();
        }
        public int GetDataStreamSize()
        {
            return m_Driver.GetDataStreamSize();
        }
        /// <summary>
        /// Check if the DataStreamWriter uses dynamic allocations to automatically resize the buffers or not.
        /// </summary>
        /// <returns>True if its dynamically resizing the DataStreamWriter</returns>
        public bool DynamicDataStreamSize()
        {
            return m_Driver.DynamicDataStreamSize();
        }

        public int ReceiveErrorCode { set{m_Driver.ReceiveErrorCode = value;} }
        internal NetworkDriver m_Driver;
    }

    [Flags]
    public enum SendHandleFlags
    {
        AllocatedByDriver = 1 << 0
    }


    public struct NetworkInterfaceSendHandle
    {
        public IntPtr data;
        public int capacity;
        public int size;
        public int id;
        public SendHandleFlags flags;
    }
    public struct NetworkSendQueueHandle
    {
        private IntPtr handle;
        internal static unsafe NetworkSendQueueHandle ToTempHandle(NativeQueue<QueuedSendMessage>.ParallelWriter sendQueue)
        {
            void* ptr = UnsafeUtility.Malloc(UnsafeUtility.SizeOf<NativeQueue<QueuedSendMessage>.ParallelWriter>(), UnsafeUtility.AlignOf<NativeQueue<QueuedSendMessage>.ParallelWriter>(), Allocator.Temp);
            UnsafeUtility.WriteArrayElement(ptr, 0, sendQueue);
            return new NetworkSendQueueHandle { handle = (IntPtr)ptr };
        }
        public unsafe NativeQueue<QueuedSendMessage>.ParallelWriter FromHandle()
        {
            void* ptr = (void*)handle;
            return UnsafeUtility.ReadArrayElement<NativeQueue<QueuedSendMessage>.ParallelWriter>(ptr, 0);
        }
    }
    public struct NetworkSendInterface
    {
        public delegate int BeginSendMessageDelegate(out NetworkInterfaceSendHandle handle, IntPtr userData, int requiredPayloadSize);
        public delegate int EndSendMessageDelegate(ref NetworkInterfaceSendHandle handle, ref NetworkInterfaceEndPoint address, IntPtr userData, ref NetworkSendQueueHandle sendQueue);
        public delegate void AbortSendMessageDelegate(ref NetworkInterfaceSendHandle handle, IntPtr userData);
        public TransportFunctionPointer<BeginSendMessageDelegate> BeginSendMessage;
        public TransportFunctionPointer<EndSendMessageDelegate> EndSendMessage;
        public TransportFunctionPointer<AbortSendMessageDelegate> AbortSendMessage;
        [NativeDisableUnsafePtrRestriction] public IntPtr UserData;
    }
    public interface INetworkInterface : IDisposable
    {
        NetworkInterfaceEndPoint LocalEndPoint { get; }

        int Initialize(params INetworkParameter[] param);

        /// <summary>
        /// Schedule a ReceiveJob. This is used to read data from your supported medium and pass it to the AppendData function
        /// supplied by <see cref="NetworkDriver"/>
        /// </summary>
        /// <param name="receiver">A <see cref="NetworkDriver"/> used to parse the data received.</param>
        /// <param name="dep">A <see cref="JobHandle"/> to any dependency we might have.</param>
        /// <returns>A <see cref="JobHandle"/> to our newly created ScheduleReceive Job.</returns>
        JobHandle ScheduleReceive(NetworkPacketReceiver receiver, JobHandle dep);

        /// <summary>
        /// Schedule a SendJob. This is used to flush send queues to your supported medium
        /// </summary>
        /// <param name="sendQueue">The send queue which can be used to emulate parallel send.</param>
        /// <param name="dep">A <see cref="JobHandle"/> to any dependency we might have.</param>
        /// <returns>A <see cref="JobHandle"/> to our newly created ScheduleSend Job.</returns>
        JobHandle ScheduleSend(NativeQueue<QueuedSendMessage> sendQueue, JobHandle dep);

        /// <summary>
        /// Binds the medium to a specific endpoint.
        /// </summary>
        /// <param name="endpoint">
        /// A valid <see cref="NetworkInterfaceEndPoint"/>.
        /// </param>
        /// <returns>0 on Success</returns>
        int Bind(NetworkInterfaceEndPoint endpoint);

        NetworkSendInterface CreateSendInterface();

        int CreateInterfaceEndPoint(NetworkEndPoint address, out NetworkInterfaceEndPoint endpoint);
        NetworkEndPoint GetGenericEndPoint(NetworkInterfaceEndPoint endpoint);
    }
}
