using System;

/*

Decoder for the Huffman-coded flavors of JPEG (ITU-T T.81) that turn up inside
DICOM files: baseline and extended sequential DCT (8 and 12 bits per sample),
progressive DCT, and the original lossless process (process 14), which DICOM
uses for most of its "JPEG Lossless" transfer syntaxes at up to 16 bits per
sample. Arithmetic-coded and hierarchical JPEG are not supported.

We decode ourselves rather than handing the data to a general-purpose library,
because most of those only handle 8-bit lossy JPEG, whereas medical images are
usually 12 or 16 bits per sample.

A DCT image is split into 8x8 blocks of samples, which are transformed into
frequency coefficients, quantized, and Huffman coded, a scan at a time. In a
sequential image each scan holds everything about the components it covers; in
a progressive image the coefficients are sent over several scans, either a band
of coefficients at a time ("spectral selection") or a bit at a time
("successive approximation"). So we accumulate all the coefficients of the image
first, and only then dequantize and inverse-transform them.

A lossless image instead predicts each sample from its neighbors to the left,
above, and above-left, and Huffman codes the difference between the prediction
and the actual value.

Copyright 2026+ Dmitry Brant
https://dmitrybrant.com

License: MIT
*/

namespace DmitryBrant.ImageFormats
{
    internal sealed class JpegDecoder
    {
        // Order in which the coefficients of a block are stored in the stream.
        private static readonly int[] ZigZag =
        {
            0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5,
            12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
            35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
            58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63
        };

        // Cosine basis for the inverse DCT: IdctTable[x * 8 + u] = C(u)/2 * cos((2x + 1) * u * pi / 16)
        private static readonly float[] IdctTable = BuildIdctTable();

        private sealed class HuffmanTable
        {
            // Fast lookup of codes up to LookupBits long: (code length << 8) | value, or 0.
            public const int LookupBits = 9;
            public readonly int[] Lookup = new int[1 << LookupBits];
            public readonly int[] MaxCode = new int[18];
            public readonly int[] ValPtr = new int[17];
            public readonly byte[] Values;

            public HuffmanTable(byte[] counts, byte[] values)
            {
                Values = values;
                int code = 0, k = 0;
                for (int len = 1; len <= 16; len++)
                {
                    ValPtr[len] = k - code;
                    for (int i = 0; i < counts[len - 1]; i++)
                    {
                        if (code >= (1 << len) || k >= values.Length)
                            throw new ImageDecodeException("Invalid Huffman table in JPEG stream.");
                        if (len <= LookupBits)
                        {
                            int shift = LookupBits - len;
                            for (int j = 0; j < (1 << shift); j++)
                                Lookup[(code << shift) | j] = (len << 8) | values[k];
                        }
                        code++;
                        k++;
                    }
                    MaxCode[len] = counts[len - 1] > 0 ? code - 1 : -1;
                    code <<= 1;
                }
                MaxCode[17] = int.MaxValue;
            }
        }

        private sealed class Component
        {
            public int Id, H, V, QuantTable;
            public int BlocksPerLine, BlocksPerColumn;   // blocks that actually cover the component
            public int BlocksPerLineAlloc, BlocksPerColumnAlloc;   // blocks including MCU padding
            public int Width, Height;   // size of the component in samples
            public int[] Coefficients = Array.Empty<int>();
            public int[] Samples = Array.Empty<int>();   // lossless only
            public HuffmanTable? DcTable, AcTable;
            public int Pred;
        }

        private readonly byte[] data;
        private readonly HuffmanTable?[] dcTables = new HuffmanTable?[4];
        private readonly HuffmanTable?[] acTables = new HuffmanTable?[4];
        private readonly int[][] quantTables = new int[4][];
        private Component[] components = Array.Empty<Component>();
        private int width, height, precision;
        private int maxH = 1, maxV = 1, mcusPerLine, mcusPerColumn;
        private int restartInterval;
        private bool progressive, lossless, frameSeen;
        private bool jfif;
        private int adobeTransform = -1;
        private int eobRun;

        // Bit reader state
        private int pos;
        private uint bitBuf;
        private int bitCount;
        private bool hitMarker;

        private JpegDecoder(byte[] data)
        {
            this.data = data;
        }

