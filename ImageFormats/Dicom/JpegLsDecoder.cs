using System;
using System.Numerics;

/*

Decoder for JPEG-LS (ITU-T T.87, ISO/IEC 14495-1) images, as used by the DICOM
transfer syntaxes 1.2.840.10008.1.2.4.80 (lossless) and 1.2.840.10008.1.2.4.81
(near-lossless).

JPEG-LS (a.k.a. LOCO-I) codes every sample by predicting it from its already
decoded neighbors (a = left, b = above, c = above-left, d = above-right) with a
simple edge-detecting predictor, and then coding the prediction error with a
Golomb-Rice code. The three local gradients d-b, b-c and c-a are quantized into
one of 365 "contexts", and each context keeps running statistics that are used
both to pick the Golomb parameter and to correct the bias of the predictor. When
all three gradients are flat, the coder switches to "run mode", in which runs of
identical samples are coded with an adaptive run-length code, and the sample that
interrupts a run gets a special context of its own. In near-lossless mode, every
error is quantized to a multiple of 2*NEAR+1, so the reconstructed samples are
within NEAR of the originals.

Components may be coded in separate scans (no interleave), one line of each in
turn (line interleave), or one pixel at a time (sample interleave).

Copyright 2026+ Dmitry Brant
https://dmitrybrant.com

License: MIT
*/

namespace DmitryBrant.ImageFormats
{
    internal static class JpegLsDecoder
    {
        // The order of the run-length code for each value of the run index.
        private static readonly int[] J =
        {
            0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3,
            4, 4, 5, 5, 6, 6, 7, 7, 8, 9, 10, 11, 12, 13, 14, 15
        };

        private const int MarkerSoi = 0xD8;
        private const int MarkerEoi = 0xD9;
        private const int MarkerSos = 0xDA;
        private const int MarkerDri = 0xDD;
        private const int MarkerApp8 = 0xE8;
        private const int MarkerSof55 = 0xF7;
        private const int MarkerLse = 0xF8;

        /// <summary>
        /// Coding parameters that may be overridden by an LSE marker segment. Zero
        /// means that the default value should be used.
        /// </summary>
        private sealed class PresetParameters
        {
            public int MaxVal, T1, T2, T3, Reset;
        }

