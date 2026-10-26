using System;
using System.Buffers.Binary;

/*

Decoders for the BC6H (high dynamic range) and BC7 block compression formats, also
known as BPTC. Each encodes a block of 4x4 pixels in 16 bytes, in one of several
modes. Modes may divide the block into two or three subsets, according to one of
64 predefined partitions, and each subset has its own pair of endpoints between
which its colors are interpolated.

Copyright 2026+ Dmitry Brant
https://dmitrybrant.com

License: MIT
*/

namespace DmitryBrant.ImageFormats
{
    internal static class BptcDecoder
    {
        /// <summary>
        /// Decodes a BC7 block into 16 pixels, each four bytes in blue, green, red, alpha order.
        /// </summary>
        public static void DecodeBc7(ReadOnlySpan<byte> block, Span<byte> pixels)
        {
            var bits = new BitReader(block);
            int mode = 0;
            while (mode < 8 && bits.Read(1) == 0)
                mode++;
            if (mode == 8)
            {
                // Reserved; decodes to transparent black.
                pixels[..64].Clear();
                return;
            }

            var m = Bc7Modes[mode];
            int partition = bits.Read(m.PartitionBits);
            int rotation = bits.Read(m.RotationBits);
            int indexSelection = bits.Read(m.IndexSelectionBits);

            int endpointCount = m.Subsets * 2;
            Span<int> endpoints = stackalloc int[6 * 4];
            for (int c = 0; c < 3; c++)
            {
                for (int e = 0; e < endpointCount; e++)
                    endpoints[e * 4 + c] = bits.Read(m.ColorBits);
            }
            for (int e = 0; e < endpointCount; e++)
                endpoints[e * 4 + 3] = bits.Read(m.AlphaBits);

            int colorPrecision = m.ColorBits, alphaPrecision = m.AlphaBits;
            if (m.EndpointPBits || m.SharedPBits)
            {
                Span<int> pBits = stackalloc int[6];
                if (m.EndpointPBits)
                {
                    for (int e = 0; e < endpointCount; e++)
                        pBits[e] = bits.Read(1);
                }
                else
                {
                    for (int s = 0; s < m.Subsets; s++)
                        pBits[s * 2] = pBits[s * 2 + 1] = bits.Read(1);
                }
                for (int e = 0; e < endpointCount; e++)
                {
                    for (int c = 0; c < 4; c++)
                        endpoints[e * 4 + c] = (endpoints[e * 4 + c] << 1) | pBits[e];
                }
                colorPrecision++;
                if (alphaPrecision > 0)
                    alphaPrecision++;
            }
            for (int e = 0; e < endpointCount; e++)
            {
                for (int c = 0; c < 3; c++)
                    endpoints[e * 4 + c] = Expand(endpoints[e * 4 + c], colorPrecision);
                endpoints[e * 4 + 3] = alphaPrecision > 0 ? Expand(endpoints[e * 4 + 3], alphaPrecision) : 255;
            }

            var subsets = Partitions(m.Subsets, partition);
            Span<int> indices = stackalloc int[16];
            Span<int> indices2 = stackalloc int[16];
            for (int i = 0; i < 16; i++)
                indices[i] = bits.Read(m.IndexBits - (IsAnchor(m.Subsets, partition, i) ? 1 : 0));
            if (m.IndexBits2 > 0)
            {
                for (int i = 0; i < 16; i++)
                    indices2[i] = bits.Read(m.IndexBits2 - (i == 0 ? 1 : 0));
            }

            for (int i = 0; i < 16; i++)
            {
                int e0 = subsets[i] * 8, e1 = e0 + 4;
                int colorIndex = indices[i], colorBits = m.IndexBits;
                int alphaIndex = indices[i], alphaBits = m.IndexBits;
                if (m.IndexBits2 > 0)
                {
                    if (indexSelection == 0)
                    {
                        alphaIndex = indices2[i];
                        alphaBits = m.IndexBits2;
                    }
                    else
                    {
                        colorIndex = indices2[i];
                        colorBits = m.IndexBits2;
                    }
                }
                int wc = Weights(colorBits)[colorIndex], wa = Weights(alphaBits)[alphaIndex];
                int r = Interpolate(endpoints[e0], endpoints[e1], wc);
                int g = Interpolate(endpoints[e0 + 1], endpoints[e1 + 1], wc);
                int b = Interpolate(endpoints[e0 + 2], endpoints[e1 + 2], wc);
                int a = Interpolate(endpoints[e0 + 3], endpoints[e1 + 3], wa);
                switch (rotation)
                {
                    case 1: (a, r) = (r, a); break;
                    case 2: (a, g) = (g, a); break;
                    case 3: (a, b) = (b, a); break;
                }
                pixels[i * 4] = (byte)b;
                pixels[i * 4 + 1] = (byte)g;
                pixels[i * 4 + 2] = (byte)r;
                pixels[i * 4 + 3] = (byte)a;
            }
        }