        /// <summary>
        /// Decodes a JPEG image. Lossy images with three components are converted from
        /// YCbCr to RGB when the stream indicates that they are YCbCr (following the same
        /// rules as libjpeg), in which case ColorConverted is set on the result.
        /// </summary>
        public static CodecImage Decode(byte[] data)
        {
            try
            {
                return new JpegDecoder(data).Decode();
            }
            catch (Exception e) when (e is IndexOutOfRangeException || e is ArgumentException)
            {
                throw new ImageDecodeException("Invalid JPEG stream: " + e.Message);
            }
        }

        private CodecImage Decode()
        {
            if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
                throw new ImageDecodeException("Not a valid JPEG stream.");
            pos = 2;
            bool done = false;

            while (!done && pos < data.Length)
            {
                int marker = NextMarker();
                if (marker < 0)
                    break;

                switch (marker)
                {
                    case 0xD8: // SOI
                    case 0x01: // TEM
                        break;
                    case 0xD9: // EOI
                        done = true;
                        break;
                    case 0xC4:
                        ReadHuffmanTables();
                        break;
                    case 0xDB:
                        ReadQuantTables();
                        break;
                    case 0xDD:
                        restartInterval = ReadUInt16(pos + 2);
                        pos += ReadUInt16(pos);
                        break;
                    case 0xC0: // baseline
                    case 0xC1: // extended sequential
                    case 0xC2: // progressive
                    case 0xC3: // lossless
                        if (frameSeen)
                            throw new ImageDecodeException("JPEG stream has more than one frame.");
                        progressive = marker == 0xC2;
                        lossless = marker == 0xC3;
                        ReadFrameHeader();
                        break;
                    case 0xC5: case 0xC6: case 0xC7:
                        throw new ImageDecodeException("Hierarchical JPEG is not supported.");
                    case 0xC9: case 0xCA: case 0xCB: case 0xCD: case 0xCE: case 0xCF:
                        throw new ImageDecodeException("Arithmetic-coded JPEG is not supported.");
                    case 0xDA:
                        if (!frameSeen)
                            throw new ImageDecodeException("JPEG scan precedes frame header.");
                        try
                        {
                            ReadScan();
                        }
                        catch (Exception e)
                        {
                            // Corrupt or truncated data; keep whatever we decoded so far.
                            Util.log("Error while decoding JPEG scan: " + e.Message);
                            done = true;
                        }
                        break;
                    case 0xE0:
                        if (ReadUInt16(pos) >= 7 && pos + 7 <= data.Length && data[pos + 2] == 'J' && data[pos + 3] == 'F' && data[pos + 4] == 'I' && data[pos + 5] == 'F')
                            jfif = true;
                        pos += ReadUInt16(pos);
                        break;
                    case 0xEE:
                        if (ReadUInt16(pos) >= 14 && pos + 14 <= data.Length && data[pos + 2] == 'A' && data[pos + 3] == 'd' && data[pos + 4] == 'o' && data[pos + 5] == 'b' && data[pos + 6] == 'e')
                            adobeTransform = data[pos + 13];
                        pos += ReadUInt16(pos);
                        break;
                    default:
                        if (marker >= 0xD0 && marker <= 0xD7)
                            break; // stray restart marker
                        // Any other segment (APPn, COM, DNL, ...) is skipped.
                        pos += ReadUInt16(pos);
                        break;
                }
            }

            if (!frameSeen)
                throw new ImageDecodeException("JPEG stream has no frame header.");

            return lossless ? BuildLosslessImage() : BuildDctImage();
        }

        private int NextMarker()
        {
            // Markers may be preceded by any number of fill bytes (0xFF).
            while (pos < data.Length - 1)
            {
                if (data[pos] == 0xFF && data[pos + 1] != 0 && data[pos + 1] != 0xFF)
                {
                    int marker = data[pos + 1];
                    pos += 2;
                    return marker;
                }
                pos++;
            }
            return -1;
        }

        private int ReadUInt16(int offset)
        {
            if (offset + 1 >= data.Length)
                throw new ImageDecodeException("Unexpected end of JPEG stream.");
            return (data[offset] << 8) | data[offset + 1];
        }

        private void ReadHuffmanTables()
        {
            int end = pos + ReadUInt16(pos);
            int p = pos + 2;
            while (p < end && p + 17 <= data.Length)
            {
                int tc = data[p] >> 4, th = data[p] & 3;
                var counts = new byte[16];
                Array.Copy(data, p + 1, counts, 0, 16);
                p += 17;
                int total = 0;
                foreach (var c in counts)
                    total += c;
                if (p + total > data.Length)
                    throw new ImageDecodeException("Unexpected end of JPEG stream.");
                var values = new byte[total];
                Array.Copy(data, p, values, 0, total);
                p += total;
                var table = new HuffmanTable(counts, values);
                if (tc == 0)
                    dcTables[th] = table;
                else
                    acTables[th] = table;
            }
            pos = end;
        }

