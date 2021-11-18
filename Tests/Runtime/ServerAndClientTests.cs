using System;
using System.Collections;
using Unity.Collections;
using NUnit.Framework;
using UnityEngine.TestTools;
using UnityEngine;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Protocols;
using Unity.Burst;

namespace Tests
{
    [TestFixture]
    public class ServerAndClientTests
    {
        NetworkDriver server_driver;
        NetworkConnection connectionToClient;

        NetworkDriver client_driver;
        NetworkConnection clientToServerConnection;

        NetworkEvent.Type ev;
        DataStreamReader stream;

        private const string backend = "baselib";

        private readonly NetworkConfigParameter defaultConfigParams = new NetworkConfigParameter
        {
            connectTimeoutMS = NetworkParameterConstants.ConnectTimeoutMS,
            maxConnectAttempts = NetworkParameterConstants.MaxConnectAttempts,
            disconnectTimeoutMS = NetworkParameterConstants.DisconnectTimeoutMS,
            heartbeatTimeoutMS = NetworkParameterConstants.HeartbeatTimeoutMS
        };

        NetworkEvent.Type PollDriverAndFindDataEvent(ref NetworkDriver driver, NetworkConnection connection, NetworkEvent.Type eventType, int maxRetryCount = 10)
        {
            for (int retry = 0; retry < maxRetryCount; ++retry)
            {
                driver.ScheduleUpdate().Complete();
                ev = driver.PopEventForConnection(connection, out stream);
                if (ev == eventType || ev != NetworkEvent.Type.Empty)
                {
                    return ev;
                }
            }
            return NetworkEvent.Type.Empty;
        }

        public void SetupServerAndClientAndConnectThem(NetworkEndPoint nep, int bufferSize, NetworkConfigParameter configParams)
        {
            //setup server
            var settings = new NetworkSettings();
            settings.WithDataStreamParameters(bufferSize)
                .AddRawParameterStruct(ref configParams);

            server_driver = NetworkDriver.Create(settings);
            NetworkEndPoint server_endpoint = nep;
            server_endpoint.Port = 1337;
            var ret = server_driver.Bind(server_endpoint);
            int maxRetry = 10;
            while (ret != 0 && --maxRetry > 0)
            {
                server_endpoint.Port += 17;
                ret = server_driver.Bind(server_endpoint);
            }
            Assert.AreEqual(ret, 0, "Failed to bind the socket");
            server_driver.Listen();

            //setup client
            client_driver = NetworkDriver.Create(settings);
            clientToServerConnection = client_driver.Connect(server_endpoint);

            //update drivers
            client_driver.ScheduleUpdate().Complete();

            for (int i = 0; i < 10; ++i)
            {
                server_driver.ScheduleUpdate().Complete();
                //accept connection
                connectionToClient = server_driver.Accept();
                if (connectionToClient.IsCreated)
                    break;
            }
            Assert.IsTrue(connectionToClient.IsCreated, "Failed to accept the connection");

            ev = PollDriverAndFindDataEvent(ref server_driver, connectionToClient, NetworkEvent.Type.Empty);
            Assert.IsTrue(ev == NetworkEvent.Type.Empty, $"Not empty NetworkEvent on the server appeared, got {ev} using {backend}");

            ev = PollDriverAndFindDataEvent(ref client_driver, clientToServerConnection, NetworkEvent.Type.Connect);
            Assert.IsTrue(ev == NetworkEvent.Type.Connect, $"NetworkEvent should have Type.Connect on the client, but got {ev} using {backend}");
        }

        public void DisconnectAndCleanup()
        {
            clientToServerConnection.Close(client_driver);

            //update drivers
            client_driver.ScheduleUpdate().Complete();
            ev = PollDriverAndFindDataEvent(ref server_driver, connectionToClient, NetworkEvent.Type.Disconnect);
            Assert.IsTrue(ev == NetworkEvent.Type.Disconnect, "NetworkEvent.Type.Disconnect was expected to appear, but " + ev + " appeared");

            server_driver.Dispose();
            client_driver.Dispose();
        }

        [Test]
        public void ServerAndClient_Connect_Successfull_IPv6()
        {
            SetupServerAndClientAndConnectThem(NetworkEndPoint.LoopbackIpv6, 0, defaultConfigParams);
            DisconnectAndCleanup();
        }

