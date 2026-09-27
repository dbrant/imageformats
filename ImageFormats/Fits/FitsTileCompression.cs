using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

/*

Decoder for tile-compressed FITS images (as produced by fpack, usually with the
.fz extension), per the FITS tiled image compression convention (FITS standard
section 10).

The image is split into rectangular tiles (by default, one row each), and each
tile is compressed separately and stored as one row of a binary table, in a
variable-length array column (COMPRESSED_DATA) whose data lives in the table's
"heap" area. The table header describes the original image with ZBITPIX,
ZNAXISn and ZTILEn keywords, and the compression algorithm with ZCMPTYPE:
RICE_1, GZIP_1, GZIP_2, PLIO_1, HCOMPRESS_1 or NOCOMPRESS.

Floating-point images are usually quantized to integers before compression: each
tile has its own scale and zero point (ZSCALE and ZZERO columns), and random
dithering may have been added before quantizing, which is subtracted again here
using the same pseudo-random sequence. Tiles that couldn't be quantized are
stored gzipped as raw floats instead, in a GZIP_COMPRESSED_DATA column.

Copyright 2026+ Dmitry Brant
https://dmitrybrant.com

License: MIT
*/

namespace DmitryBrant.ImageFormats
{
    internal static class FitsTileCompression
    {
        // Special quantized values: undefined pixels, and (with SUBTRACTIVE_DITHER_2)
        // pixels that were exactly zero.
        private const int NullValue = -2147483647;
        private const int ZeroValue = -2147483646;

        // The sequence of pseudo-random numbers used for dithering.
        private const int NumRandom = 10000;
        private static readonly double[] RandomValues = BuildRandomValues();

        private static double[] BuildRandomValues()
        {
            // Park and Miller's minimal standard generator, as specified by the convention.
            // CFITSIO keeps the values in single precision (but does its arithmetic with
            // them in double precision), so to get exactly the same results, so do we.
            const double a = 16807.0, m = 2147483647.0;
            double seed = 1;
            var values = new double[NumRandom];
            for (int i = 0; i < NumRandom; i++)
            {
                double temp = a * seed;
                seed = temp - m * Math.Floor(temp / m);
                values[i] = (float)(seed / m);
            }
            return values;
        }

        /// <summary>
        /// A column of the binary table.
        /// </summary>
        private sealed class Column
        {
            public int Offset;      // byte offset within a row
            public char Type;       // data type, or 'P'/'Q' for a variable-length array
            public char ArrayType;  // element type of a variable-length array
        }

