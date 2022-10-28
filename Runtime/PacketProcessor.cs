using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Networking.Transport.Logging;

namespace Unity.Networking.Transport
{
    /// <summary>
    /// Provides an API for processing packets.
    /// </summary>
    public unsafe struct PacketProcessor
    {
        internal PacketsQueue m_Queue;

        internal int m_BufferIndex;

        private ref PacketMetadata PacketMetadataRef => ref m_Queue.GetMetadataRef(m_BufferIndex);
        private PacketBuffer PacketBuffer => m_Queue.GetPacketBuffer(m_BufferIndex);

        /// <summary>
        /// Whether the packet processor is valid.
        /// </summary>
        public bool IsCreated => m_Queue.IsCreated;
        /// <summary>
        /// The size of the current data of the packet.
        /// </summary>
        public int Length => PacketMetadataRef.DataLength;
        /// <summary>
        /// The current offset in the packet buffer of the data.
        /// </summary>
        public int Offset => PacketMetadataRef.DataOffset;
        /// <summary>
        /// The total capacity of the packet.
        /// </summary>
        public int Capacity => PacketMetadataRef.DataCapacity;
        /// <summary>
        /// The available amount of bytes at the end of the packet data.
        /// </summary>
        public int BytesAvailableAtEnd => Capacity - (Offset + Length);
        /// <summary>
        /// The available amount of bytes at the begining of the packet data.
        /// </summary>
        public int BytesAvailableAtStart => Offset;
        /// <summary>
        /// A reference to the Endpoint of the packet.
        /// </summary>
        public ref NetworkEndpoint EndpointRef => ref *((NetworkEndpoint*)PacketBuffer.Endpoint);
        /// <summary>
        /// A reference to the connection ID of the packet.
        /// </summary>
        internal ref ConnectionId ConnectionRef => ref PacketMetadataRef.Connection;

        /// <summary>
        /// Gets a reference to the payload data reinterpreted to the type T.
        /// </summary>
        /// <typeparam name="T">The type of the data.</typeparam>
        /// <param name="offset">The offset from the start of the payload.</param>
        /// <returns>Returns a reference to the Payload data</returns>
        /// <exception cref="ArgumentException">Throws an execption if there is no enough bytes in the payload for the specified type.</exception>
        public ref T GetPayloadDataRef<T>(int offset = 0) where T : unmanaged
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (UnsafeUtility.SizeOf<T>() + offset > Length)
                throw new ArgumentException($"The requested type {typeof(T).ToString()} does not fit in the payload data ({Length - offset})");
#endif
            return ref *(T*)(((byte*)GetUnsafePayloadPtr()) + Offset + offset);
        }