        [Test]
        public void ServerAndClient_Connect_Successfully_IPv4()
        {
            SetupServerAndClientAndConnectThem(NetworkEndPoint.LoopbackIpv4, 0, defaultConfigParams);
            DisconnectAndCleanup();
        }

        [Test]
        public void ServerAnd5Clients_Connect_Successfully_IPv6()
        {
            ServerAnd5Clients_Connect_Successfully(NetworkEndPoint.LoopbackIpv6);
        }

        [Test]
        public void ServerAnd5Clients_Connect_Successfully_IPv4()
        {
            ServerAnd5Clients_Connect_Successfully(NetworkEndPoint.LoopbackIpv4);
        }

        public void ServerAnd5Clients_Connect_Successfully(NetworkEndPoint nep)
        {
            int numberOfClients = 5;
            NativeArray<NetworkConnection> connectionToClientArray;
            NetworkDriver[] client_driversArray = new NetworkDriver[numberOfClients];
            NativeArray<NetworkConnection> clientToServerConnectionsArray;

            //setup server
            connectionToClientArray = new NativeArray<NetworkConnection>(numberOfClients, Allocator.Persistent);
            server_driver = NetworkDriver.Create();
            NetworkEndPoint server_endpoint = nep;
            server_endpoint.Port = 1337;
            server_driver.Bind(server_endpoint);
            server_driver.Listen();

            //setup clients
            clientToServerConnectionsArray = new NativeArray<NetworkConnection>(numberOfClients, Allocator.Persistent);

            for (int i = 0; i < numberOfClients; i++)
            {
                client_driversArray[i] = NetworkDriver.Create();
                clientToServerConnectionsArray[i] = client_driversArray[i].Connect(server_endpoint);
            }

            //update drivers
            for (int i = 0; i < numberOfClients; i++)
                client_driversArray[i].ScheduleUpdate().Complete();
            server_driver.ScheduleUpdate().Complete();

            //accept connections
            for (int i = 0; i < numberOfClients; i++)
            {
                connectionToClientArray[i] = server_driver.Accept();
                ev = PollDriverAndFindDataEvent(ref server_driver, connectionToClientArray[i], NetworkEvent.Type.Empty);
                Assert.IsTrue(ev == NetworkEvent.Type.Empty, "Not empty NetworkEvent on the server appeared");
                ev = PollDriverAndFindDataEvent(ref client_driversArray[i], clientToServerConnectionsArray[i], NetworkEvent.Type.Connect);
                Assert.IsTrue(ev == NetworkEvent.Type.Connect, "NetworkEvent should have Type.Connect on the client");
            }
            //close connections
            for (int i = 0; i < numberOfClients; i++)
            {
                clientToServerConnectionsArray[i].Close(client_driversArray[i]);

                //update drivers
                client_driversArray[i].ScheduleUpdate().Complete();
                ev = PollDriverAndFindDataEvent(ref server_driver, connectionToClientArray[i], NetworkEvent.Type.Disconnect);
                Assert.IsTrue(ev == NetworkEvent.Type.Disconnect, "NetworkEvent.Type.Disconnect was expected to appear, but " + ev + " appeared");
            }

            server_driver.Dispose();
            for (int i = 0; i < numberOfClients; i++)
            {
                client_driversArray[i].Dispose();
            }
            connectionToClientArray.Dispose();
            clientToServerConnectionsArray.Dispose();
        }

        [Test]
        public void ServerAndClient_PingPong_Successfully_IPv6()
        {
            ServerAndClient_PingPong_Successfully(NetworkEndPoint.LoopbackIpv6);
        }

        [Test]
        public void ServerAndClient_PingPong_Successfully_IPv4()
        {
            ServerAndClient_PingPong_Successfully(NetworkEndPoint.LoopbackIpv4);
        }

