using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Networking.Transport.Utilities;

namespace Unity.Networking.Transport.Analytics
{
    /// <summary>
    /// Structure holding bandwidth-related statistics for the driver and that is returned through
    /// <see cref="NetworkDriver.GetStatistics"/>. All values are in kbit/s.
    /// </summary>
    public struct BandwidthStatistics
    {
        /// <summary>Average bandwidth usage over the lifetime of the driver.</summary>
        /// <value>Bandwidth in kbit/s.</value>
        public float Mean;

        /// <summary>Bandwidth usage over the last second.</summary>
        /// <value>Bandwidth in kbit/s.</value>
        public float Current;

        /// <summary>Minimum bandwidth seen over a complete 1-second period.</summary>
        /// <value>Bandwidth in kbit/s. 0 if less than 1 second of data has been collected.</value>
        public float Minimum;

        /// <summary>Maximum bandwidth seen over a complete 1-second period.</summary>
        /// <value>Bandwidth in kbit/s. 0 if less than 1 second of data has been collected.</value>
        public float Maximum;

        /// <summary>Maximum bandwidth seen over a single update of the driver.</summary>
        /// <value>Bandwidth in kbit/s.</value>
        public float MaximumBurst;
    }

    /// <summary>
    /// Utility object to monitor bandwidth usage over time. Tracks mean bandwidth over the entire
    /// lifetime of the object, minimum and maximum bandwidth over a 1-second period, and the
    /// maximum burst seen (largest bandwidth over smallest time window). Can also report the
    /// "current" bandwidth (usage over the last second).
    /// </summary>
    internal struct BandwidthMonitor : IDisposable
    {
        private const float k_BitsPerByte = 8.0f;
        private const long k_PeriodLength = 1000;

        // Might seem weird to have this as a function instead of just multiplying by a constant
        // whenever we need to do the conversion, but since 8/1000 is not exactly representable as
        // float doing the multiplication by 8 before dividing by 1000 helps keeping the results
        // exact which is particularly helpful in tests.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float BytesToKilobits(ulong bytes) => (bytes * k_BitsPerByte) / 1000.0f;

        private struct Sample
        {
            public long Time;
            public ulong Bytes;
        }

        // It's pretty weird to have all this in a struct and storing it in some NativeReference
        // instead of just having private fields, but without doing this we can't pass monitors to
        // jobs (at least not without breaking their functionality entirely).
        private struct InternalData
        {
            public ulong TotalBytes;
            public ulong PeriodBytes;

            public ulong MinimumPeriodBytes;
            public ulong MaximumPeriodBytes;

            public long StartTime;
            public long LastSampleTime;

            public float MaximumBurst;
        }

        private NativeDynamicRingQueue<Sample> m_Samples;
        private NativeReference<InternalData> m_InternalData;

        /// <summary>Create a new bandwidth monitor.</summary>
        /// <param name="start">Starting timestamp in milliseconds.</param>
        /// <param name="allocator">Allocator to use for any internal memory needs.</param>
        public BandwidthMonitor(long start, Allocator allocator)
        {
            // Initial capacity chosen so that at 60 updates per second, we won't need to resize the
            // ring queue, plus some extra buffer just in case and to make a nice round number.
            m_Samples = new NativeDynamicRingQueue<Sample>(64, allocator);

            var data = new InternalData();
            data.MinimumPeriodBytes = ulong.MaxValue;
            data.MaximumPeriodBytes = ulong.MinValue;
            data.StartTime = start;
            data.LastSampleTime = start;
            m_InternalData = new NativeReference<InternalData>(data, allocator);
        }

        /// <summary>Whether the current monitor is a valid object.</summary>
        public bool IsCreated => m_Samples.IsCreated;

        /// <summary>Get the current bandwidth statistics from the monitor.</summary>
        /// <returns>Current bandwidth statistics.</returns>
        public BandwidthStatistics GetStatistics()
        {
            CheckIsCreated();

            var data = m_InternalData.Value;
            var timeDelta = data.LastSampleTime - data.StartTime;

            return new BandwidthStatistics
            {
                Mean = (data.TotalBytes * k_BitsPerByte) / math.max(1.0f, timeDelta),
                Current = BytesToKilobits(data.PeriodBytes),
                Minimum = timeDelta >= k_PeriodLength ? BytesToKilobits(data.MinimumPeriodBytes) : 0.0f,
                Maximum = timeDelta >= k_PeriodLength ? BytesToKilobits(data.MaximumPeriodBytes) : 0.0f,
                MaximumBurst = data.MaximumBurst
            };
        }

        /// <summary>Add a new measurement to the bandwidth monitor.</summary>
        /// <remarks>
        /// The time parameter should be monotonically increasing. If a sample is added with a
        /// timestamp earlier than the latest sample, it will throw off the monitored statistics.
        /// In the editor, this will actually throw an exception to help catch such issues.
        /// </remarks>
        /// <param name="time">Timestamp of the measurement in milliseconds.</param>
        /// <param name="bytes">Number of bytes transferred.</param>
        public void AddSample(long time, ulong bytes)
        {
            CheckIsCreated();
            CheckSampleTime(time);

            var data = m_InternalData.Value;

            // Compute the burst bandwidth and update our maximum if needed.
            var sampleInterval = math.max(1.0f, time - data.LastSampleTime);
            var sampleBandwidth = (bytes * k_BitsPerByte) / sampleInterval;
            data.MaximumBurst = math.max(data.MaximumBurst, sampleBandwidth);

            data.LastSampleTime = time;
            data.TotalBytes += bytes;
            data.PeriodBytes += bytes;

            m_Samples.Enqueue(new Sample { Time = time, Bytes = bytes });

            // Remove samples older than 1 second.
            while (m_Samples.TryPeek(out var sample) && sample.Time <= time - k_PeriodLength)
            {
                m_Samples.TryDequeue(out _);
                data.PeriodBytes -= sample.Bytes;
            }

            // Update the minimum/maximum values, but only if we've had a full period.
            if (time - data.StartTime >= k_PeriodLength)
            {
                data.MinimumPeriodBytes = math.min(data.MinimumPeriodBytes, data.PeriodBytes);
                data.MaximumPeriodBytes = math.max(data.MaximumPeriodBytes, data.PeriodBytes);
            }

            m_InternalData.Value = data;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (m_Samples.IsCreated)
            {
                m_Samples.Dispose();
                m_InternalData.Dispose();
            }
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        private void CheckIsCreated()
        {
            if (!IsCreated)
                throw new ObjectDisposedException("BandwidthMonitor is not created or has been disposed.");
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        private void CheckSampleTime(long time)
        {
            if (time < 0)
                throw new ArgumentException("New sample time cannot be negative.");
            if (time < m_InternalData.Value.StartTime)
                throw new ArgumentException("New sample time cannot be before the monitor start time.");
            if (time < m_InternalData.Value.LastSampleTime)
                throw new ArgumentException("New sample times must be monotonically increasing.");
        }
    }
}