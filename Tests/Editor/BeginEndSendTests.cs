using System;
using Unity.Collections;
using NUnit.Framework;
using Unity.Networking.Transport.Protocols;
using UnityEngine;
using UnityEngine.TestTools;
using Unity.Jobs;

namespace Unity.Networking.Transport.Tests
{
    public class BeginEndSendTests
    {
        private NetworkDriver Driver;
        private NetworkDriver RemoteDriver;
        private NetworkConnection ToRemoteConnection;
        private NetworkConnection ToLocalConnection;

        [SetUp]
        public void IPC_Setup()
        {
            Driver = TestNetworkDriver.Create(new NetworkDataStreamParameter {size = 64});
            RemoteDriver = TestNetworkDriver.Create(new NetworkDataStreamParameter {size = 64});

            RemoteDriver.Bind(NetworkEndPoint.LoopbackIpv4);
            RemoteDriver.Listen();
            ToRemoteConnection = Driver.Connect(RemoteDriver.LocalEndPoint());
            Driver.ScheduleFlushSend(default).Complete();
            RemoteDriver.ScheduleUpdate().Complete();
            ToLocalConnection = RemoteDriver.Accept();
            Assert.AreNotEqual(default, ToLocalConnection);
            Driver.ScheduleUpdate().Complete();
            var evt = Driver.PopEventForConnection(ToRemoteConnection, out var reader);
            Assert.AreEqual(NetworkEvent.Type.Connect, evt);
        }

