# About Unity Transport

The Unity Transport package (`com.unity.transport`) is a low-level networking library geared towards multiplayer games development.

It is used as the backbone of both Unity Netcode solutions: [Netcode for GameObjects](https://docs-multiplayer.unity3d.com/netcode/current/about) and [Netcode for Entities](https://docs.unity3d.com/Packages/com.unity.netcode@latest)
but it can also be used with a custom solution.

![Unity Transport Block Diagram](images/block-diagram.png)

All the platforms supported by the Unity Engine are seamlessly supported by Unity Transport
thanks to a connection-based abstraction layer (*built-in network driver*) it provides over either UDP sockets or WebSockets. Both can be setup with or without encryption, embodied by the closed and open padlocks in the above block diagram.

Cherry on top, *pipelines* offer additional optional functionalities like reliability, packet ordering, and packet fragmentation.

## Using Unity Transport

* If you are a first user of the package, the first sections of this manual will walk you through [the installation](install.md) of the package and [the creation of your first client and server](client-server-simple.md).
* If you are upgrading from a previous version of the package, you may want to refer to our [1.X migration guide](migration.md).

## Requirements

* Unity Editor 2022.2 and later.
* For runtime builds, this package supports all platforms supported by the Unity Engine (with WebGL only supporting WebSocket connections in client mode).

## Note
This package should not be confused with the `NetworkTransport` abstraction in Netcode for GameObjects. Please see the [transports section of its documentation](https://docs-multiplayer.unity3d.com/netcode/current/advanced-topics/transports) for more information.
