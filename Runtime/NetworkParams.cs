using System;

namespace Unity.Networking.Transport
{
    /// <summary>
    /// Interface used to implement custom values for use with <see cref="NetworkSettings"/>. This
    /// is only useful to provide configuration settings for custom <see cref="INetworkInterface"/>
    /// or <see cref="INetworkPipelineStage"/> implementations.
    /// </summary>
    public interface INetworkParameter
    {
        /// <summary>
        /// Checks if the values for all fields are valid. This method will be automatically called
        /// when adding parameters to the <see cref="NetworkSettings"/>.
        /// </summary>
        /// <returns>Returns true if the parameter is valid.</returns>
        bool Validate();
    }

    /// <summary>
    /// Default values used for the different network parameters. These parameters (except the MTU)
    /// can be set with <see cref="CommonNetworkParametersExtensions.WithNetworkConfigParameters"/>.
    /// </summary>
    public struct NetworkParameterConstants
    {
        /// <summary>The default initial size of the event queue.</summary>
        public const int InitialEventQueueSize = 100;

        /// <summary>Unused.</summary>
        public const int InvalidConnectionId = -1;

        /// <summary>Default size of the internal buffer for received payloads.</summary>
        public const int DriverDataStreamSize = 64 * 1024;

        /// <summary>Default time between connection attempts.</summary>
        /// <value>Timeout in milliseconds.</value>
        public const int ConnectTimeoutMS = 1000;

        /// <summary>
        /// Default maximum number of connection attempts to try. If no answer is received from the
        /// server after this number of attempts, a <see cref="NetworkEvent.Type.Disconnect"/> event
        /// is generated for the connection.
        /// </summary>
        /// <value>Number of attempts.</value>
        public const int MaxConnectAttempts = 60;

        /// <summary>
        /// Default inactivity timeout for a connection. If nothing is received on a connection for
        /// this amount of time, it is disconnected (a <see cref="NetworkEvent.Type.Disconnect"/>
        /// event will be generated).
        /// </summary>
        /// <value>Timeout in milliseconds.</value>
        public const int DisconnectTimeoutMS = 30 * 1000;

        /// <summary>
        /// Default amount of time after which if nothing from a peer is received, a heartbeat
        /// message will be sent to keep the connection alive.
        /// </summary>
        /// <value>Timeout in milliseconds.</value>
        public const int HeartbeatTimeoutMS = 500;

        /// <summary>
        /// Default maximum size of a packet that can be sent by the transport. Note that this size
        /// includes any headers that could be added by the transport (e.g. headers for DTLS or
        /// pipelines), which means the actual maximum message size that can be sent by a user is
        /// slightly less than this value. To find out what the size of these headers is, use
        /// <see cref="NetworkDriver.MaxHeaderSize"/>. It is possible to send messages larger than
        /// that by sending them through a pipeline with a <see cref="FragmentationPipelineStage"/>.
        /// These headers do not include those added by the OS network stack (like UDP or IP).
        /// </summary>
        // <value>Size in bytes.</value>
        public const int MaxMessageSize = 1400;

        /// <summary>Same as <see cref="MaxMessageSize"/>.</summary>
        /// <value>Size in bytes.</value>
        public const int MTU = 1400;

        internal const int MaxPacketBufferSize = 1500 - 20 - 8; // Ethernet MTU minus IPv4 and UDP headers.
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

        /// <summary>Validate the settings.</summary>
        /// <returns>True if the settings are valid, false otherwise.</returns>
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
        /// <summary>Time between connection attempts.</summary>
        /// <value>Timeout in milliseconds.</value>
        public int connectTimeoutMS;

        /// <summary>
        /// Maximum number of connection attempts to try. If no answer is received from the server
        /// after this number of attempts, a <see cref="NetworkEvent.Type.Disconnect"/> event is
        /// generated for the connection.
        /// </summary>
        /// <value>Number of attempts.</value>
        public int maxConnectAttempts;

