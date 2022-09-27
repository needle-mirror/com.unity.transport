using NUnit.Framework;
using System.Collections;
using Unity.Collections;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Protocols;
using Unity.Networking.Transport.Utilities;
using UnityEngine;
using UnityEngine.TestTools;

using static Unity.Networking.Transport.Tests.CommonUtilites;

namespace Unity.Networking.Transport.Tests
{
    public class SendMessageTests
    {
        private static readonly NetworkEndPoint[] s_EndpointParameters =
        {
            NetworkEndPoint.LoopbackIpv4,
#if !(UNITY_SWITCH || UNITY_PS4 || UNITY_PS5)
            NetworkEndPoint.LoopbackIpv6
#endif
        };

        private static readonly SecureProtocolMode[] s_SecureModeParameters =
        {
#if ENABLE_MANAGED_UNITYTLS
            SecureProtocolMode.SecureProtocolServerAuthOnly,
#endif
            SecureProtocolMode.SecureProtocolDisabled
        };

        [Test]
        public void SendMessage_PingPong(
            [ValueSource("s_EndpointParameters")] NetworkEndPoint endpoint,
            [ValueSource("s_SecureModeParameters")] SecureProtocolMode secureMode)
        {
            using (var server = CreateServer(secureMode))
            using (var client = CreateClient(secureMode))
            {
                ConnectServerAndClient(endpoint, server, client, out var s2cConnection, out var c2sConnection);

                // Ping

                client.BeginSend(c2sConnection, out var writer);
                writer.WriteInt(42);
                client.EndSend(writer);

                client.ScheduleUpdate().Complete();

                WaitForDataEvent(server, out var reader);

                Assert.AreEqual(42, reader.ReadInt());

                // Pong

                server.BeginSend(s2cConnection, out writer);
                writer.WriteInt(4242);
                server.EndSend(writer);

                server.ScheduleUpdate().Complete();

                WaitForDataEvent(client, out reader);

                Assert.AreEqual(4242, reader.ReadInt());
            }
        }

        [Test]
        public void SendMessage_PingPong_MaxLength(
            [ValueSource("s_EndpointParameters")] NetworkEndPoint endpoint,
            [ValueSource("s_SecureModeParameters")] SecureProtocolMode secureMode)
        {
            using (var server = CreateServer(secureMode))
            using (var client = CreateClient(secureMode))
            {
                var messageLength = NetworkParameterConstants.MTU - client.MaxHeaderSize(NetworkPipeline.Null);

                var sendBuffer = new NativeArray<byte>(messageLength, Allocator.Temp);
                var receiveBuffer = new NativeArray<byte>(messageLength, Allocator.Temp);

                ConnectServerAndClient(endpoint, server, client, out var s2cConnection, out var c2sConnection);

                // Ping

                for (int i = 0; i < messageLength; i++)
                    sendBuffer[i] = 42;

                client.BeginSend(c2sConnection, out var writer);
                writer.WriteBytes(sendBuffer);
                client.EndSend(writer);

                client.ScheduleUpdate().Complete();

                WaitForDataEvent(server, out var reader);

                reader.ReadBytes(receiveBuffer);
                Assert.AreEqual(messageLength, receiveBuffer.Length);
                for (int i = 0; i < messageLength; i++)
                    Assert.AreEqual(42, receiveBuffer[i]);

                // Pong

                for (int i = 0; i < sendBuffer.Length; i++)
                    sendBuffer[i] = 0x42;

                server.BeginSend(s2cConnection, out writer);
                writer.WriteBytes(sendBuffer);
                server.EndSend(writer);

                server.ScheduleUpdate().Complete();

                WaitForDataEvent(client, out reader);

                reader.ReadBytes(receiveBuffer);
                Assert.AreEqual(messageLength, receiveBuffer.Length);
                for (int i = 0; i < messageLength; i++)
                    Assert.AreEqual(0x42, receiveBuffer[i]);
            }
        }

        [UnityTest, UnityPlatform(RuntimePlatform.LinuxEditor, RuntimePlatform.WindowsEditor, RuntimePlatform.OSXEditor)]
        public IEnumerator SendMessage_ErrorIfNotRead()
        {
            using (var server = NetworkDriver.Create())
            using (var client = NetworkDriver.Create())
            {
                ConnectServerAndClient(NetworkEndPoint.LoopbackIpv4, server, client, out _, out var connection);

                client.BeginSend(connection, out var writer);
                writer.WriteInt(42);
                client.EndSend(writer);

                client.ScheduleUpdate().Complete();

                LogAssert.Expect(LogType.Error,
                    "Resetting event queue with pending events (Count=1, ConnectionID=0) Listening: 1");

                server.ScheduleUpdate().Complete();
                server.ScheduleUpdate().Complete();

                yield return null;
            }
        }

