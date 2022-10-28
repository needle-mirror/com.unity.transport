using System;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Networking.Transport.Logging;
using Random = Unity.Mathematics.Random;

namespace Unity.Networking.Transport.Utilities
{
    public static class SimulatorStageParameterExtensions
    {
        /// <summary>Set the parameters of the simulator pipeline stage.</summary>
        /// <param name="maxPacketCount"><inheritdoc cref="SimulatorUtility.Parameters.MaxPacketCount"/></param>
        /// <param name="maxPacketSize">See <see cref="SimulatorUtility.Parameters.MaxPacketSize"/> for details. Defaults to MTU.</param>
        /// <param name="mode">Whether to apply simulation to received or sent packets (defaults to both).</param>
        /// <param name="packetDelayMs"><inheritdoc cref="SimulatorUtility.Parameters.PacketDelayMs"/></param>
        /// <param name="packetJitterMs"><inheritdoc cref="SimulatorUtility.Parameters.PacketJitterMs"/></param>
        /// <param name="packetDropInterval"><inheritdoc cref="SimulatorUtility.Parameters.PacketDropInterval"/></param>
        /// <param name="packetDropPercentage"><inheritdoc cref="SimulatorUtility.Parameters.PacketDropPercentage"/></param>
        /// <param name="packetDuplicationPercentage"><inheritdoc cref="SimulatorUtility.Parameters.PacketDuplicationPercentage"/></param>
        /// <param name="fuzzFactor"><inheritdoc cref="SimulatorUtility.Parameters.FuzzFactor"/></param>
        /// <param name="fuzzOffset"><inheritdoc cref="SimulatorUtility.Parameters.FuzzOffset"/></param>
        /// <param name="randomSeed"><inheritdoc cref="SimulatorUtility.Parameters.RandomSeed"/></param>
        public static ref NetworkSettings WithSimulatorStageParameters(
            ref this NetworkSettings settings,
            int maxPacketCount,
            int maxPacketSize = NetworkParameterConstants.MTU,
            ApplyMode mode = ApplyMode.AllPackets,
            int packetDelayMs = 0,
            int packetJitterMs = 0,
            int packetDropInterval = 0,
            int packetDropPercentage = 0,
            int packetDuplicationPercentage = 0,
            int fuzzFactor = 0,
            int fuzzOffset = 0,
            uint randomSeed = 0
        )
        {
            var parameter = new SimulatorUtility.Parameters
            {
                MaxPacketCount = maxPacketCount,
                MaxPacketSize = maxPacketSize,
                Mode = mode,
                PacketDelayMs = packetDelayMs,
                PacketJitterMs = packetJitterMs,
                PacketDropInterval = packetDropInterval,
                PacketDropPercentage = packetDropPercentage,
                PacketDuplicationPercentage = packetDuplicationPercentage,
                FuzzFactor = fuzzFactor,
                FuzzOffset = fuzzOffset,
                RandomSeed = randomSeed,
            };

            settings.AddRawParameterStruct(ref parameter);

            return ref settings;
        }

        public static SimulatorUtility.Parameters GetSimulatorStageParameters(ref this NetworkSettings settings)
        {
            // TODO: Pipelines need to store always all possible pipeline parameters, even when they are not used.
            // That means that we always need to provide a parameter for every pipeline.
            settings.TryGet<SimulatorUtility.Parameters>(out var parameters);
            return parameters;
        }

        // TODO This ModifySimulatorStageParameters() extension method is NOT a pattern we want
        //      repeated throughout the code. At some point we'll want to deprecate it and replace
        //      it with a proper general mechanism to modify settings at runtime (see MTT-4161).

