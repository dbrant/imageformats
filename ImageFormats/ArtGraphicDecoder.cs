#nullable enable
using System;
using System.Collections.Generic;

/*

Decoder for the "graphic" (palettized) image inside an AOL ART (Johnson-Grace)
file: header chunk 0x16, palette chunk 0x17, entropy tables 0x18, pixel data
0x19, and an optional transparency key in chunk 0x1B.

The pixel data uses the same LZ77 + canonical Huffman engine as the photo path
(see ArtJgLossless), but with two twists: the match-offset table holds
two-dimensional image offsets (the pixel above, above-left, two rows up, and so
on) instead of plain byte distances, and rows may carry PNG-style prediction
filters. The palette has a coder of its own, a per-channel delta code with its
own little Huffman tree.

Copyright 2026 Dmitry Brant
http://dmitrybrant.com

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

   http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.

*/

namespace DmitryBrant.ImageFormats
{
    internal static class ArtGraphicDecoder
    {
        // Bit in the palette flags byte that puts channel c into "raw" mode.
        private static readonly int[] PaletteRawBit = { 4, 2, 1 };

        // Pixel formats indexed by the header's "kind" nibble: bytes per pixel, and a class
        // where 2 means palette indices, 1 a synthetic grayscale ramp and 0 direct color.
        private static readonly int[] PixelFormatBytes = { 1, 1, 1, 1, 3, 1 };
        private static readonly int[] PixelFormatClass = { 2, 1, 1, 2, 0, 0 };

        // (dy, dx) neighborhood seeds for the match-offset table. (.rdata 0x22100)
        private static readonly int[,] SeedOffsets = {
            { -1, 0 }, { 0, -1 }, { -2, 0 }, { -1, -1 }, { -1, 1 }, { -2, -1 }, { -3, 0 }, { -1, -2 },
            { -2, 1 }, { -1, 2 }, { -1, -3 }, { 0, -3 }, { 0, -2 }, { -2, -2 }, { -2, 2 }, { -3, -1 },
            { -3, 1 }, { -3, -2 }, { -3, 2 }, { -1, 3 }, { -2, -3 }, { -2, 3 }, { -3, -3 }, { -3, 3 },
        };
        private const int NumSeeds = 24;

        // The tree that codes an adaptive row filter type: "0" -> 4, "10" -> 3, "11" -> 2.
        // (.rdata 0x22130)
        private static readonly int[] FilterTree = { 4, 0, 3, 2 };

        private const int MaxDimension = 32767;
        private const int MaxPixels = 64 * 1024 * 1024;

        /// <summary>
        /// The fields of a graphic image header (chunk 0x16).
        /// </summary>
        public class Header
        {
            public int Flags;
            public int Kind;
            public int Width;
            public int Height;
            public int MinMatchLen;
            public int Budget;
            public int LimitOther;      // f9
            public int LimitShort;      // f8
            public int Window;
            public int FilterMode;
            public int BytesPerPixel;
            public int PixelClass;
            public bool ThreeChannel;
            public int RowLength;

            /// <remarks>
            /// p[0..1]   : flags; bits 8..11 hold the "kind" that selects the pixel format.
            /// p[2..3]   : height, little-endian.
            /// p[4..5]   : width, little-endian.
            /// p[6..7]   : a packed word whose meaning depends on the kind; for kinds 4 and
            ///             above its low byte is the row filter mode.
            /// p[8]      : low nibble + 2 is the minimum match length; the high nibble gives
            ///             the entropy coder's position-slot budget.
            /// p[9]      : the two match-position limits, one per nibble.
            /// p[10..11] : the LZ window.
            /// </remarks>
            public Header(byte[] p)
            {
                if (p.Length < 12)
                    throw new ImageDecodeException("ART graphic header is too short.");

                Flags = p[0] | (p[1] << 8);
                Height = p[2] | (p[3] << 8);
                Width = p[4] | (p[5] << 8);
                int q = p[6] | (p[7] << 8);
                int r = p[10] | (p[11] << 8);
                Kind = (Flags >> 8) & 0xF;

                int f10 = r, f11 = 0, f12 = 0, f13 = 0;
                if (Kind >= 4)
                {
                    if (Kind == 5)
                    {
                        f12 = q >> 8;
                        f11 = r >> 8;
                        f10 = r & 0xFF;
                    }
                    f13 = q & 0xFF;
                }

                MinMatchLen = (p[8] & 0xF) + 2;
                int hi = p[8] >> 4;
                int v = hi < 7 ? hi : hi < 10 ? (4 << (hi - 7)) + 4 : 0x9254;
                Budget = MinMatchLen + v;
                LimitOther = SlotLimit(p[9] >> 4);
                LimitShort = SlotLimit(p[9] & 0xF);

                if (Kind >= PixelFormatBytes.Length)
                    throw new ImageDecodeException("Unsupported ART graphic kind " + Kind + ".");
                BytesPerPixel = PixelFormatBytes[Kind];
                PixelClass = PixelFormatClass[Kind];
                ThreeChannel = (Flags & 0xF00) == 0x500;
                RowLength = ThreeChannel ? Width * 3 : Width;
                Window = ThreeChannel ? f10 | f11 | f12 : f10;
                FilterMode = f13;
            }

