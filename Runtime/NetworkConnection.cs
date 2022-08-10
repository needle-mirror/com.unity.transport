using System;
using Unity.Collections;

namespace Unity.Networking.Transport
{
    namespace Error
    {
        /// <summary>Reason for a disconnection event.</summary>
        /// <remarks>
        /// One of these values may be present as a single byte in the <see cref="DataStreamReader">
        /// obtained with <see cref="NetworkDriver.PopEvent"> if the event type is
        /// <see cref="NetworkEvent.Type.Disconnect">.
        /// </remarks>
        public enum DisconnectReason : byte
        {
            // Don't assign explicit values.

            /// <summary>Used internally when no other reason fits.</summary>
            /// <remarks>
            /// This shouldn't normally appear as a result of calling <see cref="NetworkDriver.PopEvent">.
            /// </remarks>
            Default,

            /// <summary>
            /// Indicates the connection timed out (see <see cref="NetworkConfigParameter.disconnectTimeoutMS">).
            /// </summary>
            Timeout,

            /// <summary>
            /// Indicates the connection failed to establish after too many failed attempts
            /// (see <see cref="NetworkConfigParameter.maxConnectAttempts"/>).
            /// </summary>
            MaxConnectionAttempts,

            /// <summary>
            /// Indicates the connection was closed normally by the remote peer after calling
            /// <see cref="NetworkDriver.Disconnect"> or <see cref="NetworkConnection.close">.
            /// </summary>
            ClosedByRemote,

            /// <summary>Used internally to track the number of reasons. Keep last.</summary>
            Count
        }

        /// <summary>Status code returned by many functions in the API.</summary>
        /// <remarks>Any value less than 0 denotes a failure.</remarks>
        public enum StatusCode
        {
            /// <summary>Success; no error encountered.</summary>
            Success                       =  0,

            /// <summary>Unknown connection (internal ID doesn't exist).</summary>
            NetworkIdMismatch             = -1,

            /// <summary>Unknown connection (internal ID in use by newer connection).</summary>
            NetworkVersionMismatch        = -2,

            /// <summary>Invalid operation given connection's state.</summary>
            NetworkStateMismatch          = -3,

            /// <summary>Packet data is too large to handle.</summary>
            NetworkPacketOverflow         = -4,

            /// <summary>Network send queue is full.</summary>
            NetworkSendQueueFull          = -5,

            /// <summary>A packet's header is invalid.</summary>
            // TODO Not in use anymore, should we delete?
            NetworkHeaderInvalid          = -6,

            /// <summary>Tried to process the same connection multiple times in a parallel job.</summary>
            NetworkDriverParallelForErr   = -7,

            /// <summary>Internal send handle is invalid.</summary>
            NetworkSendHandleInvalid      = -8,

            /// <summary>Tried to create an <see cref="IPCNetworkInterface" /> on a non-loopback address.</summary>
            NetworkArgumentMismatch       = -9,

            /// <summary>The underlying network socket has failed.</summary>
            NetworkSocketError            = -10,
        }
    }

    /// <summary>
    /// Public representation of a connection. Holds all information needed by the
    /// <see cref="NetworkDriver"> to link it to an internal virtual connection.
    /// </summary>
    public struct NetworkConnection : IEquatable<NetworkConnection>
    {
        /// <summary>Index of the connection in the internal connection list.</summary>
        internal int m_NetworkId;
        
        /// <summary>Version of the connection at the above index (indices can be reused).</summary>
        internal int m_NetworkVersion;

        /// <summary>Connection States</summary>
        public enum State
        {
            /// <summary>Indicates the connection is disconnected.</summary>
            Disconnected,
            /// <summary>Indicates the connection is being established.</summary>
            Connecting,
            /// <summary>Indicates the connection is connected.</summary>
            Connected
        }

        /// <summary>
        /// Disconnects a connection and marks it for deletion. The connection will be removed on
        /// the next frame. Same as <see cref="Close{T}">.
        /// </summary>
        /// <param name="driver">Driver to which the connection belongs.</param>
        /// <returns>An Error.StatusCode value (0 on success, -1 otherwise).</returns>
        public int Disconnect(NetworkDriver driver)
        {
            return driver.Disconnect(this);
        }