        public static CodecImage Decode(byte[] data)
        {
            if (data.Length < 4 || data[0] != 0xFF || data[1] != MarkerSoi)
                throw new ImageDecodeException("Not a valid JPEG-LS stream.");

            int pos = 2;
            int width = 0, height = 0, precision = 0, numComponents = 0;
            int[] componentIds = Array.Empty<int>();
            bool[] componentDecoded = Array.Empty<bool>();
            int[][]? planes = null;
            var preset = new PresetParameters();
            int restartInterval = 0;
            int colorTransform = 0;

            while (true)
            {
                int marker = NextMarker(data, ref pos);
                if (marker < 0 || marker == MarkerEoi)
                    break;
                if ((marker >= 0xD0 && marker <= 0xD7) || marker == 0x01 || marker == MarkerSoi)
                    continue;

                if (pos + 2 > data.Length)
                    throw new ImageDecodeException("Unexpected end of JPEG-LS stream.");
                int segLen = (data[pos] << 8) | data[pos + 1];
                if (segLen < 2 || pos + segLen > data.Length)
                    throw new ImageDecodeException("Invalid JPEG-LS marker segment length.");
                int seg = pos + 2;
                pos += segLen;

                switch (marker)
                {
                    case MarkerSof55:
                        {
                            if (segLen < 8)
                                throw new ImageDecodeException("Invalid JPEG-LS frame header.");
                            precision = data[seg];
                            height = ReadU16(data, seg + 1);
                            width = ReadU16(data, seg + 3);
                            numComponents = data[seg + 5];
                            if (precision < 2 || precision > 16)
                                throw new ImageDecodeException("Unsupported JPEG-LS sample precision: " + precision);
                            if (numComponents < 1 || segLen < 8 + 3 * numComponents)
                                throw new ImageDecodeException("Invalid number of JPEG-LS components.");
                            componentIds = new int[numComponents];
                            componentDecoded = new bool[numComponents];
                            for (int i = 0; i < numComponents; i++)
                            {
                                componentIds[i] = data[seg + 6 + 3 * i];
                                if (data[seg + 7 + 3 * i] != data[seg + 7])
                                    throw new ImageDecodeException("Subsampled JPEG-LS components are not supported.");
                            }
                        }
                        break;

                    case MarkerLse:
                        {
                            int id = segLen > 2 ? data[seg] : 0;
                            if (id == 1 && segLen >= 13)
                            {
                                preset.MaxVal = ReadU16(data, seg + 1);
                                preset.T1 = ReadU16(data, seg + 3);
                                preset.T2 = ReadU16(data, seg + 5);
                                preset.T3 = ReadU16(data, seg + 7);
                                preset.Reset = ReadU16(data, seg + 9);
                            }
                            else if (id == 4 && segLen >= 4)
                            {
                                // Oversize image dimensions, which don't fit into the frame header.
                                int wxy = data[seg + 1];
                                if (wxy < 2 || wxy > 4 || segLen < 4 + 2 * wxy)
                                    throw new ImageDecodeException("Invalid JPEG-LS oversize dimensions.");
                                height = (int)ReadUBig(data, seg + 2, wxy);
                                width = (int)ReadUBig(data, seg + 2 + wxy, wxy);
                            }
                            // Other LSE types (mapping tables) are ignored.
                        }
                        break;

                    case MarkerDri:
                        restartInterval = (int)ReadUBig(data, seg, Math.Min(4, segLen - 2));
                        break;

                    case MarkerApp8:
                        // HP Labs color transform, which is written by some encoders.
                        if (segLen >= 7 && data[seg] == 'm' && data[seg + 1] == 'r' && data[seg + 2] == 'f' && data[seg + 3] == 'x')
                            colorTransform = data[seg + 4];
                        break;

                    case MarkerSos:
                        {
                            if (numComponents == 0)
                                throw new ImageDecodeException("JPEG-LS scan found before frame header.");
                            if (width <= 0 || height <= 0 || (long)width * height * numComponents > DicomReader.MaxSamples)
                                throw new ImageDecodeException("Invalid JPEG-LS image dimensions.");
                            planes ??= new int[numComponents][];

                            int ns = data[seg];
                            if (ns < 1 || segLen < 6 + 2 * ns)
                                throw new ImageDecodeException("Invalid JPEG-LS scan header.");
                            var scanComponents = new int[ns];
                            for (int i = 0; i < ns; i++)
                            {
                                int index = Array.IndexOf(componentIds, (int)data[seg + 1 + 2 * i]);
                                if (index < 0)
                                    throw new ImageDecodeException("JPEG-LS scan refers to an unknown component.");
                                scanComponents[i] = index;
                                planes[index] ??= new int[width * height];
                            }
                            int near = data[seg + 1 + 2 * ns];
                            int ilv = data[seg + 2 + 2 * ns];
                            int pointTransform = data[seg + 3 + 2 * ns] & 0xF;
                            if (ilv > 2 || (ilv == 0 && ns != 1))
                                throw new ImageDecodeException("Invalid JPEG-LS interleave mode.");
                            if (pointTransform != 0)
                                throw new ImageDecodeException("JPEG-LS point transform is not supported.");

                            var decoder = new ScanDecoder(data, pos, precision, near, preset);
                            decoder.DecodeScan(planes, scanComponents, width, height, ilv, restartInterval);
                            pos = decoder.FindMarker();
                            foreach (var c in scanComponents)
                                componentDecoded[c] = true;
                        }
                        break;

                    default:
                        if (marker == 0xC0 || marker == 0xC1 || marker == 0xC2 || marker == 0xC3)
                            throw new ImageDecodeException("Not a JPEG-LS stream (this is a regular JPEG).");
                        break;
                }
            }

            if (planes == null)
                throw new ImageDecodeException("JPEG-LS stream contains no image data.");
            for (int c = 0; c < numComponents; c++)
            {
                if (!componentDecoded[c])
                    throw new ImageDecodeException("JPEG-LS stream is missing a component.");
            }

            int pixelCount = width * height;
            var samples = new int[pixelCount * numComponents];
            for (int c = 0; c < numComponents; c++)
            {
                var plane = planes[c];
                for (int i = 0, o = c; i < pixelCount; i++, o += numComponents)
                    samples[o] = plane[i];
            }

            if (numComponents == 3 && (colorTransform == 1 || colorTransform == 2))
                InverseColorTransform(samples, colorTransform, precision);
            else if (colorTransform != 0)
                throw new ImageDecodeException("Unsupported JPEG-LS color transform: " + colorTransform);

            return new CodecImage(width, height, numComponents, precision, false, samples);
        }

