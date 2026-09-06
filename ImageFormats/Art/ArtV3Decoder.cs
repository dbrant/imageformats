#nullable enable
using System;
using System.Collections.Generic;

/*

Decoder for "version 3" AOL ART (Johnson-Grace) images, i.e. files whose major
version byte is 3, as produced by AOL 3.x.

Despite sharing the "JG" signature and the chunk framing of ArtChunkReader,
these files are a different codec generation from the version-4 photo and
graphic images: the entropy coder of ArtJgLossless is not used, and there is
neither a wavelet nor a palette. What is there instead is essentially JPEG --
YCbCr, an 8x8 DCT, the standard zigzag, JPEG's (run, size) AC coding and IJG
quality scaling, with IJG's own jpeg_idct_islow for the inverse transform --
sent as three resolution passes, with two small vector quantizers alongside.

Each pass is displayable on its own, which is what made the format useful over
a dial-up link, so a truncated file still yields a picture from whatever passes
arrived:

  * Pass 1 (chunk 0x0C) is not DCT-coded at all. It is a small predictive coder
    over the macroblock grid: one luma DC term, two luma gradient terms, and one
    Cb and one Cr sample per 16x16 macroblock, lifted to a quarter-resolution
    base image. On its own it gives a blurry but complete picture.

  * Pass 2 (chunks 0x04 and 0x0A) adds one 8x8 DCT block per macroblock, giving
    the luma at half resolution, which is then scaled up 2x. The pass-1 DC and
    gradient terms are the first three coefficients of that block, which is why
    the pass carries no DC table of its own. Each chunk also carries one Cb and
    one Cr byte per macroblock, each an index into a 30-entry codebook that
    refines the pass-1 chroma.

  * Pass 3 (chunks 0x0D and 0x0E) lays a sparse full-resolution luma detail
    layer over the result, through a second, 100-entry codebook.

There is no published specification for any of this; it was derived by
analyzing the original Johnson-Grace decoder together with a corpus of sample
files.

Copyright 2026+ Dmitry Brant
https://dmitrybrant.com

License: MIT
*/

namespace DmitryBrant.ImageFormats
{
    internal static class ArtV3Decoder
    {
        // The chunk vocabulary of a version-3 file. It has nothing in common with the
        // version-4 tags in ArtChunkReader, apart from the end-of-stream tag; every other
        // tag (0x03, 0x07..0x09, 0x0F flush, 0x10..0x13) is skipped by its length.
        public const int TagHeader = 0x02;
        public const int TagPass2Tables = 0x04;
        public const int TagPass2Data = 0x0A;
        public const int TagPass1 = 0x0C;
        public const int TagPass3Tables = 0x0D;
        public const int TagPass3Data = 0x0E;

        // The original decoder's own limits, which double as a sanity check when we are
        // looking at raw sectors rather than a real file.
        private const int MaxWidth = 1280;
        private const int MaxHeight = 4096;

        /// <summary>
        /// The contents of the 14-byte image header (chunk 0x02), plus the geometry derived
        /// from it.
        /// </summary>
        internal class V3Header
        {
            public int Width;
            public int Height;
            /// <summary>IJG quality value that the quantization table is scaled by.</summary>
            public int Quality;
            /// <summary>Scale factor applied to the pass-1 luma DC terms.</summary>
            public int Step;
            /// <summary>Bits 0 and 1 say whether pass 1 carries its luma DC and chroma runs.</summary>
            public int Flags2;
            /// <summary>Macroblock rows reconstructed at a time; 1 or 2.</summary>
            public int BandRows;
            /// <summary>How many coefficients per block the DCT pass codes; less than 64.</summary>
            public int CoefBudget;
            public int MbCols;
            public int MbRows;

            public int PaddedWidth { get { return MbCols * 16; } }
            public int PaddedHeight { get { return MbRows * 16; } }
        }

        /// <summary>
        /// Parses the image header of a version-3 file (chunk 0x02), for callers that only
        /// need the dimensions.
        /// </summary>
        public static bool ParseHeader(byte[] payload, out int width, out int height)
        {
            var header = ReadHeader(payload);
            width = header != null ? header.Width : 0;
            height = header != null ? header.Height : 0;
            return header != null;
        }

        /// <summary>
        /// Parses the 14-byte image header of a version-3 file, or returns null if it does
        /// not describe an image this decoder can produce.
        /// </summary>
        /// <remarks>
        /// payload[0..1]  : flags. Bit 1 selects how the padded height is rounded.
        /// payload[2..3]  : height, little-endian.
        /// payload[4..5]  : width, little-endian.
        /// payload[6..7]  : the IJG quality value.
        /// payload[8..9]  : the pass-1 DC scale factor, as a folded signed value.
        /// payload[10]    : flags2; bits 0 and 1 gate the pass-1 luma DC and chroma runs.
        /// payload[11]    : how many macroblock rows make up a band; must be 1 or 2.
        /// payload[12]    : how many coefficients the DCT pass codes per block.
        /// payload[13]    : unused.
        /// </remarks>
        internal static V3Header? ReadHeader(byte[] payload)
        {
            if (payload.Length < 14)
                return null;

            int flags = payload[0] | (payload[1] << 8);
            int height = payload[2] | (payload[3] << 8);
            int width = payload[4] | (payload[5] << 8);
            int quality = payload[6] | (payload[7] << 8);
            int folded = payload[8] | (payload[9] << 8);
            int bandRows = payload[11];
            int coefBudget = payload[12];

            if (width < 1 || height < 1 || width > MaxWidth || height > MaxHeight)
                return null;
            if (bandRows != 1 && bandRows != 2)
                return null;
            if (coefBudget >= 64)
                return null;

            int paddedWidth = (width + 15) & ~15;
            int paddedHeight = (height + 15) & ~15;
            int band = bandRows * 16;
            if (paddedHeight % band != 0 && (flags & 2) == 0)
                paddedHeight += 16;

            return new V3Header
            {
                Width = width,
                Height = height,
                Quality = quality,
                Step = Unfold(folded),
                Flags2 = payload[10],
                BandRows = bandRows,
                CoefBudget = coefBudget,
                MbCols = paddedWidth / 16,
                MbRows = paddedHeight / 16
            };
        }

