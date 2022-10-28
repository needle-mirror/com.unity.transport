#if !UNITY_WEBGL || UNITY_EDITOR

using Unity.Burst;
using Unity.Jobs;

namespace Unity.Networking.Transport
{
    [BurstCompile]
    public struct WebSocketNetworkInterface : INetworkInterface
    {
        // In all platforms but WebGL this network interface is just a TCPNetworkInterface in disguise.
        // The websocket protocol is in fact implemented in the WebSocketLayer. For WebGL this interface is
        // implemented in terms of javascript bindings, it does not support Bind()/Listen() and the websocket protocol
        // is implemented by the browser.
        TCPNetworkInterface tcp;

        public NetworkEndpoint LocalEndpoint => tcp.LocalEndpoint;

        internal ConnectionList CreateConnectionList() => tcp.CreateConnectionList();
        public int Initialize(ref NetworkSettings settings, ref int packetPadding) => tcp.Initialize(ref settings, ref packetPadding);
        public int Bind(NetworkEndpoint endpoint) => tcp.Bind(endpoint);
        public int Listen() => tcp.Listen();
        public void Dispose() => tcp.Dispose();

        public JobHandle ScheduleReceive(ref ReceiveJobArguments arguments, JobHandle dep) => tcp.ScheduleReceive(ref arguments, dep);
        public JobHandle ScheduleSend(ref SendJobArguments arguments, JobHandle dep) => tcp.ScheduleSend(ref arguments, dep);
    }
}

#else

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

using Unity.Collections;
using Unity.Jobs;
using Unity.Networking.Transport.Logging;
using Unity.Networking.Transport.Relay;
namespace Unity.Networking.Transport
{
    public struct WebSocketNetworkInterface : INetworkInterface
    {
        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        static void Warn(string msg) =>  DebugLog.LogWarning(msg);

        private const string DLL = "__Internal";

        static class WebSocket
        {
            public static int s_NextSocketId = 0;

            [DllImport(DLL, EntryPoint = "js_html_utpWebSocketCreate")]
            public static extern void Create(int sockId, IntPtr addrData, int addrSize);

            [DllImport(DLL, EntryPoint = "js_html_utpWebSocketDestroy")]
            public static extern void Destroy(int sockId);

            [DllImport(DLL, EntryPoint = "js_html_utpWebSocketSend")]
            public static extern int Send(int sockId, IntPtr data, int size);

            [DllImport(DLL, EntryPoint = "js_html_utpWebSocketRecv")]
            public static extern int Recv(int sockId, IntPtr data, int size);

            [DllImport(DLL, EntryPoint = "js_html_utpWebSocketIsConnected")]
            public static extern int IsConnectionReady(int sockId);
        }

        unsafe struct InternalData
        {
            public NetworkEndpoint ListenEndpoint;
            public int ConnectTimeoutMS;            // maximum time to wait for a connection to complete
            public int MaxConnectAttempts;          // maximum number of connect retries

            // If non-empty, will connect to this hostname with the wss:// protocol. Otherwise the
            // IP address of the endpoint is used to connect with the ws:// protocol.
            public FixedString512Bytes SecureHostname;
        }

        unsafe struct ConnectionData
        {
            public int Socket;

            public long ConnectTime;                // Connect start time
            public long LastConnectAttemptTime;     // Time of the last attempt
            public int LastConnectAttempt;          // Number of attempts so far
        }

        private NativeReference<InternalData> m_InternalData;

        // Maps a connection id from the connection list to its connection data.
        private ConnectionDataMap<ConnectionData> m_ConnectionMap;

        // List of connection information carried over to the layer above
        private ConnectionList m_ConnectionList;

        internal ConnectionList CreateConnectionList()
        {
            m_ConnectionList = ConnectionList.Create();
            return m_ConnectionList;
        }

        public unsafe NetworkEndpoint LocalEndpoint => m_InternalData.Value.ListenEndpoint;

        public bool IsCreated => m_InternalData.IsCreated;

