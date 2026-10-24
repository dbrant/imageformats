#nullable enable
using System;

/*

Decodes one image (page) of a TIFF file.

The image is stored in "chunks": either strips (bands of whole rows) or tiles. With
PlanarConfiguration 1 ("chunky"), the samples of each pixel are stored together;
with PlanarConfiguration 2, each sample (e.g. red, green, blue) is stored in its own
set of chunks, one plane at a time.

We decompress each chunk, undo any predictor, and copy it into place in a buffer per
plane, laid out just as an uncompressed image would be: rows of samples packed at
BitsPerSample bits each, in the file's byte order for samples of 16 bits or more,
and each row padded to a whole byte. Then we read the samples back out of the
planes and convert them to BGRA, according to the PhotometricInterpretation.

YCbCr images with subsampled chroma (and not compressed with JPEG, which takes care
of that itself) are stored as "data units" of a block of luma samples followed by
one sample each of Cb and Cr. We expand those into plain YCbCr pixels when copying
each chunk into place.

Copyright 2026+ Dmitry Brant
https://dmitrybrant.com

License: MIT
*/

namespace DmitryBrant.ImageFormats
{
    internal sealed class TiffPage
    {
        private const int TagWidth = 256;
        private const int TagHeight = 257;
        private const int TagBitsPerSample = 258;
        private const int TagCompression = 259;
        private const int TagPhotometric = 262;
        private const int TagFillOrder = 266;
        private const int TagStripOffsets = 273;
        private const int TagOrientation = 274;
        private const int TagSamplesPerPixel = 277;
        private const int TagRowsPerStrip = 278;
        private const int TagStripByteCounts = 279;
        private const int TagPlanarConfig = 284;
        private const int TagT4Options = 292;
        private const int TagPredictor = 317;
        private const int TagColorMap = 320;
        private const int TagTileWidth = 322;
        private const int TagTileLength = 323;
        private const int TagTileOffsets = 324;
        private const int TagTileByteCounts = 325;
        private const int TagInkSet = 332;
        private const int TagExtraSamples = 338;
        private const int TagSampleFormat = 339;
        private const int TagJpegTables = 347;
        private const int TagJpegInterchangeFormat = 513;
        private const int TagJpegInterchangeFormatLength = 514;
        private const int TagYCbCrCoefficients = 529;
        private const int TagYCbCrSubSampling = 530;
        private const int TagReferenceBlackWhite = 532;

        private const int CompressionNone = 1;
        private const int CompressionCcittRle = 2;
        private const int CompressionCcittG3 = 3;
        private const int CompressionCcittG4 = 4;
        private const int CompressionLzw = 5;
        private const int CompressionOldJpeg = 6;
        private const int CompressionJpeg = 7;
        private const int CompressionDeflate = 8;
        private const int CompressionJpeg2000 = 34712;
        private const int CompressionPackBits = 32773;
        private const int CompressionAdobeDeflate = 32946;

        private const int PhotometricWhiteIsZero = 0;
        private const int PhotometricBlackIsZero = 1;
        private const int PhotometricRgb = 2;
        private const int PhotometricPalette = 3;
        private const int PhotometricMask = 4;
        private const int PhotometricSeparated = 5;
        private const int PhotometricYCbCr = 6;
        private const int PhotometricCieLab = 8;
        private const int PhotometricIccLab = 9;

        private const int SampleFormatUInt = 1;
        private const int SampleFormatInt = 2;
        private const int SampleFormatFloat = 3;

        private const int ExtraUnspecified = 0;
        private const int ExtraAssociatedAlpha = 1;
        private const int ExtraUnassociatedAlpha = 2;

        private readonly TiffFile file;
        private readonly TiffDirectory dir;

        private int width, height, samplesPerPixel, compression, photometric, planar, predictor, sampleFormat;
        private int[] bitsPerSample = Array.Empty<int>();
        private bool lsbFirst;

        // Chunk layout.
        private bool tiled;
        private int chunkWidth, chunkHeight, chunksAcross, chunksDown;
        private long[] chunkOffsets = Array.Empty<long>();
        private long[] chunkByteCounts = Array.Empty<long>();

        // Subsampling of YCbCr data units, or 1x1 if there are none to expand.
        private int subH = 1, subV = 1;

        // Whether a JPEG or JPEG 2000 codec has converted the samples to RGB.
        private bool codecConvertedColor;

        // The decoded planes (one, or one per sample if planar), and the bits per pixel and
        // bytes per row of each.
        private byte[][] planes = Array.Empty<byte[]>();
        private int[] planeBits = Array.Empty<int>();
        private int[] planeRowBytes = Array.Empty<int>();

