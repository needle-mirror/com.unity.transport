using System;
using Unity.Collections;
using Unity.Jobs;
using Unity.Collections.LowLevel.Unsafe;

namespace Unity.Networking.Transport
{
    [Obsolete("Use ReceiveJobArguments.ReceiveQueue instead", true)]
    public struct NetworkPacketReceiver
    {
        public IntPtr AllocateMemory(ref int dataLen)
            => throw new NotImplementedException();

        [Flags]
        public enum AppendPacketMode
        {
            None = 0,
            NoCopyNeeded = 1
        }

        public bool AppendPacket(IntPtr data, ref NetworkEndpoint address, int dataLen, AppendPacketMode mode = AppendPacketMode.None)
            => throw new NotImplementedException();

        public bool IsAddressUsed(NetworkEndpoint address)
            => throw new NotImplementedException();

        public long LastUpdateTime
            => throw new NotImplementedException();

        public int ReceiveErrorCode
            => throw new NotImplementedException();
    }

    [Flags]
    internal enum SendHandleFlags
    {
        AllocatedByDriver = 1 << 0
    }


    internal struct NetworkInterfaceSendHandle
    {
        public IntPtr data;
        public int capacity;
        public int size;
        public int id;
        public SendHandleFlags flags;
    }

    public interface INetworkInterface : IDisposable
    {
        NetworkEndpoint LocalEndpoint { get; }

        int Initialize(ref NetworkSettings settings, ref int packetPadding);

        /// <summary>
        /// Schedule a ReceiveJob. This is used to read data from your supported medium and pass it to the AppendData function
        /// supplied by <see cref="NetworkDriver"/>
        /// </summary>
        /// <param name="arguments">A set of <see cref="ReceiveJobArguments"/> that can be used in the receive jobs.</param>
        /// <param name="dep">A <see cref="JobHandle"/> to any dependency we might have.</param>
        /// <returns>A <see cref="JobHandle"/> to our newly created ScheduleReceive Job.</returns>
        JobHandle ScheduleReceive(ref ReceiveJobArguments arguments, JobHandle dep);

        /// <summary>
        /// Schedule a SendJob. This is used to flush send queues to your supported medium
        /// </summary>
        /// <param name="arguments">A set of <see cref="SendJobArguments"/> that can be used in the send jobs.</param>
        /// <param name="dep">A <see cref="JobHandle"/> to any dependency we might have.</param>
        /// <returns>A <see cref="JobHandle"/> to our newly created ScheduleSend Job.</returns>
        JobHandle ScheduleSend(ref SendJobArguments arguments, JobHandle dep);

        /// <summary>
        /// Binds the medium to a specific endpoint.
        /// </summary>
        /// <param name="endpoint">
        /// A valid <see cref="NetworkEndpoint"/>.
        /// </param>
        /// <returns>0 on Success</returns>
        int Bind(NetworkEndpoint endpoint);

        /// <summary>
        /// Start listening for incoming connections. This is normally a no-op for real UDP sockets.
        /// </summary>
        /// <returns>0 on Success</returns>
        int Listen();
    }
}