        /// <summary>
        /// Decodes a version-3 image into a top-down BGRA buffer.
        /// </summary>
        public static ArtImage Decode(byte[] data)
        {
            V3Header? header = null;
            byte[]? pass1Payload = null;
            byte[]? pass2Tables = null;
            byte[]? pass3Tables = null;
            var pass2Data = new List<byte[]>();
            var pass3Data = new List<byte[]>();

            foreach (var chunk in ArtChunkReader.Walk(data))
            {
                if (chunk.Tag == TagHeader)
                    header ??= ReadHeader(chunk.Payload);
                else if (chunk.Tag == TagPass1)
                    pass1Payload ??= chunk.Payload;
                else if (chunk.Tag == TagPass2Tables)
                    pass2Tables ??= chunk.Payload;
                else if (chunk.Tag == TagPass2Data)
                    pass2Data.Add(chunk.Payload);
                else if (chunk.Tag == TagPass3Tables)
                    pass3Tables ??= chunk.Payload;
                else if (chunk.Tag == TagPass3Data)
                    pass3Data.Add(chunk.Payload);
                else if (chunk.Tag == ArtChunkReader.TagEndOfStream)
                    break;
            }

            if (header == null)
                throw new ImageDecodeException("This ART file does not contain a version-3 image.");

            int mbRows = header.MbRows, mbCols = header.MbCols;
            var pass1 = new Pass1Planes(mbRows, mbCols);
            if (pass1Payload != null)
            {
                try
                {
                    DecodePass1(pass1Payload, header, pass1);
                }
                catch (Exception e)
                {
                    // Keep whatever arrived before the damage.
                    Util.log("Error while parsing ART version-3 pass 1: " + e.Message);
                }
            }

            int[][]? half = null;
            var cbPicks = new List<int>();
            var crPicks = new List<int>();
            if (pass2Tables != null && pass2Tables.Length > 0 && pass2Data.Count > 0)
            {
                try
                {
                    var plane = NewPlane(mbRows * 8, mbCols * 8);
                    int filled = DecodePass2(pass2Tables, pass2Data, header, pass1, plane, cbPicks, crPicks);
                    if (filled > 0)
                    {
                        // A file that was cut short partway through the pass leaves the rest
                        // of the plane empty, which would come out as flat grey. Seed those
                        // macroblocks from the pass-1 luma instead, so that the part which
                        // never arrived falls back to the blurry preview rather than nothing.
                        if (filled < mbRows * mbCols)
                            SeedMissingLuma(plane, pass1, filled, mbRows, mbCols);
                        half = plane;
                    }
                }
                catch (Exception e)
                {
                    // The tables themselves were unusable, so fall back to the pass-1 preview.
                    Util.log("Error while parsing ART version-3 pass 2: " + e.Message);
                    half = null;
                }
            }

            int[][] planeY, planeCb, planeCr;
            if (half == null)
            {
                // Pass 1 on its own: one sample per 4x4 block of pixels, replicated. The
                // original decoder quantizes this preview to 8 bits before the color stage.
                planeY = Replicate4(Quantize8(LumaPlane(pass1, mbRows, mbCols)));
                planeCb = Replicate4(Quantize8(ChromaPlane(pass1.Cb, mbRows, mbCols)));
                planeCr = Replicate4(Quantize8(ChromaPlane(pass1.Cr, mbRows, mbCols)));
            }
            else
            {
                planeY = UpsampleV(UpsampleH(half));
                Offset(planeY, 512);

                planeCb = ChromaPlane(pass1.Cb, mbRows, mbCols);
                planeCr = ChromaPlane(pass1.Cr, mbRows, mbCols);
                RefineChroma(planeCb, planeCr, cbPicks, crPicks, mbRows, mbCols);

                // The chroma grid is a quarter of the image in each direction, so it goes
                // through the same 2x step twice; the luma only needs it once.
                planeCb = UpsampleV(UpsampleV(UpsampleH(UpsampleH(planeCb))));
                planeCr = UpsampleV(UpsampleV(UpsampleH(UpsampleH(planeCr))));

                if (pass3Tables != null && pass3Data.Count > 0)
                {
                    try
                    {
                        AddDetail(planeY, pass3Tables, pass3Data, header);
                    }
                    catch (Exception e)
                    {
                        Util.log("Error while parsing ART version-3 pass 3: " + e.Message);
                    }
                }
            }

            return ToBgra(planeY, planeCb, planeCr, header.Width, header.Height);
        }

        // ------------------------------------------------------------------ entropy layer

        /// <summary>
        /// Canonical Huffman decoder over a JPEG-style BITS/HUFFVAL pair. This is not the
        /// coder in ArtJgLossless: version-3 files build their tables from code-length
        /// counts and an explicit symbol list, exactly as JPEG does.
        /// </summary>
        private class V3Huffman
        {
            private const int MaxCodeLength = 16;

            private readonly int[] counts = new int[MaxCodeLength + 1];
            private readonly int[] values;
            private readonly int[] firstCode = new int[MaxCodeLength + 1];
            private readonly int[] firstIndex = new int[MaxCodeLength + 1];

            public V3Huffman(int[] bits, int[] values)
            {
                this.values = values;
                for (int i = 0; i < MaxCodeLength; i++)
                    counts[i + 1] = bits[i];

                int code = 0, index = 0;
                for (int len = 1; len <= MaxCodeLength; len++)
                {
                    firstCode[len] = code;
                    firstIndex[len] = index;
                    code = (code + counts[len]) << 1;
                    index += counts[len];
                }
            }

            public bool IsEmpty { get { return values.Length == 0; } }

            public int Decode(ArtBitReader reader)
            {
                int code = 0;
                for (int len = 1; len <= MaxCodeLength; len++)
                {
                    code = (code << 1) | reader.Read(1);
                    int count = counts[len];
                    if (count != 0 && code >= firstCode[len] && code - firstCode[len] < count)
                    {
                        int index = firstIndex[len] + code - firstCode[len];
                        if (index < values.Length)
                            return values[index];
                        break;
                    }
                }
                throw new ImageDecodeException("Invalid Huffman code in ART version-3 stream.");
            }
        }

