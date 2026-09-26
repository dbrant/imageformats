using System;

/*

Decoder for DCTV images stored inside ILBM files.

DCTV (Digital Creations, 1990) was a hardware video adapter for the Amiga that produced
true-color composite video from an ordinary 3- or 4-bitplane display. The bitplane data
does not represent palette colors in the usual sense. Instead, each pixel contributes a
few "digital" bits (the most significant bit of each of R, G, B, plus the least
significant bit of the 4-bit blue component, called "I" here), which the DCTV hardware
combines with those of the neighboring pixel into an 8-bit sample of a composite video
signal. This signal is then demodulated into luma and chroma, much like a TV would do.

The first scanline (or two scanlines, if interlaced) of the image contains a signature:
a pseudo-random sequence in the I bits, which the hardware uses to detect the presence
of a DCTV image. These lines are not part of the actual picture.

Copyright 2026+ Dmitry Brant
https://dmitrybrant.com

License: MIT
*/

namespace DmitryBrant.ImageFormats
{
    internal static class DctvDecoder
    {
        private const int SignatureLength = 256;

        // The demodulated chroma is scaled by this much, relative to standard YUV.
        private const double ChromaGain = 1.595;

        /// <summary>
        /// Checks whether the given decoded ILBM image is a DCTV image, by looking for
        /// the DCTV signature in its first line.
        /// </summary>
        /// <param name="lines">Palette indices of each pixel in the image, line by line.</param>
        /// <param name="width">Width of the image.</param>
        /// <param name="cmap">Raw contents of the CMAP chunk (8-bit R, G, B triplets).</param>
        public static bool IsDctv(uint[][] lines, int width, byte[] cmap)
        {
            if (lines.Length < 3 || width < SignatureLength || cmap == null)
            {
                return false;
            }
            return HasSignature(lines[0], MakeSampleTable(cmap));
        }

        /// <summary>
        /// Decodes a DCTV image into BGRA pixel data. The resulting image is shorter than the
        /// source image, since the signature line(s) at the top are removed.
        /// </summary>
        /// <param name="lines">Palette indices of each pixel in the image, line by line.</param>
        /// <param name="width">Width of the image.</param>
        /// <param name="cmap">Raw contents of the CMAP chunk (8-bit R, G, B triplets).</param>
        /// <param name="outHeight">Receives the height of the decoded image.</param>
        public static byte[] Decode(uint[][] lines, int width, byte[] cmap, out int outHeight)
        {
            var sampleTable = MakeSampleTable(cmap);

            // Interlaced images have the signature repeated in the first line of each field.
            bool interlaced = HasSignature(lines[1], sampleTable);
            int fieldShift = interlaced ? 1 : 0;
            int firstLine = interlaced ? 2 : 1;
            outHeight = lines.Length - firstLine;

            var bmpData = new byte[width * 4 * outHeight];
            var samples = new int[width];

            // Chroma from the previous line (of the same field), indexed by column.
            var chromaBuffer = new int[width + 2];

            for (int y = 0; y < outHeight; y++)
            {
                uint[] line = lines[y + firstLine];
                for (int x = 0; x < width; x++)
                {
                    samples[x] = line[x] < sampleTable.Length ? sampleTable[line[x]] : 0;
                }

                // The color subcarrier phase alternates from one line to the next, which
                // shifts which pair of pixels makes up each sample. Each line carries only one
                // of the two chroma components, alternating as well, so the other component
                // comes from the previous line.
                int phase = (y >> fieldShift) & 1;
                int field = y & fieldShift;
                bool havePrevLine = y > fieldShift;

                int prev1 = 0, prev2 = 0;
                byte r = 0, g = 0, b = 0;

                for (int x = 0; x < width; x++)
                {
                    if ((x & 1) == phase)
                    {
                        int sample = x + 1 < width ? (samples[x] << 1) | samples[x + 1] : 0;

                        // Luma is the average of consecutive samples, which cancels out the subcarrier.
                        int luma = Clamp(((prev1 + sample) / 2 - 64) * 8 / 5);

                        // Chroma is what remains after the luma is removed (a second difference),
                        // with its sign flipping along with the subcarrier.
                        int chroma = (sample + prev2 - 2 * prev1) / 4;
                        if (((x + 1) & 2) == 0)
                        {
                            chroma = -chroma;
                        }

                        int col = (x & ~1) | field;
                        int prevChroma = havePrevLine ? chromaBuffer[col] : 0;
                        chromaBuffer[col] = chroma;

                        int u, v;
                        if (phase == 0)
                        {
                            u = prevChroma;
                            v = chroma;
                        }
                        else
                        {
                            u = chroma;
                            v = prevChroma;
                        }

                        prev2 = prev1;
                        prev1 = sample;

                        // Standard YUV to RGB conversion.
                        r = (byte)Clamp((int)Math.Round(luma + ChromaGain * 1.140 * v));
                        g = (byte)Clamp((int)Math.Round(luma - ChromaGain * (0.395 * u + 0.581 * v)));
                        b = (byte)Clamp((int)Math.Round(luma + ChromaGain * 2.032 * u));
                    }

                    int ptr = 4 * (y * width + x);
                    bmpData[ptr] = b;
                    bmpData[ptr + 1] = g;
                    bmpData[ptr + 2] = r;
                    bmpData[ptr + 3] = 0xFF;
                }
            }
            return bmpData;
        }

        /// <summary>
        /// Checks whether the given line contains the DCTV signature: a pseudo-random sequence
        /// (from a linear feedback shift register) encoded in the I bits of the first 256 pixels.
        /// </summary>
        private static bool HasSignature(uint[] line, int[] sampleTable)
        {
            if (line.Length < SignatureLength || IBit(line[0], sampleTable) != 0)
            {
                return false;
            }
            int lfsr = 0x7D;
            for (int x = 1; x < SignatureLength; x++)
            {
                if (IBit(line[x], sampleTable) == (lfsr & 1))
                {
                    return false;
                }
                if ((lfsr & 1) != 0)
                {
                    lfsr ^= 0x186;
                }
                lfsr >>= 1;
            }
            return true;
        }

        private static int IBit(uint index, int[] sampleTable)
        {
            return index < sampleTable.Length ? (sampleTable[index] >> 6) & 1 : 0;
        }

        /// <summary>
        /// Makes a table that maps each palette index to its digital bits, laid out as 0I0R0B0G,
        /// so that two neighboring pixels can be interleaved into a single 8-bit sample.
        /// </summary>
        private static int[] MakeSampleTable(byte[] cmap)
        {
            var table = new int[cmap.Length / 3];
            for (int c = 0; c < table.Length; c++)
            {
                int red = cmap[c * 3], green = cmap[c * 3 + 1], blue = cmap[c * 3 + 2];
                table[c] = ((blue >> 4) & 1) << 6 | (red >> 7) << 4 | (blue >> 7) << 2 | (green >> 7);
            }
            return table;
        }

        private static int Clamp(int value)
        {
            return value < 0 ? 0 : value > 255 ? 255 : value;
        }
    }
}