        /// <summary>Modify the parameters of the simulator pipeline stage.</summary>
        /// <remarks>
        /// Some parameters (e.g. max packet count and size) are not modifiable. These need to be
        /// passed unmodified to this function (can't just leave them at 0 for example). The current
        /// parameters can be obtained using <see cref="NetworkDriver.CurrentSettings" />.
        /// </remarks>
        /// <param name="newParams">New parameters for the simulator stage.</param>
        public static unsafe void ModifySimulatorStageParameters(this NetworkDriver driver, SimulatorUtility.Parameters newParams)
        {
            var stageId = NetworkPipelineStageId.Get<SimulatorPipelineStage>();
            var currentParams = driver.GetWriteablePipelineParameter<SimulatorUtility.Parameters>(default, stageId);

            if (currentParams->MaxPacketCount != newParams.MaxPacketCount)
            {
                DebugLog.LogError("Simulator stage maximum packet count can't be modified.");
                return;
            }

            if (currentParams->MaxPacketSize != newParams.MaxPacketSize)
            {
                DebugLog.LogError("Simulator stage maximum packet size can't be modified.");
                return;
            }

            *currentParams = newParams;
            driver.m_NetworkSettings.AddRawParameterStruct(ref newParams);
        }
    }

    /// <summary>
    /// Denotes whether or not the <see cref="SimulatorPipelineStage"> should apply to sent or received packets (or both).
    /// </summary>
    public enum ApplyMode : byte
    {
        // We put received packets first so that the default value will match old behavior.
        /// <summary>Only apply simulation to received packets.</summary>
        ReceivedPacketsOnly,
        /// <summary>Only apply simulation to sent packets.</summary>
        SentPacketsOnly,
        /// <summary>Apply simulation to both sent and received packets.</summary>
        AllPackets,
        /// <summary>For runtime toggling.</summary>
        Off,
    }

