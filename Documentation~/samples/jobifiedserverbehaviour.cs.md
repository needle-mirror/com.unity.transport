```c#
using UnityEngine;
using UnityEngine.Assertions;

using Unity.Jobs;
using Unity.Collections;
using Unity.Networking.Transport;

struct ServerUpdateConnectionsJob : IJob
{
    public UdpNetworkDriver driver;
    public NativeList<NetworkConnection> connections;

    public void Execute()
    {
        // CleanUpConnections
        for (int i = 0; i < connections.Length; i++)
        {
            if (!connections[i].IsCreated)
            {
                connections.RemoveAtSwapBack(i);
                --i;
            }
        }
        // AcceptNewConnections
        NetworkConnection c;
        while ((c = driver.Accept()) != default(NetworkConnection))
        {
            connections.Add(c);
            Debug.Log("Accepted a connection");
        }
    }
}

struct ServerUpdateJob : IJobParallelForDefer
{
    public UdpNetworkDriver.Concurrent driver;
    public NativeArray<NetworkConnection> connections;

    public void Execute(int index)
    {
        DataStreamReader stream;
        if (!connections[index].IsCreated)
            Assert.IsTrue(true);

        NetworkEvent.Type cmd;
        while ((cmd = driver.PopEventForConnection(connections[index], out stream)) !=
               NetworkEvent.Type.Empty)
        {
            if (cmd == NetworkEvent.Type.Data)
            {
                var readerCtx = default(DataStreamReader.Context);
                uint number = stream.ReadUInt(ref readerCtx);

                Debug.Log("Got " + number + " from the Client adding + 2 to it.");
                number +=2;

                using (var writer = new DataStreamWriter(4, Allocator.Temp))
                {
                    writer.Write(number);
                    driver.Send(NetworkPipeline.Null, connections[index], writer);
                }
            }
            else if (cmd == NetworkEvent.Type.Disconnect)
            {
                Debug.Log("Client disconnected from server");
                connections[index] = default(NetworkConnection);
            }
        }
    }
}

public class JobifiedServerBehaviour : MonoBehaviour
{
    public UdpNetworkDriver m_Driver;
    public NativeList<NetworkConnection> m_Connections;
    private JobHandle ServerJobHandle;

    void Start ()
	{
        m_Connections = new NativeList<NetworkConnection>(16, Allocator.Persistent);
        m_Driver = new UdpNetworkDriver(new INetworkParameter[0]);
        var endpoint = NetworkEndPoint.AnyIpv4;
        endpoint.Port = 9000;
        if (m_Driver.Bind(endpoint) != 0)
            Debug.Log("Failed to bind to port 9000");
        else
            m_Driver.Listen();
    }

    public void OnDestroy()
    {
        // Make sure we run our jobs to completion before exiting.
        ServerJobHandle.Complete();
        m_Connections.Dispose();
        m_Driver.Dispose();
    }

    void Update ()
	{
        ServerJobHandle.Complete();

        var connectionJob = new ServerUpdateConnectionsJob
        {
            driver = m_Driver,
            connections = m_Connections
        };

        var serverUpdateJob = new ServerUpdateJob
        {
            driver = m_Driver.ToConcurrent(),
            connections = m_Connections.AsDeferredJobArray()
        };

        ServerJobHandle = m_Driver.ScheduleUpdate();
        ServerJobHandle = connectionJob.Schedule(ServerJobHandle);
        ServerJobHandle = serverUpdateJob.Schedule(m_Connections, 1, ServerJobHandle);
    }
}
```