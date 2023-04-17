using System;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Baselib;
using Unity.Baselib.LowLevel;
using Unity.Networking.Transport.Logging;
using Unity.Networking.Transport.Utilities;
using ErrorState = Unity.Baselib.LowLevel.Binding.Baselib_ErrorState;

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
        Ipv6 = 23
    }

    /// <summary>Obsolete. Should be automatically updated to <see cref="NetworkEndpoint"/>.</summary>
    [Obsolete("NetworkEndPoint has been renamed to NetworkEndpoint. (UnityUpgradable) -> NetworkEndpoint", true)]
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public struct NetworkEndPoint {}

    /// <summary>
    /// Representation of an endpoint on the network. Typically, this means an IP address and a port
    /// number, and the API provides means to make working with this kind of endpoint easier.
    /// Analoguous to a <c>sockaddr</c> structure in traditional BSD sockets.
    /// </summary>
    /// <remarks>
    /// While this structure can store an IP address, it can't be used to store domain names. In
    /// general, the Unity Transport package does not handle domain names and it is the user's
    /// responsibility to resolve domain names to IP addresses. This can be done with
    /// <c>System.Net.Dns.GetHostEntryAsync"</c>.
    /// </remarks>
    /// <example>
    /// The code below shows how to obtain endpoint structures for different IP addresses and port
    /// combinations (noted in comments in the <c>IP_ADDRESS:PORT</c> format):
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
        enum AddressType { Any = 0, Loopback = 1 }
        private const int rawIpv4Length = 4;
        private const int rawIpv6Length = 16;
        private const int rawDataLength = 16;               // Maximum space needed to hold a IPv6 Address
#if !UNITY_2021_1_OR_NEWER && !UNITY_DOTSRUNTIME
        private const int rawLength = rawDataLength + 4;    // SizeOf<Baselib_NetworkAddress>
#else
        private const int rawLength = rawDataLength + 8;    // SizeOf<Baselib_NetworkAddress>
