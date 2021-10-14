#if ENABLE_RELAY

using System;
using Unity.Burst;
using UnityEngine;
using Unity.Networking.Transport;
using Unity.Collections;
using Unity.Jobs;
using System.Collections.Generic;
using System.Collections;
using System.Runtime.InteropServices;
using System.Text;
using Unity.Networking.Transport.Relay;
using Unity.Services.Relay;
using Unity.Services.Relay.Allocations;
using Unity.Services.Relay.Models;
using Unity.Services.Core;
using Unity.Services.Authentication;
using Unity.Services.Relay.Http;

public class PingServerBehaviour : MonoBehaviour
{
    public NetworkDriver m_ServerDriver;
    private NativeList<NetworkConnection> m_connections;
    private List<INetworkParameter> m_NetworkParameters;
    private JobHandle m_updateHandle;
    private bool isRelayConnected = false;

    void InitDriver(ref RelayServerData relayServerData)
    {
        m_NetworkParameters = new List<INetworkParameter>();
        m_NetworkParameters.Add(new RelayNetworkParameter{ ServerData = relayServerData });

        m_ServerDriver = NetworkDriver.Create(m_NetworkParameters.ToArray());
        m_connections = new NativeList<NetworkConnection>(16, Allocator.Persistent);
    }

    public IEnumerator ConnectAndBind() {
        UnityServices.Initialize();
        
        var allocationTask = RelayService.AllocationsApiClient.CreateAllocationAsync(new CreateAllocationRequest(new AllocationRequest(2)));

        while(!allocationTask.IsCompleted)
        {
            yield return null;
        }

        if (allocationTask.IsFaulted)
        {
            Debug.LogError("Create allocation request failed");
            PingClientUIBehaviour.isServer = false;
            yield break;
        }

        var allocation = allocationTask.Result.Result.Data.Allocation;

        var joinCodeTask = RelayService.AllocationsApiClient.CreateJoincodeAsync(new CreateJoincodeRequest(new JoinCodeRequest(allocation.AllocationId)));

        while(!joinCodeTask.IsCompleted)
        {
            yield return null;
        }

        if (joinCodeTask.IsFaulted)
        {
            Debug.LogError("Create join code request failed");
            PingClientUIBehaviour.isServer = false;
            yield break;
        }

        PingClientUIBehaviour.m_JoinCode = joinCodeTask.Result.Result.Data.JoinCode;

        RelayServerEndpoint defaultEndPoint = new RelayServerEndpoint("udp", RelayServerEndpoint.NetworkOptions.Udp,
            true, false, allocation.RelayServer.IpV4, allocation.RelayServer.Port);

        foreach (var endPoint
            in allocation.ServerEndpoints)
        {
#if ENABLE_MANAGED_UNITYTLS
            if (endPoint.Secure == true && endPoint.Network == RelayServerEndpoint.NetworkOptions.Udp)
                defaultEndPoint = endPoint;
#else
            if (endPoint.Secure == false && endPoint.Network == RelayServerEndpoint.NetworkOptions.Udp)
                defaultEndPoint = endPoint;
#endif
        }

        var serverEndpoint = NetworkEndPoint.Parse(defaultEndPoint.Host, (ushort)defaultEndPoint.Port);

        var allocationId = RelayUtilities.ConvertFromAllocationIdBytes(allocation.AllocationIdBytes);
        var connectionData = RelayUtilities.ConvertConnectionData(allocation.ConnectionData);
        var key = RelayUtilities.ConvertFromHMAC(allocation.Key);
        
        var relayServerData = new RelayServerData(ref serverEndpoint, 0, ref allocationId, ref connectionData,
            ref connectionData, ref key, defaultEndPoint.Secure);
        
        relayServerData.ComputeNewNonce();

        InitDriver(ref relayServerData);

        if (m_ServerDriver.Bind(NetworkEndPoint.AnyIpv4) != 0)
        {
            Debug.LogError("Server failed to bind");
        }
        else
        {
            while (!m_ServerDriver.Bound)
            {
                yield return null;
            }

            if (m_ServerDriver.Listen() == 0)
            {
                Debug.Log("Connected to Relay");
                isRelayConnected = true;
            }
            else
            {
                Debug.LogError("Server failed to listen");
            }
        }
    }

    void OnDestroy()
    {
        // All jobs must be completed before we can dispose the data they use
        m_updateHandle.Complete();
        m_ServerDriver.Dispose();
        m_connections.Dispose();
    }

    [BurstCompile]
    struct DriverUpdateJob : IJob
    {
        public NetworkDriver driver;
        public NativeList<NetworkConnection> connections;

