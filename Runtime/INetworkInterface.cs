using System;
using Unity.Jobs;
using Unity.Networking.Transport.Protocols;

namespace Unity.Networking.Transport
{
    /// <summary>
    /// The INetworkPacketReceiver is an interface for handling received packets, needed by the <see cref="INetworkInterface"/>
    /// </summary>
    public interface INetworkPacketReceiver
    {
        int ReceiveCount { get; set; }
        /// <summary>
        /// AppendPacket is where we parse the data from the network into easy to handle events.
        /// </summary>
        /// <param name="address">The address of the endpoint we received data from.</param>
        /// <param name="header">The header data indicating what type of packet it is. <see cref="UdpCHeader"/> for more information.</param>
        /// <param name="dataLen">The size of the payload, if any.</param>
        /// <returns></returns>
        int AppendPacket(NetworkEndPoint address, UdpCHeader header, int dataLen);

        /// <summary>
        /// Get the datastream associated with this Receiver.
        /// </summary>
        /// <returns>Returns a <see cref="DataStreamWriter"/></returns>
        DataStreamWriter GetDataStream();
        /// <summary>
        /// Check if the the DataStreamWriter uses dynamic allocations to automatically resize the buffers or not.
        /// </summary>
        /// <returns>True if its dynamically resizing the DataStreamWriter</returns>
        bool DynamicDataStreamSize();

        int ReceiveErrorCode { set; }
    }

    public interface INetworkInterface : IDisposable
    {
        /// <summary>
        /// The Interface Family is used to indicate what type of medium we are trying to use. See <see cref="NetworkFamily"/>
        /// </summary>
        NetworkFamily Family { get; }

        NetworkEndPoint LocalEndPoint { get; }
        NetworkEndPoint RemoteEndPoint { get; }

        void Initialize();

        /// <summary>
        /// Schedule a ReceiveJob. This is used to read data from your supported medium and pass it to the AppendData function
        /// supplied by the <see cref="INetworkPacketReceiver"/>
        /// </summary>
        /// <param name="receiver">A <see cref="INetworkPacketReceiver"/> used to parse the data received.</param>
        /// <param name="dep">A <see cref="JobHandle"/> to any dependency we might have.</param>
        /// <typeparam name="T">Need to be of type <see cref="INetworkPacketReceiver"/></typeparam>
        /// <returns>A <see cref="JobHandle"/> to our newly created ScheduleReceive Job.</returns>
        JobHandle ScheduleReceive<T>(T receiver, JobHandle dep) where T : struct, INetworkPacketReceiver;

        /// <summary>
        /// Binds the medium to a specific endpoint.
        /// </summary>
        /// <param name="endpoint">
        /// A valid <see cref="NetworkEndPoint"/>, can be implicitly cast using an <see cref="System.Net.IPEndPoint"/>.
        /// </param>
        /// <returns>0 on Success</returns>
        int Bind(NetworkEndPoint endpoint);

        /// <summary>
        /// Sends a message using the underlying medium.
        /// </summary>
        /// <param name="iov">An array of <see cref="network_iovec"/>.</param>
        /// <param name="iov_len">Lenght of the iov array passed in.</param>
        /// <param name="address">The address of the remote host we want to send data to.</param>
        /// <returns></returns>
        unsafe int SendMessage(network_iovec* iov, int iov_len, ref NetworkEndPoint address);
    }
}