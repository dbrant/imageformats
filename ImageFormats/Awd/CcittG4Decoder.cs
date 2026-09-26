#nullable enable
using System;
using System.Collections.Generic;

/*

Decoder for CCITT Group 4 (ITU-T T.6, a.k.a. MMR) compressed bilevel images, as
used by fax machines and, among other places, Microsoft Fax (.AWD) documents.

Group 4 is a purely two-dimensional code: every line is coded relative to the line
above it (the "reference line"), with an imaginary all-white line above the first
one. The encoder walks along the line from one "changing element" (a pixel whose
color differs from the one before it) to the next, and for each one emits either
a vertical mode code, meaning the change is within three pixels of the matching
change on the reference line; a pass mode code, meaning a whole run on the
reference line has no counterpart here; or a horizontal mode code followed by two
run lengths, coded with the same modified Huffman tables as Group 3. The image
ends with an EOFB code, which is two EOL codes in a row.

Copyright 2026+ Dmitry Brant
https://dmitrybrant.com

License: MIT
*/

namespace DmitryBrant.ImageFormats
{
    internal static class CcittG4Decoder
    {
        private const int ModePass = 100;
        private const int ModeHorizontal = 101;
        private const int ModeEndOfLine = 102;

        // Terminating and makeup codes for white runs, as run length followed by code.
        private static readonly string[] WhiteCodes =
        {
            "0", "00110101", "1", "000111", "2", "0111", "3", "1000", "4", "1011", "5", "1100",
            "6", "1110", "7", "1111", "8", "10011", "9", "10100", "10", "00111", "11", "01000",
            "12", "001000", "13", "000011", "14", "110100", "15", "110101", "16", "101010",
            "17", "101011", "18", "0100111", "19", "0001100", "20", "0001000", "21", "0010111",
            "22", "0000011", "23", "0000100", "24", "0101000", "25", "0101011", "26", "0010011",
            "27", "0100100", "28", "0011000", "29", "00000010", "30", "00000011", "31", "00011010",
            "32", "00011011", "33", "00010010", "34", "00010011", "35", "00010100", "36", "00010101",
            "37", "00010110", "38", "00010111", "39", "00101000", "40", "00101001", "41", "00101010",
            "42", "00101011", "43", "00101100", "44", "00101101", "45", "00000100", "46", "00000101",
            "47", "00001010", "48", "00001011", "49", "01010010", "50", "01010011", "51", "01010100",
            "52", "01010101", "53", "00100100", "54", "00100101", "55", "01011000", "56", "01011001",
            "57", "01011010", "58", "01011011", "59", "01001010", "60", "01001011", "61", "00110010",
            "62", "00110011", "63", "00110100",
            "64", "11011", "128", "10010", "192", "010111", "256", "0110111", "320", "00110110",
            "384", "00110111", "448", "01100100", "512", "01100101", "576", "01101000",
            "640", "01100111", "704", "011001100", "768", "011001101", "832", "011010010",
            "896", "011010011", "960", "011010100", "1024", "011010101", "1088", "011010110",
            "1152", "011010111", "1216", "011011000", "1280", "011011001", "1344", "011011010",
            "1408", "011011011", "1472", "010011000", "1536", "010011001", "1600", "010011010",
            "1664", "011000", "1728", "010011011",
        };

        // Terminating and makeup codes for black runs, as run length followed by code.
        private static readonly string[] BlackCodes =
        {
            "0", "0000110111", "1", "010", "2", "11", "3", "10", "4", "011", "5", "0011",
            "6", "0010", "7", "00011", "8", "000101", "9", "000100", "10", "0000100",
            "11", "0000101", "12", "0000111", "13", "00000100", "14", "00000111",
            "15", "000011000", "16", "0000010111", "17", "0000011000", "18", "0000001000",
            "19", "00001100111", "20", "00001101000", "21", "00001101100", "22", "00000110111",
            "23", "00000101000", "24", "00000010111", "25", "00000011000", "26", "000011001010",
            "27", "000011001011", "28", "000011001100", "29", "000011001101", "30", "000001101000",
            "31", "000001101001", "32", "000001101010", "33", "000001101011", "34", "000011010010",
            "35", "000011010011", "36", "000011010100", "37", "000011010101", "38", "000011010110",
            "39", "000011010111", "40", "000001101100", "41", "000001101101", "42", "000011011010",
            "43", "000011011011", "44", "000001010100", "45", "000001010101", "46", "000001010110",
            "47", "000001010111", "48", "000001100100", "49", "000001100101", "50", "000001010010",
            "51", "000001010011", "52", "000000100100", "53", "000000110111", "54", "000000111000",
            "55", "000000100111", "56", "000000101000", "57", "000001011000", "58", "000001011001",
            "59", "000000101011", "60", "000000101100", "61", "000001011010", "62", "000001100110",
            "63", "000001100111",
            "64", "0000001111", "128", "000011001000", "192", "000011001001", "256", "000001011011",
            "320", "000000110011", "384", "000000110100", "448", "000000110101",
            "512", "0000001101100", "576", "0000001101101", "640", "0000001001010",
            "704", "0000001001011", "768", "0000001001100", "832", "0000001001101",
            "896", "0000001110010", "960", "0000001110011", "1024", "0000001110100",
            "1088", "0000001110101", "1152", "0000001110110", "1216", "0000001110111",
            "1280", "0000001010010", "1344", "0000001010011", "1408", "0000001010100",
            "1472", "0000001010101", "1536", "0000001011010", "1600", "0000001011011",
            "1664", "0000001100100", "1728", "0000001100101",
        };