        /// <summary>
        /// Decodes a BC6H block into 16 pixels, each three half-precision floating point
        /// values in red, green, blue order, given as their bits.
        /// </summary>
        public static void DecodeBc6h(ReadOnlySpan<byte> block, Span<ushort> pixels, bool signed)
        {
            var bits = new BitReader(block);
            int code = bits.Read(2);
            if (code >= 2)
                code |= bits.Read(3) << 2;
            int modeIndex = Array.IndexOf(Bc6hModeCodes, code);
            if (modeIndex < 0)
            {
                // Reserved; decodes to zero.
                pixels[..48].Clear();
                return;
            }
            var m = Bc6hModes[modeIndex];

            // The fields, in order: the endpoints w, x, y and z of red, green and blue, then
            // the partition. w and x are the endpoints of the first subset, y and z of the second.
            Span<int> fields = stackalloc int[13];
            foreach (var (field, bit) in m.Layout)
                fields[field] |= bits.Read(1) << bit;
            int partition = fields[12];

            int endpointCount = m.Subsets * 2;
            Span<int> endpoints = stackalloc int[4 * 3];
            for (int c = 0; c < 3; c++)
            {
                int mask = (1 << m.Precision) - 1;
                int w = fields[c];
                if (signed)
                    w = SignExtend(w, m.Precision);
                endpoints[c] = w;
                for (int e = 1; e < endpointCount; e++)
                {
                    int v = fields[e * 3 + c];
                    if (m.Transformed || signed)
                        v = SignExtend(v, m.Transformed ? m.DeltaBits[c] : m.Precision);
                    if (m.Transformed)
                    {
                        v = (v + w) & mask;
                        if (signed)
                            v = SignExtend(v, m.Precision);
                    }
                    endpoints[e * 3 + c] = v;
                }
            }
            for (int i = 0; i < endpointCount * 3; i++)
                endpoints[i] = Unquantize(endpoints[i], m.Precision, signed);

            var subsets = Partitions(m.Subsets, partition);
            int indexBits = m.Subsets == 2 ? 3 : 4;
            var weights = Weights(indexBits);
            bits.Position = m.Subsets == 2 ? 82 : 65;
            for (int i = 0; i < 16; i++)
            {
                int index = bits.Read(indexBits - (IsAnchor(m.Subsets, partition, i) ? 1 : 0));
                int e0 = subsets[i] * 6, e1 = e0 + 3;
                for (int c = 0; c < 3; c++)
                {
                    int v = (endpoints[e0 + c] * (64 - weights[index]) + endpoints[e1 + c] * weights[index] + 32) >> 6;
                    pixels[i * 3 + c] = FinishUnquantize(v, signed);
                }
            }
        }

        private static int Expand(int value, int precision)
        {
            value <<= 8 - precision;
            return value | (value >> precision);
        }

        private static int Interpolate(int e0, int e1, int weight)
        {
            return ((64 - weight) * e0 + weight * e1 + 32) >> 6;
        }

        private static int SignExtend(int value, int bits)
        {
            int shift = 32 - bits;
            return (value << shift) >> shift;
        }

        private static int Unquantize(int value, int bits, bool signed)
        {
            if (!signed)
            {
                if (bits >= 15)
                    return value;
                if (value == 0)
                    return 0;
                if (value == (1 << bits) - 1)
                    return 0xFFFF;
                return ((value << 16) + 0x8000) >> bits;
            }
            if (bits >= 16)
                return value;
            int sign = value < 0 ? -1 : 1;
            value = Math.Abs(value);
            int result;
            if (value == 0)
                result = 0;
            else if (value >= (1 << (bits - 1)) - 1)
                result = 0x7FFF;
            else
                result = ((value << 15) + 0x4000) >> (bits - 1);
            return sign * result;
        }

