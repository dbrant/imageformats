#nullable enable
using System;
using System.Collections.Generic;
using System.IO;

/*

The "JG lossless" entropy codec used throughout AOL ART (Johnson-Grace) files.

Every compressed byte stream in an ART file goes through this codec: the
wavelet coefficient planes of a photo image, and (with a different match-offset
table) the pixel data of a palettized graphic image. It's a Deflate-like LZ77 +
canonical Huffman coder with three Johnson-Grace peculiarities:

  * The Huffman alphabet is 256 literals followed by 11*16 match symbols (11
    length slots x 16 position slots). How many of those 176 match symbols
    actually exist is derived from the window size in the block header, so a
    block with a window of 0 is a plain order-0 Huffman coder with no matches.
  * The code lengths are themselves coded with a 9-symbol "pre-tree" whose own
    code lengths are read with an adaptive Kraft-budget scheme, i.e. no explicit
    lengths are stored at all.
  * Bits are read MSB-first.

Copyright 2026+ Dmitry Brant
https://dmitrybrant.com

License: MIT
*/

namespace DmitryBrant.ImageFormats
{
    /// <summary>
    /// MSB-first bit reader. Reading past the end of the buffer yields zero bits, which
    /// lets a truncated stream decode as far as it can instead of throwing.
    /// </summary>
    internal class ArtBitReader
    {
        private readonly byte[] data;
        private int bitPos;

        public ArtBitReader(byte[] data, int bytePos)
        {
            this.data = data;
            bitPos = bytePos * 8;
        }

        public int BitPos { get { return bitPos; } set { bitPos = value; } }

        public int BytePos { get { return (bitPos + 7) >> 3; } }

        public int Peek(int count)
        {
            int v = 0;
            for (int i = 0; i < count; i++)
            {
                int bit = bitPos + i;
                int index = bit >> 3;
                int b = index < data.Length ? data[index] : 0;
                v = (v << 1) | ((b >> (7 - (bit & 7))) & 1);
            }
            return v;
        }

        public void Skip(int count)
        {
            bitPos += count;
        }

        public int Read(int count)
        {
            int v = Peek(count);
            bitPos += count;
            return v;
        }
    }

    /// <summary>
    /// Canonical Huffman decoder, MSB-first, built from an array of code lengths.
    /// </summary>
    internal class ArtHuffman
    {
        private const int MaxCodeLength = 16;

        private readonly int[] counts = new int[MaxCodeLength + 1];
        private readonly int[] symbols;
        private readonly int[] firstCode = new int[MaxCodeLength + 2];
        private readonly int[] firstIndex = new int[MaxCodeLength + 2];

        public ArtHuffman(int[] lengths)
        {
            foreach (int len in lengths)
            {
                if (len > 0 && len <= MaxCodeLength)
                    counts[len]++;
            }

            var offsets = new int[MaxCodeLength + 2];
            for (int i = 1; i <= MaxCodeLength; i++)
                offsets[i + 1] = offsets[i] + counts[i];

            symbols = new int[offsets[MaxCodeLength + 1]];
            var next = (int[])offsets.Clone();
            for (int s = 0; s < lengths.Length; s++)
            {
                int len = lengths[s];
                if (len > 0 && len <= MaxCodeLength)
                    symbols[next[len]++] = s;
            }

            int code = 0, index = 0;
            for (int len = 1; len <= MaxCodeLength; len++)
            {
                firstCode[len] = code;
                firstIndex[len] = index;
                code = (code + counts[len]) << 1;
                index += counts[len];
            }
        }

        public int Decode(ArtBitReader reader)
        {
            int code = 0;
            for (int len = 1; len <= MaxCodeLength; len++)
            {
                code = (code << 1) | reader.Read(1);
                int count = counts[len];
                if (count != 0 && code >= firstCode[len] && code - firstCode[len] < count)
                    return symbols[firstIndex[len] + code - firstCode[len]];
            }
            throw new ImageDecodeException("Invalid Huffman code in ART stream.");
        }
    }

    /// <summary>
    /// The 65-entry match-offset table that a position code indexes into, plus the chain
    /// that position codes above 64 walk. For the plain byte-stream codec the table is
    /// simply -(i+1) and the chain is unused.
    /// </summary>
    internal class ArtOffsetTable
    {
        public int[] Offsets;
        public int[]? ChainStart;
        public int[]? ChainLength;

        public ArtOffsetTable(int[] offsets)
        {
            Offsets = offsets;
        }
    }

