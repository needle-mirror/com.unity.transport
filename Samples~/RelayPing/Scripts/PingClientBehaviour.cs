#if ENABLE_RELAY


using Unity.Burst;
using UnityEngine;
using Unity.Networking.Transport;
using Unity.Collections;
using Unity.Jobs;
using System.Collections;
using System.Collections.Generic;
using Unity.Networking.Transport.Relay;
using Unity.Services.Relay;
using Unity.Services.Relay.Allocations;
using Unity.Services.Relay.Models;
using Unity.Services.Core;
using Unity.Services.Authentication;

public class PingClientBehaviour : MonoBehaviour
{
    struct PendingPing
    {
        public int id;
        public float time;
    }

    private NetworkDriver m_ClientDriver;
    private NativeArray<NetworkConnection> m_clientToServerConnection;
    private List<INetworkParameter> m_NetworkParameters;

    // pendingPings is an array of pings sent to the server which have not yet received a response.
    // Currently we only support one ping in-flight
    private NativeArray<PendingPing> m_pendingPings;
    // The ping stats are two integers, time for last ping and number of pings
    private NativeArray<int> m_pingStats;

    private JobHandle m_updateHandle;
    private bool isRelayConnected = false;
    
    void InitDriver(ref RelayServerData relayServerData)
    {
        m_NetworkParameters = new List<INetworkParameter>();
        m_NetworkParameters.Add(new RelayNetworkParameter{ ServerData = relayServerData });

        m_ClientDriver = NetworkDriver.Create(m_NetworkParameters.ToArray());

        m_pendingPings = new NativeArray<PendingPing>(64, Allocator.Persistent);
        m_pingStats = new NativeArray<int>(2, Allocator.Persistent);
        m_clientToServerConnection = new NativeArray<NetworkConnection>(1, Allocator.Persistent);
    }

    public IEnumerator ConnectAndBind(string joinCode) {
        UnityServices.Initialize();

        var joinTask = RelayService.AllocationsApiClient.JoinRelayAsync(new JoinRelayRequest(new JoinRequest(joinCode)));
        while(!joinTask.IsCompleted)
            yield return null;

        if (joinTask.IsFaulted)
        {
            Debug.LogError("Join Relay request failed");
            PingClientUIBehaviour.isClient = false;
            yield break;
        }

        var allocation = joinTask.Result.Result.Data.Allocation;
        
        var allocationId = RelayUtilities.ConvertFromAllocationIdBytes(allocation.AllocationIdBytes);

        var connectionData = RelayUtilities.ConvertConnectionData(allocation.ConnectionData);
        var hostConnectionData = RelayUtilities.ConvertConnectionData(allocation.HostConnectionData);
        var key = RelayUtilities.ConvertFromHMAC(allocation.Key);

        Debug.Log($"client: {allocation.ConnectionData[0]} {allocation.ConnectionData[1]}");
        Debug.Log($"host: {allocation.HostConnectionData[0]} {allocation.HostConnectionData[1]}");

        Debug.Log($"client: {allocation.AllocationId}");
        
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
        
        var relayServerData = new RelayServerData(ref serverEndpoint, 0, ref allocationId, ref connectionData, ref hostConnectionData, ref key, defaultEndPoint.Secure);
        relayServerData.ComputeNewNonce();

        InitDriver(ref relayServerData);

        if (m_ClientDriver.Bind(NetworkEndPoint.AnyIpv4) != 0)
        {
                Debug.LogError("Client failed to bind");
        }
        else
        {
            while (!m_ClientDriver.Bound)
            {
                yield return null;
            }

            m_clientToServerConnection[0] = m_ClientDriver.Connect(serverEndpoint);

            while (m_ClientDriver.GetConnectionState(m_clientToServerConnection[0]) == NetworkConnection.State.Connecting)
            {
                yield return null;
            }

            if (m_ClientDriver.GetConnectionState(m_clientToServerConnection[0]) == NetworkConnection.State.Connected)
            {
                Debug.Log("Connected to Relay");
                isRelayConnected = true;
            }
            else
            {
                Debug.LogError("Client failed to connect to server");
            }
        }
    }
     
