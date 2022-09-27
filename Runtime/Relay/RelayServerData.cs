using System;
using System.Linq;
using System.Diagnostics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

#if RELAY_SDK_INSTALLED
using Unity.Services.Relay.Models;
#endif

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

        // Common code of all byte array-based constructors.
        private RelayServerData(byte[] allocationId, byte[] connectionData, byte[] hostConnectionData, byte[] key)
        {
            Nonce = 0;
            AllocationId = RelayAllocationId.FromByteArray(allocationId);
            ConnectionData = RelayConnectionData.FromByteArray(connectionData);
            HostConnectionData = RelayConnectionData.FromByteArray(hostConnectionData);
            HMACKey = RelayHMACKey.FromByteArray(key);

            // Assign temporary values to those. Chained constructors will set them.
            Endpoint = default;
            IsSecure = 0;

            fixed(byte* hmacPtr = HMAC)
            {
                ComputeBindHMAC(hmacPtr, Nonce, ref ConnectionData, ref HMACKey);
            }
        }

#if RELAY_SDK_INSTALLED
        /// <summary>Create a new Relay server data structure from an allocation.</summary>
        /// <param name="allocation">Allocation from which to create the server data.</param>
        /// <param name="connectionType">Type of connection to use ("udp", "dtls", "ws", or "wss").</param>
        public RelayServerData(Allocation allocation, string connectionType)
            : this(allocation.AllocationIdBytes, allocation.ConnectionData, allocation.ConnectionData, allocation.Key)
        {
            // We check against a hardcoded list of strings instead of just trying to find the
            // connection type in the endpoints since it may contains things we don't support
            // (e.g. they provide a "tcp" endpoint which we don't support).
            var supportedConnectionTypes = new string[] { "udp", "dtls" };
            if (!supportedConnectionTypes.Contains(connectionType))
                throw new ArgumentException($"Invalid connection type: {connectionType}. Must be udp or dtls.");

            var serverEndpoint = allocation.ServerEndpoints.First(ep => ep.ConnectionType == connectionType);

            Endpoint = HostToEndpoint(serverEndpoint.Host, (ushort)serverEndpoint.Port);
            IsSecure = serverEndpoint.Secure ? (byte)1 : (byte)0;
        }

        /// <summary>Create a new Relay server data structure from a join allocation.</summary>
        /// <param name="allocation">Allocation from which to create the server data.</param>
        /// <param name="connectionType">Type of connection to use ("udp", "dtls", "ws", or "wss").</param>
        public RelayServerData(JoinAllocation allocation, string connectionType)
            : this(allocation.AllocationIdBytes, allocation.ConnectionData, allocation.HostConnectionData, allocation.Key)
        {
            // We check against a hardcoded list of strings instead of just trying to find the
            // connection type in the endpoints since it may contains things we don't support
            // (e.g. they provide a "tcp" endpoint which we don't support).
            var supportedConnectionTypes = new string[] { "udp", "dtls" };
            if (!supportedConnectionTypes.Contains(connectionType))
                throw new ArgumentException($"Invalid connection type: {connectionType}. Must be udp, or dtls.");

            var serverEndpoint = allocation.ServerEndpoints.First(ep => ep.ConnectionType == connectionType);

            Endpoint = HostToEndpoint(serverEndpoint.Host, (ushort)serverEndpoint.Port);
            IsSecure = serverEndpoint.Secure ? (byte)1 : (byte)0;
        }
#endif

        /// <summary>Create a new Relay server data structure.</summary>
        /// <param name="host">IP address of the Relay server.</param>
        /// <param name="port">Port of the Relay server.</param>
        /// <param name="allocationId">ID of the Relay allocation.</param>
        /// <param name="connectionData">Connection data of the allocation.</param>
        /// <param name="hostConnectionData">Connection data of the host (same as previous for hosts).</param>
        /// <param name="key">HMAC signature of the allocation.</param>
        /// <param name="isSecure">Whether the Relay connection is to be secured or not.</param>
        public RelayServerData(string host, ushort port, byte[] allocationId, byte[] connectionData,
            byte[] hostConnectionData, byte[] key, bool isSecure)
            : this(allocationId, connectionData, hostConnectionData, key)
        {
            Endpoint = HostToEndpoint(host, port);
            IsSecure = isSecure ? (byte)1 : (byte)0;
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
        [Obsolete("Will be removed in Unity Transport 2.0. Use the new constructor introduced in 1.3 instead.", false)]
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
        [Obsolete("Will be removed in Unity Transport 2.0. There shouldn't be any need to call this method.")]
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

        private static NetworkEndPoint HostToEndpoint(string host, ushort port)
        {
            NetworkEndPoint endpoint;

            if (NetworkEndPoint.TryParse(host, port, out endpoint, NetworkFamily.Ipv4))
                return endpoint;

            if (NetworkEndPoint.TryParse(host, port, out endpoint, NetworkFamily.Ipv6))
                return endpoint;

            UnityEngine.Debug.LogError($"Host {host} is not a valid IPv4 or IPv6 address.");
            return endpoint;
        }
    }
}
