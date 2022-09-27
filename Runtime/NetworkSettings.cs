using System;
using System.Diagnostics;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using BurstRuntime = Unity.Burst.BurstRuntime;

namespace Unity.Networking.Transport
{
    /// <summary>
    /// A list of the parameters that describe the network configuration.
    /// </summary>
    public struct NetworkSettings : IDisposable
    {
        private struct ParameterSlice
        {
            public int Offset;
            public int Size;
        }

        private const int k_MapInitialCapacity = 8;

        private NativeHashMap<long, ParameterSlice> m_ParameterOffsets;
        private NativeList<byte> m_Parameters;
        private byte m_Initialized;

        /// <summary>If the settings have been created (e.g. not disposed).</summary>
        public bool IsCreated => m_Initialized == 0 || m_Parameters.IsCreated;

        private bool EnsureInitializedOrError()
        {
            if (m_Initialized == 0)
            {
                m_Initialized = 1;
                m_Parameters = new NativeList<byte>(Allocator.Temp);
                m_ParameterOffsets = new NativeHashMap<long, ParameterSlice>(k_MapInitialCapacity, Allocator.Temp);
            }

            if (!m_Parameters.IsCreated)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                throw new ObjectDisposedException(null, $"The {nameof(NetworkSettings)} has been deallocated, it is not allowed to access it.");
#else
                UnityEngine.Debug.LogError($"The {nameof(NetworkSettings)} has been deallocated, it is not allowed to access it.");
                return false;
#endif
            }
            return true;
        }

        /// <summary>
        /// Creates a new NetworkSettings object using the provided allocator.
        /// If no Allocator is provided, Allocator.Temp will be used.
        /// </summary>
        /// <param name="allocator">
        /// The allocator used for the parameters list.
        /// When Allocator.Temp is used, the settings are valid to use only during one frame.
        /// </param>
        public NetworkSettings(Allocator allocator)
        {
            m_Initialized = 1;
            m_Parameters = new NativeList<byte>(allocator);
            m_ParameterOffsets = new NativeHashMap<long, ParameterSlice>(k_MapInitialCapacity, allocator);
        }

        public void Dispose()
        {
            // Disposed state is m_Initialized == 1 && m_Parameters.IsCreated == false
            m_Initialized = 1;

            if (m_Parameters.IsCreated)
            {
                m_Parameters.Dispose();
                m_ParameterOffsets.Dispose();
            }
        }

        /// <summary>
        /// Adds a new parameter to the list. There must be only one instance per parameter type.
        /// </summary>
        /// <typeparam name="T">The type of INetworkParameter to add.</typeparam>
        /// <param name="parameter">The parameter to add.</param>
        /// <exception cref="ArgumentException">Throws an argument exception if the paramter type is already in the list or if it contains invalid values.</exception>
        public unsafe void AddRawParameterStruct<T>(ref T parameter) where T : unmanaged, INetworkParameter
        {
            if (!EnsureInitializedOrError())
                return;

            ValidateParameterOrError(ref parameter);

            var typeHash = BurstRuntime.GetHashCode64<T>();
            var parameterSlice = new ParameterSlice
            {
                Offset = m_Parameters.Length,
                Size = UnsafeUtility.SizeOf<T>(),
            };

            if (m_ParameterOffsets.TryAdd(typeHash, parameterSlice))
            {
                m_Parameters.Resize(m_Parameters.Length + parameterSlice.Size, NativeArrayOptions.UninitializedMemory);
            }
            else
            {
                parameterSlice = m_ParameterOffsets[typeHash];
            }

            var valuePtr = (T*)((byte*)m_Parameters.GetUnsafePtr<byte>() + parameterSlice.Offset);
            *valuePtr = parameter;
        }

        /// <summary>
        /// Try to get the parameter values for the specified type.
        /// </summary>
        /// <typeparam name="T">The type of the parameters to get.</typeparam>
        /// <param name="parameter">The stored parameter values.</param>
        /// <returns>Returns true if the parameter is in the paramaters list.</returns>
        public unsafe bool TryGet<T>(out T parameter) where T : unmanaged, INetworkParameter
        {
            parameter = default;

            if (!EnsureInitializedOrError())
                return false;

            var typeHash = BurstRuntime.GetHashCode64<T>();

            if (m_ParameterOffsets.TryGetValue(typeHash, out var parameterSlice))
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (UnsafeUtility.SizeOf<T>() != parameterSlice.Size)
                    throw new ArgumentException($"The size of the parameter type {typeof(T)} ({UnsafeUtility.SizeOf<T>()}) is different to the stored size ({parameterSlice.Size})");

                if (m_Parameters.Length < parameterSlice.Offset + parameterSlice.Size)
                    throw new OverflowException($"The registered parameter slice is out of bounds of the parameters buffer");
#endif
                parameter = *(T*)((byte*)m_Parameters.GetUnsafeReadOnlyPtr<byte>() + parameterSlice.Offset);
                return true;
            }

            return false;
        }

        internal static void ValidateParameterOrError<T>(ref T parameter) where T : INetworkParameter
        {
            if (!parameter.Validate())
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                throw new ArgumentException($"The provided network parameter ({parameter.GetType().Name}) is not valid");
#else
                UnityEngine.Debug.LogError($"The provided network parameter ({parameter.GetType().Name}) is not valid");
#endif
            }
        }

        // TODO: Remove when INetworkParameter[] constructors are deprecated on NetworkDriver
        #region LEGACY