        /// <summary>
        /// Reads one 2-D plane of a compressed image, as physical values (with undefined
        /// values as NaN).
        /// </summary>
        public static float[] ReadPlane(Stream stream, FitsHdu hdu, long plane)
        {
            int width = hdu.Width, height = hdu.Height;
            int naxis = hdu.Axes.Length;

            // The table, and the heap that follows it.
            long rowLength = (long)hdu.GetNumber("NAXIS1", 0), numRows = (long)hdu.GetNumber("NAXIS2", 0);
            if (rowLength <= 0 || numRows <= 0 || hdu.DataSize > int.MaxValue)
                throw new ImageDecodeException("Invalid compressed FITS image.");
            var data = new byte[hdu.DataSize];
            stream.Seek(hdu.DataStart, SeekOrigin.Begin);
            int total = 0;
            while (total < data.Length)
            {
                int n = stream.Read(data, total, data.Length - total);
                if (n <= 0)
                    break;
                total += n;
            }
            long heapStart = (long)hdu.GetNumber("THEAP", rowLength * numRows);

            var compressed = FindColumn(hdu, "COMPRESSED_DATA");
            var gzipped = FindColumn(hdu, "GZIP_COMPRESSED_DATA");
            var uncompressed = FindColumn(hdu, "UNCOMPRESSED_DATA");
            var zscaleCol = FindColumn(hdu, "ZSCALE");
            var zzeroCol = FindColumn(hdu, "ZZERO");
            var zblankCol = FindColumn(hdu, "ZBLANK");
            if (compressed == null)
                throw new ImageDecodeException("Compressed FITS image has no COMPRESSED_DATA column.");

            string algorithm = hdu.GetString("ZCMPTYPE", "").ToUpperInvariant();
            if (algorithm == "RICE_ONE")
                algorithm = "RICE_1";
            int blockSize = (int)GetParameter(hdu, "BLOCKSIZE", 32);
            int bytePix = (int)GetParameter(hdu, "BYTEPIX", 4);
            bool smooth = GetParameter(hdu, "SMOOTH", 0) != 0;

            // Quantization of floating-point data.
            bool isFloat = hdu.Bitpix < 0;
            string quantize = hdu.GetString("ZQUANTIZ", "NO_DITHER").ToUpperInvariant();
            bool dither = quantize.StartsWith("SUBTRACTIVE_DITHER");
            bool dither2 = quantize == "SUBTRACTIVE_DITHER_2";
            // Floating-point data can also be compressed losslessly, without quantization.
            bool quantized = quantize != "NONE" && (zscaleCol != null || hdu.Cards.ContainsKey("ZSCALE"));
            long dither0 = (long)hdu.GetNumber("ZDITHER0", 1);

            // Scaling and undefined values of integer data.
            double bzero = hdu.GetNumber("BZERO", 0), bscale = hdu.GetNumber("BSCALE", 1);
            if (bscale == 0 || double.IsNaN(bscale))
                bscale = 1;
            bool hasBlank = hdu.Cards.ContainsKey("ZBLANK") || (!isFloat && hdu.Cards.ContainsKey("BLANK"));
            long blank = (long)hdu.GetNumber("ZBLANK", hdu.GetNumber("BLANK", NullValue));

            // The tile grid.
            var tileSize = new long[naxis];
            var tileCount = new long[naxis];
            for (int i = 0; i < naxis; i++)
            {
                tileSize[i] = Math.Max(1, (long)hdu.GetNumber("ZTILE" + (i + 1), i == 0 ? hdu.Axes[0] : 1));
                tileCount[i] = (hdu.Axes[i] + tileSize[i] - 1) / tileSize[i];
            }

            // Which tiles cover the requested plane: work out its coordinates along the
            // higher axes.
            var planeCoord = new long[naxis];
            long rem = plane;
            for (int i = 2; i < naxis; i++)
            {
                planeCoord[i] = rem % hdu.Axes[i];
                rem /= hdu.Axes[i];
            }

            var output = new float[(long)width * height];
            Array.Fill(output, float.NaN);
            long tilesInPlane = tileCount[0] * (naxis > 1 ? tileCount[1] : 1);
            for (long t = 0; t < tilesInPlane; t++)
            {
                // Row number of this tile in the table.
                long tx = t % tileCount[0], ty = t / tileCount[0];
                long row = tx + ty * tileCount[0];
                long stride = tileCount[0] * (naxis > 1 ? tileCount[1] : 1);
                long depthOffset = 0;   // position of the plane within the tile, along the higher axes
                long depthStride = 1;
                for (int i = 2; i < naxis; i++)
                {
                    row += planeCoord[i] / tileSize[i] * stride;
                    stride *= tileCount[i];
                    depthOffset += planeCoord[i] % tileSize[i] * depthStride;
                    depthStride *= Math.Min(tileSize[i], hdu.Axes[i] - planeCoord[i] / tileSize[i] * tileSize[i]);
                }
                if (row >= numRows)
                    continue;

                // Actual extent of this tile (tiles at the edges may be smaller).
                var extent = new long[naxis];
                long tilePixels = 1;
                for (int i = 0; i < naxis; i++)
                {
                    long first = i == 0 ? tx * tileSize[0] : i == 1 ? ty * tileSize[1] : planeCoord[i] / tileSize[i] * tileSize[i];
                    extent[i] = Math.Min(tileSize[i], hdu.Axes[i] - first);
                    tilePixels *= extent[i];
                }
                int tileW = (int)extent[0], tileH = naxis > 1 ? (int)extent[1] : 1;

                long rowStart = row * rowLength;
                double zscale = zscaleCol != null ? ReadDouble(data, rowStart, zscaleCol) : hdu.GetNumber("ZSCALE", 1);
                double zzero = zzeroCol != null ? ReadDouble(data, rowStart, zzeroCol) : hdu.GetNumber("ZZERO", 0);
                long tileBlank = zblankCol != null ? (long)ReadDouble(data, rowStart, zblankCol) : blank;
                bool tileHasBlank = hasBlank || zblankCol != null;

                var values = new double[tilePixels];
                byte[] tileBytes = GetArray(data, rowStart, heapStart, compressed, out _);
                if (tileBytes.Length > 0 && isFloat && !quantized)
                {
                    // Raw floating-point values, compressed with GZIP or not at all.
                    if (algorithm is not ("GZIP_1" or "GZIP_2" or "NOCOMPRESS"))
                        throw new ImageDecodeException("Unsupported compression of unquantized FITS data: " + algorithm);
                    byte[] raw = algorithm == "NOCOMPRESS" ? tileBytes : Gunzip(tileBytes);
                    int size = hdu.Bitpix == -64 ? 8 : 4;
                    if (algorithm == "GZIP_2")
                        raw = Unshuffle(raw, tilePixels, size);
                    for (long i = 0; i < tilePixels && (i + 1) * size <= raw.Length; i++)
                    {
                        double v = size == 8 ? BinaryPrimitives.ReadDoubleBigEndian(raw.AsSpan((int)(i * size))) : BinaryPrimitives.ReadSingleBigEndian(raw.AsSpan((int)(i * size)));
                        values[i] = double.IsInfinity(v) ? double.NaN : v;
                    }
                }
                else if (tileBytes.Length > 0)
                {
                    long[] ints = Decompress(algorithm, tileBytes, tilePixels, tileW, (int)(tilePixels / tileW), blockSize, bytePix, hdu.Bitpix, isFloat, smooth);
                    if (isFloat)
                    {
                        // Undo the quantization.
                        long iseed = 0;
                        int nextRand = 0;
                        if (dither)
                        {
                            iseed = (row + dither0 - 1) % NumRandom;
                            nextRand = (int)(RandomValues[iseed] * 500);
                        }
                        for (long i = 0; i < tilePixels; i++)
                        {
                            long q = ints[i];
                            if (q == NullValue || (tileHasBlank && q == tileBlank))
                                values[i] = double.NaN;
                            else if (dither2 && q == ZeroValue)
                                values[i] = 0;
                            else if (dither)
                                values[i] = (q - RandomValues[nextRand] + 0.5) * zscale + zzero;
                            else
                                values[i] = q * zscale + zzero;
                            if (dither)
                            {
                                nextRand++;
                                if (nextRand == NumRandom)
                                {
                                    iseed = (iseed + 1) % NumRandom;
                                    nextRand = (int)(RandomValues[iseed] * 500);
                                }
                            }
                        }
                    }
                    else
                    {
                        for (long i = 0; i < tilePixels; i++)
                        {
                            // Lossy (HCOMPRESS) values can stray outside the range of the image's
                            // data type; they wrap around, as they do in CFITSIO.
                            long q = hdu.Bitpix switch
                            {
                                8 => (byte)ints[i],
                                16 => (short)ints[i],
                                32 => (int)ints[i],
                                _ => ints[i]
                            };
                            values[i] = tileHasBlank && q == tileBlank ? double.NaN : bzero + bscale * q;
                        }
                    }
                }
                else
                {
                    // A tile that couldn't be compressed with the main algorithm: stored as
                    // gzipped raw values, or as a plain array of values.
                    byte[] raw;
                    char type;
                    if (gzipped != null && (raw = GetArray(data, rowStart, heapStart, gzipped, out _)).Length > 0)
                    {
                        raw = Gunzip(raw);
                        int size = (int)Math.Max(1, raw.Length / Math.Max(1, tilePixels));
                        type = size == 8 ? (isFloat ? 'D' : 'K') : size == 4 ? (isFloat ? 'E' : 'J') : size == 2 ? 'I' : 'B';
                    }
                    else if (uncompressed != null)
                    {
                        raw = GetArray(data, rowStart, heapStart, uncompressed, out type);
                    }
                    else
                    {
                        continue;   // no data for this tile
                    }
                    int elemSize = ElementSize(type);
                    for (long i = 0; i < tilePixels && (i + 1) * elemSize <= raw.Length; i++)
                    {
                        var span = raw.AsSpan((int)(i * elemSize));
                        double v = type switch
                        {
                            'B' => raw[i],
                            'I' => BinaryPrimitives.ReadInt16BigEndian(span),
                            'J' => BinaryPrimitives.ReadInt32BigEndian(span),
                            'K' => BinaryPrimitives.ReadInt64BigEndian(span),
                            'E' => BinaryPrimitives.ReadSingleBigEndian(span),
                            _ => BinaryPrimitives.ReadDoubleBigEndian(span)
                        };
                        if (isFloat)
                            values[i] = double.IsInfinity(v) ? double.NaN : v;
                        else
                            values[i] = tileHasBlank && (long)v == tileBlank ? double.NaN : bzero + bscale * v;
                    }
                }

                // Copy this plane's part of the tile into the output.
                long srcBase = depthOffset * tileW * tileH;
                long x0 = tx * tileSize[0], y0 = ty * (naxis > 1 ? tileSize[1] : 1);
                for (int y = 0; y < tileH; y++)
                {
                    long dst = (y0 + y) * width + x0;
                    long src = srcBase + (long)y * tileW;
                    for (int x = 0; x < tileW; x++)
                        output[dst + x] = (float)values[src + x];
                }
            }
            return output;
        }