        /// <summary>
        /// Reads one Huffman table. These are JPEG's BITS[16] plus HUFFVAL, but bit-packed
        /// rather than byte-aligned, and several are packed back to back in one chunk.
        /// </summary>
        /// <remarks>
        /// The first seven bits give the shortest code length present (the high three bits,
        /// biased by one) and the number of codes of that length (the low four bits); a
        /// nibble of 15 means the real count is in the next eight bits instead. The counts
        /// for successive lengths follow in four bits each, and the table ends as soon as
        /// the running code-space budget is exhausted.
        ///
        /// The symbol list is then either raw bytes, or delta-coded against a running value:
        /// "1" plus eight bits sets it outright, "01" adds one, and "00" plus two bits adds
        /// two more than those bits.
        /// </remarks>
        private static V3Huffman ReadTable(ArtBitReader reader)
        {
            var bits = new int[16];
            int first = reader.Read(7);
            if (first == 0)
                return new V3Huffman(bits, Array.Empty<int>());

            int index = first >> 4;
            int count = first & 0x0F;
            int budget = 2 << index;
            int total = 0;
            while (true)
            {
                if (count == 15)
                    count = reader.Read(8);
                bits[index] = count;
                index++;
                total += count;
                budget -= count;
                if (total > 255 || budget < 0)
                    throw new ImageDecodeException("Malformed Huffman table in ART version-3 stream.");
                if (budget == 0)
                    break;
                if (index == 16)
                    throw new ImageDecodeException("Huffman table overruns 16 code lengths in ART version-3 stream.");
                budget *= 2;
                count = reader.Read(4);
            }

            var values = new int[total];
            if (reader.Read(1) != 0)
            {
                for (int i = 0; i < total; i++)
                    values[i] = reader.Read(8);
            }
            else
            {
                int running = 0;
                for (int i = 0; i < total; i++)
                {
                    if (reader.Read(1) != 0)
                        running = reader.Read(8);
                    else if (reader.Read(1) != 0)
                        running++;
                    else
                        running += 2 + reader.Read(2);
                    values[i] = running;
                }
            }
            return new V3Huffman(bits, values);
        }

        /// <summary>
        /// Reads a run of unsigned values with the simple run-length symbol coder that the
        /// pass-1 streams, the pass-2 chroma bytes and both pass-3 streams use: a symbol of
        /// 0xF0 or above stands for a run of zeros, and in word mode a symbol of 8 or above
        /// carries three more bits of magnitude.
        /// </summary>
        private static int[] ReadSymbols(ArtBitReader reader, V3Huffman huffman, int count, bool words)
        {
            var values = new int[count];
            int n = 0;
            while (n < count)
            {
                int s = huffman.Decode(reader);
                if (s >= 0xF0)
                    n += Math.Min(s - 0xEF, count - n);          // the values are already zero
                else if (words && s >= 8)
                    values[n++] = ((s - 7) << 3) | reader.Read(3);
                else
                    values[n++] = s;
            }
            return values;
        }

        /// <summary>
        /// Unfolds one of the format's signed integers, which are coded as an unsigned value
        /// with the sign in the low bit.
        /// </summary>
        private static int Unfold(int v)
        {
            return (v & 1) != 0 ? -((v + 1) / 2) : v / 2;
        }

        private static void UnfoldAll(int[] values)
        {
            for (int i = 0; i < values.Length; i++)
                values[i] = Unfold(values[i]);
        }

        // ------------------------------------------------------------------------- pass 1

        /// <summary>
        /// The five macroblock-grid planes that pass 1 produces. Passes 1 and 2 share
        /// coefficients: the DC and the two gradient terms are also the first three DCT
        /// coefficients of the pass-2 block.
        /// </summary>
        private class Pass1Planes
        {
            public readonly int[][] Dc;
            public readonly int[][] Gx;
            public readonly int[][] Gy;
            public readonly int[][] Cb;
            public readonly int[][] Cr;

            public Pass1Planes(int mbRows, int mbCols)
            {
                Dc = NewPlane(mbRows, mbCols);
                Gx = NewPlane(mbRows, mbCols);
                Gy = NewPlane(mbRows, mbCols);
                Cb = NewPlane(mbRows, mbCols);
                Cr = NewPlane(mbRows, mbCols);
            }
        }

        /// <summary>
        /// Decodes the 0x0C chunk: one header byte, then three Huffman tables, then a single
        /// bit stream that runs to the end of the chunk. For each macroblock row in turn the
        /// decoder reads a run of luma DC terms, a run of two gradient terms per macroblock,
        /// and a run of interleaved Cb and Cr samples.
        /// </summary>
        private static void DecodePass1(byte[] payload, V3Header header, Pass1Planes planes)
        {
            var reader = new ArtBitReader(payload, 1);
            var tables = new V3Huffman[3];
            for (int i = 0; i < 3; i++)
                tables[i] = ReadTable(reader);

            int mbCols = header.MbCols;
            var prevDc = new int[mbCols];
            var prevCb = new int[mbCols];
            var prevCr = new int[mbCols];
            var cb = new int[mbCols];
            var cr = new int[mbCols];

            for (int r = 0; r < header.MbRows; r++)
            {
                if ((header.Flags2 & 1) != 0)
                {
                    var row = ReadSymbols(reader, tables[0], mbCols, true);
                    UnfoldAll(row);
                    DpcmRow(row, prevDc, r == 0);
                    Array.Copy(row, prevDc, mbCols);
                    for (int c = 0; c < mbCols; c++)
                        planes.Dc[r][c] = row[c] * header.Step;
                }

                // The gradients are only unfolded: no prediction across macroblocks.
                var grad = ReadSymbols(reader, tables[1], mbCols * 2, true);
                UnfoldAll(grad);
                for (int c = 0; c < mbCols; c++)
                {
                    planes.Gx[r][c] = grad[c * 2];
                    planes.Gy[r][c] = grad[c * 2 + 1];
                }

                if ((header.Flags2 & 2) != 0)
                {
                    var chroma = ReadSymbols(reader, tables[2], mbCols * 2, true);
                    UnfoldAll(chroma);
                    for (int c = 0; c < mbCols; c++)
                    {
                        cb[c] = chroma[c * 2];
                        cr[c] = chroma[c * 2 + 1];
                    }
                    DpcmRow(cb, prevCb, r == 0);
                    DpcmRow(cr, prevCr, r == 0);
                    Array.Copy(cb, prevCb, mbCols);
                    Array.Copy(cr, prevCr, mbCols);
                    for (int c = 0; c < mbCols; c++)
                    {
                        // The second chroma component is coded as a difference from the first,
                        // and both are scaled into the two-fractional-bit units that the color
                        // stage expects, where 512 is neutral.
                        planes.Cb[r][c] = cb[c] * 4;
                        planes.Cr[r][c] = (cr[c] - cb[c]) * 4;
                    }
                }
            }
        }

        /// <summary>
        /// The two-dimensional predictive step of pass 1, in place: every sample but those of
        /// the first row and column is predicted by the average of the sample above and the
        /// sample to the left, rounded away from zero.
        /// </summary>
        private static void DpcmRow(int[] cur, int[] prev, bool firstRow)
        {
            int n = cur.Length;
            if (firstRow)
            {
                for (int k = 1; k < n; k++)
                    cur[k] += cur[k - 1];
                return;
            }
            cur[0] += prev[0];
            for (int k = 1; k < n; k++)
            {
                int t = prev[k] + cur[k - 1];
                cur[k] += t >= 0 ? (t + 1) >> 1 : t >> 1;
            }
        }