        /// <summary>
        /// Undo the HP Labs color transforms, which store red and blue relative to green.
        /// </summary>
        private static void InverseColorTransform(int[] samples, int transform, int precision)
        {
            int range = 1 << precision, mask = range - 1, half = range / 2;
            for (int i = 0; i < samples.Length; i += 3)
            {
                int g = samples[i + 1];
                int r = (samples[i] + g - half) & mask;
                int b = transform == 1
                    ? (samples[i + 2] + g - half) & mask
                    : (samples[i + 2] + ((r + g) >> 1) - half) & mask;
                samples[i] = r;
                samples[i + 2] = b;
            }
        }

        /// <summary>
        /// Advance to the next marker, and return its code, or -1 if there are no more.
        /// </summary>
        private static int NextMarker(byte[] data, ref int pos)
        {
            while (pos < data.Length && data[pos] != 0xFF)
                pos++;
            while (pos < data.Length && data[pos] == 0xFF)
                pos++;
            if (pos >= data.Length)
                return -1;
            return data[pos++];
        }

        private static int ReadU16(byte[] data, int pos)
        {
            return (data[pos] << 8) | data[pos + 1];
        }

        private static long ReadUBig(byte[] data, int pos, int count)
        {
            long v = 0;
            for (int i = 0; i < count; i++)
                v = (v << 8) | data[pos + i];
            return v;
        }

        /// <summary>
        /// Decodes the entropy-coded data of a single scan.
        /// </summary>
        private sealed class ScanDecoder
        {
            private readonly int maxVal, near, near2p1, range, qbpp, limit, reset;
            private readonly int initialA;

            // Quantized gradient (-4 to 4) for every possible difference between two samples.
            private readonly sbyte[] quant;
            private readonly int quantOffset;

            // Statistics for the regular-mode contexts.
            private readonly int[] ctxA = new int[365], ctxB = new int[365], ctxC = new int[365], ctxN = new int[365];

            // Statistics for the two run interruption contexts.
            private readonly int[] riA = new int[2], riN = new int[2], riNn = new int[2];

            private int runIndex;

            // Bit reader state. The cache holds up to 64 not-yet-consumed bits, left aligned.
            private readonly byte[] data;
            private int pos;
            private ulong cache;
            private int validBits;
            private bool lastFF;
            private int paddedBits;