        public void ServerAndClient_PingPong_Successfully(NetworkEndPoint nep)
        {
            SetupServerAndClientAndConnectThem(nep, 0, defaultConfigParams);

            //send data from client
            if (client_driver.BeginSend(clientToServerConnection, out var m_OutStream) == 0)
            {
                m_OutStream.Clear();
                m_OutStream.WriteBytes(new NativeArray<byte>(SharedConstants.ping, Allocator.Temp));
                client_driver.EndSend(m_OutStream);
            }
            client_driver.ScheduleFlushSend(default).Complete();

            //handle sent data
            ev = PollDriverAndFindDataEvent(ref server_driver, connectionToClient, NetworkEvent.Type.Data);
            Assert.IsTrue(ev == NetworkEvent.Type.Data, "Expected to get Type.Data");
            var msg = new NativeArray<byte>(stream.Length, Allocator.Temp);
            stream.ReadBytes(msg);
            if (msg.Length == SharedConstants.ping.Length)
            {
                for (var i = 0; i < msg.Length; i++)
                {
                    if (SharedConstants.ping[i] != msg[i])
                    {
                        Assert.Fail("Data reading error");
                    }
                }
            }

            client_driver.ScheduleUpdate().Complete();

            //send data from server
            if (server_driver.BeginSend(connectionToClient, out m_OutStream) == 0)
            {
                m_OutStream.WriteBytes(new NativeArray<byte>(SharedConstants.pong, Allocator.Temp));
                server_driver.EndSend(m_OutStream);
            }

            //handle sent data
            server_driver.ScheduleUpdate().Complete();

            ev = PollDriverAndFindDataEvent(ref client_driver, clientToServerConnection, NetworkEvent.Type.Data);
            Assert.IsTrue(ev == NetworkEvent.Type.Data, "Expected to get Type.Data");
            msg = new NativeArray<byte>(stream.Length, Allocator.Temp);
            stream.ReadBytes(msg);
            if (msg.Length == SharedConstants.pong.Length)
            {
                for (var i = 0; i < msg.Length; i++)
                {
                    if (SharedConstants.pong[i] != msg[i])
                    {
                        Assert.Fail("Data reading error");
                    }
                }
            }

            DisconnectAndCleanup();
        }

        //test for buffer overflow
        [UnityTest, UnityPlatform(RuntimePlatform.LinuxEditor, RuntimePlatform.WindowsEditor, RuntimePlatform.OSXEditor)]
        public IEnumerator ServerAndClient_SendBigMessage_OverflowsIncomingDriverBuffer()
        {
            SetupServerAndClientAndConnectThem(NetworkEndPoint.LoopbackIpv4, UdpCHeader.Length + SessionIdToken.k_Length, defaultConfigParams);

            //send data from client
            if (client_driver.BeginSend(clientToServerConnection, out var m_OutStream) == 0)
            {
                m_OutStream.Clear();
                m_OutStream.WriteBytes(new NativeArray<byte>(SharedConstants.ping, Allocator.Temp));
                client_driver.EndSend(m_OutStream);
            }
            client_driver.ScheduleFlushSend(default).Complete();

            LogAssert.Expect(LogType.Error, "Error on receive, errorCode = 10040");

            //handle sent data
            server_driver.ScheduleUpdate().Complete();
            client_driver.ScheduleUpdate().Complete();

            Assert.AreEqual(10040, server_driver.ReceiveErrorCode);

            DisconnectAndCleanup();
            yield return null;
        }

        [Test]
        public void ServerAndClient_SendMessageWithMaxLength_SentAndReceivedWithoutErrors()
        {
            SetupServerAndClientAndConnectThem(NetworkEndPoint.LoopbackIpv4, 0, defaultConfigParams);

            int messageLength = NetworkParameterConstants.MTU - client_driver.MaxHeaderSize(NetworkPipeline.Null);
            var messageToSend = new NativeArray<byte>(messageLength, Allocator.Temp);
            for (int i = 0; i < messageLength; i++)
            {
                messageToSend[i] = (byte)(33 + (i % 93));
            }
            //send data from client
            if (client_driver.BeginSend(clientToServerConnection, out var m_OutStream) == 0)
            {
                m_OutStream.WriteBytes(messageToSend);
                client_driver.EndSend(m_OutStream);
            }

            client_driver.ScheduleFlushSend(default).Complete();

            ev = PollDriverAndFindDataEvent(ref server_driver, connectionToClient, NetworkEvent.Type.Data);
            Assert.IsTrue(ev == NetworkEvent.Type.Data, "Expected to get Type.Data");
            var msg = new NativeArray<byte>(stream.Length, Allocator.Temp);
            stream.ReadBytes(msg);
            Assert.IsTrue(msg.Length == messageLength, "Lenghts of sent and received messages are different");

            DisconnectAndCleanup();
        }