        private void ReadQuantTables()
        {
            int end = pos + ReadUInt16(pos);
            int p = pos + 2;
            while (p < end && p < data.Length)
            {
                int pq = data[p] >> 4, tq = data[p] & 3;
                p++;
                var table = new int[64];
                for (int i = 0; i < 64; i++)
                {
                    if (pq == 0)
                        table[ZigZag[i]] = data[p++];
                    else
                    {
                        table[ZigZag[i]] = ReadUInt16(p);
                        p += 2;
                    }
                }
                quantTables[tq] = table;
            }
            pos = end;
        }

        private void ReadFrameHeader()
        {
            int len = ReadUInt16(pos);
            if (pos + len > data.Length || len < 8)
                throw new ImageDecodeException("Invalid JPEG frame header.");
            precision = data[pos + 2];
            height = ReadUInt16(pos + 3);
            width = ReadUInt16(pos + 5);
            int count = data[pos + 7];
            if (width == 0 || height == 0 || count == 0 || len < 8 + count * 3 || (long)width * height * count > CodecImage.MaxSamples)
                throw new ImageDecodeException("Invalid JPEG frame header.");
            if (precision < 2 || precision > 16)
                throw new ImageDecodeException("Unsupported JPEG sample precision: " + precision);

            components = new Component[count];
            for (int i = 0; i < count; i++)
            {
                int p = pos + 8 + i * 3;
                var c = new Component
                {
                    Id = data[p],
                    H = Math.Max(1, data[p + 1] >> 4),
                    V = Math.Max(1, data[p + 1] & 0xF),
                    QuantTable = data[p + 2] & 3
                };
                components[i] = c;
                maxH = Math.Max(maxH, c.H);
                maxV = Math.Max(maxV, c.V);
            }
            pos += len;
            frameSeen = true;

            if (lossless)
            {
                // In the lossless process a "block" is a single sample.
                mcusPerLine = (width + maxH - 1) / maxH;
                mcusPerColumn = (height + maxV - 1) / maxV;
                foreach (var c in components)
                {
                    c.Width = (width * c.H + maxH - 1) / maxH;
                    c.Height = (height * c.V + maxV - 1) / maxV;
                    c.BlocksPerLineAlloc = mcusPerLine * c.H;
                    c.BlocksPerColumnAlloc = mcusPerColumn * c.V;
                    c.Samples = new int[c.BlocksPerLineAlloc * c.BlocksPerColumnAlloc];
                }
            }
            else
            {
                mcusPerLine = (width + 8 * maxH - 1) / (8 * maxH);
                mcusPerColumn = (height + 8 * maxV - 1) / (8 * maxV);
                foreach (var c in components)
                {
                    c.Width = (width * c.H + maxH - 1) / maxH;
                    c.Height = (height * c.V + maxV - 1) / maxV;
                    c.BlocksPerLine = (c.Width + 7) / 8;
                    c.BlocksPerColumn = (c.Height + 7) / 8;
                    c.BlocksPerLineAlloc = mcusPerLine * c.H;
                    c.BlocksPerColumnAlloc = mcusPerColumn * c.V;
                    c.Coefficients = new int[c.BlocksPerLineAlloc * c.BlocksPerColumnAlloc * 64];
                }
            }
        }

        private void ReadScan()
        {
            int len = ReadUInt16(pos);
            int count = data[pos + 2];
            if (count == 0 || len < 6 + count * 2)
                throw new ImageDecodeException("Invalid JPEG scan header.");
            var scanComps = new Component[count];
            for (int i = 0; i < count; i++)
            {
                int id = data[pos + 3 + i * 2];
                int tables = data[pos + 4 + i * 2];
                var c = Array.Find(components, x => x.Id == id) ?? (i < components.Length ? components[i] : null);
                if (c == null)
                    throw new ImageDecodeException("JPEG scan refers to an unknown component.");
                c.DcTable = dcTables[(tables >> 4) & 3];
                c.AcTable = acTables[tables & 3];
                scanComps[i] = c;
            }
            int p = pos + 3 + count * 2;
            int ss = data[p], se = data[p + 1], ah = data[p + 2] >> 4, al = data[p + 2] & 0xF;
            pos += len;

            ResetBits();
            if (lossless)
                DecodeLosslessScan(scanComps, ss, al);
            else
                DecodeDctScan(scanComps, ss, se, ah, al);
        }

