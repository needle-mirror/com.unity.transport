# Change log

## [0.2.1-preview.1] - 2019-11-28
### New features
### Changes
### Fixes
* Added missing bindings for Linux and Android
### Upgrade guide

## [0.2.0-preview.4] - 2019-11-26
### New features
### Changes
* Added support for unquantized floats to `DataStream` class.
* Added `NetworkConfigParameter.maxFrameTimeMS` so you to allow longer frame times when debugging to prevent disconnections due to timeout.
* Allow "1.1.1.1:1234" strings when parsing the IP string in the NetworkEndPoint class, it will use the port part when it's present.
* Reliable pipeline now doesn't require parameters passed in (uses default window size of 32)
* Added Read/Write of ulong to `DataStream`.
* Made it possible to get connection state from the parallel NetworkDriver.
* Added `LengthInBits` to the `DataStreamWriter`.

### Fixes
* Do not push data events to disconnected connections. Fixes an error about resetting the queue with pending messages
* Made the endian checks in `DataStream` compatible with latest version of burst.
### Upgrade guide

## [0.1.2-preview.1] - 2019-07-17
### New features
* Added a new *Ping-Multiplay* sample based on the *Ping* sample
    * Created to be the main sample for demonstrating Multiplay compatibility and best practices (SQP usage, IP binding, etc.)
    * Contains both client and server code.  Additional details in readme in `/Assets/Samples/Ping-Multiplay/`.
* **DedicatedServerConfig**: added arguments for `-fps` and `-timeout`
* **NetworkEndPoint**: Added a `TryParse()` method which returns false if parsing fails
    * Note: The `Parse()` method returns a default IP / Endpoint if parsing fails, but a method that could report failure was needed for the Multiplay sample
* **CommandLine**:
    * Added a `HasArgument()` method which returns true if an argument is present
    * Added a `PrintArgsToLog()` method which is a simple way to print launch args to logs
    * Added a `TryUpdateVariableWithArgValue()` method which updates a ref var only if an arg was found and successfully parsed

### Changes
* Deleted existing SQP code and added reference to SQP Package (now in staging)
* Removed SQP server usage from basic *Ping* sample
    * Note: The SQP server was only needed for Multiplay compatibility, so the addition of *Ping-Multiplay* allowed us to remove SQP from *Ping*

### Fixes
* **DedicatedServerConfig**: Vsync is now disabled programmatically if requesting an FPS different from the current screen refresh rate

### Upgrade guide

## [0.1.1-preview.1] - 2019-06-05
### New features
* Moved MatchMaking to a package and supporting code to a separate folder.

### Fixes
* Fixed an issue with the reliable pipeline not resending when completely idle.

### Upgrade guide

## [0.1.0-preview.1] - 2019-04-16
### New features
* Added network pipelines to enable processing of outgoing and incomming packets. The available pipeline stages are `ReliableSequencedPipelineStage` for reliable UDP messages and `SimulatorPipelineStage` for emulating network conditions such as high latency and packet loss. See [the pipeline documentation](com.unity.transport/Documentation~/pipelines-usage.md) for more information.
* Added reading and writing of packed signed and unsigned integers to `DataStream`. These new methods use huffman encoding to reduce the size of transfered data for small numbers.

### Changes
* Enable Burst compilation for most jobs
* Made it possible to get the remote endpoint for a connection
* Replacing EndPoint parsing with custom code to avoid having a dependency on System.Net
* Change the ping sample command-line parameters for server to -port and -query_port
* For matchmaking - use an Assignment object containing the ConnectionString, the Roster, and an AssignmentError string instead of just the ConnectionString.

### Fixes
* Fixed an issue with building iOS on Windows
* Fixed inconsistent error handling between platforms when the network buffer is full

### Upgrade guide
Unity 2019.1 is now required.

`BasicNetworkDriver` has been renamed to `GenericNetworkDriver` and a new `UdpNetworkDriver` helper class is also available.

System.Net EndPoints can no longer be used as addresses, use the new NetworkEndpoint struct instead.