            private static int SlotLimit(int u)
            {
                return (u <= 4 ? u : 8 << (u - 5)) + 1;
            }
        }

        /// <summary>
        /// Decodes a palettized graphic image into a top-down BGRA buffer.
        /// </summary>
        public static ArtImage Decode(byte[] data)
        {
            Header? header = null;
            RowDecoder? rows = null;
            int paletteCount = 0;
            byte[] palette = Array.Empty<byte>();
            int transparentKind = 0;
            int keyB = 0, keyG = 0, keyR = 0, keyIndex = 0;

            foreach (var chunk in ArtChunkReader.Walk(data))
            {
                try
                {
                    switch (chunk.Tag)
                    {
                        case ArtChunkReader.TagGraphicHeader:
                        case ArtChunkReader.TagGraphicHeader2:
                            if (header != null)
                                break;      // a second plane (a mask); not needed for the image
                            header = new Header(chunk.Payload);
                            if (header.Width < 1 || header.Height < 1
                                || header.Width > MaxDimension || header.Height > MaxDimension
                                || (long)header.Width * header.Height > MaxPixels)
                                throw new ImageDecodeException("This ART file appears to have invalid dimensions.");
                            rows = new RowDecoder(header);
                            break;

                        case ArtChunkReader.TagPalette:
                        case ArtChunkReader.TagPalette2:
                            if (palette.Length == 0)
                                palette = DecodePalette(chunk.Payload, out paletteCount);
                            break;

                        case ArtChunkReader.TagGraphicTables:
                        case ArtChunkReader.TagGraphicTables2:
                            if (header != null && rows != null)
                                rows.SetTables(chunk.Payload, paletteCount);
                            break;

                        case ArtChunkReader.TagGraphicData:
                        case ArtChunkReader.TagGraphicData2:
                            rows?.FeedRows(chunk.Payload);
                            break;

                        case ArtChunkReader.TagTransparency:
                        case ArtChunkReader.TagTransparency2:
                            ParseTransparency(chunk.Payload, ref transparentKind, ref keyB, ref keyG, ref keyR, ref keyIndex);
                            break;
                    }
                }
                catch (Exception e)
                {
                    // Render whatever arrived before the damage.
                    Util.log("Error while parsing ART graphic chunk: " + e.Message);
                    break;
                }
                if (chunk.Tag == ArtChunkReader.TagEndOfStream)
                    break;
            }

            if (header == null || rows == null)
                throw new ImageDecodeException("This ART file does not contain a graphic image.");

            var image = new ArtImage
            {
                Width = header.Width,
                Height = header.Height,
                Bgra = new byte[header.Width * header.Height * 4]
            };

            if (header.ThreeChannel)
            {
                // Direct color: the row buffer already holds three bytes per pixel.
                int pos = 0;
                for (int y = 0; y < header.Height; y++)
                {
                    int src = y * header.RowLength;
                    for (int x = 0; x < header.Width; x++)
                    {
                        image.Bgra[pos++] = rows.Rows[src + 2];
                        image.Bgra[pos++] = rows.Rows[src + 1];
                        image.Bgra[pos++] = rows.Rows[src];
                        image.Bgra[pos++] = 0xFF;
                        src += 3;
                    }
                }
                return image;
            }

            if (header.PixelClass != 2)
                throw new ImageDecodeException("Unsupported ART graphic pixel class " + header.PixelClass
                    + " (kind " + header.Kind + ").");

            int transparentIndex = ResolveTransparentIndex(transparentKind, keyB, keyG, keyR, keyIndex,
                paletteCount, palette);
            bool anyTransparent = false;

            int outPos = 0;
            for (int y = 0; y < header.Height; y++)
            {
                int src = y * header.RowLength;
                for (int x = 0; x < header.Width; x++)
                {
                    int index = rows.Rows[src + x];
                    // A palette triple is stored as (G, B, R).
                    byte b = 0, g = 0, r = 0;
                    if (index < paletteCount)
                    {
                        g = palette[index * 3];
                        b = palette[index * 3 + 1];
                        r = palette[index * 3 + 2];
                    }
                    image.Bgra[outPos++] = b;
                    image.Bgra[outPos++] = g;
                    image.Bgra[outPos++] = r;
                    if (index == transparentIndex)
                    {
                        image.Bgra[outPos++] = 0;
                        anyTransparent = true;
                    }
                    else
                    {
                        image.Bgra[outPos++] = 0xFF;
                    }
                }
            }
            image.HasAlpha = anyTransparent;
            return image;
        }

