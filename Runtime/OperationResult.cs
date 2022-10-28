using System;
using Unity.Collections;
using Unity.Networking.Transport.Logging;

namespace Unity.Networking.Transport
{
    /// <summary>
    /// Stores the result of a NetworkDriver operation.
    /// </summary>
    public struct OperationResult : IDisposable
    {
        private FixedString64Bytes m_Label;
        private NativeReference<int> m_ErrorCode;

        internal OperationResult(FixedString64Bytes label, Allocator allocator)
        {
            m_Label = label;
            m_ErrorCode = new NativeReference<int>(allocator);
        }

        /// <summary>
        /// Allows to get and set the error code for the operation.
        /// </summary>
        /// <remarks>Setting an error code different to zero will log it.</remarks>
        public int ErrorCode
        {
            get => m_ErrorCode.Value;
            set
            {
                if (value != 0)
                {
                    DebugLog.ErrorOperation(ref m_Label, value);
                }
                m_ErrorCode.Value = value;
            }
        }

        public void Dispose()
        {
            m_ErrorCode.Dispose();
        }
    }
}
