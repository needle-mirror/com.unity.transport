using System;
using System.Runtime.InteropServices;
using Unity.Collections.LowLevel.Unsafe;

namespace Unity.Networking.Transport
{
    [StructLayout(LayoutKind.Explicit)]
    internal unsafe struct SessionIdToken : IEquatable<SessionIdToken>, IComparable<SessionIdToken>
    {
        public const int k_Length = 8;
        [FieldOffset(0)] public fixed byte Value[k_Length];

        public static bool operator==(SessionIdToken lhs, SessionIdToken rhs)
        {
            return lhs.Compare(rhs) == 0;
        }

        public static bool operator!=(SessionIdToken lhs, SessionIdToken rhs)
        {
            return lhs.Compare(rhs) != 0;
        }

        public bool Equals(SessionIdToken other)
        {
            return Compare(other) == 0;
        }

        public int CompareTo(SessionIdToken other)
        {
            return Compare(other);
        }

        public override bool Equals(object other)
        {
            return other != null && this == (SessionIdToken)other;
        }

        public override int GetHashCode()
        {
            fixed(byte* p = Value)
            unchecked
            {
                var result = 0;

                for (int i = 0; i < k_Length; i++)
                {
                    result = (result * 31) ^ (int)p[i];
                }

                return result;
            }
        }

        int Compare(SessionIdToken other)
        {
            fixed(void* p = Value)
            {
                return UnsafeUtility.MemCmp(p, other.Value, k_Length);
            }
        }
    }
}
