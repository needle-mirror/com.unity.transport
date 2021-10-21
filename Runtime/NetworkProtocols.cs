using System;
using System.Runtime.InteropServices;

namespace Unity.Networking.Transport.Protocols
{
    internal enum UdpCProtocol
    {
        ConnectionRequest,
        ConnectionReject,
        ConnectionAccept,
        Disconnect,
        Data,
        Ping,
        Pong,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct UdpCHeader
    {
        [Flags]
        public enum HeaderFlags : byte
        {
            HasConnectToken = 0x1,
            HasPipeline = 0x2
        }

        public const int Length = 2 + SessionIdToken.k_Length;  //explanation of constant 2 in this expression = sizeof(Type) + sizeof(HeaderFlags)
        public byte Type;
        public HeaderFlags Flags;
        public SessionIdToken SessionToken;
    }
}