        /// <summary>
        /// Decodes the palette (chunk 0x17).
        /// </summary>
        /// <remarks>
        /// payload[0] is the entry count minus one and payload[1] a flags byte, followed by
        /// one MSB-first bit stream that carries the three channels in turn. Each channel is
        /// either raw (fixed-width samples) or delta-coded against the previous entry with a
        /// 16-symbol Huffman tree in which symbol 15 escapes.
        /// </remarks>
        public static byte[] DecodePalette(byte[] payload, out int count)
        {
            count = 0;
            if (payload.Length < 2)
                return Array.Empty<byte>();

            count = payload[0] + 1;
            int flags = payload[1];
            var reader = new ArtBitReader(payload, 2);
            var rgb = new byte[count * 3];
            var bits = new[] { 8, 8, 8 };

            for (int c = 0; c < 3; c++)
            {
                if (c == 0)
                {
                    // 728 = the sample widths 8, 8, 8.
                    int packed = (flags & 8) != 0 ? reader.Read(10) : 728;
                    for (int k = 2; k >= 0; k--)
                    {
                        bits[k] = packed % 9;
                        packed /= 9;
                    }
                }
                int width = bits[c];
                int shift = 8 - width;
                int rounding = (0x80 >> width) & 0xFF;

                if ((flags & PaletteRawBit[c]) != 0)
                {
                    for (int i = 0; i < count; i++)
                        rgb[i * 3 + c] = (byte)(((reader.Read(width) << shift) | rounding) & 0xFF);
                    continue;
                }

                int mask = reader.Read(16);
                var lengths = new int[16];
                int index = 0;
                while (mask != 0 && index < 16)
                {
                    if ((mask & 0x8000) != 0)
                    {
                        lengths[index] = reader.Read(4) + 1;
                        mask &= 0x7FFF;
                    }
                    index++;
                    mask <<= 1;
                }
                var huffman = new ArtHuffman(lengths);

                int value = reader.Read(width);
                rgb[c] = (byte)(((value << shift) | rounding) & 0xFF);
                for (int i = 1; i < count; i++)
                {
                    while (true)
                    {
                        int sym = huffman.Decode(reader);
                        if (sym != 15)
                        {
                            value -= sym;
                            break;
                        }
                        if (c == 0)
                        {
                            value -= 15;    // long decrement, keep decoding
                            continue;
                        }
                        value = reader.Read(width);     // absolute reset
                        break;
                    }
                    rgb[i * 3 + c] = (byte)(((value << shift) | rounding) & 0xFF);
                }
            }
            return rgb;
        }

        /// <summary>
        /// Parses a transparency key (chunk 0x1B).
        /// </summary>
        private static void ParseTransparency(byte[] payload, ref int kind, ref int b, ref int g, ref int r, ref int index)
        {
            if (payload.Length == 0)
                return;
            if (payload[0] == 1 && payload.Length >= 4)
            {
                kind = 1;
                b = payload[1];
                g = payload[2];
                r = payload[3];
            }
            else if (payload[0] == 2 && payload.Length >= 2)
            {
                kind = 2;
                index = payload[1];
            }
            else
            {
                kind = 0;
            }
        }