        // For each sample, the plane it's in and its bit offset within a pixel of that plane.
        private int[] samplePlane = Array.Empty<int>();
        private int[] sampleOffset = Array.Empty<int>();

        public TiffPage(TiffFile file, TiffDirectory dir)
        {
            this.file = file;
            this.dir = dir;
        }

        public ImageData Decode()
        {
            ReadLayout();
            DecodeChunks();
            var bgra = ToBgra();
            return Reorient(bgra, (int)dir.GetLong(TagOrientation, 1));
        }

        private void ReadLayout()
        {
            width = (int)Math.Min(dir.GetLong(TagWidth, 0), int.MaxValue);
            height = (int)Math.Min(dir.GetLong(TagHeight, 0), int.MaxValue);
            if (width <= 0 || height <= 0 || (long)width * height * 4 > int.MaxValue)
                throw new ImageDecodeException("TIFF image has invalid dimensions.");

            samplesPerPixel = (int)dir.GetLong(TagSamplesPerPixel, 1);
            if (samplesPerPixel < 1 || samplesPerPixel > 64)
                throw new ImageDecodeException("Invalid number of TIFF samples per pixel: " + samplesPerPixel);
            var bps = dir.GetLongs(TagBitsPerSample) ?? new long[] { 1 };
            bitsPerSample = new int[samplesPerPixel];
            for (int i = 0; i < samplesPerPixel; i++)
            {
                bitsPerSample[i] = (int)(i < bps.Length ? bps[i] : bps.Length > 0 ? bps[0] : 1);
                if (bitsPerSample[i] < 1 || bitsPerSample[i] > 64)
                    throw new ImageDecodeException("Invalid TIFF bits per sample: " + bitsPerSample[i]);
            }

            compression = (int)dir.GetLong(TagCompression, CompressionNone);
            bool ccitt = compression == CompressionCcittRle || compression == CompressionCcittG3 || compression == CompressionCcittG4;
            int defaultPhotometric = ccitt ? PhotometricWhiteIsZero
                : dir.Has(TagColorMap) ? PhotometricPalette
                : samplesPerPixel >= 3 ? PhotometricRgb : PhotometricBlackIsZero;
            photometric = (int)dir.GetLong(TagPhotometric, defaultPhotometric);
            planar = samplesPerPixel > 1 ? (int)dir.GetLong(TagPlanarConfig, 1) : 1;
            predictor = (int)dir.GetLong(TagPredictor, 1);
            lsbFirst = dir.GetLong(TagFillOrder, 1) == 2;
            sampleFormat = (int)dir.GetLong(TagSampleFormat, SampleFormatUInt);
            if (sampleFormat != SampleFormatUInt && sampleFormat != SampleFormatInt && sampleFormat != SampleFormatFloat)
            {
                if (sampleFormat != 4) // "undefined", which we treat as unsigned
                    throw new ImageDecodeException("Unsupported TIFF sample format: " + sampleFormat);
                sampleFormat = SampleFormatUInt;
            }
            foreach (int b in bitsPerSample)
            {
                if (sampleFormat == SampleFormatFloat ? b != 16 && b != 24 && b != 32 && b != 64 : b > 32)
                    throw new ImageDecodeException("Unsupported TIFF bit depth: " + b);
            }

            if (photometric == PhotometricYCbCr && compression != CompressionJpeg && compression != CompressionOldJpeg)
            {
                var sub = dir.GetLongs(TagYCbCrSubSampling);
                subH = sub != null && sub.Length > 0 ? (int)sub[0] : 2;
                subV = sub != null && sub.Length > 1 ? (int)sub[1] : 2;
                if (subH != 1 && subH != 2 && subH != 4 || subV != 1 && subV != 2 && subV != 4)
                    throw new ImageDecodeException("Invalid TIFF YCbCr subsampling.");
                if ((subH > 1 || subV > 1) && (samplesPerPixel != 3 || planar != 1 || bitsPerSample[0] != 8))
                    throw new ImageDecodeException("Unsupported layout of subsampled TIFF YCbCr data.");
            }

            tiled = dir.Has(TagTileWidth) && dir.Has(TagTileLength);
            if (tiled)
            {
                chunkWidth = (int)dir.GetLong(TagTileWidth, 0);
                chunkHeight = (int)dir.GetLong(TagTileLength, 0);
                if (chunkWidth <= 0 || chunkHeight <= 0 || (long)chunkWidth * chunkHeight > 1 << 26)
                    throw new ImageDecodeException("Invalid TIFF tile size.");
            }
            else
            {
                chunkWidth = width;
                long rows = dir.GetLong(TagRowsPerStrip, height);
                chunkHeight = (int)(rows <= 0 || rows > height ? height : rows);
            }
            chunksAcross = (width + chunkWidth - 1) / chunkWidth;
            chunksDown = (height + chunkHeight - 1) / chunkHeight;

            chunkOffsets = dir.GetLongs(tiled ? TagTileOffsets : TagStripOffsets) ?? dir.GetLongs(TagStripOffsets) ?? dir.GetLongs(TagTileOffsets)
                ?? Array.Empty<long>();
            chunkByteCounts = dir.GetLongs(tiled ? TagTileByteCounts : TagStripByteCounts) ?? dir.GetLongs(TagStripByteCounts) ?? dir.GetLongs(TagTileByteCounts)
                ?? InferByteCounts();

            int planeCount = planar == 2 ? samplesPerPixel : 1;
            planes = new byte[planeCount][];
            planeBits = new int[planeCount];
            planeRowBytes = new int[planeCount];
            samplePlane = new int[samplesPerPixel];
            sampleOffset = new int[samplesPerPixel];
            for (int s = 0; s < samplesPerPixel; s++)
            {
                int p = planar == 2 ? s : 0;
                samplePlane[s] = p;
                sampleOffset[s] = planeBits[p];
                planeBits[p] += bitsPerSample[s];
            }
            for (int p = 0; p < planeCount; p++)
            {
                long rowBytes = ((long)width * planeBits[p] + 7) / 8;
                if (rowBytes * height > int.MaxValue)
                    throw new ImageDecodeException("TIFF image is too large.");
                planeRowBytes[p] = (int)rowBytes;
                planes[p] = new byte[rowBytes * height];
            }
        }

