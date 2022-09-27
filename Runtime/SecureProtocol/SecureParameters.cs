#if ENABLE_MANAGED_UNITYTLS

using System;
using Unity.Collections;
using Unity.Networking.Transport;

namespace Unity.Networking.Transport.TLS
{
    /// <summary>Secure transport protocols.</summary>
    public enum SecureTransportProtocol : uint
    {
        /// <summary>Standard TLS implementation for TCP connections.</summary>
        TLS = 0,
        /// <summary>Standard TLS implementation for UDP connections.</summary>
        DTLS = 1,
    }

    /// <summary>Client authentication policies (server only).</summary>
    public enum SecureClientAuthPolicy : uint
    {
        /// <summary>Client certificate is not requested (thus not verified).</summary>
        None = 0,
        /// <summary>Client certificate is requested, but not verified.</summary>
        Optional = 1,
        /// <summary>Client certificate is requested and verified.</summary>
        Required = 2,
    }

    /// <summary>
    /// Settings used to configure the secure protocol implementation.
    /// </summary>
    public struct SecureNetworkProtocolParameter : INetworkParameter
    {
        /// <summary>Root CA certificate (PEM format).</summary>
        public FixedString4096Bytes                 Pem;
        /// <summary>Server/client certificate (PEM format).</summary>
        public FixedString4096Bytes                 Rsa;
        /// <summary>Server/client private key (PEM format).</summary>
        public FixedString4096Bytes                 RsaKey;
        /// <summary>Server/client certificate's common name.</summary>
        public FixedString32Bytes                   Hostname;
        /// <summary>Protocol to which the settings will apply.</summary>
        public SecureTransportProtocol              Protocol;
        /// <summary>Client authentication policy (server only, defaults to optional).</summary>
        public SecureClientAuthPolicy               ClientAuthenticationPolicy;
        /// <summary>Timeout in milliseconds for secure reads.</summary>
        public uint                                 SSLReadTimeoutMs;
        /// <summary>Maximum secure handshake timeout (milliseconds, defaults to 60000).</summary>
        public uint                                 SSLHandshakeTimeoutMax;
        /// <summary>Minimum secure handshake timeout (milliseconds, defaults to 1000).</summary>
        public uint                                 SSLHandshakeTimeoutMin;

        public bool Validate() => true;
    }

    public static class SecureParameterExtensions
    {
        /// <summary>Set client security parameters (for use with official CA certificates).</summary>
        /// <param name="serverName">Hostname of the server to connect to.</param>
        /// <param name="readTimeout">Secure read timeout (in milliseconds).</param>
        /// <param name="handshakeTimeoutMax">Maximum handshake timeout (in milliseconds).</param>
        /// <param name="handshakeTimeoutMin">Minimum handshake timeout (in milliseconds).</param>
        public static ref NetworkSettings WithSecureClientParameters(
            ref this NetworkSettings    settings,
            ref FixedString32Bytes     serverName,
            uint                        readTimeout                  = 0,
            uint                        handshakeTimeoutMax          = 60000,
            uint                        handshakeTimeoutMin          = 1000)
        {
            var parameter = new SecureNetworkProtocolParameter
            {
                Pem                         = default,
                Rsa                         = default,
                RsaKey                      = default,
                Hostname                    = serverName,
                Protocol                    = SecureTransportProtocol.DTLS,
                ClientAuthenticationPolicy  = SecureClientAuthPolicy.None,
                SSLReadTimeoutMs            = readTimeout,
                SSLHandshakeTimeoutMax      = handshakeTimeoutMax,
                SSLHandshakeTimeoutMin      = handshakeTimeoutMin,
            };

            settings.AddRawParameterStruct(ref parameter);

            return ref settings;
        }

        /// <summary>Set client security parameters (for use with official CA certificates).</summary>
        /// <param name="serverName">Hostname of the server to connect to.</param>
        public static ref NetworkSettings WithSecureClientParameters(
            ref this NetworkSettings    settings,
            string                      serverName)
        {
            var fixedServerName = new FixedString32Bytes(serverName);

            settings.WithSecureClientParameters(ref fixedServerName);

            return ref settings;
        }