        /// <summary>
        /// Lifts the pass-1 luma terms to the quarter-resolution base grid. Each macroblock
        /// owns tiles 1, 2 and 3 of the 4x4 base tile it covers, in both directions; the
        /// tiles at index 0 sit on the macroblock boundary and are the average of their two
        /// neighbours across it, so a corner ends up averaging four.
        /// </summary>
        /// <remarks>
        /// This is the one place in the format that is reproduced approximately. The original
        /// decoder fills the grid by repeatedly halving with rounding rather than evaluating
        /// the ramp, so the linear form below comes out within about 2 of 1023 -- and that
        /// only ever shows in a file cut short after the 0x0C chunk, since a complete file
        /// rebuilds the luma from the DCT pass instead.
        /// </remarks>
        private static int[][] LumaPlane(Pass1Planes p, int mbRows, int mbCols)
        {
            int height = 4 * mbRows, width = 4 * mbCols;
            var plane = NewPlane(height, width);

            for (int r = 0; r < mbRows; r++)
            {
                for (int i = 1; i <= 3; i++)
                {
                    int[] row = plane[4 * r + i];
                    for (int c = 0; c < mbCols; c++)
                    {
                        int vertical = 1025 + p.Dc[r][c] + 10 * (2 - i) * p.Gy[r][c];
                        int gx = p.Gx[r][c];
                        for (int j = 1; j <= 3; j++)
                            row[4 * c + j] = (vertical + 9 * (2 - j) * gx) >> 1;
                    }
                }
            }

            // The first macroblock row has nothing above it to ramp away from, so the decoder
            // holds all of its tile rows at the i == 1 value.
            Array.Copy(plane[1], plane[2], width);
            Array.Copy(plane[1], plane[3], width);

            for (int r = 0; r < mbRows; r++)
            {
                int y = 4 * r;
                int[] above = r > 0 ? plane[y - 1] : plane[y + 1];
                int[] below = plane[y + 1];
                int[] row = plane[y];
                for (int x = 0; x < width; x++)
                    row[x] = (above[x] + below[x] + 1) >> 1;
            }
            for (int c = 0; c < mbCols; c++)
            {
                int x = 4 * c;
                for (int y = 0; y < height; y++)
                {
                    int[] row = plane[y];
                    row[x] = ((c > 0 ? row[x - 1] : row[x + 1]) + row[x + 1] + 1) >> 1;
                }
            }
            return plane;
        }

        /// <summary>
        /// Lifts one pass-1 chroma component to the quarter-resolution base grid. There are
        /// no gradients here, and the interpolation is bilinear rather than per-macroblock:
        /// each macroblock's sample sits at tile (3, 3) of its 4x4 tile, and everything
        /// between blends the four surrounding samples, with the top row and left column
        /// replicated.
        /// </summary>
        private static int[][] ChromaPlane(int[][] values, int mbRows, int mbCols)
        {
            var ext = NewPlane(mbRows + 1, mbCols + 1);
            for (int r = 0; r < mbRows; r++)
                for (int c = 0; c < mbCols; c++)
                    ext[r + 1][c + 1] = values[r][c];
            for (int c = 0; c < mbCols; c++)
                ext[0][c + 1] = values[0][c];
            for (int r = 0; r < mbRows; r++)
                ext[r + 1][0] = values[r][0];
            ext[0][0] = values[0][0];

            var plane = NewPlane(4 * mbRows, 4 * mbCols);
            for (int r = 0; r < mbRows; r++)
            {
                for (int c = 0; c < mbCols; c++)
                {
                    int topLeft = ext[r][c], topRight = ext[r][c + 1];
                    int bottomLeft = ext[r + 1][c], bottomRight = ext[r + 1][c + 1];
                    for (int i = 0; i < 4; i++)
                    {
                        int[] row = plane[4 * r + i];
                        for (int j = 0; j < 4; j++)
                        {
                            row[4 * c + j] = 512 + (((3 - i) * (3 - j) * topLeft
                                                   + (3 - i) * (j + 1) * topRight
                                                   + (i + 1) * (3 - j) * bottomLeft
                                                   + (i + 1) * (j + 1) * bottomRight) >> 4);
                        }
                    }
                }
            }
            return plane;
        }

        // ------------------------------------------------------------------------- pass 2

        private static readonly int[] Zigzag =
        {
             0,  1,  8, 16,  9,  2,  3, 10, 17, 24, 32, 25, 18, 11,  4,  5,
            12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13,  6,  7, 14, 21, 28,
            35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
            58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63
        };

        // The base quantization table, in zigzag order. This is not JPEG's Annex K luminance
        // table: entries 0-4 and 44-62 match it exactly, and the rest was retuned.
        private static readonly int[] QuantBase =
        {
             16,  11,  12,  14,  12,  18,  20,  18,  14,  16,  24,  16,  11,  23,  21,  40,
             22,  12,  12,  16,  40,  30,  15,  15,  20,  18,  18,  30,  33,  29,  26,  24,
             29,  30,  31,  40,  55,  45,  40,  43,  48,  43,  39,  40,  80, 109,  81,  87,
             95,  98, 103, 104, 103,  62,  77, 113, 121, 112, 100, 120,  92, 101, 103, 150
        };

        /// <summary>
        /// Scales the base table by the header's quality value, with the IJG formula
        /// (scale = Q &lt; 50 ? 5000/Q : 200 - 2Q, q = (base * scale + 50) / 100) written out
        /// for 16-bit arithmetic.
        /// </summary>
        private static int[] QuantTable(int quality)
        {
            var q = new int[64];
            for (int i = 0; i < 64; i++)
            {
                if (quality == 0 || quality >= 100)
                    q[i] = 1;
                else if (quality > 50)
                    q[i] = ((100 - quality) * QuantBase[i] + 25) * 2 / 100;
                else
                    q[i] = (50 * QuantBase[i] + quality / 2) / quality;
            }
            return q;
        }

        /// <summary>JPEG's HUFF_EXTEND: sign-extends a magnitude of the given bit width.</summary>
        private static int Extend(int v, int size)
        {
            return size != 0 && v < (1 << (size - 1)) ? v - (1 << size) + 1 : v;
        }

        /// <summary>
        /// Decodes one block's coefficients with JPEG's AC loop, into zigzag order. The block
        /// has no DC code of its own: pass 1 supplies the first three coefficients.
        /// </summary>
        private static void DecodeAcBlock(ArtBitReader reader, V3Huffman huffman, int budget, int[] block)
        {
            Array.Clear(block, 0, block.Length);
            int k = 1;
            int left = budget;
            while (left > 0)
            {
                int s = huffman.Decode(reader);
                if (s == 0)
                    break;                                  // end of block; the rest stay zero
                int run = s >> 4, size = s & 0x0F;
                if (run != 0)
                {
                    if (run > left)
                        throw new ImageDecodeException("Coefficient run overruns a block in ART version-3 stream.");
                    k += run;
                    left -= run;
                }
                if (k >= block.Length)
                    throw new ImageDecodeException("Coefficient index out of range in ART version-3 stream.");
                if (size != 0)
                    block[k] = Extend(reader.Read(size), size);
                k++;
                left--;
            }
        }

