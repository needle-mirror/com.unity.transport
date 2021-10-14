#if ENABLE_RELAY

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Networking.Transport.Relay;


public static class RelayUtilities
{
   public static RelayAllocationId ConvertFromAllocationIdBytes(byte[] allocationIdBytes)
    {
        unsafe
        {
            fixed (byte* ptr = allocationIdBytes)
            {
                return RelayAllocationId.FromBytePointer(ptr, allocationIdBytes.Length);
            }
        }
    }

    public static RelayHMACKey ConvertFromHMAC(byte[] hmac)
    {
        unsafe
        {
            fixed (byte* ptr = hmac)
            {
                return RelayHMACKey.FromBytePointer(ptr, RelayHMACKey.k_Length);
            }
        }
    }

    public static RelayConnectionData ConvertConnectionData(byte[] connectionData)
    {
        unsafe
        {
            fixed (byte* ptr = connectionData)
            {
                return RelayConnectionData.FromBytePointer(ptr, RelayConnectionData.k_Length);
            }
        }
    }
}

#endif
