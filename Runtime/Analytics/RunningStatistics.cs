using Unity.Mathematics;

namespace Unity.Networking.Transport.Analytics
{
    /// <summary>Holds running statistics (mean, variance, etc.) for a series of values.</summary>
    internal struct RunningStatistics
    {
        // We use Welford's algorithm to compute the running mean/variance, see those for details:
        //     https://en.wikipedia.org/wiki/Algorithms_for_calculating_variance
        //     https://www.johndcook.com/blog/standard_deviation/
        //
        // We know exactly what kind of data we're dealing with in UTP (packet sizes and latencies)
        // so we don't bother having any type flexibility here. If this code were to be reused
        // elsewhere, it might be worth making it generic and changing all those floats to doubles.

        private uint m_Count;

        private uint m_Minimum;
        private uint m_Maximum;

        private float m_Mean;
        private float m_MeanDist2;

        /// <summary>Minimum value encountered in the data points added so far.</summary>
        public uint Minimum => m_Minimum;

        /// <summary>Maximum value encountered in the data points added so far.</summary>
        public uint Maximum => m_Maximum;

        /// <summary>Average value of the data points added so far.</summary>
        public float Mean => m_Mean;

        /// <summary>Standard deviation of the data points added so far.</summary>
        public float StandardDeviation => math.sqrt(Variance);

        /// <summary>Variance in the data points added so far.</summary>
        public float Variance => m_Count <= 1 ? 0.0f : m_MeanDist2 / (m_Count - 1);

        /// <summary>Add a new data point to the running statistics.</summary>
        /// <param name="value">Value of the data point to add.</param>
        public void AddDataPoint(uint value)
        {
            m_Count++;

            // Using <= here covers rollover of the count. It would be unexpected to see this in
            // practice, but if we do, we'll just reset the statistics instead of dividing by 0.
            if (m_Count <= 1)
            {
                m_Count = 1;
                m_Minimum = value;
                m_Maximum = value;
                m_Mean = value;
                m_MeanDist2 = 0.0f;
            }
            else
            {
                if (value < m_Minimum)
                    m_Minimum = value;
                if (value > m_Maximum)
                    m_Maximum = value;

                var valueF = (float)value;
                var delta = valueF - m_Mean;
                m_Mean += delta / m_Count;
                var delta2 = valueF - m_Mean;
                m_MeanDist2 += delta * delta2;
            }
        }

        /// <summary>Merge other running statistics into this one.</summary>
        /// <remarks>
        /// Avoid calling this repeatedly to update the same statistics. Ideally this should only be
        /// called to merge a few sources of data before displaying/logging/returning them, and not
        /// to "maintain" statistics over time. The reason is that the calculations here might not
        /// be as numerically stable as adding data points one by one.
        /// </remarks>
        /// <param name="other">Running statistics to merge.</param>
        public void Merge(RunningStatistics other)
        {
            if (other.m_Count == 0)
                return;

            if (m_Count == 0)
            {
                this = other;
                return;
            }

            var newCount = m_Count + other.m_Count;
            var newMean = (m_Mean * m_Count + other.m_Mean * other.m_Count) / newCount;

            // https://math.stackexchange.com/a/2971563
            var newMeanDelta = m_Mean - newMean;
            var newMeanDeltaOther = other.m_Mean - newMean;
            var newMeanDist2 = m_Count * newMeanDelta * newMeanDelta +
                other.m_Count * newMeanDeltaOther * newMeanDeltaOther +
                (m_Count - 1) * Variance + (other.m_Count - 1) * other.Variance;

            m_Count = newCount;
            m_Mean = newMean;
            m_MeanDist2 = newMeanDist2;

            m_Minimum = math.min(m_Minimum, other.m_Minimum);
            m_Maximum = math.max(m_Maximum, other.m_Maximum);
        }
    }
}