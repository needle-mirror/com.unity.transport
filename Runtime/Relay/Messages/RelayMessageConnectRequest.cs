using System.Runtime.InteropServices;

namespace Unity.Networking.Transport.Relay
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct RelayMessageConnectRequest
    {
        public const int Length = RelayMessageHeader.Length + RelayAllocationId.k_Length + 1 + RelayConnectionData.k_Length; // Header + AllocationId + ToConnectionDataLength + ToConnectionData;

        public RelayMessageHeader Header;

        public RelayAllocationId AllocationId;
        public byte ToConnectionDataLength;
        public RelayConnectionData ToConnectionData;

        internal static RelayMessageConnectRequest Create(RelayAllocationId allocationId, RelayConnectionData toConnectionData)
        {
            return new RelayMessageConnectRequest
            {
                Header = RelayMessageHeader.Create(RelayMessageType.ConnectRequest),
                AllocationId = allocationId,
                ToConnectionDataLength = 255,
                ToConnectionData = toConnectionData,
            };
        }
    }
}
