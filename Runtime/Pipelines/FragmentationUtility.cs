namespace Unity.Networking.Transport.Utilities
{
    public struct FragmentationUtility
    {
        /// <summary>Configuration parameters for <see cref="FragmentationPipelineStage">.</summary>
        public struct Parameters : INetworkParameter
        {
            /// <summary>Maximum payload size that can be fragmented.</summary>
            public int PayloadCapacity;
        }
    }
}
