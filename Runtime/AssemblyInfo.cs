using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Unity.Networking.Transport.EditorTests")]

// We are making certain things visible for certain projects that require
// access to Network Protocols thus not requiring the API be visible
[assembly: InternalsVisibleTo("Unity.InternalAPINetworkingBridge.001")]