        private static ushort FinishUnquantize(int value, bool signed)
        {
            if (!signed)
                return (ushort)((value * 31) >> 6);
            value = value < 0 ? -(((-value) * 31) >> 5) : (value * 31) >> 5;
            return value < 0 ? (ushort)(0x8000 | -value) : (ushort)value;
        }

        private static ReadOnlySpan<byte> Partitions(int subsets, int partition)
        {
            return subsets switch
            {
                2 => Partition2.AsSpan(partition * 16, 16),
                3 => Partition3.AsSpan(partition * 16, 16),
                _ => Partition1
            };
        }

        /// <summary>
        /// Whether the given pixel is the anchor of its subset, whose index is stored with
        /// one bit less, since its most significant bit is always 0.
        /// </summary>
        private static bool IsAnchor(int subsets, int partition, int pixel)
        {
            return pixel == 0
                || (subsets == 2 && pixel == Anchor2[partition])
                || (subsets == 3 && (pixel == Anchor3Second[partition] || pixel == Anchor3Third[partition]));
        }

        private static int[] Weights(int bits)
        {
            return bits switch
            {
                2 => Weights2,
                3 => Weights3,
                _ => Weights4
            };
        }

        private ref struct BitReader
        {
            private readonly UInt128 value;
            public int Position;

            public BitReader(ReadOnlySpan<byte> block)
            {
                value = new UInt128(BinaryPrimitives.ReadUInt64LittleEndian(block[8..]), BinaryPrimitives.ReadUInt64LittleEndian(block));
                Position = 0;
            }

            public int Read(int count)
            {
                if (count == 0)
                    return 0;
                int result = (int)(ulong)((value >> Position) & (((UInt128)1 << count) - 1));
                Position += count;
                return result;
            }
        }

        private sealed record Bc7Mode(int Subsets, int PartitionBits, int RotationBits, int IndexSelectionBits,
            int ColorBits, int AlphaBits, bool EndpointPBits, bool SharedPBits, int IndexBits, int IndexBits2);

        private static readonly Bc7Mode[] Bc7Modes =
        {
            new(3, 4, 0, 0, 4, 0, true, false, 3, 0),
            new(2, 6, 0, 0, 6, 0, false, true, 3, 0),
            new(3, 6, 0, 0, 5, 0, false, false, 2, 0),
            new(2, 6, 0, 0, 7, 0, true, false, 2, 0),
            new(1, 0, 2, 1, 5, 6, false, false, 2, 3),
            new(1, 0, 2, 0, 7, 8, false, false, 2, 2),
            new(1, 0, 0, 0, 7, 7, true, false, 4, 0),
            new(2, 6, 0, 0, 5, 5, true, false, 2, 0),
        };

        private sealed record Bc6hMode(int Subsets, bool Transformed, int Precision, int[] DeltaBits, (int Field, int Bit)[] Layout);

        // The values of the mode bits, which are the first 2 bits of the block, or the first
        // 5 if the first 2 are 10 or 11.
        private static readonly int[] Bc6hModeCodes = { 0, 1, 2, 6, 10, 14, 18, 22, 26, 30, 3, 7, 11, 15 };

