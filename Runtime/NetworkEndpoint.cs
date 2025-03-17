using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Baselib;
using Unity.Baselib.LowLevel;
using Unity.Networking.Transport.Logging;
using Unity.Networking.Transport.Utilities;

namespace Unity.Networking.Transport
{
    /// <summary>
    /// Indicates the type of endpoint a <see cref="NetworkEndpoint"/> represents. Analoguous to a
    /// <c>sa_family_t</c> in traditional BSD sockets.
    /// </summary>
    public enum NetworkFamily
    {
        /// <summary>
        /// Invalid address family. This is the value used by default-valued endpoints.
        /// </summary>
        Invalid = 0,

        /// <summary>
        /// Family for IPv4 addresses (analoguous to <c>AF_INET</c> in traditional BSD sockets).
        /// </summary>
        Ipv4 = 2,

        /// <summary>
        /// Family for IPv6 addresses (analoguous to <c>AF_INET6</c> in traditional BSD sockets).
        /// </summary>
        Ipv6 = 23,

        /// <summary>
        /// Family for custom addresses, to be used if a custom <see cref="INetworkInterface"/>
        /// requires a <see cref="NetworkEndpoint"/> that's neither an IPv4 or IPv6 address.
        /// </summary>
        Custom = 255,
    }

    /// <summary>
    /// Representation of an endpoint on the network. Typically, this means an IP address and a port
    /// number, and the API provides means to make working with this kind of endpoint easier.
    /// Analoguous to a <c>sockaddr</c> structure in traditional BSD sockets.
    /// </summary>
    /// <remarks>
    /// While this structure can store an IP address, it can't be used to store domain names. In
    /// general, the Unity Transport package does not handle domain names and it is the user's
    /// responsibility to resolve domain names to IP addresses. This can be done with
    /// <c>System.Net.Dns.GetHostEntryAsync"</c> for example.
    /// </remarks>
    /// <example>
    /// <para>
    /// The code below shows how to obtain endpoint structures for different IP addresses and port
    /// combinations (noted in comments in the <c>IP_ADDRESS:PORT</c> format):
    /// </para>
    /// <code>
    ///     // 127.0.0.1:7777
    ///     NetworkEndpoint.LoopbackIpv4.WithPort(7777);
    ///     // 0.0.0.0:0
    ///     NetworkEndpoint.AnyIpv4;
    ///     // 192.168.0.42:7778
    ///     NetworkEndpoint.Parse("192.168.0.42", 7778);
    ///     // [fe80::210:5aff:feaa:20a2]:52000
    ///     NetworkEndpoint.Parse("fe80::210:5aff:feaa:20a2", 52000, NetworkFamily.Ipv6);
    /// </code>
    /// </example>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct NetworkEndpoint : IEquatable<NetworkEndpoint>
    {
        private const int k_Ipv4Length = 4;
        private const int k_Ipv6Length = 16;

        // Raw container is 64 bytes, minus one for the family, and 3 reserved for future use.
        private const int k_CustomLength = 60;

        private const int k_FamilyOffset = 60;

        /// <summary>
        /// Container for the raw address structure. There are no public members to this structure
        /// and it should be viewed as an opaque buffer of bytes.
        /// </summary>
        public struct TransferrableData
        {
            // We're just using this as a raw bunch of bytes, and not as a fixed list. Do not call
            // any of the fixed list methods on this as they are likely broken.
            internal FixedList64Bytes<byte> m_RawAddressContainer;
        }

        /// <summary>
        /// Raw representation of the address. This value is only useful if implementing your own
        /// <see cref="INetworkInterface"/> and you need access to the underlying data backing the
        /// endpoint. Otherwise there is no reason to access this field.
        /// </summary>
        public TransferrableData Transferrable;

#if UNITY_WEBGL && !UNITY_EDITOR
        private FixedString512Bytes m_AddressAsString;

        internal NetworkEndpoint(FixedString512Bytes addressAsString)
        {
            Transferrable = default;
            m_AddressAsString = addressAsString;
            Family = NetworkFamily.Custom;
        }
#endif

        // Helper to construct from the baselib structure.
        internal NetworkEndpoint(Binding.Baselib_NetworkAddress baselibAddress)
        {
            Transferrable = default; 
#if UNITY_WEBGL && !UNITY_EDITOR
            m_AddressAsString = default;
#endif

            if (baselibAddress.family == (byte)Binding.Baselib_NetworkAddress_Family.IPv4)
                Family = NetworkFamily.Ipv4;
            if (baselibAddress.family == (byte)Binding.Baselib_NetworkAddress_Family.IPv6)
                Family = NetworkFamily.Ipv6;

            *BaselibAddressPtr = baselibAddress;
        }

        internal unsafe byte* RawAddressPtr
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => (byte*)UnsafeUtility.AddressOf(ref Transferrable.m_RawAddressContainer);
        }