        /// <summary>
        /// Gets a compression parameter, given as ZNAMEn / ZVALn keyword pairs.
        /// </summary>
        private static double GetParameter(FitsHdu hdu, string name, double defaultValue)
        {
            for (int i = 1; i < 100; i++)
            {
                if (!hdu.Cards.TryGetValue("ZNAME" + i, out var n))
                    break;
                if (string.Equals(n.Trim(), name, StringComparison.OrdinalIgnoreCase))
                {
                    // The value may also be a logical (T or F), e.g. for SMOOTH.
                    string value = hdu.GetString("ZVAL" + i, "");
                    if (value == "T")
                        return 1;
                    if (value == "F")
                        return 0;
                    return hdu.GetNumber("ZVAL" + i, defaultValue);
                }
            }
            return defaultValue;
        }

        #region Binary table access

        private static int ElementSize(char type)
        {
            return type switch
            {
                'L' or 'B' or 'A' or 'X' => 1,
                'I' => 2,
                'J' or 'E' => 4,
                'K' or 'D' or 'C' => 8,
                'M' => 16,
                'P' => 8,
                'Q' => 16,
                _ => 0
            };
        }

        /// <summary>
        /// Finds a column by name, and works out its position within a row from the
        /// TFORMn keywords of all the columns before it.
        /// </summary>
        private static Column? FindColumn(FitsHdu hdu, string name)
        {
            int fields = (int)hdu.GetNumber("TFIELDS", 0);
            int offset = 0;
            for (int i = 1; i <= fields; i++)
            {
                string form = hdu.GetString("TFORM" + i, "").Trim().ToUpperInvariant();
                int p = 0;
                while (p < form.Length && char.IsDigit(form[p]))
                    p++;
                int repeat = p > 0 ? int.Parse(form[..p]) : 1;
                if (p >= form.Length)
                    return null;
                char type = form[p];
                char arrayType = type is 'P' or 'Q' && p + 1 < form.Length ? form[p + 1] : ' ';
                int width = type == 'X' ? (repeat + 7) / 8 : repeat * ElementSize(type);
                if (string.Equals(hdu.GetString("TTYPE" + i, "").Trim(), name, StringComparison.OrdinalIgnoreCase))
                    return repeat == 0 ? null : new Column { Offset = offset, Type = type, ArrayType = arrayType };
                offset += width;
            }
            return null;
        }

