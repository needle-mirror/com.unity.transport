using Unity.Collections;
using Unity.Networking.Transport.Logging;

namespace Unity.Networking.Transport
{
    public struct NetworkDriverIdentifierParameter : INetworkParameter
    {
        public FixedString32Bytes Label;

        public bool Validate()
        {
            var valid = true;

            if (Label.IsEmpty)
            {
                valid = false;
                DebugLog.LogError($"The NetworkDriver identifier must be not empty");
            }

            return valid;
        }
    }

    public static class NetworkDriverIdentifierParameterExtensions
    {
        public static ref NetworkSettings WithDriverIdentifierParameters(ref this NetworkSettings settings, FixedString32Bytes label)
        {
            var parameter = new NetworkDriverIdentifierParameter
            {
                Label = label,
            };

            settings.AddRawParameterStruct(ref parameter);

            return ref settings;
        }
    }
}
