using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using System;
using System.Diagnostics;
using Unity.Burst;

namespace Unity.Networking.Transport
{
    [StructLayout(LayoutKind.Explicit)]
    internal struct UIntFloat
    {
        [FieldOffset(0)] public float floatValue;

        [FieldOffset(0)] public uint intValue;

        [FieldOffset(0)] public double doubleValue;

        [FieldOffset(0)] public ulong longValue;
    }

    /// <summary>
    /// Data streams can be used to serialize data over the network. The
    /// <c>DataStreamWriter</c> and <c>DataStreamReader</c> classes work together
    /// to serialize data for sending and then to deserialize when receiving.
    /// </summary>
    /// <remarks>
    /// The reader can be used to deserialize the data from a NativeArray&lt;byte&gt;, writing data
    /// to a NativeArray&lt;byte&gt; and reading it back can be done like this:
    /// <code>
    /// using (var data = new NativeArray&lt;byte&gt;(16, Allocator.Persistent))
    /// {
    ///     var dataWriter = new DataStreamWriter(data);
    ///     dataWriter.WriteInt(42);
    ///     dataWriter.WriteInt(1234);
    ///     // Length is the actual amount of data inside the writer,
    ///     // Capacity is the total amount.
    ///     var dataReader = new DataStreamReader(nativeArrayOfBytes.GetSubArray(0, dataWriter.Length));
    ///     var myFirstInt = dataReader.ReadInt();
    ///     var mySecondInt = dataReader.ReadInt();
    /// }
    /// </code>
    ///
    /// There are a number of functions for various data types. If a copy of the writer
    /// is stored it can be used to overwrite the data later on, this is particularly useful when
    /// the size of the data is written at the start and you want to write it at
    /// the end when you know the value.
    ///
    /// <code>
    /// using (var data = new NativeArray&lt;byte&gt;(16, Allocator.Persistent))
    /// {
    ///     var dataWriter = new DataStreamWriter(data);
    ///     // My header data
    ///     var headerSizeMark = dataWriter;
    ///     dataWriter.WriteUShort((ushort)0);
    ///     var payloadSizeMark = dataWriter;
    ///     dataWriter.WriteUShort((ushort)0);
    ///     dataWriter.WriteInt(42);
    ///     dataWriter.WriteInt(1234);
    ///     var headerSize = data.Length;
    ///     // Update header size to correct value
    ///     headerSizeMark.WriteUShort((ushort)headerSize);
    ///     // My payload data
    ///     byte[] someBytes = Encoding.ASCII.GetBytes("some string");
    ///     dataWriter.Write(someBytes, someBytes.Length);
    ///     // Update payload size to correct value
    ///     payloadSizeMark.WriteUShort((ushort)(dataWriter.Length - headerSize));
    /// }
    /// </code>
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct DataStreamWriter
    {
        /// <summary>
        /// Show the byte order in which the current computer architecture stores data.
        /// </summary>
        /// <remarks>
        /// Different computer architectures store data using different byte orders.
        /// <list type="bullet">
        /// <item>Big-endian: the most significant byte is at the left end of a word.</item>
        /// <item>Little-endian: means the most significant byte is at the right end of a word.</item>
        /// </list>
        /// </remarks>
        public static bool IsLittleEndian
        {
            get
            {
                uint test = 1;
                byte* testPtr = (byte*)&test;
                return testPtr[0] == 1;
            }
        }

        struct StreamData
        {
            public byte* buffer;
            public int length;
            public int capacity;
            public ulong bitBuffer;
            public int bitIndex;
            public int failedWrites;
        }

        [NativeDisableUnsafePtrRestriction] StreamData m_Data;
        internal IntPtr m_SendHandleData;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        AtomicSafetyHandle m_Safety;
#endif

        /// <summary>
        /// Initializes a new instance of the DataStreamWriter struct.
        /// </summary>
        /// <param name="length">The length of the buffer.</param>
        /// <param name="allocator">The <see cref="Allocator"/> used to allocate the memory.</param>
        public DataStreamWriter(int length, Allocator allocator)
        {
            CheckAllocator(allocator);
            Initialize(out this, new NativeArray<byte>(length, allocator));
        }

        /// <summary>
        /// Initializes a new instance of the DataStreamWriter struct with a NativeArray{byte}
        /// </summary>
        /// <param name="data">The buffer we want to attach to our DataStreamWriter.</param>
        public DataStreamWriter(NativeArray<byte> data)
        {
            Initialize(out this, data);
        }

        /// <summary>
        /// Initializes a new instance of the DataStreamWriter struct with a memory we don't own
        /// </summary>
        /// <param name="data">Pointer to the data</param>
        /// <param name="length">Length of the data</param>
        public DataStreamWriter(byte* data, int length)
        {
            var na = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<byte>(data, length, Allocator.Invalid);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref na, AtomicSafetyHandle.GetTempMemoryHandle());
#endif
            Initialize(out this, na);
        }