        private static double ReadDouble(byte[] data, long rowStart, Column col)
        {
            var span = data.AsSpan((int)(rowStart + col.Offset));
            return col.Type switch
            {
                'D' => BinaryPrimitives.ReadDoubleBigEndian(span),
                'E' => BinaryPrimitives.ReadSingleBigEndian(span),
                'J' => BinaryPrimitives.ReadInt32BigEndian(span),
                'K' => BinaryPrimitives.ReadInt64BigEndian(span),
                'I' => BinaryPrimitives.ReadInt16BigEndian(span),
                _ => data[rowStart + col.Offset]
            };
        }

        /// <summary>
        /// Gets the bytes of a variable-length array in the given row, from the heap.
        /// </summary>
        private static byte[] GetArray(byte[] data, long rowStart, long heapStart, Column col, out char elementType)
        {
            elementType = col.ArrayType;
            long count, offset;
            var span = data.AsSpan((int)(rowStart + col.Offset));
            if (col.Type == 'Q')
            {
                count = BinaryPrimitives.ReadInt64BigEndian(span);
                offset = BinaryPrimitives.ReadInt64BigEndian(span[8..]);
            }
            else if (col.Type == 'P')
            {
                count = BinaryPrimitives.ReadUInt32BigEndian(span);
                offset = BinaryPrimitives.ReadUInt32BigEndian(span[4..]);
            }
            else
            {
                throw new ImageDecodeException("Compressed FITS data column is not a variable-length array.");
            }
            long bytes = count * Math.Max(1, ElementSize(elementType));
            long start = heapStart + offset;
            if (count <= 0 || start < 0 || start >= data.Length)
                return Array.Empty<byte>();
            bytes = Math.Min(bytes, data.Length - start);
            var result = new byte[bytes];
            Array.Copy(data, start, result, 0, bytes);
            return result;
        }