        /// <summary>
        /// Resolves a transparency key to a palette index, or -1 if the image is opaque.
        /// </summary>
        private static int ResolveTransparentIndex(int kind, int keyB, int keyG, int keyR, int keyIndex,
            int paletteCount, byte[] palette)
        {
            if (kind == 2)
                return keyIndex < paletteCount ? keyIndex : -1;
            if (kind != 1)
                return -1;
            for (int i = 0; i < paletteCount; i++)
            {
                if (palette[i * 3 + 1] == keyB && palette[i * 3] == keyG && palette[i * 3 + 2] == keyR)
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// Builds the 65-entry match-offset table for the row that is about to be decoded.
        /// </summary>
        /// <remarks>
        /// The table holds image offsets: first the 24 neighborhood seeds, then rings of
        /// radius 4, 5 and a partial 6 walked as left column / bottom row / right column,
        /// then plain backward distances for whatever is left. Entries that would reach
        /// above the top of the decoded area are dropped, so the table changes for the first
        /// six rows and then stays as built for row 5.
        ///
        /// Finally the first 24 entries are permuted using the 24-bit word from the 0x18
        /// chunk's preamble: entries whose bit (counting down from bit 31) is set come
        /// first, in order, then the rest. That is how the encoder tells the decoder which
        /// neighbors matter most for this particular image.
        /// </remarks>
        public static ArtOffsetTable BuildOffsetTable(int rowLength, int rowsDone, uint permutation)
        {
            int limit = -rowsDone;
            var offsets = new List<int>(ArtJgLossless.TableSize + 32);

            for (int i = 0; i < NumSeeds; i++)
            {
                int dy = SeedOffsets[i, 0], dx = SeedOffsets[i, 1];
                if (limit <= dy)
                    offsets.Add(dy * rowLength + dx);
            }

            int radius = 4, ringBase = -4;
            while (true)
            {
                int value = ringBase, dy = 0;
                for (int i = 0; i < Math.Min(radius, 5); i++)      // left column, upwards
                {
                    if (limit <= dy)
                        offsets.Add(value);
                    value -= rowLength;
                    dy--;
                }
                if (radius == 6)
                    break;
                for (int i = 0; i < radius * 2; i++)               // bottom row, rightwards
                {
                    if (limit <= dy)
                        offsets.Add(value);
                    value++;
                }
                for (int i = 0; i < radius; i++)                   // right column, downwards
                {
                    if (limit <= dy)
                        offsets.Add(value);
                    value += rowLength;
                    dy++;
                }
                radius++;
                ringBase--;
            }

            // Distances that the rows above already cover, so that the plain-distance filler
            // can skip them.
            var rangeLow = new int[6];
            var rangeHigh = new int[6];
            for (int k = 1; k <= 6; k++)
            {
                if (k <= rowsDone)
                {
                    int v = k * rowLength;
                    rangeLow[k - 1] = v - 6 - (k == 5 ? 1 : 0);
                    rangeHigh[k - 1] = v + 5;
                }
                else
                {
                    rangeLow[k - 1] = 0xFFFF;
                    rangeHigh[k - 1] = 0xFFFF;
                }
            }

            int counter = 6;
            while (true)
            {
                // A counter that reaches the bottom of an excluded range jumps to its top.
                // (Six shifts exhaust the ranges; the bound is only there so that a corrupt
                // row length can't spin here forever.)
                int shifts = 0;
                while (counter >= rangeLow[0] && shifts++ < 8)
                {
                    counter = rangeHigh[0];
                    for (int i = 0; i < 5; i++)
                    {
                        rangeLow[i] = rangeLow[i + 1];
                        rangeHigh[i] = rangeHigh[i + 1];
                    }
                }
                if (offsets.Count >= ArtJgLossless.TableSize)
                    break;
                counter++;
                offsets.Add(-counter);
            }

            // The chain that position codes above 64 walk, built from the exclusion ranges
            // that are still left over.
            var chainStart = new List<int> { 1 };
            var chainLength = new List<int> { counter };
            int previous = counter;
            for (int i = 0; i < 5; i++)
            {
                if (rangeLow[i] == 0xFFFF)
                    break;
                int length = rangeHigh[i] + 1 - rangeLow[i];
                chainStart.Add(rangeLow[i] + previous);
                chainLength.Add(length);
                previous = length;
            }

            var result = new int[offsets.Count];
            offsets.CopyTo(result);
            if (result.Length >= NumSeeds)
            {
                var head = new int[NumSeeds];
                int n = 0;
                for (int i = 0; i < NumSeeds; i++)
                {
                    if ((permutation & (0x80000000u >> i)) != 0)
                        head[n++] = result[i];
                }
                for (int i = 0; i < NumSeeds; i++)
                {
                    if ((permutation & (0x80000000u >> i)) == 0)
                        head[n++] = result[i];
                }
                Array.Copy(head, result, NumSeeds);
            }

            return new ArtOffsetTable(result)
            {
                ChainStart = chainStart.ToArray(),
                ChainLength = chainLength.ToArray()
            };
        }

        /// <summary>
        /// Holds the image's row buffer and the entropy state that persists across the
        /// pixel-data chunks.
        /// </summary>
        private class RowDecoder
        {
            private readonly Header header;
            private readonly int channels;
            private readonly byte[] recon;
            private readonly byte[] residual;
            private ArtHuffman? huffman;
            private uint permutation;
            private ArtOffsetTable? offsets;
            private int rowsDone;

            public readonly byte[] Rows;

            public RowDecoder(Header header)
            {
                this.header = header;
                channels = header.ThreeChannel ? 3 : 1;
                Rows = new byte[header.RowLength * header.Height];
                recon = new byte[header.RowLength];
                residual = new byte[header.RowLength];
            }

            /// <summary>
            /// Reads the entropy-coder tables (chunk 0x18): a permutation word for the
            /// offset table, followed by a code-length stream.
            /// </summary>
            public void SetTables(byte[] payload, int paletteCount)
            {
                int i = 0, population = 0, shift = 24;
                uint param = 0;
                while (i < payload.Length)
                {
                    int b = payload[i++];
                    population += System.Numerics.BitOperations.PopCount((uint)b);
                    param |= (uint)b << shift;
                    shift -= 8;
                    if (population >= 5 || shift < 8)
                        break;
                }

                int literalCount = header.PixelClass == 2 ? paletteCount : 256;
                if (literalCount <= 0)
                    throw new ImageDecodeException("ART graphic image has no palette.");
                int[] slotCounts = ArtJgLossless.SlotCounts(header.MinMatchLen, header.Budget,
                    header.LimitOther, header.LimitShort, header.Window);
                int total = literalCount;
                foreach (int c in slotCounts)
                    total += c;

                var reader = new ArtBitReader(payload, i);
                reader.Skip(1);     // the original decoder starts at bit position 6
                int[] lengths = ArtJgLossless.ReadCodeLengths(reader, total);
                huffman = new ArtHuffman(ArtJgLossless.AssembleLengths(lengths, literalCount, slotCounts));
                permutation = param;
            }

            /// <summary>
            /// Decodes one pixel-data chunk (0x19), which carries up to 32 rows that share a
            /// single bit stream.
            /// </summary>
            public void FeedRows(byte[] payload)
            {
                if (payload.Length == 0)
                    return;
                if (huffman == null)
                    throw new ImageDecodeException("ART pixel data arrived before the entropy tables.");

                int numRows = (payload[0] & 0x1F) + 1;
                bool stored = (payload[0] & 0x80) != 0;
                var reader = new ArtBitReader(payload, 1);
                int src = 1;

                for (int i = 0; i < numRows; i++)
                {
                    if (rowsDone >= header.Height)
                        break;
                    if (rowsDone < 6)
                        offsets = BuildOffsetTable(header.RowLength, rowsDone, permutation);

                    int start = rowsDone * header.RowLength;
                    if (stored)
                    {
                        int n = Math.Min(header.RowLength, payload.Length - src);
                        if (n > 0)
                            Array.Copy(payload, src, Rows, start, n);
                        src += header.RowLength;
                        rowsDone++;
                        continue;
                    }

                    int filter = header.FilterMode;
                    if (filter != 0)
                    {
                        if (rowsDone < 1)
                            filter = 3;                     // the first row always predicts from the left
                        if (header.FilterMode == 1)
                        {
                            // Adaptive: the row's filter type is coded with a tiny tree.
                            int index = 0;
                            while (true)
                            {
                                index = index * 2 + reader.Read(1);
                                filter = FilterTree[index];
                                if (filter != 0)
                                    break;
                            }
                        }
                    }

                    ArtJgLossless.LzDecode(reader, huffman, Rows, start, header.RowLength,
                        header.MinMatchLen, offsets ?? ArtJgLossless.FlatOffsets);

                    if (filter != 0)
                    {
                        Array.Copy(Rows, start, residual, 0, header.RowLength);
                        Unfilter(filter);
                        Array.Copy(recon, 0, Rows, start, header.RowLength);
                    }
                    rowsDone++;
                }
            }

            /// <summary>
            /// Undoes a row's PNG-style prediction filter. The reconstruction of the previous
            /// row is kept in `recon` and updated in place, so that `up` is the previous
            /// row's value and `left` the current row's previous output of the same channel.
            /// All arithmetic is modulo 256, and the first pixel of a row always uses filter 2.
            /// </summary>
            private void Unfilter(int filter)
            {
                for (int c = 0; c < channels; c++)
                    recon[c] = (byte)(residual[c] + recon[c]);
                for (int i = channels; i < header.Width * channels; i++)
                {
                    int up = recon[i];
                    int left = recon[i - channels];
                    int v = filter == 2 ? up
                        : filter == 3 ? left
                        : ((left + up) >> 1) + ((left + up) & 1);    // 4: average, rounded up
                    recon[i] = (byte)(v + residual[i]);
                }
            }
        }
    }
}
