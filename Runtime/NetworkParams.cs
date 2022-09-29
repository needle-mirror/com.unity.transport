using Unity.Collections;

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
        internal const int InitialEventQueueSize = 100;

        /// <summary>The default connection timeout value. This value can be overridden using the <see cref="NetworkConfigParameter"/></summary>
        public const int ConnectTimeoutMS = 1000;
        /// <summary>The default max connection attempts value. This value can be overridden using the <see cref="NetworkConfigParameter"/></summary>
        public const int MaxConnectAttempts = 60;
        /// <summary>The default disconnect timeout attempts value. This value can be overridden using the <see cref="NetworkConfigParameter"/></summary>
        public const int DisconnectTimeoutMS = 30 * 1000;
        /// <summary>The default inactivity timeout after which a heartbeat is sent. This value can be overridden using the <see cref="NetworkConfigParameter"/></summary>
        public const int HeartbeatTimeoutMS = 500;
        /// <summary>
        /// The default inactivity timeout after which re-establishing the connection is attempted.
        /// This value can be overridden using the <see cref="NetworkConfigParameter"/>.
        ///</summary>
        public const int ReconnectionTimeoutMS = 2000;
        /// <summary>
        /// The default capacity of the receive queue. This value can be overridden using the <see cref="NetworkConfigParameter"/>
        /// </summary>
        public const int ReceiveQueueCapacity = 64;
        /// <summary>
        /// The default capacity of the send queue. This value can be overridden using the <see cref="NetworkConfigParameter"/>
        /// </summary>
        public const int SendQueueCapacity = 64;
        /// <summary>
        /// Maximum size of a packet that can be sent by the transport.
        /// </summary>
        public const int MTU = 1400;
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
        /// <summary>A timeout in milliseconds after which reconnection is attempted if there is no activity.</summary>
        public int reconnectionTimeoutMS;
        /// <summary>The maximum amount of time a single frame can advance timeout values.</summary>
        /// <remarks>The main use for this parameter is to not get disconnects at frame spikes when both endpoints lives in the same process.</remarks>
        public int maxFrameTimeMS;
        /// <summary>A fixed amount of time to use for an interval between ScheduleUpdate. This is used instead of a clock.</summary>
        /// <remarks>The main use for this parameter is tests where determinism is more important than correctness.</remarks>
        public int fixedFrameTimeMS;
        /// <summary>The capacity of the receive queue.</summary>
        public int receiveQueueCapacity;
        /// <summary>The capacity of the send queue.</summary>
        public int sendQueueCapacity;

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
            if (reconnectionTimeoutMS < 0)
            {
                valid = false;
                UnityEngine.Debug.LogError($"{nameof(reconnectionTimeoutMS)} value ({reconnectionTimeoutMS}) must be greater or equal to 0");
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
            if (receiveQueueCapacity <= 0)
            {
                valid = false;
                UnityEngine.Debug.LogError($"{nameof(receiveQueueCapacity)} value ({receiveQueueCapacity}) must be greater than 0");
            }
            if (sendQueueCapacity <= 0)
            {
                valid = false;
                UnityEngine.Debug.LogError($"{nameof(sendQueueCapacity)} value ({sendQueueCapacity}) must be greater than 0");
            }

            return valid;
        }
    }

    public static class CommonNetworkParametersExtensions
    {
        public static ref NetworkSettings WithNetworkConfigParameters(
            ref this NetworkSettings settings,
            int connectTimeoutMS        = NetworkParameterConstants.ConnectTimeoutMS,
            int maxConnectAttempts      = NetworkParameterConstants.MaxConnectAttempts,
            int disconnectTimeoutMS     = NetworkParameterConstants.DisconnectTimeoutMS,
            int heartbeatTimeoutMS      = NetworkParameterConstants.HeartbeatTimeoutMS,
            int reconnectionTimeoutMS   = NetworkParameterConstants.ReconnectionTimeoutMS,
            int maxFrameTimeMS          = 0,
            int fixedFrameTimeMS        = 0,
            int receiveQueueCapacity    = NetworkParameterConstants.ReceiveQueueCapacity,
            int sendQueueCapacity       = NetworkParameterConstants.SendQueueCapacity
        )
        {
            var parameter = new NetworkConfigParameter
            {
                connectTimeoutMS = connectTimeoutMS,
                maxConnectAttempts = maxConnectAttempts,
                disconnectTimeoutMS = disconnectTimeoutMS,
                heartbeatTimeoutMS = heartbeatTimeoutMS,
                reconnectionTimeoutMS = reconnectionTimeoutMS,
                maxFrameTimeMS = maxFrameTimeMS,
                fixedFrameTimeMS = fixedFrameTimeMS,
                receiveQueueCapacity = receiveQueueCapacity,
                sendQueueCapacity = sendQueueCapacity,
            };

            settings.AddRawParameterStruct(ref parameter);

            return ref settings;
        }

        public static NetworkConfigParameter GetNetworkConfigParameters(ref this NetworkSettings settings)
        {
            if (!settings.TryGet<NetworkConfigParameter>(out var parameters))
            {
                parameters.connectTimeoutMS      = NetworkParameterConstants.ConnectTimeoutMS;
                parameters.maxConnectAttempts    = NetworkParameterConstants.MaxConnectAttempts;
                parameters.disconnectTimeoutMS   = NetworkParameterConstants.DisconnectTimeoutMS;
                parameters.heartbeatTimeoutMS    = NetworkParameterConstants.HeartbeatTimeoutMS;
                parameters.reconnectionTimeoutMS = NetworkParameterConstants.ReconnectionTimeoutMS;
                parameters.receiveQueueCapacity  = NetworkParameterConstants.ReceiveQueueCapacity;
                parameters.sendQueueCapacity     = NetworkParameterConstants.SendQueueCapacity;
                parameters.maxFrameTimeMS        = 0;
                parameters.fixedFrameTimeMS      = 0;
            }

            return parameters;
        }
    }
}