        /// <summary>
        /// Works out the sizes of the chunks for files that don't say: each runs up to the
        /// start of the next, or to the end of the file.
        /// </summary>
        private long[] InferByteCounts()
        {
            var counts = new long[chunkOffsets.Length];
            for (int i = 0; i < counts.Length; i++)
            {
                long end = file.Data.Length;
                foreach (long o in chunkOffsets)
                {
                    if (o > chunkOffsets[i] && o < end)
                        end = o;
                }
                counts[i] = Math.Max(end - chunkOffsets[i], 0);
            }
            return counts;
        }

        private void DecodeChunks()
        {
            if (compression == CompressionOldJpeg && DecodeOldJpegImage())
                return;

            int chunksPerPlane = chunksAcross * chunksDown;
            for (int p = 0; p < planes.Length; p++)
            {
                for (int c = 0; c < chunksPerPlane; c++)
                {
                    int index = p * chunksPerPlane + c;
                    if (index >= chunkOffsets.Length || index >= chunkByteCounts.Length)
                        return;
                    long offset = chunkOffsets[index];
                    long count = chunkByteCounts[index];
                    if (offset <= 0 || offset >= file.Data.Length)
                        continue;
                    count = Math.Min(count, file.Data.Length - offset);
                    var input = file.Data.AsSpan((int)offset, (int)count);
                    int x0 = (c % chunksAcross) * chunkWidth;
                    int y0 = (c / chunksAcross) * chunkHeight;
                    DecodeChunk(input, p, x0, y0);
                }
            }
        }

