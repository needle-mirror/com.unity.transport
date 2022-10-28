using Unity.Collections;
using Unity.Networking.Transport.Logging;

namespace Unity.Networking.Transport.Utilities
{
    public static class FragmentationStageParameterExtensions
    {
        public static ref NetworkSettings WithFragmentationStageParameters(
            ref this NetworkSettings settings,
            int payloadCapacity = FragmentationUtility.Parameters.k_DefaultPayloadCapacity
        )
        {
            var parameter = new FragmentationUtility.Parameters
            {
                PayloadCapacity = payloadCapacity,
            };

            settings.AddRawParameterStruct(ref parameter);

            return ref settings;
        }

        public static FragmentationUtility.Parameters GetFragmentationStageParameters(ref this NetworkSettings settings)
        {
            if (!settings.TryGet<FragmentationUtility.Parameters>(out var parameters))
            {
                parameters.PayloadCapacity = FragmentationUtility.Parameters.k_DefaultPayloadCapacity;
            }

            return parameters;
        }
    }

    public struct FragmentationUtility
    {
        public struct Parameters : INetworkParameter
        {
            internal const int k_DefaultPayloadCapacity = 4 * 1024;

            public int PayloadCapacity;

            public bool Validate()
            {
                var valid = true;

                if (PayloadCapacity <= NetworkParameterConstants.MTU)
                {
                    valid = false;
                    DebugLog.ErrorFragmentationSmallerPayloadThanMTU(PayloadCapacity, NetworkParameterConstants.MTU);
                }

                return valid;
            }
        }
    }
}