    internal static class ArtJgLossless
    {
        public const int TableSize = 0x41;

        // Window[i] is the maximum match distance; -1 means "stored".
        private static readonly int[] Window = { -1, 0, 17, 65, 257, 1025, 4097, 8192 };

        // Prefix code for the pre-tree's own code lengths, indexed by the next 4 bits.
        // (.rdata 0x22330)
        private static readonly int[] PretreeBits = { 2, 2, 2, 2, 2, 2, 2, 2, 3, 3, 4, 4, 4, 4, 4, 4 };
        private static readonly int[] PretreeValue = { 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 3, 4, 5, 6, 7, 8 };

        // Base for the "repeat run" operator, indexed by the current repeat count:
        // base[n] = 2^n + 2.  (.rdata 0x22350)
        private static readonly int[] RunBase = { 0, 4, 6, 10, 18, 34, 66, 130, 258 };

        // Match length: slots 0..6 are literal lengths, 7..9 add extra bits, and slot 10
        // escapes to a 2-bit selector with its own base / extra-bit pair.
        private static readonly int[] LenExtra = { 0, 0, 0, 0, 0, 0, 0, 1, 2, 3 };
        private static readonly int[] LenBase = { 0, 1, 2, 3, 4, 5, 6, 7, 9, 13 };
        private static readonly int[] Len10Extra = { 6, 9, 12, 15 };
        private static readonly int[] Len10Base = { 21, 85, 597, 4693 };

        // Match position: slots 0..4 select one of the first five entries of the offset
        // table directly; 5..15 add (slot - 3) extra bits to a base.
        private static readonly int[] PosBase = { 0, 1, 2, 3, 4, 5, 9, 17, 33, 65, 129, 257, 513, 1025, 2049, 4097 };

        // Sanity limit on the uncompressed size of a single block.
        private const int MaxBlockSize = 64 * 1024 * 1024;

        /// <summary>
        /// The offset table of the plain byte-stream codec, where a position code always
        /// means "distance = code + 1".
        /// </summary>
        public static readonly ArtOffsetTable FlatOffsets = MakeFlatOffsets();

        private static ArtOffsetTable MakeFlatOffsets()
        {
            var offsets = new int[TableSize];
            for (int i = 0; i < TableSize; i++)
                offsets[i] = -(i + 1);
            return new ArtOffsetTable(offsets);
        }

        /// <summary>
        /// Index of the first Window entry that is at least as large as the given size.
        /// </summary>
        private static int WindowIndex(int size)
        {
            if (size < 0)
                return 0;
            int i = 0;
            while (true)
            {
                i++;
                if (i > 7)
                    return i;
                if (Window[i] >= size)
                    return i;
            }
        }

        /// <summary>
        /// How many position slots exist for each of the 11 length slots. All zero when the
        /// window is 0, which turns the coder into a plain order-0 Huffman coder.
        /// </summary>
        public static int[] SlotCounts(int minMatchLen, int budget, int limitOther, int limitShort, int window)
        {
            var counts = new int[11];
            int cur = 0;
            for (int i = 0; i < 11; i++)
            {
                int extent = i < 8 ? i : (4 << (i - 8)) + 5;
                if (i < 2)
                {
                    cur = 0;
                    if (window != 0)
                    {
                        int limit = extent + minMatchLen == 2 ? limitShort : limitOther;
                        while (true)
                        {
                            int v = cur < 6 ? cur + 1 : (8 << (cur - 6)) + 2;
                            if (v > limit)
                                break;
                            cur++;
                            if (cur >= 0x10)
                                break;
                        }
                    }
                }
                if (budget < extent + minMatchLen)
                    cur = 0;
                counts[i] = cur;
            }
            return counts;
        }

        /// <summary>
        /// The slot counts that the plain byte-stream codec uses, i.e. with an effectively
        /// unlimited budget and limits taken from the window itself.
        /// </summary>
        private static int[] MatchSlotCounts(int window, int minMatchLen)
        {
            return SlotCounts(minMatchLen, minMatchLen + 0x9254, window, Math.Min(window, 0x81), window);
        }

