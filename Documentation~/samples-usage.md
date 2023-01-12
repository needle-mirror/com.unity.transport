# Using sample projects

The Unity Transport package comes with a `Samples` folder containing simple assembly definitions and associated scenes that serve to illustrate basic aspects of the library.

These samples can be imported through the package manager window, when selecting the Unity Transport package. The pane on the right with the package's details contains a section with all the samples.

The following samples are provided with the package:

## SimpleClientServer

Contains the code for the [documentation section](client-server-simple.md) about creating a simple client and server.

## JobifiedClientServer

Contains the code for the [documentation section](client-server-jobs.md) about creating a jobified client and server.

## Ping

Implements a simple ping/pong service. The code shows how to take advantage of the Burst compiler and the job system.

## RelayPing

This is re-write of the 'Ping' sample mentioned above that uses the [Unity Relay service](https://unity.com/products/relay) to connect players.
