using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Networking.Transport.Analytics;
using Unity.Networking.Transport.Utilities;

namespace Unity.Networking.Transport
{
    internal struct AnalyticsLayer : INetworkLayer
    {
        internal struct Context
        {
            public PacketSizeStatistics PacketSizes;
            public RunningStatistics QueueUsage;
            public ulong TotalBytes;
            public ulong TotalPackets;
            public int OSHeaderOverhead;
        }

        internal NativeReference<Context> RxContext;
        internal NativeReference<Context> TxContext;
        internal BandwidthMonitor RxBandwidthMonitor;
        internal BandwidthMonitor TxBandwidthMonitor;

        public int Initialize(ref NetworkSettings settings, ref ConnectionList connectionList, ref int packetPadding)
        {
            RxContext = new NativeReference<Context>(Allocator.Persistent);
            TxContext = new NativeReference<Context>(Allocator.Persistent);

            var fixedFrameTime = settings.GetNetworkConfigParameters().fixedFrameTimeMS;
            var now = fixedFrameTime > 0 ? 1 : TimerHelpers.GetCurrentTimestampMS();

            RxBandwidthMonitor = new BandwidthMonitor(now, Allocator.Persistent);
            TxBandwidthMonitor = new BandwidthMonitor(now, Allocator.Persistent);

            return 0;
        }

        public void Dispose()
        {
            RxContext.Dispose();
            TxContext.Dispose();

            RxBandwidthMonitor.Dispose();
            TxBandwidthMonitor.Dispose();
        }

        [BurstCompile]
        private struct AnalyticsJob : IJob
        {
            public NativeReference<Context> Context;
            public BandwidthMonitor BandwidthMonitor;
            public PacketsQueue Queue;
            public long Time;

            public void Execute()
            {
                var ctx = Context.Value;

                var bytes = 0;
                var packets = 0;

                var count = Queue.Count;
                for (int i = 0; i < count; ++i)
                {
                    var packetProcessor = Queue[i];
                    if (packetProcessor.Length == 0)
                        continue;
                    
                    var packetSize = packetProcessor.Length + ctx.OSHeaderOverhead;
#if UNITY_WEBGL && !UNITY_EDITOR
                    // WebSocket header needs 2 extra bytes if the payload is larger than 125 bytes
                    // (Note that we presume WebSocket is in use here. It could technically be IPC,
                    // but that's not a common use case for WebGL. Worst case we just uselessly add
                    // 2 bytes of overhead to some packets.)
                    if (packetProcessor.Length > 125)
                        packetSize += 2;
#endif
                    ctx.PacketSizes.AddPacket((uint)packetSize);
                    bytes += packetSize;
                    packets++;
                }

                BandwidthMonitor.AddSample(Time, (ulong)bytes);

                ctx.QueueUsage.AddDataPoint((uint)count);
                ctx.TotalBytes += (ulong)bytes;
                ctx.TotalPackets += (ulong)packets;

                Context.Value = ctx;
            }
        }

        public JobHandle ScheduleReceive(ref ReceiveJobArguments arguments, JobHandle dependency)
        {
            return new AnalyticsJob
            {
                Context = RxContext,
                BandwidthMonitor = RxBandwidthMonitor,
                Queue = arguments.ReceiveQueue,
                Time = arguments.Time,
            }.Schedule(dependency);
        }

        public JobHandle ScheduleSend(ref SendJobArguments arguments, JobHandle dependency)
        {
            return new AnalyticsJob
            {
                Context = TxContext,
                BandwidthMonitor = TxBandwidthMonitor,
                Queue = arguments.SendQueue,
                Time = arguments.Time,
            }.Schedule(dependency);
        }

        internal void SetOSHeaderOverhead(int overhead)
        {
            var rxCtx = RxContext.Value;
            rxCtx.OSHeaderOverhead = overhead;
            RxContext.Value = rxCtx;

            var txCtx = TxContext.Value;
            txCtx.OSHeaderOverhead = overhead;
            TxContext.Value = txCtx;
        }
    }
}