        /// <summary>
        /// Reads the 9 pre-tree code lengths, which are not stored explicitly: each one is
        /// an index into the lengths that still have codes available in the Kraft budget.
        /// </summary>
        private static int[] ReadPretree(ArtBitReader reader)
        {
            var available = new[] { 2, 4, 8, 16, 32, 64, 128, 256 };
            var lengths = new int[9];
            for (int sym = 0; sym < 9; sym++)
            {
                int v = 0;
                if (available[7] != 0 && sym < 8)
                {
                    int nibble = reader.Peek(4);
                    reader.Skip(PretreeBits[nibble]);
                    v = PretreeValue[nibble];
                }

                int count = 0, len = 0;
                for (int i = 0; i < 8; i++)
                {
                    if (available[i] == 0)
                        continue;
                    if (count == v)
                    {
                        len = i + 1;
                        break;
                    }
                    count++;
                }

                lengths[sym] = len;
                if (len == 0)
                    continue;

                available[len - 1]--;
                // Propagate the borrow to shorter lengths.
                int j = len - 1;
                while (j >= 0 && (available[j] & 1) != 0)
                {
                    j--;
                    if (j < 0)
                        break;
                    available[j]--;
                }
                for (int i = len; i < 8; i++)
                    available[i] -= 2 << (i - len);
            }
            return lengths;
        }

        /// <summary>
        /// Decodes the given number of Huffman code lengths using the 9-operator pre-tree.
        /// </summary>
        public static int[] ReadCodeLengths(ArtBitReader reader, int count)
        {
            var pretree = new ArtHuffman(ReadPretree(reader));
            var result = new List<int>(count + 16);
            int prev = 8;
            int runCount = 0;

            while (result.Count < count)
            {
                int op = pretree.Decode(reader);
                if (op == 0)
                {
                    // Repeat the previous length once.
                    result.Add(prev);
                    runCount++;
                }
                else if (op >= 1 && op <= 5)
                {
                    // Delta from the previous length.
                    int d = op == 5 ? reader.Read(2) + 5 : op;
                    if (reader.Read(1) != 0)
                        d = 16 - d;
                    prev = ((d + prev - 1) & 0xF) + 1;
                    result.Add(prev);
                    runCount = 0;
                }
                else if (op == 6)
                {
                    // Run of the previous length.
                    int n;
                    if (runCount != 0)
                    {
                        if (runCount > 8)
                            throw new ImageDecodeException("Invalid run in ART code lengths.");
                        n = reader.Read(runCount) + RunBase[runCount];
                    }
                    else
                    {
                        n = 3;
                    }
                    for (int i = 0; i < n; i++)
                        result.Add(prev);
                    runCount = 0;
                }
                else if (op == 7)
                {
                    result.Add(0);
                    runCount = 0;
                }
                else if (op == 8)
                {
                    // Run of zeros, gamma-coded.
                    int k = 0;
                    while (k < 8 && reader.Peek(1) != 0)
                    {
                        reader.Skip(1);
                        k++;
                    }
                    if (k < 8)
                        reader.Skip(1); // terminating zero
                    int n = k != 0 ? reader.Read(k) + 1 + (1 << k) : 2;
                    for (int i = 0; i < n; i++)
                        result.Add(0);
                    runCount = 0;
                }
                else
                {
                    throw new ImageDecodeException("Invalid ART pre-tree operator " + op + ".");
                }
            }

            var lengths = new int[count];
            result.CopyTo(0, lengths, 0, count);
            return lengths;
        }

        /// <summary>
        /// Assembles the 433-slot code-length array from the lengths as they appear in the
        /// stream: the literals first, then the match symbols starting at slot 256.
        /// </summary>
        public static int[] AssembleLengths(int[] lengths, int literalCount, int[] slotCounts)
        {
            var full = new int[433];
            for (int i = 0; i < literalCount && i < lengths.Length; i++)
                full[i] = lengths[i];
            int k = literalCount;
            for (int slot = 0; slot < slotCounts.Length; slot++)
            {
                for (int j = 0; j < slotCounts[slot]; j++)
                {
                    if (k < lengths.Length)
                        full[256 + 16 * slot + j] = lengths[k];
                    k++;
                }
            }
            return full;
        }

