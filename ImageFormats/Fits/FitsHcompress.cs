using System;

/*

Decoder for HCOMPRESS, one of the tile compression algorithms used by
tile-compressed FITS images (ZCMPTYPE = 'HCOMPRESS_1'), as implemented by
CFITSIO (originally by Richard White at STScI).

The encoder applies the "H-transform" to the tile: a two-dimensional Haar
wavelet transform, in which each 2x2 block of pixels is replaced by its sum
(h0), its x and y differences (hx, hy) and its curvature (hc), repeatedly on the
sums until only one remains. The coefficients are optionally divided by a scale
factor (which makes the compression lossy), and then coded one bit plane at a
time, from the most significant down, separately for each of the four quadrants
of the transformed array (the sums, the x differences, the y differences and the
curvatures). Each bit plane is either written directly, four bits per nybble,
or coded as a quadtree: starting from a single node, each nonzero 4-bit node
says which of its four children are nonzero, down to the pixels themselves,
with the node values Huffman coded. The signs of the nonzero coefficients come
at the end.

Decoding reverses these steps. If the image was scaled, the inverse transform
can optionally "smooth" the result, by adjusting the coefficients (within the
precision lost to the scaling) to make the image as smooth as possible, which
reduces blockiness.

The stream starts with the magic code 0xDD 0x99, followed by the dimensions of
the tile (the slowly varying one first), the scale factor, the sum of all the
pixels (a 64-bit value) and the number of bit planes in each quadrant.

Copyright 2026+ Dmitry Brant
https://dmitrybrant.com

License: MIT
*/

namespace DmitryBrant.ImageFormats
{
    internal static class FitsHcompress
    {
        /// <summary>
        /// Decodes an HCOMPRESS-compressed tile.
        /// </summary>
        /// <param name="data">The compressed data.</param>
        /// <param name="tileWidth">Size of the tile along the first (fastest varying) axis.</param>
        /// <param name="tileHeight">Size of the tile along the second axis.</param>
        /// <param name="smooth">Whether to smooth the image during the inverse transform.</param>
        /// <param name="wide">Whether to use 64-bit arithmetic (for 64-bit images). Otherwise the
        /// arithmetic is done in 32 bits, overflowing just as it does in CFITSIO, so that the
        /// (lossy) results are exactly the same.</param>
        /// <returns>The tile's pixel values, tileWidth * tileHeight of them, row by row.</returns>
        public static long[] Decode(byte[] data, int tileWidth, int tileHeight, bool smooth, bool wide)
        {
            try
            {
                return DecodeTile(data, tileWidth, tileHeight, smooth, wide);
            }
            catch (Exception e) when (e is IndexOutOfRangeException || e is OverflowException || e is OutOfMemoryException)
            {
                throw new ImageDecodeException("Invalid HCOMPRESS data.");
            }
        }

        private static long[] DecodeTile(byte[] data, int tileWidth, int tileHeight, bool smooth, bool wide)
        {
            var input = new BitInput(data);
            if (data.Length < 2 || data[0] != 0xDD || data[1] != 0x99)
                throw new ImageDecodeException("Invalid HCOMPRESS data.");
            input.Position = 2;

            // The stream's first dimension is the slowly varying one, i.e. the tile's
            // height, so the decoded array comes out in the usual row-by-row order.
            int nx = input.ReadInt32();
            int ny = input.ReadInt32();
            int scale = input.ReadInt32();
            if (nx != tileHeight || ny != tileWidth || nx <= 0 || ny <= 0)
                throw new ImageDecodeException("HCOMPRESS tile has unexpected dimensions.");
            long sumAll = input.ReadInt64();
            int planes0 = input.ReadByte(), planes1 = input.ReadByte(), planes2 = input.ReadByte();
            if (planes0 > 64 || planes1 > 64 || planes2 > 64)
                throw new ImageDecodeException("Invalid HCOMPRESS data.");

            var a = new long[(long)nx * ny];
            DecodeBitPlanes(input, a, nx, ny, planes0, planes1, planes2);

            // The sum of all pixels goes back into the first coefficient.
            a[0] = Wrap(sumAll, wide);

            // Undo the scaling.
            if (scale > 1)
            {
                for (int i = 0; i < a.Length; i++)
                    a[i] = Wrap(a[i] * scale, wide);
            }

            InverseTransform(a, nx, ny, smooth, scale, wide);
            return a;
        }

