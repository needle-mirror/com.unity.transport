using NUnit.Framework;
using System.Threading;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Networking.Transport.Protocols;
using Unity.Networking.Transport.Relay;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Unity.Networking.Transport.Tests
{
    public class RelayNetworkDriverTests
    {
        private ushort m_port = 1234;

        [Test]
        public void RelayCheckStructSizes()
        {
            Assert.AreEqual(RelayMessageHeader.Length, UnsafeUtility.SizeOf<RelayMessageHeader>());
            Assert.AreEqual(RelayAllocationId.k_Length, UnsafeUtility.SizeOf<RelayAllocationId>());

            Assert.AreEqual(RelayMessageAccepted.Length, UnsafeUtility.SizeOf<RelayMessageAccepted>());
            Assert.AreEqual(RelayMessageConnectRequest.Length, UnsafeUtility.SizeOf<RelayMessageConnectRequest>());
            Assert.AreEqual(RelayMessageDisconnect.Length, UnsafeUtility.SizeOf<RelayMessageDisconnect>());
            Assert.AreEqual(RelayMessagePing.Length, UnsafeUtility.SizeOf<RelayMessagePing>());
            Assert.AreEqual(RelayMessageRelay.Length, UnsafeUtility.SizeOf<RelayMessageRelay>());
            Assert.AreEqual(RelayConnectionData.k_Length, UnsafeUtility.SizeOf<RelayConnectionData>());
            Assert.AreEqual(RelayHMACKey.k_Length, UnsafeUtility.SizeOf<RelayHMACKey>());
        }

        [Test]
        [UnityPlatform(exclude = new[] { RuntimePlatform.OSXEditor, RuntimePlatform.OSXPlayer })] // MTT-3864
        [Ignore("Unstable in APVs. See MTT-4345.")]
        public void RelayNetworkDriver_Bind_Succeed()
        {
            using var server = new RelayServerMock("127.0.0.1", m_port++);

            var serverData = server.GetRelayConnectionData(0);
            var settings = new NetworkSettings();
            settings.WithRelayParameters(serverData: ref serverData, relayConnectionTimeMS: 10000000);

            using (var driver = new NetworkDriver(new BaselibNetworkInterface(), new RelayNetworkProtocol(), settings))
            {
                Assert.AreEqual(RelayConnectionStatus.NotEstablished, driver.GetRelayConnectionStatus());
                Assert.True(server.CompleteBind(driver, 0));
                Assert.AreEqual(RelayConnectionStatus.Established, driver.GetRelayConnectionStatus());
            }
        }

        [Test]
        [UnityPlatform(exclude = new[] { RuntimePlatform.OSXEditor, RuntimePlatform.OSXPlayer })] // MTT-3864
        [Ignore("Unstable in APVs. See MTT-4345.")]
        public void RelayNetworkDriver_Bind_Retry()
        {
            const int k_RetryCount = 10;

            using var server = new RelayServerMock("127.0.0.1", m_port++);

            var serverData = server.GetRelayConnectionData(0);
            var settings = new NetworkSettings();
            settings.WithRelayParameters(ref serverData)
                .WithNetworkConfigParameters(connectTimeoutMS: 50);

            using (var driver = new NetworkDriver(new BaselibNetworkInterface(), new RelayNetworkProtocol(), settings))
            {
                Assert.AreEqual(RelayConnectionStatus.NotEstablished, driver.GetRelayConnectionStatus());

                var retriesLeft = k_RetryCount;
                server.SetupForBindRetry(k_RetryCount, () => -- retriesLeft, 0);

                Assert.Zero(driver.Bind(NetworkEndPoint.AnyIpv4));
                driver.ScheduleFlushSend(default).Complete();

                RelayServerMock.WaitForCondition(() =>
                {
                    driver.ScheduleUpdate().Complete();
                    return server.IsBound(0);
                });

                Assert.IsTrue(retriesLeft <= 0);
                Assert.IsTrue(server.IsBound(0));
                Assert.AreEqual(RelayConnectionStatus.Established, driver.GetRelayConnectionStatus());
            }
        }

        [Test]
        [UnityPlatform(exclude = new[] { RuntimePlatform.OSXEditor, RuntimePlatform.OSXPlayer })] // MTT-3864
        public void RelayNetworkDriver_Bind_Fail()
        {
            using var server = new RelayServerMock("127.0.0.1", m_port++);

            var serverData = server.GetRelayConnectionData(0);
            var settings = new NetworkSettings();
            settings.WithRelayParameters(serverData: ref serverData, relayConnectionTimeMS: 10000000);

            using (var driver = new NetworkDriver(new BaselibNetworkInterface(), new RelayNetworkProtocol(), settings))
            {
                Assert.AreEqual(RelayConnectionStatus.NotEstablished, driver.GetRelayConnectionStatus());
                server.SetupForBindFail(0);

                Assert.Zero(driver.Bind(NetworkEndPoint.AnyIpv4));

                // One update to send the Bind message out.
                driver.ScheduleUpdate().Complete();

                // Wait long enough for our server to send its error.
                Thread.Sleep(250);

                // One update to receive the Error message.
                driver.ScheduleUpdate().Complete();

                Assert.AreEqual(RelayConnectionStatus.AllocationInvalid, driver.GetRelayConnectionStatus());
                LogAssert.Expect(LogType.Error, "Received error message from Relay: allocation ID not found.");
                LogAssert.Expect(LogType.Error,
                    "Relay allocation is invalid. See NetworkDriver.GetRelayConnectionStatus and " +
                    "RelayConnectionStatus.AllocationInvalid for details on how to handle this situation.");
            }
        }

        [Test]
        [UnityPlatform(exclude = new[] { RuntimePlatform.OSXEditor, RuntimePlatform.OSXPlayer })] // MTT-3864
        public void RelayNetworkDriver_Listen_Succeed()
        {
            using var server = new RelayServerMock("127.0.0.1", m_port++);

            var serverData = server.GetRelayConnectionData(0);
            var settings = new NetworkSettings();
            settings.WithRelayParameters(ref serverData, 10000000);

            using (var host = new NetworkDriver(new BaselibNetworkInterface(), new RelayNetworkProtocol(), settings))
            {
                Assert.True(server.CompleteBind(host, 0));
                Assert.Zero(host.Listen());
            }
        }

        [Test]
        [UnityPlatform(exclude = new[] { RuntimePlatform.OSXEditor, RuntimePlatform.OSXPlayer })] // MTT-3864
        public void RelayNetworkDriver_Connect_Succeed()
        {
            using var server = new RelayServerMock("127.0.0.1", m_port++);

            var serverData0 = server.GetRelayConnectionData(0);
            var serverData1 = server.GetRelayConnectionData(1);
            var settings0 = new NetworkSettings();
            settings0.WithRelayParameters(ref serverData0, 10000000);
            var settings1 = new NetworkSettings();
            settings1.WithRelayParameters(ref serverData1, 10000000);

            using (var host = new NetworkDriver(new BaselibNetworkInterface(), new RelayNetworkProtocol(), settings0))
            using (var client = new NetworkDriver(new BaselibNetworkInterface(), new RelayNetworkProtocol(), settings1))
            {
                Assert.True(server.CompleteBind(host, 0));
                Assert.True(server.CompleteBind(client, 1));

                Assert.Zero(host.Listen());

                server.SetupForConnect(1);

                var clientToHost = client.Connect();

                Assert.AreNotEqual(default(NetworkConnection), clientToHost);

                RelayServerMock.WaitForCondition(() =>
                {
                    client.ScheduleUpdate(default).Complete();
                    host.ScheduleUpdate(default).Complete();

                    return client.GetConnectionState(clientToHost) == NetworkConnection.State.Connected;
                });

                Assert.AreEqual(NetworkConnection.State.Connected, client.GetConnectionState(clientToHost));

                var evt = client.PopEvent(out var clientToHostConnected, out var reader);
                Assert.AreEqual(NetworkEvent.Type.Connect, evt);
                Assert.AreEqual(clientToHost, clientToHostConnected);

                Assert.AreEqual(NetworkEvent.Type.Empty, host.PopEvent(out clientToHostConnected, out reader));

                var hostToClient = host.Accept();
                Assert.AreNotEqual(default(NetworkConnection), hostToClient);
                Assert.AreEqual(NetworkConnection.State.Connected, host.GetConnectionState(hostToClient));
            }
        }

        [Test]
        [UnityPlatform(exclude = new[] { RuntimePlatform.OSXEditor, RuntimePlatform.OSXPlayer })] // MTT-3864
        public void RelayNetworkDriver_Connect_Retry()
        {
            const int k_RetryCount = 10;

            using var server = new RelayServerMock("127.0.0.1", m_port++);

            var serverData0 = server.GetRelayConnectionData(0);
            var serverData1 = server.GetRelayConnectionData(1);
            var settings0 = new NetworkSettings();
            var settings1 = new NetworkSettings();

            using (var host = new NetworkDriver(new BaselibNetworkInterface(), new RelayNetworkProtocol(),
                settings0.WithRelayParameters(ref serverData0, 10000000)))
            using (var client = new NetworkDriver(new BaselibNetworkInterface(), new RelayNetworkProtocol(),
                settings1.WithRelayParameters(ref serverData1)
                    .WithNetworkConfigParameters(connectTimeoutMS: 50)))
            {
                Assert.True(server.CompleteBind(host, 0));
                Assert.True(server.CompleteBind(client, 1));

                Assert.Zero(host.Listen());

                var retriesLeft = k_RetryCount;
                server.SetupForConnectRetry(1, k_RetryCount, () => -- retriesLeft);

                var clientToHost = client.Connect();

                Assert.AreNotEqual(default(NetworkConnection), clientToHost);

                RelayServerMock.WaitForCondition(() =>
                {
                    client.ScheduleUpdate(default).Complete();
                    host.ScheduleUpdate(default).Complete();

                    return client.GetConnectionState(clientToHost) == NetworkConnection.State.Connected;
                });

                Assert.LessOrEqual(0, retriesLeft);

                Assert.AreEqual(NetworkConnection.State.Connected, client.GetConnectionState(clientToHost));

                var evt = client.PopEvent(out var clientToHostConnected, out var reader);
                Assert.AreEqual(NetworkEvent.Type.Connect, evt);
                Assert.AreEqual(clientToHost, clientToHostConnected);

                Assert.AreEqual(NetworkEvent.Type.Empty, host.PopEvent(out clientToHostConnected, out reader));

                var hostToClient = host.Accept();
                Assert.AreNotEqual(default(NetworkConnection), hostToClient);
                Assert.AreEqual(NetworkConnection.State.Connected, host.GetConnectionState(hostToClient));
            }
        }

        [Test]
        [UnityPlatform(exclude = new[] { RuntimePlatform.OSXEditor, RuntimePlatform.OSXPlayer })] // MTT-3864
        public void RelayNetworkDriver_Disconnect_Succeed()
        {
            using var server = new RelayServerMock("127.0.0.1", m_port++);

            var serverData0 = server.GetRelayConnectionData(0);
            var serverData1 = server.GetRelayConnectionData(1);
            var settings0 = new NetworkSettings();
            var settings1 = new NetworkSettings();

            using (var host = new NetworkDriver(new BaselibNetworkInterface(), new RelayNetworkProtocol(), settings0.WithRelayParameters(ref serverData0)))
            using (var client = new NetworkDriver(new BaselibNetworkInterface(), new RelayNetworkProtocol(), settings1.WithRelayParameters(ref serverData1)))
            {
                Assert.True(server.CompleteConnect(host, out var connections, client));
                var connection = connections[0];

                server.SetupForDisconnect(1, 0);

                Assert.AreEqual(NetworkConnection.State.Connected, client.GetConnectionState(connection.clientToHost));
                Assert.AreEqual(NetworkConnection.State.Connected, host.GetConnectionState(connection.hostToClient));

                Assert.Zero(client.Disconnect(connection.clientToHost));

                RelayServerMock.WaitForCondition(() =>
                {
                    client.ScheduleUpdate(default).Complete();
                    host.ScheduleUpdate(default).Complete();

                    return host.GetConnectionState(connection.hostToClient) == NetworkConnection.State.Disconnected;
                });

                Assert.AreEqual(NetworkConnection.State.Disconnected, client.GetConnectionState(connection.clientToHost));
                Assert.AreEqual(NetworkConnection.State.Disconnected, host.GetConnectionState(connection.hostToClient));

                client.ScheduleUpdate().Complete();
                host.ScheduleUpdate().Complete();

                // That the drivers are both disconnected doesn't mean the relay server has received
                // its Disconnect message yet, since it is sent by the client upon receiving the UTP
                // Disconnect message. Most of the time this is not an issue since the message is
                // delivered very quickly to the relay server mock. But on some slower CI machines,
                // the test can end before that happens, so we wait a little to give enough time for
                // the Disconnect message to make it. Yes, this is super ugly.
                Thread.Sleep(100);
            }
        }

        [Test]
        [UnityPlatform(exclude = new[] { RuntimePlatform.OSXEditor, RuntimePlatform.OSXPlayer })] // MTT-3864
        public void RelayNetworkDriver_Send_Succeed()
        {
            const int k_PayloadSize = 100;

            using var server = new RelayServerMock("127.0.0.1", m_port++);

            var serverData0 = server.GetRelayConnectionData(0);
            var serverData1 = server.GetRelayConnectionData(1);
            var settings0 = new NetworkSettings();
            var settings1 = new NetworkSettings();

            using (var host = new NetworkDriver(new BaselibNetworkInterface(), new RelayNetworkProtocol(), settings0.WithRelayParameters(ref serverData0, 10000000)))
            using (var client = new NetworkDriver(new BaselibNetworkInterface(), new RelayNetworkProtocol(), settings1.WithRelayParameters(ref serverData1, 10000000)))
            {
                Assert.True(server.CompleteConnect(host, out var connections, client));

                var connection = connections[0];

                server.SetupForRelay(1, 0, k_PayloadSize + UdpCHeader.Length);

                Assert.Zero(client.BeginSend(connection.clientToHost, out var writer, k_PayloadSize));
                for (int i = 0; i < k_PayloadSize; i++)
                {
                    writer.WriteByte((byte)i);
                }
                Assert.AreEqual(k_PayloadSize, client.EndSend(writer));

                client.ScheduleFlushSend(default).Complete();

                RelayServerMock.WaitForCondition(() =>
                {
                    host.ScheduleUpdate(default).Complete();
                    return host.GetEventQueueSizeForConnection(connection.hostToClient) > 0;
                });

                Assert.AreEqual(NetworkEvent.Type.Data, host.PopEventForConnection(connection.hostToClient, out var reader));

                for (int i = 0; i < k_PayloadSize; i++)
                {
                    Assert.AreEqual((byte)i, reader.ReadByte());
                }
            }
        }

        [Test]
        [UnityPlatform(exclude = new[] { RuntimePlatform.OSXEditor, RuntimePlatform.OSXPlayer })] // MTT-3864
        public void RelayNetworkDriver_AllocationTimeOut()
        {
            using var server = new RelayServerMock("127.0.0.1", m_port++);

            var serverData0 = server.GetRelayConnectionData(0);
            var serverData1 = server.GetRelayConnectionData(1);
            var settings0 = new NetworkSettings();
            settings0.WithRelayParameters(ref serverData0, 10000000);
            var settings1 = new NetworkSettings();
            settings1.WithRelayParameters(ref serverData1, 10000000);

            using (var host = new NetworkDriver(new BaselibNetworkInterface(), new RelayNetworkProtocol(), settings0))
            using (var client = new NetworkDriver(new BaselibNetworkInterface(), new RelayNetworkProtocol(), settings1))
            {
                Assert.True(server.CompleteBind(host, 0));
                Assert.True(server.CompleteBind(client, 1));

                Assert.Zero(host.Listen());

                server.SetupForConnectTimeout(1);

                var clientToHost = client.Connect();

                RelayServerMock.WaitForCondition(() =>
                {
                    client.ScheduleUpdate(default).Complete();
                    host.ScheduleUpdate(default).Complete();

                    return client.GetRelayConnectionStatus() == RelayConnectionStatus.AllocationInvalid;
                }, timeout: 250);

                Assert.AreEqual(RelayConnectionStatus.AllocationInvalid, client.GetRelayConnectionStatus());
                LogAssert.Expect(LogType.Error, "Received error message from Relay: player timed out due to inactivity.");
                LogAssert.Expect(LogType.Error,
                    "Relay allocation is invalid. See NetworkDriver.GetRelayConnectionStatus and " +
                    "RelayConnectionStatus.AllocationInvalid for details on how to handle this situation.");
            }
        }
    }
}