        // Extended makeup codes, shared by white and black runs.
        private static readonly string[] ExtendedCodes =
        {
            "1792", "00000001000", "1856", "00000001100", "1920", "00000001101",
            "1984", "000000010010", "2048", "000000010011", "2112", "000000010100",
            "2176", "000000010101", "2240", "000000010110", "2304", "000000010111",
            "2368", "000000011100", "2432", "000000011101", "2496", "000000011110",
            "2560", "000000011111",
        };

        private const int MaxCodeLength = 13;

        // Keyed by (code length << 16) | code.
        private static readonly Dictionary<int, int> WhiteTable = BuildTable(WhiteCodes);
        private static readonly Dictionary<int, int> BlackTable = BuildTable(BlackCodes);

        private static Dictionary<int, int> BuildTable(string[] codes)
        {
            var table = new Dictionary<int, int>();
            foreach (var list in new[] { codes, ExtendedCodes })
            {
                for (int i = 0; i < list.Length; i += 2)
                {
                    string code = list[i + 1];
                    table.Add((code.Length << 16) | Convert.ToInt32(code, 2), int.Parse(list[i]));
                }
            }
            return table;
        }

        /// <summary>
        /// Decodes a Group 4 image.
        /// </summary>
        /// <param name="data">Buffer containing the compressed data.</param>
        /// <param name="offset">Offset of the compressed data in the buffer.</param>
        /// <param name="length">Length of the compressed data.</param>
        /// <param name="width">Width of the image, in pixels.</param>
        /// <param name="height">Height of the image, in pixels.</param>
        /// <param name="lsbFirst">Whether the bits of each byte are in least-significant-first
        /// order (TIFF FillOrder 2), which is the order that fax hardware produces.</param>
        /// <returns>Array of width * height pixels, row by row, with 1 for black and 0 for
        /// white. If the data ends early, the remaining rows are left white.</returns>
        public static byte[] Decode(byte[] data, int offset, int length, int width, int height, bool lsbFirst)
        {
            var pixels = new byte[width * height];
            var bits = new BitReader(data, offset, length, lsbFirst);

            // Positions of the changing elements on the reference and coding lines. Changes
            // at even indices are from white to black, and at odd indices from black to white.
            // The reference line is terminated by sentinels at the right edge, enough of them
            // that b2 can always be read even when b1 is the last one.
            var refLine = new int[width + 5];
            var curLine = new int[width + 5];
            refLine[0] = refLine[1] = refLine[2] = width;

            try
            {
                for (int y = 0; y < height; y++)
                {
                    int rowStart = y * width;
                    int curCount = 0;
                    int a0 = -1;
                    bool black = false;
                    int refIndex = 0;

                    while (a0 < width)
                    {
                        // b1 is the first change on the reference line to the right of a0 whose
                        // color is opposite to the current color, and b2 is the change after it.
                        // Since a0 only ever moves right, so does the first change past it.
                        while (refLine[refIndex] <= a0 && refLine[refIndex] < width)
                            refIndex++;
                        int b1Index = refIndex;
                        if ((b1Index & 1) != (black ? 1 : 0))
                            b1Index++;
                        int b1 = refLine[b1Index];
                        int b2 = refLine[b1Index + 1];

                        int mode = ReadMode(bits);
                        if (mode == ModeEndOfLine)
                            return pixels;

                        int start = Math.Max(a0, 0);
                        if (mode == ModePass)
                        {
                            if (black)
                                Fill(pixels, rowStart, start, b2, width);
                            a0 = b2;
                        }
                        else if (mode == ModeHorizontal)
                        {
                            int run1 = ReadRun(bits, black);
                            int run2 = ReadRun(bits, !black);
                            int a1 = Math.Min(start + run1, width);
                            int a2 = Math.Min(a1 + run2, width);
                            Fill(pixels, rowStart, black ? start : a1, black ? a1 : a2, width);
                            curLine[curCount++] = a1;
                            curLine[curCount++] = a2;
                            a0 = a2;
                        }
                        else
                        {
                            // Vertical mode: mode holds the offset of a1 from b1.
                            int a1 = Math.Max(Math.Min(b1 + mode, width), start);
                            if (black)
                                Fill(pixels, rowStart, start, a1, width);
                            curLine[curCount++] = a1;
                            a0 = a1;
                            black = !black;
                        }

                        if (curCount > width)
                            throw new ImageDecodeException("Too many changing elements in a CCITT line.");
                    }

                    // Changes at the right edge aren't real changes, so drop them before this
                    // line becomes the reference line, and then add the sentinels.
                    while (curCount > 0 && curLine[curCount - 1] >= width)
                        curCount--;
                    curLine[curCount] = curLine[curCount + 1] = curLine[curCount + 2] = width;
                    (refLine, curLine) = (curLine, refLine);
                }
            }
            catch (Exception e) when (e is IndexOutOfRangeException || e is ImageDecodeException)
            {
                // Return what we have so far, in case of corrupt or truncated data.
                Util.log("Error while decoding CCITT data: " + e.Message);
            }
            return pixels;
        }

