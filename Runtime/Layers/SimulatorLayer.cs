using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Burst;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Networking.Transport.Utilities;
using UnityEngine;

namespace Unity.Networking.Transport
{
    using Random = Unity.Mathematics.Random;

    internal unsafe struct SimulatorLayer : INetworkLayer
    {
        private struct PendingPacket
        {
            public fixed byte Packet[NetworkParameterConstants.AbsoluteMaxMessageSize];
            public ConnectionId ConnectionId;
            public NetworkEndpoint Endpoint;
            public long PendingUntil;
            public int Length;
            public int Offset;
        }

        // Need to store parameters in a native reference if they are to be modified since
        // ModifyNetworkSimulatorParameters() only gets a copy of the layer structure.
        private NativeReference<NetworkSimulatorParameter> m_Parameters;

        private NativeReference<Random> m_RNG;

        private NativeList<PendingPacket> m_SendDelayedPackets;

        public NetworkSimulatorParameter Parameters
        {
            private get => m_Parameters.Value;
            set => m_Parameters.Value = value;
        }

        private int m_DownStreamPadding;

        public int Initialize(ref NetworkSettings settings, ref ConnectionList connectionList, ref int packetPadding)
        {
            // Can ignore the result of TryGet since simulator layer is only added if it's true.
            settings.TryGet<NetworkSimulatorParameter>(out var parameters);

            m_Parameters = new NativeReference<NetworkSimulatorParameter>(parameters, Allocator.Persistent);
            m_SendDelayedPackets = new NativeList<PendingPacket>(Allocator.Persistent);

            var randomSeed = parameters.RandomSeed != 0 ? parameters.RandomSeed : (uint)TimerHelpers.GetTicks();
            m_RNG = new NativeReference<Random>(new Random(randomSeed), Allocator.Persistent);

            m_DownStreamPadding = packetPadding;

            return 0;
        }

        public void Dispose()
        {
            m_Parameters.Dispose();
            m_SendDelayedPackets.Dispose();
            m_RNG.Dispose();
        }

        public JobHandle ScheduleReceive(ref ReceiveJobArguments arguments, JobHandle dependency)
        {
            if (ShouldSkipSimulatorInReceive())
                return dependency;

            return new SimulatorReceiveJob
            {
                ReceiveQueue = arguments.ReceiveQueue,
                RNG = m_RNG,
                PacketLoss = Parameters.ReceivePacketLossPercent,
                LimitMTU = (int)Parameters.ReceiveMtu,
                DownStreamPadding = m_DownStreamPadding,
            }.Schedule(dependency);
        }

        public JobHandle ScheduleSend(ref SendJobArguments arguments, JobHandle dependency)
        {
            if (ShouldSkipSimulatorInSend())
                return dependency;

            return new SimulatorSendJob
            {
                SendQueue = arguments.SendQueue,
                PendingPackets = m_SendDelayedPackets,
                RNG = m_RNG,
                Time = arguments.Time,
                PacketLoss = Parameters.SendPacketLossPercent,
                DelayMS = Parameters.SendDelayMS,
                JitterMS = Parameters.SendJitterMS,
                DuplicatePercent = Parameters.SendDuplicatePercent,
            }.Schedule(dependency);
        }

        private bool ShouldSkipSimulatorInReceive()
        {
            return Parameters.ReceivePacketLossPercent == 0.0f
                && Parameters.ReceiveMtu == 0;
        }

        private bool ShouldSkipSimulatorInSend()
        {
            return Parameters.SendPacketLossPercent == 0.0f
                && Parameters.SendDelayMS == 0
                && Parameters.SendJitterMS == 0
                && Parameters.SendDuplicatePercent == 0.0f;
        }

        [BurstCompile]
        private struct SimulatorReceiveJob : IJob
        {
            public PacketsQueue ReceiveQueue;
            public NativeReference<Random> RNG;
            public float PacketLoss;
            public int LimitMTU;
            public int DownStreamPadding;

            public void Execute()
            {
                var random = RNG.Value;

                var count = ReceiveQueue.Count;
                for (int i = 0; i < count; i++)
                {
                    var packetProcessor = ReceiveQueue[i];

                    var shouldDropLoss = PacketLoss > 0.0f && random.NextFloat(100.0f) < PacketLoss;
                    var shouldDropMtu = LimitMTU > 0 && packetProcessor.Length + DownStreamPadding > LimitMTU;
                    if (shouldDropLoss || shouldDropMtu)
                    {
                        packetProcessor.Drop();
                        continue;
                    }
                }

                RNG.Value = random;
            }
        }