        /// <summary>
        /// Inactivity timeout for a connection. If nothing is received on a connection for this
        /// amount of time, it is disconnected (a <see cref="NetworkEvent.Type.Disconnect"/> event
        /// will be generated). To prevent this from happenning when the game session is simply
        /// quiet, set <see cref="heartbeatTimeoutMS"/> to a positive non-zero value.
        /// </summary>
        /// <value>Timeout in milliseconds.</value>
        public int disconnectTimeoutMS;

        /// <summary>
        /// Time after which if nothing from a peer is received, a heartbeat message will be sent
        /// to keep the connection alive. Prevents the <see cref="disconnectTimeoutMS"/> mechanism
        /// from kicking when nothing happens on a connection. A value of 0 will disable heartbeats.
        /// </summary>
        /// <value>Timeout in milliseconds.</value>
        public int heartbeatTimeoutMS;

        /// <summary>
        /// Maximum amount of time a single frame can advance timeout values. In this scenario, a
        /// frame is defined as the time between two <see cref="NetworkDriver.ScheduleUpdate"/>
        /// calls. Useful when debugging to avoid connection timeouts.
        /// </summary>
        /// <value>Timeout in milliseconds.</value>
        public int maxFrameTimeMS;

        /// <summary>
        /// Fixes a fixed amount of time to be used each frame for the purpose of timeout
        /// calculations. For example, setting this value to 1000 will have the driver consider
        /// that 1 second passed between each call to <see cref="NetworkDriver.ScheduleUpdate"/>,
        /// even if that's not actually the case. Only useful for testing scenarios, where
        /// deterministic behavior is more important than correctness.
        /// </summary>
        /// <value>Timeout in milliseconds.</value>
        public int fixedFrameTimeMS;

        /// <summary>
        /// Maximum size of a packet that can be sent by the transport. Note that this size includes
        /// any headers that could be added by the transport (e.g. headers for DTLS or pipelines),
        /// which means the actual maximum message size that can be sent by a user is slightly less
        /// than this value. To find out what the size of these headers is, use
        /// <see cref="NetworkDriver.MaxHeaderSize"/>. It is possible to send messages larger than
        /// that by sending them through a pipeline with a <see cref="FragmentationPipelineStage"/>.
        /// These headers do not include those added by the OS network stack (like UDP or IP).
        /// </summary>
        // <value>Size in bytes.</value>
        public int maxMessageSize;

        /// <summary>Validate the settings.</summary>
        /// <returns>True if the settings are valid, false otherwise.</returns>
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
            if (maxMessageSize <= 0 || maxMessageSize > NetworkParameterConstants.MaxPacketBufferSize)
            {
                valid = false;
                UnityEngine.Debug.LogError($"{nameof(maxMessageSize)} value ({maxMessageSize}) must be greater than 0 and less than or equal to {NetworkParameterConstants.MaxPacketBufferSize}");
            }