#if !UNITY_DOTSRUNTIME
        internal static unsafe NetworkSettings FromArray(params INetworkParameter[] parameters)
        {
            var networkParameters = new NetworkSettings(Allocator.Temp);

            for (int i = 0; i < parameters.Length; i++)
            {
                var parameter = parameters[i];
                var type = parameter.GetType();

                // LEGACY transformations: Some parameters were previously assuming that
                // the default 0 values converted to the actual default value.
#if !UNITY_WEBGL
                if (type == typeof(BaselibNetworkParameter))
                {
                    var p = (BaselibNetworkParameter)parameter;

                    if (p.receiveQueueCapacity == 0)
                        p.receiveQueueCapacity = BaselibNetworkParameterExtensions.k_defaultRxQueueSize;

                    if (p.sendQueueCapacity == 0)
                        p.sendQueueCapacity = BaselibNetworkParameterExtensions.k_defaultTxQueueSize;

                    if (p.maximumPayloadSize == 0)
                        p.maximumPayloadSize = BaselibNetworkParameterExtensions.k_defaultMaximumPayloadSize;

                    parameter = p;
                }
#endif
                if (type == typeof(Relay.RelayNetworkParameter))
                {
                    var p = (Relay.RelayNetworkParameter)parameter;

                    if (p.RelayConnectionTimeMS == 0)
                        p.RelayConnectionTimeMS = Relay.RelayNetworkParameter.k_DefaultConnectionTimeMS;

                    parameter = p;
                }
#if ENABLE_MANAGED_UNITYTLS
                else if (type == typeof(Unity.Networking.Transport.TLS.SecureNetworkProtocolParameter))
                {
                    var p = (Unity.Networking.Transport.TLS.SecureNetworkProtocolParameter)parameter;

                    if (p.SSLHandshakeTimeoutMin == 0)
                        p.SSLHandshakeTimeoutMin = Unity.Networking.Transport.TLS.SecureNetworkProtocol.DefaultParameters.SSLHandshakeTimeoutMin;

                    if (p.SSLHandshakeTimeoutMax == 0)
                        p.SSLHandshakeTimeoutMax = Unity.Networking.Transport.TLS.SecureNetworkProtocol.DefaultParameters.SSLHandshakeTimeoutMax;

                    parameter = p;
                }
#endif

                try
                {
                    ValidateParameterOrError(ref parameter);
                }
                catch (Exception e)
                {
                    networkParameters.Dispose();
                    throw e;
                }

                var typeHash = BurstRuntime.GetHashCode64(type);
                var parameterSlice = new ParameterSlice
                {
                    Offset = networkParameters.m_Parameters.Length,
                    Size = UnsafeUtility.SizeOf(type),
                };

                networkParameters.m_ParameterOffsets.Add(typeHash, parameterSlice);
                networkParameters.m_Parameters.Resize(networkParameters.m_Parameters.Length + parameterSlice.Size, NativeArrayOptions.UninitializedMemory);

                var valuePtr = ((byte*)networkParameters.m_Parameters.GetUnsafePtr<byte>() + parameterSlice.Offset);
                var parameterPtr = (byte*)UnsafeUtility.PinGCObjectAndGetAddress(parameter, out var gcHandle) + ObjectHeaderOffset;

                UnsafeUtility.MemCpy(valuePtr, parameterPtr, parameterSlice.Size);
                UnsafeUtility.ReleaseGCObject(gcHandle);
            }

            return networkParameters;
        }

        internal unsafe bool TryGet(Type parameterType, out INetworkParameter parameter)
        {
            parameter = default;

            if (!m_Parameters.IsCreated)
                return false;

            var typeHash = BurstRuntime.GetHashCode64(parameterType);

            if (m_ParameterOffsets.TryGetValue(typeHash, out var parameterSlice))
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (UnsafeUtility.SizeOf(parameterType) != parameterSlice.Size)
                    throw new ArgumentException($"The size of the parameter type {parameterType} ({UnsafeUtility.SizeOf(parameterType)}) is different to the stored size ({parameterSlice.Size})");

                if (m_Parameters.Length < parameterSlice.Offset + parameterSlice.Size)
                    throw new OverflowException($"The registered parameter slice is out of bounds of the parameters buffer");
#endif
                parameter = Activator.CreateInstance(parameterType) as INetworkParameter;
                var parameterPtr = (byte*)UnsafeUtility.PinGCObjectAndGetAddress(parameter, out var gcHandle) + ObjectHeaderOffset;
                UnsafeUtility.MemCpy(parameterPtr, (byte*)m_Parameters.GetUnsafeReadOnlyPtr<byte>() + parameterSlice.Offset, parameterSlice.Size);
                UnsafeUtility.ReleaseGCObject(gcHandle);
                return true;
            }

            return false;
        }

        internal static int ObjectHeaderOffset => UnsafeUtility.SizeOf<ObjectOffsetType>();

        private unsafe struct ObjectOffsetType
        {
            void* v0;
        #if !ENABLE_CORECLR
            void* v1;
        #endif
        }
#endif // !UNITY_DOTSRUNTIME

        #endregion
    }
}
