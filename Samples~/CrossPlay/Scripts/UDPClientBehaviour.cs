using UnityEngine;
using Unity.Networking.Transport;

namespace Unity.Networking.Transport.Samples
{
    public class UDPClientBehaviour : MonoBehaviour
    {
        NetworkDriver m_Driver;
        NetworkConnection m_Connection;

        void Start()
        {
            m_Driver = NetworkDriver.Create(new UDPNetworkInterface());

            var endpoint = NetworkEndpoint.LoopbackIpv4.WithPort(7777);
            m_Connection = m_Driver.Connect(endpoint);
        }

        void OnDestroy()
        {
            m_Driver.Dispose();
        }

        void Update()
        {
            m_Driver.ScheduleUpdate().Complete();

            if (!m_Connection.IsCreated)
            {
                return;
            }

            Unity.Collections.DataStreamReader stream;
            NetworkEvent.Type cmd;
            while ((cmd = m_Connection.PopEvent(m_Driver, out stream)) != NetworkEvent.Type.Empty)
            {
                if (cmd == NetworkEvent.Type.Connect)
                {
                    Debug.Log("[UDP] We are now connected to the server.");

                    uint value = 1;
                    m_Driver.BeginSend(m_Connection, out var writer);
                    writer.WriteUInt(value);
                    m_Driver.EndSend(writer);
                }
                else if (cmd == NetworkEvent.Type.Data)
                {
                    uint value = stream.ReadUInt();
                    Debug.Log($"[UDP] Got the value {value} back from the server.");

                    m_Connection.Disconnect(m_Driver);
                    m_Connection = default;
                }
                else if (cmd == NetworkEvent.Type.Disconnect)
                {
                    Debug.Log("[UDP] Client got disconnected from server.");
                    m_Connection = default;
                }
            }
        }
    }
}