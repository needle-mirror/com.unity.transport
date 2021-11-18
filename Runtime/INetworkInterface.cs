using System;
using System.Runtime.CompilerServices;
using Unity.Networking.Transport.Protocols;
using Unity.Collections;
using Unity.Jobs;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;
using System.Runtime.InteropServices;
using Unity.Networking.Transport.Utilities;

namespace Unity.Networking.Transport
{
    /// <summary>
    /// The NetworkPacketReceiver is an interface for handling received packets, needed by the <see cref="INetworkInterface"/>
    /// It either can be used in two main scenarios:
    /// 1. Your API requires a pointer to memory that you own. Then you should use <see cref="AllocateMemory"/>, write to the memory and then <see cref="AppendPacket"/> with <see cref="AppendPacketMode.NoCopyNeeded"/>. You don't need to deallocate the memory
    /// 2. Your API gives you a pointer that you don't own. In this case you should use <see cref="AppendPacket"/> with <see cref="AppendPacketMode.None"/> (default)
    /// </summary>
    public struct NetworkPacketReceiver
    {
        /// <summary>
        /// Calls NetworkDriver's <see cref="NetworkDriver.AllocateMemory"/>
        /// </summary>
        /// <param name="dataLen">Size of memory to allocate in bytes. Must be > 0</param>
        /// <returns>Pointer to allocated memory or IntPtr.Zero if there is no space left (this function doesn't set <see cref="ReceiveErrorCode"/>! caller should decide if this is Out of memory or something else)</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public IntPtr AllocateMemory(ref int dataLen)
        {
            return m_Driver.AllocateMemory(ref dataLen);
        }

        /// <summary>
        /// Permits choosing between two ways of appending packets: via a copy or not.
        /// </summary>
        [Flags]
        public enum AppendPacketMode
        {
            /// <summary>
            /// Append Packet via a copy, which is the default mode
            /// </summary>
            None = 0,
            /// <summary>
            /// No Copy required when appending a packet
            /// </summary>
            NoCopyNeeded = 1
        }

        /// <summary>
        /// When data is received this function should be called to pass it inside <see cref="NetworkDriver"/>
        /// </summary>
        /// <param name="data">Pointer to the data. If it is pointer to data that was received with <see cref="AllocateMemory"/> make sure mode is <see cref="AppendPacketMode.NoCopyNeeded"/>></param>
        /// <param name="address">Address where data was received from</param>
        /// <param name="dataLen">Length of <see cref="data"/> in bytes</param>
        /// <param name="mode">Extra flags, like <see cref="AppendPacketMode.NoCopyNeeded"/> that means - no copy is needed, data is already in <see cref="NetworkDriver"/>'s data stream</param>
        /// <returns>True if no errors</returns>
        public bool AppendPacket(IntPtr data, ref NetworkInterfaceEndPoint address, int dataLen, AppendPacketMode mode = AppendPacketMode.None)
        {
            if ((mode & AppendPacketMode.NoCopyNeeded) != 0)
            {
                m_Driver.AppendPacket(data, ref address, dataLen);
                return true;
            }

            unsafe // copy external data -> m_Driver's data stream
            {
                var allocatedLen = dataLen;
                var ptr = m_Driver.AllocateMemory(ref allocatedLen);

                if (ptr == IntPtr.Zero || allocatedLen < dataLen)
                {
                    OutOfMemoryError();
                    return false;
                }

                UnsafeUtility.MemCpy((byte*)ptr.ToPointer(), (byte*)data.ToPointer(), dataLen);
                m_Driver.AppendPacket(ptr, ref address, dataLen);
            }

            return true;
        }

        /// <summary>
        /// Check if an address is currently associated with a valid connection.
        /// This is mostly useful to keep interface internal lists of connections in sync with the correct state.
        /// </summary>
        public bool IsAddressUsed(NetworkInterfaceEndPoint address)
        {
            return m_Driver.IsAddressUsed(address);
        }

        /// <summary>
        /// Gets the value of the last update time.
        /// </summary>
        public long LastUpdateTime => m_Driver.LastUpdateTime;

        void OutOfMemoryError()
        {
            ReceiveErrorCode = 10040; //(int)ErrorCode.OutOfMemory;
        }

        /// <summary>
        /// Sets the value of the receive error code
        /// </summary>
        public int ReceiveErrorCode
        {
            set => m_Driver.ReceiveErrorCode = value;
        }

        internal NetworkDriver m_Driver;
    }

    /// <summary>
    /// The send handle flags enum
    /// </summary>
    [Flags]
    public enum SendHandleFlags
    {
        /// <summary>
        /// This SendHandle has been allocated by <see cref="NetworkDriver"/>
        /// </summary>
        AllocatedByDriver = 1 << 0
    }


