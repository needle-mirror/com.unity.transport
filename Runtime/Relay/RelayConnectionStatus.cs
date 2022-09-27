namespace Unity.Networking.Transport.Relay
{
    /// <summary>State of the connection to the relay server.</summary>
    public enum RelayConnectionStatus
    {
        /// <summary>Connection has yet to be established to the relay server.</summary>
        /// <remarks>
        /// Establishing a connection will be done automatically when calling <see cref="NetworkDriver.Connect" />
        /// or <see cref="NetworkDriver.Bind" />. If the connection is successful, the status changes to
        /// <see cref="Established" />. If not successful, the status changes to <see cref="AllocationInvalid" />.
        /// </remarks>
        NotEstablished = 0,

        /// <summary>Connection to the relay server is established.</summary>
        /// <remarks>
        /// Once a connection to the relay server is established, it will remain so until either the
        /// <see cref="NetworkDriver" /> is disposed of, or an error occurs that invalidates the relay
        /// service allocation. In the latter case, the status will change to <see cref="AllocationInvalid" />.
        /// </remarks>
        Established,

        /// <summary>Connection to the relay server has failed due to an invalid allocation.</summary>
        /// <remarks>
        /// This status indicates that the allocation used to connect to the relay server is invalid,
        /// either because an invalid allocation was provided in <see cref="NetworkSettings.WithRelayParameters" />
        /// or because the allocation timed out due to inactivity.
        ///
        /// In both cases, this is an unrecoverable error. A new allocation needs to be created through
        /// the relay service, and a new <see cref="NetworkDriver" /> needs to be created with that
        /// allocation.
        /// </remarks>
        AllocationInvalid
    }
}