        [Test]
        public void SendMessage_FragmentationCloseToMTU()
        {
            using (var server = NetworkDriver.Create())
            using (var client = NetworkDriver.Create())
            {
                server.CreatePipeline(typeof(FragmentationPipelineStage));
                var pipe = client.CreatePipeline(typeof(FragmentationPipelineStage));

                ConnectServerAndClient(NetworkEndPoint.LoopbackIpv4, server, client, out _, out var connection);

                const int MinSize = NetworkParameterConstants.MTU - 100;
                const int MaxSize = NetworkParameterConstants.MTU + 100;

                for (int size = MinSize; size <= MaxSize; size++)
                {
                    using var buffer = new NativeArray<byte>(size, Allocator.Temp);

                    Assert.AreEqual((int)Error.StatusCode.Success, client.BeginSend(pipe, connection, out var writer));
                    Assert.IsTrue(writer.WriteBytes(buffer));
                    Assert.AreEqual(size, client.EndSend(writer));

                    client.ScheduleUpdate().Complete();

                    WaitForDataEvent(server, out _);
                }
            }
        }

        [Test]
        public void SendMessage_FragmentationCloseToMaximumPayloadCapacity()
        {
            const int PayloadCapacity = 4096;

            var settings = new NetworkSettings();
            settings.WithFragmentationStageParameters(PayloadCapacity);

            using (var server = NetworkDriver.Create(settings))
            using (var client = NetworkDriver.Create(settings))
            {
                server.CreatePipeline(typeof(FragmentationPipelineStage));
                var pipe = client.CreatePipeline(typeof(FragmentationPipelineStage));

                ConnectServerAndClient(NetworkEndPoint.LoopbackIpv4, server, client, out _, out var connection);

                for (int size = PayloadCapacity - 200; size <= PayloadCapacity; size++)
                {
                    using (var buffer = new NativeArray<byte>(size, Allocator.Temp))
                    {
                        Assert.AreEqual((int)Error.StatusCode.Success, client.BeginSend(pipe, connection, out var writer));
                        Assert.IsTrue(writer.WriteBytes(buffer));
                        Assert.AreEqual(size, client.EndSend(writer));

                        client.ScheduleUpdate().Complete();

                        WaitForDataEvent(server, out _);
                    }
                }
            }
        }

        [Test]
        public void SendMessage_FragmentationOnReliabilityWindowBoundary()
        {
            var settings = new NetworkSettings();
            settings.WithReliableStageParameters(windowSize: 1);

            using (var server = NetworkDriver.Create(settings))
            using (var client = NetworkDriver.Create(settings))
            {
                server.CreatePipeline(typeof(FragmentationPipelineStage), typeof(ReliableSequencedPipelineStage));
                var pipe = client.CreatePipeline(typeof(FragmentationPipelineStage), typeof(ReliableSequencedPipelineStage));

                ConnectServerAndClient(NetworkEndPoint.LoopbackIpv4, server, client, out _, out var connection);

                using (var buffer = new NativeArray<byte>(NetworkParameterConstants.MTU + 100, Allocator.Temp))
                {
                    client.BeginSend(pipe, connection, out var writer);
                    writer.WriteBytes(buffer);
                    Assert.AreEqual((int)Error.StatusCode.NetworkSendQueueFull, client.EndSend(writer));
                }
            }
        }

        [Test]
        [Ignore("Test is unstable. Need to investigate why. See MTT-2587.")]
        public void SendMessage_OverflowReliableSequenceNumber()
        {
            const int ReliableWindowSize = 32;

            var settings = new NetworkSettings();
            settings.WithReliableStageParameters(windowSize: ReliableWindowSize);

            using (var server = NetworkDriver.Create(settings))
            using (var client = NetworkDriver.Create(settings))
            {
                server.CreatePipeline(typeof(ReliableSequencedPipelineStage));
                var pipe = client.CreatePipeline(typeof(ReliableSequencedPipelineStage));

                ConnectServerAndClient(NetworkEndPoint.LoopbackIpv4, server, client, out _, out var connection);

                for (int i = 0; i < ushort.MaxValue + (2 * ReliableWindowSize); i++)
                {
                    client.BeginSend(pipe, connection, out var writer);
                    writer.WriteInt(i);
                    client.EndSend(writer);

                    client.ScheduleUpdate().Complete();

                    WaitForDataEvent(server, out var reader);

                    Assert.AreEqual(i, reader.ReadInt());
                }
            }
        }

        [Test]
        public void SendMessage_ReceiveAfterConnectionClose([ValueSource("s_SecureModeParameters")] SecureProtocolMode secureMode)
        {
            // Test only checks that receiving a message on a closed connection doesn't generate errors.

            using (var server = CreateServer(secureMode))
            using (var client = CreateClient(secureMode))
            {
                ConnectServerAndClient(NetworkEndPoint.LoopbackIpv4, server, client, out var s2cConnection, out var c2sConnection);

                client.Disconnect(c2sConnection);

                server.BeginSend(s2cConnection, out var writer);
                writer.WriteInt(42);
                server.EndSend(writer);

                RunPeriodicallyFor(500, () =>
                {
                    server.ScheduleFlushSend(default).Complete();
                    client.ScheduleUpdate().Complete();

                    Assert.AreEqual(NetworkEvent.Type.Empty, client.PopEvent(out _, out _));
                });
            }
        }
    }
}
