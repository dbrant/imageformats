/*

Decoder for HAM-E images stored inside ILBM files.

HAM-E (Black Belt Systems, 1990) was a hardware video adapter for the Amiga that produced
256-color or true-color display from an ordinary 4-bitplane hires display. Each hires pixel
contributes four "digital" bits (the most significant bit of each of R, G, B, plus the least
significant bit of the 4-bit blue component, called "I" here), and every two consecutive
hires pixels are combined into a single 8-bit HAM-E pixel. So a 640-pixel-wide ILBM image
becomes a 320-pixel-wide HAM-E image.

The palette is not stored in the CMAP chunk, but in special lines of the image itself
(usually at the top, hidden in the overscan area), which start with a magic sequence.
Each such line sets 64 palette entries, so four of them are needed for all 256 colors.
The magic sequence is followed by a mode byte, which selects either the 256-color palette
mode, or the HAM-E mode. In HAM-E mode, pixel values 0-59 select a palette color, values
60-63 select one of four banks of 60 colors for the rest of the line, and higher values
modify one of the R, G, or B components of the previous pixel (with 6 bits of precision),
similar to the Amiga's own HAM mode.

Copyright 2026+ Dmitry Brant
https://dmitrybrant.com

License: MIT
*/

namespace DmitryBrant.ImageFormats
{
    internal static class HameDecoder
    {
        private static readonly byte[] Magic = { 0xA2, 0xF5, 0x84, 0xDC, 0x6D, 0xB0, 0x7F };
        private const int ModeRegister = 0x14;
        private const int ModeHame = 0x18;

        private const int ColorsPerLine = 64;
        private const int PaletteStart = 8;

        /// <summary>
        /// Checks whether the given decoded ILBM image is a HAM-E image, by looking for the
        /// HAM-E palette line at the top of the image.
        /// </summary>
        /// <param name="lines">Palette indices of each pixel in the image, line by line.</param>
        /// <param name="width">Width of the image.</param>
        /// <param name="cmap">Raw contents of the CMAP chunk (8-bit R, G, B triplets).</param>
        public static bool IsHame(uint[][] lines, int width, byte[] cmap)
        {
            if (lines.Length < 1 || cmap == null || width / 2 < PaletteStart + ColorsPerLine * 3)
            {
                return false;
            }
            return GetMode(lines[0], MakeNibbleTable(cmap)) != 0;
        }

        /// <summary>
        /// Decodes a HAM-E image into BGRA pixel data. The resulting image is half as wide as
        /// the source image. Palette lines are left blank.
        /// </summary>
        /// <param name="lines">Palette indices of each pixel in the image, line by line.</param>
        /// <param name="width">Width of the image.</param>
        /// <param name="cmap">Raw contents of the CMAP chunk (8-bit R, G, B triplets).</param>
        /// <param name="interlaced">Whether the image is interlaced, in which case each field has its own palette.</param>
        /// <param name="outWidth">Receives the width of the decoded image.</param>
        public static byte[] Decode(uint[][] lines, int width, byte[] cmap, bool interlaced, out int outWidth)
        {
            var nibbleTable = MakeNibbleTable(cmap);
            outWidth = width / 2;
            int height = lines.Length;
            var bmpData = new byte[outWidth * 4 * height];
            var values = new int[outWidth];

            // A palette of 256 colors (R, G, B) for each field, and where the next palette line will go.
            var palettes = new byte[2][] { new byte[256 * 3], new byte[256 * 3] };
            var paletteFill = new int[2];
            bool modeHame = false;

            for (int y = 0; y < height; y++)
            {
                GetValues(lines[y], nibbleTable, values);
                int field = interlaced ? y & 1 : 0;
                byte[] palette = palettes[field];

                int mode = GetMode(lines[y], nibbleTable);
                if (mode != 0)
                {
                    for (int i = 0; i < ColorsPerLine * 3; i++)
                    {
                        palette[paletteFill[field] * 3 + i] = (byte)values[PaletteStart + i];
                    }
                    paletteFill[field] = (paletteFill[field] + ColorsPerLine) % 256;
                    modeHame = mode == ModeHame;

                    // Leave the palette line blank (but opaque).
                    for (int x = 0; x < outWidth; x++)
                    {
                        bmpData[4 * (y * outWidth + x) + 3] = 0xFF;
                    }
                    continue;
                }

                int r = 0, g = 0, b = 0;
                int bank = 0;
                for (int x = 0; x < outWidth; x++)
                {
                    int value = values[x];
                    if (!modeHame)
                    {
                        r = palette[value * 3];
                        g = palette[value * 3 + 1];
                        b = palette[value * 3 + 2];
                    }
                    else
                    {
                        int component = (value & 0x3F) << 2;
                        switch (value >> 6)
                        {
                            case 0:
                                if (value < 60)
                                {
                                    int index = bank + value;
                                    r = palette[index * 3];
                                    g = palette[index * 3 + 1];
                                    b = palette[index * 3 + 2];
                                }
                                else
                                {
                                    bank = (value - 60) * ColorsPerLine;
                                }
                                break;
                            case 1:
                                b = component;
                                break;
                            case 2:
                                r = component;
                                break;
                            default:
                                g = component;
                                break;
                        }
                    }

                    int ptr = 4 * (y * outWidth + x);
                    bmpData[ptr] = (byte)b;
                    bmpData[ptr + 1] = (byte)g;
                    bmpData[ptr + 2] = (byte)r;
                    bmpData[ptr + 3] = 0xFF;
                }
            }
            return bmpData;
        }

        /// <summary>
        /// Returns the mode byte, if the given line is a palette line, or 0 otherwise.
        /// </summary>
        private static int GetMode(uint[] line, int[] nibbleTable)
        {
            for (int i = 0; i < Magic.Length; i++)
            {
                if (GetValue(line, nibbleTable, i) != Magic[i])
                {
                    return 0;
                }
            }
            int mode = GetValue(line, nibbleTable, Magic.Length);
            return mode == ModeRegister || mode == ModeHame ? mode : 0;
        }

        private static void GetValues(uint[] line, int[] nibbleTable, int[] values)
        {
            for (int x = 0; x < values.Length; x++)
            {
                values[x] = GetValue(line, nibbleTable, x);
            }
        }

        /// <summary>
        /// Gets the 8-bit value made up of the two hires pixels at the given (HAM-E) position.
        /// </summary>
        private static int GetValue(uint[] line, int[] nibbleTable, int x)
        {
            return (GetNibble(line[x * 2], nibbleTable) << 4) | GetNibble(line[x * 2 + 1], nibbleTable);
        }

        private static int GetNibble(uint index, int[] nibbleTable)
        {
            return index < nibbleTable.Length ? nibbleTable[index] : 0;
        }

        /// <summary>
        /// Makes a table that maps each palette index to its digital bits, as RGBI.
        /// </summary>
        private static int[] MakeNibbleTable(byte[] cmap)
        {
            var table = new int[cmap.Length / 3];
            for (int c = 0; c < table.Length; c++)
            {
                int red = cmap[c * 3], green = cmap[c * 3 + 1], blue = cmap[c * 3 + 2];
                table[c] = (red >> 7) << 3 | (green >> 7) << 2 | (blue >> 7) << 1 | ((blue >> 4) & 1);
            }
            return table;
        }
    }
}
