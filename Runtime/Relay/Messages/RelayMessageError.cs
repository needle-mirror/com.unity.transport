using System.Runtime.InteropServices;

namespace Unity.Networking.Transport.Relay
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct RelayMessageError
    {
        public const int k_Length = RelayMessageHeader.k_Length + RelayAllocationId.k_Length + sizeof(byte); // Header + AllocationId + ErrorCode

        public RelayMessageHeader Header;

        public RelayAllocationId AllocationId;
        public byte ErrorCode;

        public void LogError()
        {
            switch (ErrorCode)
            {
                case 0:
                    UnityEngine.Debug.LogError("Received error message from Relay: invalid protocol version. " +
                        "Make sure your Unity Transport package is up to date.");
                    break;
                case 1:
                    UnityEngine.Debug.LogError("Received error message from Relay: player timed out due to inactivity.");
                    break;
                case 2:
                    UnityEngine.Debug.LogError("Received error message from Relay: unauthorized.");
                    break;
                case 3:
                    UnityEngine.Debug.LogError("Received error message from Relay: allocation ID client mismatch.");
                    break;
                case 4:
                    UnityEngine.Debug.LogError("Received error message from Relay: allocation ID not found.");
                    break;
                case 5:
                    UnityEngine.Debug.LogError("Received error message from Relay: not connected.");
                    break;
                case 6:
                    UnityEngine.Debug.LogError("Received error message from Relay: self-connect not allowed.");
                    break;
                default:
                    UnityEngine.Debug.LogError($"Received error message from Relay with unknown error code {ErrorCode}");
                    break;
            }

            if (ErrorCode == 1 || ErrorCode == 4)
            {
                UnityEngine.Debug.LogError("Relay allocation is invalid. See NetworkDriver.GetRelayConnectionStatus and " +
                    "RelayConnectionStatus.AllocationInvalid for details on how to handle this situation.");
            }
        }
    }
}