    public static class SimulatorUtility
    {
        /// <summary>
        /// Configuration parameters for the simulator pipeline stage.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct Parameters : INetworkParameter
        {
            /// <summary>
            /// The maximum amount of packets the pipeline can keep track of. This used when a
            /// packet is delayed, the packet is stored in the pipeline processing buffer and can
            /// be later brought back.
            /// </summary>
            public int MaxPacketCount;

            /// <summary>
            /// The maximum size of a packet which the simulator stores. If a packet exceeds this size it will
            /// bypass the simulator.
            /// </summary>
            public int MaxPacketSize;

            /// <summary>
            /// Value to use to seed the random number generator. For non-deterministic behavior, use a
            /// dynamic value here (e.g. the result of a call to Stopwatch.GetTimestamp).
            /// </summary>
            public uint RandomSeed;

            /// <inheritdoc cref="ApplyMode"/>
            public ApplyMode Mode;

            /// <summary>
            /// Fixed delay to apply to all packets which pass through.
            /// </summary>
            public int PacketDelayMs;
            /// <summary>
            /// Variable delay to apply to all packets which pass through, adds or subtracts amount from fixed delay.
            /// </summary>
            public int PacketJitterMs;
            /// <summary>
            /// Fixed interval to drop packets on. This is most suitable for tests where predictable
            /// behaviour is desired, every Xth packet will be dropped. If PacketDropInterval is 5
            /// every 5th packet is dropped.
            /// </summary>
            public int PacketDropInterval;
            /// <summary>
            /// 0 - 100, denotes the percentage of packets that will be dropped (i.e. deleted unprocessed).
            /// E.g. "5" means approximately every 20th packet will be dropped.
            /// <see cref="RandomSeed"/> to change random seed values.
            /// </summary>
            public int PacketDropPercentage;
            /// <summary>
            /// 0 - 100, denotes the percentage of packets that will be duplicated once.
            /// E.g. "5" means approximately every 20th packet will be duplicated once.
            /// <see cref="RandomSeed"/> to change random seed values.
            /// Note: Skipped if the packet is dropped.
            /// </summary>
            public int PacketDuplicationPercentage;
            /// <summary>
            /// Use the fuzz factor when you want to fuzz a packet. For every packet
            /// a random number generator is used to determine if the packet should have the internal bits flipped.
            /// A percentage of 5 means approximately every 20th packet will be fuzzed, and that each bit in the packet
            /// has a 5 percent chance to get flipped.
            /// </summary>
            public int FuzzFactor;
            /// <summary>
            /// Use the fuzz offset in conjunction with the fuzz factor, the fuzz offset will offset where we start
            /// flipping bits. This is useful if you want to only fuzz a part of the packet.
            /// </summary>
            public int FuzzOffset;

            public bool Validate() => true;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Context
        {
            public Random Random;

            // Statistics
            public int PacketCount;
            public int PacketDropCount;
            public int PacketDuplicatedCount;
            public int ReadyPackets;
            public int WaitingPackets;
            public long NextPacketTime;
            public long StatsTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DelayedPacket
        {
            public int processBufferOffset;
            public ushort packetSize;
            public ushort packetHeaderPadding;
            public long delayUntil;
        }

        internal static unsafe void InitializeContext(Parameters param, byte* sharedProcessBuffer)
        {
            // Store parameters in the shared buffer space
            Context* ctx = (Context*)sharedProcessBuffer;
            ctx->PacketDropCount = 0;
            ctx->Random = new Random();
            if (param.RandomSeed > 0)
                ctx->Random.InitState(param.RandomSeed);
            else
                ctx->Random.InitState();
        }

        private static unsafe bool GetEmptyDataSlot(NetworkPipelineContext ctx, byte* processBufferPtr, ref int packetPayloadOffset,
            ref int packetDataOffset)
        {
            var param = *(Parameters*)ctx.staticInstanceBuffer;
            var dataSize = UnsafeUtility.SizeOf<DelayedPacket>();
            var packetPayloadStartOffset = param.MaxPacketCount * dataSize;

            bool foundSlot = false;
            for (int i = 0; i < param.MaxPacketCount; i++)
            {
                packetDataOffset = dataSize * i;
                DelayedPacket* packetData = (DelayedPacket*)(processBufferPtr + packetDataOffset);

                // Check if this slot is empty
                if (packetData->delayUntil == 0)
                {
                    foundSlot = true;
                    packetPayloadOffset = packetPayloadStartOffset + param.MaxPacketSize * i;
                    break;
                }
            }

            return foundSlot;
        }

        internal static unsafe bool GetDelayedPacket(ref NetworkPipelineContext ctx, ref InboundSendBuffer delayedPacket,
            ref NetworkPipelineStage.Requests requests, long currentTimestamp)
        {
            requests = NetworkPipelineStage.Requests.None;
            var param = *(Parameters*)ctx.staticInstanceBuffer;

            var dataSize = UnsafeUtility.SizeOf<DelayedPacket>();
            byte* processBufferPtr = (byte*)ctx.internalProcessBuffer;
            var simCtx = (Context*)ctx.internalSharedProcessBuffer;
            int oldestPacketIndex = -1;
            long oldestTime = long.MaxValue;
            int readyPackets = 0;
            int packetsInQueue = 0;
            for (int i = 0; i < param.MaxPacketCount; i++)
            {
                DelayedPacket* packet = (DelayedPacket*)(processBufferPtr + dataSize * i);
                if ((int)packet->delayUntil == 0) continue;
                packetsInQueue++;

                if (packet->delayUntil > currentTimestamp) continue;
                readyPackets++;

                if (oldestTime <= packet->delayUntil) continue;
                oldestPacketIndex = i;
                oldestTime = packet->delayUntil;
            }

            simCtx->ReadyPackets = readyPackets;
            simCtx->WaitingPackets = packetsInQueue;
            simCtx->NextPacketTime = oldestTime;
            simCtx->StatsTime = currentTimestamp;

            // If more than one item has expired timer we need to resume this pipeline stage
            if (readyPackets > 1)
            {
                requests |= NetworkPipelineStage.Requests.Resume;
            }
            // If more than one item is present (but doesn't have expired timer) we need to re-run the pipeline
            // in a later update call
            else if (packetsInQueue > 0)
            {
                requests |= NetworkPipelineStage.Requests.Update;
            }

            if (oldestPacketIndex >= 0)
            {
                DelayedPacket* packet = (DelayedPacket*)(processBufferPtr + dataSize * oldestPacketIndex);
                packet->delayUntil = 0;

                delayedPacket.bufferWithHeaders = ctx.internalProcessBuffer + packet->processBufferOffset;
                delayedPacket.bufferWithHeadersLength = packet->packetSize;
                delayedPacket.headerPadding = packet->packetHeaderPadding;
                delayedPacket.SetBufferFromBufferWithHeaders();
                return true;
            }

            return false;
        }

        internal static unsafe void FuzzPacket(Context *ctx, ref Parameters param, ref InboundSendBuffer inboundBuffer)
        {
            int fuzzFactor = param.FuzzFactor;
            int fuzzOffset = param.FuzzOffset;
            int rand = ctx->Random.NextInt(0, 100);
            if (rand > fuzzFactor)
                return;

            var length = inboundBuffer.bufferLength;
            for (int i = fuzzOffset; i < length; ++i)
            {
                for (int j = 0; j < 8; ++j)
                {
                    if (fuzzFactor > ctx->Random.NextInt(0, 100))
                    {
                        inboundBuffer.buffer[i] ^= (byte)(1 << j);
                    }
                }
            }
        }

        /// <summary>Storing it twice will trigger a resend.</summary>
        internal static unsafe bool TryDelayPacket(ref NetworkPipelineContext ctx, ref Parameters param, ref InboundSendBuffer inboundBuffer,
            ref NetworkPipelineStage.Requests requests,
            long timestamp)
        {
            var simCtx = (Context*)ctx.internalSharedProcessBuffer;

            // Find empty slot in bookkeeping data space to track this packet
            int packetPayloadOffset = 0;
            int packetDataOffset = 0;
            var processBufferPtr = (byte*)ctx.internalProcessBuffer;
            bool foundSlot = GetEmptyDataSlot(ctx, processBufferPtr, ref packetPayloadOffset, ref packetDataOffset);

            if (!foundSlot)
            {
                DebugLog.SimulatorNoSpace(param.MaxPacketCount);
                return false;
            }

            UnsafeUtility.MemCpy(ctx.internalProcessBuffer + packetPayloadOffset + inboundBuffer.headerPadding, inboundBuffer.buffer, inboundBuffer.bufferLength);

            // Add tracking for this packet so we can resurrect later
            DelayedPacket packet;
            var addedDelay = math.max(0, param.PacketDelayMs + simCtx->Random.NextInt(param.PacketJitterMs * 2) - param.PacketJitterMs);
            packet.delayUntil = timestamp + addedDelay;
            packet.processBufferOffset = packetPayloadOffset;
            packet.packetSize = (ushort)(inboundBuffer.headerPadding + inboundBuffer.bufferLength);
            packet.packetHeaderPadding = (ushort)inboundBuffer.headerPadding;
            byte* packetPtr = (byte*)&packet;
            UnsafeUtility.MemCpy(processBufferPtr + packetDataOffset, packetPtr, UnsafeUtility.SizeOf<DelayedPacket>());

            // Schedule an update call so packet can be resurrected later
            requests |= NetworkPipelineStage.Requests.Update;
            return true;
        }

        /// <summary>
        /// Optimization.
        /// We want to skip <see cref="TryDelayPacket"/> in the case where we have no delay to avoid mem-copies.
        /// Also ensures requests are updated if there are other packets in the store.
        /// </summary>
        /// <returns>True if we can skip delaying this packet.</returns>
        internal static unsafe bool TrySkipDelayingPacket(ref Parameters param, ref NetworkPipelineStage.Requests requests, Context* simCtx)
        {
            if (param.PacketDelayMs == 0 && param.PacketJitterMs == 0)
            {
                if (simCtx->WaitingPackets > 0)
                    requests |= NetworkPipelineStage.Requests.Update;
                return true;
            }
            return false;
        }

        internal static unsafe bool ShouldDropPacket(Context* ctx, Parameters param, long timestamp)
        {
            if (param.PacketDropInterval > 0 && ((ctx->PacketCount - 1) % param.PacketDropInterval) == 0)
                return true;
            if (param.PacketDropPercentage > 0)
            {
                var chance = ctx->Random.NextInt(0, 100);
                if (chance < param.PacketDropPercentage)
                    return true;
            }

            return false;
        }

        internal static unsafe bool ShouldDuplicatePacket(Context* ctx, ref Parameters param)
        {
            if (param.PacketDuplicationPercentage > 0)
            {
                var chance = ctx->Random.NextInt(0, 100);
                if (chance < param.PacketDuplicationPercentage)
                    return true;
            }

            return false;
        }
    }
}