        /// <summary>
        /// Decodes the pass-2 chunks into a half-resolution luma plane of 8 samples per
        /// macroblock in each direction, and collects the chroma codebook indices. Returns
        /// how many macroblocks of the plane were actually filled, which is short of the
        /// whole grid if the file was cut off partway through the pass.
        /// </summary>
        /// <remarks>
        /// Blocks are decoded in batches of one band -- two macroblock rows, or one when the
        /// last band is short -- with exactly one batch per 0x0A chunk, each an independent
        /// bit stream that starts at the chunk's first payload byte. The chroma bytes for the
        /// batch follow the block data on the same stream.
        ///
        /// Of the up to nine tables that the 0x04 chunk declares, slot 1 is the luma AC
        /// table, slot 3 the Cb byte stream and slot 4 the Cr byte stream; the rest are empty.
        /// </remarks>
        private static int DecodePass2(byte[] tablePayload, List<byte[]> payloads, V3Header header,
            Pass1Planes pass1, int[][] plane, List<int> cbPicks, List<int> crPicks)
        {
            var tableReader = new ArtBitReader(tablePayload, 1);
            int tableCount = Math.Min((int)tablePayload[0], 9);
            var tables = new V3Huffman[tableCount];
            for (int i = 0; i < tableCount; i++)
                tables[i] = ReadTable(tableReader);

            if (tableCount < 2 || tables[1].IsEmpty)
                throw new ImageDecodeException("Missing AC Huffman table in ART version-3 stream.");
            var ac = tables[1];
            var cbTable = tableCount > 3 && !tables[3].IsEmpty ? tables[3] : null;
            var crTable = tableCount > 4 && !tables[4].IsEmpty ? tables[4] : null;

            int mbCols = header.MbCols, mbRows = header.MbRows;
            int budget = header.CoefBudget;
            var quant = QuantTable(header.Quality);
            var coef = new int[64];
            var zz = new int[64];
            var block = new int[64];
            int done = 0;                                // macroblocks the batches have covered
            int filled = 0;                              // macroblocks actually written out

            foreach (var payload in payloads)
            {
                int count = Math.Min(2 * mbCols, mbRows * mbCols - done);
                if (count <= 0)
                    break;
                var reader = new ArtBitReader(payload, 0);
                try
                {
                    for (int k = done; k < done + count; k++)
                    {
                        DecodeAcBlock(reader, ac, budget, coef);

                        int r = k / mbCols, c = k % mbCols;
                        Array.Clear(zz, 0, zz.Length);
                        zz[0] = pass1.Dc[r][c];
                        zz[1] = pass1.Gx[r][c];
                        zz[2] = pass1.Gy[r][c];
                        for (int i = 1; i < budget - 1; i++)
                            zz[2 + i] = coef[i];

                        // Dequantization is asymmetric: the DC passes through untouched, and
                        // the rest are scaled while being scattered out of the zigzag.
                        Array.Clear(block, 0, block.Length);
                        block[0] = zz[0];
                        for (int i = 1; i <= budget; i++)
                        {
                            if (zz[i] != 0)
                                block[Zigzag[i]] = zz[i] * quant[i];
                        }
                        Idct(block);

                        for (int y = 0; y < 8; y++)
                        {
                            int[] row = plane[r * 8 + y];
                            for (int x = 0; x < 8; x++)
                                row[c * 8 + x] = block[y * 8 + x];
                        }
                        filled = k + 1;
                    }

                    if (cbTable != null)
                        cbPicks.AddRange(ReadSymbols(reader, cbTable, count, false));
                    if (crTable != null)
                        crPicks.AddRange(ReadSymbols(reader, crTable, count, false));
                }
                catch (Exception e)
                {
                    // Reconstruct whatever arrived before the damage.
                    Util.log("Error while parsing ART version-3 pass 2 chunk: " + e.Message);
                    break;
                }
                done += count;
            }
            return filled;
        }

        /// <summary>
        /// Fills the macroblocks that pass 2 never reached with the pass-1 luma, scaled to
        /// the half-resolution grid the pass-2 plane is on.
        /// </summary>
        private static void SeedMissingLuma(int[][] plane, Pass1Planes pass1, int from, int mbRows, int mbCols)
        {
            var basePlane = LumaPlane(pass1, mbRows, mbCols);
            for (int k = from; k < mbRows * mbCols; k++)
            {
                int r = k / mbCols, c = k % mbCols;
                for (int y = 0; y < 8; y++)
                {
                    int[] src = basePlane[r * 4 + (y >> 1)];
                    int[] dst = plane[r * 8 + y];
                    for (int x = 0; x < 8; x++)
                        dst[c * 8 + x] = src[c * 4 + (x >> 1)] - 512;
                }
            }
        }

        // IJG's jpeg_idct_islow constants, i.e. FIX(x) at CONST_BITS = 13.
        private const int C298 = 2446;    // FIX_0_298631336
        private const int C390 = 3196;    // FIX_0_390180644
        private const int C541 = 4433;    // FIX_0_541196100
        private const int C765 = 6270;    // FIX_0_765366865
        private const int C899 = 7373;    // FIX_0_899976223
        private const int C1175 = 9633;   // FIX_1_175875602
        private const int C1501 = 12299;  // FIX_1_501321110
        private const int C1847 = 15137;  // FIX_1_847759065
        private const int C1961 = 16069;  // FIX_1_961570560
        private const int C2053 = 16819;  // FIX_2_053119869
        private const int C2562 = 20995;  // FIX_2_562915447
        private const int C3072 = 25172;  // FIX_3_072711026

