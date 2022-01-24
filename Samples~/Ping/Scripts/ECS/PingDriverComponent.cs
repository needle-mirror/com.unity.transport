using System;
using Unity.Entities;

namespace Unity.Networking.Transport.Samples
{
    [Serializable]
    // A component used to limit when NetworkDrivers are created. The PingDriverSystem uses this to create the
    // NetworkDrivers when requested
    [GenerateAuthoringComponent]
    public struct PingDriverComponentData : IComponentData
    {
        public int isServer;
    }
}
