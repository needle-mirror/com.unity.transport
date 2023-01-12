# Jobified client and server

Following from the [the minimal client and server example](client-server-simple.md), this tutorial will walk you through creating a client and server that use jobs. Using jobs allows you to take advantage of parallel code execution.

Before reading this tutorial, you should understand how the [C# Job System](https://docs.unity3d.com/Manual/JobSystem.html) works. Review that information, then continue.

The code for that example is available in the `JobifiedClientServer` [package sample](samples-usage.md).

## Create a jobified client

Create a client job to handle your inputs from the network. As you only handle one client at a time, use [IJob](https://docs.unity3d.com/ScriptReference/Unity.Jobs.IJob.html) as your job type. You need to pass the driver and the connection to the job to handle updates within the `Execute` method of the job.

```csharp
struct ClientUpdateJob : IJob
{
    public NetworkDriver Driver;
    public NativeArray<NetworkConnection> Connection;

    public void Execute()
    {
    }
}
```

**Note:** The data inside the `ClientUpdateJob` is *copied*. If you want to use the data after the job is completed, you need to have your data in a shared container, such as a [native container](https://docs.unity3d.com/Manual/JobSystemNativeContainer.html).

You may want to update the `NetworkConnection` inside your job as you may receive a disconnect message. To ensure that this is possible, we use a [NativeArray](https://docs.unity3d.com/ScriptReference/Unity.Collections.NativeArray_1.html).

**Note:** You can only use [blittable types](https://docs.microsoft.com/en-us/dotnet/framework/interop/blittable-and-non-blittable-types) in a native container.

In your `Execute` method, move over your code from the `Update` method that you have already in place from `ClientBehaviour.cs` and you are done.

You need to change any call to `m_Connection` to `Connection[0]` to refer to the first element inside your `NativeArray`. See the following:

```csharp
public void Execute()
{
    if (!Connection[0].IsCreated)
    {
        return;
    }

    DataStreamReader stream;
    NetworkEvent.Type cmd;
    while ((cmd = Connection[0].PopEvent(Driver, out stream)) != NetworkEvent.Type.Empty)
    {
        if (cmd == NetworkEvent.Type.Connect)
        {
            Debug.Log("We are now connected to the server.");

            uint value = 1;
            Driver.BeginSend(Connection[0], out var writer);
            writer.WriteUInt(value);
            Driver.EndSend(writer);
        }
        else if (cmd == NetworkEvent.Type.Data)
        {
            uint value = stream.ReadUInt();
            Debug.Log($"Got the value {value} back from the server.");

            Driver.Disconnect(Connection[0]);
            Connection[0] = default;
        }
        else if (cmd == NetworkEvent.Type.Disconnect)
        {
            Debug.Log("Client got disconnected from the server.");
            Connection[0] = default;
        }
    }
}
}
```

### Update the client `MonoBehaviour`

Make the following changes to `ClientBehaviour`:

* Change `m_Connection` to type `NativeArray`
* Add a [JobHandle](https://docs.unity3d.com/Manual/JobSystemJobDependencies.html) to track ongoing jobs

```csharp
public class JobifiedClientBehaviour : MonoBehaviour
{
    NetworkDriver m_Driver;
    NativeArray<NetworkConnection> m_Connection;

    JobHandle m_ClientJobHandle;
```

#### `Start` method

```csharp
void Start()
{
    m_Driver = NetworkDriver.Create();
    m_Connection = new NativeArray<NetworkConnection>(1, Allocator.Persistent);

    var endpoint = NetworkEndpoint.LoopbackIpv4.WithPort(7777);
    m_Connection[0] = m_Driver.Connect(endpoint);
}
```

The `Start` method looks pretty similar to before. The major update here is to verify you create your `NativeArray`.

#### `OnDestroy` method

```csharp
void OnDestroy()
{
    m_ClientJobHandle.Complete();
    m_Driver.Dispose();
    m_Connection.Dispose();
}
```

For the `OnDestroy` method, we need to also dispose of the `NativeArray` object. We also need to add a `Complete` call on the job handle. This ensures your jobs complete before cleaning up and destroying the data they might be using.

#### `Update` loop

Finally update your core game loop:

```csharp
void Update()
{
    m_ClientJobHandle.Complete();
    ...
```

Before you start running your new frame, check that the last frame's job has completed. Instead of calling `m_Driver.ScheduleUpdate().Complete()`, we use the `JobHandle` and call `m_ClientJobHandle.Complete()`.

To chain your job, start by creating a job instance:

```csharp
var job = new ClientUpdateJob
{
    Driver = m_Driver,
    Connection = m_Connection,
};
```

To schedule the job, pass the `JobHandle` dependency that was returned from the `m_Driver.ScheduleUpdate` call in the `Schedule` function of your `IJob`. Start by invoking the `m_Driver.ScheduleUpdate` without a call to `Complete`, and pass the returned `JobHandle` to your saved `m_ClientJobHandle`. Then, pass the returned `JobHandle` to your own job, returning a newly updated handle.

```csharp
m_ClientJobHandle = m_Driver.ScheduleUpdate();
m_ClientJobHandle = job.Schedule(m_ClientJobHandle);
```

## Create a jobified server

The process of creating the jobified server is similar to creating the jobified client. You create the jobs you need and then you update the usage code.

Consider this: you know that the `NetworkDriver` has a `ScheduleUpdate` method that returns a `JobHandle`. The job as you saw above populates the internal buffers of the `NetworkDriver` and lets us call `PopEvent`/`PopEventForConnection` method. What if you create a job that will fan out and run the processing code for all connected clients in parallel? If you look at the documentation for the C# Job System, you can see that there is a [IJobParallelFor](https://docs.unity3d.com/Manual/JobSystemParallelForJobs.html) job type that can handle this scenario.

Because you do not know how many requests you may receive or how many connections you may need to process at any one time, there is another `IJobPrarallelFor` job type that you can use namely: `IJobParallelForDefer`.

```csharp
struct ServerUpdateJob : IJobParallelForDefer
{
    public void Execute(int i)
    {
    }
}
```

However, you cannot run all of your code in parallel.

In the original server example above, you begin by cleaning up closed connections and accepting new ones, which cannot be done in parallel. You need to create a connection job.

Start by creating a `ServerUpdateConnectionJob` job. Pass both the driver and connection list to the connection job. Then you want your job to clean up connections and accept new connections as it did before:

```csharp
struct ServerUpdateConnectionsJob : IJob
{
    public NetworkDriver Driver;
    public NativeList<NetworkConnection> Connections;

    public void Execute()
    {
        // Clean up connections.
        for (int i = 0; i < Connections.Length; i++)
        {
            if (!Connections[i].IsCreated)
            {
                Connections.RemoveAtSwapBack(i);
                i--;
            }
        }

        // Accept new connections.
        NetworkConnection c;
        while ((c = Driver.Accept()) != default)
        {
            Connections.Add(c);
            Debug.Log("Accepted a connection.");
        }
    }
}
```

The code above should be almost identical to your old non-jobified code.

With the `ServerUpdateConnectionsJob` done, implement the `ServerUpdateJob` using `IJobParallelFor`:

```csharp
struct ServerUpdateJob : IJobParallelForDefer
{
    public NetworkDriver.Concurrent Driver;
    public NativeArray<NetworkConnection> Connections;

    public void Execute(int i)
    {
    }
}
```

There are two major differences compared with the other job:

- You are using the `NetworkDriver.Concurrent` type, this allows you to call the `NetworkDriver` from multiple threads, precisely what you need for the `IParallelForJobDefer`. 
- You are now passing a `NativeArray` of type `NetworkConnection` instead of a `NativeList`. The `IParallelForJobDefer` does not accept any other collection type than a `NativeArray` (more on this later).

### `Execute` method

The only difference between the old code and the jobified example is that you remove the top level `for` loop that you had in your code. This is removed because the `Execute` function on this job will be called for each connection, and the `i` parameter is an index to an available connection in the array. 

```csharp
public void Execute(int i)
{
    DataStreamReader stream;
    NetworkEvent.Type cmd;
    while ((cmd = Driver.PopEventForConnection(Connections[i], out stream)) != NetworkEvent.Type.Empty)
    {
        if (cmd == NetworkEvent.Type.Data)
        {
            uint number = stream.ReadUInt();

            Debug.Log($"Got {number} from a client, adding 2 to it.");
            number += 2;

            Driver.BeginSend(Connections[i], out var writer);
            writer.WriteUInt(number);
            Driver.EndSend(writer);
        }
        else if (cmd == NetworkEvent.Type.Disconnect)
        {
            Debug.Log("Client disconnected from the server.");
            Connections[i] = default;
        }
    }
}
```

You can see this index in use in the top level `while` loop:

```csharp
while ((cmd = Driver.PopEventForConnection(Connections[i], out stream)) != NetworkEvent.Type.Empty)
```

**Note:** You are using the index that was passed into your `Execute` method to iterate over all the connections in parallel.

You now have two jobs:

- The first job updates the connections (clean up old ones and accept new ones).
- The second job is to parse `NetworkEvent` on each connected client.

### Update the server `MonoBehaviour`

Access your [MonoBehaviour](https://docs.unity3d.com/ScriptReference/MonoBehaviour.html) and start updating the server.

```csharp
public class JobifiedServerBehaviour : MonoBehaviour
{
    NetworkDriver m_Driver;
    NativeList<NetworkConnection> m_Connections;

    JobHandle m_ServerJobHandle;

    void Start()
    {
        ...
    }

    void OnDestroy()
    {
        ...
    }

    void Update()
    {
        ...
    }
}
```

The only change made is adding a `JobHandle` in the variable declarations to keep track of your ongoing jobs.

#### `Start` method

You do not need to change your `Start` method as it should look the same:

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

#### `OnDestroy` method

You need to remember to call `m_ServerJobHandle.Complete` in your `OnDestroy` method so you can properly clean up code:

```csharp
void OnDestroy()
{
    if (m_Driver.IsCreated)
    {
        m_ServerJobHandle.Complete();
        m_Driver.Dispose();
        m_Connections.Dispose();
    }
}
```

#### Server `Update` loop

In your `Update` method, call `Complete` on the `JobHandle`. This forces the jobs to complete when you start a new frame:

```csharp
void Update()
{
    m_ServerJobHandle.Complete();

    var connectionJob = new ServerUpdateConnectionsJob
    {
        Driver = m_Driver,
        Connections = m_Connections
    };

    var serverUpdateJob = new ServerUpdateJob
    {
        Driver = m_Driver.ToConcurrent(),
        Connections = m_Connections.AsDeferredJobArray()
    };

    m_ServerJobHandle = m_Driver.ScheduleUpdate();
    m_ServerJobHandle = connectionJob.Schedule(m_ServerJobHandle);
    m_ServerJobHandle = serverUpdateJob.Schedule(m_Connections, 1, m_ServerJobHandle);
}
```

To chain the jobs, you want to follow this sequence: `NetworkDriver` update, then `ServerUpdateConnectionsJob`, and finally `ServerUpdateJob`.

Start by populating your `ServerUpdateConnectionsJob`:

```csharp
var connectionJob = new ServerUpdateConnectionsJob
{
    Driver = m_Driver,
    Connections = m_Connections
};
```

Then create your `ServerUpdateJob`. Remember to use the `ToConcurrent` call on your driver, to ensure you are using a concurrent driver for the `IParallelForJobDefer`:

```csharp
var serverUpdateJob = new ServerUpdateJob
{
    Driver = m_Driver.ToConcurrent(),
    Connections = m_Connections.AsDeferredJobArray()
};
```

The final step is to ensure the `NativeArray` is populated to the correct size. This can be done using a `DeferredJobArray`. When executed, it verifies the connections array is populated with the correct number of items that you have in your list. When runnning `ServerUpdateConnectionsJob` first, this may change the *size* of the list.

Create your job chain and call `Scheduele` as follows:

```csharp
m_ServerJobHandle = m_Driver.ScheduleUpdate();
m_ServerJobHandle = connectionJob.Schedule(m_ServerJobHandle);
m_ServerJobHandle = serverUpdateJob.Schedule(m_Connections, 1, m_ServerJobHandle);
```

## Using Burst for extra performance

Most of the jobs in these examples are implement using code that adheres to [the subset of C# supported by Burst](https://docs.unity3d.com/Packages/com.unity.burst@1.7/manual/docs/CSharpLanguageSupport_Types.html). Burst is a compiler that is designed to pre-compile Unity jobs into highly-performant native code. Unity Transport has been written to take advantage of Burst, and most of its data structures (like `NetworkDriver`) are Burst-friendly.

A job can be Burst-compiled simply by adding the `[BurstCompile]` attribute to its definition. For example:

```csharp
[BurstCompile]
struct ClientUpdateJob : IJob
{
    ...
}
```

Refer to the [Burst documentation](https://docs.unity3d.com/Packages/com.unity.burst@latest) for more details on how to use it.
