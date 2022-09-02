#if ENABLE_MANAGED_UNITYTLS

using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Networking.Transport.Relay;
using Unity.TLS.LowLevel;

namespace Unity.Networking.Transport.TLS
{
    /// <summary>Secure transport protocols supported by UnityTLS.</summary>
    internal enum SecureTransportProtocol : uint
    {
        TLS = 0,
        DTLS = 1,
    }

    /// <summary>Utility containter for a UnityTLS configuration.</summary>
    internal unsafe struct UnityTLSConfiguration : IDisposable
    {
        private NativeReference<Binding.unitytls_client_config> m_Config;
        private NativeReference<UnityTLSCallbacks.CallbackContext> m_Callbacks;

        public Binding.unitytls_client_config* ConfigPtr => (Binding.unitytls_client_config*)m_Config.GetUnsafePtr();
        public UnityTLSCallbacks.CallbackContext* CallbackContextPtr => (UnityTLSCallbacks.CallbackContext*)m_Callbacks.GetUnsafePtr();

        // We need to store pointers into SecureNetworkProtocolParameter or RelayNetworkParameter
        // inside the UnityTLS configuration, so store them in native containers so that they'll
        // have a stable address.
        private NativeReference<SecureNetworkProtocolParameter> m_SecureParameters;
        private NativeReference<RelayNetworkParameter> m_RelayParameters;
        private NativeReference<FixedString32Bytes> m_RelayHostname;

        public bool IsCreated => m_Config.IsCreated;

        private static void InitializeFromSecureParameters(Binding.unitytls_client_config* config, ref SecureNetworkProtocolParameter parameters)
        {
            config->clientAuth = (uint)parameters.ClientAuthenticationPolicy;

            if (parameters.Hostname != default)
                config->hostname = parameters.Hostname.GetUnsafePtr();

            if (parameters.Pem != default)
            {
                config->caPEM = new Binding.unitytls_dataRef()
                {
                    dataPtr = parameters.Pem.GetUnsafePtr(),
                    dataLen = new UIntPtr((uint)parameters.Pem.Length)
                };
            }

            if (parameters.Rsa != default && parameters.RsaKey != default)
            {
                config->serverPEM = new Binding.unitytls_dataRef()
                {
                    dataPtr = parameters.Rsa.GetUnsafePtr(),
                    dataLen = new UIntPtr((uint)parameters.Rsa.Length)
                };

                config->privateKeyPEM = new Binding.unitytls_dataRef()
                {
                    dataPtr = parameters.RsaKey.GetUnsafePtr(),
                    dataLen = new UIntPtr((uint)parameters.RsaKey.Length)
                };
            }
        }

        private static void InitializeFromRelayParameters(Binding.unitytls_client_config* config, ref RelayNetworkParameter parameters)
        {
            // We don't set the protocol, client authentication policy, and different timeouts
            // because either their values are customized through SecureNetworkProtocolParameter,
            // or we want to use the defaults and then unitytls_client_init_config will have set
            // them appropriately for our needs.

            fixed (byte* hmacPtr = parameters.ServerData.HMACKey.Value)
            {
                config->psk = new Binding.unitytls_dataRef()
                {
                    dataPtr = hmacPtr,
                    dataLen = new UIntPtr(RelayHMACKey.k_Length)
                };
            }

            fixed (byte* allocPtr = parameters.ServerData.AllocationId.Value)
            {
                config->pskIdentity = new Binding.unitytls_dataRef()
                {
                    dataPtr = allocPtr,
                    dataLen = new UIntPtr(RelayAllocationId.k_Length)
                };
            }
        }

        public UnityTLSConfiguration(ref NetworkSettings settings, SecureTransportProtocol protocol, ushort mtu = 0)
        {
            UnityTLSCallbacks.Initialize();

            m_Config = new NativeReference<Binding.unitytls_client_config>(Allocator.Persistent);
            m_Callbacks = new NativeReference<UnityTLSCallbacks.CallbackContext>(Allocator.Persistent);

            m_SecureParameters = default;
            m_RelayParameters = default;
            m_RelayHostname = default;

            Binding.unitytls_client_init_config(ConfigPtr);

            if (settings.TryGet<SecureNetworkProtocolParameter>(out var secureParams))
            {
                m_SecureParameters = new NativeReference<SecureNetworkProtocolParameter>(Allocator.Persistent);

                // Can't just assign to value since SecureNetworkProtocolParameter is too big (on
                // Mono you can't pass parameters larger than 10K bytes as values to a property).
                var paramsPtr = (SecureNetworkProtocolParameter*)m_SecureParameters.GetUnsafePtr();
                *paramsPtr = secureParams;

                InitializeFromSecureParameters(ConfigPtr, ref UnsafeUtility.AsRef<SecureNetworkProtocolParameter>(paramsPtr));
            }

            if (settings.TryGet<RelayNetworkParameter>(out var relayParams))
            {
                // Relay authentication doesn't require a hostname, but UnityTLS will still try to
                // send an empty string if none is provided, which causes issues. See MTT-1753.
                FixedString32Bytes hostname = "relay";

                m_RelayParameters = new NativeReference<RelayNetworkParameter>(relayParams, Allocator.Persistent);
                m_RelayHostname = new NativeReference<FixedString32Bytes>(hostname, Allocator.Persistent);

                var paramsPtr = (RelayNetworkParameter*)m_RelayParameters.GetUnsafePtr();
                InitializeFromRelayParameters(ConfigPtr, ref UnsafeUtility.AsRef<RelayNetworkParameter>(paramsPtr));

                ConfigPtr->hostname = (byte*)m_RelayHostname.GetUnsafePtr();
            }

            var netConfig = settings.GetNetworkConfigParameters();
            ConfigPtr->ssl_handshake_timeout_min = (uint)netConfig.connectTimeoutMS;
            ConfigPtr->ssl_handshake_timeout_max = (uint)(netConfig.maxConnectAttempts * netConfig.connectTimeoutMS);

            ConfigPtr->transportProtocol = (uint)protocol;
            ConfigPtr->transportUserData = (IntPtr)CallbackContextPtr;

            ConfigPtr->dataSendCB = UnityTLSCallbacks.SendCallbackPtr;
            ConfigPtr->dataReceiveCB = UnityTLSCallbacks.ReceiveCallbackPtr;
            //ConfigPtr->logCallback = UnityTLSCallbacks.LogCallbackPtr;

            ConfigPtr->mtu = mtu;
        }

        public void Dispose()
        {
            if (IsCreated)
            {
                m_Config.Dispose();
                m_Callbacks.Dispose();
            }

            if (m_SecureParameters.IsCreated)
                m_SecureParameters.Dispose();

            if (m_RelayParameters.IsCreated)
                m_RelayParameters.Dispose();

            if (m_RelayHostname.IsCreated)
                m_RelayHostname.Dispose();
        }
    }
}

#endif