        // The bits that follow the mode bits. A range such as rw[9:0] is stored from its
        // lowest bit up, and a reversed range such as rw[10:15] from its highest bit down.
        private static readonly Bc6hMode[] Bc6hModes =
        {
            new(2, true, 10, new[] { 5, 5, 5 }, ParseLayout("gy4 by4 bz4 rw9:0 gw9:0 bw9:0 rx4:0 gz4 gy3:0 gx4:0 bz0 gz3:0 bx4:0 bz1 by3:0 ry4:0 bz2 rz4:0 bz3 d4:0")),
            new(2, true, 7, new[] { 6, 6, 6 }, ParseLayout("gy5 gz4 gz5 rw6:0 bz0 bz1 by4 gw6:0 by5 bz2 gy4 bw6:0 bz3 bz5 bz4 rx5:0 gy3:0 gx5:0 gz3:0 bx5:0 by3:0 ry5:0 rz5:0 d4:0")),
            new(2, true, 11, new[] { 5, 4, 4 }, ParseLayout("rw9:0 gw9:0 bw9:0 rx4:0 rw10 gy3:0 gx3:0 gw10 bz0 gz3:0 bx3:0 bw10 bz1 by3:0 ry4:0 bz2 rz4:0 bz3 d4:0")),
            new(2, true, 11, new[] { 4, 5, 4 }, ParseLayout("rw9:0 gw9:0 bw9:0 rx3:0 rw10 gz4 gy3:0 gx4:0 gw10 gz3:0 bx3:0 bw10 bz1 by3:0 ry3:0 bz0 bz2 rz3:0 gy4 bz3 d4:0")),
            new(2, true, 11, new[] { 4, 4, 5 }, ParseLayout("rw9:0 gw9:0 bw9:0 rx3:0 rw10 by4 gy3:0 gx3:0 gw10 bz0 gz3:0 bx4:0 bw10 by3:0 ry3:0 bz1 bz2 rz3:0 bz4 bz3 d4:0")),
            new(2, true, 9, new[] { 5, 5, 5 }, ParseLayout("rw8:0 by4 gw8:0 gy4 bw8:0 bz4 rx4:0 gz4 gy3:0 gx4:0 bz0 gz3:0 bx4:0 bz1 by3:0 ry4:0 bz2 rz4:0 bz3 d4:0")),
            new(2, true, 8, new[] { 6, 5, 5 }, ParseLayout("rw7:0 gz4 by4 gw7:0 bz2 gy4 bw7:0 bz3 bz4 rx5:0 gy3:0 gx4:0 bz0 gz3:0 bx4:0 bz1 by3:0 ry5:0 rz5:0 d4:0")),
            new(2, true, 8, new[] { 5, 6, 5 }, ParseLayout("rw7:0 bz0 by4 gw7:0 gy5 gy4 bw7:0 gz5 bz4 rx4:0 gz4 gy3:0 gx5:0 gz3:0 bx4:0 bz1 by3:0 ry4:0 bz2 rz4:0 bz3 d4:0")),
            new(2, true, 8, new[] { 5, 5, 6 }, ParseLayout("rw7:0 bz1 by4 gw7:0 by5 gy4 bw7:0 bz5 bz4 rx4:0 gz4 gy3:0 gx4:0 bz0 gz3:0 bx5:0 by3:0 ry4:0 bz2 rz4:0 bz3 d4:0")),
            new(2, false, 6, new[] { 6, 6, 6 }, ParseLayout("rw5:0 gz4 bz0 bz1 by4 gw5:0 gy5 by5 bz2 gy4 bw5:0 gz5 bz3 bz5 bz4 rx5:0 gy3:0 gx5:0 gz3:0 bx5:0 by3:0 ry5:0 rz5:0 d4:0")),
            new(1, false, 10, new[] { 10, 10, 10 }, ParseLayout("rw9:0 gw9:0 bw9:0 rx9:0 gx9:0 bx9:0")),
            new(1, true, 11, new[] { 9, 9, 9 }, ParseLayout("rw9:0 gw9:0 bw9:0 rx8:0 rw10 gx8:0 gw10 bx8:0 bw10")),
            new(1, true, 12, new[] { 8, 8, 8 }, ParseLayout("rw9:0 gw9:0 bw9:0 rx7:0 rw10:11 gx7:0 gw10:11 bx7:0 bw10:11")),
            new(1, true, 16, new[] { 4, 4, 4 }, ParseLayout("rw9:0 gw9:0 bw9:0 rx3:0 rw10:15 gx3:0 gw10:15 bx3:0 bw10:15")),
        };

        private static (int, int)[] ParseLayout(string layout)
        {
            const string fieldNames = "rwgwbwrxgxbxrygybyrzgzbzd";
            var result = new System.Collections.Generic.List<(int, int)>();
            foreach (var item in layout.Split(' '))
            {
                int nameLength = item[0] == 'd' ? 1 : 2;
                int field = fieldNames.IndexOf(item[..nameLength], StringComparison.Ordinal) / 2;
                var range = item[nameLength..].Split(':');
                int first = int.Parse(range[0]);
                int last = range.Length > 1 ? int.Parse(range[1]) : first;
                int step = first >= last ? 1 : -1;
                for (int bit = last; ; bit += step)
                {
                    result.Add((field, bit));
                    if (bit == first)
                        break;
                }
            }
            return result.ToArray();
        }

        private static readonly int[] Weights2 = { 0, 21, 43, 64 };
        private static readonly int[] Weights3 = { 0, 9, 18, 27, 37, 46, 55, 64 };
        private static readonly int[] Weights4 = { 0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64 };

