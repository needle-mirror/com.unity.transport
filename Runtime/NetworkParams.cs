using System;

namespace Unity.Networking.Transport
{
    /// <summary>
    /// The interface for NetworkParameters
    /// </summary>
    public interface INetworkParameter
    {
        /// <summary>
        /// Checks if the values for all fields are valid.
        /// This method will be automatically called when adding a paramter to the NetworkSettings.
        /// </summary>
        /// <returns>Returns true if the parameter is valid.</returns>
        bool Validate();
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
        public const int MTU = 1400;
    }

    /// <summary>
    /// The NetworkDataStreamParameter is used to set a fixed data stream size.
    /// </summary>
    /// <remarks>The <see cref="DataStreamWriter"/> will grow on demand if the size is set to zero. </remarks>
    public struct NetworkDataStreamParameter : INetworkParameter
    {
        internal const int k_DefaultSize = 0;

        /// <summary>Size of the default <see cref="DataStreamWriter"/></summary>
        public int size;

        public bool Validate()
        {
            var valid = true;

            if (size < 0)
            {
                valid = false;
                UnityEngine.Debug.LogError($"{nameof(size)} value ({size}) must be greater or equal to 0");
            }

            return valid;
        }
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

        public bool Validate()
        {
            var valid = true;

            if (connectTimeoutMS < 0)
            {
                valid = false;
                UnityEngine.Debug.LogError($"{nameof(connectTimeoutMS)} value ({connectTimeoutMS}) must be greater or equal to 0");
            }
            if (maxConnectAttempts < 0)
            {
                valid = false;
                UnityEngine.Debug.LogError($"{nameof(maxConnectAttempts)} value ({maxConnectAttempts}) must be greater or equal to 0");
            }
            if (disconnectTimeoutMS < 0)
            {
                valid = false;
                UnityEngine.Debug.LogError($"{nameof(disconnectTimeoutMS)} value ({disconnectTimeoutMS}) must be greater or equal to 0");
            }
            if (heartbeatTimeoutMS < 0)
            {
                valid = false;
                UnityEngine.Debug.LogError($"{nameof(heartbeatTimeoutMS)} value ({heartbeatTimeoutMS}) must be greater or equal to 0");
            }
            if (maxFrameTimeMS < 0)
            {
                valid = false;
                UnityEngine.Debug.LogError($"{nameof(maxFrameTimeMS)} value ({maxFrameTimeMS}) must be greater or equal to 0");
            }
            if (fixedFrameTimeMS < 0)
            {
                valid = false;
                UnityEngine.Debug.LogError($"{nameof(fixedFrameTimeMS)} value ({fixedFrameTimeMS}) must be greater or equal to 0");
            }

            return valid;
        }
    }

    public static class CommonNetworkParametersExtensions
    {
        /// <summary>
        /// Sets the <see cref="NetworkDataStreamParameter"/> values for the <see cref="NetworkSettings"/>
        /// </summary>
        /// <param name="size"><seealso cref="NetworkDataStreamParameter.size"/></param>
        [Obsolete("In Unity Transport 2.0, the data stream size will always be dynamically-sized and this API will be removed.")]
        public static ref NetworkSettings WithDataStreamParameters(ref this NetworkSettings settings, int size = NetworkDataStreamParameter.k_DefaultSize)
        {
            var parameter = new NetworkDataStreamParameter
            {
                size = size,
            };

            settings.AddRawParameterStruct(ref parameter);

            return ref settings;
        }

        /// <summary>
        /// Gets the <see cref="NetworkDataStreamParameter"/>
        /// </summary>
        /// <returns>Returns the <see cref="NetworkDataStreamParameter"/> values for the <see cref="NetworkSettings"/></returns>
        public static NetworkDataStreamParameter GetDataStreamParameters(ref this NetworkSettings settings)
        {
            if (!settings.TryGet<NetworkDataStreamParameter>(out var parameters))
            {
                parameters.size = NetworkDataStreamParameter.k_DefaultSize;
            }

            return parameters;
        }

        /// <summary>
        /// Sets the <see cref="NetworkConfigParameter"/> values for the <see cref="NetworkSettings"/>
        /// </summary>
        /// <param name="connectTimeoutMS"><seealso cref="NetworkConfigParameter.connectTimeoutMS"/></param>
        /// <param name="maxConnectAttempts"><seealso cref="NetworkConfigParameter.maxConnectAttempts"/></param>
        /// <param name="disconnectTimeoutMS"><seealso cref="NetworkConfigParameter.disconnectTimeoutMS"/></param>
        /// <param name="heartbeatTimeoutMS"><seealso cref="NetworkConfigParameter.heartbeatTimeoutMS"/></param>
        /// <param name="maxFrameTimeMS"><seealso cref="NetworkConfigParameter.maxFrameTimeMS"/></param>
        /// <param name="fixedFrameTimeMS"><seealso cref="NetworkConfigParameter.fixedFrameTimeMS"/></param>
        /// <returns></returns>
        public static ref NetworkSettings WithNetworkConfigParameters(
            ref this NetworkSettings settings,
            int connectTimeoutMS        = NetworkParameterConstants.ConnectTimeoutMS,
            int maxConnectAttempts      = NetworkParameterConstants.MaxConnectAttempts,
            int disconnectTimeoutMS     = NetworkParameterConstants.DisconnectTimeoutMS,
            int heartbeatTimeoutMS      = NetworkParameterConstants.HeartbeatTimeoutMS,
            int maxFrameTimeMS          = 0,
            int fixedFrameTimeMS        = 0
        )
        {
            var parameter = new NetworkConfigParameter
            {
                connectTimeoutMS = connectTimeoutMS,
                maxConnectAttempts = maxConnectAttempts,
                disconnectTimeoutMS = disconnectTimeoutMS,
                heartbeatTimeoutMS = heartbeatTimeoutMS,
                maxFrameTimeMS = maxFrameTimeMS,
                fixedFrameTimeMS = fixedFrameTimeMS,
            };

            settings.AddRawParameterStruct(ref parameter);

            return ref settings;
        }

        /// <summary>
        /// Gets the <see cref="NetworkConfigParameter"/>
        /// </summary>
        /// <returns>Returns the <see cref="NetworkConfigParameter"/> values for the <see cref="NetworkSettings"/></returns>
        public static NetworkConfigParameter GetNetworkConfigParameters(ref this NetworkSettings settings)
        {
            if (!settings.TryGet<NetworkConfigParameter>(out var parameters))
            {
                parameters.connectTimeoutMS     = NetworkParameterConstants.ConnectTimeoutMS;
                parameters.maxConnectAttempts   = NetworkParameterConstants.MaxConnectAttempts;
                parameters.disconnectTimeoutMS  = NetworkParameterConstants.DisconnectTimeoutMS;
                parameters.heartbeatTimeoutMS   = NetworkParameterConstants.HeartbeatTimeoutMS;
                parameters.maxFrameTimeMS       = 0;
                parameters.fixedFrameTimeMS     = 0;
            }

            return parameters;
        }
    }
}
