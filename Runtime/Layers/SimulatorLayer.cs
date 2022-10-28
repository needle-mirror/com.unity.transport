using Unity.Collections;
using Unity.Burst;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Networking.Transport.Logging;
using Unity.Networking.Transport.Utilities;
using UnityEngine;

namespace Unity.Networking.Transport
{
    using Random = Unity.Mathematics.Random;

    internal struct SimulatorLayer : INetworkLayer
    {
        // Need to store parameters in a native reference if they are to be modified since
        // ModifyNetworkSimulatorParameters() only gets a copy of the layer structure.
        private NativeReference<NetworkSimulatorParameter> m_Parameters;

        public NetworkSimulatorParameter Parameters
        {
            private get => m_Parameters.Value;
            set => m_Parameters.Value = value;
        }

        public int Initialize(ref NetworkSettings settings, ref ConnectionList connectionList, ref int packetPadding)
        {
            // Can ignore the result of TryGet since simulator layer is only added if it's true.
            settings.TryGet<NetworkSimulatorParameter>(out var parameters);

            m_Parameters = new NativeReference<NetworkSimulatorParameter>(parameters, Allocator.Persistent);

            return 0;
        }

        public void Dispose()
        {
            m_Parameters.Dispose();
        }

        public JobHandle ScheduleReceive(ref ReceiveJobArguments arguments, JobHandle dependency)
        {
            if (Parameters.ReceivePacketLossPercent == 0.0f)
                return dependency;

            return new SimulatorJob
            {
                Packets = arguments.ReceiveQueue,
                PacketLoss = Parameters.ReceivePacketLossPercent,
            }.Schedule(dependency);
        }

        public JobHandle ScheduleSend(ref SendJobArguments arguments, JobHandle dependency)
        {
            if (Parameters.SendPacketLossPercent == 0.0f)
                return dependency;

            return new SimulatorJob
            {
                Packets = arguments.SendQueue,
                PacketLoss = Parameters.SendPacketLossPercent,
            }.Schedule(dependency);
        }

        [BurstCompile]
        private struct SimulatorJob : IJob
        {
            public PacketsQueue Packets;
            public float PacketLoss;

            public void Execute()
            {
                var random = new Random((uint)TimerHelpers.GetTicks());

                var count = Packets.Count;
                for (int i = 0; i < count; i++)
                {
                    if (random.NextFloat(100.0f) < PacketLoss)
                        Packets[i].Drop();
                }
            }
        }
    }

    /// <summary>Parameters for the global network simulator.</summary>
    /// <remarks>
    /// These parameters are for the global network simulator, which applies to all traffic going
    /// through a <see cref="NetworkDriver" /> (including control traffic). For the parameters of
    /// <see cref="SimulatorPipelineStage" />, refer to <see cref="SimulatorUtility.Parameters" />.
    ///
    /// We recommend using <see cref="SimulatorPipelineStage" /> to simulate network conditions as
    /// it has more features than the global one (which is only intended for specialized use cases).
    /// </remarks>
    public struct NetworkSimulatorParameter : INetworkParameter
    {
        /// <summary>Percentage of received packets to drop (0-100).</summary>
        public float ReceivePacketLossPercent;
        /// <summary>Percentage of sent packets to drop (0-100).</summary>
        public float SendPacketLossPercent;

        /// <summary>Validate the network simulator parameters.</summary>
        public bool Validate()
        {
            if (ReceivePacketLossPercent < 0.0f || ReceivePacketLossPercent > 100.0f)
            {
                DebugLog.LogError($"{nameof(ReceivePacketLossPercent)} value ({ReceivePacketLossPercent}) must be between 0 and 100.");
                return false;
            }

            if (SendPacketLossPercent < 0.0f || SendPacketLossPercent > 100.0f)
            {
                DebugLog.LogError($"{nameof(SendPacketLossPercent)} value ({SendPacketLossPercent}) must be between 0 and 100.");
                return false;
            }

            return true;
        }
    }

    public static class NetworkSimulatorParameterExtensions
    {
        /// <summary>Set the global network simulator parameters.</summary>
        /// <remarks>
        /// This is not the recommended way of configuring simulated network conditions. See
        /// <see cref="NetworkSimulatorParameter" /> for details.
        /// </remarks>
        /// <param name="receivePacketLossPercent">Percentage of received packets to drop.</param>
        /// <param name="sendPacketLossPercent">Percentage of sent packets to drop.</param>
        public static ref NetworkSettings WithNetworkSimulatorParameters(
            ref this NetworkSettings settings,
            float receivePacketLossPercent = 0.0f,
            float sendPacketLossPercent    = 0.0f)
        {
            var parameters = new NetworkSimulatorParameter
            {
                ReceivePacketLossPercent = receivePacketLossPercent,
                SendPacketLossPercent    = sendPacketLossPercent,
            };

            settings.AddRawParameterStruct(ref parameters);
            return ref settings;
        }

        // TODO This ModifyNetworkSimulatorParameters() extension method is NOT a pattern we want
        //      repeated throughout the code. At some point we'll want to deprecate it and replace
        //      it with a proper general mechanism to modify settings at runtime (see MTT-4161).

        /// <summary>Modify the parameters of the global network simulator.</summary>
        /// <param name="newParams">New parameters for the simulator.</param>
        public static void ModifyNetworkSimulatorParameters(this NetworkDriver driver, NetworkSimulatorParameter newParams)
        {
            if (!driver.m_NetworkStack.TryGetLayer<SimulatorLayer>(out var layer))
            {
                DebugLog.LogError("Network simulator not available. Driver must have been configured with " +
                                  "NetworkSettings.WithNetworkSimulatorParameters for network simulator to be available.");
            }
            else if (!newParams.Validate())
            {
                DebugLog.LogError("Modified network simulator parameters are invalid and were not applied.");
            }
            else
            {
                layer.Parameters = newParams;
                driver.m_NetworkSettings.AddRawParameterStruct(ref newParams);
            }
        }
    }
}