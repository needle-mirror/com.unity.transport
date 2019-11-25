using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Jobs;
using Unity.Networking.Transport.LowLevel.Unsafe;
using Unity.Networking.Transport.Protocols;

namespace Unity.Networking.Transport
{
#if NET_DOTS
    public class NetworkInterfaceException : Exception
    {
        public NetworkInterfaceException(int err)
            : base("Socket error: " + err)
        {
        }
    }
#else
    public class NetworkInterfaceException : System.Net.Sockets.SocketException
    {
        public NetworkInterfaceException(int err)
            : base(err)
        {
        }
    }
#endif

    public struct IPv4UDPSocket : INetworkInterface
    {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
        private class SocketList
        {
            public HashSet<long> OpenSockets = new HashSet<long>();

            ~SocketList()
            {
                foreach (var socket in OpenSockets)
                {
                    long sockHand = socket;
                    int errorcode = 0;
                    NativeBindings.network_close(ref sockHand, ref errorcode);
                }
            }
        }
        private static SocketList AllSockets = new SocketList();
#endif

        private long m_SocketHandle;
        private NetworkEndPoint m_RemoteEndPoint;

        public NetworkFamily Family => NetworkFamily.UdpIpv4;

        public unsafe NetworkEndPoint LocalEndPoint
        {
            get
            {
                var localEndPoint = new NetworkEndPoint {length = sizeof(NetworkEndPoint)};
                int errorcode = 0;
                var result = NativeBindings.network_get_socket_address(m_SocketHandle, ref localEndPoint, ref errorcode);
                if (result != 0)
                {
                    throw new NetworkInterfaceException(errorcode);
                }
                return localEndPoint;
            }
        }

        public NetworkEndPoint RemoteEndPoint {
            get { return m_RemoteEndPoint; }
        }

        public void Initialize()
        {
            NativeBindings.network_initialize();
            int ret = CreateAndBindSocket(out m_SocketHandle, NetworkEndPoint.AnyIpv4);
            if (ret != 0)
                throw new NetworkInterfaceException(ret);
        }

        public void Dispose()
        {
            Close();
            NativeBindings.network_terminate();
        }

        [BurstCompile]
        struct ReceiveJob<T> : IJob where T : struct, INetworkPacketReceiver
        {
            public T receiver;
            public long socket;

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
                        return;
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

                int errorcode = 0;
                var result = NativeBindings.network_recvmsg(socket, iov, 2, ref address, ref errorcode);
                if (result == -1)
                {
                    if (errorcode == 10035 || errorcode == 35 || errorcode == 11)
                        return 0;

                    receiver.ReceiveErrorCode = errorcode;
                }
                return result;
            }
        }

        public JobHandle ScheduleReceive<T>(T receiver, JobHandle dep) where T : struct, INetworkPacketReceiver
        {
            var job = new ReceiveJob<T> {receiver = receiver, socket = m_SocketHandle};
            return job.Schedule(dep);
        }

        public int Bind(NetworkEndPoint endpoint)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (endpoint.Family != NetworkFamily.UdpIpv4)
                throw new InvalidOperationException();
#endif

            long newSocket;
            int ret = CreateAndBindSocket(out newSocket, endpoint);
            if (ret != 0)
                return ret;
            Close();

            m_RemoteEndPoint = endpoint;
            m_SocketHandle = newSocket;

            return 0;
        }

        private void Close()
        {
            if (m_SocketHandle < 0)
                return;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AllSockets.OpenSockets.Remove(m_SocketHandle);
#endif
            int errorcode = 0;
            NativeBindings.network_close(ref m_SocketHandle, ref errorcode);
            m_RemoteEndPoint = default(NetworkEndPoint);
            m_SocketHandle = -1;
        }

        public unsafe int SendMessage(network_iovec* iov, int iov_len, ref NetworkEndPoint address)
        {
            int errorcode = 0;
            return NativeBindings.network_sendmsg(m_SocketHandle, iov, iov_len, ref address, ref errorcode);
        }

        int CreateAndBindSocket(out long socket, NetworkEndPoint address)
        {
            socket = -1;
            int errorcode = 0;
            int ret = NativeBindings.network_create_and_bind(ref socket, ref address, ref errorcode);
            if (ret != 0)
                return errorcode;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AllSockets.OpenSockets.Add(socket);
#endif
            NativeBindings.network_set_nonblocking(socket);
            NativeBindings.network_set_send_buffer_size(socket, ushort.MaxValue);
            NativeBindings.network_set_receive_buffer_size(socket, ushort.MaxValue);
#if (UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN)
            // Avoid WSAECONNRESET errors when sending to an endpoint which isn't open yet (unclean connect/disconnects)
            NativeBindings.network_set_connection_reset(socket, 0);
#endif
            return 0;
        }
    }
}