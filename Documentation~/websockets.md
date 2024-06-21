# WebGL support

By default, the Unity Transport package (UTP) implements a protocol on top of simple UDP datagrams. However, since web browsers don't offer direct access to UDP sockets, UTP also supports using the WebSocket protocol instead of UDP to enable compatibility with WebGL.

**Note**: While UTP supports the WebSocket protocol, it is _not_ intended as a general-purpose WebSocket library (such as [websocket-sharp](https://github.com/sta/websocket-sharp)). It can't be used to connect to any random WebSocket server. It can only be used to connect clients and servers that are both using UTP.

## Configuring WebSockets

So far one thing that was not apparent in our [client-server example](client-server-simple.md) is the fact that the `NetworkDriver` instantiates a specific `NetworkInterface` object internally for us, whereas there might be certain times when we need to explicitly request a particular one.

The `NetworkInterface` is what defines the set of operations that a driver requires to establish and coordinate connections. By default, in most platforms the network interface object used is an instance of the `UDPNetworkInterface` which, as implied by the name, encapsulates a UDP socket. However, we cannot normally open a UDP socket in a Web browser so, in the WebGL player, the default network interface is the `WebSocketNetworkInterface` instead, which, again the name implies, encapsulates a TCP socket using the WebSocket protocol.

This is an important distinction because of the very basic network constraint that a client can only directly connect to a server with the same underlying socket type. In other words, a TCP socket can only connect to another TCP socket, and the same applies to UDP. Therefore, if you are planning to create a server for WebGL players to connect to, you have to tell the network driver to explicitly use the `WebSocketNetworInterface`. The difference from the previous example would be one line:

```csharp
    m_Driver = NetworkDriver.Create(new WebSocketNetworkInterface());
```

Now if you plan to share networking code between clients for multiple platforms including WebGL you might opt to have a WebSocket server for all platforms in which case you should make sure to assign the `WebSocketNetworkInterface` in the non-WebGL clients too. Alternatively, if you plan to have a dedicated server for browsers and another for other platforms can specify a different driver instantiation with [compiler definitions](https://docs.unity3d.com/Manual/PlatformDependentCompilation.html), depending on what is the selected platform of the project, for example:

```csharp
#if UNITY_WEBGL
    m_Driver = NetworkDriver.Create(new WebSocketNetworkInterface());
#else
    m_Driver = NetworkDriver.Create(new UDPNetworkInterface());
#endif
```

You could also leverage the package's [cross-play support](cross-play.md) to create a server that listens for both UDP and WebSocket connections, thus using the most appropriate protocol for each platform.

### Connecting to a particular path

There are situations where you could want clients connecting to a particular URL *path*, or server listening on a particular path. For example if you want to host multiple WebSocket-based games on the same server using virtual hosts.

This can be configured at the `NetworkDriver` level like:

```csharp
var settings = new NetworkSettings();
settings.WithWebSocketParameters(path: "/some/path");
var driver = NetworkDriver.Create(new WebSocketNetworkInterface(), settings);
```

For clients, the above code will make all connection requests go to the given path. For servers, it will only allow connections made to the given path. The default value is "/", the root path.

## Servers and web browsers

It is usually not possible to start a server in a WebGL player even with the `WebSocketNetworkInterface` because the player is still constrained by the browser capabilities. Web browsers to date do not permit applications to open sockets for incoming connections and trying to do so will most likely throw an exception. On the other hand, it is still perfectly valid to do so while playing in the editor, thus in some cases you might want to take advantage of the `UNITY_EDITOR` compiler definition and have something similar to:

```csharp
#if UNITY_WEBGL && !UNITY_EDITOR
    m_Driver = NetworkDriver.Create(new WebSocketNetworkInterface());
#else
    m_Driver = NetworkDriver.Create(new UDPNetworkInterface());
#endif
```

However, if using Unity Relay, a web browser can still act as a host. This is because in such a configuration, from a networking perspective the browser acts as a client to the Relay server (while still acting as a server from a game session perspective).