        private void DecodeChunk(ReadOnlySpan<byte> input, int plane, int x0, int y0)
        {
            // A tile is always stored whole, even where it extends past the edge of the
            // image; the last strip has only as many rows as are left.
            int encWidth = chunkWidth;
            int encRows = tiled ? chunkHeight : Math.Min(chunkHeight, height - y0);
            int bits = planeBits[plane];
            int rowBytes = (int)(((long)encWidth * bits + 7) / 8);
            bool units = subH > 1 || subV > 1;
            int unitsAcross = (encWidth + subH - 1) / subH;
            int unitsDown = (encRows + subV - 1) / subV;
            long size = units ? (long)unitsAcross * unitsDown * (subH * subV + 2) : (long)rowBytes * encRows;
            if (size > int.MaxValue)
                throw new ImageDecodeException("TIFF chunk is too large.");

            byte[] raw;
            switch (compression)
            {
                case CompressionCcittRle:
                case CompressionCcittG3:
                case CompressionCcittG4:
                    raw = DecodeCcitt(input, encWidth, encRows, rowBytes);
                    break;
                case CompressionJpeg:
                case CompressionOldJpeg:
                {
                    var tables = compression == CompressionJpeg ? dir.GetBytes(TagJpegTables) : null;
                    raw = CodecToRaw(JpegDecoder.Decode(JpegDecoder.WithTables(tables, input), false), plane, encWidth, encRows, rowBytes);
                    break;
                }
                case CompressionJpeg2000:
                case 33003: // Aperio JPEG 2000 YCbCr
                case 33005: // Aperio JPEG 2000 RGB
                    raw = CodecToRaw(Jpeg2000Decoder.Decode(input.ToArray()), plane, encWidth, encRows, rowBytes);
                    break;
                default:
                {
                    raw = new byte[size];
                    var data = input;
                    if (lsbFirst)
                    {
                        var reversed = input.ToArray();
                        TiffCompression.ReverseBits(reversed);
                        data = reversed;
                    }
                    switch (compression)
                    {
                        case CompressionNone:
                            data[..Math.Min(data.Length, raw.Length)].CopyTo(raw);
                            break;
                        case CompressionLzw:
                            TiffCompression.DecompressLzw(data, raw);
                            break;
                        case CompressionPackBits:
                            TiffCompression.DecompressPackBits(data, raw);
                            break;
                        case CompressionDeflate:
                        case CompressionAdobeDeflate:
                            TiffCompression.DecompressDeflate(data.ToArray(), raw);
                            break;
                        default:
                            throw new ImageDecodeException("Unsupported TIFF compression: " + compression);
                    }

                    int samples = planar == 2 ? 1 : samplesPerPixel;
                    if (predictor == 2)
                        TiffCompression.UndoHorizontalPredictor(raw, units ? 1 : encRows, units ? raw.Length : rowBytes, samples, bitsPerSample[plane], file.BigEndian);
                    else if (predictor == 3)
                        TiffCompression.UndoFloatPredictor(raw, encRows, rowBytes, samples, bitsPerSample[plane], encWidth, file.BigEndian);
                    if (units)
                        raw = ExpandYCbCrUnits(raw, encWidth, encRows, unitsAcross);
                    break;
                }
            }

            PlaceChunk(raw, plane, x0, y0, encWidth, Math.Min(encRows, height - y0), rowBytes);
        }

