using System;
using System.ComponentModel;
using Unity.Collections;

namespace Unity.Networking.Transport
{
    namespace Error
    {
        /// <summary>
        /// DisconnectReason enumerates all disconnect reasons.
        /// </summary>
        public enum DisconnectReason : byte
        {
            // This enum is matched by NetworkStreamDisconnectReason in Netcode for Entities, so any
            // change to it should be discussed and properly synchronized with them first.

            /// <summary>Indicates a normal disconnection as a result of calling Disconnect on the connection.</summary>
            Default, // don't assign explicit values
            /// <summary>Indicates the connection timed out.</summary>
            Timeout,
            /// <summary>Indicates the connection failed to establish a connection after <see cref="NetworkConfigParameter.maxConnectAttempts"/>.</summary>
            MaxConnectionAttempts,
            /// <summary>Indicates the connection was closed remotely.</summary>
            ClosedByRemote,
            /// <summary>Used only for count. Keep last and don't assign explicit values</summary>
            Count
        }

        /// <summary>
        /// Status codes that can be returned by many functions in the transport API.
        /// </summary>
        public enum StatusCode
        {
            /// <summary>Operation completed successfully.</summary>
            Success                       =  0,

            /// <summary>Connection is invalid.</summary>
            NetworkIdMismatch             = -1,

            /// <summary>Connection is invalid.</summary>
            /// <remarks>Usually caused by an attempt to use a closed connection.</remarks>
            NetworkVersionMismatch        = -2,

            /// <summary>State of the connection is invalid for the operation requested.</summary>
            /// <remarks>Usually caused by an attempt to send on a connecting/closed connection.</remarks>
            NetworkStateMismatch          = -3,

            /// <summary>Packet is too large for the supported capacity.</summary>
            NetworkPacketOverflow         = -4,

            /// <summary>Packet couldn't be sent because the send queue is full.</summary>
            NetworkSendQueueFull          = -5,

            /// <summary>Obsolete. Will never be returned.</summary>
            [Obsolete("Return code is not in use anymore and nothing will return it.")]
            NetworkHeaderInvalid          = -6,

            /// <summary>Attempted to process the same connection in different jobs.</summary>
            NetworkDriverParallelForErr   = -7,

            /// <summary>The <see cref="DataStreamWriter"/> is invalid.</summary>
            NetworkSendHandleInvalid      = -8,

            /// <summary>Obsolete. Will never be returned.</summary>
            [Obsolete("Return code is not in use anymore and nothing will return it.")]
            NetworkArgumentMismatch       = -9,

            /// <summary>A message couldn't be received because the receive queue is full.</summary>
            /// <remarks>Can only be returned through <see cref="NetworkDriver.ReceiveErrorCode"/>.</remarks>
            NetworkReceiveQueueFull       = -10,

            /// <summary>There was an error from the underlying low-level socket.</summary>
            NetworkSocketError            = -11,
        }
    }

    /// <summary>
    /// The NetworkConnection is a struct that hold all information needed by the driver to link it with a virtual
    /// connection. The NetworkConnection is a public representation of a connection.
    /// </summary>
    public struct NetworkConnection : IEquatable<NetworkConnection>
    {
        internal ConnectionId m_ConnectionId;

        /// <summary>
        /// ConnectionState enumerates available connection states a connection can have.
        /// </summary>
        public enum State
        {
            /// <summary>Indicates the connection is disconnected</summary>
            Disconnected,

            /// <summary>
            /// Indicates the connection is in the process of being disconnected.
            /// This is an internal state and it is mapped to Disconnected at the
            /// NetworkDriver level.
            /// </summary>
            [EditorBrowsable(EditorBrowsableState.Never)] Disconnecting,

            /// <summary>Indicates the connection is trying to connect.</summary>
            Connecting,

            /// <summary>Indicates the connection is connected.. </summary>
            Connected,
        }

        internal NetworkConnection(ConnectionId connectionId)
        {
            m_ConnectionId = connectionId;
        }

        /// <summary>
        /// Disconnects a virtual connection and marks it for deletion. This connection will be removed on next the next frame.
        /// </summary>
        /// <param name="driver">The driver that owns the virtual connection.</param>
        public int Disconnect(NetworkDriver driver)
        {
            return driver.Disconnect(this);
        }

        /// <summary>
        /// Receive an event for this specific connection. Should be called until it returns <see cref="NetworkEvent.Type.Empty"/>, even if the socket is disconnected.
        /// </summary>
        /// <param name="driver">The driver that owns the virtual connection.</param>
        /// <param name="strm">A DataStreamReader, that will only be populated if a <see cref="NetworkEvent.Type.Data"/>
        /// event was received.
        /// </param>
        public NetworkEvent.Type PopEvent(NetworkDriver driver, out DataStreamReader stream)
        {
            return driver.PopEventForConnection(this, out stream);
        }

        public NetworkEvent.Type PopEvent(NetworkDriver driver, out DataStreamReader stream, out NetworkPipeline pipeline)
        {
            return driver.PopEventForConnection(this, out stream, out pipeline);
        }

        /// <summary>
        /// Close an active NetworkConnection, similar to <see cref="Disconnect{T}"/>.
        /// </summary>
        /// <param name="driver">The driver that owns the virtual connection.</param>
        public int Close(NetworkDriver driver)
        {
            if (m_ConnectionId.Id >= 0)
                return driver.Disconnect(this);
            return -1;
        }

        /// <summary>
        /// Check to see if a NetworkConnection is Created.
        /// </summary>
        /// <returns>`true` if the NetworkConnection has been correctly created by a call to
        /// <see cref="NetworkDriver.Accept"/> or <see cref="NetworkDriver.Connect"/></returns>
        public bool IsCreated
        {
            get { return m_ConnectionId.Version != 0; }
        }

        public State GetState(NetworkDriver driver)
        {
            return driver.GetConnectionState(this);
        }

        public static bool operator==(NetworkConnection lhs, NetworkConnection rhs)
        {
            return lhs.m_ConnectionId == rhs.m_ConnectionId;
        }

        public static bool operator!=(NetworkConnection lhs, NetworkConnection rhs)
        {
            return lhs.m_ConnectionId != rhs.m_ConnectionId;
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
            return m_ConnectionId.GetHashCode();
        }

        public override string ToString()
        {
            return $"NetworkConnection[id{InternalId},v{Version}]";
        }

        public int InternalId => m_ConnectionId.Id;
        public int Version => m_ConnectionId.Version;
    }
}