            // Warn if the value is set ridiculously low. Packets 576 bytes long or less are always
            // safe with IPv4 and should be handled correctly by any networking equipment so there's
            // no point in setting a maximum message size that's lower than this. (We test against
            // 548 instead of 576 to account for IP and UDP headers).
            if (valid && maxMessageSize < 548)
            {
                UnityEngine.Debug.LogWarning($"{nameof(maxMessageSize)} value ({maxMessageSize}) is unnecessarily low. 548 should be safe in all circumstances.");
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
        /// <param name="settings"><see cref="NetworkSettings"/> to get parameters from.</param>
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
        /// Sets the <see cref="NetworkConfigParameter"/> in the settings.
        /// </summary>
        /// <param name="settings">Settings to modify.</param>
        /// <param name="connectTimeoutMS">Time between connection attempts.</param>
        /// <param name="maxConnectAttempts">
        /// Maximum number of connection attempts to try. If no answer is received from the server
        /// after this number of attempts, a <see cref="NetworkEvent.Type.Disconnect"/> event is
        /// generated for the connection.
        /// </param>
        /// <param name="disconnectTimeoutMS">
        /// Inactivity timeout for a connection. If nothing is received on a connection for this
        /// amount of time, it is disconnected (a <see cref="NetworkEvent.Type.Disconnect"/> event
        /// will be generated). To prevent this from happenning when the game session is simply
        /// quiet, set <c>heartbeatTimeoutMS</c> to a positive non-zero value.
        /// </param>
        /// <param name="heartbeatTimeoutMS">
        /// Time after which if nothing from a peer is received, a heartbeat message will be sent
        /// to keep the connection alive. Prevents the <c>disconnectTimeoutMS</c> mechanism from
        /// kicking when nothing happens on a connection. A value of 0 will disable heartbeats.
        /// </param>
        /// <param name="maxFrameTimeMS">
        /// Maximum amount of time a single frame can advance timeout values. In this scenario, a
        /// frame is defined as the time between two <see cref="NetworkDriver.ScheduleUpdate"/>
        /// calls. Useful when debugging to avoid connection timeouts.
        /// </param>
        /// <param name="fixedFrameTimeMS">
        /// Fixes a fixed amount of time to be used each frame for the purpose of timeout
        /// calculations. For example, setting this value to 1000 will have the driver consider
        /// that 1 second passed between each call to <see cref="NetworkDriver.ScheduleUpdate"/>,
        /// even if that's not actually the case. Only useful for testing scenarios, where
        /// deterministic behavior is more important than correctness.
        /// </param>
        /// <param name="maxMessageSize">
        /// Maximum size of a packet that can be sent by the transport. Note that this size includes
        /// any headers that could be added by the transport (e.g. headers for DTLS or pipelines),
        /// which means the actual maximum message size that can be sent by a user is slightly less
        /// than this value.
        /// <param>
        /// <returns>Settings structure with modified values.</returns>
        public static ref NetworkSettings WithNetworkConfigParameters(
            ref this NetworkSettings settings,
            int connectTimeoutMS        = NetworkParameterConstants.ConnectTimeoutMS,
            int maxConnectAttempts      = NetworkParameterConstants.MaxConnectAttempts,
            int disconnectTimeoutMS     = NetworkParameterConstants.DisconnectTimeoutMS,
            int heartbeatTimeoutMS      = NetworkParameterConstants.HeartbeatTimeoutMS,
            int maxFrameTimeMS          = 0,
            int fixedFrameTimeMS        = 0,
            int maxMessageSize          = NetworkParameterConstants.MaxMessageSize
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
                maxMessageSize = maxMessageSize,
            };

            settings.AddRawParameterStruct(ref parameter);

            return ref settings;
        }

        // Needed to preserve compatibility with the version without maxMessageSize...
        /// <inheritdoc cref="WithNetworkConfigParameters(ref NetworkSettings, int, int, int, int, int, int, int)"/> 
        public static ref NetworkSettings WithNetworkConfigParameters(
            ref this NetworkSettings settings,
            int connectTimeoutMS,
            int maxConnectAttempts,
            int disconnectTimeoutMS,
            int heartbeatTimeoutMS,
            int maxFrameTimeMS,
            int fixedFrameTimeMS
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
                maxMessageSize = NetworkParameterConstants.MaxMessageSize,
            };

            settings.AddRawParameterStruct(ref parameter);

            return ref settings;
        }

        /// <summary>
        /// Gets the <see cref="NetworkConfigParameter"/> in the settings.
        /// </summary>
        /// <param name="settings">Settings to get parameters from.</param>
        /// <returns>Structure containing the network parameters.</returns>
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
                parameters.maxMessageSize       = NetworkParameterConstants.MaxMessageSize;
            }

            return parameters;
        }
    }
}