        public unsafe int Initialize(ref NetworkSettings settings, ref int packetPadding)
        {
            var networkConfiguration = settings.GetNetworkConfigParameters();

            // This needs to match the value of Unity.Networking.Transport.WebSocket.MaxPayloadSize
            packetPadding += 14;

            var secureHostname = new FixedString512Bytes();
            if (settings.TryGet<RelayNetworkParameter>(out var relayParams) && relayParams.ServerData.IsSecure != 0)
                secureHostname.CopyFrom(relayParams.ServerData.HostString);

#if ENABLE_MANAGED_UNITYTLS
            // Shouldn't be required for normal use cases but is provided as an out in case the user
            // wants to override the hostname (useful if say the user ended up resolving the Relay's
            // hostname on their own instead of providing it directly in the Relay parameters).
            if (settings.TryGet<TLS.SecureNetworkProtocolParameter>(out var secureParams))
                secureHostname.CopyFrom(secureParams.Hostname);
#endif

            var state = new InternalData
            {
                ListenEndpoint = NetworkEndpoint.AnyIpv4,
                ConnectTimeoutMS = Math.Max(0, networkConfiguration.connectTimeoutMS),
                MaxConnectAttempts = Math.Max(1, networkConfiguration.maxConnectAttempts),
                SecureHostname = secureHostname,
            };
            m_InternalData = new NativeReference<InternalData>(state, Allocator.Persistent);

            m_ConnectionMap = new ConnectionDataMap<ConnectionData>(1, default, Allocator.Persistent);
            return 0;
        }

        public unsafe int Bind(NetworkEndpoint endpoint)
        {
            var state = m_InternalData.Value;
            state.ListenEndpoint = endpoint;
            m_InternalData.Value = state;

            return 0;
        }

        public unsafe int Listen()
            =>  throw new InvalidOperationException("Web browsers do not support listening for new WebSocket connections.");

        public unsafe void Dispose()
        {
            m_InternalData.Dispose();

            for (int i = 0; i < m_ConnectionMap.Length; ++i)
            {
                WebSocket.Destroy(m_ConnectionMap.DataAt(i).Socket);
            }

            m_ConnectionMap.Dispose();
            m_ConnectionList.Dispose();
        }

        public JobHandle ScheduleReceive(ref ReceiveJobArguments arguments, JobHandle dep)
        {
            return new ReceiveJob
            {
                ReceiveQueue = arguments.ReceiveQueue,
                InternalData = m_InternalData,
                ConnectionList = m_ConnectionList,
                ConnectionMap = m_ConnectionMap,
                Time = arguments.Time,
            }.Schedule(dep);
        }

        struct ReceiveJob : IJob
        {
            public PacketsQueue ReceiveQueue;
            public NativeReference<InternalData> InternalData;
            public ConnectionList ConnectionList;
            public ConnectionDataMap<ConnectionData> ConnectionMap;
            public long Time;

            private void Abort(ref ConnectionId connectionId, ref ConnectionData connectionData, Error.DisconnectReason reason = default)
            {
                ConnectionList.FinishDisconnecting(ref connectionId, reason);
                ConnectionMap.ClearData(ref connectionId);
                WebSocket.Destroy(connectionData.Socket);
            }

            public unsafe void Execute()
            {
                // Update each connection from the connection list
                var count = ConnectionList.Count;
                for (int i = 0; i < count; i++)
                {
                    var connectionId = ConnectionList.ConnectionAt(i);
                    var connectionState = ConnectionList.GetConnectionState(connectionId);

                    if (connectionState == NetworkConnection.State.Disconnected)
                        continue;

                    var connectionData = ConnectionMap[connectionId];

                    // Detect if the upper layer is requesting to connect.
                    if (connectionState == NetworkConnection.State.Connecting)
                    {
                        // The time here is a signed 64bit and we're never going to run at time 0 so if the connection
                        // has ConnectTime == 0 it's the creation of this connection data.
                        if (connectionData.ConnectTime == 0)
                        {
                            var socket = ++WebSocket.s_NextSocketId;
                            GetServerAddress(connectionId, out var address);
                            WebSocket.Create(socket, (IntPtr)address.GetUnsafePtr(), address.Length);

                            connectionData.ConnectTime = Time;
                            connectionData.LastConnectAttemptTime = Time;
                            connectionData.Socket = socket;
                        }

                        // Disconnect if maximum connection attempts reached
                        if (connectionData.LastConnectAttempt >= InternalData.Value.MaxConnectAttempts)
                        {
                            ConnectionList.StartDisconnecting(ref connectionId);
                            Abort(ref connectionId, ref connectionData, Error.DisconnectReason.MaxConnectionAttempts);
                            continue;
                        }

                        // Check if it's time to retry connect which just means incrementing attempts
                        if (Time - connectionData.LastConnectAttemptTime >= InternalData.Value.ConnectTimeoutMS)
                        {
                            connectionData.LastConnectAttempt++;
                            connectionData.LastConnectAttemptTime = Time;

                            var status = WebSocket.IsConnectionReady(connectionData.Socket);
                            if (status > 0)
                            {
                                ConnectionList.FinishConnectingFromLocal(ref connectionId);
                            }
                            else if (status < 0)
                            {
                                Warn($"WebSocket failed: status={status}");
                                ConnectionList.StartDisconnecting(ref connectionId);
                                Abort(ref connectionId, ref connectionData, Error.DisconnectReason.MaxConnectionAttempts);
                                continue;
                            }
                        }

                        ConnectionMap[connectionId] = connectionData;
                        continue;
                    }

                    // Detect if the upper layer is requesting to disconnect.
                    if (connectionState == NetworkConnection.State.Disconnecting)
                    {
                        Abort(ref connectionId, ref connectionData);
                        continue;
                    }

                    // Read data from the connection if we can. Receive should return chunks of up to MTU.
                    // Close the connection in case of a receive error.
                    var endpoint = ConnectionList.GetConnectionEndpoint(connectionId);
                    var nbytes = 0;
                    while (true)
                    {
                        // No need to disconnect in case the receive queue becomes full just let the TCP socket buffer
                        // the incoming data.
                        if (!ReceiveQueue.EnqueuePacket(out var packetProcessor))
                            break;

                        nbytes = WebSocket.Recv(connectionData.Socket, (IntPtr)(byte*)packetProcessor.GetUnsafePayloadPtr() + packetProcessor.Offset, packetProcessor.BytesAvailableAtEnd);
                        if (nbytes > 0)
                        {
                            packetProcessor.ConnectionRef = connectionId;
                            packetProcessor.EndpointRef = endpoint;
                            packetProcessor.SetUnsafeMetadata(nbytes, packetProcessor.Offset);
                        }
                        else
                        {
                            packetProcessor.Drop();
                            break;
                        }
                    }

                    if (nbytes < 0)
                    {
                        // Disconnect
                        ConnectionList.StartDisconnecting(ref connectionId);
                        Abort(ref connectionId, ref connectionData, Error.DisconnectReason.ClosedByRemote);
                        continue;
                    }

                    // Update the connection data
                    ConnectionMap[connectionId] = connectionData;
                }
            }