        private static readonly byte[] Partition1 = new byte[16];

        private static readonly byte[] Partition2 =
        {
            0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1,
            0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1,
            0, 1, 1, 1, 0, 1, 1, 1, 0, 1, 1, 1, 0, 1, 1, 1,
            0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 1, 1, 1,
            0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 1, 1,
            0, 0, 1, 1, 0, 1, 1, 1, 0, 1, 1, 1, 1, 1, 1, 1,
            0, 0, 0, 1, 0, 0, 1, 1, 0, 1, 1, 1, 1, 1, 1, 1,
            0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 1, 1, 0, 1, 1, 1,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 1, 1,
            0, 0, 1, 1, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
            0, 0, 0, 0, 0, 0, 0, 1, 0, 1, 1, 1, 1, 1, 1, 1,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 1, 1, 1,
            0, 0, 0, 1, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
            0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1,
            0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1,
            0, 0, 0, 0, 1, 0, 0, 0, 1, 1, 1, 0, 1, 1, 1, 1,
            0, 1, 1, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 1, 1, 0,
            0, 1, 1, 1, 0, 0, 1, 1, 0, 0, 0, 1, 0, 0, 0, 0,
            0, 0, 1, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 1, 0, 0, 0, 1, 1, 0, 0, 1, 1, 1, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 1, 0, 0,
            0, 1, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 0, 1,
            0, 0, 1, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0,
            0, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 1, 0, 0,
            0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0,
            0, 0, 1, 1, 0, 1, 1, 0, 0, 1, 1, 0, 1, 1, 0, 0,
            0, 0, 0, 1, 0, 1, 1, 1, 1, 1, 1, 0, 1, 0, 0, 0,
            0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0,
            0, 1, 1, 1, 0, 0, 0, 1, 1, 0, 0, 0, 1, 1, 1, 0,
            0, 0, 1, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 1, 0, 0,
            0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1,
            0, 0, 0, 0, 1, 1, 1, 1, 0, 0, 0, 0, 1, 1, 1, 1,
            0, 1, 0, 1, 1, 0, 1, 0, 0, 1, 0, 1, 1, 0, 1, 0,
            0, 0, 1, 1, 0, 0, 1, 1, 1, 1, 0, 0, 1, 1, 0, 0,
            0, 0, 1, 1, 1, 1, 0, 0, 0, 0, 1, 1, 1, 1, 0, 0,
            0, 1, 0, 1, 0, 1, 0, 1, 1, 0, 1, 0, 1, 0, 1, 0,
            0, 1, 1, 0, 1, 0, 0, 1, 0, 1, 1, 0, 1, 0, 0, 1,
            0, 1, 0, 1, 1, 0, 1, 0, 1, 0, 1, 0, 0, 1, 0, 1,
            0, 1, 1, 1, 0, 0, 1, 1, 1, 1, 0, 0, 1, 1, 1, 0,
            0, 0, 0, 1, 0, 0, 1, 1, 1, 1, 0, 0, 1, 0, 0, 0,
            0, 0, 1, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 1, 0, 0,
            0, 0, 1, 1, 1, 0, 1, 1, 1, 1, 0, 1, 1, 1, 0, 0,
            0, 1, 1, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0, 1, 1, 0,
            0, 0, 1, 1, 1, 1, 0, 0, 1, 1, 0, 0, 0, 0, 1, 1,
            0, 1, 1, 0, 0, 1, 1, 0, 1, 0, 0, 1, 1, 0, 0, 1,
            0, 0, 0, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 0, 0, 0,
            0, 1, 0, 0, 1, 1, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0,
            0, 0, 1, 0, 0, 1, 1, 1, 0, 0, 1, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 1, 0, 0, 1, 1, 1, 0, 0, 1, 0,
            0, 0, 0, 0, 0, 1, 0, 0, 1, 1, 1, 0, 0, 1, 0, 0,
            0, 1, 1, 0, 1, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 1,
            0, 0, 1, 1, 0, 1, 1, 0, 1, 1, 0, 0, 1, 0, 0, 1,
            0, 1, 1, 0, 0, 0, 1, 1, 1, 0, 0, 1, 1, 1, 0, 0,
            0, 0, 1, 1, 1, 0, 0, 1, 1, 1, 0, 0, 0, 1, 1, 0,
            0, 1, 1, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 0, 0, 1,
            0, 1, 1, 0, 0, 0, 1, 1, 0, 0, 1, 1, 1, 0, 0, 1,
            0, 1, 1, 1, 1, 1, 1, 0, 1, 0, 0, 0, 0, 0, 0, 1,
            0, 0, 0, 1, 1, 0, 0, 0, 1, 1, 1, 0, 0, 1, 1, 1,
            0, 0, 0, 0, 1, 1, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1,
            0, 0, 1, 1, 0, 0, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0,
            0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 1, 0, 1, 1, 1, 0,
            0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 1, 1, 0, 1, 1, 1,
        };

