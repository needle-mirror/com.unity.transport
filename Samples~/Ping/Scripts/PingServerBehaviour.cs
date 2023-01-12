using UnityEngine;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Networking.Transport;

namespace Unity.Networking.Transport.Samples
{
    /// <summary>Component that will listen for ping connections and answer pings.</summary>
    public class PingServerBehaviour : MonoBehaviour
    {
        /// <summary>Port on which the server will listen.</summary>
        public ushort Port = 7777;

        private NetworkDriver m_ServerDriver;
        private NativeList<NetworkConnection> m_ServerConnections;

        // Handle to the job chain of the ping server. We need to keep this around so that we can
        // schedule the jobs in one execution of Update and complete it in the next.
        private JobHandle m_ServerJobHandle;

        private void Start()
        {
            m_ServerDriver = NetworkDriver.Create();
            m_ServerConnections = new NativeList<NetworkConnection>(16, Allocator.Persistent);

            // Bind the server to the "any" address (0.0.0.0) so that it will listen on all local
            // IP addresses. This is usually the behavior you want for a server.
            var listenEndpoint = NetworkEndpoint.AnyIpv4.WithPort(Port);
            if (m_ServerDriver.Bind(listenEndpoint) < 0)
            {
                Debug.LogError($"Failed to bind server to 0.0.0.0:{Port}.");
                return;
            }

            // Now call Listen so that the server will actually start listening for connections.
            if (m_ServerDriver.Listen() < 0)
            {
                Debug.LogError("Failed to start listening for new connections.");
                return;
            }
        }

        private void OnDestroy()
        {
            // All jobs must be completed before we can dispose of the data they use.
            m_ServerJobHandle.Complete();

            m_ServerDriver.Dispose();
            m_ServerConnections.Dispose();
        }

        // Job to clean up old connections and accept new ones.
        [BurstCompile]
        private struct ConnectionsUpdateJob : IJob
        {
            public NetworkDriver Driver;
            public NativeList<NetworkConnection> Connections;

            public void Execute()
            {
                // Remove old connections from the list of active connections;
                for (int i = 0; i < Connections.Length; i++)
                {
                    if (!Connections[i].IsCreated)
                    {
                        Connections.RemoveAtSwapBack(i);
                        i--; // Need to re-process index i since it's a new connection.
                    }
                }

                // Accept all new connections.
                NetworkConnection connection;
                while ((connection = Driver.Accept()) != default)
                {
                    Connections.Add(connection);
                }
            }
        }

        // Job to process events from all connections in parallel.
        [BurstCompile]
        private struct ConnectionsEventsJobs : IJobParallelForDefer
        {
            public NetworkDriver.Concurrent Driver;
            public NativeArray<NetworkConnection> Connections;

            public void Execute(int i)
            {
                var connection = Connections[i];

                NetworkEvent.Type eventType;
                while ((eventType = Driver.PopEventForConnection(connection, out var reader)) != NetworkEvent.Type.Empty)
                {
                    if (eventType == NetworkEvent.Type.Data)
                    {
                        // Received a ping message, answer back by sending back the timestamp.
                        SendPingAnswer(connection, reader.ReadFloat());
                    }
                    else if (eventType == NetworkEvent.Type.Disconnect)
                    {
                        // By making it default-valued, the connections update job will clean it up.
                        Connections[i] = default;
                    }
                }
            }

            private void SendPingAnswer(NetworkConnection connection, float timestamp)
            {
                var result = Driver.BeginSend(connection, out var writer);
                if (result < 0)
                {
                    Debug.LogError($"Couldn't send ping answer (error code {result}).");
                    return;
                }

                writer.WriteFloat(timestamp);

                result = Driver.EndSend(writer);
                if (result < 0)
                {
                    Debug.LogError($"Couldn't send ping answer (error code {result}).");
                    return;
                }
            }
        }

        private void Update()
        {
            // First, complete the previously-scheduled job chain.
            m_ServerJobHandle.Complete();

            // Create the jobs first.
            var updateJob = new ConnectionsUpdateJob
            {
                Driver = m_ServerDriver,
                Connections = m_ServerConnections
            };
            var eventsJobs = new ConnectionsEventsJobs
            {
                Driver = m_ServerDriver.ToConcurrent(),
                Connections = m_ServerConnections.AsDeferredJobArray()
            };

            // Schedule the job chain.
            m_ServerJobHandle = m_ServerDriver.ScheduleUpdate();
            m_ServerJobHandle = updateJob.Schedule(m_ServerJobHandle);
            m_ServerJobHandle = eventsJobs.Schedule(m_ServerConnections, 1, m_ServerJobHandle);
        }
    }
}
