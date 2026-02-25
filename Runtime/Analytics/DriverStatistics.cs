namespace Unity.Networking.Transport.Analytics
{
    /// <summary>
    /// A structure that holds various statistics about a <see cref="NetworkDriver"/> and is
    /// obtained by calling <see cref="NetworkDriver.GetStatistics"/>. These statistics are global
    /// to the driver instance and account for the traffic of all its connections since the driver
    /// was created.
    /// </summary>
    /// <remarks>
    /// All traffic-related statistics are meant to represent network usage at the network layer and
    /// above (L3+), which is most representative of how bandwidth is typically measured by cloud
    /// providers. There are some caveats relating to how Unity Transport can infer packet sizes
    /// however. Refer to <see cref="PacketSizeStatistics"/> for details.
    /// </remarks>
    public struct DriverStatistics
    {
        /// <summary>Total number of bytes received by the driver.</summary>
        public ulong RxTotalBytes;

        /// <summary>Total number of bytes transmitted by the driver.</summary>
        public ulong TxTotalBytes;

        /// <summary>Total number of packets received by the driver.</summary>
        public ulong RxTotalPackets;

        /// <summary>Total number of packets transmitted by the driver.</summary>
        public ulong TxTotalPackets;

        /// <summary>Information about the size distribution of received packets.</summary>
        public PacketSizeStatistics RxPacketSizes;

        /// <summary>Information about the size distribution of transmitted packets.</summary>
        public PacketSizeStatistics TxPacketSizes;

        /// <summary>Information about the bandwidth usage of incoming traffic.</summary>
        public BandwidthStatistics RxBandwidth;

        /// <summary>Information about the bandwidth usage of outgoing traffic.</summary>
        public BandwidthStatistics TxBandwidth;

        /// <summary>
        /// Average usage (in number of packets) of the receive queue over the lifetime of the
        /// driver. See <see cref="NetworkConfigParameter.receiveQueueCapacity"/> for details.
        /// </summary>
        public float RxMeanQueueUsage;

        /// <summary>
        /// Average usage (in number of packets) of the send queue over the lifetime of the driver
        /// See <see cref="NetworkConfigParameter.sendQueueCapacity"/> for details.
        /// </summary>
        public float TxMeanQueueUsage;

        /// <summary>
        /// Maximum usage (in number of packets) of the receive queue over the lifetime of the
        /// driver. See <see cref="NetworkConfigParameter.receiveQueueCapacity"/> for details.
        /// </summary>
        public uint RxMaximumQueueUsage;

        /// <summary>
        /// Maximum usage (in number of packets) of the send queue over the lifetime of the driver
        /// See <see cref="NetworkConfigParameter.sendQueueCapacity"/> for details.
        /// </summary>
        public uint TxMaximumQueueUsage;
    }
}