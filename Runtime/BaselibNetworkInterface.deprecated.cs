using Unity.Jobs;
using System;
using Unity.Networking.Transport.Logging;

namespace Unity.Networking.Transport
{
    /// <summary>Obsolete extensions for network interface parameters.</summary>
    public static class BaselibNetworkParameterExtensions
    {
        /// <summary>(Obsolete) Modify the network interface parameters.</summary>
        /// <param name="settings">Network settings to modify.</param>
        /// <param name="receiveQueueCapacity">Receive queue capacity.</param>
        /// <param name="sendQueueCapacity">Send queue capacity.</param>
        /// <param name="maximumPayloadSize">Maximum payload size.</param>
        /// <returns>Modified network settings.</returns>
        [Obsolete("To set receiveQueueCapacity and sendQueueCapacity parameters use WithNetworkConfigParameters()", false)]
        public static ref NetworkSettings WithBaselibNetworkInterfaceParameters(
            ref this NetworkSettings settings,
            int receiveQueueCapacity = 0,
            int sendQueueCapacity = 0,
            uint maximumPayloadSize = 0
        )
        {
            var parameter = new BaselibNetworkParameter
            {
                receiveQueueCapacity = receiveQueueCapacity,
                sendQueueCapacity = sendQueueCapacity,
                maximumPayloadSize = maximumPayloadSize,
            };

            settings.AddRawParameterStruct(ref parameter);

            return ref settings;
        }
    }

    /// <summary>Obsolete. Set the receive/send queue capacities with <see cref="NetworkConfigParameter"/> instead.</summary>
    [Obsolete("To set receiveQueueCapacity and sendQueueCapacity parameters use NetworkConfigParameter", false)]
    public struct BaselibNetworkParameter : INetworkParameter
    {
        /// <summary>Receive queue capacity.</summary>
        public int receiveQueueCapacity;

        /// <summary>Send queue capacity.</summary>
        public int sendQueueCapacity;

        /// <summary>Maximum payload size.</summary>
        public uint maximumPayloadSize;

        /// <inheritdoc/>
        public bool Validate()
        {
            var valid = true;

            if (receiveQueueCapacity <= 0)
            {
                valid = false;
                DebugLog.ErrorValueIsNegative("ReceiveQueueCapacity", receiveQueueCapacity);
            }
            if (sendQueueCapacity <= 0)
            {
                valid = false;
                DebugLog.ErrorValueIsNegative("SendQueueCapacity", sendQueueCapacity);
            }

            return valid;
        }
    }

    /// <summary>Obsolete. Use <see cref="UDPNetworkInterface"/> instead.</summary>
    [Obsolete("BaselibNetworkInterface has been deprecated. Use UDPNetworkInterface instead (UnityUpgradable) -> UDPNetworkInterface")]
    public struct BaselibNetworkInterface : INetworkInterface
    {
        /// <inheritdoc/>
        public NetworkEndpoint LocalEndpoint
            => throw new System.NotImplementedException();

        /// <inheritdoc/>
        public int Initialize(ref NetworkSettings settings, ref int packetPadding)
            => throw new System.NotImplementedException();

        /// <inheritdoc/>
        public JobHandle ScheduleReceive(ref ReceiveJobArguments arguments, JobHandle dep)
            => throw new System.NotImplementedException();

        /// <inheritdoc/>
        public JobHandle ScheduleSend(ref SendJobArguments arguments, JobHandle dep)
            => throw new System.NotImplementedException();

        /// <inheritdoc/>
        public int Bind(NetworkEndpoint endpoint)
            => throw new System.NotImplementedException();

        /// <inheritdoc/>
        public int Listen()
            => throw new System.NotImplementedException();

        /// <inheritdoc/>
        public unsafe void Dispose()
            => throw new System.NotImplementedException();
    }
}