#endif
        private static readonly bool IsLittleEndian = true;

        internal Binding.Baselib_NetworkAddress rawNetworkAddress;

        /// <summary>Length of the raw endpoint representation.</summary>
        /// <value>Length in bytes.</value>
        public int Length
        {
            get
            {
                switch (Family)
                {
                    case NetworkFamily.Ipv4:
                        return rawIpv4Length;
                    case NetworkFamily.Ipv6:
                        return rawIpv6Length;
                    case NetworkFamily.Invalid:
                    default:
                        return 0;
                }
            }
        }

        static NetworkEndpoint()
        {
            uint test = 1;
            byte* test_b = (byte*)&test;
            IsLittleEndian = test_b[0] == 1;
        }

        /// <summary>Get or set the port number of the endpoint.</summary>
        /// <value>Port number.</value>
        public ushort Port
        {
            get => (ushort)(rawNetworkAddress.port1 | (rawNetworkAddress.port0 << 8));
            set
            {
                rawNetworkAddress.port0 = (byte)((value >> 8) & 0xff);
                rawNetworkAddress.port1 = (byte)(value & 0xff);
            }
        }

        /// <summary>Get or set the family of the endpoint.</summary>
        /// <value>Address family of the endpoint.</value>
        public NetworkFamily Family
        {
            get => FromBaselibFamily((Binding.Baselib_NetworkAddress_Family)rawNetworkAddress.family);
            set => rawNetworkAddress.family = (byte)ToBaselibFamily(value);
        }

        /// <summary>
        /// Get the raw representation of the endpoint. This is only useful for low-level code that
        /// must interface with native libraries, for example if writing a custom implementation of
        /// <see cref="INetworkInterface"/>. The raw representation of an endpoint will match that
        /// of an appropriate <c>sockaddr</c> structure for the current platform.
        /// </summary>
        /// <returns>Temporary native array with raw representation of the endpoint.</returns>
        public NativeArray<byte> GetRawAddressBytes()
        {
            var bytes = new NativeArray<byte>(Length, Allocator.Temp);
            UnsafeUtility.MemCpy(bytes.GetUnsafePtr(), UnsafeUtility.AddressOf(ref rawNetworkAddress), Length);
            return bytes;
        }

        /// <summary>
        /// Set the raw representation of the endpoint. This is only useful for low-level code that
        /// must interface with native libraries, for example if writing a custom implementation of
        /// <see cref="INetworkInterface"/>. The raw representation of an endpoint must match that
        /// of an appropriate <c>sockaddr</c> structure for the current platform.
        /// </summary>
        /// <param name="bytes">Raw representation of the endpoint.</param>
        /// <param name="family">Address family of the raw representation.</param>
        public void SetRawAddressBytes(NativeArray<byte> bytes, NetworkFamily family = NetworkFamily.Ipv4)
        {
            if ((family == NetworkFamily.Ipv4 && bytes.Length != rawIpv4Length) ||
                (family == NetworkFamily.Ipv6 && bytes.Length != rawIpv6Length))
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                throw new InvalidOperationException("Bad input length for given address family.");
#else
                DebugLog.LogError("Bad input length for given address family.");
                return;
#endif
            }

            if (family == NetworkFamily.Ipv4)
            {
                UnsafeUtility.MemCpy(UnsafeUtility.AddressOf(ref rawNetworkAddress), bytes.GetUnsafeReadOnlyPtr(), rawIpv4Length);
                Family = family;
            }
            else if (family == NetworkFamily.Ipv6)
            {
                UnsafeUtility.MemCpy(UnsafeUtility.AddressOf(ref rawNetworkAddress), bytes.GetUnsafeReadOnlyPtr(), rawIpv6Length);
                Family = family;
            }
        }

        /// <summary>
        /// Get or set the raw value of the endpoint's port number. This is only useful to interface
        /// with low-level native libraries. Prefer <see cref="Port"/> in most circumstances, since
        /// that value will always match the endianness of the current platform.
        /// </summary>
        /// <value>Port value in network byte order.</value>
        public ushort RawPort
        {
            get
            {
                ushort *port = (ushort*)((byte*)UnsafeUtility.AddressOf(ref rawNetworkAddress) + rawDataLength);
                return *port;
            }
            set
            {
                ushort *port = (ushort*)((byte*)UnsafeUtility.AddressOf(ref rawNetworkAddress) + rawDataLength);
                *port = value;
            }
        }

        /// <summary>String representation of the endpoint. Same as <see cref="ToString"/>.</summary>
        /// <value>Endpoint represented as a string.</value>
        public string Address => ToString();

        /// <summary>Whether the endpoint is valid or not (whether it's IPv4 or IPv6).</summary>
        /// <value>True if family is IPv4 or IPv6, false otherwise.</value>
        public bool IsValid => Family != 0;

        /// <summary>Shortcut for the wildcard IPv4 address (0.0.0.0).</summary>
        /// <value>Endpoint structure for the 0.0.0.0 IPv4 address.</value>
        public static NetworkEndpoint AnyIpv4 => CreateAddress(0);

        /// <summary>Shortcut for the loopback/localhost IPv4 address (127.0.0.1).</summary>
        /// <value>Endpoint structure for the 127.0.0.1 IPv4 address.</value>
        public static NetworkEndpoint LoopbackIpv4 => CreateAddress(0, AddressType.Loopback);

        /// <summary>Shortcut for the wildcard IPv6 address (::).</summary>
        /// <value>Endpoint structure for the :: IPv6 address.</value>
        public static NetworkEndpoint AnyIpv6 => CreateAddress(0, AddressType.Any, NetworkFamily.Ipv6);

        /// <summary>Shortcut for the loopback/localhost IPv6 address (::1).</summary>
        /// <value>Endpoint structure for the ::1 IPv6 address.</value>
        public static NetworkEndpoint LoopbackIpv6 => CreateAddress(0, AddressType.Loopback, NetworkFamily.Ipv6);

        /// <summary>Get a copy of the endpoint that uses the specified port.</summary>
        /// <param name="port">Port number of the new endpoint.</param>
        /// <returns>Copy of the endpoint that uses the given port.</returns>
        public NetworkEndpoint WithPort(ushort port)
        {
            var ep = this;
            ep.Port = port;
            return ep;
        }

        /// <summary>Whether the endpoint is for a wildcard address.</summary>
        /// <value>True if the address is 0.0.0.0 or ::.</value>
        public bool IsAny => (this == AnyIpv4.WithPort(Port)) || (this == AnyIpv6.WithPort(Port));

        /// <summary>Whether the endpoint is for a loopback address.</summary>
        /// <value>True if the address is 127.0.0.1 or ::1.</value>
        public bool IsLoopback => (this == LoopbackIpv4.WithPort(Port)) || (this == LoopbackIpv6.WithPort(Port));

        /// <summary>
        /// Attempt to parse the provided IP address and port. Prefer this method when parsing IP
        /// addresses and port numbers coming from user inputs.
        /// </summary>
        /// <param name="address">IP address to parse.</param>
        /// <param name="port">Port number to parse.</param>
        /// <param name="endpoint">Return value for the endpoint if successfully parsed.</param>
        /// <param name="family">Address family of the provided address.</param>
        /// <returns>True if endpoint could be parsed successfully, false otherwise.</return>
        public static bool TryParse(string address, ushort port, out NetworkEndpoint endpoint, NetworkFamily family = NetworkFamily.Ipv4)
        {
            endpoint = default;

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

            var nullTerminator = '\0';
            var errorState = default(ErrorState);
            var ipBytes = System.Text.Encoding.UTF8.GetBytes(address + nullTerminator);

            fixed(byte* ipBytesPtr = ipBytes)
            fixed(Binding.Baselib_NetworkAddress * rawAddress = &endpoint.rawNetworkAddress)
            {
                Binding.Baselib_NetworkAddress_Encode(
                    rawAddress,
                    ToBaselibFamily(family),
                    ipBytesPtr,
                    (ushort)port,
                    &errorState);
            }

            if (errorState.code != Binding.Baselib_ErrorCode.Success)
            {
                return false;
            }
            return endpoint.IsValid;
        }

        /// <summary>
        /// Parse the provided IP address and port. Prefer this method when parsing IP addresses
        /// and ports that are known to be good (e.g. hardcoded values).
        /// </summary>
        /// <param name="address">IP address to parse.</param>
        /// <param name="port">Port number to parse.</param>
        /// <param name="family">Address family of the provided address.</param>
        /// <returns>Parsed endpoint, or a default value if couldn't parse successfully.</returns>
        public static NetworkEndpoint Parse(string address, ushort port, NetworkFamily family = NetworkFamily.Ipv4)
        {
            if (TryParse(address, port, out var endpoint, family))
                return endpoint;

            return default;
        }

        public static bool operator==(NetworkEndpoint lhs, NetworkEndpoint rhs)
        {
            return lhs.Compare(rhs);
        }

        public static bool operator!=(NetworkEndpoint lhs, NetworkEndpoint rhs)
        {
            return !lhs.Compare(rhs);
        }

        public override bool Equals(object other)
        {
            return this == (NetworkEndpoint)other;
        }

        public bool Equals(NetworkEndpoint other)
        {
            return this == other;
        }

        public override int GetHashCode()
        {
            var p = (byte*)UnsafeUtility.AddressOf(ref rawNetworkAddress);
            unchecked
            {
                var result = 0;

                for (int i = 0; i < rawLength; i++)
                {
                    result = (result * 31) ^ (int)p[i];
                }

                return result;
            }
        }

        bool Compare(NetworkEndpoint other)
        {
            var p = (byte*)UnsafeUtility.AddressOf(ref rawNetworkAddress);
            var p1 = (byte*)UnsafeUtility.AddressOf(ref other.rawNetworkAddress);
            return UnsafeUtility.MemCmp(p, p1, rawLength) == 0;
        }

        internal static FixedString128Bytes AddressToString(ref Binding.Baselib_NetworkAddress rawNetworkAddress)
        {
            FixedString128Bytes str = default;
            FixedString32Bytes dot = ".";
            FixedString32Bytes colon = ":";
            FixedString32Bytes opensqb = "[";
            FixedString32Bytes closesqb = "]";
            switch ((Binding.Baselib_NetworkAddress_Family)rawNetworkAddress.family)
            {
                case Binding.Baselib_NetworkAddress_Family.IPv4:
                    // TODO(steve): Update to use ipv4_0 ... 3 when its available.
                    str.Append(rawNetworkAddress.data0);
                    str.Append(dot);
                    str.Append(rawNetworkAddress.data1);
                    str.Append(dot);
                    str.Append(rawNetworkAddress.data2);
                    str.Append(dot);
                    str.Append(rawNetworkAddress.data3);

                    str.Append(colon);
                    str.Append((ushort)(rawNetworkAddress.port1 | (rawNetworkAddress.port0 << 8)));
                    break;
                case Binding.Baselib_NetworkAddress_Family.IPv6:
                    // TODO(steve): Include scope and handle leading zeros
                    // TODO(steve): Update to use ipv6_0 ... 15 when its available.
                    str.Append(opensqb);

                    str.AppendHex((ushort)(rawNetworkAddress.data1 | (rawNetworkAddress.data0 << 8)));
                    str.Append(colon);
                    str.AppendHex((ushort)(rawNetworkAddress.data3 | (rawNetworkAddress.data2 << 8)));
                    str.Append(colon);
                    str.AppendHex((ushort)(rawNetworkAddress.data5 | (rawNetworkAddress.data4 << 8)));
                    str.Append(colon);
                    str.AppendHex((ushort)(rawNetworkAddress.data7 | (rawNetworkAddress.data6 << 8)));
                    str.Append(colon);
                    str.AppendHex((ushort)(rawNetworkAddress.data9 | (rawNetworkAddress.data8 << 8)));
                    str.Append(colon);
                    str.AppendHex((ushort)(rawNetworkAddress.data11 | (rawNetworkAddress.data10 << 8)));
                    str.Append(colon);
                    str.AppendHex((ushort)(rawNetworkAddress.data13 | (rawNetworkAddress.data12 << 8)));
                    str.Append(colon);
                    str.AppendHex((ushort)(rawNetworkAddress.data15 | (rawNetworkAddress.data14 << 8)));
                    str.Append(colon);

                    str.Append(closesqb);
                    str.Append(colon);
                    str.Append((ushort)(rawNetworkAddress.port1 | (rawNetworkAddress.port0 << 8)));
                    break;
                default:
                    return "invalid";
            }
            return str;
        }

        public override string ToString()
        {
            return ToFixedString().ToString();
        }

        /// <summary>
        /// Get a fixed string representation of the endpoint. Useful for contexts where managed
        /// types (like <see cref="string"/>) can't be used (e.g. Burst-compiled code).
        /// </summary>
        /// <returns>Fixed string representation of the endpoint.</returns>
        public FixedString128Bytes ToFixedString()
        {
            return AddressToString(ref rawNetworkAddress);
        }

        private static ushort ByteSwap(ushort val)
        {
            return (ushort)(((val & 0xff) << 8) | (val >> 8));
        }

        private static uint ByteSwap(uint val)
        {
            return (uint)(((val & 0xff) << 24) | ((val & 0xff00) << 8) | ((val >> 8) & 0xff00) | (val >> 24));
        }

        static NetworkEndpoint CreateAddress(ushort port, AddressType type = AddressType.Any, NetworkFamily family = NetworkFamily.Ipv4)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            UnityEngine.Debug.Assert(UnsafeUtility.SizeOf<Binding.Baselib_NetworkAddress>() == rawLength);
