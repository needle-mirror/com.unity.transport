using UnityEngine;
using Unity.Collections;
using Unity.Networking.Transport;

namespace Unity.Networking.Transport.Samples
{
    public class CrossPlayServerBehaviour : MonoBehaviour
    {
        MultiNetworkDriver m_Driver;
        NativeList<NetworkConnection> m_Connections;

        void Start()
        {
            var endpoint = NetworkEndpoint.AnyIpv4.WithPort(7777);

            var udpDriver = NetworkDriver.Create(new UDPNetworkInterface());
            if (udpDriver.Bind(endpoint) != 0 || udpDriver.Listen() != 0)
            {
                Debug.LogError("Failed to bind or listen to UDP port 7777.");
                udpDriver.Dispose();
                return;
            }

            var wsDriver = NetworkDriver.Create(new WebSocketNetworkInterface());
            if (wsDriver.Bind(endpoint) != 0 || wsDriver.Listen() != 0)
            {
                Debug.LogError("Failed to bind or listen to TCP port 7777.");
                udpDriver.Dispose();
                wsDriver.Dispose();
                return;
            }

            m_Driver = MultiNetworkDriver.Create();
            m_Driver.AddDriver(udpDriver);
            m_Driver.AddDriver(wsDriver);

            m_Connections = new NativeList<NetworkConnection>(16, Allocator.Persistent);
        }

        void OnDestroy()
        {
            if (m_Driver.IsCreated)
            {
                m_Driver.Dispose();
                m_Connections.Dispose();
            }
        }

        void Update()
        {
            m_Driver.ScheduleUpdate().Complete();

            // Clean up connections.
            for (int i = 0; i < m_Connections.Length; i++)
            {
                if (!m_Connections[i].IsCreated)
                {
                    m_Connections.RemoveAtSwapBack(i);
                    i--;
                }
            }

            // Accept new connections.
            NetworkConnection c;
            while ((c = m_Driver.Accept()) != default)
            {
                m_Connections.Add(c);
                Debug.Log("Accepted a connection.");
            }

            for (int i = 0; i < m_Connections.Length; i++)
            {
                DataStreamReader stream;
                NetworkEvent.Type cmd;
                while ((cmd = m_Driver.PopEventForConnection(m_Connections[i], out stream)) != NetworkEvent.Type.Empty)
                {
                    if (cmd == NetworkEvent.Type.Data)
                    {
                        uint number = stream.ReadUInt();

                        Debug.Log($"Got {number} from a client, adding 2 to it.");
                        number += 2;

                        m_Driver.BeginSend(NetworkPipeline.Null, m_Connections[i], out var writer);
                        writer.WriteUInt(number);
                        m_Driver.EndSend(writer);
                    }
                    else if (cmd == NetworkEvent.Type.Disconnect)
                    {
                        Debug.Log("Client disconnected from the server.");
                        m_Connections[i] = default;
                        break;
                    }
                }
            }
        }
    }
}