        internal unsafe Binding.Baselib_NetworkAddress* BaselibAddressPtr
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                CheckFamilyIsIPv4OrIPv6();
                return (Binding.Baselib_NetworkAddress*)RawAddressPtr;
            }
        }

        /// <summary>Get or set the family of the endpoint.</summary>
        /// <value>Address family of the endpoint.</value>
        public NetworkFamily Family
        {
            get => (NetworkFamily)RawAddressPtr[k_FamilyOffset];
            set
            {
                RawAddressPtr[k_FamilyOffset] = (byte)value;

                if (value == NetworkFamily.Ipv4)
                    BaselibAddressPtr->family = (byte)Binding.Baselib_NetworkAddress_Family.IPv4;
                if (value == NetworkFamily.Ipv6)
                    BaselibAddressPtr->family = (byte)Binding.Baselib_NetworkAddress_Family.IPv6;
            }
        }

        /// <summary>Whether the endpoint is valid or not (based on the address family).</summary>
        /// <value>True if family is IPv4, IPv6 or custom, false otherwise.</value>
        public bool IsValid => Family != NetworkFamily.Invalid;

        /// <summary>
        /// Length of the raw address representation. Does not include the size of the port and of
        /// the family. Generally, there's no use for this property except for low-level code.
        /// </summary>
        /// <value>Length in bytes.</value>
        public int Length
        {
            get
            {
                switch (Family)
                {
                    case NetworkFamily.Ipv4:
                        return k_Ipv4Length;
                    case NetworkFamily.Ipv6:
                        return k_Ipv6Length;
                    case NetworkFamily.Custom:
                        return k_CustomLength;
                    default:
                        return 0;
                }
            }
        }

        /// <summary>Get or set the port number of the endpoint (IPv4 or IPv6 only).</summary>
        /// <value>Port number.</value>
        public ushort Port
        {
            get => (ushort)(BaselibAddressPtr->port1 | (BaselibAddressPtr->port0 << 8));
            set
            {
                BaselibAddressPtr->port0 = (byte)((value >> 8) & 0xff);
                BaselibAddressPtr->port1 = (byte)(value & 0xff);
            }
        }

        /// <summary>
        /// Get or set the raw value of the endpoint's port number. This is only useful to interface
        /// with low-level native libraries. Prefer <see cref="Port"/> in most circumstances, since
        /// that value will always match the endianness of the current platform.
        /// </summary>
        /// <value>Port value in network byte order.</value>
        [Obsolete("Use Port instead, and use standard C# APIs to convert to/from network byte order.")]
        public ushort RawPort
        {
            get => (ushort)((BaselibAddressPtr->port1 << 8) | BaselibAddressPtr->port0);
            set
            {
                BaselibAddressPtr->port0 = (byte)(value & 0xff);
                BaselibAddressPtr->port1 = (byte)((value >> 8) & 0xff);
            }
        }

        /// <summary>Get a copy of the endpoint that uses the specified port.</summary>
        /// <param name="port">Port number of the new endpoint.</param>
        /// <returns>Copy of the endpoint that uses the given port.</returns>
        public NetworkEndpoint WithPort(ushort port)
        {
            var endpoint = this;
            endpoint.Port = port;
            return endpoint;
        }

        /// <summary>
        /// Get the raw representation of the endpoint's address. This is only useful for low-level
        /// code that must interface with native libraries, for example if writing a custom
        /// implementation of <see cref="INetworkInterface"/>.
        /// </summary>
        /// <returns>Temporary native array with raw representation of the endpoint.</returns>
        public NativeArray<byte> GetRawAddressBytes()
        {
            var bytes = new NativeArray<byte>(Length, Allocator.Temp);
            UnsafeUtility.MemCpy(bytes.GetUnsafePtr(), RawAddressPtr, Length);
            return bytes;
        }

        /// <summary>
        /// Set the raw representation of the endpoint's address and set its family. This is only
        /// useful for low-level code that must interface with native libraries, for example if
        /// writing a custom implementation of <see cref="INetworkInterface"/>.
        /// </summary>
        /// <param name="bytes">Raw representation of the endpoint.</param>
        /// <param name="family">Address family of the raw representation.</param>
        public void SetRawAddressBytes(NativeArray<byte> bytes, NetworkFamily family = NetworkFamily.Ipv4)
        {
            CheckRawAddressLength(bytes.Length, family);

            var length = math.min(bytes.Length, k_CustomLength);
            UnsafeUtility.MemCpy(RawAddressPtr, bytes.GetUnsafeReadOnlyPtr(), length);
            Family = family;
        }

        /// <summary>Shortcut for the wildcard IPv4 address (0.0.0.0).</summary>
        /// <value>Endpoint structure for the 0.0.0.0 IPv4 address.</value>
        public static NetworkEndpoint AnyIpv4 => new NetworkEndpoint { Family = NetworkFamily.Ipv4 };

        /// <summary>Shortcut for the wildcard IPv6 address (::).</summary>
        /// <value>Endpoint structure for the :: IPv6 address.</value>
        public static NetworkEndpoint AnyIpv6 => new NetworkEndpoint { Family = NetworkFamily.Ipv6 };

        /// <summary>Shortcut for the loopback/localhost IPv4 address (127.0.0.1).</summary>
        /// <value>Endpoint structure for the 127.0.0.1 IPv4 address.</value>
        public static unsafe NetworkEndpoint LoopbackIpv4
        {
            get
            {
                var endpoint = new NetworkEndpoint { Family = NetworkFamily.Ipv4 };
                endpoint.BaselibAddressPtr->data0 = 127;
                endpoint.BaselibAddressPtr->data3 = 1;
                return endpoint;
            }
        }

        /// <summary>Shortcut for the loopback/localhost IPv6 address (::1).</summary>
        /// <value>Endpoint structure for the ::1 IPv6 address.</value>
        public static unsafe NetworkEndpoint LoopbackIpv6
        {
            get
            {
                var endpoint = new NetworkEndpoint { Family = NetworkFamily.Ipv6 };
                endpoint.BaselibAddressPtr->data15 = 1;
                return endpoint;
            }
        }

        /// <summary>Whether the endpoint is for a wildcard address.</summary>
        /// <value>True if the address is 0.0.0.0 or ::.</value>
        public bool IsAny =>
            (Family == NetworkFamily.Ipv4 || Family == NetworkFamily.Ipv6) &&
            (this == AnyIpv4.WithPort(Port)) || (this == AnyIpv6.WithPort(Port));

        /// <summary>Whether the endpoint is for a loopback address.</summary>
        /// <value>True if the address is 127.0.0.1 or ::1.</value>
        public bool IsLoopback =>
            (Family == NetworkFamily.Ipv4 || Family == NetworkFamily.Ipv6) &&
            (this == LoopbackIpv4.WithPort(Port)) || (this == LoopbackIpv6.WithPort(Port));

        /// <summary>
        /// Attempt to parse the provided IP address and port. Prefer this method when parsing IP
        /// addresses and port numbers coming from user inputs.
        /// </summary>
        /// <param name="address">IP address to parse.</param>
        /// <param name="port">Port number to parse.</param>
        /// <param name="endpoint">Return value for the endpoint if successfully parsed.</param>
        /// <param name="family">Address family of the provided address.</param>
        /// <returns>True if endpoint could be parsed successfully, false otherwise.</returns>
        public static bool TryParse(string address, ushort port, out NetworkEndpoint endpoint, NetworkFamily family = NetworkFamily.Invalid)
        {
            if (family == NetworkFamily.Invalid)
            {
                endpoint = default;

                if (TryParse(address, port, out endpoint, NetworkFamily.Ipv4))
                {
                    return true;
                }

                return TryParse(address, port, out endpoint, NetworkFamily.Ipv6);
            }
            endpoint = default;

            if (family != NetworkFamily.Ipv4 && family != NetworkFamily.Ipv6)
            {
                DebugLog.LogError("Can only parse addresses that are IPv4 or IPv6.");
                return false;
            }

#if UNITY_SWITCH
            if (family == NetworkFamily.Ipv6)
            {
                DebugLog.LogError("IPv6 is not supported on Switch.");
                return false;
            }
#endif

#if (UNITY_PS4 || UNITY_PS5)
            if (family == NetworkFamily.Ipv6)
            {
                DebugLog.LogError("IPv6 is not supported on PlayStation platforms.");
                return false;
            }
#endif

            endpoint.Family = family;

            var addressBytes = System.Text.Encoding.UTF8.GetBytes(address + '\0');
            var baselibFamily = family == NetworkFamily.Ipv4 ? Binding.Baselib_NetworkAddress_Family.IPv4 : Binding.Baselib_NetworkAddress_Family.IPv6;
            var errorState = default(Binding.Baselib_ErrorState);

            fixed (byte* addressPtr = addressBytes)
            {
                Binding.Baselib_NetworkAddress_Encode(endpoint.BaselibAddressPtr, baselibFamily, addressPtr, port, &errorState);
            }

            if (errorState.code == Binding.Baselib_ErrorCode.Success)
            {
                return true;
            }
            else
            {
                endpoint.Family = NetworkFamily.Invalid;
                return false;
            }
        }

        /// <summary>
        /// Parse the provided IP address and port. Prefer this method when parsing IP addresses
        /// and ports that are known to be good (e.g. hardcoded values).
        /// </summary>
        /// <param name="address">IP address to parse.</param>
        /// <param name="port">Port number to parse.</param>
        /// <param name="family">Address family of the provided address.</param>
        /// <returns>Parsed endpoint, or a default value if couldn't parse successfully.</returns>
        public static NetworkEndpoint Parse(string address, ushort port, NetworkFamily family = NetworkFamily.Invalid)
        {
            return TryParse(address, port, out var endpoint, family) ? endpoint : default;
        }

        /// <summary>
        /// Get a fixed string representation of the endpoint. Useful for contexts where managed
        /// types (like <see cref="string"/>) can't be used (e.g. Burst-compiled code).
        /// </summary>
        /// <remarks>This method is limited to 125 bytes, but on WebGL, NetworkEndpoint supports encapsulating
        /// 512-byte URLs. In this situation, if your URL exceeds 125 bytes, you need to use
        /// <see cref="ToFixedString512Bytes"/> instead.</remarks>
        /// <returns>Fixed string representation of the endpoint.</returns>
        public FixedString128Bytes ToFixedString()
        {
            var result = ToFixedString512Bytes();
            if (result.Length > FixedString128Bytes.UTF8MaxLengthInBytes)
            {
                throw new Exception("Endpoint URL is too long. Use ToFixedString512Bytes instead.");
            }

            return new FixedString128Bytes(result);
        }
        
        /// <summary>
        /// Get a fixed string representation of the endpoint. Useful for contexts where managed
        /// types (like <see cref="string"/>) can't be used (e.g. Burst-compiled code).
        /// </summary>
        /// <returns>Fixed string representation of the endpoint.</returns>
        public FixedString512Bytes ToFixedString512Bytes()
        { 
#if UNITY_WEBGL && !UNITY_EDITOR
            if (m_AddressAsString != default)
            {
                return m_AddressAsString;
            }
#endif
            var str = default(FixedString128Bytes);
            var temp = default(FixedString32Bytes);

            var ptr = RawAddressPtr;

            switch (Family)
            {
                case NetworkFamily.Ipv4:
                    str = $"{ptr[0]}.{ptr[1]}.{ptr[2]}.{ptr[3]}";
                    break;
                case NetworkFamily.Ipv6:
                    str.Append('[');
                    for (int i = 0; i < k_Ipv6Length; i += 2)
                    {
                        temp = $"{ptr[i]:x2}{ptr[i+1]:x2}:";
                        str.Append(temp);
                    }
                    str.Length -= 1;
                    str.Append(']');
                    break;
                case NetworkFamily.Custom:
                    str = "custom:0x";
                    for (int i = 0; i < k_CustomLength; i++)
                    {
                        temp = $"{ptr[i]:x2}";
                        str.Append(temp);
                    }
                    break;
                case NetworkFamily.Invalid:
                default:
                    return "invalid";
            }

            if (Family == NetworkFamily.Ipv4 || Family == NetworkFamily.Ipv6)
            {
                str.Append(':');
                str.Append(Port);
            }

            return str;
        }
        
        /// <summary>
        /// Get a fixed string representation of the endpoint. Useful for contexts where managed
        /// types (like <see cref="string"/>) can't be used (e.g. Burst-compiled code).
        /// </summary>
        /// <returns>Fixed string representation of the endpoint.</returns>
        public FixedString512Bytes ToFixedStringNoPort()
        { 
#if UNITY_WEBGL && !UNITY_EDITOR
            if (Family == NetworkFamily.Custom)
            {
                return m_AddressAsString;
            }
#endif
            var str = default(FixedString128Bytes);
            var temp = default(FixedString32Bytes);

            var ptr = RawAddressPtr;

            switch (Family)
            {
                case NetworkFamily.Ipv4:
                    str = $"{ptr[0]}.{ptr[1]}.{ptr[2]}.{ptr[3]}";
                    break;
                case NetworkFamily.Ipv6:
                    str.Append('[');
                    for (int i = 0; i < k_Ipv6Length; i += 2)
                    {
                        temp = $"{ptr[i]:x2}{ptr[i+1]:x2}:";
                        str.Append(temp);
                    }
                    str.Length -= 1;
                    str.Append(']');
                    break;
                case NetworkFamily.Custom:
                    str = "custom:0x";
                    for (int i = 0; i < k_CustomLength; i++)
                    {
                        temp = $"{ptr[i]:x2}";
                        str.Append(temp);
                    }
                    break;
                case NetworkFamily.Invalid:
                default:
                    return "invalid";
            }

            return str;
        }

        /// <summary>String representation of the endpoint. Same as <see cref="ToString"/>.</summary>
        /// <value>Endpoint represented as a string.</value>
        public string Address => ToString();

        /// <inheritdoc/>
        public override string ToString()
        {
            return ToFixedString512Bytes().ToString();
        }

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            var size = UnsafeUtility.SizeOf<FixedList64Bytes<byte>>();
            return (int)CollectionHelper.Hash(RawAddressPtr, size);
        }

        /// <inheritdoc/>
        public bool Equals(NetworkEndpoint other)
        {
            var size = UnsafeUtility.SizeOf<FixedList64Bytes<byte>>();
            return UnsafeUtility.MemCmp(RawAddressPtr, other.RawAddressPtr, size) == 0;
        }

        /// <inheritdoc/>
        public override bool Equals(object other)
        {
            return this.Equals((NetworkEndpoint)other);
        }

        /// <inheritdoc/>
        public static bool operator==(NetworkEndpoint lhs, NetworkEndpoint rhs)
        {
            return lhs.Equals(rhs);
        }

        /// <inheritdoc/>
        public static bool operator!=(NetworkEndpoint lhs, NetworkEndpoint rhs)
        {
            return !lhs.Equals(rhs);
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        private void CheckFamilyIsIPv4OrIPv6()
        {
            if (Family != NetworkFamily.Ipv4 && Family != NetworkFamily.Ipv6)
                throw new InvalidOperationException($"Trying to access endpoint as IPv4 or IPv6, but family is {Family}.");
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        private static void CheckRawAddressLength(int length, NetworkFamily family)
        {
            if (family == NetworkFamily.Ipv4 && length != k_Ipv4Length)
                throw new ArgumentException($"Raw IPv4 addresses must be {k_Ipv4Length} bytes long (got {length}).");

            if (family == NetworkFamily.Ipv6 && length != k_Ipv6Length)
                throw new ArgumentException($"Raw IPv6 addresses must be {k_Ipv4Length} bytes long (got {length}).");

            if (family == NetworkFamily.Custom && length > k_CustomLength)
                throw new ArgumentException($"Raw custom addresses must be at least {k_CustomLength} bytes long (got {length}).");

            if (family == NetworkFamily.Invalid)
                throw new ArgumentException("Can't set raw address if family is invalid.");
        }
    }

    /// <summary>Obsolete. Should be automatically updated to <see cref="NetworkEndpoint"/>.</summary>
    [Obsolete("NetworkEndPoint has been renamed to NetworkEndpoint. (UnityUpgradable) -> NetworkEndpoint", true)]
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public struct NetworkEndPoint {}

    /// <summary>Obsolete. Part of the old <c>INetworkInterface</c> API.</summary>
    [Obsolete("Use NetworkEndpoint instead.", true)]
    public struct NetworkInterfaceEndPoint : IEquatable<NetworkInterfaceEndPoint>
    {
        /// <inheritdoc/>
        public bool Equals(NetworkInterfaceEndPoint other)
        {
            throw new NotImplementedException();
        }
    }
}
