using System;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Baselib;
using Unity.Baselib.LowLevel;
using Unity.Networking.Transport.Utilities;
using ErrorState = Unity.Baselib.LowLevel.Binding.Baselib_ErrorState;

namespace Unity.Networking.Transport
{
    /// <summary>
    /// Indicates the protocol family of the address (analogous of AF_* in sockets API).
    /// </summary>
    public enum NetworkFamily
    {
        /// <summary>Invalid network family.</summary>
        Invalid = 0,
        /// <summary>IPv4 (analogous to AF_INET).</summary>
        Ipv4 = 2,
        /// <summary>IPv6 (analogous to AF_INET6).</summary>
        Ipv6 = 23
    }

    /// <summary>
    /// Describes a raw network endpoint (typically IP and port number).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct NetworkEndPoint
    {
        /// <summary>
        /// Types of addresses with special handling.
        /// </summary>
        enum AddressType 
        { 
            /// <summary>Type to use when binding to any available address.</summary>
            Any = 0, 
            /// <summary>Loopback address (e.g. localhost connection).</summary>
            Loopback = 1 
        }
        
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

        /// <summary>
        /// Returns the length of the raw network endpoint in bytes.
        /// </summary>
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

        /// <summary>
        /// Initializes a new instance of <see cref="NetworkEndPoint"/>.
        /// </summary>
        static NetworkEndPoint()
        {
            uint test = 1;
            byte* test_b = (byte*)&test;
            IsLittleEndian = test_b[0] == 1;
        }

        /// <summary>
        /// Gets or sets port number of the endpoint.
        /// </summary>
        public ushort Port
        {
            get => (ushort)(rawNetworkAddress.port1 | (rawNetworkAddress.port0 << 8));
            set
            {
                rawNetworkAddress.port0 = (byte)((value >> 8) & 0xff);
                rawNetworkAddress.port1 = (byte)(value & 0xff);
            }
        }

        /// <summary>
        /// Gets or sets <see cref="NetworkFamily"/> of the endpoint.
        /// </summary>
        public NetworkFamily Family
        {
            get => FromBaselibFamily((Binding.Baselib_NetworkAddress_Family)rawNetworkAddress.family);
            set => rawNetworkAddress.family = (byte)ToBaselibFamily(value);
        }

        /// <summary>
        /// Gets the raw bytes for the endpoint.
        /// </summary>
        /// <returns>Native array containing the raw bytes (uses temporary allocation).</returns>
        public NativeArray<byte> GetRawAddressBytes()
        {
            var bytes = new NativeArray<byte>(Length, Allocator.Temp);
            UnsafeUtility.MemCpy(bytes.GetUnsafePtr(), UnsafeUtility.AddressOf(ref rawNetworkAddress), Length);
            return bytes;
        }

        /// <summary>
        /// Directly sets the raw bytes of the endpoint using the specified bytes and family.
        /// </summary>
        /// <param name="bytes">Raw bytes to use for the endpoint.</param>
        /// <param name="family">Endpoint's address family.</param>
        /// <exception cref="InvalidOperationException">Length of bytes doesn't match family.</exception>
        public void SetRawAddressBytes(NativeArray<byte> bytes, NetworkFamily family = NetworkFamily.Ipv4)
        {
            if ((family == NetworkFamily.Ipv4 && bytes.Length != rawIpv4Length) ||
                (family == NetworkFamily.Ipv6 && bytes.Length != rawIpv6Length))
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                throw new InvalidOperationException("Bad input length for given address family.");
#else
                UnityEngine.Debug.LogError("Bad input length for given address family.");
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
        /// Gets or sets the value of the raw port number.
        /// </summary>
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

        /// <summary>
        /// Gets the endpoint's representation as a <see cref="string">.
        /// </summary>
        public string Address => AddressAsString();

        /// <summary>
        /// Whether the endpoint is valid or not.
        /// </summary>
        public bool IsValid => Family != 0;

        /// <summary>
        /// Gets an IPv4 endpoint that can be used to bind to any address available (0.0.0.0:0).
        /// </summary>
        public static NetworkEndPoint AnyIpv4 => CreateAddress(0);
        
        /// <summary>
        /// Gets an IPv4 loopback endpoint (127.0.0.1:0).
        /// </summary>
        public static NetworkEndPoint LoopbackIpv4 => CreateAddress(0, AddressType.Loopback);

        /// <summary>
        /// Gets an IPv6 endpoint that can be used to bind to any address available ([::0]:0).
        /// </summary>
        public static NetworkEndPoint AnyIpv6 => CreateAddress(0, AddressType.Any, NetworkFamily.Ipv6);
        
        /// <summary>
        /// Gets an IPv6 loopback endpoint ([::1]:0).
        /// </summary>
        public static NetworkEndPoint LoopbackIpv6 => CreateAddress(0, AddressType.Loopback, NetworkFamily.Ipv6);

        /// <summary>
        /// Use the given port number for this endpoint.
        /// </summary>
        /// <param name="port">The port number.</param>
        /// <returns>The endpoint (this).</returns>
        public NetworkEndPoint WithPort(ushort port)
        {
            var ep = this;
            ep.Port = port;
            return ep;
        }

        /// <summary>
        /// Whether the endpoint is using a loopback address.
        /// </summary>
        public bool IsLoopback => (this == LoopbackIpv4.WithPort(Port)) || (this == LoopbackIpv6.WithPort(Port));
        
        /// <summary>
        /// Whether the endpoint is using an "any" address.
        /// </summary>
        public bool IsAny => (this == AnyIpv4.WithPort(Port)) || (this == AnyIpv6.WithPort(Port));

        /// <summary>
        /// Try to parse the given address and port into a new <see cref="NetworkEndPoint">.
        /// </summary>
        /// <param name="address">String representation of the address.</param>
        /// <param name="port">Port number.</param>
        /// <param name="endpoint">Return value for the parsed endpoint.</param>
        /// <param name="family">Address family of 'address'.</param>
        /// <returns>Whether the endpoint was successfully parsed or not.</returns>
        public static bool TryParse(string address, ushort port, out NetworkEndPoint endpoint, NetworkFamily family = NetworkFamily.Ipv4)
        {
            UnsafeUtility.SizeOf<Binding.Baselib_NetworkAddress>();
            endpoint = default;

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
        /// Same as <see cref="TryParse{T}">, except an endpoint is always returned. If the given
        /// address, port, and family don't represent a valid endpoint, the default one is returned.
        /// </summary>
        /// <param name="address">String representation of the address.</param>
        /// <param name="port">Return value for the parsed endpoint.</param>
        /// <param name="family">Address family of 'address'.</param>
        /// <returns>Parsed network endpoint (default if parsing failed).</returns>
        public static NetworkEndPoint Parse(string address, ushort port, NetworkFamily family = NetworkFamily.Ipv4)
        {
            if (TryParse(address, port, out var endpoint, family))
                return endpoint;

            return default;
        }

        public static bool operator==(NetworkEndPoint lhs, NetworkEndPoint rhs)
        {
            return lhs.Compare(rhs);
        }

        public static bool operator!=(NetworkEndPoint lhs, NetworkEndPoint rhs)
        {
            return !lhs.Compare(rhs);
        }
        
        public override bool Equals(object other)
        {
            return this == (NetworkEndPoint)other;
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
        
        bool Compare(NetworkEndPoint other)
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
                    break;
            }
            return str;
        }
        
        private string AddressAsString()
        {
            return AddressToString(ref rawNetworkAddress).ToString();
        }

        public override string ToString()
        {
            return AddressToString(ref rawNetworkAddress).ToString();
        }
        
        private static ushort ByteSwap(ushort val)
        {
            return (ushort)(((val & 0xff) << 8) | (val >> 8));
        }
        
        private static uint ByteSwap(uint val)
        {
            return (uint)(((val & 0xff) << 24) | ((val & 0xff00) << 8) | ((val >> 8) & 0xff00) | (val >> 24));
        }

        static NetworkEndPoint CreateAddress(ushort port, AddressType type = AddressType.Any, NetworkFamily family = NetworkFamily.Ipv4)
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

            var ep = new NetworkEndPoint
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

    /// <summary>
    /// Representation of an endpoint, to be used internally by <see cref="INetworkInterface">.
    /// </summary>
    public unsafe struct NetworkInterfaceEndPoint : IEquatable<NetworkInterfaceEndPoint>
    {
        /// <summary>
        /// Maximum length of the interface endpoint's raw representation.
        /// </summary>
        public const int k_MaxLength = 56;

        /// <summary>
        /// Actual length of the interface endpoint's raw representation.
        /// </summary>
        public int dataLength;
        
        /// <summary>
        /// Raw representation of the interface endpoint.
        /// </summary>
        public fixed byte data[k_MaxLength];

        /// <summary>
        /// Whether the interface endpoint is valid or not.
        /// </summary>
        public bool IsValid => dataLength != 0;

        public static bool operator==(NetworkInterfaceEndPoint lhs, NetworkInterfaceEndPoint rhs)
        {
            return lhs.Equals(rhs);
        }

        public static bool operator!=(NetworkInterfaceEndPoint lhs, NetworkInterfaceEndPoint rhs)
        {
            return !lhs.Equals(rhs);
        }
        
        public override bool Equals(object other)
        {
            return Equals((NetworkInterfaceEndPoint)other);
        }
        
        public override int GetHashCode()
        {
            fixed(byte* p = data)
            unchecked
            {
                var result = 0;

                for (int i = 0; i < dataLength; i++)
                {
                    result = (result * 31) ^ (int)p[i];
                }

                return result;
            }
        }
        
        public bool Equals(NetworkInterfaceEndPoint other)
        {
            // baselib doesn't return consistent lengths under posix, so lengths can
            // only be used as a shortcut if only one addresses a blank.
            if (dataLength != other.dataLength && (dataLength <= 0 || other.dataLength <= 0))
                return false;

            fixed(void* p = this.data)
            {
                return UnsafeUtility.MemCmp(p, other.data, math.min(dataLength, other.dataLength)) == 0;
            }
        }

        /// <summary>
        /// Returns the <see cref="NetworkInterfaceEndPoint"/> as a <see cref="FixedString64Bytes"/>.
        /// </summary>
        public FixedString64Bytes ToFixedString()
        {
            if (IsValid == false)
                return (FixedString64Bytes)"Not Valid";

            var n = dataLength;
            var res = new FixedString64Bytes();

            if (n == 4)
            {
                res.Append(data[0]);
                res.Append('.');
                res.Append(data[1]);
                res.Append('.');
                res.Append(data[2]);
                res.Append('.');
                res.Append(data[3]);

                return res;
            }

            res.Append((FixedString32Bytes)"0x");
            fixed(byte* p = this.data)
            {
                for (var i = 0; i < n; i += 2)
                {
                    var ushortP = (ushort*)(p + i);
                    res.AppendHex(*ushortP);
                }
            }
            return res;
        }

        /// <summary>
        /// Returns the <see cref="NetworkInterfaceEndPoint"/> as a <see cref="string">.
        /// </summary>
        public override string ToString()
        {
            return ToFixedString().ToString();
        }
    }
}
