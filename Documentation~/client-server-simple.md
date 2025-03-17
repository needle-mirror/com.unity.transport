# Simple client and server

This example covers all aspects of the Unity Transport package and helps you create a sample project that highlights how to use the API to:

* Configure the transport
* Establish a connection
* Send data
* Receive data
* Close a connection

The goal is to make a remote "add" function. The flow will be: a client connects to the server, and sends a number. This number is then received by the server that adds another number to it and sends it back to the client. The client, upon receiving the number, closes the connection.

The code for this example can be found in the `SimpleClientServer` [package sample](samples-usage.md).

## Creating a server

A server is an endpoint that listens for incoming connection requests and sends and receives messages.

Start by creating a C# script in the Unity Editor named `ServerBehaviour.cs`.

```csharp
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class ServerBehaviour : MonoBehaviour {

    // Use this for initialization
    void Start () {

    }

    // Update is called once per frame
    void Update () {

    }
}
```

### Boilerplate code

As the package only provides a low level API, there is a bit of boilerplate code you might want to setup. This is an architecture design Unity chose to make sure that you always have full control.

Replace the contents of the file with the following:

```csharp
using UnityEngine;
using Unity.Collections;
using Unity.Networking.Transport;

public class ServerBehaviour : MonoBehaviour {

    NetworkDriver m_Driver;
    NativeList<NetworkConnection> m_Connections;

    void Start()
    {
    }

    void OnDestroy()
    {
    }

    void Update ()
    {
    }
}
```

This code declares a `NetworkDriver`, which is the primary API with which to interact with the transport. It also declares a `NativeList` to hold connections that will be made to the server.

### `Start` method

```csharp
void Start()
{
    m_Driver = NetworkDriver.Create();
    m_Connections = new NativeList<NetworkConnection>(16, Allocator.Persistent);

    var endpoint = NetworkEndpoint.AnyIpv4.WithPort(7777);
    if (m_Driver.Bind(endpoint) != 0)
    {
        Debug.LogError("Failed to bind to port 7777.");
        return;
    }
    m_Driver.Listen();
}
```

#### Code walkthrough

The first line of code, `m_Driver = NetworkDriver.Create();`, simply creates the driver without any parameters. The next one creates the `NativeList` that will hold the connection handles.

```csharp
    var endpoint = NetworkEndpoint.AnyIpv4.WithPort(7777);
    if (m_Driver.Bind(endpoint) != 0)
    {
        Debug.LogError("Failed to bind to port 7777.");
        return;
    }
    m_Driver.Listen();
```

Then we try to bind our driver to a specific network address and port, and if that does not fail, we call the `Listen` method. The address we bind to is the `AnyIpv4` address, which basically means to listen on all IP addresses on the computer.

**Note**: The call to the `Listen` method puts the `NetworkDriver` in the `Listening` state. This means that the `NetworkDriver` will now actively listen for incoming connections.

### `OnDestroy` method

Both `NetworkDriver` and `NativeList` allocate unmanaged memory and need to be disposed of to avoid memory leaks. To make sure this happens we can simply call the `Dispose` method when we are done with both of them.