            public ScanDecoder(byte[] data, int pos, int precision, int near, PresetParameters preset)
            {
                this.data = data;
                this.pos = pos;

                maxVal = preset.MaxVal != 0 ? preset.MaxVal : (1 << precision) - 1;
                if (near < 0 || near > Math.Min(255, maxVal / 2))
                    throw new ImageDecodeException("Invalid JPEG-LS NEAR parameter.");
                this.near = near;
                near2p1 = 2 * near + 1;
                range = (maxVal + 2 * near) / near2p1 + 1;
                qbpp = CeilLog2(range);
                int bpp = Math.Max(2, CeilLog2(maxVal + 1));
                limit = 2 * (bpp + Math.Max(8, bpp));
                reset = preset.Reset != 0 ? preset.Reset : 64;

                // Default gradient thresholds, as given in C.2.4.1.1.
                int t1, t2, t3;
                if (maxVal >= 128)
                {
                    int factor = (Math.Min(maxVal, 4095) + 128) / 256;
                    t1 = Clamp(factor * (3 - 2) + 2 + 3 * near, near + 1, maxVal);
                    t2 = Clamp(factor * (7 - 3) + 3 + 5 * near, t1, maxVal);
                    t3 = Clamp(factor * (21 - 4) + 4 + 7 * near, t2, maxVal);
                }
                else
                {
                    int factor = 256 / (maxVal + 1);
                    t1 = Clamp(Math.Max(2, 3 / factor + 3 * near), near + 1, maxVal);
                    t2 = Clamp(Math.Max(3, 7 / factor + 5 * near), t1, maxVal);
                    t3 = Clamp(Math.Max(4, 21 / factor + 7 * near), t2, maxVal);
                }
                if (preset.T1 != 0) t1 = preset.T1;
                if (preset.T2 != 0) t2 = preset.T2;
                if (preset.T3 != 0) t3 = preset.T3;

                quantOffset = maxVal;
                quant = new sbyte[2 * maxVal + 1];
                for (int d = -maxVal; d <= maxVal; d++)
                {
                    int q;
                    if (d <= -t3) q = -4;
                    else if (d <= -t2) q = -3;
                    else if (d <= -t1) q = -2;
                    else if (d < -near) q = -1;
                    else if (d <= near) q = 0;
                    else if (d < t1) q = 1;
                    else if (d < t2) q = 2;
                    else if (d < t3) q = 3;
                    else q = 4;
                    quant[d + quantOffset] = (sbyte)q;
                }

                initialA = Math.Max(2, (range + 32) / 64);
                ResetContexts();
            }

            private static int Clamp(int i, int j, int maxVal)
            {
                return (i > maxVal || i < j) ? j : i;
            }

            private static int CeilLog2(int n)
            {
                int i = 0;
                while ((1 << i) < n)
                    i++;
                return i;
            }

            private void ResetContexts()
            {
                for (int i = 0; i < ctxA.Length; i++)
                {
                    ctxA[i] = initialA;
                    ctxB[i] = 0;
                    ctxC[i] = 0;
                    ctxN[i] = 1;
                }
                for (int i = 0; i < 2; i++)
                {
                    riA[i] = initialA;
                    riN[i] = 1;
                    riNn[i] = 0;
                }
                runIndex = 0;
            }

            public void DecodeScan(int[][] planes, int[] scanComponents, int width, int height, int ilv, int restartInterval)
            {
                int ns = scanComponents.Length;

                // Each line buffer has one extra sample on either side, for the edges.
                var prev = new int[ns][];
                var cur = new int[ns][];
                var runIndices = new int[ns];
                for (int i = 0; i < ns; i++)
                {
                    prev[i] = new int[width + 2];
                    cur[i] = new int[width + 2];
                }

                for (int y = 0; y < height; y++)
                {
                    if (restartInterval > 0 && y > 0 && y % restartInterval == 0)
                    {
                        // Each restart interval is coded as if it were a new image.
                        Restart();
                        for (int i = 0; i < ns; i++)
                        {
                            Array.Clear(prev[i]);
                            Array.Clear(cur[i]);
                            runIndices[i] = 0;
                        }
                    }

                    if (ilv == 2)
                    {
                        DecodeLineInterleaved(prev, cur, width, ns);
                    }
                    else
                    {
                        for (int i = 0; i < ns; i++)
                        {
                            runIndex = runIndices[i];
                            DecodeLine(prev[i], cur[i], width);
                            runIndices[i] = runIndex;
                        }
                    }

                    for (int i = 0; i < ns; i++)
                    {
                        Array.Copy(cur[i], 1, planes[scanComponents[i]], y * width, width);
                        (prev[i], cur[i]) = (cur[i], prev[i]);
                    }
                }
            }