        private byte[] DecodeCcitt(ReadOnlySpan<byte> input, int encWidth, int encRows, int rowBytes)
        {
            if (samplesPerPixel != 1 || bitsPerSample[0] != 1)
                throw new ImageDecodeException("CCITT-compressed TIFF images must be bilevel.");
            var data = input.ToArray();
            byte[] pixels = compression switch
            {
                CompressionCcittRle => CcittDecoder.DecodeModifiedHuffman(data, 0, data.Length, encWidth, encRows, lsbFirst),
                CompressionCcittG3 => CcittDecoder.DecodeG3(data, 0, data.Length, encWidth, encRows, lsbFirst, (dir.GetLong(TagT4Options, 0) & 1) != 0),
                _ => CcittDecoder.DecodeG4(data, 0, data.Length, encWidth, encRows, lsbFirst),
            };

            // The decoder gives 1 for black runs, which we keep as the sample value; with
            // the usual WhiteIsZero interpretation, that's black.
            var raw = new byte[rowBytes * encRows];
            for (int y = 0; y < encRows; y++)
            {
                for (int x = 0; x < encWidth; x++)
                {
                    if (pixels[y * encWidth + x] != 0)
                        raw[y * rowBytes + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                }
            }
            return raw;
        }

        /// <summary>
        /// Packs the samples decoded by a JPEG or JPEG 2000 codec into a chunk.
        /// </summary>
        private byte[] CodecToRaw(CodecImage image, int plane, int encWidth, int encRows, int rowBytes)
        {
            int samples = planar == 2 ? 1 : samplesPerPixel;
            if (image.Components != samples)
                throw new ImageDecodeException("TIFF compressed chunk has the wrong number of samples.");
            var s = image.Samples;
            if (photometric == PhotometricYCbCr && samples == 3 && !image.ColorConverted)
            {
                // JPEG takes care of chroma subsampling, but leaves the color conversion to us.
                JpegDecoder.YccToRgb(s, (1 << image.Precision) - 1, 3);
                image.ColorConverted = true;
            }
            codecConvertedColor |= image.ColorConverted;

            var raw = new byte[rowBytes * encRows];
            int rows = Math.Min(encRows, image.Height);
            int cols = Math.Min(encWidth, image.Width);
            for (int y = 0; y < rows; y++)
            {
                for (int x = 0; x < cols; x++)
                {
                    for (int c = 0; c < samples; c++)
                    {
                        int sample = planar == 2 ? plane : c;
                        PutBits(raw, y * rowBytes, (long)x * planeBits[plane] + sampleOffset[sample], bitsPerSample[sample], (ulong)s[(y * image.Width + x) * samples + c]);
                    }
                }
            }
            return raw;
        }

        /// <summary>
        /// Decodes an image compressed with the original (TIFF 6.0) JPEG scheme, in the one
        /// form that's reasonably common: a complete JPEG stream for the whole image, which
        /// the JPEGInterchangeFormat field points to. Returns false if there is none, in
        /// which case we try the strips as JPEG streams of their own.
        /// </summary>
        private bool DecodeOldJpegImage()
        {
            long offset = dir.GetLong(TagJpegInterchangeFormat, 0);
            if (offset <= 0 || offset >= file.Data.Length)
                return false;
            long length = dir.GetLong(TagJpegInterchangeFormatLength, file.Data.Length - offset);
            length = Math.Min(length <= 0 ? file.Data.Length - offset : length, file.Data.Length - offset);
            var image = JpegDecoder.Decode(file.Data.AsSpan((int)offset, (int)length).ToArray(), false);
            if (planar != 1)
                throw new ImageDecodeException("Unsupported layout of old-style JPEG TIFF data.");
            int rowBytes = planeRowBytes[0];
            var raw = CodecToRaw(image, 0, width, height, rowBytes);
            PlaceChunk(raw, 0, 0, 0, width, height, rowBytes);
            return true;
        }

        /// <summary>
        /// Expands YCbCr data units (subH x subV luma samples, then Cb and Cr) into pixels of
        /// three samples each.
        /// </summary>
        private byte[] ExpandYCbCrUnits(byte[] units, int encWidth, int encRows, int unitsAcross)
        {
            var result = new byte[encWidth * encRows * 3];
            int unitSize = subH * subV + 2;
            int u = 0;
            for (int uy = 0; uy < encRows; uy += subV)
            {
                for (int ux = 0; ux < unitsAcross * subH; ux += subH, u += unitSize)
                {
                    if (u + unitSize > units.Length)
                        return result;
                    byte cb = units[u + subH * subV], cr = units[u + subH * subV + 1];
                    for (int dy = 0; dy < subV; dy++)
                    {
                        for (int dx = 0; dx < subH; dx++)
                        {
                            int x = ux + dx, y = uy + dy;
                            if (x >= encWidth || y >= encRows)
                                continue;
                            int p = (y * encWidth + x) * 3;
                            result[p] = units[u + dy * subH + dx];
                            result[p + 1] = cb;
                            result[p + 2] = cr;
                        }
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Copies a decoded chunk into its plane.
        /// </summary>
        private void PlaceChunk(byte[] raw, int plane, int x0, int y0, int encWidth, int rows, int rowBytes)
        {
            var dest = planes[plane];
            int destRowBytes = planeRowBytes[plane];
            int bits = planeBits[plane];
            long startBit = (long)x0 * bits;
            int cols = Math.Min(encWidth, width - x0);
            for (int y = 0; y < rows; y++)
            {
                int srcRow = y * rowBytes;
                int destRow = (y0 + y) * destRowBytes;
                if (srcRow + rowBytes > raw.Length)
                    break;
                if (startBit % 8 == 0)
                {
                    int destStart = (int)(startBit / 8);
                    int count = Math.Min(rowBytes, (int)(((long)(x0 + cols) * bits + 7) / 8) - destStart);
                    Array.Copy(raw, srcRow, dest, destRow + destStart, count);
                }
                else
                {
                    for (long b = 0; b < (long)cols * bits; b++)
                        PutBits(dest, destRow, startBit + b, 1, (ulong)((raw[srcRow + (int)(b >> 3)] >> (7 - (int)(b & 7))) & 1));
                }
            }
        }

        private static void PutBits(byte[] buf, int rowStart, long bitPos, int count, ulong value)
        {
            if ((bitPos & 7) == 0 && (count & 7) == 0)
            {
                TiffCompression.WriteSample(buf, rowStart + (int)(bitPos >> 3), count / 8, false, value);
                return;
            }
            for (int i = count - 1; i >= 0; i--, bitPos++)
            {
                int p = rowStart + (int)(bitPos >> 3);
                int mask = 0x80 >> (int)(bitPos & 7);
                if (((value >> i) & 1) != 0)
                    buf[p] |= (byte)mask;
                else
                    buf[p] &= (byte)~mask;
            }
        }

        /// <summary>
        /// Reads the raw value of a sample of a pixel.
        /// </summary>
        private ulong GetSample(int x, int y, int sample)
        {
            int p = samplePlane[sample];
            var buf = planes[p];
            int rowStart = y * planeRowBytes[p];
            long bitPos = (long)x * planeBits[p] + sampleOffset[sample];
            int n = bitsPerSample[sample];
            if ((bitPos & 7) == 0 && (n & 7) == 0)
            {
                int pos = rowStart + (int)(bitPos >> 3);
                return n == 8 ? buf[pos] : TiffCompression.ReadSample(buf, pos, n / 8, file.BigEndian && !SamplesRepacked);
            }
            ulong v = 0;
            for (int i = 0; i < n; i++, bitPos++)
                v = (v << 1) | (uint)((buf[rowStart + (int)(bitPos >> 3)] >> (7 - (int)(bitPos & 7))) & 1);
            return v;
        }

        // Whether samples were written by us (in little-endian order) rather than copied
        // from the file.
        private bool SamplesRepacked => compression == CompressionJpeg || compression == CompressionOldJpeg
            || compression == CompressionJpeg2000 || compression == 33003 || compression == 33005;

        /// <summary>
        /// Converts a raw sample to a value from 0 to 1 (for floats, possibly beyond).
        /// </summary>
        private double ToUnit(ulong raw, int sample)
        {
            int n = bitsPerSample[sample];
            switch (sampleFormat)
            {
                case SampleFormatFloat:
                    return n switch
                    {
                        16 => (double)BitConverter.UInt16BitsToHalf((ushort)raw),
                        24 => Float24ToDouble((uint)raw),
                        32 => BitConverter.UInt32BitsToSingle((uint)raw),
                        _ => BitConverter.UInt64BitsToDouble(raw),
                    };
                case SampleFormatInt:
                {
                    // Shift the signed range up to start at zero.
                    ulong half = 1UL << (n - 1);
                    return (double)((raw + half) & ((half << 1) - 1)) / ((half << 1) - 1);
                }
                default:
                    return (double)raw / ((1UL << n) - 1);
            }
        }

        private static double Float24ToDouble(uint v)
        {
            // 1 sign bit, 7 exponent bits (bias 63), 16 mantissa bits.
            int exp = (int)(v >> 16) & 0x7F;
            uint mant = v & 0xFFFF;
            double sign = (v & 0x800000) != 0 ? -1 : 1;
            if (exp == 0)
                return sign * mant / 65536.0 * Math.Pow(2, -62);
            if (exp == 0x7F)
                return mant == 0 ? sign * double.PositiveInfinity : double.NaN;
            return sign * (1 + mant / 65536.0) * Math.Pow(2, exp - 63);
        }

        private ImageData ToBgra()
        {
            if (codecConvertedColor)
                photometric = PhotometricRgb;

            // Figure out which samples are color and which (if any) is alpha.
            var extra = dir.GetLongs(TagExtraSamples);
            int extraCount = extra?.Length ?? 0;
            int colorCount = Math.Max(samplesPerPixel - extraCount, 1);
            int expectedColors = photometric switch
            {
                PhotometricRgb or PhotometricYCbCr or PhotometricCieLab or PhotometricIccLab => 3,
                PhotometricSeparated => colorCount,
                _ => 1
            };
            if (extra == null)
                colorCount = Math.Min(expectedColors, samplesPerPixel);
            if (colorCount < expectedColors)
                throw new ImageDecodeException("TIFF image has too few samples for its color space.");

            // As libtiff does, treat an unspecified extra sample as associated alpha when
            // there are more than three samples, and so is the fourth sample of an RGB image
            // with no ExtraSamples field at all.
            int alpha = -1;
            bool associated = false;
            if (samplesPerPixel > colorCount)
            {
                int type = extra != null && extra.Length > 0 ? (int)extra[0] : ExtraUnspecified;
                if (type == ExtraAssociatedAlpha || type == ExtraUnassociatedAlpha || samplesPerPixel > 3)
                {
                    alpha = colorCount;
                    associated = type != ExtraUnassociatedAlpha;
                }
            }

            var mapping = BuildMapping(colorCount);
            bool premultipliable = photometric == PhotometricWhiteIsZero || photometric == PhotometricBlackIsZero
                || photometric == PhotometricRgb || photometric == PhotometricSeparated;
            associated = associated && premultipliable && !HasUnassociatedAlpha(mapping, colorCount, alpha);

            var bgra = new byte[width * height * 4];
            var c = new double[4];
            for (int y = 0, d = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++, d += 4)
                {
                    double a = alpha >= 0 ? Math.Clamp(ToUnit(GetSample(x, y, alpha), alpha), 0, 1) : 1;
                    if (photometric == PhotometricPalette)
                    {
                        int rgb = mapping.Palette(GetSample(x, y, 0));
                        bgra[d] = (byte)rgb;
                        bgra[d + 1] = (byte)(rgb >> 8);
                        bgra[d + 2] = (byte)(rgb >> 16);
                        bgra[d + 3] = (byte)Math.Round(a * 255);
                        continue;
                    }

                    for (int s = 0; s < Math.Min(colorCount, 4); s++)
                    {
                        c[s] = mapping.Unit(GetSample(x, y, s), s);
                        if (associated)
                            c[s] = a > 0 ? Math.Min(c[s] / a, 1) : 0;
                    }
                    int packed = mapping.ToRgb(c, colorCount, x, y);
                    bgra[d] = (byte)packed;
                    bgra[d + 1] = (byte)(packed >> 8);
                    bgra[d + 2] = (byte)(packed >> 16);
                    bgra[d + 3] = (byte)Math.Round(a * 255);
                }
            }
            return new ImageData(width, height, bgra);
        }

        /// <summary>
        /// Checks whether alpha that's labeled as associated (premultiplied) really is. In
        /// premultiplied data no color sample can exceed the alpha, but some writers label
        /// unassociated alpha as associated, so if more than a few pixels break that rule
        /// (allowing for the noise of lossy compression), we take the alpha to be unassociated.
        /// </summary>
        private bool HasUnassociatedAlpha(ColorMapping mapping, int colorCount, int alpha)
        {
            long violations = 0, limit = (long)width * height / 100;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    double a = ToUnit(GetSample(x, y, alpha), alpha);
                    for (int s = 0; s < colorCount; s++)
                    {
                        if (mapping.Unit(GetSample(x, y, s), s) > a + 0.02)
                        {
                            if (++violations > limit)
                                return true;
                            break;
                        }
                    }
                }
            }
            return false;
        }

        private ColorMapping BuildMapping(int colorCount)
        {
            var mapping = new ColorMapping(this);
            if (sampleFormat == SampleFormatFloat)
            {
                // Floating point data is usually from 0 to 1, but if it isn't, we scale its
                // actual range to fit.
                double min = double.MaxValue, max = double.MinValue;
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        for (int s = 0; s < colorCount; s++)
                        {
                            double v = ToUnit(GetSample(x, y, s), s);
                            if (double.IsFinite(v))
                            {
                                min = Math.Min(min, v);
                                max = Math.Max(max, v);
                            }
                        }
                    }
                }
                if (min < 0 || max > 1)
                {
                    mapping.FloatMin = min;
                    mapping.FloatScale = max > min ? 1 / (max - min) : 1;
                }
            }
            return mapping;
        }