        /// <summary>
        /// One pass of IJG's jpeg_idct_islow over eight samples.
        /// </summary>
        /// <param name="dcShift">
        /// How to descale a run that has only a DC term, or -1 for the row pass, which scales
        /// up by PASS1_BITS instead.
        /// </param>
        private static void IdctPass(int[] v, int[] result, int shift, int dcShift)
        {
            if ((v[1] | v[2] | v[3] | v[4] | v[5] | v[6] | v[7]) == 0)
            {
                int flat = dcShift < 0 ? v[0] << 2 : (v[0] + (1 << (dcShift - 1))) >> dcShift;
                for (int i = 0; i < 8; i++)
                    result[i] = flat;
                return;
            }

            int round = 1 << (shift - 1);
            int z2 = v[2], z3 = v[6];
            int z1 = (z2 + z3) * C541;
            int tmp2 = z1 - z3 * C1847;
            int tmp3 = z1 + z2 * C765;
            int tmp0 = (v[0] + v[4]) << 13;
            int tmp1 = (v[0] - v[4]) << 13;
            int tmp10 = tmp0 + tmp3, tmp13 = tmp0 - tmp3;
            int tmp11 = tmp1 + tmp2, tmp12 = tmp1 - tmp2;

            int t0 = v[7], t1 = v[5], t2 = v[3], t3 = v[1];
            int za = t0 + t3, zb = t1 + t2, zc = t0 + t2, zd = t1 + t3;
            int z5 = (zc + zd) * C1175;
            t0 *= C298;
            t1 *= C2053;
            t2 *= C3072;
            t3 *= C1501;
            za *= -C899;
            zb *= -C2562;
            zc = z5 - zc * C1961;
            zd = z5 - zd * C390;
            t0 += za + zc;
            t1 += zb + zd;
            t2 += zb + zc;
            t3 += za + zd;

            result[0] = (tmp10 + t3 + round) >> shift;
            result[7] = (tmp10 - t3 + round) >> shift;
            result[1] = (tmp11 + t2 + round) >> shift;
            result[6] = (tmp11 - t2 + round) >> shift;
            result[2] = (tmp12 + t1 + round) >> shift;
            result[5] = (tmp12 - t1 + round) >> shift;
            result[3] = (tmp13 + t0 + round) >> shift;
            result[4] = (tmp13 - t0 + round) >> shift;
        }

        /// <summary>
        /// The 8x8 inverse DCT, in place. This is IJG's jpeg_idct_islow with one change: the
        /// column pass descales by 16 rather than 18, so the output keeps two fractional
        /// bits, and there is no level shift and no clamp. The original decoder works in
        /// 16-bit registers, so every intermediate is truncated to 16 bits as well.
        /// </summary>
        private static void Idct(int[] block)
        {
            var v = new int[8];
            var result = new int[8];

            for (int i = 0; i < 64; i++)
                block[i] = (short)block[i];

            for (int r = 0; r < 8; r++)
            {
                Array.Copy(block, r * 8, v, 0, 8);
                IdctPass(v, result, 11, -1);
                for (int i = 0; i < 8; i++)
                    block[r * 8 + i] = (short)result[i];
            }
            for (int c = 0; c < 8; c++)
            {
                for (int r = 0; r < 8; r++)
                    v[r] = block[r * 8 + c];
                IdctPass(v, result, 16, 3);
                for (int r = 0; r < 8; r++)
                    block[r * 8 + c] = (short)result[r];
            }
        }

        /// <summary>
        /// Applies the pass-2 chroma refinement: one codebook entry per macroblock, laid over
        /// the 4x4 base tile it covers. Entry 0 is a flat 512, so a macroblock whose byte
        /// never arrived simply keeps its pass-1 chroma.
        /// </summary>
        private static void RefineChroma(int[][] cb, int[][] cr, List<int> cbPicks, List<int> crPicks,
            int mbRows, int mbCols)
        {
            for (int k = 0; k < mbRows * mbCols; k++)
            {
                int i1 = k < cbPicks.Count ? cbPicks[k] : 0;
                int i2 = k < crPicks.Count ? crPicks[k] : 0;
                if (i1 >= ChromaCodebookEntries)
                    i1 = 0;
                if (i2 >= ChromaCodebookEntries)
                    i2 = 0;

                int r = k / mbCols, c = k % mbCols;
                for (int i = 0; i < 4; i++)
                {
                    int[] cbRow = cb[4 * r + i];
                    int[] crRow = cr[4 * r + i];
                    for (int j = 0; j < 4; j++)
                    {
                        int e1 = ChromaCodebook[i1 * 16 + i * 4 + j];
                        int e2 = ChromaCodebook[i2 * 16 + i * 4 + j];
                        int x = 4 * c + j;
                        cbRow[x] = Clamp(cbRow[x] - 512 + e1, 0, 1023);
                        crRow[x] = Clamp(crRow[x] - 512 + e2 - e1 + 512, 0, 1023);
                    }
                }
            }
        }

        // ------------------------------------------------------------------------- pass 3

        /// <summary>
        /// Adds the pass-3 luma detail layer, at full resolution, to the given plane.
        /// </summary>
        /// <remarks>
        /// Chunk 0x0D is 1 + nbands header bytes -- one band per BandRows macroblock rows --
        /// followed by two Huffman tables; payload[1 + b] is how many macroblocks band b
        /// refines. A band that refines none has no 0x0E chunk at all, so the chunks line up
        /// with the bands that do, in order.
        ///
        /// Each 0x0E chunk is one band, holding a run of gaps that names the macroblocks
        /// getting detail -- numbered within the band, and restarting at each one -- followed
        /// by sixteen codebook indices for each of them, one per 4x4 sub-block.
        /// </remarks>
        private static void AddDetail(int[][] plane, byte[] tablePayload, List<byte[]> payloads, V3Header header)
        {
            int mbCols = header.MbCols, mbRows = header.MbRows;
            int bands = (mbRows + header.BandRows - 1) / header.BandRows;
            if (tablePayload.Length < 1 + bands)
                throw new ImageDecodeException("Short pass-3 table chunk in ART version-3 stream.");

            var tableReader = new ArtBitReader(tablePayload, 1 + bands);
            var gaps = ReadTable(tableReader);
            var picks = ReadTable(tableReader);

            int next = 0;
            for (int b = 0; b < bands && next < payloads.Count; b++)
            {
                int count = tablePayload[1 + b];
                if (count == 0)
                    continue;                        // a band with no detail carries no chunk

                var reader = new ArtBitReader(payloads[next++], 0);
                int[] deltas, values;
                try
                {
                    deltas = ReadSymbols(reader, gaps, count, false);
                    values = ReadSymbols(reader, picks, count * 16, false);
                }
                catch (Exception e)
                {
                    Util.log("Error while parsing ART version-3 pass 3 chunk: " + e.Message);
                    break;
                }

                int index = -1;
                for (int k = 0; k < count; k++)
                {
                    index += deltas[k] + 1;
                    int r = index / mbCols + b * header.BandRows;
                    int c = index % mbCols;
                    if (r >= mbRows)
                        break;
                    for (int sub = 0; sub < 16; sub++)
                    {
                        int entry = values[k * 16 + sub];
                        if (entry >= DetailCodebookEntries)
                            continue;
                        int y = r * 16 + (sub / 4) * 4;
                        int x = c * 16 + (sub % 4) * 4;
                        for (int i = 0; i < 4; i++)
                        {
                            int[] row = plane[y + i];
                            for (int j = 0; j < 4; j++)
                                row[x + j] += DetailCodebook[entry * 16 + i * 4 + j];
                        }
                    }
                }
            }
        }

