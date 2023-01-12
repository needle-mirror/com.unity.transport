using System;
using UnityEngine;
using Unity.Networking.Transport;

namespace Unity.Networking.Transport.Samples
{
    /// <summary>
    /// Component responsible for providing an IP/port to a <see cref="PingClientBehaviour"/>, and
    /// display its running statistics. Note that the UI only supports entering IPv4 addresses.
    /// </summary>
    public class PingClientUIBehaviour : MonoBehaviour
    {
        /// <summary>Endpoint (IP/port) of the server to send pings to.</summary>
        public NetworkEndpoint ServerEndpoint { get; private set; }

        /// <summary>Whether the ping client should be running or not.</summary>
        public bool IsPingRunning => ServerEndpoint != default;

        // Ping statistics.
        private int m_PingCount;
        private int m_PingLastRTT;

        // String for the endpoint text field.
        private string m_EndpointString = "127.0.0.1:7777";

        private void OnGUI()
        {
            GUILayout.Label($"Ping {m_PingCount}: {m_PingLastRTT}ms");

            // While the ping is running, we only display a "Stop Ping" button. While stopped, we
            // display a "Start Ping" button and a text field to enter the IP/port.
            if (IsPingRunning)
            {
                if (GUILayout.Button("Stop Ping"))
                {
                    ServerEndpoint = default;
                }
            }
            else
            {
                if (GUILayout.Button("Start Ping"))
                {
                    string[] endpoint = m_EndpointString.Split(':');
                    var port = ushort.Parse(endpoint[1]);
                    ServerEndpoint = NetworkEndpoint.Parse(endpoint[0], port);

                    Debug.Log($"Starting ping to address {m_EndpointString}.");
                }

                m_EndpointString = GUILayout.TextField(m_EndpointString);
            }
        }

        /// <summary>Update the statistics that are displayed in the UI.</summary>
        /// <param name="count">Number of pings sent.</param>
        /// <param name="rtt">Round-trip time (RTT) of the last ping.</param>
        public void UpdateStatistics(int count, int rtt)
        {
            m_PingCount = count;
            m_PingLastRTT = rtt;
        }
    }
}