        [BurstCompile]
        private struct SimulatorSendJob : IJob
        {
            public PacketsQueue SendQueue;
            public NativeList<PendingPacket> PendingPackets;
            public NativeReference<Random> RNG;
            public long Time;
            public float PacketLoss;
            public uint DelayMS;
            public uint JitterMS;
            public float DuplicatePercent;

            public void Execute()
            {
                var random = RNG.Value;

                ProcessPackets(ref random);
                EnqueuePendingPackets();
                
                RNG.Value = random;
            }

            private void ProcessPackets(ref Random random)
            {
                var count = SendQueue.Count;
                for (int i = 0; i < count; i++)
                {
                    var packetProcessor = SendQueue[i];

                    // Check if we need to drop this packet.
                    if (PacketLoss > 0.0f && random.NextFloat(100.0f) < PacketLoss)
                    {
                        packetProcessor.Drop();
                        continue;
                    }

                    // If there's no delay, jitter, or duplication, we can just stop here and avoid
                    // having to deal with the expense of copying the packet to the pending list.
                    if (DelayMS == 0 && JitterMS == 0 && DuplicatePercent == 0.0f)
                        continue;

                    // Add delay and jitter. Even if there isn't any, we need to add the packets to
                    // the pending list to avoid messing up the order of duplicated packets.
                    var delay = DelayMS + math.max(0, random.NextInt(2 * (int)JitterMS) - (int)JitterMS);
                    var pendingPacket = new PendingPacket
                    {
                        PendingUntil = Time + delay,
                        Length = packetProcessor.Length,
                        Offset = packetProcessor.Offset,
                        ConnectionId = packetProcessor.ConnectionRef,
                        Endpoint = packetProcessor.EndpointRef,
                    };
                    packetProcessor.CopyPayload(pendingPacket.Packet, packetProcessor.Length);
                    PendingPackets.Add(pendingPacket);

                    // Check if we need to duplicate this packet.
                    if (random.NextFloat(100.0f) < DuplicatePercent)
                    {
                        // Recompute a new delay for the duplicate. We don't want duplicates to
                        // always be delivered together at the same time if there is jitter.
                        pendingPacket.PendingUntil = Time + DelayMS + random.NextUInt(2 * JitterMS) - JitterMS;
                        PendingPackets.Add(pendingPacket);
                    }

                    packetProcessor.Drop();
                }
            }

            private void EnqueuePendingPackets()
            {
                // We try to re-use packets that were dropped instead of always enqueuing new ones.
                // This variable tracks the index in Packets throughout the loop below.
                var packetsIndex = 0;
                var initialCount = SendQueue.Count;

                for (int i = 0; i < PendingPackets.Length; i++)
                {
                    var packetProcessor = default(PacketProcessor);
                    var pendingPacket = PendingPackets[i];

                    if (Time >= pendingPacket.PendingUntil)
                    {
                        // Select a packet processor, either a free one from the list, or a new one.
                        if (packetsIndex < initialCount && SendQueue[packetsIndex].Length == 0)
                        {
                            packetProcessor = SendQueue[packetsIndex];
                            packetsIndex++;
                        }
                        else if (!SendQueue.EnqueuePacket(out packetProcessor))
                        {
                            // No room in packets queue. No big deal. We'll just wait until next time.
                            return;
                        }

                        packetProcessor.EndpointRef = pendingPacket.Endpoint;
                        packetProcessor.ConnectionRef = pendingPacket.ConnectionId;
                        packetProcessor.SetUnsafeMetadata(0, pendingPacket.Offset);
                        packetProcessor.AppendToPayload(pendingPacket.Packet, pendingPacket.Length);

                        PendingPackets.RemoveAtSwapBack(i--);
                    }
                }
            }
        }
    }

    /// <summary>Parameters for the global network simulator.</summary>
    /// <remarks>
    /// <para>
    /// These parameters are for the global network simulator, which applies to all traffic going
    /// through a <see cref="NetworkDriver" /> (including control traffic). For the parameters of
    /// <see cref="SimulatorPipelineStage" />, refer to <see cref="SimulatorUtility.Parameters" />.
    /// </para>
    /// <para>
    /// We recommend using <see cref="SimulatorPipelineStage" /> to simulate network conditions as
    /// it has more features than the global one (which is only intended for specialized use cases).
    /// </para>
    /// </remarks>
    [Serializable]
    public struct NetworkSimulatorParameter : INetworkParameter
    {
        /// <summary>Percentage of received packets to drop (0-100).</summary>
        /// <value>Packet loss percentage.</value>
        public float ReceivePacketLossPercent;

