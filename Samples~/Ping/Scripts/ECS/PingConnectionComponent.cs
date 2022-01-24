using Unity.Entities;
using Unity.Networking.Transport;

namespace Unity.Networking.Transport.Samples
{
    struct PingServerConnectionComponentData : IComponentData
    {
        public NetworkConnection connection;
    }

    struct PingClientConnectionComponentData : IComponentData
    {
        public NetworkConnection connection;
    }
}
