#nullable enable
using System;
using System.Collections.Generic;
using System.IO;

/*

Decoder for the "photo" image inside an AOL ART (Johnson-Grace) file: header
chunk 0x40 followed by data chunks 0x41.

The photo codec is a progressive, five-level CDF 9/7 wavelet coder, i.e. the
same transform that JPEG 2000 calls "9/7 irreversible":

  * Level 0 carries the LL band, DPCM-coded with a gradient predictor.
  * Levels 1..4 each carry three subbands (HL, LH, HH). Their quantized
    coefficients are run-length coded across three parallel byte streams, each
    compressed with the JG lossless entropy codec (see ArtJgLossless).
  * Color is YCbCr. The chroma components are not subsampled; they simply stop
    refining at a lower pyramid level and are upsampled at the end.

Chunks are handed to the components by a credit scheduler, so a single file
interleaves the components' refinement passes in bandwidth order.

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
    internal static class ArtPhotoDecoder
    {
        private const int Levels = 5;

        private const int StateIdle = 0x100;
        private const int StatePartial = 0x101;

        private const int MaxPixels = 64 * 1024 * 1024;

        // CDF 9/7 synthesis filters. The original decoder stores them as 16-bit fixed
        // point (G0 as 0xC9DA / 0x0A6B for the even taps and 0x6B08 / 0x1086 for the odd
        // ones, G1 as 0xDA4A / 0x609D / 0x1C52 / 0x061B / 0x09AF); using doubles instead
        // costs about 0.45 of mean absolute error out of 255.
        private static readonly double[] G0 = MakeG0();
        private static readonly double[] G1 = MakeG1();

        // Byte alphabet of the LL band's DPCM coder. The encoder always selects mode 2.
        private static readonly int[] LlRunLimit = { 100, 50, 25, 0 };
        private static readonly int[] LlBias = { 177, 152, 139, 126 };
        private const int LlMode = 2;

        private static double[] MakeG0()
        {
            double[] g = { -0.09127176311424948, -0.05754352622849957, 0.5912717631142470,
                           1.115087052456994, 0.5912717631142470, -0.05754352622849957,
                           -0.09127176311424948 };
            double s = Math.Sqrt(2.0);
            for (int i = 0; i < g.Length; i++)
                g[i] /= s;
            return g;
        }

        private static double[] MakeG1()
        {
            double[] g = { 0.02674875741080976, 0.01686411844287495, -0.07822326652898785,
                           -0.2668641184428723, 0.6029490182363579, -0.2668641184428723,
                           -0.07822326652898785, 0.01686411844287495, 0.02674875741080976 };
            double s = Math.Sqrt(2.0);
            for (int i = 0; i < g.Length; i++)
                g[i] *= s;
            return g;
        }

        /// <summary>
        /// Everything one level of one component accumulates across its chunks.
        /// </summary>
        private class LevelData
        {
            public int NumRows;
            public int Flags;
            public int Mode;
            public readonly int[] Steps = new int[3];
            public readonly MemoryStream Values = new MemoryStream();      // sub-stream 1
            public readonly MemoryStream ValueRuns = new MemoryStream();   // sub-stream 2
            public readonly MemoryStream ZeroRuns = new MemoryStream();    // sub-stream 3
        }

        private class Component
        {
            public int Width;
            public int Height;
            public int Level;                 // number of pyramid levels started
            public int State = StateIdle;
            public LevelData[] LevelData = new LevelData[Levels];

            // LL band (level 0)
            public int LlStep;
            public int LlSeen;
            public readonly MemoryStream LlParts = new MemoryStream();
            public int[][] LlPlane = Array.Empty<int[]>();

            // Reconstructed plane, in units of 1/4 of an 8-bit sample.
            public double[][]? Plane;

            public Component(int width, int height)
            {
                Width = width;
                Height = height;
                for (int i = 0; i < Levels; i++)
                    LevelData[i] = new LevelData();
                LevelDims(0, out int w0, out int h0);
                LlPlane = new int[h0][];
                for (int y = 0; y < h0; y++)
                    LlPlane[y] = new int[w0];
            }

            /// <summary>
            /// Dimensions of the given pyramid level: the full dimensions halved (rounding
            /// up) once for each level above it.
            /// </summary>
            public void LevelDims(int level, out int w, out int h)
            {
                w = Width;
                h = Height;
                for (int i = 0; i < Levels - 1 - level; i++)
                {
                    w = (w + 1) >> 1;
                    h = (h + 1) >> 1;
                }
            }

            /// <summary>Dimensions of the level currently being produced.</summary>
            public void CurrentDims(out int w, out int h)
            {
                LevelDims(Math.Max(0, Level - 1), out w, out h);
            }
        }

        /// <summary>
        /// The credit scheduler that decides which component each 0x41 chunk belongs to.
        /// </summary>
        private class Scheduler
        {
            private readonly int count;
            private readonly int[] credit;
            private int group;

            public Scheduler(int componentCount)
            {
                count = componentCount;
                credit = new int[componentCount];
                group = (1 << componentCount) - 1;
            }

            public int Pick()
            {
                if (group != 0)
                    return LowestBit(group);
                for (int i = 0; i < count; i++)
                {
                    if (credit[i] == 0)
                        return i;
                }
                while (true)
                {
                    int mask = 0;
                    for (int i = 0; i < count; i++)
                    {
                        credit[i]--;
                        if (credit[i] == 0)
                            mask |= 1 << i;
                    }
                    if (mask != 0)
                        return LowestBit(mask);
                }
            }

            public void Done(int component, int numRows)
            {
                if (group != 0 && numRows != 0)
                    group &= ~(1 << component);
                credit[component] += numRows;
            }

            private static int LowestBit(int mask)
            {
                int i = 0;
                while ((mask & 1) == 0)
                {
                    mask >>= 1;
                    i++;
                }
                return i;
            }
        }

        /// <summary>
        /// Parses the header of a photo image (chunk 0x40).
        /// </summary>
        /// <remarks>
        /// payload[0]   : codec identifier (0x15 in every file seen so far).
        /// payload[1]   : bit 0 set means 3 color components, clear means 1 (grayscale).
        ///                bit 1 selects between two component-sampling configurations.
        /// payload[2..3]: width, little-endian.
        /// payload[4..5]: height, little-endian.
        /// </remarks>
        public static bool ParseHeader(byte[] payload, out int componentCount, out int width, out int height)
        {
            componentCount = 0;
            width = 0;
            height = 0;
            if (payload.Length < 6 || payload[0] != 0x15)
                return false;
            componentCount = (payload[1] & 1) != 0 ? 3 : 1;
            width = payload[2] | (payload[3] << 8);
            height = payload[4] | (payload[5] << 8);
            return width > 0 && height > 0 && (long)width * height <= MaxPixels;
        }

        /// <summary>
        /// Decodes a photo image into a top-down BGRA buffer.
        /// </summary>
        public static ArtImage Decode(byte[] data)
        {
            List<Component>? components = null;
            Scheduler? scheduler = null;
            int width = 0, height = 0;

            foreach (var chunk in ArtChunkReader.Walk(data))
            {
                if (chunk.Tag == ArtChunkReader.TagPhotoHeader)
                {
                    if (components != null)
                        break;
                    if (!ParseHeader(chunk.Payload, out int componentCount, out width, out height))
                        throw new ImageDecodeException("Unsupported ART photo header.");
                    components = new List<Component>();
                    for (int i = 0; i < componentCount; i++)
                        components.Add(new Component(width, height));
                    scheduler = new Scheduler(componentCount);
                }
                else if (chunk.Tag == ArtChunkReader.TagPhotoData && components != null && scheduler != null)
                {
                    int index = scheduler.Pick();
                    int numRows;
                    try
                    {
                        numRows = Feed(components[index], chunk.Payload);
                    }
                    catch (Exception e)
                    {
                        // Reconstruct whatever arrived before the damage.
                        Util.log("Error while parsing ART photo chunk: " + e.Message);
                        break;
                    }
                    scheduler.Done(index, numRows);
                }
                else if (chunk.Tag == ArtChunkReader.TagEndOfStream)
                {
                    break;
                }
            }

            if (components == null)
                throw new ImageDecodeException("This ART file does not contain a photo image.");

            foreach (var component in components)
            {
                try
                {
                    Reconstruct(component);
                }
                catch (Exception e)
                {
                    Util.log("Error while reconstructing ART photo component: " + e.Message);
                }
            }

            return ToBgra(components, width, height);
        }

        /// <summary>
        /// Parses one 0x41 chunk into the given component, and returns its row count.
        /// </summary>
        private static int Feed(Component c, byte[] payload)
        {
            if (payload.Length == 1 && payload[0] == 0 && c.State == StateIdle)
            {
                // A "skip" chunk advances the component one level with no detail
                // coefficients, i.e. that level is a pure lowpass upsample.
                if (c.Level >= Levels)
                    return 0;
                c.Level++;
                c.CurrentDims(out _, out int skipRows);
                c.LevelData[c.Level - 1].Flags = 0;
                c.LevelData[c.Level - 1].NumRows = skipRows;
                return skipRows;
            }

            int p = 0;
            int numRows = ArtChunkReader.ReadVarint(payload, ref p);

            c.LevelDims(0, out _, out int llHeight);

            if (c.Level == 0)
            {
                p++;    // unused byte
                if (p + 1 >= payload.Length)
                    return 0;
                c.LlPlane[0][0] = payload[p] | (payload[p + 1] << 8);
                p += 2;
                c.LlStep = ArtChunkReader.ReadVarint(payload, ref p);
                Append(c.LlParts, payload, p, payload.Length - p);
                c.Level = 1;
                c.LlSeen = numRows;
                c.State = numRows >= llHeight ? StateIdle : StatePartial;
                c.LevelData[0].NumRows = numRows;
                return numRows;
            }

            if (c.State == StatePartial && c.Level == 1 && c.LlSeen < llHeight)
            {
                Append(c.LlParts, payload, p, payload.Length - p);
                c.LlSeen += numRows;
                c.State = c.LlSeen >= llHeight ? StateIdle : StatePartial;
                c.LevelData[0].NumRows += numRows;
                return numRows;
            }

            bool newLevel = c.State == StateIdle;
            if (newLevel)
            {
                if (c.Level >= Levels)
                    return 0;
                c.Level++;
            }
            var level = c.LevelData[c.Level - 1];
            c.CurrentDims(out _, out int levelHeight);

            int len1 = ArtChunkReader.ReadVarint(payload, ref p);
            int len2 = ArtChunkReader.ReadVarint(payload, ref p);
            if (newLevel)
            {
                if (p >= payload.Length)
                    return 0;
                int flags = payload[p++];
                level.Flags = flags;
                level.Mode = (~flags >> 3) & 1;
                if ((flags & 1) != 0)
                    level.Steps[0] = ArtChunkReader.ReadVarint(payload, ref p);
                if ((flags & 2) != 0)
                    level.Steps[1] = ArtChunkReader.ReadVarint(payload, ref p);
                if ((flags & 4) != 0)
                    level.Steps[2] = ArtChunkReader.ReadVarint(payload, ref p);
                level.NumRows = 0;
            }

            if (len1 < 0 || len2 < 0)
                throw new ImageDecodeException("Invalid sub-stream lengths in ART photo chunk.");
            Append(level.Values, payload, p, len1);
            Append(level.ValueRuns, payload, p + len1, len2);
            Append(level.ZeroRuns, payload, p + len1 + len2, payload.Length - (p + len1 + len2));

            level.NumRows += numRows;
            c.State = level.NumRows >= levelHeight ? StateIdle : StatePartial;
            return numRows;
        }

        private static void Append(MemoryStream stream, byte[] payload, int offset, int count)
        {
            if (offset < 0)
            {
                count += offset;
                offset = 0;
            }
            if (offset >= payload.Length || count <= 0)
                return;
            stream.Write(payload, offset, Math.Min(count, payload.Length - offset));
        }

        /// <summary>
        /// Runs the LL band's DPCM decode and then the wavelet synthesis levels.
        /// </summary>
        private static void Reconstruct(Component c)
        {
            c.LevelDims(0, out int llWidth, out int llHeight);
            byte[] llStream = ArtJgLossless.Decode(c.LlParts.ToArray());
            DecodeLowpass(c, llStream, llWidth, llHeight);

            var plane = new double[llHeight][];
            for (int y = 0; y < llHeight; y++)
            {
                plane[y] = new double[llWidth];
                for (int x = 0; x < llWidth; x++)
                    plane[y][x] = c.LlPlane[y][x];
            }

            for (int levelIndex = 1; levelIndex < c.Level; levelIndex++)
            {
                var level = c.LevelData[levelIndex];
                c.LevelDims(levelIndex, out int w, out int h);
                int lowW = (w + 1) >> 1, lowH = (h + 1) >> 1;
                int hlW = w >> 1, lhW = (w + 1) >> 1, hhW = w >> 1;
                int highH = h >> 1;

                var hl = NewPlane(lowH, hlW);
                var lh = NewPlane(highH, lhW);
                var hh = NewPlane(highH, hhW);

                if (level.Flags != 0)
                    DecodeSubbands(level, hl, lh, hh, lowH, highH, hlW, lhW, hhW);

                plane = InverseDwt(plane, hl, lh, hh, w, h, lowW, lowH);
            }

            c.Plane = plane;
        }

        private static double[][] NewPlane(int height, int width)
        {
            var plane = new double[height][];
            for (int y = 0; y < height; y++)
                plane[y] = new double[width];
            return plane;
        }

        /// <summary>
        /// Decodes the LL band of level 0, which is stored as 16-bit values with a gradient
        /// predictor.
        /// </summary>
        /// <remarks>
        /// The predictor is p[x][y] = p[x-1][y] + p[x][y-1] - p[x-1][y-1] + step * delta,
        /// with column 0 predicted purely vertically and row 0 purely horizontally. The
        /// deltas are byte codes: a code at or below the run limit starts a run of zero
        /// deltas (and the run carries over row boundaries), 0xFC..0xFF escape to 8- or
        /// 16-bit signed deltas, and everything else is byte - bias.
        /// </remarks>
        private static void DecodeLowpass(Component c, byte[] stream, int width, int height)
        {
            var state = new LowpassReader(stream, LlRunLimit[LlMode], LlBias[LlMode]);
            int step = c.LlStep;
            int[][] buf = c.LlPlane;

            for (int y = 1; y < height; y++)
                buf[y][0] = S16((long)buf[y - 1][0] + (long)step * state.Next());
            for (int x = 1; x < width; x++)
                buf[0][x] = S16((long)buf[0][x - 1] + (long)step * state.Next());
            for (int y = 1; y < height; y++)
            {
                int[] row = buf[y];
                int[] prev = buf[y - 1];
                for (int x = 1; x < width; x++)
                    row[x] = S16((long)row[x - 1] - prev[x - 1] + prev[x] + (long)step * state.Next());
            }
        }

        private static int S16(long v)
        {
            return (short)v;
        }

        /// <summary>
        /// Produces the LL band's stream of deltas, one per cell.
        /// </summary>
        private class LowpassReader
        {
            private readonly byte[] stream;
            private readonly int runLimit;
            private readonly int bias;
            private int pos;
            private int run;

            public LowpassReader(byte[] stream, int runLimit, int bias)
            {
                this.stream = stream;
                this.runLimit = runLimit;
                this.bias = bias;
            }

            public int Next()
            {
                if (run > 0)
                {
                    run--;
                    return 0;
                }
                if (pos >= stream.Length)
                    return 0;

                int b = stream[pos++];
                if (b > runLimit + 1)
                {
                    switch (b)
                    {
                        case 0xFC: return -ReadByte();
                        case 0xFD: return -ReadUInt16();
                        case 0xFE: return ReadByte();
                        case 0xFF: return ReadUInt16();
                        default: return b - bias;
                    }
                }
                if (b == 0)
                    run = ReadByte();
                else if (b == 1)
                    run = ReadUInt16();
                else
                    run = b - 1;
                run--;
                return 0;
            }

            private int ReadByte()
            {
                return pos < stream.Length ? stream[pos++] : 0;
            }

            private int ReadUInt16()
            {
                int lo = ReadByte();
                return lo | (ReadByte() << 8);
            }
        }

        /// <summary>
        /// Decodes and dequantizes the three subbands of one detail level.
        /// </summary>
        /// <remarks>
        /// The three sub-streams feed one run-length decoder that emits the coefficients of
        /// each output row pair in the order HL row, LH row, HH row. When the level's height
        /// is odd the final row pair contributes only its HL row.
        /// </remarks>
        private static void DecodeSubbands(LevelData level, double[][] hl, double[][] lh, double[][] hh,
            int lowH, int highH, int hlW, int lhW, int hhW)
        {
            bool haveHl = (level.Flags & 1) != 0;
            bool haveLh = (level.Flags & 2) != 0;
            bool haveHh = (level.Flags & 4) != 0;

            int total = 0;
            for (int g = 0; g < lowH; g++)
            {
                if (haveHl)
                    total += hlW;
                if (g < highH)
                {
                    if (haveLh)
                        total += lhW;
                    if (haveHh)
                        total += hhW;
                }
            }
            if (total == 0)
                return;

            byte[] values = ArtJgLossless.Decode(level.Values.ToArray());
            byte[] valueRuns = ArtJgLossless.Decode(level.ValueRuns.ToArray());
            byte[] zeroRuns = ArtJgLossless.Decode(level.ZeroRuns.ToArray());
            int[] flat = UnpackCoefficients(zeroRuns, valueRuns, values, total, level.Mode);

            int k = 0;
            for (int g = 0; g < lowH; g++)
            {
                if (haveHl)
                {
                    Dequantize(flat, k, hl[g], hlW, level.Steps[0]);
                    k += hlW;
                }
                if (g >= highH)
                    continue;
                if (haveLh)
                {
                    Dequantize(flat, k, lh[g], lhW, level.Steps[1]);
                    k += lhW;
                }
                if (haveHh)
                {
                    Dequantize(flat, k, hh[g], hhW, level.Steps[2]);
                    k += hhW;
                }
            }
        }

        /// <summary>
        /// Turns quantized coefficients back into values: v = c*step + step/5 for positive
        /// coefficients and c*step - step/5 for negative ones, keeping the original 16-bit
        /// wraparound.
        /// </summary>
        private static void Dequantize(int[] flat, int offset, double[] dest, int count, int step)
        {
            int delta = step / 5;
            for (int i = 0; i < count; i++)
            {
                int c = flat[offset + i];
                long v = c > 0 ? (long)c * step + delta : c < 0 ? (long)c * step - delta : 0;
                dest[i] = (short)v;
            }
        }

        /// <summary>
        /// Expands the alternating runs of zeros and of explicit values that carry a level's
        /// coefficients.
        /// </summary>
        /// <remarks>
        /// A mode of 0 means the next run is a run of zeros, 1 a run of values. Run lengths
        /// come from two separate byte streams; a length byte of 0xFF escapes to a 16-bit
        /// little-endian length, otherwise the length is the byte plus one. Values are one
        /// byte (0x00..0x7E meaning +1..+127, 0x80..0xFF meaning -128..-1) with 0x7F
        /// escaping to a 16-bit signed value.
        /// </remarks>
        private static int[] UnpackCoefficients(byte[] zeroRuns, byte[] valueRuns, byte[] values, int count, int mode)
        {
            var result = new int[count];
            int zi = 0, vi = 0, si = 0;
            int i = 0;
            int remaining = 0;

            while (i < count)
            {
                if (remaining == 0)
                {
                    mode = mode != 0 ? 0 : 1;
                    byte[] runs = mode == 0 ? zeroRuns : valueRuns;
                    int index = mode == 0 ? zi : vi;
                    if (index >= runs.Length)
                        break;      // truncated: leave the rest of the level at zero
                    int c = runs[index++];
                    if (c == 0xFF)
                    {
                        if (index + 1 >= runs.Length)
                            break;
                        remaining = runs[index] | (runs[index + 1] << 8);
                        index += 2;
                    }
                    else
                    {
                        remaining = c + 1;
                    }
                    if (mode == 0)
                        zi = index;
                    else
                        vi = index;
                    continue;
                }

                int n = Math.Min(remaining, count - i);
                if (mode == 0)
                {
                    i += n;
                    remaining -= n;
                    continue;
                }
                bool truncated = false;
                for (int j = 0; j < n; j++)
                {
                    if (si >= values.Length)
                    {
                        truncated = true;
                        break;
                    }
                    int b = values[si++];
                    int v;
                    if (b >= 0x80)
                    {
                        v = b - 256;
                    }
                    else if (b < 0x7F)
                    {
                        v = b + 1;
                    }
                    else
                    {
                        if (si + 1 >= values.Length)
                        {
                            truncated = true;
                            break;
                        }
                        v = (short)(values[si] | (values[si + 1] << 8));
                        si += 2;
                    }
                    result[i++] = v;
                }
                if (truncated)
                    break;
                remaining -= n;
            }
            return result;
        }

        /// <summary>
        /// One synthesis level: a standard inverse CDF 9/7 DWT with whole-sample symmetric
        /// extension, applied horizontally and then vertically.
        /// </summary>
        private static double[][] InverseDwt(double[][] ll, double[][] hl, double[][] lh, double[][] hh,
            int w, int h, int lowW, int lowH)
        {
            int highH = h >> 1;
            var scratch = new double[w];

            // Horizontal pass: LL + HL make the vertical-lowpass rows, LH + HH the
            // vertical-highpass rows.
            var lowRows = new double[lowH][];
            for (int y = 0; y < lowH; y++)
            {
                lowRows[y] = new double[w];
                SynthLine(y < ll.Length ? ll[y] : Array.Empty<double>(), hl[y], w, lowRows[y], scratch);
            }
            var highRows = new double[highH][];
            for (int y = 0; y < highH; y++)
            {
                highRows[y] = new double[w];
                SynthLine(lh[y], hh[y], w, highRows[y], scratch);
            }

            // Vertical pass, done one output row at a time rather than one column at a time:
            // each of the nine taps then applies to a whole row of the interleaved plane,
            // which keeps the inner loop sequential in memory.
            var result = NewPlane(h, w);
            for (int y = 0; y < h; y++)
            {
                double[] dest = result[y];
                for (int d = -4; d <= 4; d++)
                {
                    int q = y + d;
                    double tap;
                    if ((q & 1) == 0)
                    {
                        if (d < -3 || d > 3)
                            continue;
                        tap = G0[3 - d];
                    }
                    else
                    {
                        tap = G1[4 - d];
                    }
                    // The lowpass rows occupy the even positions, the highpass rows the odd ones.
                    int index = SymmetricExtend(h, q);
                    double[] source = (index & 1) == 0 ? lowRows[index >> 1] : highRows[index >> 1];
                    for (int x = 0; x < w; x++)
                        dest[x] += tap * source[x];
                }
            }
            return result;
        }

        /// <summary>
        /// Inverse 9/7 synthesis along one line. The lowpass samples land on the even output
        /// positions and the highpass samples on the odd ones.
        /// </summary>
        private static void SynthLine(double[] low, double[] high, int n, double[] dest, double[] scratch)
        {
            int lowCount = (n + 1) >> 1;
            int highCount = n >> 1;
            for (int i = 0; i < n; i++)
                scratch[i] = 0.0;
            for (int i = 0; i < lowCount && i < low.Length; i++)
                scratch[2 * i] = low[i];
            for (int i = 0; i < highCount && i < high.Length; i++)
                scratch[2 * i + 1] = high[i];

            // Away from the two ends no symmetric extension is needed, so the taps that
            // survive the parity test can simply be written out.
            int first = Math.Min(4, n);
            int last = Math.Max(first, n - 4);
            for (int p = 0; p < first; p++)
                dest[p] = SynthSample(scratch, n, p);
            for (int p = first; p < last; p++)
            {
                dest[p] = (p & 1) == 0
                    ? G0[5] * scratch[p - 2] + G0[3] * scratch[p] + G0[1] * scratch[p + 2]
                      + G1[7] * scratch[p - 3] + G1[5] * scratch[p - 1]
                      + G1[3] * scratch[p + 1] + G1[1] * scratch[p + 3]
                    : G0[6] * scratch[p - 3] + G0[4] * scratch[p - 1]
                      + G0[2] * scratch[p + 1] + G0[0] * scratch[p + 3]
                      + G1[8] * scratch[p - 4] + G1[6] * scratch[p - 2] + G1[4] * scratch[p]
                      + G1[2] * scratch[p + 2] + G1[0] * scratch[p + 4];
            }
            for (int p = last; p < n; p++)
                dest[p] = SynthSample(scratch, n, p);
        }

        /// <summary>
        /// One output sample of the inverse 9/7 synthesis, with symmetric extension.
        /// </summary>
        private static double SynthSample(double[] scratch, int n, int p)
        {
            double sum = 0.0;
            for (int d = -4; d <= 4; d++)
            {
                int q = p + d;
                double tap;
                if ((q & 1) == 0)
                {
                    if (d < -3 || d > 3)
                        continue;
                    tap = G0[3 - d];
                }
                else
                {
                    tap = G1[4 - d];
                }
                sum += tap * scratch[SymmetricExtend(n, q)];
            }
            return sum;
        }

        /// <summary>
        /// Whole-sample symmetric extension: x[-i] = x[i], x[n-1+i] = x[n-1-i].
        /// </summary>
        private static int SymmetricExtend(int n, int index)
        {
            if (n == 1)
                return 0;
            int period = 2 * (n - 1);
            int m = index % period;
            if (m < 0)
                m += period;
            return m < n ? m : period - m;
        }

        /// <summary>
        /// Converts the reconstructed component planes to a top-down BGRA buffer. The planes
        /// carry two fractional bits, so they get divided by 4 to give 0..255 samples.
        /// </summary>
        private static ArtImage ToBgra(List<Component> components, int width, int height)
        {
            var image = new ArtImage { Width = width, Height = height, Bgra = new byte[width * height * 4] };
            var planes = new double[components.Count][][];
            for (int i = 0; i < components.Count; i++)
                planes[i] = Resample(components[i].Plane, width, height, i == 0 ? 0.0 : 128.0 * 4.0);

            int pos = 0;
            if (components.Count == 1)
            {
                for (int y = 0; y < height; y++)
                {
                    double[] row = planes[0][y];
                    for (int x = 0; x < width; x++)
                    {
                        byte g = Clamp(row[x] / 4.0);
                        image.Bgra[pos++] = g;
                        image.Bgra[pos++] = g;
                        image.Bgra[pos++] = g;
                        image.Bgra[pos++] = 0xFF;
                    }
                }
                return image;
            }

            for (int y = 0; y < height; y++)
            {
                double[] rowY = planes[0][y];
                double[] rowCb = planes[1][y];
                double[] rowCr = planes[2][y];
                for (int x = 0; x < width; x++)
                {
                    double luma = rowY[x] / 4.0;
                    double cb = rowCb[x] / 4.0 - 128.0;
                    double cr = rowCr[x] / 4.0 - 128.0;
                    image.Bgra[pos++] = Clamp(luma + 1.772 * cb);
                    image.Bgra[pos++] = Clamp(luma - 0.344136 * cb - 0.714136 * cr);
                    image.Bgra[pos++] = Clamp(luma + 1.402 * cr);
                    image.Bgra[pos++] = 0xFF;
                }
            }
            return image;
        }

        /// <summary>
        /// Brings a component up to the full image size. A component that stopped at a lower
        /// level is normally already full size, because its remaining levels arrive as skip
        /// chunks, but a truncated file can leave one short.
        /// </summary>
        private static double[][] Resample(double[][]? plane, int width, int height, double fill)
        {
            if (plane == null || plane.Length == 0)
            {
                var flat = NewPlane(height, width);
                if (fill != 0.0)
                {
                    for (int y = 0; y < height; y++)
                        for (int x = 0; x < width; x++)
                            flat[y][x] = fill;
                }
                return flat;
            }
            if (plane.Length == height && plane[0].Length == width)
                return plane;

            int srcHeight = plane.Length;
            int srcWidth = plane[0].Length;
            var result = new double[height][];
            for (int y = 0; y < height; y++)
            {
                result[y] = new double[width];
                double[] src = plane[Math.Min(y * srcHeight / height, srcHeight - 1)];
                for (int x = 0; x < width; x++)
                    result[y][x] = src[Math.Min(x * srcWidth / width, srcWidth - 1)];
            }
            return result;
        }

        private static byte Clamp(double v)
        {
            if (v <= 0.0)
                return 0;
            if (v >= 255.0)
                return 255;
            return (byte)v;
        }
    }
}