            /// <summary>
            /// Decode one line of a single component. Sample x of a line is stored at index x + 1.
            /// </summary>
            private void DecodeLine(int[] prev, int[] cur, int width)
            {
                // The sample to the right of the last one on the previous line is taken to be
                // the same as the last one, and the sample to the left of the first one on this
                // line is taken to be the one above it.
                prev[width + 1] = prev[width];
                cur[0] = prev[1];

                int x = 0;
                while (x < width)
                {
                    int ra = cur[x], rb = prev[x + 1], rc = prev[x], rd = prev[x + 2];
                    int qs = quant[rd - rb + quantOffset] * 81 + quant[rb - rc + quantOffset] * 9 + quant[rc - ra + quantOffset];
                    if (qs == 0)
                    {
                        x += DecodeRun(prev, cur, x, width);
                    }
                    else
                    {
                        cur[x + 1] = DecodeRegular(qs, Predict(ra, rb, rc));
                        x++;
                    }
                }
            }

            /// <summary>
            /// Decode one line of all components of a sample-interleaved scan.
            /// </summary>
            private void DecodeLineInterleaved(int[][] prev, int[][] cur, int width, int ns)
            {
                Span<int> qs = stackalloc int[ns];
                for (int c = 0; c < ns; c++)
                {
                    prev[c][width + 1] = prev[c][width];
                    cur[c][0] = prev[c][1];
                }

                int x = 0;
                while (x < width)
                {
                    bool flat = true;
                    for (int c = 0; c < ns; c++)
                    {
                        int[] p = prev[c];
                        int ra = cur[c][x], rb = p[x + 1], rc = p[x], rd = p[x + 2];
                        qs[c] = quant[rd - rb + quantOffset] * 81 + quant[rb - rc + quantOffset] * 9 + quant[rc - ra + quantOffset];
                        if (qs[c] != 0)
                            flat = false;
                    }

                    if (flat)
                    {
                        int remaining = width - x;
                        int count = DecodeRunLength(remaining);
                        for (int c = 0; c < ns; c++)
                        {
                            int[] line = cur[c];
                            int ra = line[x];
                            for (int i = 1; i <= count; i++)
                                line[x + i] = ra;
                        }
                        if (count == remaining)
                        {
                            x += count;
                            continue;
                        }

                        // In sample-interleaved mode, the run interruption sample of each
                        // component is predicted from the sample above it.
                        int ix = x + count + 1;
                        for (int c = 0; c < ns; c++)
                        {
                            int ra = cur[c][x], rb = prev[c][ix];
                            int e = DecodeRunInterruptionError(0) * near2p1;
                            cur[c][ix] = Fix(rb + (rb - ra < 0 ? -e : e));
                        }
                        if (runIndex > 0)
                            runIndex--;
                        x += count + 1;
                    }
                    else
                    {
                        for (int c = 0; c < ns; c++)
                        {
                            int[] p = prev[c];
                            cur[c][x + 1] = DecodeRegular(qs[c], Predict(cur[c][x], p[x + 1], p[x]));
                        }
                        x++;
                    }
                }
            }

            /// <summary>
            /// The median edge detector: predicts the sample from its left, upper, and
            /// upper-left neighbors.
            /// </summary>
            private static int Predict(int ra, int rb, int rc)
            {
                if (ra < rb)
                {
                    if (rc >= rb) return ra;
                    if (rc <= ra) return rb;
                }
                else
                {
                    if (rc >= ra) return rb;
                    if (rc <= rb) return ra;
                }
                return ra + rb - rc;
            }

            /// <summary>
            /// Bring a reconstructed sample back into range, undoing the modulo reduction
            /// of the prediction error.
            /// </summary>
            private int Fix(int rx)
            {
                if (rx < -near)
                    rx += range * near2p1;
                else if (rx > maxVal + near)
                    rx -= range * near2p1;
                if (rx < 0)
                    return 0;
                return rx > maxVal ? maxVal : rx;
            }

