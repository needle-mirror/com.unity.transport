using Unity.Mathematics;

namespace Unity.Networking.Transport.Analytics
{
    /// <summary>
    /// A structure to hold statistics and counters about packet sizes. Usually this will be
    /// returned through <see cref="NetworkDriver.GetStatistics"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sizes tracked in this structure include headers for IP and UDP/TCP but do not include any
    /// link-layer headers (Ethernet, Wi-Fi, etc.) as those are too dependent on the deployment
    /// environment. Similarly, the sizes do not account for any padding that could be added to
    /// meet minimum frame size requirements (e.g. 64 bytes for Ethernet).
    /// </para>
    /// <para>
    /// For WebSocket connections, the TCP header is assumed to be 32 bytes long. Furthermore even
    /// though Nagle's algorithm is disabled in Unity Transport (for non-web platforms), the nature
    /// of TCP means that the packet sizes reported here may not correspond one-to-one with sizes
    /// observed on the wire since the OS may opt to segment the traffic differently. Nevertheless,
    /// they should represent a good approximation of the shape of the traffic.
    /// </para>
    /// <para>
    /// Note that if using a custom <see cref="INetworkInterface"/>, no assumptions are made about
    /// the size of headers that could be added by the OS and the sizes reported here will be
    /// exactly what is being passed to/from the network interface.
    /// </para>
    /// </remarks>
    public unsafe struct PacketSizeStatistics
    {
        private RunningStatistics m_RunningStats;

        // Index 0: smaller than 128 bytes.
        // Index 1: between 128 and 255 bytes.
        // Index 2: between 256 and 511 bytes.
        // Index 3: between 512 and 1023 bytes.
        // Index 4: larger than 1023 bytes.
        private fixed uint m_PacketsBySize[5];

        /// <summary>Average packet size over all packets recorded.</summary>
        public float Mean => m_RunningStats.Mean;

        /// <summary>Standard deviation of packet sizes over all packets recorded.</summary>
        public float StandardDeviation =>m_RunningStats.StandardDeviation;

        /// <summary>Smallest packet size recorded.</summary>
        public uint Minimum => m_RunningStats.Minimum;

        /// <summary>Largest packet size recorded.</summary>
        public uint Maximum => m_RunningStats.Maximum;

        /// <summary>Number of packets recorded that were smaller than 128 bytes.</summary>
        public uint SmallerThan128Bytes => m_PacketsBySize[0];

        /// <summary>Number of packets recorded that were between 128 and 255 bytes.</summary>
        public uint Between128And255Bytes => m_PacketsBySize[1];

        /// <summary>Number of packets recorded that were between 256 and 511 bytes.</summary>
        public uint Between256And511Bytes => m_PacketsBySize[2];

        /// <summary>Number of packets recorded that were between 512 and 1023 bytes.</summary>
        public uint Between512And1023Bytes => m_PacketsBySize[3];

        /// <summary>Number of packets recorded that were larger than 1023 bytes.</summary>
        public uint LargerThan1023Bytes => m_PacketsBySize[4];

        internal void AddPacket(uint size)
        {
            m_RunningStats.AddDataPoint(size);
            m_PacketsBySize[math.min(4, 32 - math.lzcnt(size >> 7))]++;
        }
    }
}