namespace Unity.Networking.Transport.Utilities
{
    public static class FragmentationStageParameterExtensions
    {
        /// <summary>
        /// Sets the <see cref="FragmentationUtility.Parameters"/> values for the <see cref="NetworkSettings"/>
        /// </summary>
        /// <param name="payloadCapacity"><seealso cref="FragmentationUtility.Parameters.PayloadCapacity"/></param>
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

        /// <summary>
        /// Gets the <see cref="FragmentationUtility.Parameters"/>
        /// </summary>
        /// <returns>Returns the <see cref="FragmentationUtility.Parameters"/> values for the <see cref="NetworkSettings"/></returns>
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
        /// <summary>Configuration parameters for <see cref="FragmentationPipelineStage">.</summary>
        public struct Parameters : INetworkParameter
        {
            internal const int k_DefaultPayloadCapacity = 4 * 1024;

            /// <summary>Maximum payload size that can be fragmented.</summary>
            public int PayloadCapacity;

            public bool Validate()
            {
                var valid = true;

                if (PayloadCapacity <= NetworkParameterConstants.MTU)
                {
                    valid = false;
                    UnityEngine.Debug.LogError($"{nameof(PayloadCapacity)} value ({PayloadCapacity}) must be greater than MTU ({NetworkParameterConstants.MTU})");
                }

                return valid;
            }
        }
    }
}
