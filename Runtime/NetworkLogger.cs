using System;
using Unity.Collections;

namespace Unity.Networking.Transport
{
    public interface INetworkLogMessage
    {
        void Print(ref FixedString512 container);
    }

    public struct NetworkLogger : IDisposable
    {
        internal struct LogMessage
        {
            public FixedString512 msg;
            public LogLevel level;
        }
        public enum LogLevel
        {
            None = 0,
            Error,
            Warning,
            Info,
            Debug
        }

        public NetworkLogger(LogLevel level)
        {
            m_Level = level;
            m_PendingLog = new NativeQueue<LogMessage>(Allocator.Persistent);
            m_LogFile = new NativeList<LogMessage>(Allocator.Persistent);
        }

        public void Dispose()
        {
            m_PendingLog.Dispose();
            m_LogFile.Dispose();
        }

        public void FlushPending()
        {
            LogMessage msg;
            while (m_PendingLog.TryDequeue(out msg))
                m_LogFile.Add(msg);
        }

        public void DumpToConsole()
        {
            for (int i = 0; i < m_LogFile.Length; ++i)
            {
                var msg = m_LogFile[i];
                switch (msg.level)
                {
                case LogLevel.Error:
                    UnityEngine.Debug.LogError(msg.msg.ToString());
                    break;
                case LogLevel.Warning:
                    UnityEngine.Debug.LogWarning(msg.msg.ToString());
                    break;
                default:
                    UnityEngine.Debug.Log(msg.msg.ToString());
                    break;
                }
            }
            m_LogFile.Clear();
        }
        public void Clear()
        {
            m_LogFile.Clear();
        }

        public void Log<T>(LogLevel level, T message) where T : struct, INetworkLogMessage
        {
            if ((int) level > (int) m_Level)
                return;
            var msg = default(LogMessage);
            msg.level = level;
            message.Print(ref msg.msg);
            m_PendingLog.Enqueue(msg);
        }
        public void Log(LogLevel level, FixedString512 str)
        {
            if ((int) level > (int) m_Level)
                return;
            var msg = new LogMessage {level = level, msg = str};
            m_PendingLog.Enqueue(msg);
        }

        private LogLevel m_Level;
        private NativeList<LogMessage> m_LogFile;
        private NativeQueue<LogMessage> m_PendingLog;

        public Concurrent ToConcurrent()
        {
            var concurrent = default(Concurrent);
            concurrent.m_PendingLog = m_PendingLog.AsParallelWriter();
            concurrent.m_Level = m_Level;
            return concurrent;
        }

        public struct Concurrent
        {
            public void Log<T>(LogLevel level, T message) where T : struct, INetworkLogMessage
            {
                if ((int) level > (int) m_Level)
                    return;
                var msg = default(LogMessage);
                msg.level = level;
                message.Print(ref msg.msg);
                m_PendingLog.Enqueue(msg);
            }
            public void Log(LogLevel level, FixedString512 str)
            {
                if ((int) level > (int) m_Level)
                    return;
                var msg = new LogMessage {level = level, msg = str};
                m_PendingLog.Enqueue(msg);
            }

            internal NativeQueue<LogMessage>.ParallelWriter m_PendingLog;
            internal LogLevel m_Level;
        }
    }
}