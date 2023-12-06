using System;
using Unity.Collections;
using Unity.Networking.Transport.Logging;

namespace Unity.Networking.Transport
{
    /// <summary>Extensions for <see cref="WebSocketParameter"/>.</summary>
    public static class WebSocketParameterExtensions
    {
        /// <summary>
        /// Sets the <see cref="NetworkConfigParameter"/> in the settings.
        /// </summary>
        /// <param name="settings">Settings to modify.</param>
        /// <param name="path">
        /// For WebSocket clients, the path to which WebSocket connections will be established. For
        /// WebSocket servers, the path on which new connections will be accepted. This setting is
        /// only meant to specify the actual path of the URL, not the entire URL (so if you want to
        /// connect to "127.0.0.1/some/path", you would specify "/some/path" here). Defaults to "/".
        /// </param>
        /// <returns>Settings structure with modified values.</returns>
        public static ref NetworkSettings WithWebSocketParameters(
            ref this NetworkSettings settings,
            FixedString128Bytes path = default)
        {
            var parameter = new WebSocketParameter { Path = path == default ? "/" : path };
            settings.AddRawParameterStruct(ref parameter);
            return ref settings;
        }

        /// <summary>
        /// Gets the <see cref="WebSocketParameter"/> in the settings.
        /// </summary>
        /// <param name="settings">Settings to get parameters from.</param>
        /// <returns>Structure containing the WebSocket parameters.</returns>
        public static WebSocketParameter GetWebSocketParameters(ref this NetworkSettings settings)
        {
            if (!settings.TryGet<WebSocketParameter>(out var parameters))
            {
                parameters.Path = "/";
            }

            return parameters;
        }
    }

    /// <summary>Parameters for WebSocket connections.</summary>
    [Serializable]
    public struct WebSocketParameter : INetworkParameter
    {
        /// <summary>
        /// For WebSocket clients, the path to which WebSocket connections will be established. For
        /// WebSocket servers, the path on which new connections will be accepted. This setting is
        /// only meant to specify the actual path of the URL, not the entire URL (so if you want to
        /// connect to "127.0.0.1/some/path", you would specify "/some/path" here).
        /// </summary>
        /// <value>URL path starting with "/".</value>
        public FixedString128Bytes Path;

        /// <inheritdoc/>
        public bool Validate()
        {
            var valid = true;

            if (Path.Length == 0 || Path[0] != '/')
            {
                valid = false;
                DebugLog.ErrorWebSocketPathInvalid(Path);
            }

            return valid;
        }
    }
}