using System;

namespace ViveUltimateTrackerStandalone.Runtime.Scripts.Protocol
{
    /// <summary>
    /// IEEE754 半精度変換。純粋関数。
    /// </summary>
    public static class ParseHelper
    {
        public static float HalfToSingle(ushort half)
        {
            int sign = (half >> 15) & 0x1;
            int exp = (half >> 10) & 0x1F;
            int mant = half & 0x3FF;
            if (exp == 0)
            {
                if (mant == 0) return sign == 0 ? 0f : -0f;
                float sub = (mant / 1024f) * (float)Math.Pow(2, -14);
                return sign == 0 ? sub : -sub;
            }
            if (exp == 0x1F)
            {
                if (mant == 0) return sign == 0 ? float.PositiveInfinity : float.NegativeInfinity;
                return float.NaN;
            }
            float val = (1 + mant / 1024f) * (float)Math.Pow(2, exp - 15);
            return sign == 0 ? val : -val;
        }
        
        

        // ヘルパ
        public static string HexSlice(byte[] src, int start, int count)
        {
            if (src == null || start >= src.Length || count <= 0) return string.Empty;
            int safeCount = Math.Min(count, src.Length - start);
            char[] chars = new char[safeCount * 3];
            int ci = 0;
            for (int i = 0; i < safeCount; i++)
            {
                byte b = src[start + i];
                chars[ci++] = GetHexNibble(b >> 4);
                chars[ci++] = GetHexNibble(b & 0xF);
                chars[ci++] = ' ';
            }
            return new string(chars, 0, Math.Max(0, ci - 1));
        }
        public static char GetHexNibble(int v) => (char)(v < 10 ? ('0' + v) : ('A' + (v - 10)));
    }
}