        /// <summary>Set client security parameters (server authentication only).</summary>
        /// <param name="caCertificate">CA certificate that signed the server's certificate (PEM format).</param>
        /// <param name="serverName">Common name (CN) in the server certificate.</param>
        /// <param name="readTimeout">Secure read timeout (in milliseconds).</param>
        /// <param name="handshakeTimeoutMax">Maximum handshake timeout (in milliseconds).</param>
        /// <param name="handshakeTimeoutMin">Minimum handshake timeout (in milliseconds).</param>
        public static ref NetworkSettings WithSecureClientParameters(
            ref this NetworkSettings    settings,
            ref FixedString4096Bytes    caCertificate,
            ref FixedString32Bytes      serverName,
            uint                        readTimeout                  = 0,
            uint                        handshakeTimeoutMax          = 60000,
            uint                        handshakeTimeoutMin          = 1000
        )
        {
            var parameter = new SecureNetworkProtocolParameter
            {
                Pem                         = caCertificate,
                Rsa                         = default,
                RsaKey                      = default,
                Hostname                    = serverName,
                Protocol                    = SecureTransportProtocol.DTLS,
                ClientAuthenticationPolicy  = SecureClientAuthPolicy.None,
                SSLReadTimeoutMs            = readTimeout,
                SSLHandshakeTimeoutMax      = handshakeTimeoutMax,
                SSLHandshakeTimeoutMin      = handshakeTimeoutMin,
            };

            settings.AddRawParameterStruct(ref parameter);

            return ref settings;
        }

        /// <summary>Set client security parameters (server authentication only).</summary>
        /// <param name="caCertificate">CA certificate that signed the server's certificate (PEM format).</param>
        /// <param name="serverName">Common name (CN) in the server certificate.</param>
        public static ref NetworkSettings WithSecureClientParameters(
            ref this NetworkSettings    settings,
            string                      caCertificate,
            string                      serverName)
        {
            var fixedCaCertificate = new FixedString4096Bytes(caCertificate);
            var fixedServerName = new FixedString32Bytes(serverName);

            settings.WithSecureClientParameters(ref fixedCaCertificate, ref fixedServerName);

            return ref settings;
        }

        /// <summary>Set client security parameters (for client authentication).</summary>
        /// <param name="certificate">Client's certificate (PEM format).</param>
        /// <param name="privateKey">Client's private key (PEM format).</param>
        /// <param name="caCertificate">CA certificate that signed the server's certificate (PEM format).</param>
        /// <param name="serverName">Common name (CN) in the server certificate.</param>
        /// <param name="readTimeout">Secure read timeout (in milliseconds).</param>
        /// <param name="handshakeTimeoutMax">Maximum handshake timeout (in milliseconds).</param>
        /// <param name="handshakeTimeoutMin">Minimum handshake timeout (in milliseconds).</param>
        public static ref NetworkSettings WithSecureClientParameters(
            ref this NetworkSettings    settings,
            ref FixedString4096Bytes    certificate,
            ref FixedString4096Bytes    privateKey,
            ref FixedString4096Bytes    caCertificate,
            ref FixedString32Bytes      serverName,
            uint                        readTimeout                  = 0,
            uint                        handshakeTimeoutMax          = 60000,
            uint                        handshakeTimeoutMin          = 1000
        )
        {
            var parameter = new SecureNetworkProtocolParameter
            {
                Pem                         = caCertificate,
                Rsa                         = certificate,
                RsaKey                      = privateKey,
                Hostname                    = serverName,
                Protocol                    = SecureTransportProtocol.DTLS,
                ClientAuthenticationPolicy  = SecureClientAuthPolicy.None,
                SSLReadTimeoutMs            = readTimeout,
                SSLHandshakeTimeoutMax      = handshakeTimeoutMax,
                SSLHandshakeTimeoutMin      = handshakeTimeoutMin,
            };

            settings.AddRawParameterStruct(ref parameter);

            return ref settings;
        }

        /// <summary>Set client security parameters (for client authentication).</summary>
        /// <param name="certificate">Client's certificate (PEM format).</param>
        /// <param name="privateKey">Client's private key (PEM format).</param>
        /// <param name="caCertificate">CA certificate that signed the server's certificate (PEM format).</param>
        /// <param name="serverName">Common name (CN) in the server certificate.</param>
        public static ref NetworkSettings WithSecureClientParameters(
            ref this NetworkSettings    settings,
            string                      certificate,
            string                      privateKey,
            string                      caCertificate,
            string                      serverName)
        {
            var fixedCertificate = new FixedString4096Bytes(certificate);
            var fixedPrivateKey = new FixedString4096Bytes(privateKey);
            var fixedCaCertificate = new FixedString4096Bytes(caCertificate);
            var fixedServerName = new FixedString32Bytes(serverName);

            settings.WithSecureClientParameters(
                ref fixedCertificate,
                ref fixedPrivateKey,
                ref fixedCaCertificate,
                ref fixedServerName);

            return ref settings;
        }