        #region Bit input

        private sealed class BitInput
        {
            private readonly byte[] data;
            private int buffer, bitsToGo;
            public int Position;

            public BitInput(byte[] data)
            {
                this.data = data;
            }

            public int ReadByte()
            {
                if (Position >= data.Length)
                    throw new ImageDecodeException("HCOMPRESS data is truncated.");
                return data[Position++];
            }

            public int ReadInt32()
            {
                int v = 0;
                for (int i = 0; i < 4; i++)
                    v = (v << 8) | ReadByte();
                return v;
            }

            public long ReadInt64()
            {
                long v = 0;
                for (int i = 0; i < 8; i++)
                    v = (v << 8) | (long)ReadByte();
                return v;
            }

            /// <summary>Discards any bits left in the current byte.</summary>
            public void StartBits()
            {
                bitsToGo = 0;
            }

            public int ReadBit()
            {
                if (bitsToGo == 0)
                {
                    buffer = ReadByte();
                    bitsToGo = 8;
                }
                bitsToGo--;
                return (buffer >> bitsToGo) & 1;
            }

            public int ReadBits(int n)
            {
                if (bitsToGo < n)
                {
                    buffer = (buffer << 8) | ReadByte();
                    bitsToGo += 8;
                }
                bitsToGo -= n;
                return (buffer >> bitsToGo) & ((1 << n) - 1);
            }

            public int ReadNybble()
            {
                return ReadBits(4);
            }

            /// <summary>
            /// Reads a Huffman-coded quadtree node value.
            /// </summary>
            public int ReadHuffman()
            {
                int c = ReadBits(3);
                if (c < 4)
                    return 1 << c;   // 1, 2, 4, 8 for c = 0, 1, 2, 3
                c = ReadBit() | (c << 1);
                switch (c)
                {
                    case 8: return 3;
                    case 9: return 5;
                    case 10: return 10;
                    case 11: return 12;
                    case 12: return 15;
                }
                c = ReadBit() | (c << 1);
                switch (c)
                {
                    case 26: return 6;
                    case 27: return 7;
                    case 28: return 9;
                    case 29: return 11;
                    case 30: return 13;
                }
                c = ReadBit() | (c << 1);
                return c == 62 ? 0 : 14;
            }
        }

        #endregion

        #region Bit plane decoding

