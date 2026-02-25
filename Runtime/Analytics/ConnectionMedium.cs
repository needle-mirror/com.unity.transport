using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Unity.Networking.Transport.Analytics
{
    /// <summary>The medium on which a network connection is established.</summary>
    internal enum ConnectionMedium
    {
        /// <summary>For connections on which the medium can't be determined.</summary>
        Unknown     = 1 << 0,

        /// <summary>Connection is established over a loopback interface.</summary>
        Loopback    = 1 << 1,

        /// <summary>Connection is established over a wired network (e.g. Ethernet).</summary>
        Wired       = 1 << 2,

        /// <summary>Connection is established over a Wi-Fi network.</summary>
        Wifi        = 1 << 3,

        /// <summary>Connection is established over a cellular network (e.g. 4G, 5G).</summary>
        Cellular    = 1 << 4,
    }

    /// <summary>Utility methods related to <see cref="ConnectionMedium"/>.</summary>
    internal static class ConnectionMediumUtilities
    {
        /// <summary>
        /// Infers the medium of a connection based on its local endpoint and the current platform.
        /// As the name implies, there is some uncertainty to this process as we may not always be
        /// able to determine the information. In some cases it may just be a best guess.
        /// </summary>
        /// <param name="endpoint">Local endpoint of the connection.</param>
        /// <returns>Medium of the connection (can be unknown if it can't be determined).</returns>
        public static ConnectionMedium InferFromLocalEndpointAndCurrentPlatform(NetworkEndpoint endpoint)
        {
            // If we're connecting from a loopback address, we're sure it's a loopback connection.
            if (endpoint.IsLoopback)
                return ConnectionMedium.Loopback;

#if UNITY_WEBGL && !UNITY_EDITOR
            // On WebGL most browsers can tell us the connection medium. Just go with that because
            // there's not much else we can do to infer the information aside from that.
            return (ConnectionMedium)GetMediumFromBrowser();
#else
            // Application.internetReachability is a bit too coarse for our needs, but for cases
            // where it can identify we're on cellular, it is very accurate in reporting so.
            if (Application.internetReachability == NetworkReachability.ReachableViaCarrierDataNetwork)
                return ConnectionMedium.Cellular;

            // Next let's see if we can determine the medium from the local network interface.
            var medium = GetMediumFromNetworkInterface(endpoint);
            if (medium != ConnectionMedium.Unknown)
                return medium;

            // At this point our best bet is to make an educated guess based on the platform.
            switch (Application.platform)
            {
                case RuntimePlatform.Android:
                case RuntimePlatform.IPhonePlayer:
                    // We'll have identified cellular using Application.internetReachability earlier
                    // and if a mobile phone is not on cellular, then it's most likely on Wi-Fi.
                    return ConnectionMedium.Wifi;
                case RuntimePlatform.LinuxServer:
                case RuntimePlatform.WindowsServer:
                case RuntimePlatform.OSXServer:
                    return ConnectionMedium.Wired;
                case RuntimePlatform.Switch:
                case RuntimePlatform.VisionOS:
                    return ConnectionMedium.Wifi;
                default:
                    return ConnectionMedium.Unknown;
            }
#endif
        }

        private static ConnectionMedium GetMediumFromNetworkInterface(NetworkEndpoint endpoint)
        {
            if (endpoint.Family != NetworkFamily.Ipv4 && endpoint.Family != NetworkFamily.Ipv6)
                return ConnectionMedium.Unknown;

            var endpointBytes = endpoint.GetRawAddressBytes().AsReadOnlySpan();
            var localAddress = new IPAddress(endpointBytes);

            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    var props = ni.GetIPProperties();
                    foreach (var unicast in props.UnicastAddresses)
                    {
                        if (unicast.Address == localAddress)
                        {
                            // Most network interface types are actually wired variations (all
                            // flavors of Ethernet, DSL, fiber, etc.), so for the sake of brevity
                            // just single out the non-wired types.
                            switch (ni.NetworkInterfaceType)
                            {
                                case NetworkInterfaceType.Unknown:
                                case NetworkInterfaceType.Tunnel:
                                    return ConnectionMedium.Unknown;
                                case NetworkInterfaceType.Loopback:
                                    return ConnectionMedium.Loopback;
                                case NetworkInterfaceType.Wireless80211:
                                    return ConnectionMedium.Wifi;
                                case NetworkInterfaceType.Wman:
                                case NetworkInterfaceType.Wwanpp:
                                case NetworkInterfaceType.Wwanpp2:
                                    return ConnectionMedium.Cellular;
                                default:
                                    return ConnectionMedium.Wired;
                            }
                        }
                    }
                }
            }
            catch
            {
                // Just ignore any errors. Likely that getting network interfaces is not supported.
            }

            return ConnectionMedium.Unknown;
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal", EntryPoint = "utp_GetConnectionMedium")]
        private static extern int GetMediumFromBrowser();
#endif
    }
}