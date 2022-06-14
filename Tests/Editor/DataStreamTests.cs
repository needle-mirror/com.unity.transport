using System;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine.TestTools;
using UnityEngine;

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
                (byte)'s', (byte)'o', (byte)'m', (byte)'e',
                (byte)' ', (byte)'d', (byte)'a', (byte)'t', (byte)'a'
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

            LogAssert.Expect(LogType.Error, "Trying to read 1 bytes from a stream where only 0 are available");
            Assert.AreEqual(0, reader.ReadByte());
        }

        [Test]
        public void CreateStreamWithSourceByteArray()
        {
            byte[] byteArray = new byte[100];
            byteArray[0] = (byte)'a';
            byteArray[1] = (byte)'b';
            byteArray[2] = (byte)'c';

            DataStreamWriter dataStream;
            dataStream = new DataStreamWriter(byteArray.Length, Allocator.Temp);
            dataStream.WriteBytes(new NativeArray<byte>(byteArray, Allocator.Temp));

            var arr = dataStream.AsNativeArray();
            var reader = new DataStreamReader(arr);
            for (var i = 0; i < byteArray.Length; ++i)
            {
                Assert.AreEqual(byteArray[i], reader.ReadByte());
            }

            unsafe
            {
                var reader2 = new DataStreamReader((byte*)arr.GetUnsafePtr(), arr.Length);
                for (var i = 0; i < byteArray.Length; ++i)
                {
                    Assert.AreEqual(byteArray[i], reader2.ReadByte());
                }
            }
        }

        [Test]
        public void ReadIntoExistingByteArray()
        {
            var byteArray = new NativeArray<byte>(100, Allocator.Temp);

            DataStreamWriter dataStream;
            dataStream = new DataStreamWriter(3, Allocator.Temp);
            {
                dataStream.WriteByte((byte)'a');
                dataStream.WriteByte((byte)'b');
                dataStream.WriteByte((byte)'c');
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
            dataStream.WriteByte((byte)'a');
            dataStream.WriteByte((byte)'b');
            dataStream.WriteByte((byte)'c');
            dataStream.WriteByte((byte)'d');
            dataStream.WriteByte((byte)'e');
            dataStream.WriteByte((byte)'f');
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

                dataStream.WriteInt((int)1979);
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
        public void ReadWritePackedIntExistingData()
        {
            unsafe
            {
                var n = 300 * 4;
                var data = stackalloc byte[n];
                using (var compressionModel = new NetworkCompressionModel(Allocator.Persistent))
                {
                    var dataStream = new DataStreamWriter(data, n);
                    int base_val = -10;
                    int count = 20;
                    for (int i = 0; i < count; ++i)
                        dataStream.WritePackedInt(base_val + i, compressionModel);

                    dataStream.WriteInt((int)1979);
                    dataStream.Flush();
                    var reader = new DataStreamReader(data, n);
                    for (int i = 0; i < count; ++i)
                    {
                        var val = reader.ReadPackedInt(compressionModel);
                        Assert.AreEqual(base_val + i, val);
                    }
                    Assert.AreEqual(1979, reader.ReadInt());
                }
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

                dataStream.WriteInt((int)1979);
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
                dataStream.WriteInt((int)0);
                for (uint i = 0; i < count; ++i)
                    dataStream.WritePackedUInt(base_val + i, compressionModel);

                dataStream.Flush();
                def.WriteInt(1979);
                def = dataStream;
                dataStream.WriteInt((int)0);
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
        public void ReadWriteFixedString32()
        {
            var dataStream = new DataStreamWriter(300 * 4, Allocator.Temp);

            var src = new FixedString32Bytes("This is a string");
            dataStream.WriteFixedString32(src);

            //Assert.AreEqual(src.LengthInBytes+2, dataStream.Length);

            var reader = new DataStreamReader(dataStream.AsNativeArray());
            var dst = reader.ReadFixedString32();
            Assert.AreEqual(src, dst);
        }

        [Test]
        public void ReadWritePackedFixedString32Delta()
        {
            var dataStream = new DataStreamWriter(300 * 4, Allocator.Temp);
            var compressionModel = new NetworkCompressionModel(Allocator.Temp);

            var src = new FixedString32Bytes("This is a string");
            var baseline = new FixedString32Bytes("This is another string");
            dataStream.WritePackedFixedString32Delta(src, baseline, compressionModel);
            dataStream.Flush();

            //Assert.LessOrEqual(dataStream.Length, src.LengthInBytes+2);

            var reader = new DataStreamReader(dataStream.AsNativeArray());
            var dst = reader.ReadPackedFixedString32Delta(baseline, compressionModel);
            Assert.AreEqual(src, dst);
        }

        [Test]
        public void ReadWriteFixedString64()
        {
            var dataStream = new DataStreamWriter(300 * 4, Allocator.Temp);

            var src = new FixedString64Bytes("This is a string");
            dataStream.WriteFixedString64(src);

            //Assert.AreEqual(src.LengthInBytes+2, dataStream.Length);

            var reader = new DataStreamReader(dataStream.AsNativeArray());
            var dst = reader.ReadFixedString64();
            Assert.AreEqual(src, dst);
        }

        [Test]
        public void ReadWritePackedFixedString64Delta()
        {
            var dataStream = new DataStreamWriter(300 * 4, Allocator.Temp);
            var compressionModel = new NetworkCompressionModel(Allocator.Temp);

            var src = new FixedString64Bytes("This is a string");
            var baseline = new FixedString64Bytes("This is another string");
            dataStream.WritePackedFixedString64Delta(src, baseline, compressionModel);
            dataStream.Flush();

            //Assert.LessOrEqual(dataStream.Length, src.LengthInBytes+2);

            var reader = new DataStreamReader(dataStream.AsNativeArray());
            var dst = reader.ReadPackedFixedString64Delta(baseline, compressionModel);
            Assert.AreEqual(src, dst);
        }

        [Test]
        public void ReadWriteFixedString128()
        {
            var dataStream = new DataStreamWriter(300 * 4, Allocator.Temp);

            var src = new FixedString128Bytes("This is a string");
            dataStream.WriteFixedString128(src);

            //Assert.AreEqual(src.LengthInBytes+2, dataStream.Length);

            var reader = new DataStreamReader(dataStream.AsNativeArray());
            var dst = reader.ReadFixedString128();
            Assert.AreEqual(src, dst);
        }

        [Test]
        public void ReadWritePackedFixedString128Delta()
        {
            var dataStream = new DataStreamWriter(300 * 4, Allocator.Temp);
            var compressionModel = new NetworkCompressionModel(Allocator.Temp);

            var src = new FixedString128Bytes("This is a string");
            var baseline = new FixedString128Bytes("This is another string");
            dataStream.WritePackedFixedString128Delta(src, baseline, compressionModel);
            dataStream.Flush();

            //Assert.LessOrEqual(dataStream.Length, src.LengthInBytes+2);

            var reader = new DataStreamReader(dataStream.AsNativeArray());
            var dst = reader.ReadPackedFixedString128Delta(baseline, compressionModel);
            Assert.AreEqual(src, dst);
        }

        [Test]
        public void ReadWriteFixedString512()
        {
            var dataStream = new DataStreamWriter(300 * 4, Allocator.Temp);

            var src = new FixedString512Bytes("This is a string");
            dataStream.WriteFixedString512(src);

            //Assert.AreEqual(src.LengthInBytes+2, dataStream.Length);

            var reader = new DataStreamReader(dataStream.AsNativeArray());
            var dst = reader.ReadFixedString512();
            Assert.AreEqual(src, dst);
        }

        [Test]
        public void ReadWritePackedFixedString512Delta()
        {
            var dataStream = new DataStreamWriter(300 * 4, Allocator.Temp);
            var compressionModel = new NetworkCompressionModel(Allocator.Temp);

            var src = new FixedString512Bytes("This is a string");
            var baseline = new FixedString512Bytes("This is another string");
            dataStream.WritePackedFixedString512Delta(src, baseline, compressionModel);
            dataStream.Flush();

            //Assert.LessOrEqual(dataStream.Length, src.LengthInBytes+2);

            var reader = new DataStreamReader(dataStream.AsNativeArray());
            var dst = reader.ReadPackedFixedString512Delta(baseline, compressionModel);
            Assert.AreEqual(src, dst);
        }

        [Test]
        public void ReadWriteFixedString4096()
        {
            var dataStream = new DataStreamWriter(300 * 4, Allocator.Temp);

            var src = new FixedString4096Bytes("This is a string");
            dataStream.WriteFixedString4096(src);

            //Assert.AreEqual(src.LengthInBytes+2, dataStream.Length);

            var reader = new DataStreamReader(dataStream.AsNativeArray());
            var dst = reader.ReadFixedString4096();
            Assert.AreEqual(src, dst);
        }

        [Test]
        public void ReadWritePackedFixedString4096Delta()
        {
            var dataStream = new DataStreamWriter(300 * 4, Allocator.Temp);
            var compressionModel = new NetworkCompressionModel(Allocator.Temp);

            var src = new FixedString4096Bytes("This is a string");
            var baseline = new FixedString4096Bytes("This is another string");
            dataStream.WritePackedFixedString4096Delta(src, baseline, compressionModel);
            dataStream.Flush();

            //Assert.LessOrEqual(dataStream.Length, src.LengthInBytes+2);

            var reader = new DataStreamReader(dataStream.AsNativeArray());
            var dst = reader.ReadPackedFixedString4096Delta(baseline, compressionModel);
            Assert.AreEqual(src, dst);
        }
        
        [Test]
        public void ReadWriteLong()
        {
            var dataStream = new DataStreamWriter(300 * 8, Allocator.Temp);
            long base_val = -99;
            long count = 277;
            for (uint i = 0; i < count; ++i)
                dataStream.WriteLong(base_val + i);

            dataStream.WriteLong(-1979);
            dataStream.Flush();
            var reader = new DataStreamReader(dataStream.AsNativeArray());
            for (uint i = 0; i < count; ++i)
            {
                var val = reader.ReadLong();
                Assert.AreEqual(base_val + i, val);
            }

            Assert.AreEqual(-1979, reader.ReadLong());
        }
        
        [Test]
        public void ReadWritePackedLong()
        {
            using (var compressionModel = new NetworkCompressionModel(Allocator.Persistent))
            {
                var dataStream = new DataStreamWriter(300 * 8, Allocator.Temp);
                long base_val = -99;
                long count = 277;
                for (uint i = 0; i < count; ++i)
                    dataStream.WritePackedLong(base_val + i, compressionModel);

                dataStream.WriteLong(-1979);
                dataStream.Flush();
                var reader = new DataStreamReader(dataStream.AsNativeArray());
                for (uint i = 0; i < count; ++i)
                {
                    var val = reader.ReadPackedLong(compressionModel);
                    Assert.AreEqual(base_val + i, val);
                }
                Assert.AreEqual(-1979, reader.ReadLong());
            }
        }    
        
        [Test]
        public void ReadWritePackedULong()
        {
            using (var compressionModel = new NetworkCompressionModel(Allocator.Persistent))
            {
                var dataStream = new DataStreamWriter(300 * 8, Allocator.Temp);
                ulong base_val = 2000;
                ulong count = 277;
                for (uint i = 0; i < count; ++i)
                    dataStream.WritePackedULong(base_val + i, compressionModel);

                dataStream.WriteULong(1979);
                dataStream.Flush();
                var reader = new DataStreamReader(dataStream.AsNativeArray());
                for (uint i = 0; i < count; ++i)
                {
                    var val = reader.ReadPackedULong(compressionModel);
                    Assert.AreEqual(base_val + i, val);
                }
                Assert.AreEqual(1979, reader.ReadULong());
            }
        }

        private struct ReaderTestJob : IJob
        {
            public DataStreamReader Reader;
            public NativeArray<int> ReturnValue;

            public void Execute()
            {
                ReturnValue[0] = Reader.ReadInt();
            }
        }

        [Test]
        public void ReaderAsJobParameter()
        {
            using (var returnValue = new NativeArray<int>(1, Allocator.TempJob))
            {
                var writer = new DataStreamWriter(sizeof(int), Allocator.Temp);
                writer.WriteInt(42);

                var reader = new DataStreamReader(writer.AsNativeArray());

                new ReaderTestJob
                {
                    Reader = reader,
                    ReturnValue = returnValue
                }.Run();

                Assert.AreEqual(42, returnValue[0]);
            }
        }
    }
}