            // Get the address to connect to for the given connection. If not using TLS, then this
            // is just "ws://{address}:{port}" where address/port are taken from the connection's
            // endpoint in the connection list. But if using TLS, then the hostname provided in the
            // secure parameters overrides the address, and we connect to "wss://{hostname}:{port}"
            // (with the port still taken from the connection's endpoint in the connection list).
            private void GetServerAddress(ConnectionId connection, out FixedString512Bytes address)
            {
                var endpoint = ConnectionList.GetConnectionEndpoint(connection);
                var secureHostname = InternalData.Value.SecureHostname;

                if (secureHostname.IsEmpty)
                    address = FixedString.Format("ws://{0}", endpoint.ToFixedString());
                else
                    address = FixedString.Format("wss://{0}:{1}", secureHostname, endpoint.Port);
            }
        }

        public JobHandle ScheduleSend(ref SendJobArguments arguments, JobHandle dep)
        {
            return new SendJob
            {
                SendQueue = arguments.SendQueue,
                ConnectionList = m_ConnectionList,
                ConnectionMap = m_ConnectionMap,
            }.Schedule(dep);
        }

        unsafe struct SendJob : IJob
        {
            public PacketsQueue SendQueue;
            public ConnectionList ConnectionList;
            public ConnectionDataMap<ConnectionData> ConnectionMap;

            private void Abort(ref ConnectionId connectionId, ref ConnectionData connectionData, Error.DisconnectReason reason = default)
            {
                ConnectionList.FinishDisconnecting(ref connectionId, reason);
                ConnectionMap.ClearData(ref connectionId);
                WebSocket.Destroy(connectionData.Socket);
            }

            public void Execute()
            {
                // Each packet is sent individually. The connection is aborted if a packet cannot be transmiited
                // entirely.
                for (int i = 0; i < SendQueue.Count; i++)
                {
                    var packetProcessor = SendQueue[i];
                    if (packetProcessor.Length == 0)
                        continue;

                    var connectionId = packetProcessor.ConnectionRef;
                    var connectionState = ConnectionList.GetConnectionState(connectionId);

                    if (connectionState == NetworkConnection.State.Disconnected)
                        continue;

                    var connectionData = ConnectionMap[connectionId];

                    var nbytes = WebSocket.Send(connectionData.Socket, (IntPtr)(byte*)packetProcessor.GetUnsafePayloadPtr() + packetProcessor.Offset, packetProcessor.Length);
                    if (nbytes != packetProcessor.Length)
                    {
                        Warn($"Send buffer overflow trying to send data to {ConnectionList.GetConnectionEndpoint(connectionId)}");

                        // Disconnect
                        ConnectionList.StartDisconnecting(ref connectionId);
                        Abort(ref connectionId, ref connectionData, Error.DisconnectReason.ClosedByRemote);
                        continue;
                    }

                    ConnectionMap[connectionId] = connectionData;
                }
            }
        }
    }
}

#endif
