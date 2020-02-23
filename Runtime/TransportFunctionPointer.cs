using System;
using Unity.Burst;

namespace Unity.Networking.Transport
{
    public struct TransportFunctionPointer<T> where T : Delegate
    {
        public TransportFunctionPointer(T executeDelegate)
        {
#if !UNITY_DOTSPLAYER
            Ptr = BurstCompiler.CompileFunctionPointer(executeDelegate);
#else
            Ptr = executeDelegate;
#endif
        }

#if !UNITY_DOTSPLAYER
        internal readonly FunctionPointer<T> Ptr;
#else
        internal readonly T Ptr;
#endif
    }
}