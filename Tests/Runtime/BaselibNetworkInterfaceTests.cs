using NUnit.Framework;
using Unity.Networking.Transport;
using UnityEngine.TestTools;
using System.Linq;

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
    }
}