        #endregion

        #region Decompression

        /// <summary>
        /// Decompresses a tile into integers (the stored values of an integer image, or the
        /// quantized values of a floating-point one).
        /// </summary>
        private static long[] Decompress(string algorithm, byte[] bytes, long count, int tileW, int tileH, int blockSize, int bytePix, int bitpix, bool isFloat, bool smooth)
        {
            switch (algorithm)
            {
                case "RICE_1":
                    return RiceDecode(bytes, count, blockSize, bytePix);
                case "GZIP_1":
                case "GZIP_2":
                    return ReadIntegers(Gunzip(bytes), count, bitpix, isFloat, algorithm == "GZIP_2");
                case "NOCOMPRESS":
                    return ReadIntegers(bytes, count, bitpix, isFloat, false);
                case "PLIO_1":
                    return PlioDecode(bytes, count);
                case "HCOMPRESS_1":
                    return FitsHcompress.Decode(bytes, tileW, tileH, smooth, bitpix is not (8 or 16));
                default:
                    throw new ImageDecodeException("Unsupported FITS compression: " + algorithm);
            }
        }

        private static byte[] Gunzip(byte[] bytes)
        {
            using var input = new MemoryStream(bytes);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            try
            {
                gzip.CopyTo(output);
            }
            catch (InvalidDataException e)
            {
                // Truncated or corrupt; use what we got.
                Util.log("Error while decompressing FITS tile: " + e.Message);
            }
            return output.ToArray();
        }

        /// <summary>
        /// Reads big-endian integers, whose size is inferred from the amount of data (since
        /// quantized floating-point data is stored as 32-bit integers). With GZIP_2, the
        /// bytes are shuffled: all the most significant bytes first, and so on.
        /// </summary>
        private static long[] ReadIntegers(byte[] bytes, long count, int bitpix, bool isFloat, bool shuffled)
        {
            int size = count > 0 ? (int)(bytes.Length / count) : 0;
            if (size is not (1 or 2 or 4 or 8))
                size = isFloat ? 4 : Math.Abs(bitpix) / 8;
            if (shuffled)
                bytes = Unshuffle(bytes, count, size);
            var values = new long[count];
            for (long i = 0; i < count && (i + 1) * size <= bytes.Length; i++)
            {
                var span = bytes.AsSpan((int)(i * size));
                values[i] = size switch
                {
                    1 => bytes[i],
                    2 => BinaryPrimitives.ReadInt16BigEndian(span),
                    4 => BinaryPrimitives.ReadInt32BigEndian(span),
                    _ => BinaryPrimitives.ReadInt64BigEndian(span)
                };
            }
            return values;
        }