            private int DecodeRegular(int qs, int predicted)
            {
                // Contexts with a negative gradient pattern share statistics with their
                // positive counterpart, with the sign of the error flipped.
                int sign = qs >> 31;
                int q = (qs ^ sign) - sign;

                int px = predicted + ((ctxC[q] ^ sign) - sign);
                if (px > maxVal)
                    px = maxVal;
                else if (px < 0)
                    px = 0;

                int n = ctxN[q], a = ctxA[q];
                int k = 0;
                while ((n << k) < a && k < 30)
                    k++;

                int mapped = DecodeValue(k, limit);
                int err = (mapped & 1) != 0 ? -((mapped + 1) >> 1) : mapped >> 1;
                if (k == 0 && near == 0 && 2 * ctxB[q] + n - 1 < 0)
                    err = ~err;
                if (err > 65535 || err < -65535)
                    throw new ImageDecodeException("Invalid JPEG-LS prediction error.");

                // Update the context statistics, and the bias correction.
                int b = ctxB[q] + err * near2p1;
                a += Math.Abs(err);
                if (n == reset)
                {
                    a >>= 1;
                    b >>= 1;
                    n >>= 1;
                }
                n++;
                if (b + n <= 0)
                {
                    b += n;
                    if (b <= -n)
                        b = -n + 1;
                    if (ctxC[q] > -128)
                        ctxC[q]--;
                }
                else if (b > 0)
                {
                    b -= n;
                    if (b > 0)
                        b = 0;
                    if (ctxC[q] < 127)
                        ctxC[q]++;
                }
                ctxA[q] = a;
                ctxB[q] = b;
                ctxN[q] = n;

                int e = err * near2p1;
                return Fix(px + ((e ^ sign) - sign));
            }

            /// <summary>
            /// Decode a run starting at sample x of a single-component line, including the
            /// sample that interrupts it, if any. Returns the number of samples decoded.
            /// </summary>
            private int DecodeRun(int[] prev, int[] cur, int x, int width)
            {
                int ra = cur[x];
                int remaining = width - x;
                int count = DecodeRunLength(remaining);
                for (int i = 1; i <= count; i++)
                    cur[x + i] = ra;
                if (count == remaining)
                    return count;

                int rb = prev[x + count + 1];
                int rx;
                if (Math.Abs(ra - rb) <= near)
                {
                    rx = Fix(ra + DecodeRunInterruptionError(1) * near2p1);
                }
                else
                {
                    int e = DecodeRunInterruptionError(0) * near2p1;
                    rx = Fix(rb + (rb - ra < 0 ? -e : e));
                }
                cur[x + count + 1] = rx;
                if (runIndex > 0)
                    runIndex--;
                return count + 1;
            }

            private int DecodeRunLength(int remaining)
            {
                int index = 0;
                while (ReadBit() != 0)
                {
                    int runLength = 1 << J[runIndex];
                    int count = Math.Min(runLength, remaining - index);
                    index += count;
                    if (count == runLength && runIndex < 31)
                        runIndex++;
                    if (index == remaining)
                        return index;
                }

                // A run that ends before the end of the line is followed by its remaining length.
                if (J[runIndex] > 0)
                    index += ReadBits(J[runIndex]);
                if (index > remaining)
                    throw new ImageDecodeException("Invalid JPEG-LS run length.");
                return index;
            }

            private int DecodeRunInterruptionError(int riType)
            {
                int a = riA[riType], n = riN[riType];
                int temp = a + (n >> 1) * riType;
                int k = 0;
                for (int nt = n; nt < temp && k < 30; nt <<= 1)
                    k++;

                int mapped = DecodeValue(k, limit - J[runIndex] - 1);
                int t = mapped + riType;
                int map = t & 1;
                int absErr = (t + map) >> 1;
                int err = ((k != 0 || 2 * riNn[riType] >= n) == (map != 0)) ? -absErr : absErr;

                if (err < 0)
                    riNn[riType]++;
                riA[riType] = a + ((mapped + 1 - riType) >> 1);
                if (n == reset)
                {
                    riA[riType] >>= 1;
                    riN[riType] >>= 1;
                    riNn[riType] >>= 1;
                }
                riN[riType]++;
                return err;
            }

