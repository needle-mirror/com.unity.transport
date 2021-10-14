using System.Net;
using Unity.Collections;
using UnityEngine;
using Unity.Networking.Transport;

public class ClientBehaviour : MonoBehaviour
{
    private NetworkDriver m_Driver;
    //private NetworkPipeline m_UnreliablePipeline;
    private NetworkPipeline m_SequencedPipeline;
    public NetworkConnection m_Connection;
    public bool m_Done;

    void Start()
    {
        m_Driver = NetworkDriver.Create();
        //m_UnreliablePipeline = NetworkPipeline.Null;
        m_SequencedPipeline = m_Driver.CreatePipeline(typeof(UnreliableSequencedPipelineStage));
        m_Connection = default(NetworkConnection);

        var endpoint = NetworkEndPoint.LoopbackIpv4;
        endpoint.Port = 9000;
        m_Connection = m_Driver.Connect(endpoint);
    }

    public void OnDestroy()
    {
        m_Driver.Dispose();
    }

    void Update()
    {
        m_Driver.ScheduleUpdate().Complete();

        if (!m_Connection.IsCreated)
        {
            if (!m_Done)
                Debug.Log("Something went wrong during connect");
            return;
        }

        DataStreamReader stream;
        NetworkEvent.Type cmd;

        while ((cmd = m_Connection.PopEvent(m_Driver, out stream)) !=
               NetworkEvent.Type.Empty)
        {
            if (cmd == NetworkEvent.Type.Connect)
            {
                Debug.Log("We are now connected to the server");

                var value = 1;
                if (m_Driver.BeginSend(m_SequencedPipeline, m_Connection, out var writer) == 0)
                {
                    writer.WriteInt(value);
                    m_Driver.EndSend(writer);
                }
            }
            else if (cmd == NetworkEvent.Type.Data)
            {
                uint value = stream.ReadUInt();
                Debug.Log("Got the value = " + value + " back from the server");
                m_Done = true;
                m_Connection.Disconnect(m_Driver);
                m_Connection = default(NetworkConnection);
            }
            else if (cmd == NetworkEvent.Type.Disconnect)
            {
                Debug.Log("Client got disconnected from server");
                m_Connection = default(NetworkConnection);
            }
        }
    }
}
