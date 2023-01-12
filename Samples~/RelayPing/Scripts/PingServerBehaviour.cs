using System.Collections;
using UnityEngine;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Relay;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;

namespace Unity.Networking.Transport.Samples
{
    /// <summary>Component that will listen for ping connections and answer pings.</summary>
    public class PingServerBehaviour : MonoBehaviour
    {
        /// <summary>UI component on which to set the join code.</summay>
        public PingUIBehaviour PingUI;

        private NetworkDriver m_ServerDriver;
        private NativeList<NetworkConnection> m_ServerConnections;

        // Handle to the job chain of the ping server. We need to keep this around so that we can
        // schedule the jobs in one execution of Update and complete it in the next.
        private JobHandle m_ServerJobHandle;

        private void Start()
        {
            m_ServerConnections = new NativeList<NetworkConnection>(16, Allocator.Persistent);
        }

        private void OnDestroy()
        {
            if (m_ServerDriver.IsCreated)
            {
                // All jobs must be completed before we can dispose of the data they use.
                m_ServerJobHandle.Complete();
                m_ServerDriver.Dispose();
            }

            m_ServerConnections.Dispose();
        }

        /// <summary>Start establishing a connection to the server and listening for connections.</summary>
        /// <returns>Enumerator for a coroutine.</returns>
        public IEnumerator Connect()
        {
            var allocationTask = RelayService.Instance.CreateAllocationAsync(5);
            while (!allocationTask.IsCompleted)
                yield return null;

            if (allocationTask.IsFaulted)
            {
                Debug.LogError("Failed to create Relay allocation.");
                yield break;
            }

            var allocation = allocationTask.Result;

            var joinCodeTask = RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);
            while (!joinCodeTask.IsCompleted)
                yield return null;

            if (joinCodeTask.IsFaulted)
            {
                Debug.LogError("Failed to request join code for allocation.");
                yield break;
            }

            PingUI.JoinCode = joinCodeTask.Result;

            var relayServerData = new RelayServerData(allocation, "udp");
            var settings = new NetworkSettings();
            settings.WithRelayParameters(serverData: ref relayServerData);

            m_ServerDriver = NetworkDriver.Create(settings);

            // NetworkDriver expects to be bound to something before listening for connections, but
            // for Relay it really doesn't matter what we bound to. AnyIpv4 is as good as any.
            if (m_ServerDriver.Bind(NetworkEndpoint.AnyIpv4) < 0)
            {
                Debug.LogError("Failed to bind the NetworkDriver.");
                yield break;
            }

            if (m_ServerDriver.Listen() < 0)
            {
                Debug.LogError("Failed to start listening for connections.");
                yield break;
            }

            Debug.Log("Server is now listening for connections.");
        }

        // Job to clean up old connections and accept new ones.
        [BurstCompile]
        private struct ConnectionsRelayUpdateJob : IJob
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
        private struct ConnectionsRelayEventsJobs : IJobParallelForDefer
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
            if (m_ServerDriver.IsCreated)
            {
                // First, complete the previously-scheduled job chain.
                m_ServerJobHandle.Complete();

                // Create the jobs first.
                var updateJob = new ConnectionsRelayUpdateJob
                {
                    Driver = m_ServerDriver,
                    Connections = m_ServerConnections
                };
                var eventsJobs = new ConnectionsRelayEventsJobs
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
}
