using System;
using NUnit.Framework;
using Unity.Networking.Transport;
using UnityEngine;
using UnityEngine.TestTools;

namespace Unity.Networking.Transport.Tests
{
    public class NetworkConfigTests
    {
        [Test]
        public void NetworkConfig_MaxMessageSize_Below548_Warns()
        {
            var settings = new NetworkSettings();
            settings.WithNetworkConfigParameters(maxMessageSize: 547);

            LogAssert.Expect(LogType.Warning, "maxMessageSize value (547) is unnecessarily low. 548 should be safe in all circumstances.");
        }

        [Test]
        public void NetworkConfig_MaxMessageSize_InvalidValue_Throws()
        {
            var settings = new NetworkSettings();

            Assert.Throws<ArgumentException>(() => settings.WithNetworkConfigParameters(maxMessageSize: -42));
            LogAssert.Expect(LogType.Error, "maxMessageSize value (-42) must be greater than 0 and less than or equal to 1472");

            Assert.Throws<ArgumentException>(() => settings.WithNetworkConfigParameters(maxMessageSize: 0));
            LogAssert.Expect(LogType.Error, "maxMessageSize value (0) must be greater than 0 and less than or equal to 1472");

            Assert.Throws<ArgumentException>(() => settings.WithNetworkConfigParameters(maxMessageSize: NetworkParameterConstants.MaxPacketBufferSize + 1));
            LogAssert.Expect(LogType.Error, "maxMessageSize value (1473) must be greater than 0 and less than or equal to 1472");
        }

        [Test]
        public void NetworkConfig_MaxMessageSize_ValidValue_Succeeds()
        {
            var values = new int[] { 548, 1200, NetworkParameterConstants.MaxMessageSize, NetworkParameterConstants.MaxPacketBufferSize };
            foreach (var value in values)
            {
                var settings = new NetworkSettings();
                settings.WithNetworkConfigParameters(maxMessageSize: value);
            }
        }
    }
}