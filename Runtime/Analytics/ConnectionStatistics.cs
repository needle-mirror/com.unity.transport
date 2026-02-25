using Unity.Networking.Transport.Utilities;

namespace Unity.Networking.Transport.Analytics
{
    /// <summary>
    /// A structure that holds various statistics about a <see cref="NetworkConnection"/> and is
    /// obtained by calling <see cref="NetworkDriver.GetConnectionStatistics"/>. These statistics
    /// are particularly useful to assess the quality of a connection to a specific peer.
    /// </summary>
    public struct ConnectionStatistics
    {
        /// <summary>
        /// Statistics about the latency experienced on a connection. All statistics are measured in
        /// milliseconds and reflect the round-trip time (RTT, commonly called "ping") on the link.
        /// </summary>
        /// <remarks>
        /// Currently these statistics are drawn entirely from reliable pipelines (pipelines with a
        /// <see cref="ReliableSequencedPipelineStage"/>) and their quality depends on the amount of
        /// traffic sent over those.
        /// </remarks>
        public struct LatencyStatistics
        {
            /// <summary>Average latency over the lifetime of the connection.</summary>
            /// <value>RTT in milliseconds.</value>
            public float Mean;

            /// <summary>Standard deviation around the mean latency.</summary>
            /// <value>Latency deviation in milliseconds.</value>
            public float StandardDeviation;

            /// <summary>Minimum latency seen over the lifetime of the connection.</summary>
            /// <value>RTT in milliseconds.</value>
            public uint Minimum;

            /// <summary>Maximum latency seen over the lifetime of the connection.</summary>
            /// <value>RTT in milliseconds.</value>
            public uint Maximum;

            /// <summary>Latest latency measurement taken on the connection.</summary>
            /// <value>RTT in milliseconds.</value>
            public uint Current;

            /// <summary>Weighted average of the last few latency measurements.</summary>
            /// <value>RTT in milliseconds.</value>
            public float SmoothedCurrent;
        }

        /// <summary>Latency statistics for the connection.</summary>
        public LatencyStatistics Latency;

        /// <summary>
        /// Cumulative counters for all pipelines configured as reliable (e.g. using the
        /// <see cref="ReliableSequencedPipelineStage"/>) for this connection.
        /// </summary>
        public ReliableUtility.Statistics Reliable;

        /// <summary>Percentage of packets that were lost on this connection.</summary>
        public float PacketLossPercent
        {
            get
            {
                // We actually have two ways of counting lost packets. On the receive side we have
                // the dropped counter, and on the send side we can infer packet loss by looking at
                // how many packets we needed to resend (that's not entirely accurate because a
                // resend could be due to delays in delivery and not loss, but it's a good proxy).
                var totalPackets = Reliable.PacketsReceived + Reliable.PacketsSent;
                var lostPackets = Reliable.PacketsDropped + Reliable.PacketsResent;
                return totalPackets > 0 ? lostPackets * 100.0f / totalPackets : 0.0f;
            }
        }

        /// <summary>Percentage of packets that were duplicated on this connection.</summary>
        public float PacketDuplicationPercent => Reliable.PacketsReceived > 0
            ? Reliable.PacketsDuplicated * 100.0f / Reliable.PacketsReceived
            : 0.0f;

        /// <summary>Percentage of packets received out of order on this connection.</summary>
        public float PacketOutOfOrderPercent => Reliable.PacketsReceived > 0
            ? Reliable.PacketsOutOfOrder * 100.0f / Reliable.PacketsReceived
            : 0.0f;
    }
}