using System.Threading;
using NUnit.Framework;
using Unity.Networking.Transport;
using UnityEngine;
using UnityEngine.TestTools;
using System.Linq;

using static Unity.Networking.Transport.Tests.CommonUtilites;

namespace Unity.Networking.Transport.Tests
{
    public class BaselibNetworkInterfaceTests
    {
        [Test]
        public unsafe void Baselib_Send_WaitForCompletion()
        {
            var settings = new NetworkSettings();
            settings.WithBaselibNetworkInterfaceParameters(sendQueueCapacity: 2000);

            using (var baselibInterface = new BaselibNetworkInterface())
            {
                baselibInterface.Initialize(settings);
                baselibInterface.CreateInterfaceEndPoint(NetworkEndPoint.AnyIpv4, out var endpoint);
                Assert.Zero(baselibInterface.Bind(endpoint));

                // This tests is only valid when sending packets to a public IP.
                // So we use an invalid one: https://stackoverflow.com/questions/10456044/what-is-a-good-invalid-ip-address-to-use-for-unit-tests/
                baselibInterface.CreateInterfaceEndPoint(NetworkEndPoint.Parse("192.0.2.0", 1234), out var destination);
                var queueHandle = default(NetworkSendQueueHandle);

                var sendInterface = baselibInterface.CreateSendInterface();

                for (int i = 0; i < settings.GetBaselibNetworkInterfaceParameters().sendQueueCapacity; i++)
                {
                    sendInterface.BeginSendMessage.Ptr.Invoke(out var sendHandle, sendInterface.UserData, NetworkParameterConstants.MTU);
                    sendHandle.size = sendHandle.capacity;
                    var data = (byte*)sendHandle.data;
                    for (int j = 0; j < sendHandle.size; j++)
                    {
                        data[j] = (byte)j;
                    }
                    Assert.AreEqual(sendHandle.capacity, sendInterface.EndSendMessage.Ptr.Invoke(ref sendHandle, ref destination, sendInterface.UserData, ref queueHandle));
                }

                baselibInterface.ScheduleSend(default, default).Complete();

                LogAssert.NoUnexpectedReceived();
            }
        }

        private void FakeSocketFailure(BaselibNetworkInterface baselibInterface)
        {
            var baselib = baselibInterface.m_Baselib[0];
            baselib.m_SocketStatus = BaselibNetworkInterface.SocketStatus.SocketNeedsRecreate;
            baselibInterface.m_Baselib[0] = baselib;
        }

        [Test]
        public void Baselib_AfterSocketFailure_SocketIsRecreated()
        {
            using (var baselibInterface = new BaselibNetworkInterface())
            using (var dummyDriver = NetworkDriver.Create())
            {
                var settings = new NetworkSettings();
                baselibInterface.Initialize(settings);
                baselibInterface.CreateInterfaceEndPoint(NetworkEndPoint.AnyIpv4, out var endpoint);
                Assert.Zero(baselibInterface.Bind(endpoint));

                var socket = baselibInterface.m_Baselib[0].m_Socket;

                var packetReceiver = new NetworkPacketReceiver();

                dummyDriver.ScheduleUpdate().Complete();
                packetReceiver.m_Driver = dummyDriver;
                baselibInterface.ScheduleReceive(packetReceiver, default).Complete();

                // Sleep to ensure different update times.
                Thread.Sleep(2);

                FakeSocketFailure(baselibInterface);

                dummyDriver.ScheduleUpdate().Complete();
                packetReceiver.m_Driver = dummyDriver;
                baselibInterface.ScheduleReceive(packetReceiver, default).Complete();

                Assert.AreNotEqual(socket, baselibInterface.m_Baselib[0].m_Socket);

                LogAssert.Expect(LogType.Warning, "Socket error encountered; attempting recovery by creating a new one.");
            }
        }

        [Test]
        public void Baselib_AfterBackToBackSocketFailures_SocketIsFailed()
        {
            using (var baselibInterface = new BaselibNetworkInterface())
            using (var dummyDriver = NetworkDriver.Create())
            {
                var settings = new NetworkSettings();
                baselibInterface.Initialize(settings);
                baselibInterface.CreateInterfaceEndPoint(NetworkEndPoint.AnyIpv4, out var endpoint);
                Assert.Zero(baselibInterface.Bind(endpoint));

                var packetReceiver = new NetworkPacketReceiver();

                dummyDriver.ScheduleUpdate().Complete();
                packetReceiver.m_Driver = dummyDriver;
                baselibInterface.ScheduleReceive(packetReceiver, default).Complete();

                // Sleep to ensure different update times.
                Thread.Sleep(2);

                FakeSocketFailure(baselibInterface);

                dummyDriver.ScheduleUpdate().Complete();
                packetReceiver.m_Driver = dummyDriver;
                baselibInterface.ScheduleReceive(packetReceiver, default).Complete();

                LogAssert.Expect(LogType.Warning, "Socket error encountered; attempting recovery by creating a new one.");

                // Sleep to ensure different update times.
                Thread.Sleep(2);

                FakeSocketFailure(baselibInterface);

                dummyDriver.ScheduleUpdate().Complete();
                packetReceiver.m_Driver = dummyDriver;
                baselibInterface.ScheduleReceive(packetReceiver, default).Complete();

                Assert.AreEqual((int)Error.StatusCode.NetworkSocketError, dummyDriver.ReceiveErrorCode);

                LogAssert.Expect(LogType.Error, "Unrecoverable socket failure. An unknown condition is preventing the application from reliably creating sockets.");
                LogAssert.Expect(LogType.Error, "Error on receive, errorCode = -10");
            }
        }

        [Test]
        public void Baselib_AfterSocketRecreation_CanSendReceive()
        {
            using (var server = NetworkDriver.Create())
            using (var client = NetworkDriver.Create())
            {
                ConnectServerAndClient(NetworkEndPoint.LoopbackIpv4, server, client, out _, out var connection);

                var clientBaselibInterface = (BaselibNetworkInterface)client.NetworkInterface;
                FakeSocketFailure(clientBaselibInterface);

                // Let the server and client recreate their sockets.
                client.ScheduleUpdate().Complete();
                server.ScheduleUpdate().Complete();

                client.BeginSend(connection, out var writer);
                writer.WriteInt(42);
                client.EndSend(writer);

                client.ScheduleUpdate().Complete();

                WaitForDataEvent(server, out var reader);

                Assert.AreEqual(42, reader.ReadInt());
            }
        }
    }
}
