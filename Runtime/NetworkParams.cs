namespace Unity.Networking.Transport
{
    /// <summary>
    /// The interface for NetworkParameters
    /// </summary>
    public interface INetworkParameter
    {
    }

    /// <summary>
    /// Default NetworkParameter Constants.
    /// </summary>
    public struct NetworkParameterConstants
    {
        /// <summary>The default size of the event queue.</summary>
        public const int InitialEventQueueSize = 100;
        
        /// <summary>
        /// The invalid connection id
        /// </summary>
        public const int InvalidConnectionId = -1;

        /// <summary>
        /// The default size of the DataStreamWriter. This value can be overridden using the <see cref="NetworkConfigParameter"/>.
        /// </summary>
        public const int DriverDataStreamSize = 64 * 1024;
        /// <summary>The default connection timeout value. This value can be overridden using the <see cref="NetworkConfigParameter"/></summary>
        public const int ConnectTimeoutMS = 1000;
        /// <summary>The default max connection attempts value. This value can be overridden using the <see cref="NetworkConfigParameter"/></summary>
        public const int MaxConnectAttempts = 60;
        /// <summary>The default disconnect timeout attempts value. This value can be overridden using the <see cref="NetworkConfigParameter"/></summary>
        public const int DisconnectTimeoutMS = 30 * 1000;
        /// <summary>The default inactivity timeout after which a heartbeat is sent. This This value can be overridden using the <see cref="NetworkConfigParameter"/></summary>
        public const int HeartbeatTimeoutMS = 500;

        /// <summary>
        /// The max size of any packet that can be sent
        /// </summary>
#if (UNITY_GAMECORE || UNITY_XBOXONE)
        // Microsoft recommends all Xbox games should be made to work with a MTU of 1384.
        public const int MTU = 1384;
#else
        public const int MTU = 1400;
#endif
    }

    /// <summary>
    /// The NetworkDataStreamParameter is used to set a fixed data stream size.
    /// </summary>
    /// <remarks>The <see cref="DataStreamWriter"/> will grow on demand if the size is set to zero. </remarks>
    public struct NetworkDataStreamParameter : INetworkParameter
    {
        /// <summary>Size of the default <see cref="DataStreamWriter"/></summary>
        public int size;
    }

    /// <summary>
    /// The NetworkConfigParameter is used to set specific parameters that the driver uses.
    /// </summary>
    public struct NetworkConfigParameter : INetworkParameter
    {
        /// <summary>A timeout in milliseconds indicating how long we will wait until we send a new connection attempt.</summary>
        public int connectTimeoutMS;
        /// <summary>The maximum amount of connection attempts we will try before disconnecting.</summary>
        public int maxConnectAttempts;
        /// <summary>A timeout in milliseconds indicating how long we will wait for a connection event, before we disconnect it.</summary>
        /// <remarks>
        /// The connection needs to receive data from the connected endpoint within this timeout.
        /// Note that with heartbeats enabled (<see cref="heartbeatTimeoutMS"/> > 0), simply not
        /// sending any data will not be enough to trigger this timeout (since heartbeats count as
        /// connection events).
        /// </remarks>
        public int disconnectTimeoutMS;
        /// <summary>A timeout in milliseconds after which a heartbeat is sent if there is no activity.</summary>
        public int heartbeatTimeoutMS;
        /// <summary>The maximum amount of time a single frame can advance timeout values.</summary>
        /// <remarks>The main use for this parameter is to not get disconnects at frame spikes when both endpoints lives in the same process.</remarks>
        public int maxFrameTimeMS;
        /// <summary>A fixed amount of time to use for an interval between ScheduleUpdate. This is used instead of a clock.</summary>
        /// <remarks>The main use for this parameter is tests where determinism is more important than correctness.</remarks>
        public int fixedFrameTimeMS;
    }
}