        #region Bit reading

        private void ResetBits()
        {
            bitBuf = 0;
            bitCount = 0;
            hitMarker = false;
        }

        private void FillBits()
        {
            while (bitCount <= 24)
            {
                int b = 0;
                if (!hitMarker && pos < data.Length)
                {
                    b = data[pos];
                    if (b == 0xFF)
                    {
                        int next = pos + 1 < data.Length ? data[pos + 1] : 0xD9;
                        if (next == 0)
                            pos += 2;
                        else
                        {
                            // A marker: don't consume it, and feed zeros from here on.
                            hitMarker = true;
                            b = 0;
                        }
                    }
                    else
                        pos++;
                }
                else if (!hitMarker)
                    hitMarker = true;
                bitBuf |= (uint)b << (24 - bitCount);
                bitCount += 8;
            }
        }

        private int ReadBit()
        {
            if (bitCount < 1)
                FillBits();
            int bit = (int)(bitBuf >> 31);
            bitBuf <<= 1;
            bitCount--;
            return bit;
        }

        private int ReadBits(int n)
        {
            if (n == 0)
                return 0;
            if (bitCount < n)
                FillBits();
            int val = (int)(bitBuf >> (32 - n));
            bitBuf <<= n;
            bitCount -= n;
            return val;
        }

        private int DecodeHuffman(HuffmanTable? table)
        {
            if (table == null)
                throw new ImageDecodeException("JPEG scan uses an undefined Huffman table.");
            if (bitCount < 16)
                FillBits();
            int look = table.Lookup[bitBuf >> (32 - HuffmanTable.LookupBits)];
            if (look != 0)
            {
                int len = look >> 8;
                bitBuf <<= len;
                bitCount -= len;
                return look & 0xFF;
            }
            // Longer code: continue bit by bit.
            int code = (int)(bitBuf >> (32 - HuffmanTable.LookupBits));
            int l = HuffmanTable.LookupBits;
            bitBuf <<= l;
            bitCount -= l;
            while (code > table.MaxCode[l])
            {
                code = (code << 1) | ReadBit();
                l++;
                if (l > 16)
                    throw new ImageDecodeException("Invalid Huffman code in JPEG stream.");
            }
            int idx = table.ValPtr[l] + code;
            if (idx < 0 || idx >= table.Values.Length)
                throw new ImageDecodeException("Invalid Huffman code in JPEG stream.");
            return table.Values[idx];
        }

        private int ReceiveExtend(int s)
        {
            if (s == 0)
                return 0;
            if (s >= 16)
            {
                // Only valid in lossless mode, where it means a difference of 32768
                // with no additional bits.
                return 32768;
            }
            int v = ReadBits(s);
            return v < (1 << (s - 1)) ? v - (1 << s) + 1 : v;
        }

        /// <summary>
        /// Handle a restart marker: discard remaining bits, skip past the marker, and
        /// reset the prediction state.
        /// </summary>
        private void ProcessRestart(Component[] scanComps)
        {
            ResetBits();
            // Find the RSTn marker, skipping any garbage before it.
            while (pos < data.Length - 1)
            {
                if (data[pos] == 0xFF && data[pos + 1] >= 0xD0 && data[pos + 1] <= 0xD7)
                {
                    pos += 2;
                    break;
                }
                if (data[pos] == 0xFF && data[pos + 1] != 0 && data[pos + 1] != 0xFF)
                    break; // some other marker; the data is probably truncated
                pos++;
            }
            foreach (var c in scanComps)
                c.Pred = 0;
            eobRun = 0;
        }

        #endregion

        #region DCT decoding

