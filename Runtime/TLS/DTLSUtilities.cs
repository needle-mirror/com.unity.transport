using System.Runtime.CompilerServices;
using Unity.Collections;

namespace Unity.Networking.Transport.TLS
{
    /// <summary>Utility functions used by the DTLS or TLS layer.</summary>
    internal unsafe static class DTLSUtilities
    {
        /// <summary>Check if a DTLS message is a Client Hello message.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool IsClientHello(ref PacketProcessor packetProcessor)
        {
            // A DTLS message is a client hello if it's content type (offset 0 in the header) has
            // value 0x16 and if the handshake type (offset 13 in the header) has value 0x01.
            // Furthermore, any valid client hello will be at least 25 bytes long.

            var ptr = (byte*)packetProcessor.GetUnsafePayloadPtr() + packetProcessor.Offset;
            var size = packetProcessor.Length;

            return size >= 25 && ptr[0] == (byte)0x16 && ptr[13] == (byte)0x01;
        }

        /// <summary>Check if a DTLS message is a Server Hello message.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool IsServerHello(ref PacketProcessor packetProcessor)
        {
            // A DTLS message is a server hello if it's content type (offset 0 in the header) has
            // value 0x16 and if the handshake type (offset 13 in the header) has value 0x02.
            // Furthermore, any valid server hello will be at least 25 bytes long.

            var ptr = (byte*)packetProcessor.GetUnsafePayloadPtr() + packetProcessor.Offset;
            var size = packetProcessor.Length;

            return size >= 25 && ptr[0] == (byte)0x16 && ptr[13] == (byte)0x02;
        }

        /// <summary>Check if a DTLS message is a Hello Verify Request message.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool IsHelloVerifyRequest(ref PacketProcessor packetProcessor)
        {
            // A DTLS message is a hello verify if it's content type (offset 0 in the header) has
            // value 0x16 and if the handshake type (offset 13 in the header) has value 0x03.
            // Furthermore, any valid hello verify request will be at least 25 bytes long.

            var ptr = (byte*)packetProcessor.GetUnsafePayloadPtr() + packetProcessor.Offset;
            var size = packetProcessor.Length;

            return size >= 25 && ptr[0] == (byte)0x16 && ptr[13] == (byte)0x03;
        }

        /// <summary>Check if a DTLS message is an alert message.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool IsAlert(ref PacketProcessor packetProcessor)
        {
            // Alerts have a content type (offset 0 in the header) of 0x15.

            var ptr = (byte*)packetProcessor.GetUnsafePayloadPtr() + packetProcessor.Offset;
            var size = packetProcessor.Length;

            return size >= 1 && ptr[0] == (byte)0x15;
        }

        /// <summary>Describe the TLS/DTLS handshake step.</summary>
        internal static FixedString128Bytes DescribeHandshakeStep(uint step)
        {
            return step switch
            {
                0 =>  "HELLO_REQUEST",
                1 =>  "CLIENT_HELLO - failure at this step likely means the server is rejecting secure connections",
                2 =>  "SERVER_HELLO",
                3 =>  "SERVER_CERTIFICATE - failure at this step likely means the server has a misconfigured certificate",
                4 =>  "SERVER_KEY_EXCHANGE",
                5 =>  "CERTIFICATE_REQUEST",
                6 =>  "SERVER_HELLO_DONE",
                7 =>  "CLIENT_CERTIFICATE",
                8 =>  "CLIENT_KEY_EXCHANGE - failure at this step likely means the client couldn't verify the server's certificate",
                9 =>  "CERTIFICATE_VERIFY",
                10 => "CLIENT_CHANGE_CIPHER_SPEC",
                11 => "CLIENT_FINISHED",
                12 => "SERVER_CHANGE_CIPHER_SPEC",
                13 => "SERVER_FINISHED",
                14 => "FLUSH_BUFFERS",
                15 => "WRAPUP",
                16 => "SERVER_NEW_SESSION_TICKET",
                17 => "HELLO_VERIFY_REQUIRED",
                _ =>  "unknown step",
            };
        }
    }
}