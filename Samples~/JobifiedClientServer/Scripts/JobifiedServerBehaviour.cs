using UnityEngine;
using Unity.Jobs;
using Unity.Collections;
using Unity.Networking.Transport;

namespace Unity.Networking.Transport.Samples
{
    public class JobifiedServerBehaviour : MonoBehaviour
    {
        NetworkDriver m_Driver;
        NativeList<NetworkConnection> m_Connections;

        JobHandle m_ServerJobHandle;

        void Start()
        {
            m_Driver = NetworkDriver.Create();
            m_Connections = new NativeList<NetworkConnection>(16, Allocator.Persistent);

            var endpoint = NetworkEndpoint.AnyIpv4.WithPort(7777);
            if (m_Driver.Bind(endpoint) != 0)
            {
                Debug.LogError("Failed to bind to port 7777.");
                return;
            }
            m_Driver.Listen();
        }

        void OnDestroy()
        {
            if (m_Driver.IsCreated)
            {
                m_ServerJobHandle.Complete();
                m_Driver.Dispose();
                m_Connections.Dispose();
            }
        }

        void Update()
        {
            m_ServerJobHandle.Complete();

            var connectionJob = new ServerUpdateConnectionsJob
            {
                Driver = m_Driver,
                Connections = m_Connections
            };

            var serverUpdateJob = new ServerUpdateJob
            {
                Driver = m_Driver.ToConcurrent(),
                Connections = m_Connections.AsDeferredJobArray()
            };

            m_ServerJobHandle = m_Driver.ScheduleUpdate();
            m_ServerJobHandle = connectionJob.Schedule(m_ServerJobHandle);
            m_ServerJobHandle = serverUpdateJob.Schedule(m_Connections, 1, m_ServerJobHandle);
        }

        struct ServerUpdateConnectionsJob : IJob
        {
            public NetworkDriver Driver;
            public NativeList<NetworkConnection> Connections;

            public void Execute()
            {
                // Clean up connections.
                for (int i = 0; i < Connections.Length; i++)
                {
                    if (!Connections[i].IsCreated)
                    {
                        Connections.RemoveAtSwapBack(i);
                        i--;
                    }
                }

                // Accept new connections.
                NetworkConnection c;
                while ((c = Driver.Accept()) != default)
                {
                    Connections.Add(c);
                    Debug.Log("Accepted a connection.");
                }
            }
        }

        struct ServerUpdateJob : IJobParallelForDefer
        {
            public NetworkDriver.Concurrent Driver;
            public NativeArray<NetworkConnection> Connections;

            public void Execute(int i)
            {
                DataStreamReader stream;
                NetworkEvent.Type cmd;
                while ((cmd = Driver.PopEventForConnection(Connections[i], out stream)) != NetworkEvent.Type.Empty)
                {
                    if (cmd == NetworkEvent.Type.Data)
                    {
                        uint number = stream.ReadUInt();

                        Debug.Log($"Got {number} from a client, adding 2 to it.");
                        number += 2;

                        Driver.BeginSend(Connections[i], out var writer);
                        writer.WriteUInt(number);
                        Driver.EndSend(writer);
                    }
                    else if (cmd == NetworkEvent.Type.Disconnect)
                    {
                        Debug.Log("Client disconnected from the server.");
                        Connections[i] = default;
                    }
                }
            }
        }
    }
}