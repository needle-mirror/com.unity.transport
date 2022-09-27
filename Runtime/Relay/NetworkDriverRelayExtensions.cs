using System;
using Unity.Networking.Transport;
using UnityEngine;

namespace Unity.Networking.Transport.Relay
{
    public static class NetworkDriverRelayExtensions
    {
        /// <summary>Get the current status of the connection to the relay server.</summary>
        public static RelayConnectionStatus GetRelayConnectionStatus(this NetworkDriver driver)
        {
            if (driver.NetworkProtocol is RelayNetworkProtocol)
            {
                return (RelayConnectionStatus)driver.ProtocolStatus;
            }
            else
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                throw new InvalidOperationException("Can't call GetRelayConnectionStatus when not using the Relay.");
#else
                Debug.LogError("Can't call GetRelayConnectionStatus when not using the Relay.");
                return RelayConnectionStatus.NotEstablished;
#endif
            }
        }

        /// <summary>Connect to the Relay server.</summary>
        /// <remarks>This method can only be used when using the Relay service.</remarks>
        /// <returns>The new <see cref="NetworkConnection"/> (or default if failed).</returns>
        public static NetworkConnection Connect(this NetworkDriver driver)
        {
            if (driver.NetworkProtocol is RelayNetworkProtocol)
            {
                // Fine to connect to the default endpoint. It's ignored anyway.
                return driver.Connect(default);
            }
            else
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                throw new InvalidOperationException("Can't call Connect without an endpoint when not using the Relay.");
#else
                Debug.LogError("Can't call Connect without an endpoint when not using the Relay.");
                return default;
#endif
            }
        }
    }
}