# Sample projects

The Unity Transport package comes with a `Samples` folder containing simple assembly definitions and associated scenes that serve to illustrate basic aspects of the library. 

## Ping

Implements a simple ping/pong service. Correlating client and server behaviours are supplied for two scenarios. In the first scenario with `PingMainThreadServerBehaviour` and `PingMainThreadClientBehaviour`, the peers are implemented to process messages in the main thread only. In the second scenario covered by `PingServerBehaviour` and `PingClientBehaviour` the code shows how to take advantage of the Burst compiler and the job system.

## Pipeline

This sample shows how to define pipeline stages as described in [Pipelines](pipelines-usage.md). The code demonstrates a pipeline definition for unreliable sequenced delivery based on a default `UDPNetworkInterface`.

**Note**: Consider using pipeline stages carefully as depending on the underlying `NetworkInterface` used certain pipeline configurations cannot add value to the quality of service and might in fact be detrimental. For example, it makes sense to have a pipeline stage for unreliable sequenced delivery over a `UDPNetworkInterface` but over a `WebSocketNetworkInterface` it will only incur overhead try to offer something that the underlying network interface already provided.

## RelayPing

This is re-write of the [Ping](#ping) sample mentioned above that uses the [Unity Relay Service](https://unity.com/products/relay) to connect players. The code demonstrates how to manipulate and pass custom [Network Settings](network-settings.md) to a network driver.

## Soaker

This sample produces a dense reliable stream of messages and can generate a report with some statistics about the 
connection. The code also illustrates more advanced techniques to manage concurrent jobs. 