        private void DecodeDctScan(Component[] scanComps, int ss, int se, int ah, int al)
        {
            foreach (var c in scanComps)
                c.Pred = 0;
            eobRun = 0;

            Action<Component, int> decodeBlock;
            if (!progressive)
                decodeBlock = (c, offset) => DecodeBaseline(c, offset);
            else if (ss == 0)
                decodeBlock = ah == 0 ? (c, offset) => DecodeDcFirst(c, offset, al) : (c, offset) => DecodeDcRefine(c, offset, al);
            else
                decodeBlock = ah == 0 ? (c, offset) => DecodeAcFirst(c, offset, ss, se, al) : (c, offset) => DecodeAcRefine(c, offset, ss, se, al);

            int mcu = 0;
            if (scanComps.Length == 1)
            {
                // A non-interleaved scan covers only the blocks that are actually in the image.
                var c = scanComps[0];
                int total = c.BlocksPerLine * c.BlocksPerColumn;
                while (mcu < total)
                {
                    int row = mcu / c.BlocksPerLine, col = mcu % c.BlocksPerLine;
                    decodeBlock(c, (row * c.BlocksPerLineAlloc + col) * 64);
                    mcu++;
                    if (restartInterval > 0 && mcu % restartInterval == 0 && mcu < total)
                        ProcessRestart(scanComps);
                }
            }
            else
            {
                int total = mcusPerLine * mcusPerColumn;
                while (mcu < total)
                {
                    int mcuRow = mcu / mcusPerLine, mcuCol = mcu % mcusPerLine;
                    foreach (var c in scanComps)
                    {
                        for (int v = 0; v < c.V; v++)
                        {
                            for (int h = 0; h < c.H; h++)
                            {
                                int row = mcuRow * c.V + v, col = mcuCol * c.H + h;
                                decodeBlock(c, (row * c.BlocksPerLineAlloc + col) * 64);
                            }
                        }
                    }
                    mcu++;
                    if (restartInterval > 0 && mcu % restartInterval == 0 && mcu < total)
                        ProcessRestart(scanComps);
                }
            }
        }

        private void DecodeBaseline(Component c, int offset)
        {
            var coefs = c.Coefficients;
            int t = DecodeHuffman(c.DcTable);
            c.Pred += ReceiveExtend(t);
            coefs[offset] = c.Pred;
            int k = 1;
            while (k < 64)
            {
                int rs = DecodeHuffman(c.AcTable);
                int s = rs & 15, r = rs >> 4;
                if (s == 0)
                {
                    if (r < 15)
                        break;
                    k += 16;
                    continue;
                }
                k += r;
                if (k > 63)
                    break;
                coefs[offset + ZigZag[k]] = ReceiveExtend(s);
                k++;
            }
        }

        private void DecodeDcFirst(Component c, int offset, int al)
        {
            int t = DecodeHuffman(c.DcTable);
            c.Pred += ReceiveExtend(t) * (1 << al);
            c.Coefficients[offset] = c.Pred;
        }

        private void DecodeDcRefine(Component c, int offset, int al)
        {
            if (ReadBit() != 0)
                c.Coefficients[offset] |= 1 << al;
        }

        private void DecodeAcFirst(Component c, int offset, int ss, int se, int al)
        {
            if (eobRun > 0)
            {
                eobRun--;
                return;
            }
            var coefs = c.Coefficients;
            int k = ss;
            while (k <= se)
            {
                int rs = DecodeHuffman(c.AcTable);
                int s = rs & 15, r = rs >> 4;
                if (s == 0)
                {
                    if (r < 15)
                    {
                        eobRun = (1 << r) - 1;
                        if (r > 0)
                            eobRun += ReadBits(r);
                        break;
                    }
                    k += 16;
                    continue;
                }
                k += r;
                if (k > 63)
                    break;
                coefs[offset + ZigZag[k]] = ReceiveExtend(s) * (1 << al);
                k++;
            }
        }

        private void DecodeAcRefine(Component c, int offset, int ss, int se, int al)
        {
            var coefs = c.Coefficients;
            int p1 = 1 << al, m1 = -1 << al;
            int k = ss;

            if (eobRun <= 0)
            {
                for (; k <= se; k++)
                {
                    int rs = DecodeHuffman(c.AcTable);
                    int s = rs & 15, r = rs >> 4;
                    int newValue = 0;
                    if (s == 0)
                    {
                        if (r < 15)
                        {
                            eobRun = 1 << r;
                            if (r > 0)
                                eobRun += ReadBits(r);
                            break;
                        }
                        // r == 15: skip 16 zero coefficients (refining nonzero ones along the way)
                    }
                    else
                    {
                        newValue = ReadBit() != 0 ? p1 : m1;
                    }

                    // Advance over r zero-valued coefficients, refining any nonzero ones passed.
                    while (k <= se)
                    {
                        int z = offset + ZigZag[k];
                        if (coefs[z] != 0)
                        {
                            if (ReadBit() != 0 && (coefs[z] & p1) == 0)
                                coefs[z] += coefs[z] >= 0 ? p1 : m1;
                        }
                        else
                        {
                            if (r == 0)
                            {
                                if (newValue != 0)
                                    coefs[z] = newValue;
                                break;
                            }
                            r--;
                        }
                        k++;
                    }
                }
            }

            if (eobRun > 0)
            {
                // Refine the remaining nonzero coefficients in this band.
                for (; k <= se; k++)
                {
                    int z = offset + ZigZag[k];
                    if (coefs[z] != 0)
                    {
                        if (ReadBit() != 0 && (coefs[z] & p1) == 0)
                            coefs[z] += coefs[z] >= 0 ? p1 : m1;
                    }
                }
                eobRun--;
            }
        }

