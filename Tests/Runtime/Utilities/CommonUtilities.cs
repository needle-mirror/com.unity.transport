using NUnit.Framework;
using System;
using System.Diagnostics;
using System.Threading;
using Unity.Collections;
using Unity.Networking.Transport;
#if ENABLE_MANAGED_UNITYTLS
using Unity.Networking.Transport.TLS;
#endif

namespace Unity.Networking.Transport.Tests
{
    public static class CommonUtilites
    {
        // Used as ValueSource to create DTLS versions of a test.
        public enum SecureProtocolMode
        {
            SecureProtocolServerAuthOnly,
            SecureProtocolClientAndServerAuth,
            SecureProtocolDisabled
        }

        // Maximum time to wait for a network event. This is purposely very high because we don't
        // want spike lags (which are fairly frequent on CI machines) to cause tests to fail. Also
        // there are cases where DTLS connections take unusually long in the editor (see DST-585).
        public const long MaxEventWaitTimeMS = 2000;

        // Create a new server driver (possibly secure).
        public static NetworkDriver CreateServer(
            SecureProtocolMode secureMode,
            NetworkSettings settings = new NetworkSettings(),
            INetworkInterface netInterface = null)
        {
#if ENABLE_MANAGED_UNITYTLS
            if (secureMode == SecureProtocolMode.SecureProtocolServerAuthOnly)
            {
                settings.WithSecureServerParameters(
                    certificate: ref SecureTestParameters.Certificate1,
                    privateKey: ref SecureTestParameters.PrivateKey1,
                    handshakeTimeoutMin: SecureTestParameters.MinHandshakeTimeoutMS,
                    handshakeTimeoutMax: SecureTestParameters.MaxHandshakeTimeoutMS);
            }
            else if (secureMode == SecureProtocolMode.SecureProtocolClientAndServerAuth)
            {
                var clientName = new FixedString32Bytes("127.0.0.1");
                settings.WithSecureServerParameters(
                    certificate: ref SecureTestParameters.Certificate1,
                    privateKey: ref SecureTestParameters.PrivateKey1,
                    caCertificate: ref SecureTestParameters.CaCertificate,
                    clientName: ref clientName,
                    handshakeTimeoutMin: SecureTestParameters.MinHandshakeTimeoutMS,
                    handshakeTimeoutMax: SecureTestParameters.MaxHandshakeTimeoutMS);
            }
#endif
            var netif = netInterface == null ? new BaselibNetworkInterface() : netInterface;
            return new NetworkDriver(netif, settings);
        }

        // Create a new client driver (possibly secure).
        public static NetworkDriver CreateClient(
            SecureProtocolMode secureMode,
            NetworkSettings settings = new NetworkSettings(),
            INetworkInterface netInterface = null)
        {
#if ENABLE_MANAGED_UNITYTLS
            if (secureMode == SecureProtocolMode.SecureProtocolServerAuthOnly)
            {
                var serverName = new FixedString32Bytes("127.0.0.1");
                settings.WithSecureClientParameters(
                    caCertificate: ref SecureTestParameters.CaCertificate,
                    serverName: ref serverName,
                    handshakeTimeoutMin: SecureTestParameters.MinHandshakeTimeoutMS,
                    handshakeTimeoutMax: SecureTestParameters.MaxHandshakeTimeoutMS);
            }
            else if (secureMode == SecureProtocolMode.SecureProtocolClientAndServerAuth)
            {
                var serverName = new FixedString32Bytes("127.0.0.1");
                settings.WithSecureClientParameters(
                    certificate: ref SecureTestParameters.Certificate2,
                    privateKey: ref SecureTestParameters.PrivateKey2,
                    caCertificate: ref SecureTestParameters.CaCertificate,
                    serverName: ref serverName,
                    handshakeTimeoutMin: SecureTestParameters.MinHandshakeTimeoutMS,
                    handshakeTimeoutMax: SecureTestParameters.MaxHandshakeTimeoutMS);
            }
#endif
            var netif = netInterface == null ? new BaselibNetworkInterface() : netInterface;
            return new NetworkDriver(netif, settings);
        }

