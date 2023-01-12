using UnityEngine;
using Unity.Jobs;
using Unity.Collections;
using Unity.Networking.Transport;

namespace Unity.Networking.Transport.Samples
{
    public class JobifiedClientBehaviour : MonoBehaviour
    {
        NetworkDriver m_Driver;
        NativeArray<NetworkConnection> m_Connection;

        JobHandle m_ClientJobHandle;

        void Start()
        {
            m_Driver = NetworkDriver.Create();
            m_Connection = new NativeArray<NetworkConnection>(1, Allocator.Persistent);

            var endpoint = NetworkEndpoint.LoopbackIpv4.WithPort(7777);
            m_Connection[0] = m_Driver.Connect(endpoint);
        }

        void OnDestroy()
        {
            m_ClientJobHandle.Complete();
            m_Driver.Dispose();
            m_Connection.Dispose();
        }

        void Update()
        {
            m_ClientJobHandle.Complete();

            var job = new ClientUpdateJob
            {
                Driver = m_Driver,
                Connection = m_Connection,
            };
            m_ClientJobHandle = m_Driver.ScheduleUpdate();
            m_ClientJobHandle = job.Schedule(m_ClientJobHandle);
        }

        struct ClientUpdateJob : IJob
        {
            public NetworkDriver Driver;
            public NativeArray<NetworkConnection> Connection;

            public void Execute()
            {
                if (!Connection[0].IsCreated)
                {
                    return;
                }

                DataStreamReader stream;
                NetworkEvent.Type cmd;
                while ((cmd = Connection[0].PopEvent(Driver, out stream)) != NetworkEvent.Type.Empty)
                {
                    if (cmd == NetworkEvent.Type.Connect)
                    {
                        Debug.Log("We are now connected to the server.");

                        uint value = 1;
                        Driver.BeginSend(Connection[0], out var writer);
                        writer.WriteUInt(value);
                        Driver.EndSend(writer);
                    }
                    else if (cmd == NetworkEvent.Type.Data)
                    {
                        uint value = stream.ReadUInt();
                        Debug.Log($"Got the value {value} back from the server.");

                        Driver.Disconnect(Connection[0]);
                        Connection[0] = default;
                    }
                    else if (cmd == NetworkEvent.Type.Disconnect)
                    {
                        Debug.Log("Client got disconnected from the server.");
                        Connection[0] = default;
                    }
                }
            }
        }
    }
}