        /// <summary>
        /// Convert internal data buffer to NativeArray for use in entities APIs.
        /// </summary>
        /// <returns>NativeArray representation of internal buffer.</returns>
        public NativeArray<byte> AsNativeArray()
        {
            var na = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<byte>(m_Data.buffer, Length, Allocator.Invalid);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref na, m_Safety);
#endif
            return na;
        }

        private static void Initialize(out DataStreamWriter self, NativeArray<byte> data)
        {
            self.m_SendHandleData = IntPtr.Zero;

            self.m_Data.capacity = data.Length;
            self.m_Data.length = 0;
            self.m_Data.buffer = (byte*)data.GetUnsafePtr();
            self.m_Data.bitBuffer = 0;
            self.m_Data.bitIndex = 0;
            self.m_Data.failedWrites = 0;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            self.m_Safety = NativeArrayUnsafeUtility.GetAtomicSafetyHandle(data);
#endif
        }

        private static short ByteSwap(short val)
        {
            return (short)(((val & 0xff) << 8) | ((val >> 8) & 0xff));
        }

        private static int ByteSwap(int val)
        {
            return (int)(((val & 0xff) << 24) | ((val & 0xff00) << 8) | ((val >> 8) & 0xff00) | ((val >> 24) & 0xff));
        }

        /// <summary>
        /// True if there is a valid data buffer present. This would be false
        /// if the writer was created with no arguments.
        /// </summary>
        public bool IsCreated
        {
            get { return m_Data.buffer != null; }
        }

        public bool HasFailedWrites => m_Data.failedWrites > 0;

        /// <summary>
        /// The total size of the data buffer, see <see cref="Length"/> for
        /// the size of space used in the buffer.
        /// </summary>
        public int Capacity
        {
            get
            {
                CheckRead();
                return m_Data.capacity;
            }
        }

        /// <summary>
        /// The size of the buffer used. See <see cref="Capacity"/> for the total size.
        /// </summary>
        public int Length
        {
            get
            {
                CheckRead();
                SyncBitData();
                return m_Data.length + ((m_Data.bitIndex + 7) >> 3);
            }
        }
        /// <summary>
        /// The size of the buffer used in bits. See <see cref="Length"/> for the length in bytes.
        /// </summary>
        public int LengthInBits
        {
            get
            {
                CheckRead();
                SyncBitData();
                return m_Data.length * 8 + m_Data.bitIndex;
            }
        }

        private void SyncBitData()
        {
            var bitIndex = m_Data.bitIndex;
            if (bitIndex <= 0)
                return;
            CheckWrite();

            var bitBuffer = m_Data.bitBuffer;
            int offset = 0;
            while (bitIndex > 0)
            {
                m_Data.buffer[m_Data.length + offset] = (byte)bitBuffer;
                bitIndex -= 8;
                bitBuffer >>= 8;
                ++offset;
            }
        }

        /// <summary>
        /// Causes any buffered bits to be written to the data buffer.
        /// Note this needs to be invoked after using methods that writes directly to the bit buffer.
        /// </summary>
        public void Flush()
        {
            while (m_Data.bitIndex > 0)
            {
                m_Data.buffer[m_Data.length++] = (byte)m_Data.bitBuffer;
                m_Data.bitIndex -= 8;
                m_Data.bitBuffer >>= 8;
            }

            m_Data.bitIndex = 0;
        }

        /// <summary>
        /// Writes a byte array (as pointer and length) to the stream.
        /// </summary>
        /// <param name="data">Pointer to the array.</param>
        /// <param name="bytes">Length of the array.</param>
        /// <returns>Whether the write was successful</returns>
        public bool WriteBytes(byte* data, int bytes)
        {
            CheckWrite();

            if (m_Data.length + ((m_Data.bitIndex + 7) >> 3) + bytes > m_Data.capacity)
            {
                ++m_Data.failedWrites;
                return false;
            }
            Flush();
            UnsafeUtility.MemCpy(m_Data.buffer + m_Data.length, data, bytes);
            m_Data.length += bytes;
            return true;
        }

        /// <summary>
        /// Writes an unsigned byte to the current stream and advances the stream position by one byte.
        /// </summary>
        /// <param name="value">The unsigned byte to write.</param>
        /// <returns>Whether the write was successful</returns>
        public bool WriteByte(byte value)
        {
            return WriteBytes((byte*)&value, sizeof(byte));
        }

        /// <summary>
        /// Copy NativeArray of bytes into the writers data buffer.
        /// </summary>
        /// <param name="value">Source byte array</param>
        /// <returns>Whether the write was successful</returns>
        public bool WriteBytes(NativeArray<byte> value)
        {
            return WriteBytes((byte*)value.GetUnsafeReadOnlyPtr(), value.Length);
        }

        /// <summary>
        /// Writes a 2-byte signed short to the current stream and advances the stream position by two bytes.
        /// </summary>
        /// <param name="value">The 2-byte signed short to write.</param>
        /// <returns>Whether the write was successful</returns>
        public bool WriteShort(short value)
        {
            return WriteBytes((byte*)&value, sizeof(short));
        }

        /// <summary>
        /// Writes a 2-byte unsigned short to the current stream and advances the stream position by two bytes.
        /// </summary>
        /// <param name="value">The 2-byte unsigned short to write.</param>
        /// <returns>Whether the write was successful</returns>
        public bool WriteUShort(ushort value)
        {
            return WriteBytes((byte*)&value, sizeof(ushort));
        }

        /// <summary>
        /// Writes a 4-byte signed integer from the current stream and advances the current position of the stream by four bytes.
        /// </summary>
        /// <param name="value">The 4-byte signed integer to write.</param>
        /// <returns>Whether the write was successful</returns>
        public bool WriteInt(int value)
        {
            return WriteBytes((byte*)&value, sizeof(int));
        }

        /// <summary>
        /// Reads a 4-byte unsigned integer from the current stream and advances the current position of the stream by four bytes.
        /// </summary>
        /// <param name="value">The 4-byte unsigned integer to write.</param>
        /// <returns>Whether the write was successful</returns>
        public bool WriteUInt(uint value)
        {
            return WriteBytes((byte*)&value, sizeof(uint));
        }

        /// <summary>
        /// Writes an 8-byte signed long from the stream and advances the current position of the stream by eight bytes.
        /// </summary>
        /// <param name="value">The 8-byte signed long to write.</param>
        /// <returns>Whether the write was successful</returns>
        public bool WriteLong(long value)
        {
            return WriteBytes((byte*)&value, sizeof(long));
        }

        /// <summary>
        /// Reads an 8-byte unsigned long from the stream and advances the current position of the stream by eight bytes.
        /// </summary>
        /// <param name="value">The 8-byte unsigned long to write.</param>
        /// <returns>Whether the write was successful</returns>
        public bool WriteULong(ulong value)
        {
            return WriteBytes((byte*)&value, sizeof(ulong));
        }

        /// <summary>
        /// Writes a 2-byte signed short to the current stream using Big-endian byte order and advances the stream position by two bytes.
        /// If the stream is in little-endian order, the byte order will be swapped.
        /// </summary>
        /// <param name="value">The 2-byte signed short to write.</param>
        /// <returns>Whether the write was successful</returns>
        public bool WriteShortNetworkByteOrder(short value)
        {
            short netValue = IsLittleEndian ? ByteSwap(value) : value;
            return WriteBytes((byte*)&netValue, sizeof(short));
        }

        /// <summary>
        /// Writes a 2-byte unsigned short to the current stream using Big-endian byte order and advances the stream position by two bytes.
        /// If the stream is in little-endian order, the byte order will be swapped.
        /// </summary>
        /// <param name="value">The 2-byte unsigned short to write.</param>
        /// <returns>Whether the write was successful</returns>
        public bool WriteUShortNetworkByteOrder(ushort value)
        {
            return WriteShortNetworkByteOrder((short)value);
        }

        /// <summary>
        /// Writes a 4-byte signed integer from the current stream using Big-endian byte order and advances the current position of the stream by four bytes.
        /// If the current machine is in little-endian order, the byte order will be swapped.
        /// </summary>
        /// <param name="value">The 4-byte signed integer to write.</param>
        /// <returns>Whether the write was successful</returns>
        public bool WriteIntNetworkByteOrder(int value)
        {
            int netValue = IsLittleEndian ? ByteSwap(value) : value;
            return WriteBytes((byte*)&netValue, sizeof(int));
        }

        /// <summary>
        /// Writes a 4-byte unsigned integer from the current stream using Big-endian byte order and advances the current position of the stream by four bytes.
        /// If the stream is in little-endian order, the byte order will be swapped.
        /// </summary>
        /// <param name="value">The 4-byte unsigned integer to write.</param>
        /// <returns>Whether the write was successful</returns>
        public bool WriteUIntNetworkByteOrder(uint value)
        {
            return WriteIntNetworkByteOrder((int)value);
        }

        /// <summary>
        /// Writes a 4-byte floating point value to the data stream.
        /// </summary>
        /// <param name="value">The 4-byte floating point value to write.</param>
        /// <returns>Whether the write was successful</returns>
        public bool WriteFloat(float value)
        {
            UIntFloat uf = new UIntFloat();
            uf.floatValue = value;
            return WriteInt((int)uf.intValue);
        }

        private void FlushBits()
        {
            while (m_Data.bitIndex >= 8)
            {
                m_Data.buffer[m_Data.length++] = (byte)m_Data.bitBuffer;
                m_Data.bitIndex -= 8;
                m_Data.bitBuffer >>= 8;
            }
        }

        void WriteRawBitsInternal(uint value, int numbits)
        {
            CheckBits(value, numbits);

            m_Data.bitBuffer |= ((ulong)value << m_Data.bitIndex);
            m_Data.bitIndex += numbits;
        }

        /// <summary>
        /// Appends a specified number of bits to the data stream.
        /// </summary>
        /// <param name="value">The bits to write.</param>
        /// <param name="numbits">A positive number of bytes to write.</param>
        /// <returns>Whether the write was successful</returns>
        public bool WriteRawBits(uint value, int numbits)
        {
            CheckWrite();

            if (m_Data.length + ((m_Data.bitIndex + numbits + 7) >> 3) > m_Data.capacity)
            {
                ++m_Data.failedWrites;
                return false;
            }
            WriteRawBitsInternal(value, numbits);
            FlushBits();
            return true;
        }

        /// <summary>
        /// Writes a 4-byte unsigned integer value to the data stream using a <see cref="NetworkCompressionModel"/>.
        /// </summary>
        /// <param name="value">The 4-byte unsigned integer to write.</param>
        /// <param name="model"><see cref="NetworkCompressionModel"/> model for writing value in a packed manner.</param>
        /// <returns>Whether the write was successful</returns>
        public bool WritePackedUInt(uint value, NetworkCompressionModel model)
        {
            CheckWrite();
            int bucket = model.CalculateBucket(value);
            uint offset = model.bucketOffsets[bucket];
            int bits = model.bucketSizes[bucket];
            ushort encodeEntry = model.encodeTable[bucket];

            if (m_Data.length + ((m_Data.bitIndex + (encodeEntry & 0xff) + bits + 7) >> 3) > m_Data.capacity)
            {
                ++m_Data.failedWrites;
                return false;
            }
            WriteRawBitsInternal((uint)(encodeEntry >> 8), encodeEntry & 0xFF);
            WriteRawBitsInternal(value - offset, bits);
            FlushBits();
            return true;
        }

        /// <summary>
        /// Writes an 8-byte unsigned long value to the data stream using a <see cref="NetworkCompressionModel"/>.
        /// </summary>
        /// <param name="value">The 8-byte unsigned long to write.</param>
        /// <param name="model"><see cref="NetworkCompressionModel"/> model for writing value in a packed manner.</param>
        /// <returns>Whether the write was successful</returns>
        public bool WritePackedULong(ulong value, NetworkCompressionModel model)
        {
            return WritePackedUInt((uint)(value >> 32), model) &
                WritePackedUInt((uint)(value & 0xFFFFFFFF), model);
        }

        /// <summary>
        /// Writes a 4-byte signed integer value to the data stream using a <see cref="NetworkCompressionModel"/>.
        /// Negative values are interleaved between positive values, i.e. (0, -1, 1, -2, 2)
        /// </summary>
        /// <param name="value">The 4-byte signed integer to write.</param>
        /// <param name="model"><see cref="NetworkCompressionModel"/> model for writing value in a packed manner.</param>
        /// <returns>Whether the write was successful</returns>
        public bool WritePackedInt(int value, NetworkCompressionModel model)
        {
            uint interleaved = (uint)((value >> 31) ^ (value << 1));      // interleave negative values between positive values: 0, -1, 1, -2, 2
            return WritePackedUInt(interleaved, model);
        }

        /// <summary>
        /// Writes a 8-byte signed long value to the data stream using a <see cref="NetworkCompressionModel"/>.
        /// </summary>
        /// <param name="value">The 8-byte signed long to write.</param>
        /// <param name="model"><see cref="NetworkCompressionModel"/> model for writing value in a packed manner.</param>
        /// <returns>Whether the write was successful</returns>
        public bool WritePackedLong(long value, NetworkCompressionModel model)
        {
            ulong interleaved = (ulong)((value >> 63) ^ (value << 1));      // interleave negative values between positive values: 0, -1, 1, -2, 2
            return WritePackedULong(interleaved, model);
        }

        /// <summary>
        /// Writes a 4-byte floating point value to the data stream using a <see cref="NetworkCompressionModel"/>.
        /// </summary>
        /// <param name="value">The 4-byte floating point value to write.</param>
        /// <param name="model"><see cref="NetworkCompressionModel"/> model for writing value in a packed manner.</param>
        /// <returns>Whether the write was successful</returns>
        public bool WritePackedFloat(float value, NetworkCompressionModel model)
        {
            return WritePackedFloatDelta(value, 0, model);
        }

        /// <summary>
        /// Writes a delta 4-byte unsigned integer value to the data stream using a <see cref="NetworkCompressionModel"/>.
        /// Note that the Uint values are cast to an Int after computing the diff.
        /// </summary>
        /// <param name="value">The current 4-byte unsigned integer value.</param>
        /// <param name="baseline">The previous 4-byte unsigned integer value, used to compute the diff.</param>
        /// <param name="model"><see cref="NetworkCompressionModel"/> model for writing value in a packed manner.</param>
        /// <returns>Whether the write was successful</returns>
        public bool WritePackedUIntDelta(uint value, uint baseline, NetworkCompressionModel model)
        {
            int diff = (int)(baseline - value);
            return WritePackedInt(diff, model);
        }

        /// <summary>
        /// Writes a delta 4-byte signed integer value to the data stream using a <see cref="NetworkCompressionModel"/>.
        /// </summary>
        /// <param name="value">The current 4-byte signed integer value.</param>
        /// <param name="baseline">The previous 4-byte signed integer value, used to compute the diff.</param>
        /// <param name="model"><see cref="NetworkCompressionModel"/> model for writing value in a packed manner.</param>
        /// <returns>Whether the write was successful</returns>
        public bool WritePackedIntDelta(int value, int baseline, NetworkCompressionModel model)
        {
            int diff = (int)(baseline - value);
            return WritePackedInt(diff, model);
        }

        /// <summary>
        /// Writes a delta 8-byte signed long value to the data stream using a <see cref="NetworkCompressionModel"/>.
        /// </summary>
        /// <param name="value">The current 8-byte signed long value.</param>
        /// <param name="baseline">The previous 8-byte signed long value, used to compute the diff.</param>
        /// <param name="model"><see cref="NetworkCompressionModel"/> model for writing value in a packed manner.</param>
        /// <returns>Whether the write was successful</returns>
        public bool WritePackedLongDelta(long value, long baseline, NetworkCompressionModel model)
        {
            long diff = (long)(baseline - value);
            return WritePackedLong(diff, model);
        }

        /// <summary>
        /// Writes a delta 8-byte unsigned long value to the data stream using a <see cref="NetworkCompressionModel"/>.
        /// Note that the unsigned long values are cast to a signed long after computing the diff.
        /// </summary>
        /// <param name="value">The current 8-byte unsigned long value.</param>
        /// <param name="baseline">The previous 8-byte unsigned long, used to compute the diff.</param>
        /// <param name="model"><see cref="NetworkCompressionModel"/> model for writing value in a packed manner.</param>
        /// <returns>Whether the write was successful</returns>
        public bool WritePackedULongDelta(ulong value, ulong baseline, NetworkCompressionModel model)
        {
            long diff = (long)(baseline - value);
            return WritePackedLong(diff, model);
        }

        /// <summary>
        /// Writes a 4-byte floating point value to the data stream.
        /// If the data did not change a zero bit is prepended, otherwise a 1 bit is prepended.
        /// When reading back the data, the first bit is then checked for whether the data was changed or not.
        /// </summary>
        /// <param name="value">The current 4-byte floating point value.</param>
        /// <param name="baseline">The previous 4-byte floating value, used to compute the diff.</param>
        /// <param name="model">Not currently used.</param>
        /// <returns>Whether the write was successful</returns>
        public bool WritePackedFloatDelta(float value, float baseline, NetworkCompressionModel model)
        {
            CheckWrite();
            var bits = 0;
            if (value != baseline)
                bits = 32;
            if (m_Data.length + ((m_Data.bitIndex + 1 + bits + 7) >> 3) > m_Data.capacity)
            {
                ++m_Data.failedWrites;
                return false;
            }
            if (bits == 0)
                WriteRawBitsInternal(0, 1);
            else
            {
                WriteRawBitsInternal(1, 1);
                UIntFloat uf = new UIntFloat();
                uf.floatValue = value;
                WriteRawBitsInternal(uf.intValue, bits);
            }
            FlushBits();
            return true;
        }

        /// <summary>
        /// Writes a <c>FixedString32Bytes</c> value to the data stream.
        /// </summary>
        /// <param name="str">The <c>FixedString32Bytes</c> to write.</param>
        /// <returns>Whether the write was successful</returns>
        public unsafe bool WriteFixedString32(FixedString32Bytes str)
        {
            int length = (int)*((ushort*)&str) + 2;
            byte* data = ((byte*)&str);
            return WriteBytes(data, length);
        }

        /// <summary>
        /// Writes a <c>FixedString64Bytes</c> value to the data stream.
        /// </summary>
        /// <param name="str">The <c>FixedString64Bytes</c> to write.</param>
        /// <returns>Whether the write was successful</returns>
        public unsafe bool WriteFixedString64(FixedString64Bytes str)
        {
            int length = (int)*((ushort*)&str) + 2;
            byte* data = ((byte*)&str);
            return WriteBytes(data, length);
        }

        /// <summary>
        /// Writes a <c>FixedString128Bytes</c> value to the data stream.
        /// </summary>
        /// <param name="str">The <c>FixedString128Bytes</c> to write.</param>
        /// <returns>Whether the write was successful</returns>
        public unsafe bool WriteFixedString128(FixedString128Bytes str)
        {
            int length = (int)*((ushort*)&str) + 2;
            byte* data = ((byte*)&str);
            return WriteBytes(data, length);
        }

        /// <summary>
        /// Writes a <c>FixedString512Bytes</c> value to the data stream.
        /// </summary>
        /// <param name="str">The <c>FixedString512Bytes</c> to write.</param>
        /// <returns>Whether the write was successful</returns>
        public unsafe bool WriteFixedString512(FixedString512Bytes str)
        {
            int length = (int)*((ushort*)&str) + 2;
            byte* data = ((byte*)&str);
            return WriteBytes(data, length);
        }

        /// <summary>
        /// Writes a <c>FixedString4096Bytes</c> value to the data stream.
        /// </summary>
        /// <param name="str">The <c>FixedString4096Bytes</c> to write.</param>
        /// <returns>Whether the write was successful</returns>
        public unsafe bool WriteFixedString4096(FixedString4096Bytes str)
        {
            int length = (int)*((ushort*)&str) + 2;
            byte* data = ((byte*)&str);
            return WriteBytes(data, length);
        }

        /// <summary>
        /// Writes a <c>FixedString32Bytes</c> delta value to the data stream using a <see cref="NetworkCompressionModel"/>.
        /// </summary>
        /// <param name="str">The current <c>FixedString32Bytes</c> value.</param>
        /// <param name="baseline">The previous <c>FixedString32Bytes</c> value, used to compute the diff.</param>
        /// <param name="model"><see cref="NetworkCompressionModel"/> model for writing value in a packed manner.</param>
        /// <returns>Whether the write was successful</returns>
        public unsafe bool WritePackedFixedString32Delta(FixedString32Bytes str, FixedString32Bytes baseline, NetworkCompressionModel model)
        {
            ushort length = *((ushort*)&str);
            byte* data = ((byte*)&str) + 2;
            return WritePackedFixedStringDelta(data, length, ((byte*)&baseline) + 2, *((ushort*)&baseline), model);
        }

        /// <summary>
        /// Writes a delta <c>FixedString64Bytes</c> value to the data stream using a <see cref="NetworkCompressionModel"/>.
        /// </summary>
        /// <param name="str">The current <c>FixedString64Bytes</c> value.</param>
        /// <param name="baseline">The previous <c>FixedString64Bytes</c> value, used to compute the diff.</param>
        /// <param name="model"><see cref="NetworkCompressionModel"/> model for writing value in a packed manner.</param>
        /// <returns>Whether the write was successful</returns>
        public unsafe bool WritePackedFixedString64Delta(FixedString64Bytes str, FixedString64Bytes baseline, NetworkCompressionModel model)
        {
            ushort length = *((ushort*)&str);
            byte* data = ((byte*)&str) + 2;
            return WritePackedFixedStringDelta(data, length, ((byte*)&baseline) + 2, *((ushort*)&baseline), model);
        }

        /// <summary>
        /// Writes a delta <c>FixedString128Bytes</c> value to the data stream using a <see cref="NetworkCompressionModel"/>.
        /// </summary>
        /// <param name="str">The current <c>FixedString128Bytes</c> value.</param>
        /// <param name="baseline">The previous <c>FixedString128Bytes</c> value, used to compute the diff.</param>
        /// <param name="model"><see cref="NetworkCompressionModel"/> model for writing value in a packed manner.</param>
        /// <returns>Whether the write was successful</returns>
        public unsafe bool WritePackedFixedString128Delta(FixedString128Bytes str, FixedString128Bytes baseline, NetworkCompressionModel model)
        {
            ushort length = *((ushort*)&str);
            byte* data = ((byte*)&str) + 2;
            return WritePackedFixedStringDelta(data, length, ((byte*)&baseline) + 2, *((ushort*)&baseline), model);
        }

        /// <summary>
        /// Writes a delta <c>FixedString512Bytes</c> value to the data stream using a <see cref="NetworkCompressionModel"/>.
        /// </summary>
        /// <param name="str">The current <c>FixedString512Bytes</c> value.</param>
        /// <param name="baseline">The previous <c>FixedString512Bytes</c> value, used to compute the diff.</param>
        /// <param name="model"><see cref="NetworkCompressionModel"/> model for writing value in a packed manner.</param>
        /// <returns>Whether the write was successful</returns>
        public unsafe bool WritePackedFixedString512Delta(FixedString512Bytes str, FixedString512Bytes baseline, NetworkCompressionModel model)
        {
            ushort length = *((ushort*)&str);
            byte* data = ((byte*)&str) + 2;
            return WritePackedFixedStringDelta(data, length, ((byte*)&baseline) + 2, *((ushort*)&baseline), model);
        }

        /// <summary>
        /// Writes a delta <c>FixedString4096Bytes</c> value to the data stream using a <see cref="NetworkCompressionModel"/>.
        /// </summary>
        /// <param name="str">The current <c>FixedString4096Bytes</c> value.</param>
        /// <param name="baseline">The previous <c>FixedString4096Bytes</c> value, used to compute the diff.</param>
        /// <param name="model"><see cref="NetworkCompressionModel"/> model for writing value in a packed manner.</param>
        /// <returns>Whether the write was successful</returns>
        public unsafe bool WritePackedFixedString4096Delta(FixedString4096Bytes str, FixedString4096Bytes baseline, NetworkCompressionModel model)
        {
            ushort length = *((ushort*)&str);
            byte* data = ((byte*)&str) + 2;
            return WritePackedFixedStringDelta(data, length, ((byte*)&baseline) + 2, *((ushort*)&baseline), model);
        }

        private unsafe bool WritePackedFixedStringDelta(byte* data, uint length, byte* baseData, uint baseLength, NetworkCompressionModel model)
        {
            var oldData = m_Data;
            if (!WritePackedUIntDelta(length, baseLength, model))
                return false;
            bool didFailWrite = false;
            if (length <= baseLength)
            {
                for (uint i = 0; i < length; ++i)
                    didFailWrite |= !WritePackedUIntDelta(data[i], baseData[i], model);
            }
            else
            {
                for (uint i = 0; i < baseLength; ++i)
                    didFailWrite |= !WritePackedUIntDelta(data[i], baseData[i], model);
                for (uint i = baseLength; i < length; ++i)
                    didFailWrite |= !WritePackedUInt(data[i], model);
            }
            // If anything was not written, rewind to the previous position
            if (didFailWrite)
            {
                m_Data = oldData;
                ++m_Data.failedWrites;
            }
            return !didFailWrite;
        }

        /// <summary>
        /// Moves the write position to the start of the data buffer used.
        /// </summary>
        public void Clear()
        {
            m_Data.length = 0;
            m_Data.bitIndex = 0;
            m_Data.bitBuffer = 0;
            m_Data.failedWrites = 0;
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        void CheckRead()
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        void CheckWrite()
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
#endif
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        static void CheckAllocator(Allocator allocator)
        {
            if (allocator != Allocator.Temp)
                throw new InvalidOperationException("DataStreamWriters can only be created with temp memory");
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        static void CheckBits(uint value, int numbits)
        {
            if (numbits < 0 || numbits > 32)
                throw new ArgumentOutOfRangeException("Invalid number of bits");
            if (value >= (1UL << numbits))
                throw new ArgumentOutOfRangeException("Value does not fit in the specified number of bits");
        }
    }

    /// <summary>
    /// The <c>DataStreamReader</c> class is the counterpart of the
    /// <c>DataStreamWriter</c> class and can be be used to deserialize
    /// data which was prepared with it.
    /// </summary>
    /// <remarks>
    /// Simple usage example:
    /// <code>
    /// using (var dataWriter = new DataStreamWriter(16, Allocator.Persistent))
    /// {
    ///     dataWriter.Write(42);
    ///     dataWriter.Write(1234);
    ///     // Length is the actual amount of data inside the writer,
    ///     // Capacity is the total amount.
    ///     var dataReader = new DataStreamReader(dataWriter, 0, dataWriter.Length);
    ///     var context = default(DataStreamReader.Context);
    ///     var myFirstInt = dataReader.ReadInt(ref context);
    ///     var mySecondInt = dataReader.ReadInt(ref context);
    /// }
    /// </code>
    ///
    /// The <c>DataStreamReader</c> carries the position of the read pointer inside the struct,
    /// taking a copy of the reader will also copy the read position. This includes passing the
    /// reader to a method by value instead of by ref.
    ///
    /// See the <see cref="DataStreamWriter"/> class for more information
    /// and examples.
    /// </remarks>
    public unsafe struct DataStreamReader
    {
        struct Context
        {
            public int m_ReadByteIndex;
            public int m_BitIndex;
            public ulong m_BitBuffer;
            public int m_FailedReads;
        }

        [NativeDisableUnsafePtrRestriction] byte* m_bufferPtr;
        Context m_Context;
        int m_Length;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
        AtomicSafetyHandle m_Safety;
#endif

        /// <summary>
        /// Initializes a new instance of the DataStreamReader struct with a NativeArray&lt;byte&gt;.
        /// </summary>
        /// <param name="array">The buffer to attach to the DataStreamReader.</param>
        public DataStreamReader(NativeArray<byte> array)
        {
            Initialize(out this, array);
        }

        /// <summary>
        /// Initializes a new instance of the DataStreamReader struct with a pointer and length.
        /// </summary>
        /// <param name="data">Pointer to the buffer to attach to the DataStreamReader.</param>
        /// <param name="length">Length of the buffer to attach to the DataStreamReader.</param>
        public DataStreamReader(byte* data, int length)
        {
            var na = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<byte>(data, length, Allocator.Invalid);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref na, AtomicSafetyHandle.GetTempMemoryHandle());
#endif
            Initialize(out this, na);
        }

        private static void Initialize(out DataStreamReader self, NativeArray<byte> array)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            self.m_Safety = NativeArrayUnsafeUtility.GetAtomicSafetyHandle(array);
#endif
            self.m_bufferPtr = (byte*)array.GetUnsafeReadOnlyPtr();
            self.m_Length = array.Length;
            self.m_Context = default;
        }

        /// <summary>
        /// Show the byte order in which the current computer architecture stores data.
        /// </summary>
        /// <remarks>
        /// Different computer architectures store data using different byte orders.
        /// <list type="bullet">
        /// <item>Big-endian: the most significant byte is at the left end of a word.</item>
        /// <item>Little-endian: means the most significant byte is at the right end of a word.</item>
        /// </list>
        /// </remarks>
        public bool IsLittleEndian => DataStreamWriter.IsLittleEndian;

        private static short ByteSwap(short val)
        {
            return (short)(((val & 0xff) << 8) | ((val >> 8) & 0xff));
        }

        private static int ByteSwap(int val)
        {
            return (int)(((val & 0xff) << 24) | ((val & 0xff00) << 8) | ((val >> 8) & 0xff00) | ((val >> 24) & 0xff));
        }

        /// <summary>
        /// If there is a read failure this returns true. A read failure might happen if this attempts to read more than there is capacity for.
        /// </summary>
        public bool HasFailedReads => m_Context.m_FailedReads > 0;

        /// <summary>
        /// The total size of the buffer space this reader is working with.
        /// </summary>
        public int Length
        {
            get
            {
                CheckRead();
                return m_Length;
            }
        }

        /// <summary>
        /// True if the reader has been pointed to a valid buffer space. This
        /// would be false if the reader was created with no arguments.
        /// </summary>
        public bool IsCreated
        {
            get { return m_bufferPtr != null; }
        }

        /// <summary>
        /// Read the requested number of bytes to the given pointer.
        /// </summary>
        /// <param name="data">Pointer to write the data to.</param>
        /// <param name="length">Number of bytes to read.</param>
        public void ReadBytes(byte* data, int length)
        {
            CheckRead();
            if (GetBytesRead() + length > m_Length)
            {
                ++m_Context.m_FailedReads;
#if ENABLE_UNITY_COLLECTIONS_CHECKS && !UNITY_DOTSRUNTIME
                UnityEngine.Debug.LogError($"Trying to read {length} bytes from a stream where only {m_Length - GetBytesRead()} are available");
#endif
                UnsafeUtility.MemClear(data, length);
                return;
            }
            // Restore the full bytes moved to the bit buffer but no consumed
            m_Context.m_ReadByteIndex -= (m_Context.m_BitIndex >> 3);
            m_Context.m_BitIndex = 0;
            m_Context.m_BitBuffer = 0;
            UnsafeUtility.MemCpy(data, m_bufferPtr + m_Context.m_ReadByteIndex, length);
            m_Context.m_ReadByteIndex += length;
        }

        /// <summary>
        /// Read and copy data into the given NativeArray of bytes. The number of bytes
        /// read is the length of the provided array.
        /// </summary>
        /// <param name="array">Array to read the data into.</param>
        public void ReadBytes(NativeArray<byte> array)
        {
            ReadBytes((byte*)array.GetUnsafePtr(), array.Length);
        }

        /// <summary>
        /// Gets the number of bytes read from the data stream.
        /// </summary>
        /// <returns>Number of bytes read.</returns>
        public int GetBytesRead()
        {
            return m_Context.m_ReadByteIndex - (m_Context.m_BitIndex >> 3);
        }

        /// <summary>
        /// Gets the number of bits read from the data stream.
        /// </summary>
        /// <returns>Number of bits read.</returns>
        public int GetBitsRead()
        {
            return (m_Context.m_ReadByteIndex << 3) - m_Context.m_BitIndex;
        }

        /// <summary>
        /// Sets the current position of this stream to the given value.
        /// An error will be logged if <paramref name="pos"/> is outside the length of the stream.
        /// <br/>
        /// In addition this will reset the bit index and the bit buffer.
        /// </summary>
        /// <param name="pos">Seek position.</param>
        public void SeekSet(int pos)
        {
            if (pos > m_Length)
            {
                ++m_Context.m_FailedReads;
#if ENABLE_UNITY_COLLECTIONS_CHECKS && !UNITY_DOTSRUNTIME
                UnityEngine.Debug.LogError($"Trying to seek to {pos} in a stream of length {m_Length}");
#endif
                return;
            }
            m_Context.m_ReadByteIndex = pos;
            m_Context.m_BitIndex = 0;
            m_Context.m_BitBuffer = 0UL;
        }

        /// <summary>
        /// Reads an unsigned byte from the current stream and advances the current position of the stream by one byte.
        /// </summary>
        /// <returns>The next byte read from the current stream, or 0 if the end of the stream has been reached.</returns>
        public byte ReadByte()
        {
            byte data;
            ReadBytes((byte*)&data, sizeof(byte));
            return data;
        }

        /// <summary>
        /// Reads a 2-byte signed short from the current stream and advances the current position of the stream by two bytes.
        /// </summary>
        /// <returns>A 2-byte signed short read from the current stream, or 0 if the end of the stream has been reached.</returns>
        public short ReadShort()
        {
            short data;
            ReadBytes((byte*)&data, sizeof(short));
            return data;
        }

        /// <summary>
        /// Reads a 2-byte unsigned short from the current stream and advances the current position of the stream by two bytes.
        /// </summary>
        /// <returns>A 2-byte unsigned short read from the current stream, or 0 if the end of the stream has been reached.</returns>
        public ushort ReadUShort()
        {
            ushort data;
            ReadBytes((byte*)&data, sizeof(ushort));
            return data;
        }

        /// <summary>
        /// Reads a 4-byte signed integer from the current stream and advances the current position of the stream by four bytes.
        /// </summary>
        /// <returns>A 4-byte signed integer read from the current stream, or 0 if the end of the stream has been reached.</returns>
        public int ReadInt()
        {
            int data;
            ReadBytes((byte*)&data, sizeof(int));
            return data;
        }

        /// <summary>
        /// Reads a 4-byte unsigned integer from the current stream and advances the current position of the stream by four bytes.
        /// </summary>
        /// <returns>A 4-byte unsigned integer read from the current stream, or 0 if the end of the stream has been reached.</returns>
        public uint ReadUInt()
        {
            uint data;
            ReadBytes((byte*)&data, sizeof(uint));
            return data;
        }

        /// <summary>
        /// Reads an 8-byte signed long from the stream and advances the current position of the stream by eight bytes.
        /// </summary>
        /// <returns>An 8-byte signed long read from the current stream, or 0 if the end of the stream has been reached.</returns>
        public long ReadLong()
        {
            long data;
            ReadBytes((byte*)&data, sizeof(long));
            return data;
        }

        /// <summary>
        /// Reads an 8-byte unsigned long from the stream and advances the current position of the stream by eight bytes.
        /// </summary>
        /// <returns>An 8-byte unsigned long read from the current stream, or 0 if the end of the stream has been reached.</returns>
        public ulong ReadULong()
        {
            ulong data;
            ReadBytes((byte*)&data, sizeof(ulong));
            return data;
        }  

        /// <summary>
        /// Reads a 2-byte signed short from the current stream in Big-endian byte order and advances the current position of the stream by two bytes.
        /// If the current endianness is in little-endian order, the byte order will be swapped.
        /// </summary>
        /// <returns>A 2-byte signed short read from the current stream, or 0 if the end of the stream has been reached.</returns>
        public short ReadShortNetworkByteOrder()
        {
            short data;
            ReadBytes((byte*)&data, sizeof(short));
            return IsLittleEndian ? ByteSwap(data) : data;
        }

        /// <summary>
        /// Reads a 2-byte unsigned short from the current stream in Big-endian byte order and advances the current position of the stream by two bytes.
        /// If the current endianness is in little-endian order, the byte order will be swapped.
        /// </summary>
        /// <returns>A 2-byte unsigned short read from the current stream, or 0 if the end of the stream has been reached.</returns>
        public ushort ReadUShortNetworkByteOrder()
        {
            return (ushort)ReadShortNetworkByteOrder();
        }

        /// <summary>
        /// Reads a 4-byte signed integer from the current stream in Big-endian byte order and advances the current position of the stream by four bytes.
        /// If the current endianness is in little-endian order, the byte order will be swapped.
        /// </summary>
        /// <returns>A 4-byte signed integer read from the current stream, or 0 if the end of the stream has been reached.</returns>
        public int ReadIntNetworkByteOrder()
        {
            int data;
            ReadBytes((byte*)&data, sizeof(int));
            return IsLittleEndian ? ByteSwap(data) : data;
        }

        /// <summary>
        /// Reads a 4-byte unsigned integer from the current stream in Big-endian byte order and advances the current position of the stream by four bytes.
        /// If the current endianness is in little-endian order, the byte order will be swapped.
        /// </summary>
        /// <returns>A 4-byte unsigned integer read from the current stream, or 0 if the end of the stream has been reached.</returns>
        public uint ReadUIntNetworkByteOrder()
        {
            return (uint)ReadIntNetworkByteOrder();
        }

        /// <summary>
        /// Reads a 4-byte floating point value from the current stream and advances the current position of the stream by four bytes.
        /// </summary>
        /// <returns>A 4-byte floating point value read from the current stream, or 0 if the end of the stream has been reached.</returns>
        public float ReadFloat()
        {
            UIntFloat uf = new UIntFloat();
            uf.intValue = (uint)ReadInt();
            return uf.floatValue;
        }

        /// <summary>
        /// Reads a 4-byte unsigned integer from the current stream using a <see cref="NetworkCompressionModel"/> and advances the current position the number of bits depending on the model.
        /// </summary>
        /// <param name="model"><see cref="NetworkCompressionModel"/> model for reading value in a packed manner.</param>
        /// <returns>A 4-byte unsigned integer read from the current stream, or 0 if the end of the stream has been reached.</returns>
        public uint ReadPackedUInt(NetworkCompressionModel model)
        {
            CheckRead();
            FillBitBuffer();
            uint peekMask = (1u << NetworkCompressionModel.k_MaxHuffmanSymbolLength) - 1u;
            uint peekBits = (uint)m_Context.m_BitBuffer & peekMask;
            ushort huffmanEntry = model.decodeTable[(int)peekBits];
            int symbol = huffmanEntry >> 8;
            int length = huffmanEntry & 0xFF;

            if (m_Context.m_BitIndex < length)
            {
                ++m_Context.m_FailedReads;
#if ENABLE_UNITY_COLLECTIONS_CHECKS && !UNITY_DOTSRUNTIME
                UnityEngine.Debug.LogError($"Trying to read {length} bits from a stream where only {m_Context.m_BitIndex} are available");
#endif
                return 0;
            }

            // Skip Huffman bits
            m_Context.m_BitBuffer >>= length;
            m_Context.m_BitIndex -= length;

            uint offset = model.bucketOffsets[symbol];
            int bits = model.bucketSizes[symbol];
            return ReadRawBitsInternal(bits) + offset;
        }

        void FillBitBuffer()
        {
            while (m_Context.m_BitIndex <= 56 && m_Context.m_ReadByteIndex < m_Length)
            {
                m_Context.m_BitBuffer |= (ulong)m_bufferPtr[m_Context.m_ReadByteIndex++] << m_Context.m_BitIndex;
                m_Context.m_BitIndex += 8;
            }
        }

        uint ReadRawBitsInternal(int numbits)
        {
            CheckBits(numbits);
            if (m_Context.m_BitIndex < numbits)
            {
                ++m_Context.m_FailedReads;
#if ENABLE_UNITY_COLLECTIONS_CHECKS && !UNITY_DOTSRUNTIME
                UnityEngine.Debug.LogError($"Trying to read {numbits} bits from a stream where only {m_Context.m_BitIndex} are available");
#endif
                return 0;
            }
            uint res = (uint)(m_Context.m_BitBuffer & ((1UL << numbits) - 1UL));
            m_Context.m_BitBuffer >>= numbits;
            m_Context.m_BitIndex -= numbits;
            return res;
        }

        /// <summary>
        /// Reads a specified number of bits from the data stream.
        /// </summary>
        /// <param name="numbits">A positive number of bytes to write.</param>
        /// <returns>A 4-byte unsigned integer read from the current stream, or 0 if the end of the stream has been reached.</returns>
        public uint ReadRawBits(int numbits)
        {
            CheckRead();
            FillBitBuffer();
            return ReadRawBitsInternal(numbits);
        }

        /// <summary>
        /// Reads an 8-byte unsigned long value from the data stream using a <see cref="NetworkCompressionModel"/>.
        /// </summary>
        /// <param name="model"><see cref="NetworkCompressionModel"/> model for reading value in a packed manner.</param>
        /// <returns>An 8-byte unsigned long read from the current stream, or 0 if the end of the stream has been reached.</returns>
        public ulong ReadPackedULong(NetworkCompressionModel model)
        {
            //hi
            ulong hi = ReadPackedUInt(model);
            hi <<= 32;
            hi |= ReadPackedUInt(model);
            return hi;
        }

        /// <summary>
        /// Reads a 4-byte signed integer value from the data stream using a <see cref="NetworkCompressionModel"/>.
        /// <br/>
        /// Negative values de-interleaves from positive values before returning, for example (0, -1, 1, -2, 2) -> (-2, -1, 0, 1, 2)
        /// </summary>
        /// <param name="model"><see cref="NetworkCompressionModel"/> model for reading value in a packed manner.</param>
        /// <returns>A 4-byte signed integer read from the current stream, or 0 if the end of the stream has been reached.</returns>
        public int ReadPackedInt(NetworkCompressionModel model)
        {
            uint folded = ReadPackedUInt(model);
            return (int)(folded >> 1) ^ -(int)(folded & 1);    // Deinterleave values from [0, -1, 1, -2, 2...] to [..., -2, -1, -0, 1, 2, ...]
        }

        /// <summary>
        /// Reads an 8-byte signed long value from the data stream using a <see cref="NetworkCompressionModel"/>.
        /// <br/>
        /// Negative values de-interleaves from positive values before returning, for example (0, -1, 1, -2, 2) -> (-2, -1, 0, 1, 2)
        /// </summary>
        /// <param name="model"><see cref="NetworkCompressionModel"/> model for reading value in a packed manner.</param>
        /// <returns>An 8-byte signed long read from the current stream, or 0 if the end of the stream has been reached.</returns>
        public long ReadPackedLong(NetworkCompressionModel model)
        {
            ulong folded = ReadPackedULong(model);
            return (long)(folded >> 1) ^ -(long)(folded & 1);    // Deinterleave values from [0, -1, 1, -2, 2...] to [..., -2, -1, -0, 1, 2, ...]
        }

        /// <summary>
        /// Reads a 4-byte floating point value from the data stream using a <see cref="NetworkCompressionModel"/>.
        /// </summary>
        /// <param name="model"><see cref="NetworkCompressionModel"/> model for reading value in a packed manner.</param>
        /// <returns>A 4-byte floating point value read from the current stream, or 0 if the end of the stream has been reached.</returns>
        public float ReadPackedFloat(NetworkCompressionModel model)
        {
            return ReadPackedFloatDelta(0, model);
        }

        /// <summary>
        /// Reads a 4-byte signed integer delta value from the data stream using a <see cref="NetworkCompressionModel"/>.
        /// </summary>
        /// <param name="baseline">The previous 4-byte signed integer value, used to compute the diff.</param>
        /// <param name="model"><see cref="NetworkCompressionModel"/> model for reading value in a packed manner.</param>
        /// <returns>A 4-byte signed integer read from the current stream, or 0 if the end of the stream has been reached.
        /// If the data did not change, this also returns 0.
        /// <br/>
        /// See: <see cref="HasFailedReads"/> to verify if the read failed.</returns>
        public int ReadPackedIntDelta(int baseline, NetworkCompressionModel model)
        {
            int delta = ReadPackedInt(model);
            return baseline - delta;
        }

        /// <summary>
        /// Reads a 4-byte unsigned integer delta value from the data stream using a <see cref="NetworkCompressionModel"/>.
        /// </summary>
        /// <param name="baseline">The previous 4-byte unsigned integer value, used to compute the diff.</param>
        /// <param name="model"><see cref="NetworkCompressionModel"/> model for reading value in a packed manner.</param>
        /// <returns>A 4-byte unsigned integer read from the current stream, or 0 if the end of the stream has been reached.
        /// If the data did not change, this also returns 0.
        /// <br/>
        /// See: <see cref="HasFailedReads"/> to verify if the read failed.</returns>
        public uint ReadPackedUIntDelta(uint baseline, NetworkCompressionModel model)
        {
            uint delta = (uint)ReadPackedInt(model);
            return baseline - delta;
        }

        /// <summary>
        /// Reads an 8-byte signed long delta value from the data stream using a <see cref="NetworkCompressionModel"/>.
        /// </summary>
        /// <param name="baseline">The previous 8-byte signed long value, used to compute the diff.</param>
        /// <param name="model"><see cref="NetworkCompressionModel"/> model for reading value in a packed manner.</param>
        /// <returns>An 8-byte signed long read from the current stream, or 0 if the end of the stream has been reached.
        /// If the data did not change, this also returns 0.
        /// <br/>
        /// See: <see cref="HasFailedReads"/> to verify if the read failed.</returns>
        public long ReadPackedLongDelta(long baseline, NetworkCompressionModel model)
        {
            long delta = ReadPackedLong(model);
            return baseline - delta;
        }

        /// <summary>
        /// Reads an 8-byte unsigned long delta value from the data stream using a <see cref="NetworkCompressionModel"/>.
        /// </summary>
        /// <param name="baseline">The previous 8-byte unsigned long value, used to compute the diff.</param>
        /// <param name="model"><see cref="NetworkCompressionModel"/> model for reading value in a packed manner.</param>
        /// <returns>An 8-byte unsigned long read from the current stream, or 0 if the end of the stream has been reached.
        /// If the data did not change, this also returns 0.
        /// <br/>
        /// See: <see cref="HasFailedReads"/> to verify if the read failed.</returns>
        public ulong ReadPackedULongDelta(ulong baseline, NetworkCompressionModel model)
        {
            ulong delta = (ulong)ReadPackedLong(model);
            return baseline - delta;
        }

        /// <summary>
        /// Reads a 4-byte floating point value from the data stream.
        ///
        /// If the first bit is 0, the data did not change and <paramref name="baseline"/> will be returned.
        /// </summary>
        /// <param name="baseline">The previous 4-byte floating point value.</param>
        /// <param name="model">Not currently used.</param>
        /// <returns>A 4-byte floating point value read from the current stream, or <paramref name="baseline"/> if there are no changes to the value.
        /// <br/>
        /// See: <see cref="HasFailedReads"/> to verify if the read failed.</returns>
        public float ReadPackedFloatDelta(float baseline, NetworkCompressionModel model)
        {
            CheckRead();
            FillBitBuffer();
            if (ReadRawBitsInternal(1) == 0)
                return baseline;

            var bits = 32;
            UIntFloat uf = new UIntFloat();
            uf.intValue = ReadRawBitsInternal(bits);
            return uf.floatValue;
        }

        /// <summary>
        /// Reads a <c>FixedString32Bytes</c> value from the current stream and advances the current position of the stream by the length of the string.
        /// </summary>
        /// <returns>A <c>FixedString32Bytes</c> value read from the current stream, or 0 if the end of the stream has been reached.</returns>
        public unsafe FixedString32Bytes ReadFixedString32()
        {
            FixedString32Bytes str;
            byte* data = ((byte*)&str) + 2;
            *(ushort*)&str = ReadFixedString(data, str.Capacity);
            return str;
        }

        /// <summary>
        /// Reads a <c>FixedString64Bytes</c> value from the current stream and advances the current position of the stream by the length of the string.
        /// </summary>
        /// <returns>A <c>FixedString64Bytes</c> value read from the current stream, or 0 if the end of the stream has been reached.</returns>
        public unsafe FixedString64Bytes ReadFixedString64()
        {
            FixedString64Bytes str;
            byte* data = ((byte*)&str) + 2;
            *(ushort*)&str = ReadFixedString(data, str.Capacity);
            return str;
        }

        /// <summary>
        /// Reads a <c>FixedString128Bytes</c> value from the current stream and advances the current position of the stream by the length of the string.
        /// </summary>
        /// <returns>A <c>FixedString128Bytes</c> value read from the current stream, or 0 if the end of the stream has been reached.</returns>
        public unsafe FixedString128Bytes ReadFixedString128()
        {
            FixedString128Bytes str;
            byte* data = ((byte*)&str) + 2;
            *(ushort*)&str = ReadFixedString(data, str.Capacity);
            return str;
        }

        /// <summary>
        /// Reads a <c>FixedString512Bytes</c> value from the current stream and advances the current position of the stream by the length of the string.
        /// </summary>
        /// <returns>A <c>FixedString512Bytes</c> value read from the current stream, or 0 if the end of the stream has been reached.</returns>
        public unsafe FixedString512Bytes ReadFixedString512()
        {
            FixedString512Bytes str;
            byte* data = ((byte*)&str) + 2;
            *(ushort*)&str = ReadFixedString(data, str.Capacity);
            return str;
        }

        /// <summary>
        /// Reads a <c>FixedString4096Bytes</c> value from the current stream and advances the current position of the stream by the length of the string.
        /// </summary>
        /// <returns>A <c>FixedString4096Bytes</c> value read from the current stream, or 0 if the end of the stream has been reached.</returns>
        public unsafe FixedString4096Bytes ReadFixedString4096()
        {
            FixedString4096Bytes str;
            byte* data = ((byte*)&str) + 2;
            *(ushort*)&str = ReadFixedString(data, str.Capacity);
            return str;
        }

        /// <summary>
        /// Read and copy a fixed string (of unknown length) into the given buffer.
        /// </summary>
        /// <param name="data">Pointer to the buffer to write the string bytes to.</param>
        /// <param name="maxLength">Length of the buffer to write the string bytes to.</param>
        /// <returns>Length of data read into byte array, or zero if error occurred.</returns>
        public unsafe ushort ReadFixedString(byte* data, int maxLength)
        {
            ushort length = ReadUShort();
            if (length > maxLength)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS && !UNITY_DOTSRUNTIME
                UnityEngine.Debug.LogError($"Trying to read a string of length {length} but max length is {maxLength}");
#endif
                return 0;
            }
            ReadBytes(data, length);
            return length;
        }

        /// <summary>
        /// Reads a <c>FixedString32Bytes</c> delta value to the data stream using a <see cref="NetworkCompressionModel"/>.
        /// </summary>
        /// <param name="baseline">The previous <c>FixedString32Bytes</c> value, used to compute the diff.</param>
        /// <param name="model"><see cref="NetworkCompressionModel"/> model for writing value in a packed manner.</param>
        /// <returns>A <c>FixedString32Bytes</c> value read from the current stream, or 0 if the end of the stream has been reached.</returns>
        public unsafe FixedString32Bytes ReadPackedFixedString32Delta(FixedString32Bytes baseline, NetworkCompressionModel model)
        {
            FixedString32Bytes str;
            byte* data = ((byte*)&str) + 2;
            *(ushort*)&str = ReadPackedFixedStringDelta(data, str.Capacity, ((byte*)&baseline) + 2, *((ushort*)&baseline), model);
            return str;
        }

        /// <summary>
        /// Reads a <c>FixedString64Bytes</c> delta value to the data stream using a <see cref="NetworkCompressionModel"/>.
        /// </summary>
        /// <param name="baseline">The previous <c>FixedString64Bytes</c> value, used to compute the diff.</param>
        /// <param name="model"><see cref="NetworkCompressionModel"/> model for writing value in a packed manner.</param>
        /// <returns>A <c>FixedString64Bytes</c> value read from the current stream, or 0 if the end of the stream has been reached.</returns>
        public unsafe FixedString64Bytes ReadPackedFixedString64Delta(FixedString64Bytes baseline, NetworkCompressionModel model)
        {
            FixedString64Bytes str;
            byte* data = ((byte*)&str) + 2;
            *(ushort*)&str = ReadPackedFixedStringDelta(data, str.Capacity, ((byte*)&baseline) + 2, *((ushort*)&baseline), model);
            return str;
        }

        /// <summary>
        /// Reads a <c>FixedString128Bytes</c> delta value to the data stream using a <see cref="NetworkCompressionModel"/>.
        /// </summary>
        /// <param name="baseline">The previous <c>FixedString128Bytes</c> value, used to compute the diff.</param>
        /// <param name="model"><see cref="NetworkCompressionModel"/> model for writing value in a packed manner.</param>
        /// <returns>A <c>FixedString128Bytes</c> value read from the current stream, or 0 if the end of the stream has been reached.</returns>
        public unsafe FixedString128Bytes ReadPackedFixedString128Delta(FixedString128Bytes baseline, NetworkCompressionModel model)
        {
            FixedString128Bytes str;
            byte* data = ((byte*)&str) + 2;
            *(ushort*)&str = ReadPackedFixedStringDelta(data, str.Capacity, ((byte*)&baseline) + 2, *((ushort*)&baseline), model);
            return str;
        }

        /// <summary>
        /// Reads a <c>FixedString512Bytes</c> delta value to the data stream using a <see cref="NetworkCompressionModel"/>.
        /// </summary>
        /// <param name="baseline">The previous <c>FixedString512Bytes</c> value, used to compute the diff.</param>
        /// <param name="model"><see cref="NetworkCompressionModel"/> model for writing value in a packed manner.</param>
        /// <returns>A <c>FixedString512Bytes</c> value read from the current stream, or 0 if the end of the stream has been reached.</returns>
        public unsafe FixedString512Bytes ReadPackedFixedString512Delta(FixedString512Bytes baseline, NetworkCompressionModel model)
        {
            FixedString512Bytes str;
            byte* data = ((byte*)&str) + 2;
            *(ushort*)&str = ReadPackedFixedStringDelta(data, str.Capacity, ((byte*)&baseline) + 2, *((ushort*)&baseline), model);
            return str;
        }

        /// <summary>
        /// Reads a <c>FixedString4096Bytes</c> delta value to the data stream using a <see cref="NetworkCompressionModel"/>.
        /// </summary>
        /// <param name="baseline">The previous <c>FixedString4096Bytes</c> value, used to compute the diff.</param>
        /// <param name="model"><see cref="NetworkCompressionModel"/> model for writing value in a packed manner.</param>
        /// <returns>A <c>FixedString4096Bytes</c> value read from the current stream, or 0 if the end of the stream has been reached.</returns>
        public unsafe FixedString4096Bytes ReadPackedFixedString4096Delta(FixedString4096Bytes baseline, NetworkCompressionModel model)
        {
            FixedString4096Bytes str;
            byte* data = ((byte*)&str) + 2;
            *(ushort*)&str = ReadPackedFixedStringDelta(data, str.Capacity, ((byte*)&baseline) + 2, *((ushort*)&baseline), model);
            return str;
        }

        /// <summary>
        /// Read and copy a fixed string delta (of unknown length) into the given buffer.
        /// </summary>
        /// <param name="data">Pointer to the buffer to write the string bytes to.</param>
        /// <param name="maxLength">Length of the buffer to write the string bytes to.</param>
        /// <param name="baseData">Pointer to the previous value, used to compute the diff.</param>
        /// <param name="baseLength">Length of the previous value, used to compute the diff.</param>
        /// <param name="model"><see cref="NetworkCompressionModel"/> model for writing value in a packed manner.</param>
        /// <returns>Length of data read into byte array, or zero if error occurred.</returns>
        public unsafe ushort ReadPackedFixedStringDelta(byte* data, int maxLength, byte* baseData, ushort baseLength, NetworkCompressionModel model)
        {
            uint length = ReadPackedUIntDelta(baseLength, model);
            if (length > (uint)maxLength)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS && !UNITY_DOTSRUNTIME
                UnityEngine.Debug.LogError($"Trying to read a string of length {length} but max length is {maxLength}");
#endif
                return 0;
            }
            if (length <= baseLength)
            {
                for (int i = 0; i < length; ++i)
                    data[i] = (byte)ReadPackedUIntDelta(baseData[i], model);
            }
            else
            {
                for (int i = 0; i < baseLength; ++i)
                    data[i] = (byte)ReadPackedUIntDelta(baseData[i], model);
                for (int i = baseLength; i < length; ++i)
                    data[i] = (byte)ReadPackedUInt(model);
            }
            return (ushort)length;
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        void CheckRead()
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        static void CheckBits(int numbits)
        {
            if (numbits < 0 || numbits > 32)
                throw new ArgumentOutOfRangeException("Invalid number of bits");
        }
    }
}
