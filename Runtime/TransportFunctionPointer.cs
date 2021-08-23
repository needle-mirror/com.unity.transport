using System;
using System.Runtime.InteropServices;
using Unity.Burst;

namespace Unity.Networking.Transport
{
    public struct TransportFunctionPointer<T> where T : Delegate
    {
        public TransportFunctionPointer(T executeDelegate)
        {
            Ptr = BurstCompiler.CompileFunctionPointer(executeDelegate);
        }

        public TransportFunctionPointer(FunctionPointer<T> Pointer)
        {
            Ptr = Pointer;
        }

        public static TransportFunctionPointer<T> Burst(T burstCompilableDelegate)
        {
            return new TransportFunctionPointer<T>(BurstCompiler.CompileFunctionPointer(burstCompilableDelegate));
        }

        public static TransportFunctionPointer<T> Managed(T managedDelegate)
        {
            GCHandle.Alloc(managedDelegate); // Ensure delegate is never garbage-collected.
            return new TransportFunctionPointer<T>(new FunctionPointer<T>(Marshal.GetFunctionPointerForDelegate(managedDelegate)));
        }

        public readonly FunctionPointer<T> Ptr;
    }
}