        // Setup (bind and listen) a server driver. Returns the endpoint that can be connected to.
        public static NetworkEndPoint SetupServer(NetworkEndPoint endpoint, NetworkDriver driver)
        {
            var nep = endpoint;
            nep.Port = 1337;

            // Bind the server. Retrying a few times makes tests more reliable (e.g. after a failed
            // test if there's still a driver hanging around that's already bound to 1337).
            for (int i = 0; i < 10; i++)
            {
                if (driver.Bind(nep) == 0)
                    break;

                nep.Port += 17;
            }
            Assert.IsTrue(driver.Bound, "Failed to bind server driver.");

            Assert.AreEqual(0, driver.Listen(), "Failed to listen on server driver.");

            return nep;
        }

        // Connect a server and a client driver. Takes care of setting up the server.
        public static void ConnectServerAndClient(
            NetworkEndPoint endpoint, NetworkDriver server, NetworkDriver client,
            out NetworkConnection s2cConnection, out NetworkConnection c2sConnection)
        {
            var nep = SetupServer(endpoint, server);

            c2sConnection = client.Connect(nep);

            s2cConnection = WaitForAcceptedConnection(server, client);

            // Have the server send the Connection Accept message.
            server.ScheduleUpdate().Complete();

            WaitForEvent(NetworkEvent.Type.Connect, client);
        }

        // Wait for a connection to be accepted by the server.
        public static NetworkConnection WaitForAcceptedConnection(NetworkDriver server, NetworkDriver client)
        {
            NetworkConnection connection = default;

            WaitForCondition(() =>
            {
                client.ScheduleUpdate().Complete();
                server.ScheduleUpdate().Complete();

                connection = server.Accept();
                return connection.IsCreated;
            }, "Timed out while waiting to accept a connection.");

            return connection;
        }

        // Wait for an event of the given type to happen on the given driver. Getting any other
        // type of event (except Empty of course) results in an assertion failure.
        public static void WaitForEvent(NetworkEvent.Type type, NetworkDriver driver, long timeout = MaxEventWaitTimeMS)
        {
            NetworkEvent.Type ev = NetworkEvent.Type.Empty;

            WaitForCondition(() =>
            {
                driver.ScheduleUpdate().Complete();

                ev = driver.PopEvent(out _, out _);
                return ev != NetworkEvent.Type.Empty;
            }, $"Timed out while waiting for network event {type}.", timeout);

            Assert.AreEqual(type, ev);
        }

        // Wait for a data event on the given driver and return its reader stream as an output
        // parameter. Getting any other type of event (except Empty of course) results in an
        // assertion failure.
        public static void WaitForDataEvent(NetworkDriver driver, out DataStreamReader stream)
        {
            DataStreamReader reader = default;

            WaitForCondition(() =>
            {
                driver.ScheduleUpdate().Complete();

                var ev = driver.PopEvent(out _, out reader);
                return ev == NetworkEvent.Type.Data;
            }, "Timed out while waiting for Data event.");

            stream = reader;
        }

        // Wait for a condition to be true. Timing out results in an assertion failure.
        public static void WaitForCondition(Func<bool> condition,
            string message = "Timed out while waiting for a condition.",
            long timeout = MaxEventWaitTimeMS)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds <= timeout)
            {
                if (condition()) return;
                Thread.Sleep(5);
            }

            Assert.Fail(message);
        }

        // Run the given function periodically for the given amount of time. Aside from failing an
        // assertion, there is no way to stop early before the given timeout is elapsed.
        public static void RunPeriodicallyFor(long timeout, Action function)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds <= timeout)
            {
                function();
                Thread.Sleep(5);
            }
        }

        public unsafe static void FillBuffer(byte* bufferPtr, int size)
        {
            for (int i = 0; i < size; i++)
            {
                bufferPtr[i] = (byte)(i % 256);
            }
        }

        public unsafe static bool CheckBuffer(byte* bufferPtr, int size)
        {
            for (int i = 0; i < size; i++)
            {
                if (bufferPtr[i] != (byte)(i % 256))
                    return false;
            }

            return true;
        }
    }
}