        /// <summary>
        /// Receive an event for this specific connection. Should be called until it returns
        /// <see cref="NetworkEvent.Type.Empty"/>, even if the connection is disconnected.
        /// </summary>
        /// <param name="driver">Driver to which the connection belongs.</param>
        /// <param name="stream">
        /// A <see cref="DataStreamReader">, that will only be populated if a <see cref="NetworkEvent.Type.Data"/>
        /// (or possibly <see cref="NetworkEvent.Type.Disconnect">) event was received.
        /// </param>
        /// <returns>The <see cref="NetworkEvent.Type"> of the event.</returns>
        public NetworkEvent.Type PopEvent(NetworkDriver driver, out DataStreamReader stream)
        {
            return driver.PopEventForConnection(this, out stream);
        }

        /// <summary>
        /// Receive an event for this specific connection. Should be called until it returns
        /// <see cref="NetworkEvent.Type.Empty"/>, even if the connection is disconnected.
        /// </summary>
        /// <param name="driver">Driver to which the connection belongs.</param>
        /// <param name="stream">
        /// A <see cref="DataStreamReader">, that will only be populated if a <see cref="NetworkEvent.Type.Data"/>
        /// (or possibly <see cref="NetworkEvent.Type.Disconnect">) event was received.
        /// </param>
        /// <param name="pipeline">
        /// The <see cref="NetworkPipeline"> on which the event was received. Will only be populated
        /// for <see cref="NetworkEvent.Type.Data"> events.
        /// </param>
        /// <returns>The <see cref="NetworkEvent.Type"> of the event.</returns>
        public NetworkEvent.Type PopEvent(NetworkDriver driver, out DataStreamReader stream, out NetworkPipeline pipeline)
        {
            return driver.PopEventForConnection(this, out stream, out pipeline);
        }

        /// <summary>
        /// Disconnects a connection and marks it for deletion. The connection will be removed on
        /// the next frame. Same as <see cref="Disconnect{T}">.
        /// </summary>
        /// <param name="driver">Driver to which the connection belongs.</param>
        /// <returns>An Error.StatusCode value (0 on success, -1 otherwise).</returns>
        public int Close(NetworkDriver driver)
        {
            if (m_NetworkId >= 0)
                return driver.Disconnect(this);
            return (int)Error.StatusCode.NetworkIdMismatch;
        }

        /// <summary>Checks to see if a connection is created.</summary>
        /// <remarks>
        /// Connections are considered as created only if they've been obtained by a call to
        /// <see cref="NetworkDriver.Accept"> or <see cref="NetworkDriver.Connect">.
        /// </remarks>
        /// <returns>Whether the connection is created or not.</returns>
        public bool IsCreated
        {
            get { return m_NetworkVersion != 0; }
        }

        /// <summary>Gets the state of the connection.</summary>
        /// <param name="driver">Driver to which the connection belongs.</param>
        /// <returns>The <see cref="State{T}"> value for the connection.</returns>
        public State GetState(NetworkDriver driver)
        {
            return driver.GetConnectionState(this);
        }

        public static bool operator==(NetworkConnection lhs, NetworkConnection rhs)
        {
            return lhs.m_NetworkId == rhs.m_NetworkId && lhs.m_NetworkVersion == rhs.m_NetworkVersion;
        }

        public static bool operator!=(NetworkConnection lhs, NetworkConnection rhs)
        {
            return lhs.m_NetworkId != rhs.m_NetworkId || lhs.m_NetworkVersion != rhs.m_NetworkVersion;
        }
        
        public override bool Equals(object o)
        {
            return this == (NetworkConnection)o;
        }
        
        public bool Equals(NetworkConnection o)
        {
            return this == o;
        }
        
        public override int GetHashCode()
        {
            return (m_NetworkId << 8) ^ m_NetworkVersion;
        }

        /// <summary>Gets the value of the connection's internal ID.</summary>
        public int InternalId => m_NetworkId;
    }
}
