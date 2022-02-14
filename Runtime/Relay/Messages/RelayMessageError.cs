using System.Runtime.InteropServices;

namespace Unity.Networking.Transport.Relay
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct RelayMessageError
    {
        public const int Length = RelayMessageHeader.Length + RelayAllocationId.k_Length + sizeof(byte); // Header + AllocationId + ErrorCode

        public RelayMessageHeader Header;

        public RelayAllocationId AllocationId;
        public byte ErrorCode;

        internal static RelayMessageError Create(RelayAllocationId allocationId, byte errorCode)
        {
            return new RelayMessageError
            {
                Header = RelayMessageHeader.Create(RelayMessageType.Error),
                AllocationId = allocationId,
                ErrorCode = errorCode
            };
        }
    }
}