#endif
            if (family == NetworkFamily.Invalid)
                return default;

            uint ipv4Loopback = (127 << 24) | 1;

            if (IsLittleEndian)
            {
                port = ByteSwap(port);
                ipv4Loopback = ByteSwap(ipv4Loopback);
            }

            var ep = new NetworkEndpoint
            {
                Family = family,
                RawPort = port
            };

            if (type == AddressType.Loopback)
            {
                if (family == NetworkFamily.Ipv4)
                {
                    *(uint*)UnsafeUtility.AddressOf(ref ep.rawNetworkAddress) = ipv4Loopback;
                }
                else if (family == NetworkFamily.Ipv6)
                {
                    ep.rawNetworkAddress.data15 = 1;
                }
            }
            return ep;
        }

        static NetworkFamily FromBaselibFamily(Binding.Baselib_NetworkAddress_Family family)
        {
            if (family == Binding.Baselib_NetworkAddress_Family.IPv4)
                return NetworkFamily.Ipv4;
            if (family == Binding.Baselib_NetworkAddress_Family.IPv6)
                return NetworkFamily.Ipv6;
            return NetworkFamily.Invalid;
        }

        static Binding.Baselib_NetworkAddress_Family ToBaselibFamily(NetworkFamily family)
        {
            if (family == NetworkFamily.Ipv4)
                return Binding.Baselib_NetworkAddress_Family.IPv4;
            if (family == NetworkFamily.Ipv6)
                return Binding.Baselib_NetworkAddress_Family.IPv6;
            return Binding.Baselib_NetworkAddress_Family.Invalid;
        }
    }

    /// <summary>Obsolete. Part of the old <c>INetworkInterface</c> API.</summary>
    [Obsolete("Use NetworkEndpoint instead.", true)]
    public struct NetworkInterfaceEndPoint : IEquatable<NetworkInterfaceEndPoint>
    {
        public bool Equals(NetworkInterfaceEndPoint other)
        {
            throw new NotImplementedException();
        }
    }
}
