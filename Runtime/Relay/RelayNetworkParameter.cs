using System;
using Unity.Collections.LowLevel.Unsafe;

namespace Unity.Networking.Transport.Relay
{
    public static class RelayParameterExtensions
    {
        public static ref NetworkSettings WithRelayParameters(
            ref this NetworkSettings settings,
            ref RelayServerData serverData,
            int relayConnectionTimeMS = RelayNetworkParameter.k_DefaultConnectionTimeMS
        )
        {
            var parameter = new RelayNetworkParameter
            {
                ServerData = serverData,
                RelayConnectionTimeMS = relayConnectionTimeMS,
            };

            settings.AddRawParameterStruct(ref parameter);

            return ref settings;
        }

        public static RelayNetworkParameter GetRelayParameters(ref this NetworkSettings settings)
        {
            if (!settings.TryGet<RelayNetworkParameter>(out var parameters))
            {
                throw new System.InvalidOperationException($"Can't extract Relay parameters: {nameof(RelayNetworkParameter)} must be provided to the {nameof(NetworkSettings)}");
            }

            return parameters;
        }
    }

    public struct RelayNetworkParameter : INetworkParameter
    {
        internal const int k_DefaultConnectionTimeMS = 3000;

        public RelayServerData ServerData;
        public int RelayConnectionTimeMS;

        public unsafe bool Validate()
        {
            var valid = true;

            if (ServerData.Endpoint == default)
            {
                valid = false;
                UnityEngine.Debug.LogError($"{nameof(ServerData.Endpoint)} value ({ServerData.Endpoint}) must be a valid value");
            }
            if (ServerData.AllocationId == default)
            {
                valid = false;
                UnityEngine.Debug.LogError($"{nameof(ServerData.AllocationId)} value ({ServerData.AllocationId}) must be a valid value");
            }
            if (RelayConnectionTimeMS < 0)
            {
                valid = false;
                UnityEngine.Debug.LogError($"{nameof(RelayConnectionTimeMS)} value({RelayConnectionTimeMS}) must be greater or equal to 0");
            }

            return valid;
        }
    }
}