        /// <summary>Set server security parameters (server authentication only).</summary>
        /// <param name="certificate">Server's certificate chain (PEM format).</param>
        /// <param name="privateKey">Server's private key (PEM format).</param>
        /// <param name="readTimeout">Secure read timeout (in milliseconds).</param>
        /// <param name="handshakeTimeoutMax">Maximum handshake timeout (in milliseconds).</param>
        /// <param name="handshakeTimeoutMin">Minimum handshake timeout (in milliseconds).</param>
        public static ref NetworkSettings WithSecureServerParameters(
            ref this NetworkSettings    settings,
            ref FixedString4096Bytes    certificate,
            ref FixedString4096Bytes    privateKey,
            uint                        readTimeout                  = 0,
            uint                        handshakeTimeoutMax          = 60000,
            uint                        handshakeTimeoutMin          = 1000
        )
        {
            var parameter = new SecureNetworkProtocolParameter
            {
                Pem                         = default,
                Rsa                         = certificate,
                RsaKey                      = privateKey,
                Hostname                    = default,
                Protocol                    = SecureTransportProtocol.DTLS,
                ClientAuthenticationPolicy  = SecureClientAuthPolicy.None,
                SSLReadTimeoutMs            = readTimeout,
                SSLHandshakeTimeoutMax      = handshakeTimeoutMax,
                SSLHandshakeTimeoutMin      = handshakeTimeoutMin,
            };

            settings.AddRawParameterStruct(ref parameter);

            return ref settings;
        }

        /// <summary>Set server security parameters (server authentication only).</summary>
        /// <param name="certificate">Server's certificate chain (PEM format).</param>
        /// <param name="privateKey">Server's private key (PEM format).</param>
        public static ref NetworkSettings WithSecureServerParameters(
            ref this NetworkSettings    settings,
            string                      certificate,
            string                      privateKey)
        {
            var fixedCertificate = new FixedString4096Bytes(certificate);
            var fixedPrivateKey = new FixedString4096Bytes(privateKey);

            settings.WithSecureServerParameters(ref fixedCertificate, ref fixedPrivateKey);

            return ref settings;
        }

        /// <summary>Set server security parameters (for client authentication).</summary>
        /// <param name="certificate">Server's certificate chain (PEM format).</param>
        /// <param name="privateKey">Server's private key (PEM format).</param>
        /// <param name="caCertificate">CA certificate that signed the client certificates (PEM format).</param>
        /// <param name="clientName">Common name (CN) in the client certificates.</param>
        /// <param name="clientAuthenticationPolicy">Client authentication policy.</param>
        /// <param name="readTimeout">Secure read timeout (in milliseconds).</param>
        /// <param name="handshakeTimeoutMax">Maximum handshake timeout (in milliseconds).</param>
        /// <param name="handshakeTimeoutMin">Minimum handshake timeout (in milliseconds).</param>
        public static ref NetworkSettings WithSecureServerParameters(
            ref this NetworkSettings    settings,
            ref FixedString4096Bytes    certificate,
            ref FixedString4096Bytes    privateKey,
            ref FixedString4096Bytes    caCertificate,
            ref FixedString32Bytes      clientName,
            SecureClientAuthPolicy      clientAuthenticationPolicy   = SecureClientAuthPolicy.Required,
            uint                        readTimeout                  = 0,
            uint                        handshakeTimeoutMax          = 60000,
            uint                        handshakeTimeoutMin          = 1000
        )
        {
            var parameter = new SecureNetworkProtocolParameter
            {
                Pem                         = caCertificate,
                Rsa                         = certificate,
                RsaKey                      = privateKey,
                Hostname                    = clientName,
                Protocol                    = SecureTransportProtocol.DTLS,
                ClientAuthenticationPolicy  = clientAuthenticationPolicy,
                SSLReadTimeoutMs            = readTimeout,
                SSLHandshakeTimeoutMax      = handshakeTimeoutMax,
                SSLHandshakeTimeoutMin      = handshakeTimeoutMin,
            };

            settings.AddRawParameterStruct(ref parameter);

            return ref settings;
        }

