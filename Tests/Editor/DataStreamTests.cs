using System;
using NUnit.Framework;
using Unity.Collections;

// using FsCheck;

namespace Unity.Networking.Transport.Tests
{
    public class DataStreamTests
    {
        [Test]
        public void CreateStreamWithPartOfSourceByteArray()
        {
            byte[] byteArray =
            {
                (byte) 's', (byte) 'o', (byte) 'm', (byte) 'e',
                (byte) ' ', (byte) 'd', (byte) 'a', (byte) 't', (byte) 'a'
            };

            DataStreamWriter dataStream;
            dataStream = new DataStreamWriter(4, Allocator.Temp);
            dataStream.WriteBytes(new NativeArray<byte>(byteArray, Allocator.Temp).GetSubArray(0, 4));
            Assert.AreEqual(dataStream.Length, 4);
            var reader = new DataStreamReader(dataStream.AsNativeArray());
            for (int i = 0; i < dataStream.Length; ++i)
            {
                Assert.AreEqual(byteArray[i], reader.ReadByte());
            }

            Assert.Throws<ArgumentOutOfRangeException>(() => { reader.ReadByte(); });
        }

        [Test]
        public void CreateStreamWithSourceByteArray()
        {
            byte[] byteArray = new byte[100];
            byteArray[0] = (byte) 'a';
            byteArray[1] = (byte) 'b';
            byteArray[2] = (byte) 'c';

            DataStreamWriter dataStream;
            dataStream = new DataStreamWriter(byteArray.Length, Allocator.Temp);
            dataStream.WriteBytes(new NativeArray<byte>(byteArray, Allocator.Temp));
            var reader = new DataStreamReader(dataStream.AsNativeArray());
            for (int i = 0; i < byteArray.Length; ++i)
            {
                Assert.AreEqual(byteArray[i], reader.ReadByte());
            }
        }

        [Test]
        public void ReadIntoExistingByteArray()
        {
            var byteArray = new NativeArray<byte>(100, Allocator.Temp);

            DataStreamWriter dataStream;
            dataStream = new DataStreamWriter(3, Allocator.Temp);
            {
                dataStream.WriteByte((byte) 'a');
                dataStream.WriteByte((byte) 'b');
                dataStream.WriteByte((byte) 'c');
                var reader = new DataStreamReader(dataStream.AsNativeArray());
                reader.ReadBytes(byteArray.GetSubArray(0, dataStream.Length));
                reader = new DataStreamReader(dataStream.AsNativeArray());
                for (int i = 0; i < reader.Length; ++i)
                {
                    Assert.AreEqual(byteArray[i], reader.ReadByte());
                }
            }
        }

        [Test]
        public void ReadingDataFromStreamWithSliceOffset()
        {
            var dataStream = new DataStreamWriter(100, Allocator.Temp);
            dataStream.WriteByte((byte) 'a');
            dataStream.WriteByte((byte) 'b');
            dataStream.WriteByte((byte) 'c');
            dataStream.WriteByte((byte) 'd');
            dataStream.WriteByte((byte) 'e');
            dataStream.WriteByte((byte) 'f');
            var reader = new DataStreamReader(dataStream.AsNativeArray().GetSubArray(3, 3));
            Assert.AreEqual('d', reader.ReadByte());
            Assert.AreEqual('e', reader.ReadByte());
            Assert.AreEqual('f', reader.ReadByte());
        }