        /// <summary>
        /// Turns a match symbol's payload (symbol - 256) into a length and a negative
        /// offset back into the output buffer.
        /// </summary>
        private static void DecodeMatch(ArtBitReader reader, int m, int minMatchLen, ArtOffsetTable table,
            out int length, out int offset)
        {
            int slot = m >> 4;
            if (slot < 7)
                length = slot;
            else if (slot < 10)
                length = reader.Read(LenExtra[slot]) + LenBase[slot];
            else
            {
                int i = reader.Read(2);
                length = reader.Read(Len10Extra[i]) + Len10Base[i];
            }
            length += minMatchLen;

            int posSlot = m & 0xF;
            int code = posSlot <= 4 ? posSlot : reader.Read(posSlot - 3) + PosBase[posSlot];
            if (code < TableSize)
            {
                offset = table.Offsets[code];
                return;
            }
            if (table.ChainStart == null || table.ChainLength == null)
            {
                offset = -(code + 1);
                return;
            }
            // Codes above 64 walk the chain of distance ranges that the rows above didn't
            // already cover.
            int d = code - 0x40;
            int index = 0;
            while (true)
            {
                d += table.ChainLength[index];
                index++;
                if (index >= table.ChainStart.Length || table.ChainStart[index] > d)
                    break;
            }
            offset = -d;
        }

        /// <summary>
        /// Decodes the given number of bytes into buf[start .. start+count). The buffer may
        /// already hold history that matches can reach back into.
        /// </summary>
        public static void LzDecode(ArtBitReader reader, ArtHuffman huffman, byte[] buf, int start, int count,
            int minMatchLen, ArtOffsetTable table)
        {
            int p = start;
            int end = start + count;
            while (p < end)
            {
                int sym = huffman.Decode(reader);
                if (sym < 256)
                {
                    buf[p++] = (byte)sym;
                    continue;
                }
                DecodeMatch(reader, sym - 256, minMatchLen, table, out int length, out int offset);
                if (p + offset < 0)
                    throw new ImageDecodeException("ART match reaches before the start of the buffer.");
                if (p + length > end)
                    throw new ImageDecodeException("ART match overruns the end of the block.");
                // Byte at a time, so that overlapping matches work.
                for (int i = 0; i < length; i++, p++)
                    buf[p] = buf[p + offset];
            }
        }

        /// <summary>
        /// Decodes one block, and reports the position just past it.
        /// </summary>
        private static byte[] DecodeBlock(byte[] data, int pos, out int nextPos)
        {
            int b0 = data[pos];
            int size, window, headerLength;

            if (b0 <= 100)
            {
                nextPos = pos + 1 + b0;
                return Slice(data, pos + 1, b0);
            }
            if (b0 < 0xE0)
            {
                size = b0 - 0x5E;
                int index = WindowIndex(b0 - 0x5F);
                window = index < 8 ? Window[index] : 8192;
                headerLength = 1;
            }
            else
            {
                int n = (b0 & 3) + 1;
                size = 0;
                for (int i = 0; i < n; i++)
                {
                    int index = pos + 1 + i;
                    size |= (index < data.Length ? data[index] : 0) << (8 * i);
                }
                window = Window[(b0 >> 2) & 7];
                headerLength = 1 + n;
            }

            if (window < 0)
            {
                nextPos = pos + headerLength + size;
                return Slice(data, pos + headerLength, size);
            }
            if (size < 0 || size > MaxBlockSize)
                throw new ImageDecodeException("ART block declares an implausible size.");

            int minMatchLen = window >= 0x2000 ? 2 : window >= 0x1000 ? 3 : 4;
            int[] slotCounts = MatchSlotCounts(window, minMatchLen);
            int numLengths = 256;
            foreach (int c in slotCounts)
                numLengths += c;

            var reader = new ArtBitReader(data, pos + headerLength);
            int[] lengths = ReadCodeLengths(reader, numLengths);
            var huffman = new ArtHuffman(AssembleLengths(lengths, 256, slotCounts));

            var output = new byte[size];
            LzDecode(reader, huffman, output, 0, size, minMatchLen, FlatOffsets);
            nextPos = reader.BytePos;
            return output;
        }

        /// <summary>
        /// Decodes a whole stream, which is one or more blocks.
        /// </summary>
        public static byte[] Decode(byte[] data)
        {
            using (var output = new MemoryStream())
            {
                int pos = 0;
                while (pos < data.Length)
                {
                    byte[] block = DecodeBlock(data, pos, out pos);
                    output.Write(block, 0, block.Length);
                    if (block.Length == 0)
                        break;
                }
                return output.ToArray();
            }
        }

        private static byte[] Slice(byte[] data, int pos, int count)
        {
            if (pos >= data.Length || count <= 0)
                return Array.Empty<byte>();
            int n = Math.Min(count, data.Length - pos);
            var result = new byte[n];
            Array.Copy(data, pos, result, 0, n);
            return result;
        }
    }
}