        public void Execute()
        {
            // Remove connections which have been destroyed from the list of active connections
            for (int i = 0; i < connections.Length; ++i)
            {
                if (!connections[i].IsCreated)
                {
                    connections.RemoveAtSwapBack(i);
                    // Index i is a new connection since we did a swap back, check it again
                    --i;
                }
            }

            // Accept all new connections
            while (true)
            {
                var con = driver.Accept();
                // "Nothing more to accept" is signaled by returning an invalid connection from accept
                if (!con.IsCreated)
                    break;
                connections.Add(con);
            }
        }
    }

    static NetworkConnection ProcessSingleConnection(NetworkDriver.Concurrent driver, NetworkConnection connection)
    {
        DataStreamReader strm;
        NetworkEvent.Type cmd;
        // Pop all events for the connection
        while ((cmd = driver.PopEventForConnection(connection, out strm)) != NetworkEvent.Type.Empty)
        {
            if (cmd == NetworkEvent.Type.Data)
            {
                // For ping requests we reply with a pong message
                int id = strm.ReadInt();
                // Create a temporary DataStreamWriter to keep our serialized pong message
                if (driver.BeginSend(connection, out var pongData) == 0)
                {
                    pongData.WriteInt(id);
                    // Send the pong message with the same id as the ping
                    driver.EndSend(pongData);
                }
            }
            else if (cmd == NetworkEvent.Type.Disconnect)
            {
                // When disconnected we make sure the connection return false to IsCreated so the next frames
                // DriverUpdateJob will remove it
                return default(NetworkConnection);
            }
        }

        return connection;
    }
#if ENABLE_IL2CPP
    [BurstCompile]
    struct PongJob : IJob
    {
        public NetworkDriver.Concurrent driver;
        public NativeList<NetworkConnection> connections;

        public void Execute()
        {
            for (int i = 0; i < connections.Length; ++i)
                connections[i] = ProcessSingleConnection(driver, connections[i]);
        }
    }
#else
    [BurstCompile]
    struct PongJob : IJobParallelForDefer
    {
        public NetworkDriver.Concurrent driver;
        public NativeArray<NetworkConnection> connections;

        public void Execute(int i)
        {
            connections[i] = ProcessSingleConnection(driver, connections[i]);
        }
    }
#endif

    private void Update()
    {
        // When connecting to the relay we need to this? 
        if (m_ServerDriver.IsCreated && !isRelayConnected)
        {
            m_ServerDriver.ScheduleUpdate().Complete();
            
            var updateJob = new DriverUpdateJob {driver = m_ServerDriver, connections = m_connections};
            updateJob.Schedule().Complete();
        }
    }

    void LateUpdate()
    {
        // On fast clients we can get more than 4 frames per fixed update, this call prevents warnings about TempJob
        // allocation longer than 4 frames in those cases
        if (m_ServerDriver.IsCreated && isRelayConnected)
            m_updateHandle.Complete();
    }

    void FixedUpdate()
    {
        if (m_ServerDriver.IsCreated && isRelayConnected) {
            // Wait for the previous frames ping to complete before starting a new one, the Complete in LateUpdate is not
            // enough since we can get multiple FixedUpdate per frame on slow clients
            m_updateHandle.Complete();
            var updateJob = new DriverUpdateJob {driver = m_ServerDriver, connections = m_connections};
            var pongJob = new PongJob
            {
                // PongJob is a ParallelFor job, it must use the concurrent NetworkDriver
                driver = m_ServerDriver.ToConcurrent(),
                // PongJob uses IJobParallelForDeferExtensions, we *must* use AsDeferredJobArray in order to access the
                // list from the job
#if ENABLE_IL2CPP
                // IJobParallelForDeferExtensions is not working correctly with IL2CPP
                connections = m_connections
#else
                connections = m_connections.AsDeferredJobArray()
#endif
            };
            // Update the driver should be the first job in the chain
            m_updateHandle = m_ServerDriver.ScheduleUpdate();
            // The DriverUpdateJob which accepts new connections should be the second job in the chain, it needs to depend
            // on the driver update job
            m_updateHandle = updateJob.Schedule(m_updateHandle);
            // PongJob uses IJobParallelForDeferExtensions, we *must* schedule with a list as first parameter rather than
            // an int since the job needs to pick up new connections from DriverUpdateJob
            // The PongJob is the last job in the chain and it must depends on the DriverUpdateJob
#if ENABLE_IL2CPP
            m_updateHandle = pongJob.Schedule(m_updateHandle);
#else
            m_updateHandle = pongJob.Schedule(m_connections, 1, m_updateHandle);
#endif
        }
        
    }
}

#endif