        private static float[] BuildIdctTable()
        {
            var t = new float[64];
            for (int x = 0; x < 8; x++)
            {
                for (int u = 0; u < 8; u++)
                {
                    double cu = u == 0 ? Math.Sqrt(0.5) : 1.0;
                    t[x * 8 + u] = (float)(cu / 2.0 * Math.Cos((2 * x + 1) * u * Math.PI / 16.0));
                }
            }
            return t;
        }

        private CodecImage BuildDctImage()
        {
            int maxVal = (1 << precision) - 1;
            int levelShift = 1 << (precision - 1);
            var block = new float[64];
            var temp = new float[64];

            // Inverse-transform each component into a plane of samples.
            var planes = new int[components.Length][];
            for (int ci = 0; ci < components.Length; ci++)
            {
                var c = components[ci];
                var q = quantTables[c.QuantTable] ?? throw new ImageDecodeException("JPEG image uses an undefined quantization table.");
                int planeWidth = c.BlocksPerLineAlloc * 8;
                var plane = new int[planeWidth * c.BlocksPerColumnAlloc * 8];
                for (int by = 0; by < c.BlocksPerColumn; by++)
                {
                    for (int bx = 0; bx < c.BlocksPerLine; bx++)
                    {
                        int offset = (by * c.BlocksPerLineAlloc + bx) * 64;
                        for (int i = 0; i < 64; i++)
                            block[i] = c.Coefficients[offset + i] * q[i];

                        // Rows first: temp[y][x] = sum_u block[y][u] * T[x][u]
                        for (int y = 0; y < 8; y++)
                        {
                            for (int x = 0; x < 8; x++)
                            {
                                float sum = 0;
                                for (int u = 0; u < 8; u++)
                                    sum += block[y * 8 + u] * IdctTable[x * 8 + u];
                                temp[y * 8 + x] = sum;
                            }
                        }
                        // Then columns.
                        for (int x = 0; x < 8; x++)
                        {
                            for (int y = 0; y < 8; y++)
                            {
                                float sum = 0;
                                for (int v = 0; v < 8; v++)
                                    sum += temp[v * 8 + x] * IdctTable[y * 8 + v];
                                int val = (int)MathF.Round(sum) + levelShift;
                                plane[(by * 8 + y) * planeWidth + bx * 8 + x] = val < 0 ? 0 : val > maxVal ? maxVal : val;
                            }
                        }
                    }
                }
                planes[ci] = plane;
            }

            var strides = new int[components.Length];
            var hScale = new int[components.Length];
            var vScale = new int[components.Length];
            for (int ci = 0; ci < components.Length; ci++)
            {
                var c = components[ci];
                strides[ci] = c.BlocksPerLineAlloc * 8;
                hScale[ci] = Math.Max(1, maxH / c.H);
                vScale[ci] = Math.Max(1, maxV / c.V);
                bool h2 = hScale[ci] == 2 && maxH == c.H * 2, v2 = vScale[ci] == 2 && maxV == c.V * 2;
                if ((h2 || v2) && (hScale[ci] <= 2 && vScale[ci] <= 2))
                {
                    planes[ci] = UpsampleFancy(planes[ci], strides[ci], c.Width, c.Height, h2, v2, out strides[ci]);
                    hScale[ci] = 1;
                    vScale[ci] = 1;
                }
            }
            var result = Interleave(planes, strides, hScale, vScale);

            if (components.Length == 3 && UseColorTransform())
            {
                YccToRgb(result.Samples, maxVal);
                result.ColorConverted = true;
            }
            return result;
        }

        private bool UseColorTransform()
        {
            // The same rules as libjpeg.
            if (adobeTransform >= 0)
                return adobeTransform != 0;
            if (jfif)
                return true;
            if (components[0].Id == 'R' && components[1].Id == 'G' && components[2].Id == 'B')
                return false;
            return true;
        }

