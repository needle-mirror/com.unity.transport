#if UNITY_2020_1_OR_NEWER
#define UNITY_TRANSPORT_ENABLE_BASELIB
#endif
using System;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
#if UNITY_TRANSPORT_ENABLE_BASELIB
using Unity.Baselib;
using Unity.Baselib.LowLevel;
#endif

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

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct NetworkEndPoint
    {
        enum AddressType { Any = 0, Loopback = 1 }
        private const int rawIpv4Length = 4;
        private const int rawIpv6Length = 16;
        private const int rawDataLength = 16;               // Maximum space needed to hold a IPv6 Address
        private const int rawLength = rawDataLength + 3;    // SizeOf<Baselib_NetworkAddress>
        private static readonly bool IsLittleEndian = true;

#if UNITY_TRANSPORT_ENABLE_BASELIB
        internal Binding.Baselib_NetworkAddress rawNetworkAddress;
#else
        [StructLayout(LayoutKind.Explicit)]
        public struct RawNetworkAddress
        {
            [FieldOffset(0)] public fixed byte data[19];
            [FieldOffset(0)] public fixed byte ipv6[16];
            [FieldOffset(0)] public fixed byte ipv4_bytes[4];
            [FieldOffset(0)] public uint ipv4;
            [FieldOffset(16)] public ushort port;
            [FieldOffset(18)] public byte family;
        }
        public RawNetworkAddress rawNetworkAddress;
#endif
        public int length;

        static NetworkEndPoint()
        {
            uint test = 1;
            byte* test_b = (byte*) &test;
            IsLittleEndian = test_b[0] == 1;
        }

#if UNITY_TRANSPORT_ENABLE_BASELIB
        public ushort Port
        {
            get => (ushort) (rawNetworkAddress.port1 | (rawNetworkAddress.port0 << 8));
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
#else
 public ushort Port
        {
            get => IsLittleEndian ? ByteSwap(rawNetworkAddress.port) : rawNetworkAddress.port;
            set => rawNetworkAddress.port = IsLittleEndian ? ByteSwap(value) : value;
        }

        public NetworkFamily Family
        {
            get => (NetworkFamily) rawNetworkAddress.family;
            set => rawNetworkAddress.family = (byte) value;
        }
#endif

        public NativeArray<byte> GetRawAddressBytes()
        {
            if (Family == NetworkFamily.Ipv4)
            {
                var bytes = new NativeArray<byte>(4, Allocator.Temp);
                UnsafeUtility.MemCpy(bytes.GetUnsafePtr(), UnsafeUtility.AddressOf(ref rawNetworkAddress), rawIpv4Length);
                return bytes;
            }
            else if (Family == NetworkFamily.Ipv6)
            {
                var bytes = new NativeArray<byte>(16, Allocator.Temp);
                UnsafeUtility.MemCpy(bytes.GetUnsafePtr(), UnsafeUtility.AddressOf(ref rawNetworkAddress), rawIpv6Length);
                return bytes;
            }
            return default;
        }

        public void SetRawAddressBytes(NativeArray<byte> bytes, NetworkFamily family = NetworkFamily.Ipv4)
        {
            if (family == NetworkFamily.Ipv4 && bytes.Length== rawIpv4Length)
            {
                UnsafeUtility.MemCpy(UnsafeUtility.AddressOf(ref rawNetworkAddress), bytes.GetUnsafeReadOnlyPtr(),  rawIpv4Length);
            }
            else if (family == NetworkFamily.Ipv6 && bytes.Length == rawIpv6Length)
            {
                UnsafeUtility.MemCpy(UnsafeUtility.AddressOf(ref rawNetworkAddress), bytes.GetUnsafeReadOnlyPtr(),  rawIpv6Length);
            }
            else
            {
                if (family == NetworkFamily.Ipv4 && bytes.Length != rawIpv4Length)
                    throw new InvalidOperationException("Bad input length, a ipv4 address is 4 bytes long");
                if (family == NetworkFamily.Ipv6 && bytes.Length == rawIpv6Length)
                    throw new InvalidOperationException("Bad input length, a ipv6 address is 16 bytes long");
            }
        }

        public ushort RawPort
        {
            get
            {
                ushort *port = (ushort*)((byte*) UnsafeUtility.AddressOf(ref rawNetworkAddress) + rawDataLength);
                return *port;
            }
            set
            {
                ushort *port = (ushort*)((byte*) UnsafeUtility.AddressOf(ref rawNetworkAddress) + rawDataLength);
                *port = value;
            }
        }

        public string Address => AddressAsString();

        public bool IsValid => Family != 0;

        public static NetworkEndPoint AnyIpv4 => CreateAddress(0);
        public static NetworkEndPoint LoopbackIpv4 => CreateAddress(0, AddressType.Loopback);

#if UNITY_TRANSPORT_ENABLE_BASELIB
        public static NetworkEndPoint AnyIpv6 => CreateAddress(0, AddressType.Any, NetworkFamily.Ipv6);
        public static NetworkEndPoint LoopbackIpv6 => CreateAddress(0, AddressType.Loopback, NetworkFamily.Ipv6);
#endif

        public NetworkEndPoint WithPort(ushort port)
        {
            var ep = this;
            ep.Port = port;
            return ep;
        }
#if UNITY_TRANSPORT_ENABLE_BASELIB
        public bool IsLoopback => (this == LoopbackIpv4.WithPort(Port)) || (this == LoopbackIpv6.WithPort(Port));
        public bool IsAny => (this == AnyIpv4.WithPort(Port)) || (this == AnyIpv6.WithPort(Port));
#else
        public bool IsLoopback => this == LoopbackIpv4.WithPort(Port);
        public bool IsAny => this == AnyIpv4.WithPort(Port);
#endif

        // Returns true if we can fully parse the input and return a valid endpoint
#if UNITY_TRANSPORT_ENABLE_BASELIB
        public static bool TryParse(string address, ushort port, out NetworkEndPoint endpoint, NetworkFamily family = NetworkFamily.Ipv4)
        {
            UnsafeUtility.SizeOf<Binding.Baselib_NetworkAddress>();
            endpoint = default(NetworkEndPoint);

            var errorState = default(ErrorState);
            var ipBytes = System.Text.Encoding.UTF8.GetBytes(address + char.MinValue);

            fixed (byte* ipBytesPtr = ipBytes)
            fixed (Binding.Baselib_NetworkAddress* rawAddress = &endpoint.rawNetworkAddress)
            {
                Binding.Baselib_NetworkAddress_Encode(
                    rawAddress,
                    ToBaselibFamily(family),
                    ipBytesPtr,
                    (ushort) port,
                    errorState.NativeErrorStatePtr);
            }

            if (errorState.ErrorCode != Binding.Baselib_ErrorCode.Success)
            {
                return false;
            }
            return endpoint.IsValid;
        }
#else
        public static bool TryParse(string address, ushort port, out NetworkEndPoint endpoint, NetworkFamily family = NetworkFamily.Ipv4)
        {
            endpoint = default(NetworkEndPoint);

            if (family != NetworkFamily.Ipv4)
                return false;
            // Parse failure check
            if (address == null || address.Length < 7)
                return false;

            uint ipaddr = 0;
            var pos = 0;

            // Parse characters
            for (var part = 0; part < 4; ++part)
            {
                // Parse failure check
                if (pos >= address.Length || address[pos] < '0' || address[pos] > '9')
                    return false;

                uint byteVal = 0;

                while (pos < address.Length && address[pos] >= '0' && address[pos] <= '9')
                {
                    byteVal = byteVal * 10 + (uint)(address[pos] - '0');
                    ++pos;
                }

                // Parse failure check
                if (byteVal > 255)
                    return false;

                ipaddr = (ipaddr << 8) | byteVal;

                if (pos < address.Length && address[pos] == '.')
                    ++pos;
            }

            if (pos + 1 < address.Length && address[pos] == ':')
            {
                ++pos;
                uint customPort = 0;
                while (pos < address.Length && address[pos] >= '0' && address[pos] <= '9')
                {
                    customPort = customPort * 10 + (uint)(address[pos] - '0');
                    ++pos;
                }

                if (customPort > ushort.MaxValue)
                    return false;

                port = (ushort)customPort;
            }

            endpoint = CreateAddress(port);
            endpoint.rawNetworkAddress.ipv4 = IsLittleEndian ? ByteSwap(ipaddr) : ipaddr;

            return endpoint.IsValid;
        }
#endif
        // Returns a default address if parsing fails
        public static NetworkEndPoint Parse(string address, ushort port, NetworkFamily family = NetworkFamily.Ipv4)
        {
            if (TryParse(address, port, out var endpoint, family))
                return endpoint;

            return default;
        }

        public static bool operator ==(NetworkEndPoint lhs, NetworkEndPoint rhs)
        {
            return lhs.Compare(rhs);
        }

        public static bool operator !=(NetworkEndPoint lhs, NetworkEndPoint rhs)
        {
            return !lhs.Compare(rhs);
        }

        public override bool Equals(object other)
        {
            return this == (NetworkEndPoint) other;
        }

        public override int GetHashCode()
        {
            var p = (byte*) UnsafeUtility.AddressOf(ref rawNetworkAddress);
            unchecked
            {
                var result = 0;

                for (int i = 0; i < rawLength; i++)
                {
                    result = (result * 31) ^ (int) (IntPtr) (p + 1);
                }

                return result;
            }
        }

        bool Compare(NetworkEndPoint other)
        {
            var p = (byte*) UnsafeUtility.AddressOf(ref rawNetworkAddress);
            var p1 = (byte*) UnsafeUtility.AddressOf(ref other.rawNetworkAddress);
            if (UnsafeUtility.MemCmp(p, p1, rawLength) == 0)
                return true;

            return false;
        }

        private string AddressAsString()
        {
            return string.Empty;
        }

        private static ushort ByteSwap(ushort val)
        {
            return (ushort) (((val & 0xff) << 8) | (val >> 8));
        }

        private static uint ByteSwap(uint val)
        {
            return (uint) (((val & 0xff) << 24) | ((val & 0xff00) << 8) | ((val >> 8) & 0xff00) | (val >> 24));
        }

        static NetworkEndPoint CreateAddress(ushort port, AddressType type = AddressType.Any, NetworkFamily family = NetworkFamily.Ipv4)
        {
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
                RawPort = port,
                length = rawLength
            };

            if (type == AddressType.Loopback)
            {
                if (family == NetworkFamily.Ipv4)
                {
                    *(uint*) UnsafeUtility.AddressOf(ref ep.rawNetworkAddress) = ipv4Loopback;
                }
#if UNITY_TRANSPORT_ENABLE_BASELIB
                else if (family == NetworkFamily.Ipv6)
                {
                    ep.rawNetworkAddress.data15 = 1;
                }
#endif
            }
            return ep;
        }

#if UNITY_TRANSPORT_ENABLE_BASELIB
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
#endif
    }

    public unsafe struct NetworkInterfaceEndPoint
    {
        public int dataLength;
        public fixed byte data[56];

        public bool IsValid => dataLength != 0;

        public static bool operator ==(NetworkInterfaceEndPoint lhs, NetworkInterfaceEndPoint rhs)
        {
            return lhs.Compare(rhs);
        }

        public static bool operator !=(NetworkInterfaceEndPoint lhs, NetworkInterfaceEndPoint rhs)
        {
            return !lhs.Compare(rhs);
        }

        public override bool Equals(object other)
        {
            return this == (NetworkInterfaceEndPoint) other;
        }

        public override int GetHashCode()
        {
            fixed (byte* p = data)
                unchecked
                {
                    var result = 0;

                    for (int i = 0; i < dataLength; i++)
                    {
                        result = (result * 31) ^ (int)(IntPtr) (p + 1);
                    }

                    return result;
                }
        }

        bool Compare(NetworkInterfaceEndPoint other)
        {
// Workaround for baselib issue on Posix
            if (dataLength != other.dataLength)
            {
#if UNITY_TRANSPORT_ENABLE_BASELIB
                if (dataLength <= 0 || other.dataLength <= 0)
                    return false;
#else
                return false;
#endif
            }
            fixed (void* p = this.data)
            {
                if (UnsafeUtility.MemCmp(p, other.data, math.min(dataLength, other.dataLength)) == 0)
                    return true;
            }

            return false;
        }
    }
}