        private static void Fill(byte[] pixels, int rowStart, int from, int to, int width)
        {
            to = Math.Min(to, width);
            for (int x = Math.Max(from, 0); x < to; x++)
                pixels[rowStart + x] = 1;
        }

        /// <summary>
        /// Reads a two-dimensional mode code. Returns the vertical offset (-3 to 3) for a
        /// vertical mode code, or one of the Mode constants.
        /// </summary>
        private static int ReadMode(BitReader bits)
        {
            if (bits.Read() == 1) return 0;                             // 1        V0
            if (bits.Read() == 1) return bits.Read() == 1 ? 1 : -1;     // 01x      VR1, VL1
            if (bits.Read() == 1) return ModeHorizontal;                // 001      H
            if (bits.Read() == 1) return ModePass;                      // 0001     P
            if (bits.Read() == 1) return bits.Read() == 1 ? 2 : -2;     // 00001x   VR2, VL2
            if (bits.Read() == 1) return bits.Read() == 1 ? 3 : -3;     // 000001x  VR3, VL3
            if (bits.Read() == 1)                                       // 0000001  extension
                throw new ImageDecodeException("CCITT extension modes are not supported.");

            // Otherwise this should be an EOL (eleven zeros and a one), which in Group 4
            // only appears as part of the EOFB code at the end of the image.
            for (int i = 7; i < 11; i++)
            {
                if (bits.Read() != 0)
                    throw new ImageDecodeException("Invalid CCITT mode code.");
            }
            while (bits.Read() == 0) { }
            return ModeEndOfLine;
        }

        /// <summary>
        /// Reads a complete run length: any number of makeup codes followed by a
        /// terminating code.
        /// </summary>
        private static int ReadRun(BitReader bits, bool black)
        {
            var table = black ? BlackTable : WhiteTable;
            int total = 0;
            while (true)
            {
                int code = 0, length = 0, run = -1;
                while (length < MaxCodeLength)
                {
                    code = (code << 1) | bits.Read();
                    length++;
                    if (table.TryGetValue((length << 16) | code, out run))
                        break;
                }
                if (run < 0)
                    throw new ImageDecodeException("Invalid CCITT run length code.");
                total += run;
                if (run < 64)
                    return total;
            }
        }

        private class BitReader
        {
            private readonly byte[] data;
            private readonly int end;
            private readonly bool lsbFirst;
            private int pos;
            private int bit = 8;
            private int current;

            public BitReader(byte[] data, int offset, int length, bool lsbFirst)
            {
                this.data = data;
                pos = offset;
                end = Math.Min(offset + length, data.Length);
                this.lsbFirst = lsbFirst;
            }

            public int Read()
            {
                if (bit == 8)
                {
                    if (pos >= end)
                        throw new ImageDecodeException("Unexpected end of CCITT data.");
                    current = data[pos++];
                    bit = 0;
                }
                int value = lsbFirst ? (current >> bit) & 1 : (current >> (7 - bit)) & 1;
                bit++;
                return value;
            }
        }
    }
}