        /// <summary>
        /// Copies the provided bytes at the end of the packet and increases its size accordingly.
        /// </summary>
        /// <param name="dataPtr">The pointer to the data to copy.</param>
        /// <param name="size">The size in bytes to copy.</param>
        /// <exception cref="ArgumentException">Throws an exception if there are not enough bytes available at the end of the packet.</exception>
        public void AppendToPayload(void* dataPtr, int size)
        {
            if (size > BytesAvailableAtEnd)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                throw new ArgumentException($"The requested data size ({size}) does not fit at the end of the payload ({BytesAvailableAtEnd} Bytes available)");
#else
                DebugLog.ErrorPayloadNotFitEndSize(size, BytesAvailableAtEnd);
                return;
#endif
            }
            UnsafeUtility.MemCpy(((byte*)PacketBuffer.Payload) + Offset + Length, dataPtr, size);
            PacketMetadataRef.DataLength += size;
        }

        /// <summary>
        /// </summary>
        /// <param name="processor">The packet processor to copy the data from.</param>
        /// <exception cref="ArgumentException">Throws an exception if there are not enough bytes available at the end of the packet.</exception>
        public void AppendToPayload(PacketProcessor processor)
        {
            AppendToPayload((byte*)processor.GetUnsafePayloadPtr() + processor.Offset, processor.Length);
        }

        /// <summary>
        /// Copies the provided value at the end of the packet and increases its size accordingly.
        /// </summary>
        /// <typeparam name="T">The type of the data to copy.</typeparam>
        /// <param name="value">The value to copy.</param>
        /// <exception cref="ArgumentException">Throws an exception if there are not enough bytes available at the end of the packet.</exception>
        public void AppendToPayload<T>(T value) where T : unmanaged
        {
            var size = UnsafeUtility.SizeOf<T>();
            if (size > BytesAvailableAtEnd)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                throw new ArgumentException($"The requested data size ({size}) does not fit at the end of the payload ({BytesAvailableAtEnd} Bytes available)");
#else
                DebugLog.ErrorPayloadNotFitEndSize(size, BytesAvailableAtEnd);
                return;
#endif
            }
            UnsafeUtility.MemCpy(((byte*)PacketBuffer.Payload + Offset + Length), &value, size);
            PacketMetadataRef.DataLength += size;
        }

        /// <summary>
        /// Copies the provided value at the start of the packet and increases its size accordingly.
        /// </summary>
        /// <typeparam name="T">The type of the data to copy.</typeparam>
        /// <param name="value">The value to copy.</param>
        /// <exception cref="ArgumentException">Throws an exception if there are not enough bytes available at the end of the packet.</exception>
        public void PrependToPayload<T>(T value) where T : unmanaged
        {
            var size = UnsafeUtility.SizeOf<T>();
            if (size > BytesAvailableAtStart)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                throw new ArgumentException($"The requested data size ({size}) does not fit at the start of the payload ({BytesAvailableAtStart} Bytes available)");
#else
                DebugLog.ErrorPayloadNotFitStartSize(size, BytesAvailableAtStart);
                return;
#endif
            }

            UnsafeUtility.MemCpy((byte*)PacketBuffer.Payload + Offset - size, &value, size);
            PacketMetadataRef.DataOffset -= size;
            PacketMetadataRef.DataLength += size;
        }

        /// <summary>
        /// Gets and removes the data at the start of the payload reinterpreted to the type T.
        /// </summary>
        /// <typeparam name="T">The type of the data.</typeparam>
        /// <returns>The extracted data value</returns>
        /// <exception cref="ArgumentException">Throws an execption if there is no enough bytes in the payload for the specified type.</exception>
        public T RemoveFromPayloadStart<T>() where T : unmanaged
        {
            var size = UnsafeUtility.SizeOf<T>();
            if (size > Length)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                throw new ArgumentException($"The size of the required type ({size}) does not fit in the payload ({Length}).");
#else
                DebugLog.ErrorPayloadNotFitSize(size, Length);
                return default(T);
#endif
            }

            var value = GetPayloadDataRef<T>(0);
            PacketMetadataRef.DataOffset += size;
            PacketMetadataRef.DataLength -= size;
            return value;
        }

        /// <summary>
        /// Fill the buffer with the data at the start of the payload.
        /// </summary>
        /// <param name="ptr">Pointer to the start of the buffer to fill.</param>
        /// <param name="size">Size of the buffer to fill.</param>
        /// <exception cref="ArgumentException">If provided buffer is larger than payload.</exception>
        public void RemoveFromPayloadStart(void* ptr, int size)
        {
            if (size > Length)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                throw new ArgumentException($"The size of the buffer ({size}) is larger than the payload ({Length}).");
#else
                DebugLog.ErrorPayloadWrongSize(size, Length);
                return;
#endif
            }

            UnsafeUtility.MemCpy(ptr, ((byte*)PacketBuffer.Payload) + Offset, size);
            PacketMetadataRef.DataOffset += size;
            PacketMetadataRef.DataLength -= size;
        }

        /// <summary>
        /// Copies the current packet data to the destination pointer.
        /// </summary>
        /// <param name="destinationPtr">The destination pointer where the data will be copied.</param>
        /// <returns>Returns the ammount of bytes copied.</returns>
        /// <exception cref="OverflowException">Throws an exception if the packet has overflowed the buffer.</exception>
        public int CopyPayload(void* destinationPtr, int size)
        {
            if (Length <= 0)
                return 0;

            var copiedBytes = Length;

            if (size < Length)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                throw new ArgumentException($"The payload size ({Length}) does not fit in the provided pointer ({size})");
#else
                DebugLog.ErrorCopyPayloadFailure(Length, size);
                copiedBytes = size;
#endif
                
            }

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (Offset < 0)
                throw new OverflowException("Packet DataOffset must be >= 0");
            if (Offset + Length > Capacity)
                throw new OverflowException("Packet data overflows packet capacity");
#endif

            UnsafeUtility.MemCpy(destinationPtr, ((byte*)PacketBuffer.Payload) + Offset, copiedBytes);

            return copiedBytes;
        }

        /// <summary>
        /// Gets the unsafe pointer of the packet data.
        /// </summary>
        /// <returns>A pointer to the packet data.</returns>
        public void* GetUnsafePayloadPtr()
        {
            return (byte*)PacketBuffer.Payload;
        }

        /// <summary>
        /// Manually sets the packet metadata.
        /// </summary>
        /// <param name="size">The new size of the packet</param>
        /// <param name="offset">The new offset of the packet</param>
        /// <exception cref="ArgumentException">Throws an ArgumentException if the size and offset does not fit in the packet.</exception>
        internal void SetUnsafeMetadata(int size, int offset = 0)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (offset + size > Capacity || offset < 0 || size < 0)
                throw new ArgumentException($"The requested data size ({size}) and offset ({offset}) does not fit in the payload ({Capacity} Bytes available)");
#endif
            PacketMetadataRef.DataLength = size;
            PacketMetadataRef.DataOffset = offset;
        }

        /// <summary>
        /// Drops the packet.
        /// </summary>
        public void Drop()
        {
            SetUnsafeMetadata(0);
        }
    }
}