        /// <summary>
        /// Converts samples, as values from 0 to 1, to RGB according to the photometric
        /// interpretation.
        /// </summary>
        private sealed class ColorMapping
        {
            private readonly TiffPage page;
            private readonly int[]? palette;
            private readonly double[] refBlackWhite;
            private readonly double lumaRed, lumaGreen, lumaBlue;

            public double FloatMin;
            public double FloatScale = 1;

            public ColorMapping(TiffPage page)
            {
                this.page = page;
                if (page.photometric == PhotometricPalette)
                    palette = BuildPalette(page);

                int max = page.bitsPerSample[0] >= 32 ? 255 : (1 << page.bitsPerSample[0]) - 1;
                refBlackWhite = page.dir.GetDoubles(TagReferenceBlackWhite) is { Length: >= 6 } rbw ? rbw
                    : new double[] { 0, max, (max + 1) / 2, max, (max + 1) / 2, max };
                var coefficients = page.dir.GetDoubles(TagYCbCrCoefficients);
                lumaRed = coefficients is { Length: >= 3 } ? coefficients[0] : 0.299;
                lumaGreen = coefficients is { Length: >= 3 } ? coefficients[1] : 0.587;
                lumaBlue = coefficients is { Length: >= 3 } ? coefficients[2] : 0.114;
            }