        [UnityTest, UnityPlatform(RuntimePlatform.LinuxEditor, RuntimePlatform.WindowsEditor, RuntimePlatform.OSXEditor)]
        [Ignore("EndSend can't send messages larger than MTU. Test is actually testing sending an empty message.")]
        public IEnumerator ServerAndClient_SendMessageWithMoreThenMaxLength_OverflowsIncomingDriverBuffer()
        {
            SetupServerAndClientAndConnectThem(NetworkEndPoint.LoopbackIpv4, 0, defaultConfigParams);

            //send data from client

            if (client_driver.BeginSend(clientToServerConnection, out var m_OutStream) == 0)
            {
                m_OutStream.Clear();
                int messageLength = 1401 - UdpCHeader.Length;
                var messageToSend = new NativeArray<byte>(messageLength, Allocator.Temp);
                for (int i = 0; i < messageLength; i++)
                {
                    messageToSend[i] = (byte)(33 + (i % 93));
                }

                Assert.IsFalse(m_OutStream.WriteBytes(messageToSend));
                Assert.AreEqual(0, client_driver.EndSend(m_OutStream));
            }
            client_driver.ScheduleFlushSend(default).Complete();

            //handle sent data
            client_driver.ScheduleUpdate().Complete();
            ev = PollDriverAndFindDataEvent(ref server_driver, connectionToClient, NetworkEvent.Type.Data);
            Assert.IsTrue(ev == NetworkEvent.Type.Data, "Expected to get Type.Empty");
            Assert.IsFalse(stream.IsCreated);

            DisconnectAndCleanup();
            yield return null;
        }

        [UnityTest, UnityPlatform(RuntimePlatform.LinuxEditor, RuntimePlatform.WindowsEditor, RuntimePlatform.OSXEditor)]
        public IEnumerator ServerAndClient_SendMessageWithoutReadingIt_GivesErrorOnDriverUpdate()
        {
            SetupServerAndClientAndConnectThem(NetworkEndPoint.LoopbackIpv4, 0, defaultConfigParams);

            //send data from client
            if (client_driver.BeginSend(clientToServerConnection, out var m_OutStream) == 0)
            {
                m_OutStream.WriteBytes(new NativeArray<byte>(SharedConstants.ping, Allocator.Temp));
                client_driver.EndSend(m_OutStream);
            }
            client_driver.ScheduleFlushSend(default).Complete();

            server_driver.ScheduleUpdate().Complete();
            client_driver.ScheduleUpdate().Complete();

            LogAssert.Expect(LogType.Error, "Resetting event queue with pending events (Count=1, ConnectionID=0) Listening: 1");
            server_driver.ScheduleUpdate().Complete();

            DisconnectAndCleanup();
            yield return null;
        }

        [UnityTest]
        public IEnumerator ServerAndClient_DisconnectTimeout_ReachedOnCommLoss()
        {
            const int DisconnectTimeout = 200;
            const float WaitTime = (DisconnectTimeout / 1000f) + 0.05f;

            var config = defaultConfigParams;
            config.disconnectTimeoutMS = DisconnectTimeout;

            SetupServerAndClientAndConnectThem(NetworkEndPoint.LoopbackIpv4, 0, config);

            // Make it seem to the server like there's a communication loss.
            client_driver.Dispose();

            NetworkEvent.Type ev = NetworkEvent.Type.Empty;

            float startTime = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - startTime <= WaitTime)
            {
                ev = PollDriverAndFindDataEvent(ref server_driver, connectionToClient, NetworkEvent.Type.Disconnect);
                if (ev == NetworkEvent.Type.Disconnect)
                {
                    break;
                }

                yield return null;
            }

            server_driver.Dispose();
            yield return null;

            Assert.AreEqual(NetworkEvent.Type.Disconnect, ev);
        }