Add the following code to the `OnDestroy` method of your [MonoBehaviour](https://docs.unity3d.com/ScriptReference/MonoBehaviour.html):

```csharp
void OnDestroy()
{
    if (m_Driver.IsCreated)
    {
        m_Driver.Dispose();
        m_Connections.Dispose();
    }
}
```

The check for `m_Driver.IsCreated` ensures we don't dispose of the memory if it hasn't been allocated (e.g. if the component is disabled).

### `Update` loop

As the Unity Transport package uses the [Unity C# Job System](https://docs.unity3d.com/Manual/JobSystem.html) internally, the `m_Driver` has a `ScheduleUpdate` method call. Inside the `Update` loop you need to make sure to call the `Complete` method on the [JobHandle](https://docs.unity3d.com/Manual/JobSystemJobDependencies.html) that is returned, in order to know when you are ready to process any updates.

```csharp
void Update()
{
    m_Driver.ScheduleUpdate().Complete();
```

**Note**: In this example, we are forcing a synchronization on the main thread in order to update and handle our data later in the `Update` call. The example with [jobified client and server](client-server-jobs.md) shows you how to use the API to take advantage of the job system.

The first thing we want to do, after you have updated your `m_Driver`, is to handle your connections. Start by cleaning up any old stale connections from the list before processing any new ones. This cleanup ensures that, when we iterate through the list to check what new events we have gotten, we dont have any old connections laying around.

Inside the "clean up connections" block below, we iterate through our connection list and just simply remove any stale connections.

```csharp
// Clean up connections.
for (int i = 0; i < m_Connections.Length; i++)
{
    if (!m_Connections[i].IsCreated)
    {
        m_Connections.RemoveAtSwapBack(i);
        i--;
    }
}
```

Under "accept new connections" below, we add a connection while there are new connections to accept.

```csharp
// Accept new connections.
NetworkConnection c;
while ((c = m_Driver.Accept()) != default)
{
    m_Connections.Add(c);
    Debug.Log("Accepted a connection.");
}
```

Now for each connection we want to call `PopEventForConnection` while there are more events still needing to get processed. The `DataStreamReader` returned by the method will be used to read any `Data` messages.

```csharp
for (int i = 0; i < m_Connections.Length; i++)
{
    DataStreamReader stream;
    NetworkEvent.Type cmd;
    while ((cmd = m_Driver.PopEventForConnection(m_Connections[i], out stream)) != NetworkEvent.Type.Empty)
    {
```

**Note**: There is also a `PopEvent` method that returns the first event for _any_ connection. The connection is then returned as an `out` parameter.

We are now ready to process events. Lets start with the `Data` event.

```csharp
if (cmd == NetworkEvent.Type.Data)
{
```

Next, we try to read a `uint` from the stream and output what we have received:

```csharp
    uint number = stream.ReadUInt();
    Debug.Log($"Got {number} from a client, adding 2 to it.");
```

When this is done we simply add 2 to the number we received and send it back. To send anything with the `NetworkDriver` we need an instance of a `DataStreamWriter`. A `DataStreamWriter` is a new type that comes with the `com.unity.collections` package. You get a `DataStreamWriter` when you start sending a message by calling `BeginSend`.

After you have written your updated number to your stream, you call the `EndSend` method on the driver and the message will be scheduled for sending:

```csharp
    number += 2;

    m_Driver.BeginSend(NetworkPipeline.Null, m_Connections[i], out var writer);
    writer.WriteUInt(number);
    m_Driver.EndSend(writer);
}
```

**Note**: We are passing `NetworkPipeline.Null` to the `BeginSend` function. This way we say to the driver to use the unreliable pipeline to send our data. It is also possible to not specify a pipeline. Refer to the [pipelines section of the documentation](pipelines-usage.md) for details.

Finally, you need to handle the disconnection case. This is pretty straight forward, if you receive a disconnect message you need to reset that connection's handle to its default value. As you might remember, the next time the `Update` loop runs it will clean up the connection list.

```csharp
else if (cmd == NetworkEvent.Type.Disconnect)
{
    Debug.Log("Client disconnected from the server.");
    m_Connections[i] = default;
    break;
}
```

That is the whole server.

## Creating a client

The client code looks pretty similar to the server code at first glance, but there are a few subtle differences. This part of the example covers the differences between them, and not so much the similarities.

### ClientBehaviour.cs

You still define a `NetworkDriver` but instead of having a list of connections we now only have one.

```csharp
using UnityEngine;
using Unity.Networking.Transport;

public class ClientBehaviour : MonoBehaviour
{
    NetworkDriver m_Driver;
    NetworkConnection m_Connection;

    void Start()
    {
    }

    void OnDestroy()
    {
    }

    void Update()
    {
    }
}
```

### Connecting a client

Start by creating a driver for the client and connecting it to the server's address:

```csharp
void Start()
{
    m_Driver = NetworkDriver.Create();

    var endpoint = NetworkEndpoint.LoopbackIpv4.WithPort(7777);
    m_Connection = m_Driver.Connect(endpoint);
}
```

Cleaning up this time is a bit easier because you don’t need a `NativeList` to hold connection handles, so it simply just becomes:

```csharp
void OnDestroy()
{
    m_Driver.Dispose();
}
```

### Client `Update` loop

You start the same way as you did in the server by calling `m_Driver.ScheduleUpdate().Complete();` and make sure that there is a connection to process.

```csharp
void Update()
{
    m_Driver.ScheduleUpdate().Complete();

    if (!m_Connection.IsCreated)
    {
        return;
    }
```

You should recognize the code below, but if you look closely you can see that the call to `m_Driver.PopEventForConnection` was switched out with a call to `m_Connection.PopEvent`. This is technically the same method, it just makes it a bit clearer that you are handling a single connection.

```csharp
Unity.Collections.DataStreamReader stream;
NetworkEvent.Type cmd;
while ((cmd = m_Connection.PopEvent(m_Driver, out stream)) != NetworkEvent.Type.Empty)
{
```

Now you encounter a new event you have not seen yet: `NetworkEvent.Type.Connect`. This event tells you that the connection we started establishing with the `Connect` call has succeeded and you are now connected to the remote peer.

```csharp
if (cmd == NetworkEvent.Type.Connect)
{
    Debug.Log("We are now connected to the server.");

    uint value = 1;
    m_Driver.BeginSend(m_Connection, out var writer);
    writer.WriteUInt(value);
    m_Driver.EndSend(writer);
}
```

When you establish a connection between the client and the server, you send a number (that you want the server to increment by two). We again make use of the `BeginSend`/`EndSend` pattern together with the `DataStreamWriter`. Here we're sending the value 1 to the server.

When the `NetworkEvent` type is `Data`, as below, you read the value back that you received from the server and then call the `Disconnect` method.

**Note**: A good pattern is to always set your `NetworkConnection` to `default` to avoid stale references.

```csharp
else if (cmd == NetworkEvent.Type.Data)
{
    uint value = stream.ReadUInt();
    Debug.Log($"Got the value {value} back from the server.");

    m_Connection.Disconnect(m_Driver);
    m_Connection = default;
}
```

Lastly, we need to handle potential server disconnects:

```csharp
else if (cmd == NetworkEvent.Type.Disconnect)
{
    Debug.Log("Client got disconnected from server.");
    m_Connection = default;
}
```

**Note**: If you were to close the connection before popping the `Disconnect` event (say you're closing it in response to a `Data` event), make sure to pop all remaining events for that connection anyway. Otherwise an error will be printed on the next update about resetting the event queue while there were pending events.

## Putting it all together

To take this for a test run, you can add a new empty [GameObject](https://docs.unity3d.com/ScriptReference/GameObject.html) to our scene:

![GameObject Added](images/game-object.png)

Add add both of our behaviours to it:

![Inspector](images/inspector.png)

Click **Play**. Five log messages should load in your **Console** window:

![Console](images/console-view.png)