        [TearDown]
        public void IPC_TearDown()
        {
            Driver.Dispose();
            RemoteDriver.Dispose();
        }
        [Test]
        public void BeginEndSimple()
        {
            var writer = Driver.BeginSend(ToRemoteConnection);
            Assert.AreEqual(NetworkParameterConstants.MTU - UdpCHeader.Length, writer.Capacity);
            writer.WriteInt(42);
            Driver.EndSend(writer);
            Driver.ScheduleFlushSend(default).Complete();
            RemoteDriver.ScheduleUpdate().Complete();
            var evt = RemoteDriver.PopEventForConnection(ToLocalConnection, out var reader);
            Assert.AreEqual(NetworkEvent.Type.Data, evt);
            Assert.AreEqual(4, reader.Length);
            Assert.AreEqual(42, reader.ReadInt());
        }
        [Test]
        public void NestedBeginEndSend()
        {
            var writer = Driver.BeginSend(ToRemoteConnection);
            var writer2 = Driver.BeginSend(ToRemoteConnection);
            writer.WriteInt(42);
            writer2.WriteInt(4242);
            Driver.EndSend(writer2);
            Driver.EndSend(writer);
            Driver.ScheduleFlushSend(default).Complete();
            RemoteDriver.ScheduleUpdate().Complete();
            var evt = RemoteDriver.PopEventForConnection(ToLocalConnection, out var reader);
            Assert.AreEqual(NetworkEvent.Type.Data, evt);
            Assert.AreEqual(4, reader.Length);
            Assert.AreEqual(4242, reader.ReadInt());
            evt = RemoteDriver.PopEventForConnection(ToLocalConnection, out reader);
            Assert.AreEqual(NetworkEvent.Type.Data, evt);
            Assert.AreEqual(4, reader.Length);
            Assert.AreEqual(42, reader.ReadInt());
        }
        [Test]
        public void OverlappingBeginEndSend()
        {
            var writer = Driver.BeginSend(ToRemoteConnection);
            var writer2 = Driver.BeginSend(ToRemoteConnection);
            writer.WriteInt(42);
            writer2.WriteInt(4242);
            Driver.EndSend(writer);
            Driver.EndSend(writer2);
            Driver.ScheduleFlushSend(default).Complete();
            RemoteDriver.ScheduleUpdate().Complete();
            var evt = RemoteDriver.PopEventForConnection(ToLocalConnection, out var reader);
            Assert.AreEqual(NetworkEvent.Type.Data, evt);
            Assert.AreEqual(4, reader.Length);
            Assert.AreEqual(42, reader.ReadInt());
            evt = RemoteDriver.PopEventForConnection(ToLocalConnection, out reader);
            Assert.AreEqual(NetworkEvent.Type.Data, evt);
            Assert.AreEqual(4, reader.Length);
            Assert.AreEqual(4242, reader.ReadInt());
        }
        [Test]
        public void MissingEndSend()
        {
            var writer = Driver.BeginSend(ToRemoteConnection);
            writer.WriteInt(42);
            LogAssert.Expect(LogType.Error, "Missing EndSend, calling BeginSend without calling EndSend will result in a memory leak");
            Driver.ScheduleUpdate().Complete();
        }
        [Test]
        public void DuplicateEndSend()
        {
            var writer = Driver.BeginSend(ToRemoteConnection);
            writer.WriteInt(42);
            Driver.EndSend(writer);
            Assert.Throws<InvalidOperationException>(()=>{Driver.EndSend(writer);});

            Driver.ScheduleFlushSend(default).Complete();
            RemoteDriver.ScheduleUpdate().Complete();
            var evt = RemoteDriver.PopEventForConnection(ToLocalConnection, out var reader);
            Assert.AreEqual(NetworkEvent.Type.Data, evt);
            Assert.AreEqual(4, reader.Length);
            Assert.AreEqual(42, reader.ReadInt());
        }
        [Test]
        public void DuplicateAbortSend()
        {
            var writer = Driver.BeginSend(ToRemoteConnection);
            writer.WriteInt(42);
            Driver.AbortSend(writer);
            Assert.Throws<InvalidOperationException>(()=>{Driver.AbortSend(writer);});

            Driver.ScheduleFlushSend(default).Complete();
            RemoteDriver.ScheduleUpdate().Complete();
            var evt = RemoteDriver.PopEventForConnection(ToLocalConnection, out var reader);
            Assert.AreEqual(NetworkEvent.Type.Empty, evt);
        }
        [Test]
        public void AbortBeforeEndSend()
        {
            var writer = Driver.BeginSend(ToRemoteConnection);
            writer.WriteInt(42);
            Driver.AbortSend(writer);
            Assert.Throws<InvalidOperationException>(()=>{Driver.EndSend(writer);});

            Driver.ScheduleFlushSend(default).Complete();
            RemoteDriver.ScheduleUpdate().Complete();
            var evt = RemoteDriver.PopEventForConnection(ToLocalConnection, out var reader);
            Assert.AreEqual(NetworkEvent.Type.Empty, evt);
        }
        [Test]
        public void EndBeforeAbortSend()
        {
            var writer = Driver.BeginSend(ToRemoteConnection);
            writer.WriteInt(42);
            Driver.EndSend(writer);
            Assert.Throws<InvalidOperationException>(()=>{Driver.AbortSend(writer);});

            Driver.ScheduleFlushSend(default).Complete();
            RemoteDriver.ScheduleUpdate().Complete();
            var evt = RemoteDriver.PopEventForConnection(ToLocalConnection, out var reader);
            Assert.AreEqual(NetworkEvent.Type.Data, evt);
            Assert.AreEqual(4, reader.Length);
            Assert.AreEqual(42, reader.ReadInt());
        }
        struct BeginSendJob : IJob
        {
            public static DataStreamWriter writer = default;
            public NetworkDriver Driver;
            public NetworkConnection ToRemoteConnection;
            public void Execute()
            {
                writer = Driver.BeginSend(ToRemoteConnection);
                writer.WriteInt(42);
            }
        }
        [Test]
        public void EndSendAfterDispose()
        {
            var beginJob = new BeginSendJob
            {
                Driver = Driver,
                ToRemoteConnection = ToRemoteConnection
            };
            beginJob.Schedule().Complete();
            Assert.Catch(()=>{Driver.EndSend(BeginSendJob.writer);});

            LogAssert.Expect(LogType.Error, "Missing EndSend, calling BeginSend without calling EndSend will result in a memory leak");
            Driver.ScheduleUpdate().Complete();
        }
        [Test]
        public void EndSendWithoutBeginSend()
        {
            var writer = new DataStreamWriter(16, Allocator.Temp);
            Assert.Throws<InvalidOperationException>(()=>{Driver.EndSend(writer);});
        }
    }
}