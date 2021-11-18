namespace Unity.Networking.Transport.Tests
{
    public static class CommonUtilites
    {
        public unsafe static void FillBuffer(byte* bufferPtr, int size)
        {
            for (int i = 0; i < size; i++)
            {
                bufferPtr[i] = (byte)(i % 256);
            }
        }

        public unsafe static bool CheckBuffer(byte* bufferPtr, int size)
        {
            for (int i = 0; i < size; i++)
            {
                if (bufferPtr[i] != (byte)(i % 256))
                    return false;
            }

            return true;
        }
    }
}
