using UnityEngine;

namespace Unity.Networking.Transport.Samples
{
    public class SoakClientBehaviour : MonoBehaviour
    {
        [SerializeField] public int SoakClientCount;
        [SerializeField] public int SoakPacketsPerSecond;
        [SerializeField] public int SoakPacketSize;
        [SerializeField] public int SoakDuration;
        [SerializeField] public int SampleTime;

        private SoakClientJobManager m_Manager;
        private float m_SampleExpiresTime;
        private bool m_Running;
        private float m_StartTime;

        private StatisticsReport m_Report;

        void Start()
        {
            m_Manager = new SoakClientJobManager(SoakClientCount, SoakPacketsPerSecond, SoakPacketSize, SoakDuration);
            m_Report = new StatisticsReport(SoakClientCount);
        }

        void OnDestroy()
        {
            m_Manager.Dispose();
            m_Report.Dispose();
        }

        void FixedUpdate()
        {
            if (!m_Running)
                return;

            m_Manager.Sync();
            if (SampleExpired())
            {
                var now = Time.time;
                var sample = m_Manager.Sample();
                for (int i = 0; i < sample.Length; i++)
                {
                    m_Report.AddSample(sample[i], now);
                }
            }

            if (m_Manager.Done())
            {
                Debug.Log("Soak done!");
                m_Running = false;
                m_Manager.Stop();
                Log();
            }

            m_Manager.Update();
        }

        void StartSoak()
        {
            m_Running = true;
            m_SampleExpiresTime = Time.time + SampleTime;
            m_Manager.Start();
            m_StartTime = Time.time;
        }

        void StopSoak()
        {
            m_Running = false;
            m_Manager.Stop();
        }

        bool SampleExpired()
        {
            if (!m_Running)
                return false;

            bool expired = false;
            var now = Time.time;
            if (now > m_SampleExpiresTime)
            {
                expired = true;
                m_SampleExpiresTime = now + SampleTime;
            }
            return expired;
        }

        void Log()
        {
            var gen = new SoakStatisticsReporter();
            gen.GenerateReport(m_Report, m_Manager.ClientInfos());
        }

        void OnGUI()
        {
            if (!m_Running)
            {
                if (GUILayout.Button("Start soaking"))
                {
                    StartSoak();
                }
            }
            else
            {
                GUILayout.Label((int)(Time.time - m_StartTime) + " seconds elapsed");
                if (GUILayout.Button("Stop soaking"))
                {
                    StopSoak();
                }
            }
        }
    }
}