        private static readonly byte[] Partition3 =
        {
            0, 0, 1, 1, 0, 0, 1, 1, 0, 2, 2, 1, 2, 2, 2, 2,
            0, 0, 0, 1, 0, 0, 1, 1, 2, 2, 1, 1, 2, 2, 2, 1,
            0, 0, 0, 0, 2, 0, 0, 1, 2, 2, 1, 1, 2, 2, 1, 1,
            0, 2, 2, 2, 0, 0, 2, 2, 0, 0, 1, 1, 0, 1, 1, 1,
            0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 2, 2, 1, 1, 2, 2,
            0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 2, 2, 0, 0, 2, 2,
            0, 0, 2, 2, 0, 0, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1,
            0, 0, 1, 1, 0, 0, 1, 1, 2, 2, 1, 1, 2, 2, 1, 1,
            0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2,
            0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2,
            0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2,
            0, 0, 1, 2, 0, 0, 1, 2, 0, 0, 1, 2, 0, 0, 1, 2,
            0, 1, 1, 2, 0, 1, 1, 2, 0, 1, 1, 2, 0, 1, 1, 2,
            0, 1, 2, 2, 0, 1, 2, 2, 0, 1, 2, 2, 0, 1, 2, 2,
            0, 0, 1, 1, 0, 1, 1, 2, 1, 1, 2, 2, 1, 2, 2, 2,
            0, 0, 1, 1, 2, 0, 0, 1, 2, 2, 0, 0, 2, 2, 2, 0,
            0, 0, 0, 1, 0, 0, 1, 1, 0, 1, 1, 2, 1, 1, 2, 2,
            0, 1, 1, 1, 0, 0, 1, 1, 2, 0, 0, 1, 2, 2, 0, 0,
            0, 0, 0, 0, 1, 1, 2, 2, 1, 1, 2, 2, 1, 1, 2, 2,
            0, 0, 2, 2, 0, 0, 2, 2, 0, 0, 2, 2, 1, 1, 1, 1,
            0, 1, 1, 1, 0, 1, 1, 1, 0, 2, 2, 2, 0, 2, 2, 2,
            0, 0, 0, 1, 0, 0, 0, 1, 2, 2, 2, 1, 2, 2, 2, 1,
            0, 0, 0, 0, 0, 0, 1, 1, 0, 1, 2, 2, 0, 1, 2, 2,
            0, 0, 0, 0, 1, 1, 0, 0, 2, 2, 1, 0, 2, 2, 1, 0,
            0, 1, 2, 2, 0, 1, 2, 2, 0, 0, 1, 1, 0, 0, 0, 0,
            0, 0, 1, 2, 0, 0, 1, 2, 1, 1, 2, 2, 2, 2, 2, 2,
            0, 1, 1, 0, 1, 2, 2, 1, 1, 2, 2, 1, 0, 1, 1, 0,
            0, 0, 0, 0, 0, 1, 1, 0, 1, 2, 2, 1, 1, 2, 2, 1,
            0, 0, 2, 2, 1, 1, 0, 2, 1, 1, 0, 2, 0, 0, 2, 2,
            0, 1, 1, 0, 0, 1, 1, 0, 2, 0, 0, 2, 2, 2, 2, 2,
            0, 0, 1, 1, 0, 1, 2, 2, 0, 1, 2, 2, 0, 0, 1, 1,
            0, 0, 0, 0, 2, 0, 0, 0, 2, 2, 1, 1, 2, 2, 2, 1,
            0, 0, 0, 0, 0, 0, 0, 2, 1, 1, 2, 2, 1, 2, 2, 2,
            0, 2, 2, 2, 0, 0, 2, 2, 0, 0, 1, 2, 0, 0, 1, 1,
            0, 0, 1, 1, 0, 0, 1, 2, 0, 0, 2, 2, 0, 2, 2, 2,
            0, 1, 2, 0, 0, 1, 2, 0, 0, 1, 2, 0, 0, 1, 2, 0,
            0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 0, 0, 0, 0,
            0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0,
            0, 1, 2, 0, 2, 0, 1, 2, 1, 2, 0, 1, 0, 1, 2, 0,
            0, 0, 1, 1, 2, 2, 0, 0, 1, 1, 2, 2, 0, 0, 1, 1,
            0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 0, 0, 0, 0, 1, 1,
            0, 1, 0, 1, 0, 1, 0, 1, 2, 2, 2, 2, 2, 2, 2, 2,
            0, 0, 0, 0, 0, 0, 0, 0, 2, 1, 2, 1, 2, 1, 2, 1,
            0, 0, 2, 2, 1, 1, 2, 2, 0, 0, 2, 2, 1, 1, 2, 2,
            0, 0, 2, 2, 0, 0, 1, 1, 0, 0, 2, 2, 0, 0, 1, 1,
            0, 2, 2, 0, 1, 2, 2, 1, 0, 2, 2, 0, 1, 2, 2, 1,
            0, 1, 0, 1, 2, 2, 2, 2, 2, 2, 2, 2, 0, 1, 0, 1,
            0, 0, 0, 0, 2, 1, 2, 1, 2, 1, 2, 1, 2, 1, 2, 1,
            0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 2, 2, 2, 2,
            0, 2, 2, 2, 0, 1, 1, 1, 0, 2, 2, 2, 0, 1, 1, 1,
            0, 0, 0, 2, 1, 1, 1, 2, 0, 0, 0, 2, 1, 1, 1, 2,
            0, 0, 0, 0, 2, 1, 1, 2, 2, 1, 1, 2, 2, 1, 1, 2,
            0, 2, 2, 2, 0, 1, 1, 1, 0, 1, 1, 1, 0, 2, 2, 2,
            0, 0, 0, 2, 1, 1, 1, 2, 1, 1, 1, 2, 0, 0, 0, 2,
            0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 2, 2, 2, 2,
            0, 0, 0, 0, 0, 0, 0, 0, 2, 1, 1, 2, 2, 1, 1, 2,
            0, 1, 1, 0, 0, 1, 1, 0, 2, 2, 2, 2, 2, 2, 2, 2,
            0, 0, 2, 2, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 2, 2,
            0, 0, 2, 2, 1, 1, 2, 2, 1, 1, 2, 2, 0, 0, 2, 2,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 1, 1, 2,
            0, 0, 0, 2, 0, 0, 0, 1, 0, 0, 0, 2, 0, 0, 0, 1,
            0, 2, 2, 2, 1, 2, 2, 2, 0, 2, 2, 2, 1, 2, 2, 2,
            0, 1, 0, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
            0, 1, 1, 1, 2, 0, 1, 1, 2, 2, 0, 1, 2, 2, 2, 0,
        };

