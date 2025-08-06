# WebGL support

By default, the Unity Transport package (UTP) implements a protocol on top of simple UDP datagrams. However, since web browsers don't offer direct access to UDP sockets, UTP also supports using the WebSocket protocol instead of UDP to enable compatibility with WebGL.

**Note**: While UTP supports the WebSocket protocol, it is _not_ intended as a general-purpose WebSocket library (such as [websocket-sharp](https://github.com/sta/websocket-sharp)). It can't be used to connect to any random WebSocket server. It can only be used to connect clients and servers that are both using UTP.

## Configuring WebSockets

So far one thing that was not apparent in our [client-server example](client-server-simple.md) is the fact that the `NetworkDriver` instantiates a specific `NetworkInterface` object internally for us, whereas there might be certain times when we need to explicitly request a particular one.

The _network interface_ is what defines the set of operations that a driver requires to establish and coordinate connections. By default, in most platforms the network interface object used is an instance of `UDPNetworkInterface` which, as implied by the name, encapsulates a UDP socket. However, we cannot normally open a UDP socket in a web browser so, in the WebGL player, the default network interface is the `WebSocketNetworkInterface` instead, which, again as the name implies, encapsulates a TCP socket using the WebSocket protocol.

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

This can be configured at the `NetworkDriver` level like this:

```csharp
var settings = new NetworkSettings();
settings.WithWebSocketParameters(path: "/some/path");
var driver = NetworkDriver.Create(new WebSocketNetworkInterface(), settings);
```

For clients, the above code will make all connection requests go to the given path. For servers, it will only allow connections made to the given path. The default value is "/", the root path.

### WebSockets and encryption (TLS)

UTP supports connections over secure WebSockets (often abbreviated WSS). If your WebGL game is to be distributed over HTTPS, then you _must_ use WSS to connect to your game server. This is because web browsers act on the principle that if the initial connection is secure, then all connections emanating from the same page must also be. That is, if your game was accessed on a page served over HTTPS, any WebSocket connection it makes must be WSS.

Configuring WSS is similar to how we configured DTLS in our [encrypted sample](client-server-secure.md), with a few critical differences.

On clients, you do not need to specify a CA certificate. Only the server name is required. This will cause UTP to defer to the OS or browser's trust store to verify any certificate's authenticity.

```csharp
var settings = new NetworkSettings();
settings.WithSecureClientParameters(serverName: "your-domain.com");
var driver = NetworkDriver.Create(new WebSocketNetworkInterface(), settings);
```

On servers, it is not possible to generate your own certificates as we did in the [encrypted sample](client-server-secure.md) because this produces self-signed certificates, which web browsers do not allow (at least not by default). You will need certificates produced by an official certificate authority. Your hosting provider may already provide such certificates. If hosting your server yourself, you can obtain one using providers like [Let's Encrypt](https://letsencrypt.org/).

Your official certificate and its key will likely be provided as files on the server that you will then need to read from your application:

```csharp
// Actual file location will depend on your specific setup.
var cert = System.IO.ReadAllText("/etc/letsencrypt/live/your-domain.com/fullchain.pem");
var key = System.IO.ReadAllText("/etc/letsencrypt/live/your-domain.com/privkey.pem");

var settings = new NetworkSettings();
settings.WithSecureServerParameters(certificate: cert, privateKey: key);
var driver = NetworkDriver.Create(new WebSocketNetworkInterface(), settings);
```

If you do not have access to a certificate on your game server (e.g. because your hosting service does not provide one), then you will need an intermediary between the client and server to handle the encryption. One such option is to use [Unity Relay](https://unity.com/products/relay) which readily supports WSS. Another is to run your own reverse proxy that performs [TLS termination](https://en.wikipedia.org/wiki/TLS_termination_proxy). Note that this latter solution requires some networking know-how and is only recommended for more advanced users.

## Servers and web browsers

It is usually not possible to start a _server_ in a WebGL player even with the `WebSocketNetworkInterface` because to date browsers do not allow creating listening sockets. Thus you will need another non-WebGL build (or the editor) to act as the server when running WebGL builds.

There are two exceptions to this however:

  * The `IPCNetworkInterface` allows servers in WebGL players because it is a purely in-memory communication channel. Obviously, that means the client and server must run in the same build. This can be useful to create a single-player version of a game while still leaving most of the networking code the same.
  * If using Unity Relay, a web browser can still act as a host. This is because in such a configuration, from a networking perspective the browser acts as a client to the Relay server (while still acting as a server from a game session perspective).