            private static int[] BuildPalette(TiffPage page)
            {
                int n = 1 << Math.Min(page.bitsPerSample[0], 16);
                var map = page.dir.GetLongs(TagColorMap);
                if (map == null || map.Length < 3 * n)
                    throw new ImageDecodeException("TIFF palette image has an invalid color map.");

                // The values are meant to be 16 bits, but some writers store 8-bit values; as
                // libtiff does, assume 8 bits if they all fit.
                int shift = Array.TrueForAll(map, v => v < 256) ? 0 : 8;
                var palette = new int[n];
                for (int i = 0; i < n; i++)
                {
                    palette[i] = (int)(((map[i] >> shift) & 0xFF) << 16 | ((map[n + i] >> shift) & 0xFF) << 8 | ((map[2 * n + i] >> shift) & 0xFF));
                }
                return palette;
            }

            public int Palette(ulong index)
            {
                return index < (ulong)palette!.Length ? palette[index] : 0;
            }

            public double Unit(ulong raw, int sample)
            {
                double v = page.ToUnit(raw, sample);
                return page.sampleFormat == SampleFormatFloat ? (v - FloatMin) * FloatScale : v;
            }

            public int ToRgb(double[] c, int colorCount, int x, int y)
            {
                switch (page.photometric)
                {
                    case PhotometricWhiteIsZero:
                    case PhotometricMask:
                        return Gray(1 - c[0]);
                    case PhotometricBlackIsZero:
                        return Gray(c[0]);
                    case PhotometricRgb:
                        return Pack(c[0], c[1], c[2]);
                    case PhotometricSeparated:
                    {
                        // Plain "white minus ink", without regard to any ICC profile.
                        if (colorCount < 3)
                            return Gray(1 - c[0]);
                        double k = colorCount >= 4 ? c[3] : 0;
                        return Pack((1 - c[0]) * (1 - k), (1 - c[1]) * (1 - k), (1 - c[2]) * (1 - k));
                    }
                    case PhotometricYCbCr:
                        return YCbCrToRgb(x, y);
                    case PhotometricCieLab:
                    case PhotometricIccLab:
                        return LabToRgb(x, y);
                    default:
                        throw new ImageDecodeException("Unsupported TIFF photometric interpretation: " + page.photometric);
                }
            }

