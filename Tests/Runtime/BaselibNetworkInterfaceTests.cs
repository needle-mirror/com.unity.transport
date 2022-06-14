using System.Threading;
using NUnit.Framework;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Utilities;
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

        [Test]
        [UnityPlatform(include = new[] { RuntimePlatform.IPhonePlayer })]
        public void Baselib_AfterAppSuspension_SocketIsRecreated()
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

                // Fake an app suspension by manually calling the focus callback. We add sleeps
                // around the call to ensure the timestamp is different from the receice jobs.
                Thread.Sleep(5);
                AppForegroundTracker.OnFocusChanged(true);
                Thread.Sleep(5);

                dummyDriver.ScheduleUpdate().Complete();
                packetReceiver.m_Driver = dummyDriver;
                baselibInterface.ScheduleReceive(packetReceiver, default).Complete();

                Assert.AreNotEqual(socket, baselibInterface.m_Baselib[0].m_Socket);
            }
        }

        [Test]
        [UnityPlatform(include = new[] { RuntimePlatform.IPhonePlayer })]
        public void Baselib_AfterAppSuspension_CanSendReceive()
        {
            using (var server = NetworkDriver.Create())
            using (var client = NetworkDriver.Create())
            {
                ConnectServerAndClient(NetworkEndPoint.LoopbackIpv4, server, client, out _, out var connection);

                // Fake an app suspension by manually calling the focus callback. We add sleeps
                // around the call to ensure the timestamp is different from the driver updates.
                Thread.Sleep(5);
                AppForegroundTracker.OnFocusChanged(true);
                Thread.Sleep(5);

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