        // ---------------------------------------------------------------- color conversion

        // The four color-conversion tables, which hold exactly the JFIF coefficients
        // (1.77200, -0.34411, -0.71428 and 1.40200) in 16-bit fixed point. Samples carry two
        // fractional bits, so neutral chroma is 512 rather than 128.
        private static readonly int[] BlueFromCb = MakeColorTable(7258, 12);
        private static readonly int[] GreenFromCb = MakeColorTable(-5638, 14);
        private static readonly int[] GreenFromCr = MakeColorTable(-2926, 12);
        private static readonly int[] RedFromCr = MakeColorTable(22970, 14);

        private static int[] MakeColorTable(int factor, int shift)
        {
            var table = new int[1024];
            for (int c = 0; c < 1024; c++)
                table[c] = (factor * (c - 512) + (1 << (shift - 1))) >> shift;
            return table;
        }

        /// <summary>
        /// Crops the reconstructed planes to the image size and converts them to a top-down
        /// BGRA buffer. The planes carry two fractional bits, which the color stage shifts
        /// off at the end.
        /// </summary>
        private static ArtImage ToBgra(int[][] planeY, int[][] planeCb, int[][] planeCr, int width, int height)
        {
            var image = new ArtImage { Width = width, Height = height, Bgra = new byte[width * height * 4] };
            int pos = 0;
            for (int y = 0; y < height; y++)
            {
                int[] rowY = planeY[y];
                int[] rowCb = planeCb[y];
                int[] rowCr = planeCr[y];
                for (int x = 0; x < width; x++)
                {
                    int luma = rowY[x];
                    int cb = Clamp(rowCb[x], 0, 1023);
                    int cr = Clamp(rowCr[x], 0, 1023);
                    image.Bgra[pos++] = (byte)Clamp((luma + BlueFromCb[cb]) >> 2, 0, 255);
                    image.Bgra[pos++] = (byte)Clamp((luma + GreenFromCb[cb] + GreenFromCr[cr]) >> 2, 0, 255);
                    image.Bgra[pos++] = (byte)Clamp((luma + RedFromCr[cr]) >> 2, 0, 255);
                    image.Bgra[pos++] = 0xFF;
                }
            }
            return image;
        }

        // --------------------------------------------------------------------- plane utils

        private static int[][] NewPlane(int height, int width)
        {
            var plane = new int[height][];
            for (int y = 0; y < height; y++)
                plane[y] = new int[width];
            return plane;
        }

        /// <summary>Scales a plane up 2x across, as out[2k + 1] = src[k] and out[2k] = the mean.</summary>
        private static int[][] UpsampleH(int[][] src)
        {
            int height = src.Length, width = src[0].Length;
            var plane = NewPlane(height, width * 2);
            for (int y = 0; y < height; y++)
            {
                int[] s = src[y];
                int[] d = plane[y];
                d[0] = s[0];
                for (int k = 0; k < width; k++)
                    d[2 * k + 1] = s[k];
                for (int k = 1; k < width; k++)
                    d[2 * k] = (s[k - 1] + s[k]) >> 1;
            }
            return plane;
        }

        /// <summary>Scales a plane up 2x down the image, by the same rule.</summary>
        private static int[][] UpsampleV(int[][] src)
        {
            int height = src.Length, width = src[0].Length;
            var plane = NewPlane(height * 2, width);
            Array.Copy(src[0], plane[0], width);
            for (int k = 0; k < height; k++)
                Array.Copy(src[k], plane[2 * k + 1], width);
            for (int k = 1; k < height; k++)
            {
                int[] above = src[k - 1], below = src[k], d = plane[2 * k];
                for (int x = 0; x < width; x++)
                    d[x] = (above[x] + below[x]) >> 1;
            }
            return plane;
        }

        /// <summary>Scales a plane up 4x in each direction by plain sample replication.</summary>
        private static int[][] Replicate4(int[][] src)
        {
            int height = src.Length, width = src[0].Length;
            var plane = NewPlane(height * 4, width * 4);
            for (int y = 0; y < height; y++)
            {
                int[] s = src[y];
                int[] d = plane[y * 4];
                for (int x = 0; x < width; x++)
                {
                    int v = s[x];
                    d[x * 4] = v;
                    d[x * 4 + 1] = v;
                    d[x * 4 + 2] = v;
                    d[x * 4 + 3] = v;
                }
                for (int i = 1; i < 4; i++)
                    Array.Copy(d, plane[y * 4 + i], width * 4);
            }
            return plane;
        }

        /// <summary>
        /// Takes a plane to 8 bits and back, which is what the original decoder does to the
        /// pass-1 preview before the color stage.
        /// </summary>
        private static int[][] Quantize8(int[][] plane)
        {
            foreach (var row in plane)
            {
                for (int x = 0; x < row.Length; x++)
                    row[x] = Clamp(row[x] >> 2, 0, 255) * 4;
            }
            return plane;
        }

        private static void Offset(int[][] plane, int delta)
        {
            foreach (var row in plane)
            {
                for (int x = 0; x < row.Length; x++)
                    row[x] += delta;
            }
        }

        private static int Clamp(int v, int min, int max)
        {
            return v < min ? min : (v > max ? max : v);
        }

        // ------------------------------------------------------------------- the codebooks

        // Two fixed vector-quantizer codebooks, each entry a table of 16 bytes read in raster
        // order as a 4x4 patch. Pass 2 refines the chroma with the 30-entry one, read as an
        // absolute chroma value (byte << 2, so 128 is the neutral 512); pass 3 lays down luma
        // detail with the 100-entry one, read as a signed offset ((byte - 128) << 2).
        private const int ChromaCodebookEntries = 30;
        private const int DetailCodebookEntries = 100;

        private static readonly int[] ChromaCodebook = MakeCodebook(ChromaCodebookBase64, 0);
        private static readonly int[] DetailCodebook = MakeCodebook(DetailCodebookBase64, 128);

        private static int[] MakeCodebook(string base64, int bias)
        {
            var raw = Convert.FromBase64String(base64);
            var book = new int[raw.Length];
            for (int i = 0; i < raw.Length; i++)
                book[i] = (raw[i] - bias) << 2;
            return book;
        }

