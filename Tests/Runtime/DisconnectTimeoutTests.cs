using NUnit.Framework;
using Unity.Networking.Transport;

using static Unity.Networking.Transport.Tests.CommonUtilites;

namespace Unity.Networking.Transport.Tests
{
    public class DisconnectTimeoutTests
    {
        // By default it's 30 seconds. We want tests to go a bit quicker...
        private const int DisconnectTimeoutMS = 200;

        // Time to wait for a disconnection. It's significantly more than the disconnect timeout
        // because lag spikes on slower CI machines can lead to the event being generated late.
        private const long MaxDisconnectTimeMS = DisconnectTimeoutMS + 150;

        [Test]
        public void DisconnectTimeout_ReachedOnCommLoss()
        {
            var settings = new NetworkSettings();
            settings.WithNetworkConfigParameters(disconnectTimeoutMS: DisconnectTimeoutMS);

            using (var server = NetworkDriver.Create(settings))
            using (var client = NetworkDriver.Create(settings))
            {
                ConnectServerAndClient(NetworkEndPoint.LoopbackIpv4, server, client, out _, out _);

                // Make it seem to the server like there's a communication loss.
                client.Dispose();

                WaitForEvent(NetworkEvent.Type.Disconnect, server, MaxDisconnectTimeMS);
            }
        }

        [Test]
        public void DisconnectTimeout_ReachedWithDisabledHeartbeats()
        {
            var settings = new NetworkSettings();
            settings.WithNetworkConfigParameters(
                disconnectTimeoutMS: DisconnectTimeoutMS,
                heartbeatTimeoutMS: 0
            );

            using (var server = NetworkDriver.Create(settings))
            using (var client = NetworkDriver.Create(settings))
            {
                ConnectServerAndClient(NetworkEndPoint.LoopbackIpv4, server, client, out _, out _);

                WaitForCondition(() =>
                {
                    server.ScheduleUpdate().Complete();
                    client.ScheduleUpdate().Complete();

                    var ev1 = server.PopEvent(out _, out _);
                    var ev2 = client.PopEvent(out _, out _);
                    return ev1 == NetworkEvent.Type.Disconnect || ev2 == NetworkEvent.Type.Disconnect;
                }, "Timed out while waiting for Disconnect event.", MaxDisconnectTimeMS);
            }
        }

        [Test]
        public void DisconnectTimeout_ReachedWithInfrequentHeartbeats()
        {
            var settings = new NetworkSettings();
            settings.WithNetworkConfigParameters(
                disconnectTimeoutMS: DisconnectTimeoutMS,
                heartbeatTimeoutMS: DisconnectTimeoutMS * 2
            );

            using (var server = NetworkDriver.Create(settings))
            using (var client = NetworkDriver.Create(settings))
            {
                ConnectServerAndClient(NetworkEndPoint.LoopbackIpv4, server, client, out _, out _);

                WaitForCondition(() =>
                {
                    server.ScheduleUpdate().Complete();
                    client.ScheduleUpdate().Complete();

                    var ev1 = server.PopEvent(out _, out _);
                    var ev2 = client.PopEvent(out _, out _);
                    return ev1 == NetworkEvent.Type.Disconnect || ev2 == NetworkEvent.Type.Disconnect;
                }, "Timed out while waiting for Disconnect event.", MaxDisconnectTimeMS);
            }
        }

        [Test]
        [Ignore("Unstable in APVs. See MTT-4345.")]
        public void DisconnectTimeout_NotReachedWithFrequentHeartbeats()
        {
            var settings = new NetworkSettings();
            settings.WithNetworkConfigParameters(
                disconnectTimeoutMS: DisconnectTimeoutMS,
                heartbeatTimeoutMS: DisconnectTimeoutMS / 2
            );

            using (var server = NetworkDriver.Create(settings))
            using (var client = NetworkDriver.Create(settings))
            {
                ConnectServerAndClient(NetworkEndPoint.LoopbackIpv4, server, client, out _, out _);

                RunPeriodicallyFor(MaxDisconnectTimeMS, () =>
                {
                    server.ScheduleUpdate().Complete();
                    client.ScheduleUpdate().Complete();

                    var ev1 = server.PopEvent(out _, out _);
                    var ev2 = client.PopEvent(out _, out _);

                    if (ev1 == NetworkEvent.Type.Disconnect || ev2 == NetworkEvent.Type.Disconnect)
                        Assert.Fail("Unexpected Disconnect event.");
                });
            }
        }
    }
}