        private static void DecodeBitPlanes(BitInput input, long[] a, int nx, int ny, int planes0, int planes1, int planes2)
        {
            int nx2 = (nx + 1) / 2, ny2 = (ny + 1) / 2;
            input.StartBits();

            // The four quadrants of the transformed array: sums, then x differences, y
            // differences, and curvatures. The x and y differences share a bit plane count.
            DecodeQuadtree(input, a, 0, ny, nx2, ny2, planes0);
            DecodeQuadtree(input, a, ny2, ny, nx2, ny / 2, planes1);
            DecodeQuadtree(input, a, ny * nx2, ny, nx / 2, ny2, planes1);
            DecodeQuadtree(input, a, ny * nx2 + ny2, ny, nx / 2, ny / 2, planes2);

            // The bit planes end with a zero nybble.
            if (input.ReadNybble() != 0)
                throw new ImageDecodeException("Invalid HCOMPRESS bit planes.");

            // Then the signs of the nonzero coefficients.
            input.StartBits();
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != 0 && input.ReadBit() != 0)
                    a[i] = -a[i];
            }
        }

        private static int CeilLog2(int n)
        {
            int log2n = 0;
            while ((1 << log2n) < n)
                log2n++;
            return log2n;
        }

        /// <summary>
        /// Decodes the bit planes of one quadrant, of nqx by nqy coefficients, starting at
        /// offset in a, whose rows are n long.
        /// </summary>
        private static void DecodeQuadtree(BitInput input, long[] a, int offset, int n, int nqx, int nqy, int numPlanes)
        {
            int log2n = CeilLog2(Math.Max(nqx, nqy));
            int nqx2 = (nqx + 1) / 2, nqy2 = (nqy + 1) / 2;
            var scratch = new byte[Math.Max(1, nqx2 * nqy2)];

            for (int bit = numPlanes - 1; bit >= 0; bit--)
            {
                int format = input.ReadNybble();
                if (format == 0)
                {
                    // The bit plane is written directly, four bits per nybble.
                    for (int i = 0; i < nqx2 * nqy2; i++)
                        scratch[i] = (byte)input.ReadNybble();
                }
                else if (format == 0xF)
                {
                    // The bit plane is quadtree coded: expand from a single node, log2n times.
                    scratch[0] = (byte)input.ReadHuffman();
                    int nx = 1, ny = 1, nfx = nqx, nfy = nqy;
                    int c = 1 << log2n;
                    for (int k = 1; k < log2n; k++)
                    {
                        // This generates the sequence n[k - 1] = (n[k] + 1) / 2, where
                        // n[log2n] is nqx or nqy.
                        c >>= 1;
                        nx <<= 1;
                        ny <<= 1;
                        if (nfx <= c)
                            nx--;
                        else
                            nfx -= c;
                        if (nfy <= c)
                            ny--;
                        else
                            nfy -= c;
                        ExpandQuadtree(input, scratch, nx, ny);
                    }
                }
                else
                {
                    throw new ImageDecodeException("Invalid HCOMPRESS bit plane format.");
                }
                InsertBits(scratch, nqx, nqy, a, offset, n, bit);
            }
        }

        /// <summary>
        /// Expands the 4-bit nodes of a (nx + 1) / 2 by (ny + 1) / 2 level of the quadtree
        /// into the nx by ny level below it, and reads the values of the nonzero nodes.
        /// </summary>
        private static void ExpandQuadtree(BitInput input, byte[] b, int nx, int ny)
        {
            // Spread each 4-bit node over the 2x2 block it covers. Work backwards, since
            // the source and destination are the same array.
            int nx2 = (nx + 1) / 2, ny2 = (ny + 1) / 2;
            int k = ny2 * (nx2 - 1) + ny2 - 1;
            for (int i = nx2 - 1; i >= 0; i--)
            {
                int s00 = 2 * (ny * i + ny2 - 1);
                for (int j = ny2 - 1; j >= 0; j--)
                {
                    b[s00] = b[k];
                    k--;
                    s00 -= 2;
                }
            }

            // Now each node's four bits become the four pixels of its block: bit 3 is
            // [i, j], bit 2 is [i, j + 1], bit 1 is [i + 1, j], and bit 0 is [i + 1, j + 1].
            int ii;
            for (ii = 0; ii < nx - 1; ii += 2)
            {
                int s00 = ny * ii, s10 = s00 + ny;
                int j;
                for (j = 0; j < ny - 1; j += 2)
                {
                    int v = b[s00];
                    b[s10 + 1] = (byte)(v & 1);
                    b[s10] = (byte)((v >> 1) & 1);
                    b[s00 + 1] = (byte)((v >> 2) & 1);
                    b[s00] = (byte)((v >> 3) & 1);
                    s00 += 2;
                    s10 += 2;
                }
                if (j < ny)
                {
                    // Odd row length: the last element of the row.
                    int v = b[s00];
                    b[s10] = (byte)((v >> 1) & 1);
                    b[s00] = (byte)((v >> 3) & 1);
                }
            }
            if (ii < nx)
            {
                // Odd column length: the last row.
                int s00 = ny * ii;
                int j;
                for (j = 0; j < ny - 1; j += 2)
                {
                    int v = b[s00];
                    b[s00 + 1] = (byte)((v >> 2) & 1);
                    b[s00] = (byte)((v >> 3) & 1);
                    s00 += 2;
                }
                if (j < ny)
                    b[s00] = (byte)((b[s00] >> 3) & 1);
            }

            // Read new values for the nonzero nodes.
            for (int i = nx * ny - 1; i >= 0; i--)
            {
                if (b[i] != 0)
                    b[i] = (byte)input.ReadHuffman();
            }
        }

        /// <summary>
        /// Expands the 4-bit values of a (nx + 1) / 2 by (ny + 1) / 2 array into the nx by
        /// ny quadrant at offset in b (whose rows are n long), setting bit plane bit.
        /// </summary>
        private static void InsertBits(byte[] a, int nx, int ny, long[] b, int offset, int n, int bit)
        {
            long planeValue = 1L << bit;
            int k = 0;
            int i;
            for (i = 0; i < nx - 1; i += 2)
            {
                int s00 = offset + n * i;
                int j;
                for (j = 0; j < ny - 1; j += 2)
                {
                    int v = a[k++];
                    if ((v & 1) != 0) b[s00 + n + 1] |= planeValue;
                    if ((v & 2) != 0) b[s00 + n] |= planeValue;
                    if ((v & 4) != 0) b[s00 + 1] |= planeValue;
                    if ((v & 8) != 0) b[s00] |= planeValue;
                    s00 += 2;
                }
                if (j < ny)
                {
                    int v = a[k++];
                    if ((v & 2) != 0) b[s00 + n] |= planeValue;
                    if ((v & 8) != 0) b[s00] |= planeValue;
                }
            }
            if (i < nx)
            {
                int s00 = offset + n * i;
                int j;
                for (j = 0; j < ny - 1; j += 2)
                {
                    int v = a[k++];
                    if ((v & 4) != 0) b[s00 + 1] |= planeValue;
                    if ((v & 8) != 0) b[s00] |= planeValue;
                    s00 += 2;
                }
                if (j < ny)
                {
                    int v = a[k++];
                    if ((v & 8) != 0) b[s00] |= planeValue;
                }
            }
        }

        #endregion

        #region Inverse H-transform

        /// <summary>
        /// Inverts the H-transform of the nx by ny array a, in place.
        /// </summary>
        /// <summary>
        /// Truncates a value to 32 bits, unless doing 64-bit arithmetic.
        /// </summary>
        private static long Wrap(long v, bool wide)
        {
            return wide ? v : unchecked((int)v);
        }

        private static void InverseTransform(long[] a, int nx, int ny, bool smooth, int scale, bool wide)
        {
            int nmax = Math.Max(nx, ny);
            int log2n = CeilLog2(nmax);
            if (log2n == 0)
                return; // a single pixel, which is its own sum

            var tmp = new long[(nmax + 1) / 2];

            // Masks and rounding values for the current level.
            int shift = 1;
            long bit0 = 1L << (log2n - 1);
            long bit1 = bit0 << 1;
            long bit2 = bit0 << 2;
            long mask0 = -bit0;
            long mask1 = mask0 << 1;
            long mask2 = mask0 << 2;
            long prnd0 = bit0 >> 1;
            long prnd1 = bit1 >> 1;
            long prnd2 = bit2 >> 1;
            long nrnd0 = prnd0 - 1;
            long nrnd1 = prnd1 - 1;
            long nrnd2 = prnd2 - 1;

            // Round h0 to a multiple of bit2.
            a[0] = Wrap(Wrap(a[0] + (a[0] >= 0 ? prnd2 : nrnd2), wide) & mask2, wide);

            int nxtop = 1, nytop = 1, nxf = nx, nyf = ny;
            int c = 1 << log2n;
            for (int k = log2n - 1; k >= 0; k--)
            {
                // This generates the sequence ntop[k - 1] = (ntop[k] + 1) / 2, where
                // ntop[0] is nmax.
                c >>= 1;
                nxtop <<= 1;
                nytop <<= 1;
                if (nxf <= c)
                    nxtop--;
                else
                    nxf -= c;
                if (nyf <= c)
                    nytop--;
                else
                    nyf -= c;

                // The last pass divides by 4 rather than 2.
                if (k == 0)
                {
                    nrnd0 = 0;
                    shift = 2;
                }

                // Unshuffle in each dimension, to interleave the coefficients.
                for (int i = 0; i < nxtop; i++)
                    Unshuffle(a, ny * i, nytop, 1, tmp);
                for (int j = 0; j < nytop; j++)
                    Unshuffle(a, j, nxtop, ny, tmp);

                if (smooth)
                    Smooth(a, nxtop, nytop, ny, scale, wide);

                int oddx = nxtop % 2, oddy = nytop % 2;
                int ii;
                for (ii = 0; ii < nxtop - oddx; ii += 2)
                {
                    int s00 = ny * ii, s10 = s00 + ny;
                    for (int j = 0; j < nytop - oddy; j += 2)
                    {
                        long h0 = a[s00], hx = a[s10], hy = a[s00 + 1], hc = a[s10 + 1];

                        // Round hx and hy to multiples of bit1, and hc to a multiple of bit0;
                        // h0 is already a multiple of bit2.
                        hx = Wrap(Wrap(hx + (hx >= 0 ? prnd1 : nrnd1), wide) & mask1, wide);
                        hy = Wrap(Wrap(hy + (hy >= 0 ? prnd1 : nrnd1), wide) & mask1, wide);
                        hc = Wrap(Wrap(hc + (hc >= 0 ? prnd0 : nrnd0), wide) & mask0, wide);

                        // Propagate bit 0 of hc to hx and hy.
                        long lowbit0 = hc & bit0;
                        hx = Wrap(hx >= 0 ? hx - lowbit0 : hx + lowbit0, wide);
                        hy = Wrap(hy >= 0 ? hy - lowbit0 : hy + lowbit0, wide);

                        // Propagate bits 0 and 1 of hc, hx and hy to h0.
                        long lowbit1 = (hc ^ hx ^ hy) & bit1;
                        h0 = Wrap(h0 >= 0
                            ? h0 + lowbit0 - lowbit1
                            : h0 + (lowbit0 == 0 ? lowbit1 : lowbit0 - lowbit1), wide);

                        a[s10 + 1] = Wrap(h0 + hx + hy + hc, wide) >> shift;
                        a[s10] = Wrap(h0 + hx - hy - hc, wide) >> shift;
                        a[s00 + 1] = Wrap(h0 - hx + hy - hc, wide) >> shift;
                        a[s00] = Wrap(h0 - hx - hy + hc, wide) >> shift;
                        s00 += 2;
                        s10 += 2;
                    }
                    if (oddy != 0)
                    {
                        // Odd row length: the last element of the row.
                        long h0 = a[s00], hx = a[s10];
                        hx = Wrap(Wrap(hx >= 0 ? hx + prnd1 : hx + nrnd1, wide) & mask1, wide);
                        long lowbit1 = hx & bit1;
                        h0 = Wrap(h0 >= 0 ? h0 - lowbit1 : h0 + lowbit1, wide);
                        a[s10] = Wrap(h0 + hx, wide) >> shift;
                        a[s00] = Wrap(h0 - hx, wide) >> shift;
                    }
                }
                if (oddx != 0)
                {
                    // Odd column length: the last row.
                    int s00 = ny * ii;
                    for (int j = 0; j < nytop - oddy; j += 2)
                    {
                        long h0 = a[s00], hy = a[s00 + 1];
                        hy = Wrap(Wrap(hy >= 0 ? hy + prnd1 : hy + nrnd1, wide) & mask1, wide);
                        long lowbit1 = hy & bit1;
                        h0 = Wrap(h0 >= 0 ? h0 - lowbit1 : h0 + lowbit1, wide);
                        a[s00 + 1] = Wrap(h0 + hy, wide) >> shift;
                        a[s00] = Wrap(h0 - hy, wide) >> shift;
                        s00 += 2;
                    }
                    if (oddy != 0)
                        a[s00] >>= shift; // the corner element
                }

                // Halve all the masks and rounding values for the next level.
                bit2 = bit1;
                bit1 = bit0;
                bit0 >>= 1;
                mask1 = mask0;
                mask0 >>= 1;
                prnd1 = prnd0;
                prnd0 >>= 1;
                nrnd1 = nrnd0;
                nrnd0 = prnd0 - 1;
            }
        }

        /// <summary>
        /// Moves the first half of the n elements of a (starting at offset, n2 apart) to
        /// the even positions, and the second half to the odd ones.
        /// </summary>
        private static void Unshuffle(long[] a, int offset, int n, int n2, long[] tmp)
        {
            int nhalf = (n + 1) >> 1;
            for (int i = nhalf, p = offset + n2 * nhalf; i < n; i++, p += n2)
                tmp[i - nhalf] = a[p];
            for (int i = nhalf - 1; i >= 0; i--)
                a[offset + 2 * n2 * i] = a[offset + n2 * i];
            for (int i = 1, t = 0; i < n; i += 2, t++)
                a[offset + n2 * i] = tmp[t];
        }

        /// <summary>
        /// Adjusts the differences in the nxtop by nytop block of coefficients to make the
        /// image smoother, while staying within the precision lost to the scale factor.
        /// </summary>
        private static void Smooth(long[] a, int nxtop, int nytop, int ny, int scale, bool wide)
        {
            // Since the encoder rounded when dividing by the scale factor, the biggest
            // permitted change is scale / 2.
            long smax = scale >> 1;
            if (smax <= 0)
                return;
            int ny2 = ny << 1;

            // The coefficients at the edges aren't adjusted. (In 32-bit mode, every
            // intermediate result is wrapped as CFITSIO's arithmetic would be, before it's
            // compared or shifted right.)

            // Adjust the x differences hx.
            for (int i = 2; i < nxtop - 2; i += 2)
            {
                int s00 = ny * i, s10 = s00 + ny;
                for (int j = 0; j < nytop; j += 2)
                {
                    // hm and hp are the sums (h0) of the previous and next zones in x.
                    long hm = a[s00 - ny2], h0 = a[s00], hp = a[s00 + ny2];
                    // diff is 8 * the hx slope that would match the neighboring zones.
                    long diff = Wrap(hp - hm, wide);
                    // Monotonicity constraints on diff
                    long dmax = Wrap(Math.Max(Math.Min(Wrap(hp - h0, wide), Wrap(h0 - hm, wide)), 0) << 2, wide);
                    long dmin = Wrap(Math.Min(Math.Max(Wrap(hp - h0, wide), Wrap(h0 - hm, wide)), 0) << 2, wide);
                    if (dmin < dmax)
                    {
                        diff = Math.Max(Math.Min(diff, dmax), dmin);
                        long s = Wrap(diff - Wrap(a[s10] << 3, wide), wide);
                        s = s >= 0 ? s >> 3 : Wrap(s + 7, wide) >> 3;
                        s = Math.Max(Math.Min(s, smax), -smax);
                        a[s10] = Wrap(a[s10] + s, wide);
                    }
                    s00 += 2;
                    s10 += 2;
                }
            }

            // Adjust the y differences hy.
            for (int i = 0; i < nxtop; i += 2)
            {
                int s00 = ny * i + 2;
                for (int j = 2; j < nytop - 2; j += 2)
                {
                    long hm = a[s00 - 2], h0 = a[s00], hp = a[s00 + 2];
                    long diff = Wrap(hp - hm, wide);
                    long dmax = Wrap(Math.Max(Math.Min(Wrap(hp - h0, wide), Wrap(h0 - hm, wide)), 0) << 2, wide);
                    long dmin = Wrap(Math.Min(Math.Max(Wrap(hp - h0, wide), Wrap(h0 - hm, wide)), 0) << 2, wide);
                    if (dmin < dmax)
                    {
                        diff = Math.Max(Math.Min(diff, dmax), dmin);
                        long s = Wrap(diff - Wrap(a[s00 + 1] << 3, wide), wide);
                        s = s >= 0 ? s >> 3 : Wrap(s + 7, wide) >> 3;
                        s = Math.Max(Math.Min(s, smax), -smax);
                        a[s00 + 1] = Wrap(a[s00 + 1] + s, wide);
                    }
                    s00 += 2;
                }
            }

            // Adjust the curvatures hc.
            for (int i = 2; i < nxtop - 2; i += 2)
            {
                int s00 = ny * i + 2, s10 = s00 + ny;
                for (int j = 2; j < nytop - 2; j += 2)
                {
                    // The sums of the four diagonally neighboring zones:
                    // hmm is at (-x, -y), hpm at (+x, -y), hmp at (-x, +y), hpp at (+x, +y).
                    long hmm = a[s00 - ny2 - 2], hpm = a[s00 + ny2 - 2];
                    long hmp = a[s00 - ny2 + 2], hpp = a[s00 + ny2 + 2];
                    long h0 = a[s00];
                    // diff is 64 * the hc value that would match the neighboring zones.
                    long diff = Wrap(hpp + hmm - hmp - hpm, wide);
                    // Twice the x and y slopes in this zone
                    long hx2 = Wrap(a[s10] << 1, wide), hy2 = Wrap(a[s00 + 1] << 1, wide);
                    // Monotonicity constraints on 64 * hc
                    long m1 = Math.Min(Wrap(Math.Max(Wrap(hpp - h0, wide), 0) - hx2 - hy2, wide), Wrap(Math.Max(Wrap(h0 - hpm, wide), 0) + hx2 - hy2, wide));
                    long m2 = Math.Min(Wrap(Math.Max(Wrap(h0 - hmp, wide), 0) - hx2 + hy2, wide), Wrap(Math.Max(Wrap(hmm - h0, wide), 0) + hx2 + hy2, wide));
                    long dmax = Wrap(Math.Min(m1, m2) << 4, wide);
                    m1 = Math.Max(Wrap(Math.Min(Wrap(hpp - h0, wide), 0) - hx2 - hy2, wide), Wrap(Math.Min(Wrap(h0 - hpm, wide), 0) + hx2 - hy2, wide));
                    m2 = Math.Max(Wrap(Math.Min(Wrap(h0 - hmp, wide), 0) - hx2 + hy2, wide), Wrap(Math.Min(Wrap(hmm - h0, wide), 0) + hx2 + hy2, wide));
                    long dmin = Wrap(Math.Max(m1, m2) << 4, wide);
                    if (dmin < dmax)
                    {
                        diff = Math.Max(Math.Min(diff, dmax), dmin);
                        long s = Wrap(diff - Wrap(a[s10 + 1] << 6, wide), wide);
                        s = s >= 0 ? s >> 6 : Wrap(s + 63, wide) >> 6;
                        s = Math.Max(Math.Min(s, smax), -smax);
                        a[s10 + 1] = Wrap(a[s10 + 1] + s, wide);
                    }
                    s00 += 2;
                    s10 += 2;
                }
            }
        }

        #endregion
    }
}
