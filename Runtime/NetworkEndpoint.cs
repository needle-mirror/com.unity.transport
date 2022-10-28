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
    /// NetworkFamily indicates what type of underlying medium we are using.
    /// </summary>
    public enum NetworkFamily
    {
        Invalid = 0,
        Ipv4 = 2,
        Ipv6 = 23
    }

    [Obsolete("NetworkEndPoint has been renamed to NetworkEndpoint. (UnityUpgradable) -> NetworkEndpoint", true)]
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public struct NetworkEndPoint {}

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

        public ushort Port
        {
            get => (ushort)(rawNetworkAddress.port1 | (rawNetworkAddress.port0 << 8));
            set
            {
                rawNetworkAddress.port0 = (byte)((value >> 8) & 0xff);
                rawNetworkAddress.port1 = (byte)(value & 0xff);
            }
        }

        public NetworkFamily Family
        {
            get => FromBaselibFamily((Binding.Baselib_NetworkAddress_Family)rawNetworkAddress.family);
            set => rawNetworkAddress.family = (byte)ToBaselibFamily(value);
        }

        public NativeArray<byte> GetRawAddressBytes()
        {
            var bytes = new NativeArray<byte>(Length, Allocator.Temp);
            UnsafeUtility.MemCpy(bytes.GetUnsafePtr(), UnsafeUtility.AddressOf(ref rawNetworkAddress), Length);
            return bytes;
        }

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

        public string Address => ToString();

        public bool IsValid => Family != 0;

        public static NetworkEndpoint AnyIpv4 => CreateAddress(0);
        public static NetworkEndpoint LoopbackIpv4 => CreateAddress(0, AddressType.Loopback);

        public static NetworkEndpoint AnyIpv6 => CreateAddress(0, AddressType.Any, NetworkFamily.Ipv6);
        public static NetworkEndpoint LoopbackIpv6 => CreateAddress(0, AddressType.Loopback, NetworkFamily.Ipv6);

        public NetworkEndpoint WithPort(ushort port)
        {
            var ep = this;
            ep.Port = port;
            return ep;
        }

        public bool IsLoopback => (this == LoopbackIpv4.WithPort(Port)) || (this == LoopbackIpv6.WithPort(Port));
        public bool IsAny => (this == AnyIpv4.WithPort(Port)) || (this == AnyIpv6.WithPort(Port));

        // Returns true if we can fully parse the input and return a valid endpoint
        public static bool TryParse(string address, ushort port, out NetworkEndpoint endpoint, NetworkFamily family = NetworkFamily.Ipv4)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (family == NetworkFamily.Ipv6)
            {
#if UNITY_SWITCH
                throw new ArgumentException("IPv6 is not supported on Switch.");
#elif (UNITY_PS4 || UNITY_PS5)
                throw new ArgumentException("IPv6 is not supported on PlayStation platforms.");
#endif
            }
#endif // ENABLE_UNITY_COLLECTIONS_CHECKS

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

        // Returns a default address if parsing fails
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
                    break;
            }
            return str;
        }

        public override string ToString()
        {
            return ToFixedString().ToString();
        }

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

    [Obsolete("Use NetworkEndpoint instead", true)]
    public struct NetworkInterfaceEndPoint : IEquatable<NetworkInterfaceEndPoint>
    {
        public bool Equals(NetworkInterfaceEndPoint other)
        {
            throw new NotImplementedException();
        }
    }
}