        private static void YccToRgb(int[] samples, int maxVal)
        {
            // Fixed-point arithmetic, as in libjpeg, to produce identical results.
            const int half = 1 << 15;
            const long crR = 91881, cbB = 116130, crG = -46802, cbG = -22554;   // 1.402, 1.772, -0.71414, -0.34414 (x 65536)
            int center = (maxVal + 1) / 2;
            for (int i = 0; i + 2 < samples.Length; i += 3)
            {
                int y = samples[i], cb = samples[i + 1] - center, cr = samples[i + 2] - center;
                int r = y + (int)((crR * cr + half) >> 16);
                int g = y + (int)((cbG * cb + half + crG * cr) >> 16);
                int b = y + (int)((cbB * cb + half) >> 16);
                samples[i] = r < 0 ? 0 : r > maxVal ? maxVal : r;
                samples[i + 1] = g < 0 ? 0 : g > maxVal ? maxVal : g;
                samples[i + 2] = b < 0 ? 0 : b > maxVal ? maxVal : b;
            }
        }

        /// <summary>
        /// Combine the per-component planes into interleaved samples at full resolution.
        /// Components that are still subsampled (by hScale, vScale) are upsampled by
        /// replication.
        /// </summary>
        private CodecImage Interleave(int[][] planes, int[] strides, int[] hScale, int[] vScale)
        {
            int n = components.Length;
            var samples = new int[width * height * n];
            for (int ci = 0; ci < n; ci++)
            {
                var plane = planes[ci];
                int stride = strides[ci], hs = hScale[ci], vs = vScale[ci];
                for (int y = 0; y < height; y++)
                {
                    int rowOffset = y / vs * stride;
                    int dst = y * width * n + ci;
                    if (hs == 1)
                    {
                        for (int x = 0; x < width; x++, dst += n)
                            samples[dst] = plane[rowOffset + x];
                    }
                    else
                    {
                        for (int x = 0; x < width; x++, dst += n)
                            samples[dst] = plane[rowOffset + x / hs];
                    }
                }
            }
            return new CodecImage(width, height, n, precision, false, samples);
        }

        /// <summary>
        /// Upsamples a component by a factor of two horizontally and/or vertically, using
        /// the same triangular ("fancy") interpolation as libjpeg, so that our output
        /// matches what most other decoders produce.
        /// </summary>
        private static int[] UpsampleFancy(int[] plane, int stride, int w, int h, bool horizontal, bool vertical, out int newStride)
        {
            int outW = horizontal ? w * 2 : w, outH = vertical ? h * 2 : h;
            var output = new int[outW * outH];
            newStride = outW;
            var colSum = new int[w];

            for (int oy = 0; oy < outH; oy++)
            {
                int y = vertical ? oy / 2 : oy;
                int row = y * stride;
                int outRow = oy * outW;
                if (vertical)
                {
                    // Weight the nearer input row 3:1 against the farther one.
                    int near = (oy & 1) == 0 ? Math.Max(y - 1, 0) : Math.Min(y + 1, h - 1);
                    for (int x = 0; x < w; x++)
                        colSum[x] = plane[row + x] * 3 + plane[near * stride + x];
                    if (!horizontal)
                    {
                        int bias = (oy & 1) == 0 ? 1 : 2;
                        for (int x = 0; x < w; x++)
                            output[outRow + x] = (colSum[x] + bias) >> 2;
                        continue;
                    }
                    if (w == 1)
                    {
                        output[outRow] = output[outRow + 1] = (colSum[0] * 4 + 8) >> 4;
                        continue;
                    }
                    output[outRow] = (colSum[0] * 4 + 8) >> 4;
                    output[outRow + 1] = (colSum[0] * 3 + colSum[1] + 7) >> 4;
                    for (int x = 1; x < w - 1; x++)
                    {
                        output[outRow + x * 2] = (colSum[x] * 3 + colSum[x - 1] + 8) >> 4;
                        output[outRow + x * 2 + 1] = (colSum[x] * 3 + colSum[x + 1] + 7) >> 4;
                    }
                    output[outRow + (w - 1) * 2] = (colSum[w - 1] * 3 + colSum[w - 2] + 8) >> 4;
                    output[outRow + (w - 1) * 2 + 1] = (colSum[w - 1] * 4 + 7) >> 4;
                }
                else
                {
                    if (w == 1)
                    {
                        output[outRow] = output[outRow + 1] = plane[row];
                        continue;
                    }
                    output[outRow] = plane[row];
                    output[outRow + 1] = (plane[row] * 3 + plane[row + 1] + 2) >> 2;
                    for (int x = 1; x < w - 1; x++)
                    {
                        int v = plane[row + x] * 3;
                        output[outRow + x * 2] = (v + plane[row + x - 1] + 1) >> 2;
                        output[outRow + x * 2 + 1] = (v + plane[row + x + 1] + 2) >> 2;
                    }
                    output[outRow + (w - 1) * 2] = (plane[row + w - 1] * 3 + plane[row + w - 2] + 1) >> 2;
                    output[outRow + (w - 1) * 2 + 1] = plane[row + w - 1];
                }
            }
            return output;
        }