        /// <summary>Set server security parameters (for client authentication).</summary>
        /// <param name="certificate">Server's certificate chain (PEM format).</param>
        /// <param name="privateKey">Server's private key (PEM format).</param>
        /// <param name="caCertificate">CA certificate that signed the client certificates (PEM format).</param>
        /// <param name="clientName">Common name (CN) in the client certificates.</param>
        /// <param name="clientAuthenticationPolicy">Client authentication policy.</param>
        public static ref NetworkSettings WithSecureServerParameters(
            ref this NetworkSettings    settings,
            string                      certificate,
            string                      privateKey,
            string                      caCertificate,
            string                      clientName,
            SecureClientAuthPolicy      clientAuthenticationPolicy = SecureClientAuthPolicy.Required)
        {
            var fixedCertificate = new FixedString4096Bytes(certificate);
            var fixedPrivateKey = new FixedString4096Bytes(privateKey);
            var fixedCaCertificate = new FixedString4096Bytes(caCertificate);
            var fixedClientName = new FixedString32Bytes(clientName);

            settings.WithSecureServerParameters(
                ref fixedCertificate,
                ref fixedPrivateKey,
                ref fixedCaCertificate,
                ref fixedClientName,
                clientAuthenticationPolicy);

            return ref settings;
        }


        [Obsolete("Use WithSecureClientParameters or WithSecureServerParameters instead.")]
        public static ref NetworkSettings WithSecureParameters(
            ref this NetworkSettings    settings,
            ref FixedString4096Bytes    pem,
            ref FixedString32Bytes      hostname,
            SecureTransportProtocol     protocol                        = SecureTransportProtocol.DTLS,
            SecureClientAuthPolicy      clientAuthenticationPolicy      = SecureClientAuthPolicy.Optional,
            uint                        sslReadTimeoutMs                = 0,
            uint                        sslHandshakeTimeoutMax          = 60000,
            uint                        sslHandshakeTimeoutMin          = 1000
        )
        {
            var parameter = new SecureNetworkProtocolParameter
            {
                Pem                         = pem,
                Rsa                         = default,
                RsaKey                      = default,
                Hostname                    = hostname,
                Protocol                    = protocol,
                ClientAuthenticationPolicy  = clientAuthenticationPolicy,
                SSLReadTimeoutMs            = sslReadTimeoutMs,
                SSLHandshakeTimeoutMax      = sslHandshakeTimeoutMax,
                SSLHandshakeTimeoutMin      = sslHandshakeTimeoutMin,
            };

            settings.AddRawParameterStruct(ref parameter);

            return ref settings;
        }

        [Obsolete("Use WithSecureClientParameters or WithSecureServerParameters instead.")]
        public static ref NetworkSettings WithSecureParameters(
            ref this NetworkSettings    settings,
            ref FixedString4096Bytes    pem,
            ref FixedString4096Bytes    rsa,
            ref FixedString4096Bytes    rsaKey,
            ref FixedString32Bytes      hostname,
            SecureTransportProtocol     protocol                        = SecureTransportProtocol.DTLS,
            SecureClientAuthPolicy      clientAuthenticationPolicy      = SecureClientAuthPolicy.Optional,
            uint                        sslReadTimeoutMs                = 0,
            uint                        sslHandshakeTimeoutMax          = 60000,
            uint                        sslHandshakeTimeoutMin          = 1000
        )
        {
            var parameter = new SecureNetworkProtocolParameter
            {
                Pem                         = pem,
                Rsa                         = rsa,
                RsaKey                      = rsaKey,
                Hostname                    = hostname,
                Protocol                    = protocol,
                ClientAuthenticationPolicy  = clientAuthenticationPolicy,
                SSLReadTimeoutMs            = sslReadTimeoutMs,
                SSLHandshakeTimeoutMax      = sslHandshakeTimeoutMax,
                SSLHandshakeTimeoutMin      = sslHandshakeTimeoutMin,
            };

            settings.AddRawParameterStruct(ref parameter);

            return ref settings;
        }

        public static SecureNetworkProtocolParameter GetSecureParameters(ref this NetworkSettings settings)
        {
            if (!settings.TryGet<SecureNetworkProtocolParameter>(out var parameters))
            {
                throw new System.InvalidOperationException($"Can't extract Secure parameters: {nameof(SecureNetworkProtocolParameter)} must be provided to the {nameof(NetworkSettings)}");
            }

            return parameters;
        }
    }
}

#endif