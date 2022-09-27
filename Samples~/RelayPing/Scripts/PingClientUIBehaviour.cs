using System;
using System.Collections;
using UnityEngine;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Relay;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using Unity.Services.Core;
using Unity.Services.Authentication;

namespace Unity.Networking.Transport.Samples
{
    public class PingClientUIBehaviour : MonoBehaviour
    {
        // Ping statistics
        static int s_PingTime;
        static int s_PingCounter;

        static bool signedIn = false;
        public static bool isClient = false;
        public static bool isServer = false;

        public static string m_JoinCode = "";

        void Start()
        {
            s_PingTime = 0;
            s_PingCounter = 0;
        }

        void OnGUI()
        {
            UpdatePingClientUI();
        }

        // Update the ping statistics displayed in the ui. Should be called from the ping client every time a new ping is complete
        public static void UpdateStats(int count, int time)
        {
            s_PingCounter = count;
            s_PingTime = time;
        }

        public async void OnSignIn()
        {
            await UnityServices.InitializeAsync();
            await AuthenticationService.Instance.SignInAnonymouslyAsync();
            Debug.Log($"Logging in with PlayerID {AuthenticationService.Instance.PlayerId}");

            if (AuthenticationService.Instance.IsSignedIn) {
                signedIn = true;
            }
        }

        public void ClientBindAndConnect() {
        
            var clientBehaviour = gameObject.AddComponent<PingClientBehaviour>() as PingClientBehaviour;
            StartCoroutine(clientBehaviour.ConnectAndBind(m_JoinCode));
        }

        public void ServerBindAndConnect() {
        
            var serverBehaviour = gameObject.AddComponent<PingServerBehaviour>() as PingServerBehaviour;
            StartCoroutine(serverBehaviour.ConnectAndBind());
        }

        void UpdatePingClientUI()
        {
            if (!signedIn) {
                if (GUILayout.Button("SignIn")) {
                    OnSignIn();
                }
            }
            else {
            
                if (!isClient && !isServer){
                    if (GUILayout.Button("Start Server") && (!isServer || !isClient)) {
                        isServer = true;
                        isClient = false;
                        ServerBindAndConnect();
                    }
                    if (GUILayout.Button("Start Client") && (!isServer || !isClient)) {
                        isClient = true;
                        isServer = false;
                        ClientBindAndConnect();
                    }
                }

                GUILayout.Label("Join Code");
                if (isServer || isClient) {
                    GUILayout.Label(m_JoinCode);
                }
                else {
                    m_JoinCode = GUILayout.TextField(m_JoinCode);
                }
                
                if (isClient) {
                    GUILayout.Label("PING " + s_PingCounter + ": " + s_PingTime + "ms");
                }
            }
        }
    }
}