            /// <summary>
            /// Decode a Golomb-coded value with parameter k, whose unary part is limited in
            /// length; beyond the limit, the value is escaped and given in full.
            /// </summary>
            private int DecodeValue(int k, int glimit)
            {
                int high = ReadHighBits(glimit);
                if (high >= glimit - (qbpp + 1))
                    return ReadBits(qbpp) + 1;
                if (k == 0)
                    return high;
                if (k > 24)
                    throw new ImageDecodeException("Invalid JPEG-LS code.");

                // A valid mapped error is never much larger than the sample range.
                int value = (high << k) + ReadBits(k);
                if (value > 0x3FFFF)
                    throw new ImageDecodeException("Invalid JPEG-LS code.");
                return value;
            }

            private void Fill()
            {
                while (validBits <= 56)
                {
                    if (pos >= data.Length || (data[pos] == 0xFF && (pos + 1 >= data.Length || data[pos + 1] >= 0x80)))
                    {
                        // We've reached a marker, or the end of the data; supply zero bits.
                        if (validBits < 32)
                        {
                            validBits += 32;
                            paddedBits += 32;
                            if (paddedBits > 1024)
                                throw new ImageDecodeException("Unexpected end of JPEG-LS data.");
                        }
                        return;
                    }

                    byte b = data[pos++];
                    if (lastFF)
                    {
                        // A byte after 0xFF has a stuffed zero bit at the top.
                        cache |= (ulong)b << (57 - validBits);
                        validBits += 7;
                    }
                    else
                    {
                        cache |= (ulong)b << (56 - validBits);
                        validBits += 8;
                    }
                    lastFF = b == 0xFF;
                }
            }

            private int ReadBit()
            {
                if (validBits < 1)
                    Fill();
                int bit = (int)(cache >> 63);
                cache <<= 1;
                validBits--;
                return bit;
            }

            private int ReadBits(int count)
            {
                if (count == 0)
                    return 0;
                if (validBits < count)
                    Fill();
                int value = (int)(cache >> (64 - count));
                cache <<= count;
                validBits -= count;
                return value;
            }

            /// <summary>
            /// Read a unary code: the number of zero bits before the next one bit.
            /// </summary>
            private int ReadHighBits(int maxCount)
            {
                int count = 0;
                while (true)
                {
                    if (validBits < 32)
                        Fill();
                    int zeros = BitOperations.LeadingZeroCount(cache);
                    if (zeros < validBits)
                    {
                        cache <<= zeros + 1;
                        validBits -= zeros + 1;
                        return count + zeros;
                    }
                    count += validBits;
                    cache = 0;
                    validBits = 0;
                    if (count > maxCount)
                        throw new ImageDecodeException("Invalid JPEG-LS code.");
                }
            }

            /// <summary>
            /// Discard the rest of the current restart interval, and skip over the restart
            /// marker that follows it.
            /// </summary>
            private void Restart()
            {
                int p = FindMarker();
                if (p + 1 >= data.Length || data[p + 1] < 0xD0 || data[p + 1] > 0xD7)
                    throw new ImageDecodeException("Expected a JPEG-LS restart marker.");
                pos = p + 2;
                cache = 0;
                validBits = 0;
                lastFF = false;
                paddedBits = 0;
                ResetContexts();
            }

            /// <summary>
            /// Find the position of the marker that follows the entropy-coded data.
            /// </summary>
            public int FindMarker()
            {
                int p = pos;
                while (p + 1 < data.Length)
                {
                    if (data[p] == 0xFF && data[p + 1] >= 0x80 && data[p + 1] != 0xFF)
                        return p;
                    p++;
                }
                return data.Length;
            }
        }
    }
}
