using System;
using UnityEngine;
using Unity.Services.Core;
using Unity.Services.Authentication;

namespace Unity.Networking.Transport.Samples
{
    /// <summary>
    /// Component responsible for signing in the user to Unity Gaming Services and starting a client
    /// or server depending on which option is chosen in the UI. If starting a client, the UI will
    /// also display statistics for the running pings.
    /// </summary>
    public class PingUIBehaviour : MonoBehaviour
    {
        // Possible states for the UI.
        private enum PingUIState
        {
            NotStarted,
            ClientWaitingForJoinCode,
            ClientStarted,
            ServerStarted
        }

        private PingUIState m_CurrentState = PingUIState.NotStarted;

        /// <summary>Join code the client must use to connect to the server.</summary>
        [NonSerialized]
        public string JoinCode = "";

        private bool m_IsSignedIn;

        // Ping statistics.
        private int m_PingCount;
        private int m_PingLastRTT;

        private void OnGUI()
        {
            if (!m_IsSignedIn)
            {
                if (GUILayout.Button("Sign In"))
                {
                    SignIn();
                }
                return;
            }

            switch (m_CurrentState)
            {
                case PingUIState.NotStarted:
                    if (GUILayout.Button("Start Client"))
                    {
                        m_CurrentState = PingUIState.ClientWaitingForJoinCode;
                    }
                    if (GUILayout.Button("Start Server"))
                    {
                        var server = gameObject.AddComponent<PingServerBehaviour>() as PingServerBehaviour;
                        server.PingUI = this;
                        StartCoroutine(server.Connect());

                        m_CurrentState = PingUIState.ServerStarted;
                    }
                    break;

                case PingUIState.ClientWaitingForJoinCode:
                    GUILayout.Label("Join code:");
                    JoinCode = GUILayout.TextField(JoinCode);
                    if (GUILayout.Button("Start Ping"))
                    {
                        var client = gameObject.AddComponent<PingClientBehaviour>() as PingClientBehaviour;
                        client.PingUI = this;
                        StartCoroutine(client.Connect());

                        m_CurrentState = PingUIState.ClientStarted;
                    }
                    break;

                case PingUIState.ClientStarted:
                    GUILayout.Label($"Ping {m_PingCount}: {m_PingLastRTT}ms");
                    break;

                case PingUIState.ServerStarted:
                    GUILayout.Label("Join code:");
                    GUILayout.Label(JoinCode);
                    break;
            }
        }

        private async void SignIn()
        {
            await UnityServices.InitializeAsync();
            await AuthenticationService.Instance.SignInAnonymouslyAsync();

            m_IsSignedIn = AuthenticationService.Instance.IsSignedIn;
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