        /// <summary>
        /// Undoes the byte shuffling of GZIP_2: all the most significant bytes of the values
        /// come first, then all the next bytes, and so on.
        /// </summary>
        private static byte[] Unshuffle(byte[] bytes, long count, int size)
        {
            if (size <= 1)
                return bytes;
            var result = new byte[count * size];
            for (long i = 0; i < count; i++)
            {
                for (int k = 0; k < size; k++)
                {
                    long src = k * count + i;
                    if (src < bytes.Length)
                        result[i * size + k] = bytes[src];
                }
            }
            return result;
        }

        /// <summary>
        /// Rice decompression. The first value is stored as-is; after that, the
        /// differences between consecutive values are mapped to non-negative numbers, and
        /// coded in blocks, each of which starts with the number of low bits (fs) that are
        /// sent verbatim, the rest being sent in unary. Special values of fs mean that the
        /// whole block is zero, or that the differences are sent verbatim.
        /// </summary>
        private static long[] RiceDecode(byte[] bytes, long count, int blockSize, int bytePix)
        {
            int fsBits, fsMax, bBits;
            switch (bytePix)
            {
                case 1: fsBits = 3; fsMax = 6; bBits = 8; break;
                case 2: fsBits = 4; fsMax = 14; bBits = 16; break;
                case 8: fsBits = 6; fsMax = 62; bBits = 64; break;
                default: fsBits = 5; fsMax = 25; bBits = 32; bytePix = 4; break;
            }
            if (blockSize <= 0)
                blockSize = 32;
            var values = new long[count];
            if (bytes.Length < bytePix)
                return values;

            // The first value, big-endian.
            ulong last = 0;
            for (int i = 0; i < bytePix; i++)
                last = (last << 8) | bytes[i];

            var bits = new RiceBitReader(bytes, bytePix);
            ulong mask = bytePix == 8 ? ulong.MaxValue : (1UL << (8 * bytePix)) - 1;
            long pos = 0;
            while (pos < count)
            {
                int fs = (int)bits.Read(fsBits) - 1;
                long blockEnd = Math.Min(pos + blockSize, count);
                if (fs < 0)
                {
                    // Low entropy: all differences are zero.
                    for (; pos < blockEnd; pos++)
                        values[pos] = Sign(last, bytePix);
                }
                else if (fs == fsMax)
                {
                    // High entropy: the differences are sent verbatim.
                    for (; pos < blockEnd; pos++)
                    {
                        ulong diff = bits.Read(bBits);
                        last = (last + Unmap(diff)) & mask;
                        values[pos] = Sign(last, bytePix);
                    }
                }
                else
                {
                    for (; pos < blockEnd; pos++)
                    {
                        // Unary-coded high bits (a run of zeros ended by a one), then fs low bits.
                        int zeros = bits.CountZerosAndSkipOne();
                        ulong diff = ((ulong)zeros << fs) | bits.Read(fs);
                        last = (last + Unmap(diff)) & mask;
                        values[pos] = Sign(last, bytePix);
                    }
                }
                if (bits.Exhausted)
                    break;
            }
            return values;
        }

        // Differences are mapped as 0, -1, 1, -2, 2... => 0, 1, 2, 3, 4...
        private static ulong Unmap(ulong diff)
        {
            return (diff & 1) == 0 ? diff >> 1 : ~(diff >> 1);
        }

        // Rice-coded values are unsigned bytes, or signed 16/32/64-bit integers.
        private static long Sign(ulong v, int bytePix)
        {
            return bytePix switch
            {
                1 => (long)v,
                2 => (short)v,
                4 => (int)v,
                _ => (long)v
            };
        }

        private sealed class RiceBitReader
        {
            private readonly byte[] data;
            private int pos;
            private ulong buffer;
            private int count;