        #endregion

        #region Lossless decoding

        private void DecodeLosslessScan(Component[] scanComps, int predictor, int pointTransform)
        {
            if (predictor < 1 || predictor > 7)
                predictor = 1;
            int initial = 1 << (precision - pointTransform - 1);
            int mask = 0xFFFF;

            // Each component is predicted within its own plane. With several components
            // in a scan, each MCU holds H x V samples of each component, in order.
            int total = scanComps.Length == 1
                ? scanComps[0].Width * scanComps[0].Height
                : mcusPerLine * mcusPerColumn;
            int unitsPerLine = scanComps.Length == 1 ? scanComps[0].Width : mcusPerLine;

            // The row at which the current restart interval began; prediction on that
            // row behaves as if it were the first row of the image.
            int restartRow = 0, restartMcu = 0;
            int mcu = 0;
            while (mcu < total)
            {
                int mcuRow = mcu / unitsPerLine, mcuCol = mcu % unitsPerLine;
                foreach (var c in scanComps)
                {
                    int stride = c.BlocksPerLineAlloc;
                    int hCount = scanComps.Length == 1 ? 1 : c.H;
                    int vCount = scanComps.Length == 1 ? 1 : c.V;
                    for (int v = 0; v < vCount; v++)
                    {
                        for (int h = 0; h < hCount; h++)
                        {
                            int y = mcuRow * vCount + v, x = mcuCol * hCount + h;
                            int idx = y * stride + x;
                            int pred;
                            bool firstRow = y == restartRow * vCount;
                            if (firstRow && (x == 0 || (mcu == restartMcu && h == 0)))
                                pred = initial;
                            else if (firstRow)
                                pred = c.Samples[idx - 1];
                            else if (x == 0)
                                pred = c.Samples[idx - stride];
                            else
                            {
                                int ra = c.Samples[idx - 1], rb = c.Samples[idx - stride], rc = c.Samples[idx - stride - 1];
                                pred = predictor switch
                                {
                                    1 => ra,
                                    2 => rb,
                                    3 => rc,
                                    4 => ra + rb - rc,
                                    5 => ra + ((rb - rc) >> 1),
                                    6 => rb + ((ra - rc) >> 1),
                                    _ => (ra + rb) >> 1
                                };
                            }
                            int diff = ReceiveExtend(DecodeHuffman(c.DcTable));
                            c.Samples[idx] = (pred + diff) & mask;
                        }
                    }
                }
                mcu++;
                if (restartInterval > 0 && mcu % restartInterval == 0 && mcu < total)
                {
                    ProcessRestart(scanComps);
                    restartRow = mcu / unitsPerLine;
                    restartMcu = mcu;
                }
            }

            if (pointTransform > 0)
            {
                foreach (var c in scanComps)
                    for (int i = 0; i < c.Samples.Length; i++)
                        c.Samples[i] <<= pointTransform;
            }
        }

        private CodecImage BuildLosslessImage()
        {
            int maxVal = precision >= 16 ? 0xFFFF : (1 << precision) - 1;
            var planes = new int[components.Length][];
            for (int i = 0; i < components.Length; i++)
            {
                var s = components[i].Samples;
                for (int j = 0; j < s.Length; j++)
                    s[j] &= maxVal;
                planes[i] = s;
            }
            var strides = Array.ConvertAll(components, c => c.BlocksPerLineAlloc);
            var hScale = Array.ConvertAll(components, c => Math.Max(1, maxH / c.H));
            var vScale = Array.ConvertAll(components, c => Math.Max(1, maxV / c.V));
            return Interleave(planes, strides, hScale, vScale);
        }

        #endregion
    }
}
