using System;

/*

Decoders for the BC1 to BC5 block compression formats (also known as DXT1 to DXT5,
ATI1 and ATI2), each of which encodes a block of 4x4 pixels in 8 or 16 bytes.

Copyright 2026+ Dmitry Brant
https://dmitrybrant.com

License: MIT
*/

namespace DmitryBrant.ImageFormats
{
    internal static class BcDecoder
    {
        /// <summary>
        /// Decodes a BC1 color block into 16 pixels, each four bytes in blue, green, red,
        /// alpha order. Within BC2 and BC3 blocks, the color block always has four colors.
        /// </summary>
        public static void DecodeColor(ReadOnlySpan<byte> block, Span<byte> pixels, bool alwaysFourColors)
        {
            int c0 = block[0] | (block[1] << 8);
            int c1 = block[2] | (block[3] << 8);
            bool fourColors = c0 > c1 || alwaysFourColors;

            // The colors are interpolated in floating point, as Microsoft's decoders do.
            Span<byte> colors = stackalloc byte[16];
            for (int i = 0; i < 3; i++)
            {
                int shift = 11 - i * 6 + (i == 2 ? 1 : 0);
                float max = i == 1 ? 63 : 31;
                float a = ((c0 >> shift) & (int)max) * (1 / max);
                float b = ((c1 >> shift) & (int)max) * (1 / max);
                colors[i] = ToByte(a);
                colors[4 + i] = ToByte(b);
                colors[8 + i] = ToByte(fourColors ? a + (1f / 3) * (b - a) : a + 0.5f * (b - a));
                colors[12 + i] = fourColors ? ToByte(a + (2f / 3) * (b - a)) : (byte)0;
            }
            colors[3] = colors[7] = colors[11] = 255;
            colors[15] = fourColors ? (byte)255 : (byte)0;

            uint indices = (uint)(block[4] | (block[5] << 8) | (block[6] << 16) | (block[7] << 24));
            for (int i = 0; i < 16; i++)
            {
                int c = (int)((indices >> (i * 2)) & 3) * 4;
                pixels[i * 4] = colors[c + 2];
                pixels[i * 4 + 1] = colors[c + 1];
                pixels[i * 4 + 2] = colors[c];
                pixels[i * 4 + 3] = colors[c + 3];
            }
        }

        private static byte ToByte(float value)
        {
            return (byte)MathF.Round(Math.Clamp(value, 0, 1) * 255);
        }

        /// <summary>
        /// Decodes the explicit 4-bit alpha values of a BC2 block into every fourth byte of
        /// the given pixels.
        /// </summary>
        public static void DecodeExplicitAlpha(ReadOnlySpan<byte> block, Span<byte> pixels)
        {
            for (int i = 0; i < 16; i++)
            {
                int a = (block[i / 2] >> ((i & 1) * 4)) & 0xF;
                pixels[i * 4 + 3] = (byte)(a * 17);
            }
        }

        /// <summary>
        /// Decodes a single-channel block, as used for the alpha of BC3 and the channels of
        /// BC4 and BC5, into 16 values that are nominally from 0 to 1, or from -1 to 1 if
        /// the block is signed.
        /// </summary>
        public static void DecodeChannel(ReadOnlySpan<byte> block, Span<float> values, bool signed)
        {
            Span<float> palette = stackalloc float[8];
            int e0 = signed ? (sbyte)block[0] : block[0];
            int e1 = signed ? (sbyte)block[1] : block[1];
            // For signed blocks, -128 is treated as -127, so that the range is symmetric.
            float a0 = signed ? Math.Max(e0, -127) / 127f : e0 / 255f;
            float a1 = signed ? Math.Max(e1, -127) / 127f : e1 / 255f;
            palette[0] = a0;
            palette[1] = a1;
            if (e0 > e1)
            {
                for (int i = 1; i < 7; i++)
                    palette[i + 1] = ((7 - i) * a0 + i * a1) / 7;
            }
            else
            {
                for (int i = 1; i < 5; i++)
                    palette[i + 1] = ((5 - i) * a0 + i * a1) / 5;
                palette[6] = signed ? -1 : 0;
                palette[7] = 1;
            }

            ulong indices = 0;
            for (int i = 0; i < 6; i++)
                indices |= (ulong)block[2 + i] << (i * 8);
            for (int i = 0; i < 16; i++)
                values[i] = palette[(int)((indices >> (i * 3)) & 7)];
        }
    }
}