    /// <summary>
    /// A handle to data that's going to be sent on an interface.
    /// </summary>
    public struct NetworkInterfaceSendHandle
    {
        /// <summary>Pointer to the data buffer.</summary>
        public IntPtr data;
        /// <summary>Maximum capacity of the data buffer.</summary>
        public int capacity;
        /// <summary>Actual size of the data in the buffer.</summary>
        public int size;
        /// <summary>Internal ID for this handle.</summary>
        public int id;
        /// <summary>Internal flags used by the driver (tracks who allocated the memory).</summary>
        public SendHandleFlags flags;
    }
    /// <summary>
    /// The network send queue handle
    /// </summary>
    public struct NetworkSendQueueHandle
    {
        private IntPtr handle;

        internal static unsafe NetworkSendQueueHandle ToTempHandle(NativeQueue<QueuedSendMessage>.ParallelWriter sendQueue)
        {
            void* ptr = UnsafeUtility.Malloc(UnsafeUtility.SizeOf<NativeQueue<QueuedSendMessage>.ParallelWriter>(), UnsafeUtility.AlignOf<NativeQueue<QueuedSendMessage>.ParallelWriter>(), Allocator.Temp);
            UnsafeUtility.WriteArrayElement(ptr, 0, sendQueue);
            return new NetworkSendQueueHandle { handle = (IntPtr)ptr };
        }

        /// <summary>
        /// Create <see cref="NativeQueue{QueuedSendMessage}.ParallelWriter"/> from the internal handle
        /// </summary>
        /// <returns><see cref="NativeQueue{QueuedSendMessage}.ParallelWriter"/></returns>
        public unsafe NativeQueue<QueuedSendMessage>.ParallelWriter FromHandle()
        {
            void* ptr = (void*)handle;
            return UnsafeUtility.ReadArrayElement<NativeQueue<QueuedSendMessage>.ParallelWriter>(ptr, 0);
        }
    }
    /// <summary>
    /// The network send interface used to pass around function pointers to the actual <see cref="INetworkInterface"/>
    /// </summary>
    public struct NetworkSendInterface
    {
        /// <summary>
        /// Invoked from the lower level library at the beginning of the message sending routine.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int BeginSendMessageDelegate(out NetworkInterfaceSendHandle handle, IntPtr userData, int requiredPayloadSize);

        /// <summary>
        /// Invoked from the lower level library at the end of the message sending routine.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int EndSendMessageDelegate(ref NetworkInterfaceSendHandle handle, ref NetworkInterfaceEndPoint address, IntPtr userData, ref NetworkSendQueueHandle sendQueue);

        /// <summary>
        /// Will be invoked from the lower level library if sending a message was aborted.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void AbortSendMessageDelegate(ref NetworkInterfaceSendHandle handle, IntPtr userData);

        /// <summary>
        /// The begin send message function pointer
        /// </summary>
        public TransportFunctionPointer<BeginSendMessageDelegate> BeginSendMessage;
        /// <summary>
        /// The end send message function pointer
        /// </summary>
        public TransportFunctionPointer<EndSendMessageDelegate> EndSendMessage;
        /// <summary>
        /// The abort send message function pointer
        /// </summary>
        public TransportFunctionPointer<AbortSendMessageDelegate> AbortSendMessage;
        /// <summary>
        /// The user data
        /// </summary>
        [NativeDisableUnsafePtrRestriction] public IntPtr UserData;
    }
    /// <summary>
    /// Interface for implementing a low-level networking interface see <see cref="BaselibNetworkInterface"/> as an example
    /// </summary>
    /// <seealso cref="IDisposable"/>
    public interface INetworkInterface : IDisposable
    {
        /// <summary>
        /// Gets the value of the local end point
        /// </summary>
        NetworkInterfaceEndPoint LocalEndPoint { get; }

        /// <summary>
        /// Initializes the interfacing passing in optional <see cref="INetworkParameter"/>
        /// </summary>
        /// <param name="param">The param</param>
        /// <returns>The int</returns>
        int Initialize(NetworkSettings settings);

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

        /// <summary>
        /// Start listening for incoming connections. This is normally a no-op for real UDP sockets.
        /// </summary>
        /// <returns>0 on Success</returns>
        int Listen();

        /// <summary>
        /// Creates the send interface
        /// </summary>
        /// <returns>The network send interface</returns>
        NetworkSendInterface CreateSendInterface();

        /// <summary>
        /// Creates the interface end point using the specified address
        /// </summary>
        /// <param name="address">The address</param>
        /// <param name="endpoint">The endpoint</param>
        /// <returns>The int</returns>
        int CreateInterfaceEndPoint(NetworkEndPoint address, out NetworkInterfaceEndPoint endpoint);

        /// <summary>
        /// Gets the generic end point using the specified endpoint
        /// </summary>
        /// <param name="endpoint">The endpoint</param>
        /// <returns>The network end point</returns>
        NetworkEndPoint GetGenericEndPoint(NetworkInterfaceEndPoint endpoint);
    }
}
