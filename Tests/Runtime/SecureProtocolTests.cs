#if ENABLE_MANAGED_UNITYTLS

using NUnit.Framework;
using Unity.Networking.Transport;
using UnityEngine;
using UnityEngine.TestTools;

using static Unity.Networking.Transport.Tests.CommonUtilites;

namespace Unity.Networking.Transport.Tests
{
    public class SecureProtocolTests
    {
        private static readonly SecureProtocolMode[] s_SecureModeParameters =
        {
            SecureProtocolMode.SecureProtocolServerAuthOnly,
            SecureProtocolMode.SecureProtocolClientAndServerAuth
        };

        [Test]
        [Ignore("Unstable because of Burst bug. See MTT-2511.")]
        public void SecureProtocol_HalfOpenConnectionsPruning(
            [ValueSource("s_SecureModeParameters")] SecureProtocolMode secureMode)
        {
            using (var server = CreateServer(secureMode))
            using (var client = CreateClient(secureMode))
            {
                var nep = SetupServer(NetworkEndPoint.LoopbackIpv4, server);

                var connection = client.Connect(nep);

                // Let the client begin the handshake...
                client.ScheduleUpdate().Complete();

                // ...but kill it before it can complete it.
                client.Dispose();

                // We won't check pruning until SSLHandshakeTimeoutMin has elapsed, and then we don't
                // prune anything until secureParams.SSLHandshakeTimeoutMax later, so that's the minimum
                // time we should wait. We add 50 ms to avoid instabilities when timing is a bit tight.
                var pruneWaitTime = SecureTestParameters.MinHandshakeTimeoutMS + SecureTestParameters.MaxHandshakeTimeoutMS + 50;

                RunPeriodicallyFor(pruneWaitTime, () => server.ScheduleUpdate().Complete());

                LogAssert.Expect(LogType.Error,
                    "Had to prune half-open connections (clients with unfinished TLS handshakes).");
            }
        }
    }
}

#endif
