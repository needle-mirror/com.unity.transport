using System;
using System.Runtime.InteropServices;
using Unity.Burst;

namespace Unity.Networking.Transport
{
    /// <summary>
    /// Convenience wrapper around a Burst function pointer. Should only be used when defining
    /// functions for custom <see cref="INetworkPipelineStage"/> implementations.
    /// </summary>
    /// <typeparam name="T">Type of the delegate.</typeparam>
    public struct TransportFunctionPointer<T> where T : Delegate
    {
        /// <summary>
        /// Construct a wrapped function pointer from a delegate.
        /// </summary>
        /// <param name="executeDelegate">Delegate to wrap.</param>
        public TransportFunctionPointer(T executeDelegate)
        {
            Ptr = BurstCompiler.CompileFunctionPointer(executeDelegate);
        }

        /// <summary>
        /// Construct a wrapped function pointer from a Burst function pointer.
        /// </summary>
        /// <param name="pointer">Burst function pointer to wrap.</param>
        public TransportFunctionPointer(FunctionPointer<T> pointer)
        {
            Ptr = pointer;
        }

        /// <summary>Wrap a Burst-compilable delegate into a function pointer.</summary>
        /// <param name="burstCompilableDelegate">Delegate to wrap.</param>
        /// <returns>Wrapped function pointer.</returns>
        public static TransportFunctionPointer<T> Burst(T burstCompilableDelegate)
        {
            return new TransportFunctionPointer<T>(BurstCompiler.CompileFunctionPointer(burstCompilableDelegate));
        }

        /// <summary>Wrap a managed delegate into a function pointer.</summary>
        /// <param name="managedDelegate">Managed delegate to wrap.</param>
        /// <returns>Wrapped function pointer.</returns>
        public static TransportFunctionPointer<T> Managed(T managedDelegate)
        {
            GCHandle.Alloc(managedDelegate); // Ensure delegate is never garbage-collected.
            return new TransportFunctionPointer<T>(new FunctionPointer<T>(Marshal.GetFunctionPointerForDelegate(managedDelegate)));
        }

        /// <summary>The actual Burst function pointer being wrapped.</summary>
        public readonly FunctionPointer<T> Ptr;
    }
}
