using System;
using System.Runtime.InteropServices;
using Unity.Burst;

namespace Unity.Networking.Transport
{
    /// <summary>
    /// Represents a wrapper around burst compatible function pointers in a portable way
    /// </summary>
    public struct TransportFunctionPointer<T> where T : Delegate
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="TransportFunctionPointer"/> class
        /// </summary>
        /// <param name="executeDelegate">The execute delegate</param>
        public TransportFunctionPointer(T executeDelegate)
        {
            Ptr = BurstCompiler.CompileFunctionPointer(executeDelegate);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="TransportFunctionPointer"/> class
        /// </summary>
        /// <param name="Pointer">The pointer</param>
        public TransportFunctionPointer(FunctionPointer<T> Pointer)
        {
            Ptr = Pointer;
        }

        /// <summary>
        /// returns a wrapped Burst compiled function pointer
        /// </summary>
        /// <param name="burstCompilableDelegate">The burst compilable delegate</param>
        /// <returns>A transport function pointer of t</returns>
        public static TransportFunctionPointer<T> Burst(T burstCompilableDelegate)
        {
            return new TransportFunctionPointer<T>(BurstCompiler.CompileFunctionPointer(burstCompilableDelegate));
        }

        /// <summary>
        /// Returns a wrapped managed function pointer 
        /// </summary>
        /// <param name="managedDelegate">The managed delegate</param>
        /// <returns>A transport function pointer of t</returns>
        public static TransportFunctionPointer<T> Managed(T managedDelegate)
        {
            GCHandle.Alloc(managedDelegate); // Ensure delegate is never garbage-collected.
            return new TransportFunctionPointer<T>(new FunctionPointer<T>(Marshal.GetFunctionPointerForDelegate(managedDelegate)));
        }

        /// <summary>
        /// Returns Burst <see cref="FunctionPointer{T}"/>
        /// </summary>
        public readonly FunctionPointer<T> Ptr;
    }
}