        [UnityTest]
        public IEnumerator ServerAndClient_DisconnectTimeout_ReachedWithDisabledHeartbeats()
        {
            const int DisconnectTimeout = 200;
            const float WaitTime = (DisconnectTimeout / 1000f) + 0.05f;

            var config = defaultConfigParams;
            config.disconnectTimeoutMS = DisconnectTimeout;
            config.heartbeatTimeoutMS = 0;

            SetupServerAndClientAndConnectThem(NetworkEndPoint.LoopbackIpv4, 0, config);

            bool disconnected = false;

            float startTime = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - startTime <= WaitTime)
            {
                var ev1 = PollDriverAndFindDataEvent(ref server_driver, connectionToClient, NetworkEvent.Type.Disconnect);
                var ev2 = PollDriverAndFindDataEvent(ref client_driver, clientToServerConnection, NetworkEvent.Type.Disconnect);
                if (ev1 == NetworkEvent.Type.Disconnect || ev2 == NetworkEvent.Type.Disconnect)
                {
                    disconnected = true;
                    break;
                }

                yield return null;
            }

            server_driver.Dispose();
            client_driver.Dispose();
            yield return null;

            Assert.IsTrue(disconnected);
        }

        [UnityTest]
        public IEnumerator ServerAndClient_DisconnectTimeout_ReachedWithInfrequentHeartbeats()
        {
            const int DisconnectTimeout = 200;
            const float WaitTime = (DisconnectTimeout / 1000f) + 0.05f;

            var config = defaultConfigParams;
            config.disconnectTimeoutMS = DisconnectTimeout;
            config.heartbeatTimeoutMS = DisconnectTimeout * 2;

            SetupServerAndClientAndConnectThem(NetworkEndPoint.LoopbackIpv4, 0, config);

            bool disconnected = false;

            float startTime = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - startTime <= WaitTime)
            {
                var ev1 = PollDriverAndFindDataEvent(ref server_driver, connectionToClient, NetworkEvent.Type.Disconnect);
                var ev2 = PollDriverAndFindDataEvent(ref client_driver, clientToServerConnection, NetworkEvent.Type.Disconnect);
                if (ev1 == NetworkEvent.Type.Disconnect || ev2 == NetworkEvent.Type.Disconnect)
                {
                    disconnected = true;
                    break;
                }

                yield return null;
            }

            server_driver.Dispose();
            client_driver.Dispose();
            yield return null;

            Assert.IsTrue(disconnected);
        }

        [UnityTest]
        public IEnumerator ServerAndClient_DisconnectTimeout_NotReachedWithFrequentHeartbeats()
        {
            const int DisconnectTimeout = 200;
            const float WaitTime = (DisconnectTimeout / 1000f) + 0.05f;

            var config = defaultConfigParams;
            config.disconnectTimeoutMS = DisconnectTimeout;
            config.heartbeatTimeoutMS = DisconnectTimeout / 2;

            SetupServerAndClientAndConnectThem(NetworkEndPoint.LoopbackIpv4, 0, config);

            bool disconnected = false;

            float startTime = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - startTime <= WaitTime)
            {
                var ev1 = PollDriverAndFindDataEvent(ref server_driver, connectionToClient, NetworkEvent.Type.Disconnect);
                var ev2 = PollDriverAndFindDataEvent(ref client_driver, clientToServerConnection, NetworkEvent.Type.Disconnect);
                if (ev1 == NetworkEvent.Type.Disconnect || ev2 == NetworkEvent.Type.Disconnect)
                {
                    disconnected = true;
                    break;
                }

                yield return null;
            }

            server_driver.Dispose();
            client_driver.Dispose();
            yield return null;

            Assert.IsFalse(disconnected);
        }
    }
}

public class SharedConstants
{
    public static byte[] ping =
    {
        (byte)'f',
        (byte)'r',
        (byte)'o',
        (byte)'m',
        (byte)'s',
        (byte)'e',
        (byte)'r',
        (byte)'v',
        (byte)'e',
        (byte)'r'
    };

    public static byte[] pong =
    {
        (byte)'c',
        (byte)'l',
        (byte)'i',
        (byte)'e',
        (byte)'n',
        (byte)'t'
    };
}