        private const string ChromaCodebookBase64 =
            "gICAgICAgICAgICAgICAgH9/f4F+fX6AfX1+gH5+foCBgYKCgoODg4GCg4KAgIGAfn19fn18fH1/fn5+gYGBgH9+fn6B" +
            "f39+hIOBgIODgYCEhISCg4OBgIGAf4B/f39/hYSBf4aFgn+GhoSAhIWEgn18fH97ent9e3l5e317e355d3h7dnV1dn19" +
            "e32DhISEiYiGhImLi4eBhYiIfX+ChYKKiIJ3gIuKeXaAin54eX+Gh4mHj42Ojnl6e3t6enp9hYiDgIiHenODfnt4g4SE" +
            "g29seIFtbHB2bm9venl3e4Byhod/c4mHgH2Ih36Fhod/ipCOi3aEkpZva3KGgHZwbXqIhn1xd4J+dHeKhH1+iYWOkJGQ" +
            "e3yGjnh8fnl9gXx6f4B+fo2NioyKkZKSdnZ3eJKSjYyHfHl4eHd6eXx8fX5/hYuMgIeRl4WOhnWGcG56c3WDh3t5hYWS" +
            "iHyCmYB6gImKf3WQj5SLlJWVl3mFjpR3gpCPjpSTiY2Ce3V3eXyBgXJ2eYp2d3SJgnl3eYiQlICGh4iAh46VhpGal4qI" +
            "dHWNhYWKmJeVlY6Yloxrd4mKiIZ+gnt0epFwf5GHjpCFeX15c4F3b22NeW19iIGBgoEAAAAAAAAAAAAAAAAAAAAA";

        private const string DetailCodebookBase64 =
            "gICAgICAgICAgICAgH+AgH98fn5/f35+hISDgIOHhYOBgYODgoOEhoOEhoaCg4SHfHx6eX58fX1+fn5/fn1/f4WFgn2G" +
            "iIR/hYWEgoSGg4KDhISEgYGAf31+fH19fHx8hIWHhoOFh4d/gIOEfn5+f3x+goV6fIGHe3t/hnx7f4Z+fn+AeXl6fHx5" +
            "en18e3p+g4aBeYOCfXp/fX58e31/gYGChICCg4F9hoR9d4WCeXd4f4WGeoKEhIOFgICHgn6AeneAiX53g4iBe4OFgX+F" +
            "g3+AgoB+goSDdnuBgXJ1en+Dh4mGhoiJhYmJh3+HiYB6gHd8gYF5eYGEeXiBhXt5gX5/fH17e3h6fH2ChYCDhoeDgXl3" +
            "goJ9dIKEfnWEhoF4e3mBhX58eH2Bgn13gIaCfHZ3foB6e3uAe3t7eXp8enmKhoB/h4iEfX2GiIR1foiKdHF3enV1enx6" +
            "e3+CgIKFhISDeX+GhHd/iIR2gIiDeYByhIKEdIOChXaDhIB5g4F/gHh1d4eCd3OIioSBgICFh4WCfn6FhYGCf4B8e3x5" +
            "c3OLgoKAj4B/gI9+gIKGf4KCfHp2eIB+dXGCe3Vzfnx5eoaIjYSDiI6If4aPh3yEjoaPkI+LfHyAf31+gIKBgYCBeoOF" +
            "gnd5g4eCeXN7iYeAd317f4CJiYqHhYSCf3p5fH15f4l8d3qOfXx4i4OAeISChYF6eX94eYR1c4OMcnuFiJB8doCIfnqA" +
            "gHx6fHuEg3mGiImKh4aGg3x6d3SChoWGf4KBgYuIgH+PkYZ9go6MiH2Bg4OAhYeFbmxuc4SCgoWChYaGen6HiHZtb32D" +
            "fnRxfXmCh299hYtyhYqMeYiLioOBf313g4V8a4eHfmuAi319eXZyent8epCSkZKCgYOAaWt0eoOIjIyRjYuNgYOAf5R9" +
            "a4KVf2qCj35vg4Z/eIJ8gYJ9fH6Bfn6FbXeIiF2JdmtrbJWSiXuNkZaVfHp+gW1zdnl3dW9zkYt9bomLiYSFhIWAho+L" +
            "i2pzgIGCdW95i4mDd3J3c3hwdXR6hIN8fpWLd2uIjo6EfIWIjXt9fX1qcpCOfXx+l398c4OIhnKDg4tae4aMYIR2bZ2E" +
            "YIWed4qWjHd6f5CJeniDinpxc3V3aW6MdXyJfIuQi3t+gH95hotpjImFaoOJiG6Ai4twfX15fXaAfW1xeXNniXhvhYiI" +
            "kJSVfH58hYN/bmeKioV/bnGegWB+nHtxj4p7lXd6f3CGjXdxhYx4aHSPgWyEln19hXVmgIJsgYeLVpKGinB9dWqLhGtz" +
            "pHdZhpx2XYeMemaQlX2DeH2BoXp+f46AfX6RdHyCdIh/gHeNeIOChX2CZ4GVgmqElIZqe45/cneHf2F5iIB2a4KPi3lv" +
            "iI6FcnSJeJKNgH+JlICAdZKGgm2HbGVye4yAdHyRnZZ9gISWl3uFjY+EiYiPZ1xiZY+LjHpwcHmQimt9lJKLZHl6j49/" +
            "lYJlf5d1enp5eIJ7d4eIf4hra4iLl3B6hpyQaHqBlYpohX6KYXd9iHtpd4OSdGl/dnFtbGlxhZCMk5KakI2RmIB9Wm6E" +
            "iG2BZn2fiId1iICkcoiMm3NziZZ1cISfe36Nfo+AiXSFfYqMd1lYiJF/bpmajX2AcIOWaGlwe352Zmh0f357am1vfWlf" +
            "YmuUlIt7eHKLfHSDjnGIp7R+i4eUfK+EsZB6RpZgvZitcotrdIJdlphyS4mWe2eBin6jaX6BjZeenHCUj4N0aGZljpGM" +
            "hnFvdYCQY2N+l5FqZHOGlW2alGRtfZl7fXKKjmxtbJZ5dYOhc3lnjo+Hb4WPjnB0e6KpgJuNlHZzfH9ua4mQcYV8q42A" +
            "jpGAfZOegnd9e4V3ZoKKiViHf5FfkXuQbJF8hoGAc4J/c2yod2VjqXFtiI96eGOzgXltmYuIdWyRk4trcJaKi2Nmj5dw" +
            "dXCIgX59c3t3YpF4gVKeg3VeoYGAarCSqJ2LU3Z2Z12xt55tbnh2gIBkkodtgId3eKl0fY2Dg45lZ3eMd2SDiYpiZIKN" +
            "gVegmJOLc3x3dl5xbG11fn6MkJWIh2lvd4Z2aWdvk6OEeX6LhJZ0gXOHdGlkZItpgn92Z4+YXoCqdVaUm3qYcH6BZHOn" +
            "eWyGgnyfi356mHmCew==";
    }
}