        /// <summary>Percentage of sent packets to drop (0-100).</summary>
        /// <value>Packet loss percentage.</value>
        public float SendPacketLossPercent;

        /// <summary>Fixed delay to apply to sent packets.</summary>
        /// <value>Delay in milliseconds.</value>
        public uint SendDelayMS;

        /// <summary>Delay variance to apply to sent packets.</summary>
        /// <value>Delay in milliseconds.</value>
        public uint SendJitterMS;

        /// <summary>Percentage of sent packets to duplicate (0-100).</summary>
        /// <value>Packet duplicate percentage.</value>
        public float SendDuplicatePercent;

        /// <summary>Maximum packet length to process when receiving messages.</summary>
        /// <value>Maximum size in bytes.</value>
        public float ReceiveMtu;

        internal uint RandomSeed;

        /// <inheritdoc/>
        public bool Validate()
        {
            if (ReceivePacketLossPercent < 0.0f || ReceivePacketLossPercent > 100.0f)
            {
                Debug.LogError($"{nameof(ReceivePacketLossPercent)} value ({ReceivePacketLossPercent}) must be between 0 and 100.");
                return false;
            }

            if (SendPacketLossPercent < 0.0f || SendPacketLossPercent > 100.0f)
            {
                Debug.LogError($"{nameof(SendPacketLossPercent)} value ({SendPacketLossPercent}) must be between 0 and 100.");
                return false;
            }

            if (SendDuplicatePercent < 0.0f || SendDuplicatePercent > 100.0f)
            {
                Debug.LogError($"{nameof(SendDuplicatePercent)} value ({SendDuplicatePercent}) must be between 0 and 100.");
                return false;
            }

            return true;
        }
    }

    /// <summary>Extensions for <see cref="NetworkSimulatorParameter"/>.</summary>
    public static class NetworkSimulatorParameterExtensions
    {
        /// <summary>
        /// Sets the <see cref="NetworkSimulatorParameter"/> in the settings.
        /// </summary>
        /// <param name="settings">Settings to modify.</param>
        /// <param name="receivePacketLossPercent">Percentage of received packets to drop.</param>
        /// <param name="sendPacketLossPercent">Percentage of sent packets to drop.</param>
        /// <param name="sendDelayMS">Milliseconds of delay to add to sent packets.</param>
        /// <param name="sendJitterMS">Milliseconds of delaying variance sending packet.</param>
        /// <param name="sendDuplicatePercent">Percentage of sent packets to duplicate.</param>
        /// <param name="receiveMtu">Path MTU size to simulate; messages larger than this will be dropped by the receiver.</param>
        /// <returns>Settings structure with modified values.</returns>
        public static ref NetworkSettings WithNetworkSimulatorParameters(
            ref this NetworkSettings settings,
            float receivePacketLossPercent = 0.0f,
            float sendPacketLossPercent = 0.0f,
            uint sendDelayMS = 0,
            uint sendJitterMS = 0,
            float sendDuplicatePercent = 0.0f,
            int receiveMtu = 0)
        {
            var parameters = new NetworkSimulatorParameter
            {
                ReceivePacketLossPercent = receivePacketLossPercent,
                SendPacketLossPercent = sendPacketLossPercent,
                SendDelayMS = sendDelayMS,
                SendJitterMS = sendJitterMS,
                SendDuplicatePercent = sendDuplicatePercent,
                ReceiveMtu = receiveMtu
            };

            settings.AddRawParameterStruct(ref parameters);
            return ref settings;
        }

        // TODO This ModifyNetworkSimulatorParameters() extension method is NOT a pattern we want
        //      repeated throughout the code. At some point we'll want to deprecate it and replace
        //      it with a proper general mechanism to modify settings at runtime (see MTT-4161).

        /// <summary>Modify the parameters of the global network simulator.</summary>
        /// <param name="driver">Driver to modify.</param>
        /// <param name="newParams">New parameters for the simulator.</param>
        public static void ModifyNetworkSimulatorParameters(this NetworkDriver driver, NetworkSimulatorParameter newParams)
        {
            if (!driver.m_NetworkStack.TryGetLayer<SimulatorLayer>(out var layer))
            {
                Debug.LogError("Network simulator not available. Driver must have been configured with " +
                                  "NetworkSettings.WithNetworkSimulatorParameters for network simulator to be available.");
            }
            else if (!newParams.Validate())
            {
                Debug.LogError("Modified network simulator parameters are invalid and were not applied.");
            }
            else
            {
                layer.Parameters = newParams;
                driver.m_NetworkSettings.AddRawParameterStruct(ref newParams);
            }
        }
    }
}