        [Test]
        public void ReadWritePackedUInt()
        {
            using (var compressionModel = new NetworkCompressionModel(Allocator.Persistent))
            {
                var dataStream = new DataStreamWriter(300 * 4, Allocator.Temp);
                uint base_val = 2000;
                uint count = 277;
                for (uint i = 0; i < count; ++i)
                    dataStream.WritePackedUInt(base_val + i, compressionModel);

                dataStream.WriteInt((int) 1979);
                dataStream.Flush();
                var reader = new DataStreamReader(dataStream.AsNativeArray());
                for (uint i = 0; i < count; ++i)
                {
                    var val = reader.ReadPackedUInt(compressionModel);
                    Assert.AreEqual(base_val + i, val);
                }
                Assert.AreEqual(1979, reader.ReadInt());
            }
        }
        [Test]
        public void ReadWritePackedInt()
        {
            using (var compressionModel = new NetworkCompressionModel(Allocator.Persistent))
            {
                var dataStream = new DataStreamWriter(300 * 4, Allocator.Temp);
                int base_val = -10;
                int count = 20;
                for (int i = 0; i < count; ++i)
                    dataStream.WritePackedInt(base_val + i, compressionModel);

                dataStream.WriteInt((int) 1979);
                dataStream.Flush();
                var reader = new DataStreamReader(dataStream.AsNativeArray());
                for (int i = 0; i < count; ++i)
                {
                    var val = reader.ReadPackedInt(compressionModel);
                    Assert.AreEqual(base_val + i, val);
                }
                Assert.AreEqual(1979, reader.ReadInt());
            }
        }
        [Test]
        public void ReadWritePackedUIntWithDeferred()
        {
            using (var compressionModel = new NetworkCompressionModel(Allocator.Persistent))
            {
                var dataStream = new DataStreamWriter(300 * 4, Allocator.Temp);
                uint base_val = 2000;
                uint count = 277;
                var def = dataStream;
                dataStream.WriteInt((int) 0);
                for (uint i = 0; i < count; ++i)
                    dataStream.WritePackedUInt(base_val + i, compressionModel);

                dataStream.Flush();
                def.WriteInt(1979);
                def = dataStream;
                dataStream.WriteInt((int) 0);
                def.WriteInt(1979);
                dataStream.Flush();
                var reader = new DataStreamReader(dataStream.AsNativeArray());
                Assert.AreEqual(1979, reader.ReadInt());
                for (uint i = 0; i < count; ++i)
                {
                    var val = reader.ReadPackedUInt(compressionModel);
                    Assert.AreEqual(base_val + i, val);
                }
                Assert.AreEqual(1979, reader.ReadInt());
            }
        }
        [Test]
        public void WriteOutOfBounds()
        {
            var dataStream = new DataStreamWriter(9, Allocator.Temp);
            Assert.IsTrue(dataStream.WriteInt(42));
            Assert.AreEqual(4, dataStream.Length);
            Assert.IsTrue(dataStream.WriteInt(42));
            Assert.AreEqual(8, dataStream.Length);
            Assert.IsFalse(dataStream.HasFailedWrites);
            Assert.IsFalse(dataStream.WriteInt(42));
            Assert.AreEqual(8, dataStream.Length);
            Assert.IsTrue(dataStream.HasFailedWrites);

            Assert.IsFalse(dataStream.WriteShort(42));
            Assert.AreEqual(8, dataStream.Length);
            Assert.IsTrue(dataStream.HasFailedWrites);

            Assert.IsTrue(dataStream.WriteByte(42));
            Assert.AreEqual(9, dataStream.Length);
            Assert.IsTrue(dataStream.HasFailedWrites);

            Assert.IsFalse(dataStream.WriteByte(42));
            Assert.AreEqual(9, dataStream.Length);
            Assert.IsTrue(dataStream.HasFailedWrites);
        }
        [Test]
        public void ReadWriteString()
        {
            var dataStream = new DataStreamWriter(300 * 4, Allocator.Temp);

            NativeString64 src = new NativeString64("This is a string");
            dataStream.WriteString(src);

            //Assert.AreEqual(src.LengthInBytes+2, dataStream.Length);

            var reader = new DataStreamReader(dataStream.AsNativeArray());
            var dst = reader.ReadString();
            Assert.AreEqual(src, dst);
        }
        [Test]
        public void ReadWritePackedStringDelta()
        {
            var dataStream = new DataStreamWriter(300 * 4, Allocator.Temp);
            var compressionModel = new NetworkCompressionModel(Allocator.Temp);

            NativeString64 src = new NativeString64("This is a string");
            NativeString64 baseline = new NativeString64("This is another string");
            dataStream.WritePackedStringDelta(src, baseline, compressionModel);
            dataStream.Flush();

            //Assert.LessOrEqual(dataStream.Length, src.LengthInBytes+2);

            var reader = new DataStreamReader(dataStream.AsNativeArray());
            var dst = reader.ReadPackedStringDelta(baseline, compressionModel);
            Assert.AreEqual(src, dst);
        }
    }
}