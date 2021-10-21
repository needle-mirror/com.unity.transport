using System;
using System.Diagnostics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Unity.Networking.Transport.Relay
{
    /// <summary>
    /// Used by the Relay Protocol to describe how to connect to the Relay Service.
    /// </summary>
    public unsafe struct RelayServerData
    {
        /// <summary>
        /// The endpoint of the Relay Server.
        /// </summary>
        public NetworkEndPoint Endpoint;
        /// <summary>
        /// The Nonce value used to stablish the connection with the Relay Server.
        /// </summary>
        public ushort Nonce;
        /// <summary>
        /// The data that describes the client presence on the Relay Server.
        /// </summary>
        public RelayConnectionData ConnectionData;
        /// <summary>
        /// The connection data of the host client on the Relay Server.
        /// </summary>
        public RelayConnectionData HostConnectionData;
        /// <summary>
        /// The unique identifier of the client on the Relay Server.
        /// </summary>
        public RelayAllocationId AllocationId;
        /// <summary>
        /// The HMAC key for the connection.
        /// </summary>
        public RelayHMACKey HMACKey;
        /// <summary>
        /// The computed HMAC.
        /// </summary>
        public fixed byte HMAC[32]; // TODO: this shouldn't be here and should be computed on connection binding but today it's not Burst compatible.
        /// <summary>
        /// A byte that identifies the connection as secured.
        /// </summary>
        public readonly byte IsSecure;

        /// <summary>
        /// Initializes a new instance of the <see cref="RelayServerData"/> class
        /// </summary>
        /// <param name="endpoint">The endpoint</param>
        /// <param name="nonce">The nonce</param>
        /// <param name="allocationId">The allocation id</param>
        /// <param name="connectionData">The connection data</param>
        /// <param name="hostConnectionData">The host connection data</param>
        /// <param name="key">The key</param>
        /// <param name="isSecure">The is secure</param>
        public RelayServerData(ref NetworkEndPoint endpoint, ushort nonce, RelayAllocationId allocationId, string connectionData, string hostConnectionData, string key, bool isSecure)
        {
            Endpoint = endpoint;
            AllocationId = allocationId;
            Nonce = nonce;

            IsSecure = isSecure ? (byte)1 : (byte)0;

            fixed(byte* connPtr = ConnectionData.Value)
            fixed(byte* hostPtr = HostConnectionData.Value)
            fixed(byte* keyPtr = HMACKey.Value)
            {
                Base64.FromBase64String(connectionData, connPtr, RelayConnectionData.k_Length);
                Base64.FromBase64String(hostConnectionData, hostPtr, RelayConnectionData.k_Length);
                Base64.FromBase64String(key, keyPtr, RelayHMACKey.k_Length);
            }

            fixed(byte* hmacPtr = HMAC)
            {
                ComputeBindHMAC(hmacPtr, Nonce, ref ConnectionData, ref HMACKey);
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="RelayServerData"/> class
        /// </summary>
        /// <param name="endpoint">The endpoint</param>
        /// <param name="nonce">The nonce</param>
        /// <param name="allocationId">The allocation id</param>
        /// <param name="connectionData">The connection data</param>
        /// <param name="hostConnectionData">The host connection data</param>
        /// <param name="key">The key</param>
        /// <param name="isSecure">The is secure</param>
        public RelayServerData(ref NetworkEndPoint endpoint, ushort nonce, ref RelayAllocationId allocationId,
                               ref RelayConnectionData connectionData, ref RelayConnectionData hostConnectionData, ref RelayHMACKey key, bool isSecure)
        {
            Endpoint = endpoint;
            Nonce = nonce;
            AllocationId = allocationId;
            ConnectionData = connectionData;
            HostConnectionData = hostConnectionData;
            HMACKey = key;

            IsSecure = isSecure ? (byte)1 : (byte)0;

            fixed(byte* hmacPtr = HMAC)
            {
                ComputeBindHMAC(hmacPtr, Nonce, ref connectionData, ref key);
            }
        }

        /// <summary>
        /// Computes the new nonce, this must be called one time!
        /// </summary>
        public void ComputeNewNonce()
        {
            Nonce = (ushort)(new Unity.Mathematics.Random((uint)Stopwatch.GetTimestamp())).NextUInt(1, 0xefff);

            fixed(byte* hmacPtr = HMAC)
            {
                ComputeBindHMAC(hmacPtr, Nonce, ref ConnectionData, ref HMACKey);
            }
        }

        /// <summary>
        /// Computes the bind hmac using the specified result
        /// </summary>
        /// <param name="result">The result</param>
        /// <param name="nonce">The nonce</param>
        /// <param name="connectionData">The connection data</param>
        /// <param name="key">The key</param>
        private static void ComputeBindHMAC(byte* result, ushort nonce, ref RelayConnectionData connectionData, ref RelayHMACKey key)
        {
            var keyArray = new byte[64];

            fixed(byte* keyValue = &key.Value[0])
            {
                fixed(byte* keyArrayPtr = &keyArray[0])
                {
                    UnsafeUtility.MemCpy(keyArrayPtr, keyValue, keyArray.Length);
                }

                const int messageLength = 263;

                var messageBytes = stackalloc byte[messageLength];

                messageBytes[0] = 0xDA;
                messageBytes[1] = 0x72;
                // ... zeros
                messageBytes[5] = (byte)nonce;
                messageBytes[6] = (byte)(nonce >> 8);
                messageBytes[7] = 255;

                fixed(byte* connValue = &connectionData.Value[0])
                {
                    UnsafeUtility.MemCpy(messageBytes + 8, connValue, 255);
                }

                HMACSHA256.ComputeHash(keyValue, keyArray.Length, messageBytes, messageLength, result);
            }
        }
    }
}