            public bool Exhausted => pos >= data.Length && count == 0;

            public RiceBitReader(byte[] data, int start)
            {
                this.data = data;
                pos = start;
            }

            private void Fill()
            {
                while (count <= 56)
                {
                    // Past the end of the data, feed zeros.
                    ulong b = pos < data.Length ? data[pos] : 0UL;
                    pos++;
                    buffer |= b << (56 - count);
                    count += 8;
                }
            }

            public ulong Read(int n)
            {
                if (n == 0)
                    return 0;
                if (n > 56)
                    return (Read(n - 32) << 32) | Read(32);
                if (count < n)
                    Fill();
                ulong v = buffer >> (64 - n);
                buffer <<= n;
                count -= n;
                return v;
            }

            public int CountZerosAndSkipOne()
            {
                int zeros = 0;
                while (true)
                {
                    if (count == 0)
                        Fill();
                    if (buffer == 0)
                    {
                        // A long run of zeros; guard against running off corrupt data forever.
                        zeros += count;
                        count = 0;
                        buffer = 0;
                        if (pos > data.Length + 8)
                            throw new ImageDecodeException("Invalid Rice-compressed FITS data.");
                        continue;
                    }
                    int lz = System.Numerics.BitOperations.LeadingZeroCount(buffer);
                    if (lz >= count)
                    {
                        zeros += count;
                        buffer = 0;
                        count = 0;
                        continue;
                    }
                    zeros += lz;
                    buffer <<= lz + 1;
                    count -= lz + 1;
                    return zeros;
                }
            }
        }

        /// <summary>
        /// Decodes an IRAF PLIO "line list": a sequence of 16-bit instructions that set the
        /// current value, or output runs of zeros or of the current value.
        /// </summary>
        private static long[] PlioDecode(byte[] bytes, long count)
        {
            var values = new long[count];
            int n = bytes.Length / 2;
            if (n < 3)
                return values;
            // ll[1..n], 1-based like the original.
            var ll = new int[n + 1];
            for (int i = 0; i < n; i++)
                ll[i + 1] = BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(i * 2));

            int length, first;
            if (ll[3] > 0)
            {
                length = ll[3];
                first = 4;
            }
            else
            {
                length = n >= 5 ? (ll[5] << 15) + ll[4] : 0;
                first = ll[2] + 1;
            }
            length = Math.Min(length, n);

            long xs = 1, xe = count;
            long op = 0, x1 = 1;
            long pv = 1;
            bool skip = false;
            for (int ip = first; ip <= length; ip++)
            {
                if (skip)
                {
                    skip = false;
                    continue;
                }
                int opcode = (ll[ip] >> 12) & 0xF, data = ll[ip] & 0xFFF;
                switch (opcode)
                {
                    case 0:
                    case 4:
                    case 5:
                        {
                            // A run of zeros (0), of the current value (4), or of zeros ending
                            // with the current value (5).
                            long x2 = x1 + data - 1;
                            long i1 = Math.Max(x1, xs), i2 = Math.Min(x2, xe);
                            long np = i2 - i1 + 1;
                            if (np > 0)
                            {
                                long top = op + np - 1;
                                for (long i = op; i <= top; i++)
                                    values[i] = opcode == 4 ? pv : 0;
                                if (opcode == 5 && i2 == x2)
                                    values[top] = pv;
                                op = top + 1;
                            }
                            x1 = x2 + 1;
                            break;
                        }
                    case 1:
                        // Set the current value, with high bits from the next word.
                        if (ip + 1 <= n)
                            pv = ((long)ll[ip + 1] << 12) + data;
                        skip = true;
                        break;
                    case 2:
                        pv += data;
                        break;
                    case 3:
                        pv -= data;
                        break;
                    case 6:
                    case 7:
                        // Change the current value, and output a single pixel of it.
                        pv += opcode == 6 ? data : -data;
                        if (x1 >= xs && x1 <= xe && op < count)
                            values[op++] = pv;
                        x1++;
                        break;
                }
                if (x1 > xe)
                    break;
            }
            return values;
        }

        #endregion
    }
}
