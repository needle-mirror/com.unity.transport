using Unity.Collections;

namespace Unity.Networking.Transport
{
    internal interface IUnderlyingConnectionList
    {
        /// <summary>
        /// Tries to open a new connection in the underlying layer.
        /// </summary>
        /// <param name="endpoint">The endpoint to connect to.</param>
        /// <param name="underlyingConnection">The connection id in the underlying layer. Default will create a new connection and will override this value.</param>
        /// <returns>Returns true if the connection is fully stablished.</returns>
        /// <remarks>
        /// Returning false means the connection was created but it has not been fully stablished yet,
        /// eg. there is a handshake pending to complete.
        /// </remarks>
        bool TryConnect(ref NetworkEndpoint endpoint, ref ConnectionId underlyingConnection);

        /// <summary>
        /// Disconnect a connection in the underlying layer.
        /// </summary>
        /// <param name="connectionId">The connection to disconnect.</param>
        void Disconnect(ref ConnectionId connectionId);

        /// <summary>
        /// Get the next disconnection from the underlying layer (if any).
        /// </summary>
        /// <param name="disconnection">The next incoming disconnection.</param>
        /// <returns>True if an incoming disconnection was found, false otherwise.</returns>
        public bool TryGetNextIncomingDisconnection(out ConnectionList.IncomingDisconnection disconnection);
    }

    internal struct NullUnderlyingConnectionList : IUnderlyingConnectionList
    {
        public bool TryConnect(ref NetworkEndpoint endpoint, ref ConnectionId underlyingConnection)
        {
            underlyingConnection = default;
            return true;
        }

        public void Disconnect(ref ConnectionId connectionId) {}

        public bool TryGetNextIncomingDisconnection(out ConnectionList.IncomingDisconnection disconnection)
        {
            disconnection = default;
            return false;
        }
    }

    internal struct UnderlyingConnectionList : IUnderlyingConnectionList
    {
        private ConnectionList Connections;

        public UnderlyingConnectionList(ref ConnectionList connections)
        {
            Connections = connections;
        }

        public bool TryConnect(ref NetworkEndpoint endpoint, ref ConnectionId underlyingConnection)
        {
            if (underlyingConnection != default)
            {
                if (Connections.GetConnectionState(underlyingConnection) == NetworkConnection.State.Connected)
                    return true;
            }
            else
            {
                underlyingConnection = Connections.StartConnecting(ref endpoint);
            }
            return false;
        }

        public void Disconnect(ref ConnectionId connectionId)
        {
            var state = Connections.GetConnectionState(connectionId);
            if (state != NetworkConnection.State.Disconnecting && state != NetworkConnection.State.Disconnected)
            {
                Connections.StartDisconnecting(ref connectionId);
            }
        }

        public bool TryGetNextIncomingDisconnection(out ConnectionList.IncomingDisconnection disconnection)
        {
            return Connections.TryGetNextIncomingDisconnection(out disconnection);
        }
    }
}