        private static readonly byte[] Anchor2 =
        {
            15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15,
            15, 2, 8, 2, 2, 8, 8, 15, 2, 8, 2, 2, 8, 8, 2, 2,
            15, 15, 6, 8, 2, 8, 15, 15, 2, 8, 2, 2, 2, 15, 15, 6,
            6, 2, 6, 8, 15, 15, 2, 2, 15, 15, 15, 15, 15, 2, 2, 15,
        };

        private static readonly byte[] Anchor3Second =
        {
            3, 3, 15, 15, 8, 3, 15, 15, 8, 8, 6, 6, 6, 5, 3, 3,
            3, 3, 8, 15, 3, 3, 6, 10, 5, 8, 8, 6, 8, 5, 15, 15,
            8, 15, 3, 5, 6, 10, 8, 15, 15, 3, 15, 5, 15, 15, 15, 15,
            3, 15, 5, 5, 5, 8, 5, 10, 5, 10, 8, 13, 15, 12, 3, 3,
        };

        private static readonly byte[] Anchor3Third =
        {
            15, 8, 8, 3, 15, 15, 3, 8, 15, 15, 15, 15, 15, 15, 15, 8,
            15, 8, 15, 3, 15, 8, 15, 8, 3, 15, 6, 10, 15, 15, 10, 8,
            15, 3, 15, 10, 10, 8, 9, 10, 6, 15, 8, 15, 3, 6, 6, 8,
            15, 3, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 3, 15, 15, 8,
        };
    }
}
