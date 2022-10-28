using System;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Logging;
using UnityEngine;

namespace Unity.Networking.Transport.Relay
{
    public static class NetworkDriverRelayExtensions
    {
        /// <summary>Get the current status of the connection to the relay server.</summary>
        public static RelayConnectionStatus GetRelayConnectionStatus(this NetworkDriver driver)
        {
            if (driver.m_NetworkStack.TryGetLayer<RelayLayer>(out var layer))
            {
                return layer.ConnectionStatus;
            }
            else
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                throw new InvalidOperationException("Can't call GetRelayConnectionStatus when not using the Relay.");
#else
                DebugLog.LogError("Can't call GetRelayConnectionStatus when not using the Relay.");
                return RelayConnectionStatus.NotEstablished;
#endif
            }
        }

        /// <summary>Connect to the Relay server.</summary>
        /// <remarks>This method can only be used when using the Relay service.</remarks>
        /// <returns>The new <see cref="NetworkConnection"/> (or default if failed).</returns>
        public static NetworkConnection Connect(this NetworkDriver driver)
        {
            if (driver.CurrentSettings.TryGet<RelayNetworkParameter>(out var parameters))
            {
                return driver.Connect(parameters.ServerData.Endpoint);
            }
            else
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                throw new InvalidOperationException("Can't call Connect without an endpoint when not using the Relay.");
#else
                DebugLog.LogError("Can't call Connect without an endpoint when not using the Relay.");
                return default;
#endif
            }
        }
    }
}
