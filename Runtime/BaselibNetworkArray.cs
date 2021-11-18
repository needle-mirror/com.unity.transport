#if UNITY_STANDALONE_WIN || UNITY_GAMECORE || UNITY_XBOXONE || UNITY_EDITOR_WIN
    #define BASELIB_USE_SPLIT_BUFFERS
#else
    #undef BASELIB_USE_SPLIT_BUFFERS
#endif

using System;
using Unity.Baselib.LowLevel;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using ErrorState = Unity.Baselib.LowLevel.Binding.Baselib_ErrorState;
using ErrorCode = Unity.Baselib.LowLevel.Binding.Baselib_ErrorCode;

namespace Unity.Networking.Transport
{
    using size_t = UIntPtr;

    internal unsafe struct UnsafeBaselibNetworkArray : IDisposable
    {
        [NativeDisableUnsafePtrRestriction] UnsafePtrList<Binding.Baselib_RegisteredNetwork_Buffer> m_BufferPool;

        /// <summary>
        /// Initializes a new instance of the UnsafeBaselibNetworkArray struct.
        /// </summary>
        /// <param name="capacity"></param>
        /// <param name="typeSize"></param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown if the capacity is less then 0 or if the value exceeds <see cref="int.MaxValue"/></exception>
        /// <exception cref="Exception">Thrown on internal baselib errors</exception>
        public UnsafeBaselibNetworkArray(int capacity, int typeSize)
        {
            var totalSize = (long)typeSize;
#if !BASELIB_USE_SPLIT_BUFFERS
            totalSize = capacity * typeSize;
#endif

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (typeSize < 0)
                throw new ArgumentOutOfRangeException(nameof(typeSize), "Capacity must be >= 0");

            if (totalSize > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(typeSize), $"Capacity * sizeof(T) cannot exceed {int.MaxValue} bytes");
#endif

            var poolSize = capacity;
#if !BASELIB_USE_SPLIT_BUFFERS
            poolSize = 1;
#endif

            m_BufferPool = new UnsafePtrList<Binding.Baselib_RegisteredNetwork_Buffer>(poolSize, Allocator.Persistent);

            for (int i = 0; i < poolSize; i++)
            {
                var pageInfo = stackalloc Binding.Baselib_Memory_PageSizeInfo[1];
                Binding.Baselib_Memory_GetPageSizeInfo(pageInfo);
                var defaultPageSize = (ulong)pageInfo->defaultPageSize;

                var pageCount = (ulong)1;
                if ((ulong)totalSize > defaultPageSize)
                {
                    pageCount = (ulong)math.ceil(totalSize / (double)defaultPageSize);
                }

                var buffer = (Binding.Baselib_RegisteredNetwork_Buffer*)UnsafeUtility.Malloc(UnsafeUtility.SizeOf<Binding.Baselib_RegisteredNetwork_Buffer>(), UnsafeUtility.AlignOf<Binding.Baselib_RegisteredNetwork_Buffer>(), Allocator.Persistent);

                var error = default(ErrorState);

                var pageAllocation = Binding.Baselib_Memory_AllocatePages(
                    pageInfo->defaultPageSize,
                    pageCount,
                    1,
                    Binding.Baselib_Memory_PageState.ReadWrite,
                    &error);

                if (error.code != ErrorCode.Success)
                {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    throw new Exception("Couldn't allocate baselib memory pages");
#else
                    return;
#endif
                }

                UnsafeUtility.MemSet((void*)pageAllocation.ptr, 0, (long)(pageAllocation.pageCount * pageAllocation.pageSize));
                *buffer = Binding.Baselib_RegisteredNetwork_Buffer_Register(pageAllocation, &error);

                if (error.code != (int)ErrorCode.Success)
                {
                    Binding.Baselib_Memory_ReleasePages(pageAllocation, &error);
                    *buffer = default;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    throw new Exception("Couldn't register baselib network buffer");
#endif
                }

                m_BufferPool.Add(buffer);
            }
        }

        public void Dispose()
        {
            var error = default(ErrorState);
            for (int i = 0; i < m_BufferPool.Length; i++)
            {
                var buffer = m_BufferPool[i];
                var pageAllocation = buffer->allocation;
                Binding.Baselib_RegisteredNetwork_Buffer_Deregister(*buffer);
                Binding.Baselib_Memory_ReleasePages(pageAllocation, &error);
                UnsafeUtility.Free(buffer, Allocator.Persistent);
            }
        }

        /// <summary>
        /// Gets a element at the specified index, with the size of <see cref="elementSize" />.
        /// </summary>
        /// <value>A <see cref="Binding.Baselib_RegisteredNetwork_BufferSlice" /> pointing to the index supplied.</value>
        public Binding.Baselib_RegisteredNetwork_BufferSlice AtIndexAsSlice(int index, uint elementSize)
        {
            uint offset = 0;
            IntPtr data;
            Binding.Baselib_RegisteredNetwork_Buffer* buffer = null;
#if BASELIB_USE_SPLIT_BUFFERS
    #if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (index >= m_BufferPool.Length)
            {
                throw new Exception($"Index out of range when trying to get slice {index}");
            }
    #endif
            buffer = m_BufferPool[index];
            data = (IntPtr)((byte*)buffer->allocation.ptr);
#else // BASELIB_USE_SPLIT_BUFFERS
            buffer = m_BufferPool[0];
            offset = elementSize * (uint)index;
            data = (IntPtr)((byte*)buffer->allocation.ptr + offset);
#endif // BASELIB_USE_SPLIT_BUFFERS

            Binding.Baselib_RegisteredNetwork_BufferSlice slice;
            slice.id = buffer->id;
            slice.data = data;
            slice.offset = offset;
            slice.size = elementSize;
            return slice;
        }
    }
}