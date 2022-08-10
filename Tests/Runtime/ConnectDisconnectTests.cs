using NUnit.Framework;
using Unity.Networking.Transport;

using static Unity.Networking.Transport.Tests.CommonUtilites;

namespace Unity.Networking.Transport.Tests
{
    public class ConnectDisconnectTests
    {
        private static readonly NetworkEndPoint[] s_EndpointParameters =
        {
            NetworkEndPoint.LoopbackIpv4,
#if !(UNITY_SWITCH || UNITY_PS4 || UNITY_PS5)
            NetworkEndPoint.LoopbackIpv6
#endif
        };

        private static readonly SecureProtocolMode[] s_SecureModeParameters =
        {
#if ENABLE_MANAGED_UNITYTLS
            SecureProtocolMode.SecureProtocolServerAuthOnly,
            SecureProtocolMode.SecureProtocolClientAndServerAuth,
#endif
            SecureProtocolMode.SecureProtocolDisabled
        };

        [Test]
        public void ConnectDisconnect_SingleClient(
            [ValueSource("s_EndpointParameters")] NetworkEndPoint endpoint,
            [ValueSource("s_SecureModeParameters")] SecureProtocolMode secureMode)
        {
            using (var server = CreateServer(secureMode))
            using (var client = CreateClient(secureMode))
            {
                ConnectServerAndClient(endpoint, server, client, out _, out var connection);

                connection.Close(client);
                client.ScheduleUpdate().Complete();

                WaitForEvent(NetworkEvent.Type.Disconnect, server);
            }
        }

        [Test]
        public void ConnectDisconnect_MultipleClients(
            [ValueSource("s_EndpointParameters")] NetworkEndPoint endpoint,
            [ValueSource("s_SecureModeParameters")] SecureProtocolMode secureMode)
        {
            using (var server = CreateServer(secureMode))
            using (var client1 = CreateClient(secureMode))
            using (var client2 = CreateClient(secureMode))
            using (var client3 = CreateClient(secureMode))
            using (var client4 = CreateClient(secureMode))
            using (var client5 = CreateClient(secureMode))
            {
                var clients = new NetworkDriver[] { client1, client2, client3, client4, client5 };
                var connections = new NetworkConnection[clients.Length];

                var nep = SetupServer(endpoint, server);

                for (int i = 0; i < clients.Length; i++)
                    connections[i] = clients[i].Connect(nep);

                int clientsConnected = 0;
                int connectionsAccepted = 0;

                WaitForCondition(() =>
                {
                    server.ScheduleUpdate().Complete();

                    var connection = server.Accept();
                    while (connection.IsCreated)
                    {
                        connectionsAccepted++;
                        connection = server.Accept();
                    }

                    for (int i = 0; i < clients.Length; i++)
                    {
                        clients[i].ScheduleUpdate().Complete();

                        var ev = clients[i].PopEvent(out _, out _);
                        if (ev == NetworkEvent.Type.Connect)
                            clientsConnected++;
                    }

                    return clientsConnected == clients.Length && connectionsAccepted == clients.Length;
                }, "Timed out while waiting to accept all connections.");

                for (int i = 0; i < clients.Length; i++)
                {
                    connections[i].Close(clients[i]);
                    clients[i].ScheduleUpdate().Complete();

                    WaitForEvent(NetworkEvent.Type.Disconnect, server);
                }
            }
        }

        [Test]
        [Ignore("Unstable in APVs. See MTT-4345.")]
        public void ConnectDisconnect_ConnectSucceedsAfterRetrying(
            [ValueSource("s_SecureModeParameters")] SecureProtocolMode secureMode)
        {
            const int ConnectTimeoutMS = 100;
            const int MaxConnectAttempts = 10;

            var clientSettings = new NetworkSettings();
            clientSettings.WithNetworkConfigParameters(
                connectTimeoutMS: ConnectTimeoutMS,
                maxConnectAttempts: MaxConnectAttempts
            );

            using (var server = CreateServer(secureMode))
            using (var client = CreateClient(secureMode, clientSettings))
            {
                var nep = SetupServer(NetworkEndPoint.LoopbackIpv4, server);

                // We test the server not answering by simply killing it and creating a new one later.
                server.Dispose();

                client.Connect(nep);

                // We don't attempt to go to the very limit of the connection attempts, because with
                // such a short connection timeout, it's very possible we wouldn't have time afterwards
                // to complete the connection (especially with DTLS on slower CI machines).
                var retryDuration = (MaxConnectAttempts / 2) * ConnectTimeoutMS;

                RunPeriodicallyFor(retryDuration, () => client.ScheduleUpdate().Complete());

                using (var newServer = CreateServer(secureMode))
                {
                    newServer.Bind(nep);
                    Assert.IsTrue(newServer.Bound, "Failed to bind new server driver.");

                    Assert.AreEqual(0, newServer.Listen(), "Failed to listen on new server driver.");

                    WaitForAcceptedConnection(newServer, client);

                    WaitForEvent(NetworkEvent.Type.Connect, client);
                }
            }
        }

        [Test]
        public void ConnectDisconnect_ConnectFailsAfterTooManyAttempts(
            [ValueSource("s_SecureModeParameters")] SecureProtocolMode secureMode)
        {
            var clientSettings = new NetworkSettings();
            clientSettings.WithNetworkConfigParameters(
                connectTimeoutMS: 100,
                maxConnectAttempts: 3
            );

            using (var server = CreateServer(secureMode))
            using (var client = CreateClient(secureMode, clientSettings))
            {
                var nep = SetupServer(NetworkEndPoint.LoopbackIpv4, server);

                // We test the server never answering by simply killing it.
                server.Dispose();

                client.Connect(nep);

                WaitForEvent(NetworkEvent.Type.Disconnect, client);
            }
        }

        [Test]
        public void ConnectDisconnect_ConnectAfterDisconnect(
            [ValueSource("s_EndpointParameters")] NetworkEndPoint endpoint,
            [ValueSource("s_SecureModeParameters")] SecureProtocolMode secureMode)
        {
            using var server = CreateServer(secureMode);
            using var client = CreateClient(secureMode);

            var nep = SetupServer(endpoint, server);

            var connection = client.Connect(nep);

            WaitForAcceptedConnection(server, client);
            server.ScheduleUpdate().Complete();

            WaitForEvent(NetworkEvent.Type.Connect, client);

            client.Disconnect(connection);
            client.ScheduleUpdate().Complete();

            WaitForEvent(NetworkEvent.Type.Disconnect, server);

            client.Connect(nep);

            WaitForAcceptedConnection(server, client);
            server.ScheduleUpdate().Complete();

            WaitForEvent(NetworkEvent.Type.Connect, client);
        }
    }
}
