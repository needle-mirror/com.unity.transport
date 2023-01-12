using UnityEngine;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Networking.Transport;

namespace Unity.Networking.Transport.Samples
{
    /// <summary>Component responsible for sending pings to the server.</summary>
    public class PingClientBehaviour : MonoBehaviour
    {
        /// <summary>UI component to get the server endpoint and update statistics.</summay>
        public PingClientUIBehaviour PingClientUI;

        // Frequency (in seconds) at which to send ping messages.
        private const float k_PingFrequency = 1.0f;

        private NetworkDriver m_ClientDriver;

        // Connection and ping statistics. Values that can be modified by a job are stored in
        // NativeReferences since jobs can only modify values in native containers.
        private NativeReference<NetworkConnection> m_ClientConnection;
        private NativeReference<int> m_PingLastRTT;
        private int m_PingCount;
        private float m_LastSendTime;

        // Handle to the job chain of the ping client. We need to keep this around so that we can
        // schedule the jobs in one execution of Update and complete it in the next.
        private JobHandle m_ClientJobHandle;

        private void Start()
        {
            m_ClientDriver = NetworkDriver.Create();

            m_ClientConnection = new NativeReference<NetworkConnection>(Allocator.Persistent);
            m_PingLastRTT = new NativeReference<int>(Allocator.Persistent);
        }

        private void OnDestroy()
        {
            // All jobs must be completed before we can dispose of the data they use.
            m_ClientJobHandle.Complete();

            m_ClientDriver.Dispose();
            m_ClientConnection.Dispose();
            m_PingLastRTT.Dispose();
        }

        // Job that will send ping messages to the server.
        [BurstCompile]
        private struct PingSendJob : IJob
        {
            public NetworkDriver.Concurrent Driver;
            public NetworkConnection Connection;
            public float CurrentTime;

            public void Execute()
            {
                var result = Driver.BeginSend(Connection, out var writer);
                if (result < 0)
                {
                    Debug.LogError($"Couldn't send ping (error code {result}).");
                    return;
                }

                // We write the current time in the ping message, and the server will just resend
                // it back, allowing us to compute the RTT for that ping message.
                writer.WriteFloat(CurrentTime);

                result = Driver.EndSend(writer);
                if (result < 0)
                {
                    Debug.LogError($"Couldn't send ping (error code {result}).");
                    return;
                }
            }
        }

        // Job to run after a driver update, which will deal with update the client connection and
        // reacting to ping messages received by the servers.
        [BurstCompile]
        private struct PingUpdateJob : IJob
        {
            public NetworkDriver Driver;
            public NativeReference<NetworkConnection> Connection;
            public NativeReference<int> PingLastRTT;
            public float CurrentTime;

            public void Execute()
            {
                NetworkEvent.Type eventType;
                while ((eventType = Connection.Value.PopEvent(Driver, out var reader)) != NetworkEvent.Type.Empty)
                {
                    switch (eventType)
                    {
                        // Connect event means the connection has been established.
                        case NetworkEvent.Type.Connect:
                            Debug.Log("Connected to server.");
                            break;

                        // Disconnect event means the connection was closed (server exited).
                        case NetworkEvent.Type.Disconnect:
                            Debug.Log("Got disconnected from server.");

                            // Storing a default value as the connection will make the Update method
                            // retry to connect when it next executes.
                            Connection.Value = default;
                            break;

                        // Data events must be ping messages sent by the server.
                        case NetworkEvent.Type.Data:
                            PingLastRTT.Value = (int)((CurrentTime - reader.ReadFloat()) * 1000);
                            break;
                    }
                }
            }
        }

        private void Update()
        {
            // First, complete the previously-scheduled job chain.
            m_ClientJobHandle.Complete();

            if (PingClientUI.IsPingRunning)
            {
                var currentTime = Time.realtimeSinceStartup;

                // Update the UI with the latest statistics.
                PingClientUI.UpdateStatistics(m_PingCount, m_PingLastRTT.Value);

                // Connect if there's no connection already.
                if (!m_ClientConnection.Value.IsCreated)
                {
                    m_ClientConnection.Value = m_ClientDriver.Connect(PingClientUI.ServerEndpoint);
                }

                var updateJob = new PingUpdateJob
                {
                    Driver = m_ClientDriver,
                    Connection = m_ClientConnection,
                    PingLastRTT = m_PingLastRTT,
                    CurrentTime = currentTime
                };

                // If it's time to send, schedule a send job.
                var state = m_ClientDriver.GetConnectionState(m_ClientConnection.Value);
                if (state == NetworkConnection.State.Connected && currentTime - m_LastSendTime >= k_PingFrequency)
                {
                    m_LastSendTime = currentTime;
                    m_PingCount++;

                    m_ClientJobHandle = new PingSendJob
                    {
                        Driver = m_ClientDriver.ToConcurrent(),
                        Connection = m_ClientConnection.Value,
                        CurrentTime = currentTime
                    }.Schedule();
                }

                // Schedule a job chain with the ping send job (if scheduled), the driver update
                // job, and then the ping update job. All jobs will run one after the other.
                m_ClientJobHandle = m_ClientDriver.ScheduleUpdate(m_ClientJobHandle);
                m_ClientJobHandle = updateJob.Schedule(m_ClientJobHandle);
            }
            else
            {
                // Make sure we disconnect if there was a running ping.
                if (m_ClientConnection.Value.IsCreated)
                {
                    Debug.Log("Closing connection to server.");
                    m_ClientDriver.Disconnect(m_ClientConnection.Value);
                    m_ClientConnection.Value = default;
                }
            }
        }
    }
}
