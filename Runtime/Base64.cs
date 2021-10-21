using System;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Assertions;

namespace Unity.Networking.Transport
{
    /// <summary>
    /// Utility class used to Decode a base64 string
    /// </summary>
    internal static class Base64
    {
        /// <summary>
        /// Decode characters representing a Base64 encoding into bytes.
        /// </summary>
        /// <param name="startInputPtr">Pointer to first input char in UTF16 string (C# strings)</param>
        /// <param name="inputLength">Number of input chars</param>
        /// <param name="startDestPtr">Pointer to location for teh first result byte</param>
        /// <param name="destLength">Max length of the preallocated result buffer</param>
        /// <returns>
        /// Actually written bytes to startDestPtr that is less or equal than destLength. -1 on error.
        /// </returns>
        private static unsafe int FromBase64_Decode_UTF16(byte* startInputPtr, int inputLength, byte* startDestPtr, int destLength)
        {
            if (inputLength == 0)
                return 0;

            const int sizeCharUTF16 = 2;

            // 3 bytes == 4 chars in the input base64 string
            if (inputLength % 4 != 0)
            {
                Debug.LogError("Base64 string's length must be multiple of 4");
                return -1;
            }

            if (destLength < inputLength / 4 * 3 - 2)
            {
                Debug.LogError("Dest array is too small");
                return -1;
            }

            var originalStartDestPtr = startDestPtr;

            var n = inputLength / 4;

            const string table = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

            var lookup = stackalloc byte[256];
            UnsafeUtility.MemSet(lookup, 0xFF, 256);
            for (byte i = 0; i < table.Length; i++)
                lookup[table[i]] = i;
            lookup['='] = 0;

            // skip last 4 chars
            for (var i = 0; i < n - 1; i++)
            {
                byte a = lookup[startInputPtr[0 * sizeCharUTF16]];
                byte b = lookup[startInputPtr[1 * sizeCharUTF16]];
                byte c = lookup[startInputPtr[2 * sizeCharUTF16]];
                byte d = lookup[startInputPtr[3 * sizeCharUTF16]];

                if (a == 0xFF || b == 0xFF || c == 0xFF || d == 0xFF)
                {
                    Debug.LogError("Invalid Base64 symbol");
                    return -1;
                }

                *startDestPtr++ = (byte)((a << 2) | (b >> 4));
                *startDestPtr++ = (byte)((b << 4) | (c >> 2));
                *startDestPtr++ = (byte)((c << 6) | d);

                startInputPtr += 4 * sizeCharUTF16;
            }

            // last 4 chars
            var cc = startInputPtr[2 * sizeCharUTF16];
            var dd = startInputPtr[3 * sizeCharUTF16];

            var la = lookup[startInputPtr[0 * sizeCharUTF16]];
            var lb = lookup[startInputPtr[1 * sizeCharUTF16]];
            var lc = lookup[cc];
            var ld = lookup[dd];

            if (la == 0xFF || lb == 0xFF || lc == 0xFF || ld == 0xFF)
            {
                Debug.LogError("Invalid Base64 symbol");
                return -1;
            }

            *startDestPtr++ = (byte)((la << 2) | (lb >> 4));

            if (cc != '=') // == means 4 chars == 1 byte, we already wrote that
            {
                if (dd == '=') // = means 4 chars == 2 bytes, 1 more
                {
                    if (destLength < inputLength / 4 * 3 - 1)
                    {
                        Debug.LogError("Dest array is too small");
                        return -1;
                    }

                    *startDestPtr++ = (byte)((lb << 4) | (lc >> 2));
                }
                else // no padding, 4 chars == 3 bytes, 2 more
                {
                    if (destLength < inputLength / 4 * 3)
                    {
                        Debug.LogError("Dest array is too small");
                        return -1;
                    }

                    *startDestPtr++ = (byte)((lb << 4) | (lc >> 2));
                    *startDestPtr++ = (byte)((lc << 6) | ld);
                }
            }

            return (int)(startDestPtr - originalStartDestPtr);
        }

        /// <summary>
        /// Decodes base64 string and writes binary data into dest
        /// </summary>
        /// <param name="base64">Input base64 string to decode</param>
        /// <param name="dest">Decoded base64 will be written here</param>
        /// <param name="destMaxLength">Max length that dest can handle.</param>
        /// <returns>
        /// Actual length of data that was written to dest. Less or equal than destLength.
        /// On error, will throw if ENABLE_UNITY_COLLECTIONS_CHECK is defined, or return -1 otherwise.
        /// </returns>
        public static unsafe int FromBase64String(string base64, byte* dest, int destMaxLength)
        {
            var result = 0;
            fixed(char* ptr = base64)
            {
                result = FromBase64_Decode_UTF16((byte*)ptr, base64.Length, dest, destMaxLength);
            }

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (result == -1)
            {
                throw new ArgumentException("Invalid base64 string or too short destination array");
            }
#endif
            return result;
        }
    }
}