    void OnDestroy()
    {
        // All jobs must be completed before we can dispose the data they use
        m_updateHandle.Complete();
        m_ClientDriver.Dispose();
        m_pendingPings.Dispose();
        m_pingStats.Dispose();
        m_clientToServerConnection.Dispose();
    }

    [BurstCompile]
    struct PingJob : IJob
    {
        public NetworkDriver driver;
        public NativeArray<NetworkConnection> connection;
        public NativeArray<PendingPing> pendingPings;
        public NativeArray<int> pingStats;
        public float fixedTime;

        public void Execute()
        {
            DataStreamReader strm;
            NetworkEvent.Type cmd;
            // Process all events on the connection. If the connection is invalid it will return Empty immediately
            while ((cmd = connection[0].PopEvent(driver, out strm)) != NetworkEvent.Type.Empty)
            {
                if (cmd == NetworkEvent.Type.Connect)
                {
                    // When we get the connect message we can start sending data to the server
                    // Set the ping id to a sequence number for the new ping we are about to send
                    pendingPings[0] = new PendingPing {id = pingStats[0], time = fixedTime};
                    // Create a 4 byte data stream which we can store our ping sequence number in

                    if (driver.BeginSend(connection[0], out var pingData) == 0)
                    {
                        pingData.WriteInt(pingStats[0]);
                        driver.EndSend(pingData);
                    }
                    // Update the number of sent pings
                    pingStats[0] = pingStats[0] + 1;
                }
                else if (cmd == NetworkEvent.Type.Data)
                {
                    // When the pong message is received we calculate the ping time and disconnect
                    pingStats[1] = (int) ((fixedTime - pendingPings[0].time) * 1000);
                    connection[0].Disconnect(driver);
                    connection[0] = default(NetworkConnection);
                }
                else if (cmd == NetworkEvent.Type.Disconnect)
                {
                    // If the server disconnected us we clear out connection
                    connection[0] = default(NetworkConnection);
                }
            }
        }
    }

    private void Update()
    {
        // When connecting to the relay we need to this? 
        if (m_ClientDriver.IsCreated && !isRelayConnected)
        {
            m_ClientDriver.ScheduleUpdate().Complete();
            
            var pingJob = new PingJob
            {
                driver = m_ClientDriver,
                connection = m_clientToServerConnection,
                pendingPings = m_pendingPings,
                pingStats = m_pingStats,
                fixedTime = Time.fixedTime
            };
            
            pingJob.Schedule().Complete();
        }
    }

    void LateUpdate()
    {
        // On fast clients we can get more than 4 frames per fixed update, this call prevents warnings about TempJob
        // allocation longer than 4 frames in those cases
        if (m_ClientDriver.IsCreated && isRelayConnected)
            m_updateHandle.Complete();
    }

    void FixedUpdate()
    {
        if (m_ClientDriver.IsCreated && isRelayConnected) {

            // Wait for the previous frames ping to complete before starting a new one, the Complete in LateUpdate is not
            // enough since we can get multiple FixedUpdate per frame on slow clients
            m_updateHandle.Complete();

            // Update the ping client UI with the ping statistics computed by teh job scheduled previous frame since that
            // is now guaranteed to have completed
            PingClientUIBehaviour.UpdateStats(m_pingStats[0], m_pingStats[1]);
            var pingJob = new PingJob
            {
                driver = m_ClientDriver,
                connection = m_clientToServerConnection,
                pendingPings = m_pendingPings,
                pingStats = m_pingStats,
                fixedTime = Time.fixedTime
            };
            // Schedule a chain with the driver update followed by the ping job
            m_updateHandle = m_ClientDriver.ScheduleUpdate();
            m_updateHandle = pingJob.Schedule(m_updateHandle);
        }
    }
}

#endif