            private int YCbCrToRgb(int x, int y)
            {
                double codingRange = (1L << page.bitsPerSample[0]) - 1;
                double luma = (page.GetSample(x, y, 0) - refBlackWhite[0]) * codingRange / (refBlackWhite[1] - refBlackWhite[0]);
                double cb = (page.GetSample(x, y, 1) - refBlackWhite[2]) * 127 / (refBlackWhite[3] - refBlackWhite[2]);
                double cr = (page.GetSample(x, y, 2) - refBlackWhite[4]) * 127 / (refBlackWhite[5] - refBlackWhite[4]);
                double r = cr * (2 - 2 * lumaRed) + luma;
                double b = cb * (2 - 2 * lumaBlue) + luma;
                double g = (luma - lumaBlue * b - lumaRed * r) / lumaGreen;
                return Pack(r / codingRange, g / codingRange, b / codingRange);
            }

            private int LabToRgb(int x, int y)
            {
                int bits = page.bitsPerSample[0];
                double max = (1L << bits) - 1;
                double l = page.GetSample(x, y, 0) * 100 / max;
                double a = page.GetSample(x, y, 1), b = page.GetSample(x, y, 2);
                double half = 1L << (bits - 1);
                if (page.photometric == PhotometricCieLab)
                {
                    // a* and b* are signed.
                    a = a >= half ? a - 2 * half : a;
                    b = b >= half ? b - 2 * half : b;
                }
                else
                {
                    a -= half;
                    b -= half;
                }
                // Scale 16-bit values down to the 8-bit range of a* and b*.
                a = a * 128 / half;
                b = b * 128 / half;
                return ColorConversion.LabToRgb(l, a, b);
            }

            private static int Gray(double v)
            {
                int g = ToByte(v);
                return g << 16 | g << 8 | g;
            }

            private static int Pack(double r, double g, double b)
            {
                return ToByte(r) << 16 | ToByte(g) << 8 | ToByte(b);
            }

            private static int ToByte(double v)
            {
                return !(v > 0) ? 0 : v >= 1 ? 255 : (int)(v * 255 + 0.5);
            }
        }

        /// <summary>
        /// Applies the Orientation field, which says where the first row and column of the
        /// stored image belong.
        /// </summary>
        private static ImageData Reorient(ImageData image, int orientation)
        {
            if (orientation <= 1 || orientation > 8)
                return image;
            int w = image.Width, h = image.Height;
            bool transpose = orientation >= 5;
            int newW = transpose ? h : w, newH = transpose ? w : h;
            var src = image.Data!;
            var dst = new byte[src.Length];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    // Where pixel (x, y) of the stored image goes in the displayed one.
                    (int dx, int dy) = orientation switch
                    {
                        2 => (w - 1 - x, y),
                        3 => (w - 1 - x, h - 1 - y),
                        4 => (x, h - 1 - y),
                        5 => (y, x),
                        6 => (h - 1 - y, x),
                        7 => (h - 1 - y, w - 1 - x),
                        _ => (y, w - 1 - x),
                    };
                    Buffer.BlockCopy(src, (y * w + x) * 4, dst, (dy * newW + dx) * 4, 4);
                }
            }
            return new ImageData(newW, newH, dst);
        }
    }
}
