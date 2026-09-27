using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

/*

Decoder for DICOM images.

A DICOM file usually starts with a 128-byte preamble, the signature "DICM", and
a "meta" group of data elements (always explicit VR little endian) that gives
the transfer syntax of the rest of the file: implicit or explicit VR, little or
big endian, possibly deflated, and whether the pixel data is stored natively or
compressed with JPEG, JPEG-LS, JPEG 2000 or RLE. Older (ACR-NEMA style) files have
no preamble or meta group at all, and are simply a data set in implicit VR.

Native pixel data is stored as samples of BitsAllocated bits, of which only
BitsStored bits (ending at HighBit) are significant, and which may be signed.
Grayscale images are then put through a "modality" transform (usually a linear
rescale into real-world units such as Hounsfield units), and a "VOI" transform
(usually a window center and width) that selects the range of values to
display. Color images can be RGB, YCbCr, or indexed via a palette.

Only the first frame of a multi-frame image is decoded.

Copyright 2013+ Dmitry Brant
https://dmitrybrant.com

License: MIT
*/

namespace DmitryBrant.ImageFormats
{
    /// <summary>
    /// Handles reading DICOM images.
    /// </summary>
    public static class DicomReader
    {
        private const string TsImplicitLittle = "1.2.840.10008.1.2";
        private const string TsExplicitBig = "1.2.840.10008.1.2.2";
        private const string TsDeflated = "1.2.840.10008.1.2.1.99";
        private const string TsRle = "1.2.840.10008.1.2.5";

        // Frequently used tags
        private const uint TagTransferSyntax = 0x00020010;
        private const uint TagSamplesPerPixel = 0x00280002;
        private const uint TagPhotometric = 0x00280004;
        private const uint TagPlanarConfig = 0x00280006;
        private const uint TagNumberOfFrames = 0x00280008;
        private const uint TagRows = 0x00280010;
        private const uint TagColumns = 0x00280011;
        private const uint TagBitsAllocated = 0x00280100;
        private const uint TagBitsStored = 0x00280101;
        private const uint TagHighBit = 0x00280102;
        private const uint TagPixelRepresentation = 0x00280103;
        private const uint TagPixelPaddingValue = 0x00280120;
        private const uint TagWindowCenter = 0x00281050;
        private const uint TagWindowWidth = 0x00281051;
        private const uint TagRescaleIntercept = 0x00281052;
        private const uint TagRescaleSlope = 0x00281053;
        private const uint TagVoiLutFunction = 0x00281056;
        private const uint TagModalityLutSequence = 0x00283000;
        private const uint TagLutDescriptor = 0x00283002;
        private const uint TagLutData = 0x00283006;
        private const uint TagVoiLutSequence = 0x00283010;
        private const uint TagSharedFunctionalGroups = 0x52009229;
        private const uint TagPerFrameFunctionalGroups = 0x52009230;
        private const uint TagFrameVoiLutSequence = 0x00289132;
        private const uint TagPixelValueTransformationSequence = 0x00289145;

        /// <summary>
        /// Reads a DICOM image from a file.
        /// </summary>
        /// <param name="fileName">Name of the file to read.</param>
        /// <returns>ImageData that contains the image that was read.</returns>
        public static ImageData? Load(string fileName)
        {
            using var f = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Load(f);
        }

        /// <summary>
        /// Reads a DICOM image from a stream.
        /// </summary>
        /// <param name="stream">Stream from which to read the image.</param>
        /// <returns>ImageData that contains the image that was read.</returns>
        public static ImageData? Load(Stream stream)
        {
            byte[] buffer;
            using (var ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                buffer = ms.ToArray();
            }
            try
            {
                var ds = ParseFile(buffer, out string transferSyntax);
                return new FrameRenderer(ds, transferSyntax).Render(0);
            }
            catch (Exception e) when (e is IndexOutOfRangeException || e is ArgumentException || e is OverflowException || e is InvalidDataException)
            {
                // Malformed data that slipped past our checks.
                throw new ImageDecodeException("Invalid DICOM file: " + e.Message);
            }
        }

        /// <summary>
        /// Checks whether the given header looks like the start of a DICOM file that has
        /// no preamble, i.e. one that begins directly with a data set.
        /// </summary>
        internal static bool HasHeaderlessSignature(byte[] header)
        {
            if (header.Length < 16)
                return false;
            foreach (bool le in new[] { true, false })
            {
                int group = le ? header[0] | (header[1] << 8) : (header[0] << 8) | header[1];
                int element = le ? header[2] | (header[3] << 8) : (header[2] << 8) | header[3];
                if ((group != 0x0002 && group != 0x0008) || element > 0x00FF)
                    continue;
                if (DicomDataSet.IsVr(header[4], header[5]))
                    return true;
                uint len = le ? BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) : BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
                if (len > 0 && len < 256 && (element != 0 || len == 4))
                    return true;
            }
            return false;
        }

        private static DicomDataSet ParseFile(byte[] buffer, out string transferSyntax)
        {
            int pos;
            if (buffer.Length >= 132 && buffer[128] == 'D' && buffer[129] == 'I' && buffer[130] == 'C' && buffer[131] == 'M')
                pos = 132;
            else if (buffer.Length >= 4 && buffer[0] == 'D' && buffer[1] == 'I' && buffer[2] == 'C' && buffer[3] == 'M')
                pos = 4;
            else if (HasHeaderlessSignature(buffer))
                pos = 0;
            else
                throw new ImageDecodeException("Not a valid DICOM file.");

            transferSyntax = ReadMetaGroup(buffer, ref pos);

            if (transferSyntax == TsDeflated)
            {
                // Everything after the meta group is compressed with raw deflate.
                using var input = new MemoryStream(buffer, pos, buffer.Length - pos);
                using var deflate = new DeflateStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                try
                {
                    deflate.CopyTo(output);
                }
                catch (InvalidDataException e)
                {
                    Util.log("Error while inflating DICOM data set: " + e.Message);
                }
                buffer = output.ToArray();
                pos = 0;
            }

            if (pos + 8 > buffer.Length)
                throw new ImageDecodeException("DICOM file does not appear to have any image data.");

            // Work out the encoding of the data set. The transfer syntax tells us, but it's
            // wrong often enough that we check it against the first element: its group
            // number should be small, and an explicit VR element has a valid VR at byte 4.
            bool littleEndian = transferSyntax != TsExplicitBig;
            int groupLe = buffer[pos] | (buffer[pos + 1] << 8);
            int groupBe = (buffer[pos] << 8) | buffer[pos + 1];
            int group = littleEndian ? groupLe : groupBe, otherGroup = littleEndian ? groupBe : groupLe;
            if (group > 0xFF && otherGroup <= 0xFF)
                littleEndian = !littleEndian;
            bool explicitVr = DicomDataSet.IsVr(buffer[pos + 4], buffer[pos + 5]);

            var ds = DicomDataSet.Parse(buffer, pos, explicitVr, littleEndian);

            if (!ds.Contains(DicomDataSet.TagPixelData) && !ds.Contains(DicomDataSet.TagFloatPixelData) && !ds.Contains(DicomDataSet.TagDoublePixelData))
            {
                // Something went wrong along the way (or the file is truncated). As a last
                // resort, look for the pixel data element directly, and parse from there.
                var salvaged = FindPixelData(buffer, pos, explicitVr, littleEndian);
                if (salvaged != null)
                {
                    foreach (var kv in salvaged.Elements)
                        ds.Elements.TryAdd(kv.Key, kv.Value);
                }
            }
            return ds;
        }

        /// <summary>
        /// Reads the elements of group 0002, if present, and returns the transfer syntax.
        /// </summary>
        private static string ReadMetaGroup(byte[] b, ref int pos)
        {
            string transferSyntax = "";
            while (pos + 8 <= b.Length)
            {
                int group = b[pos] | (b[pos + 1] << 8);
                if (group != 0x0002)
                    break;
                int element = b[pos + 2] | (b[pos + 3] << 8);
                int len, valuePos;
                if (DicomDataSet.IsVr(b[pos + 4], b[pos + 5]))
                {
                    string vr = Encoding.ASCII.GetString(b, pos + 4, 2);
                    if (vr is "OB" or "OW" or "OF" or "SQ" or "UT" or "UN" or "UC" or "UR" or "OD" or "OL" or "OV" or "SV" or "UV")
                    {
                        if (pos + 12 > b.Length)
                            break;
                        len = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(pos + 8));
                        valuePos = pos + 12;
                    }
                    else
                    {
                        len = b[pos + 6] | (b[pos + 7] << 8);
                        valuePos = pos + 8;
                    }
                }
                else
                {
                    // Some writers use implicit VR even here.
                    len = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(pos + 4));
                    valuePos = pos + 8;
                }
                if (len < 0 || valuePos + len > b.Length)
                    break;
                if (element == 0x0010)
                    transferSyntax = Encoding.ASCII.GetString(b, valuePos, len).Trim(' ', '\0');
                pos = valuePos + len;
            }
            return transferSyntax;
        }

        private static DicomDataSet? FindPixelData(byte[] b, int start, bool explicitVr, bool littleEndian)
        {
            byte[] pattern = littleEndian ? new byte[] { 0xE0, 0x7F, 0x10, 0x00 } : new byte[] { 0x7F, 0xE0, 0x00, 0x10 };
            int found = -1;
            for (int i = b.Length - 12; i >= start; i--)
            {
                if (b[i] == pattern[0] && b[i + 1] == pattern[1] && b[i + 2] == pattern[2] && b[i + 3] == pattern[3])
                {
                    // Make sure it's followed by something that looks like a valid VR or length.
                    if ((b[i + 4] == 'O' && (b[i + 5] == 'B' || b[i + 5] == 'W')) || (!explicitVr && i + 8 < b.Length))
                    {
                        found = i;
                        break;
                    }
                }
            }
            if (found < 0)
                return null;
            var ds = DicomDataSet.Parse(b, found, DicomDataSet.IsVr(b[found + 4], b[found + 5]), littleEndian);
            return ds.Contains(DicomDataSet.TagPixelData) ? ds : null;
        }

        /// <summary>
        /// Decodes a frame of a parsed DICOM data set, and turns it into displayable pixels.
        /// </summary>
        private sealed class FrameRenderer
        {
            private readonly DicomDataSet ds;
            private readonly string transferSyntax;
            private readonly int rows, columns, samplesPerPixel, planarConfig, numFrames;
            private readonly int bitsAllocated, bitsStored, highBit;
            private readonly bool signed;
            private readonly string photometric;

            // Stored values that don't fit in an int (unsigned 32-bit) are halved, and
            // this factor restores their magnitude.
            private double valueScale = 1;

            public FrameRenderer(DicomDataSet ds, string transferSyntax)
            {
                this.ds = ds;
                this.transferSyntax = transferSyntax;

                rows = ds.GetInt(TagRows, 0);
                columns = ds.GetInt(TagColumns, 0);
                samplesPerPixel = Math.Max(1, ds.GetInt(TagSamplesPerPixel, 1));
                planarConfig = ds.GetInt(TagPlanarConfig, 0);
                numFrames = Math.Max(1, ds.GetInt(TagNumberOfFrames, 1));
                photometric = (ds.GetString(TagPhotometric) ?? "").ToUpperInvariant().Replace(" ", "");
                if (photometric.Length == 0)
                    photometric = samplesPerPixel >= 3 ? "RGB" : "MONOCHROME2";
                signed = ds.GetInt(TagPixelRepresentation, 0) == 1;

                bitsAllocated = ds.GetInt(TagBitsAllocated, 0);
                if (bitsAllocated <= 0 || bitsAllocated > 32)
                {
                    // Not stated (or nonsense); infer it from the size of the pixel data.
                    var pix = ds.Get(DicomDataSet.TagPixelData);
                    long pixels = (long)rows * columns * samplesPerPixel * numFrames;
                    bitsAllocated = pix != null && pix.Fragments == null && pixels > 0 && pix.Length >= pixels * 2 ? 16 : 8;
                }
                bitsStored = ds.GetInt(TagBitsStored, bitsAllocated);
                if (bitsStored <= 0 || bitsStored > bitsAllocated)
                    bitsStored = bitsAllocated;
                highBit = ds.GetInt(TagHighBit, bitsStored - 1);
                if (highBit < bitsStored - 1 || highBit >= bitsAllocated)
                    highBit = bitsStored - 1;
            }

            public ImageData? Render(int frame)
            {
                if (ds.Contains(DicomDataSet.TagFloatPixelData) || ds.Contains(DicomDataSet.TagDoublePixelData))
                {
                    if (!ds.Contains(DicomDataSet.TagPixelData))
                        return RenderFloat(frame);
                }

                var pixelData = ds.Get(DicomDataSet.TagPixelData);
                if (pixelData == null || (pixelData.Fragments == null && pixelData.Length == 0))
                    throw new ImageDecodeException("DICOM file does not appear to have any image data.");
                if (rows <= 0 || columns <= 0 || (long)rows * columns * samplesPerPixel > CodecImage.MaxSamples)
                    throw new ImageDecodeException("DICOM file has invalid image dimensions.");

                CodecImage image;
                bool rawSamples;   // true if samples are raw stored values that still need masking
                if (pixelData.Fragments != null)
                {
                    byte[] frameData = GetEncapsulatedFrame(pixelData, frame);
                    if (frameData.Length == 0)
                        throw new ImageDecodeException("DICOM file does not appear to have any image data.");
                    try
                    {
                        image = DecodeCompressed(frameData, out rawSamples);
                    }
                    catch (ImageDecodeException) when (IsPlainJpeg(frameData))
                    {
                        // We couldn't decode it, but it's an ordinary 8-bit JPEG, so the
                        // caller's imaging library probably can.
                        return ImageData.FromEncoded(columns, rows, frameData, "jpeg");
                    }
                }
                else if (IsCompressedStream(pixelData))
                {
                    // Compressed data that isn't encapsulated, as some broken writers do.
                    var bytes = new byte[pixelData.Length];
                    Array.Copy(ds.Buffer, pixelData.Offset, bytes, 0, bytes.Length);
                    image = DecodeCompressed(bytes, out rawSamples);
                }
                else
                {
                    image = ReadNativeFrame(pixelData, frame);
                    rawSamples = true;
                }

                int[] samples = image.Samples;
                if (rawSamples)
                    NormalizeRawSamples(samples, image.Components);
                else if (signed && !image.Signed && image.Components == 1)
                    SignExtend(samples, Math.Min(bitsStored, image.Precision));

                int width = image.Width, height = image.Height;
                var bgra = new byte[width * height * 4];
                if (image.Components == 1 && photometric == "PALETTECOLOR")
                    RenderPalette(samples, bgra);
                else if (image.Components == 1 || (image.Components == 2))
                    RenderGrayscale(samples, image.Components, bgra, frame);
                else
                    RenderColor(samples, image.Components, image.ColorConverted, rawSamples ? bitsStored : Math.Max(image.Precision, 1), bgra);

                return Util.LoadRgb(width, height, bgra);
            }

            #region Getting samples

            private bool IsCompressedStream(DicomElement pixelData)
            {
                if (pixelData.Length < 4)
                    return false;
                byte[] b = ds.Buffer;
                int p = pixelData.Offset;
                bool jpeg = b[p] == 0xFF && b[p + 1] == 0xD8 && b[p + 2] == 0xFF;
                bool j2k = b[p] == 0xFF && b[p + 1] == 0x4F && b[p + 2] == 0xFF && b[p + 3] == 0x51;
                // Only believe it if the data is too small to be uncompressed.
                long expected = (long)rows * columns * samplesPerPixel * bitsAllocated / 8;
                return (jpeg || j2k) && pixelData.Length < expected;
            }

            private static bool IsPlainJpeg(byte[] data)
            {
                if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
                    return false;
                int p = 2;
                while (p + 9 < data.Length)
                {
                    if (data[p] != 0xFF)
                        return false;
                    int marker = data[p + 1];
                    if (marker == 0xC0 || marker == 0xC1 || marker == 0xC2)
                        return data[p + 4] == 8;
                    if (marker == 0xDA)
                        return false;
                    p += 2 + ((data[p + 2] << 8) | data[p + 3]);
                }
                return false;
            }

            /// <summary>
            /// Gathers the bytes of the given frame from the fragments of encapsulated pixel data.
            /// </summary>
            private byte[] GetEncapsulatedFrame(DicomElement pixelData, int frame)
            {
                var fragments = pixelData.Fragments!;
                byte[] b = ds.Buffer;
                if (fragments.Count > 0 && fragments[0].Length >= 2 && b[fragments[0].Offset] == 0xFF && (b[fragments[0].Offset + 1] == 0xD8 || b[fragments[0].Offset + 1] == 0x4F))
                {
                    // Some writers leave out the offset table item altogether, and start
                    // right away with the image data.
                    fragments = new List<(int, int)>(fragments);
                    fragments.Insert(0, (fragments[0].Offset, 0));
                }
                if (fragments.Count < 2)
                    return Array.Empty<byte>();

                // The first fragment is the basic offset table, which may be empty.
                var offsetTable = new List<long>();
                var (tableOffset, tableLength) = fragments[0];
                for (int i = 0; i + 4 <= tableLength; i += 4)
                    offsetTable.Add(ds.LittleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(tableOffset + i)) : BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(tableOffset + i)));

                int first = 1, last = fragments.Count - 1;
                if (numFrames > 1)
                {
                    if (offsetTable.Count == numFrames)
                    {
                        // Offsets are relative to the first byte of the first fragment's item tag.
                        long baseOffset = fragments[1].Offset - 8;
                        long start = offsetTable[frame], end = frame + 1 < numFrames ? offsetTable[frame + 1] : long.MaxValue;
                        first = -1;
                        for (int i = 1; i < fragments.Count; i++)
                        {
                            long rel = fragments[i].Offset - 8 - baseOffset;
                            if (rel >= start && rel < end)
                            {
                                if (first < 0)
                                    first = i;
                                last = i;
                            }
                        }
                        if (first < 0)
                            return Array.Empty<byte>();
                    }
                    else if (fragments.Count - 1 == numFrames)
                    {
                        first = last = frame + 1;
                    }
                    else
                    {
                        // No help from an offset table; a new frame starts with each fragment
                        // that begins with a JPEG or JPEG 2000 start marker.
                        int current = -1;
                        first = -1;
                        for (int i = 1; i < fragments.Count; i++)
                        {
                            var (o, l) = fragments[i];
                            bool isStart = l >= 2 && b[o] == 0xFF && (b[o + 1] == 0xD8 || b[o + 1] == 0x4F);
                            if (isStart || current < 0)
                                current++;
                            if (current == frame)
                            {
                                if (first < 0)
                                    first = i;
                                last = i;
                            }
                        }
                        if (first < 0)
                            return Array.Empty<byte>();
                    }
                }

                int total = 0;
                for (int i = first; i <= last; i++)
                    total += fragments[i].Length;
                var data = new byte[total];
                int p = 0;
                for (int i = first; i <= last; i++)
                {
                    Array.Copy(b, fragments[i].Offset, data, p, fragments[i].Length);
                    p += fragments[i].Length;
                }
                return data;
            }

            private CodecImage DecodeCompressed(byte[] data, out bool rawSamples)
            {
                rawSamples = false;
                if (data.Length > 2 && data[0] == 0xFF && data[1] == 0xD8)
                {
                    // JPEG or JPEG-LS: which one depends on the frame marker.
                    int p = 2;
                    while (p + 3 < data.Length && data[p] == 0xFF)
                    {
                        int marker = data[p + 1];
                        if (marker == 0xF7)
                            return JpegLsDecoder.Decode(data);
                        if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                            break;
                        if (marker == 0xFF)
                        {
                            p++;
                            continue;
                        }
                        p += 2 + ((data[p + 2] << 8) | data[p + 3]);
                    }
                    return JpegDecoder.Decode(data);
                }
                if (data.Length > 4 && data[0] == 0xFF && data[1] == 0x4F && data[2] == 0xFF && data[3] == 0x51)
                    return Jpeg2000Decoder.Decode(data);
                if (data.Length > 12 && data[0] == 0 && data[1] == 0 && data[2] == 0 && data[3] == 0x0C && data[4] == 0x6A && data[5] == 0x50)
                    return Jpeg2000Decoder.Decode(data);
                if (transferSyntax == TsRle || RleDecoder.HasSignature(data))
                {
                    rawSamples = true;
                    return RleDecoder.Decode(data, columns, rows, samplesPerPixel, bitsAllocated);
                }
                if (transferSyntax.StartsWith("1.2.840.10008.1.2.4.10"))
                    throw new ImageDecodeException("MPEG-encoded DICOM images are not supported.");
                throw new ImageDecodeException("Unsupported DICOM compression: " + transferSyntax);
            }

            /// <summary>
            /// Reads a frame of uncompressed pixel data, as raw stored values, one per sample,
            /// interleaved by pixel.
            /// </summary>
            private CodecImage ReadNativeFrame(DicomElement pixelData, int frame)
            {
                byte[] b = ds.Buffer;
                int numPixels = rows * columns;
                int spp = samplesPerPixel;
                var samples = new int[numPixels * spp];
                int start = pixelData.Offset, end = pixelData.Offset + pixelData.Length;

                if (photometric == "YBR_FULL_422" && bitsAllocated == 8 && spp == 3)
                {
                    // Pairs of pixels are stored as Y1 Y2 Cb Cr.
                    long frameStart = start + (long)frame * numPixels * 2;
                    for (int y = 0; y < rows; y++)
                    {
                        for (int x = 0; x < columns; x += 2)
                        {
                            long p = frameStart + (y * (long)columns + x) * 2;
                            if (p + 3 >= end)
                                return new CodecImage(columns, rows, spp, bitsAllocated, false, samples);
                            int i = (y * columns + x) * 3;
                            samples[i] = b[p];
                            samples[i + 1] = b[p + 2];
                            samples[i + 2] = b[p + 3];
                            if (x + 1 < columns)
                            {
                                samples[i + 3] = b[p + 1];
                                samples[i + 4] = b[p + 2];
                                samples[i + 5] = b[p + 3];
                            }
                        }
                    }
                    return new CodecImage(columns, rows, spp, bitsAllocated, false, samples);
                }

                long frameBits = (long)numPixels * spp * bitsAllocated;
                if (bitsAllocated == 1)
                {
                    // Packed bits, least significant bit first.
                    long bitPos = frameBits * frame;
                    for (int i = 0; i < samples.Length; i++, bitPos++)
                    {
                        long p = start + (bitPos >> 3);
                        if (p >= end)
                            break;
                        samples[i] = (b[p] >> (int)(bitPos & 7)) & 1;
                    }
                    return new CodecImage(columns, rows, spp, 1, false, samples);
                }

                long frameStartBytes = start + frameBits / 8 * frame;
                if (frameStartBytes >= end && frame > 0)
                    throw new ImageDecodeException("DICOM frame is out of range.");

                // In big endian files, 8-bit data stored as OW is byte-swapped in pairs.
                bool swap8 = bitsAllocated == 8 && !ds.LittleEndian && pixelData.Vr == "OW";
                bool le = ds.LittleEndian;
                int bytesPerSample = bitsAllocated == 12 ? 0 : (bitsAllocated + 7) / 8;

                for (int i = 0; i < samples.Length; i++)
                {
                    // With planar configuration 1, all of the first sample come first, and so on.
                    int src = planarConfig == 1 && spp > 1 ? (i % spp) * numPixels + i / spp : i;
                    int val;
                    if (bitsAllocated == 12)
                    {
                        // Old ACR-NEMA packing: two 12-bit samples in three bytes.
                        long p = frameStartBytes + (src / 2) * 3;
                        if (p + 2 >= end)
                            break;
                        val = (src & 1) == 0 ? b[p] | ((b[p + 1] & 0xF) << 8) : (b[p + 1] >> 4) | (b[p + 2] << 4);
                    }
                    else
                    {
                        long p = frameStartBytes + (long)src * bytesPerSample;
                        if (p + bytesPerSample > end)
                            break;
                        if (bytesPerSample == 1)
                            val = b[swap8 ? p ^ 1 : p];
                        else if (bytesPerSample == 2)
                            val = le ? b[p] | (b[p + 1] << 8) : (b[p] << 8) | b[p + 1];
                        else
                        {
                            val = 0;
                            for (int k = 0; k < bytesPerSample; k++)
                                val |= b[p + (le ? k : bytesPerSample - 1 - k)] << (8 * k);
                        }
                    }
                    samples[i] = val;
                }
                return new CodecImage(columns, rows, spp, bitsAllocated, false, samples);
            }

            /// <summary>
            /// Extracts the significant bits from raw stored values, sign-extending them if
            /// the pixel data is signed.
            /// </summary>
            private void NormalizeRawSamples(int[] samples, int components)
            {
                int shift = highBit + 1 - bitsStored;
                if (bitsStored >= 32)
                {
                    if (!signed && components == 1)
                    {
                        for (int i = 0; i < samples.Length; i++)
                            samples[i] = (int)((uint)samples[i] >> 1);
                        valueScale = 2;
                    }
                    return;
                }
                int mask = (int)((1L << bitsStored) - 1);
                bool extend = signed && components == 1;
                int signBit = 1 << (bitsStored - 1);
                for (int i = 0; i < samples.Length; i++)
                {
                    int v = (samples[i] >> shift) & mask;
                    if (extend && (v & signBit) != 0)
                        v -= 1 << bitsStored;
                    samples[i] = v;
                }
            }

            private static void SignExtend(int[] samples, int bits)
            {
                if (bits <= 0 || bits >= 32)
                    return;
                int mask = (1 << bits) - 1, signBit = 1 << (bits - 1);
                for (int i = 0; i < samples.Length; i++)
                {
                    int v = samples[i] & mask;
                    samples[i] = (v & signBit) != 0 ? v - (1 << bits) : v;
                }
            }

            #endregion

            #region Grayscale

            /// <summary>
            /// Finds an attribute that may be in the data set itself, or, in an enhanced
            /// multi-frame image, in the functional group macros for the frame.
            /// </summary>
            private DicomDataSet? FindAttribute(uint tag, uint functionalGroup, int frame)
            {
                if (ds.Contains(tag))
                    return ds;
                var perFrame = ds.GetItems(TagPerFrameFunctionalGroups);
                if (perFrame != null && frame < perFrame.Count)
                {
                    var items = perFrame[frame].GetItems(functionalGroup);
                    if (items != null && items.Count > 0 && items[0].Contains(tag))
                        return items[0];
                }
                var shared = ds.GetItems(TagSharedFunctionalGroups);
                if (shared != null && shared.Count > 0)
                {
                    var items = shared[0].GetItems(functionalGroup);
                    if (items != null && items.Count > 0 && items[0].Contains(tag))
                        return items[0];
                }
                return null;
            }

            private sealed class Lut
            {
                public int FirstMapped;
                public int[] Data = Array.Empty<int>();
                public int Bits;

                public int Lookup(int v)
                {
                    int i = v - FirstMapped;
                    return Data[i < 0 ? 0 : i >= Data.Length ? Data.Length - 1 : i];
                }
            }

            private Lut? ReadLut(DicomDataSet item)
            {
                var desc = item.GetNumbers(TagLutDescriptor);
                var lutElement = item.Get(TagLutData);
                if (desc.Length < 3 || lutElement == null)
                    return null;
                int entries = desc[0] == 0 ? 65536 : (int)desc[0];
                int first = (int)desc[1];
                if (signed && first >= 32768)
                    first -= 65536;
                int bits = (int)desc[2];

                var lut = new Lut { FirstMapped = first, Bits = bits, Data = new int[entries] };
                byte[] b = item.Buffer;
                bool eightBitData = lutElement.Length == entries || (lutElement.Vr == "OB");
                for (int i = 0; i < entries; i++)
                {
                    if (eightBitData)
                        lut.Data[i] = i < lutElement.Length ? b[lutElement.Offset + i] : 0;
                    else
                    {
                        int p = lutElement.Offset + i * 2;
                        lut.Data[i] = p + 1 < lutElement.Offset + lutElement.Length ? (item.LittleEndian ? b[p] | (b[p + 1] << 8) : (b[p] << 8) | b[p + 1]) : 0;
                    }
                }
                if (eightBitData)
                    lut.Bits = 8;
                if (lut.Bits <= 0 || lut.Bits > 16)
                    lut.Bits = 16;
                return lut;
            }

            private void RenderGrayscale(int[] samples, int components, byte[] bgra, int frame)
            {
                int numPixels = bgra.Length / 4;

                // Histogram of the stored values present in the image, so we only need to
                // work out the output value once for each.
                int min = int.MaxValue, max = int.MinValue;
                for (int i = 0; i < numPixels; i++)
                {
                    int v = samples[i * components];
                    if (v < min) min = v;
                    if (v > max) max = v;
                }
                if (min > max)
                    min = max = 0;

                // The stored value that each table entry i corresponds to is
                // ((i + min) << valueShift) + valueOffset.
                long range = (long)max - min + 1;
                int valueShift = 0;
                long valueOffset = 0;
                if (range > (1 << 24))
                {
                    // Too wide to tabulate (e.g. 32-bit data); scale it down first.
                    while (range >> valueShift > (1 << 24))
                        valueShift++;
                    for (int i = 0; i < numPixels; i++)
                        samples[i * components] = (int)(((long)samples[i * components] - min) >> valueShift);
                    valueOffset = min;
                    min = 0;
                    max = (int)((range - 1) >> valueShift);
                    range = max + 1;
                }

                var present = new bool[range];
                for (int i = 0; i < numPixels; i++)
                    present[samples[i * components] - min] = true;

                // The padding value marks pixels outside of the actual image, and shouldn't
                // be taken into account when choosing a window automatically.
                if (ds.Contains(TagPixelPaddingValue) && valueShift == 0 && valueScale == 1)
                {
                    int padding = (int)ds.GetNumber(TagPixelPaddingValue, 0, signed);
                    if (padding >= min && padding <= max && range > 1)
                        present[padding - min] = false;
                }

                // Modality transform: stored values to real-world values.
                var modality = new double[range];
                var rescaleDs = FindAttribute(TagRescaleSlope, TagPixelValueTransformationSequence, frame) ?? FindAttribute(TagRescaleIntercept, TagPixelValueTransformationSequence, frame);
                double slope = rescaleDs?.GetNumber(TagRescaleSlope, 1) ?? 1;
                double intercept = rescaleDs?.GetNumber(TagRescaleIntercept, 0) ?? 0;
                if (slope == 0 || double.IsNaN(slope) || double.IsInfinity(slope))
                    slope = 1;
                Lut? modalityLut = null;
                if (rescaleDs == null)
                {
                    var seq = ds.GetItems(TagModalityLutSequence);
                    if (seq != null && seq.Count > 0)
                        modalityLut = ReadLut(seq[0]);
                }
                double modMin = double.MaxValue, modMax = double.MinValue;
                for (int i = 0; i < range; i++)
                {
                    double stored = ((double)(i + min) * (1L << valueShift) + valueOffset) * valueScale;
                    double m = modalityLut != null ? modalityLut.Lookup((int)stored) : stored * slope + intercept;
                    modality[i] = m;
                    if (present[i])
                    {
                        if (m < modMin) modMin = m;
                        if (m > modMax) modMax = m;
                    }
                }
                if (modMin > modMax)
                {
                    modMin = modality[0];
                    modMax = modality[range - 1];
                }

                // VOI transform: real-world values to display values.
                var output = new byte[range];
                bool haveVoi = false;
                var windowDs = FindAttribute(TagWindowCenter, TagFrameVoiLutSequence, frame);
                if (windowDs != null)
                {
                    var centers = windowDs.GetNumbers(TagWindowCenter);
                    var widths = windowDs.GetNumbers(TagWindowWidth);
                    string function = (windowDs.GetString(TagVoiLutFunction) ?? "LINEAR").ToUpperInvariant();
                    if (centers.Length > 0 && widths.Length > 0)
                    {
                        double c = centers[0], w = widths[0];
                        // Ignore windows that are invalid, or entirely outside the range of
                        // values in the image (they do happen).
                        if (w >= 1 && !double.IsNaN(c) && c + w / 2 >= modMin && c - w / 2 <= modMax)
                        {
                            for (int i = 0; i < range; i++)
                                output[i] = ApplyWindow(modality[i], c, w, function);
                            haveVoi = true;
                        }
                    }
                }
                if (!haveVoi)
                {
                    var voiSeq = ds.GetItems(TagVoiLutSequence);
                    var voiLut = voiSeq != null && voiSeq.Count > 0 ? ReadLut(voiSeq[0]) : null;
                    if (voiLut != null && voiLut.Data.Length > 0)
                    {
                        int lutMax = 0;
                        foreach (var v in voiLut.Data)
                            lutMax = Math.Max(lutMax, v);
                        // The stated bit depth of a VOI LUT is unreliable; use the actual maximum.
                        int outMax = Math.Max(1, Math.Min(lutMax, (1 << voiLut.Bits) - 1));
                        for (int i = 0; i < range; i++)
                            output[i] = ClampByte(voiLut.Lookup((int)Math.Round(modality[i])) * 255.0 / outMax);
                        haveVoi = true;
                    }
                }
                if (!haveVoi)
                {
                    // No VOI information: stretch the full range of values in the image.
                    double span = modMax - modMin;
                    for (int i = 0; i < range; i++)
                        output[i] = span <= 0 ? (byte)128 : ClampByte((modality[i] - modMin) * 255.0 / span);
                }

                if (photometric == "MONOCHROME1")
                {
                    for (int i = 0; i < range; i++)
                        output[i] = (byte)(255 - output[i]);
                }

                for (int i = 0; i < numPixels; i++)
                {
                    byte g = output[samples[i * components] - min];
                    bgra[i * 4] = g;
                    bgra[i * 4 + 1] = g;
                    bgra[i * 4 + 2] = g;
                }
            }

            private static byte ApplyWindow(double x, double c, double w, string function)
            {
                switch (function)
                {
                    case "SIGMOID":
                        return ClampByte(255.0 / (1 + Math.Exp(-4 * (x - c) / w)));
                    case "LINEAR_EXACT":
                        return ClampByte(((x - c) / w + 0.5) * 255.0);
                    default:
                        if (w <= 1)
                            return x > c - 0.5 ? (byte)255 : (byte)0;
                        return ClampByte(((x - (c - 0.5)) / (w - 1) + 0.5) * 255.0);
                }
            }

            private static byte ClampByte(double v)
            {
                return v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5);
            }

            private ImageData? RenderFloat(int frame)
            {
                var element = ds.Get(DicomDataSet.TagFloatPixelData) ?? ds.Get(DicomDataSet.TagDoublePixelData)!;
                bool isDouble = element.Tag == DicomDataSet.TagDoublePixelData;
                int size = isDouble ? 8 : 4;
                if (rows <= 0 || columns <= 0 || (long)rows * columns > CodecImage.MaxSamples)
                    throw new ImageDecodeException("DICOM file has invalid image dimensions.");
                int numPixels = rows * columns;
                var values = new double[numPixels];
                byte[] b = ds.Buffer;
                long start = element.Offset + (long)frame * numPixels * size;
                double min = double.MaxValue, max = double.MinValue;
                var tmp = new byte[8];
                for (int i = 0; i < numPixels; i++)
                {
                    long p = start + (long)i * size;
                    if (p + size > element.Offset + element.Length)
                        break;
                    Array.Copy(b, p, tmp, 0, size);
                    if (ds.LittleEndian != BitConverter.IsLittleEndian)
                        Array.Reverse(tmp, 0, size);
                    double v = isDouble ? BitConverter.ToDouble(tmp, 0) : BitConverter.ToSingle(tmp, 0);
                    if (double.IsNaN(v) || double.IsInfinity(v))
                        v = 0;
                    values[i] = v;
                    if (v < min) min = v;
                    if (v > max) max = v;
                }
                var windowDs = FindAttribute(TagWindowCenter, TagFrameVoiLutSequence, frame);
                double c = (min + max) / 2, w = max - min;
                if (windowDs != null)
                {
                    var centers = windowDs.GetNumbers(TagWindowCenter);
                    var widths = windowDs.GetNumbers(TagWindowWidth);
                    if (centers.Length > 0 && widths.Length > 0 && widths[0] > 0)
                    {
                        c = centers[0];
                        w = widths[0];
                    }
                }
                var bgra = new byte[numPixels * 4];
                for (int i = 0; i < numPixels; i++)
                {
                    byte g = w <= 0 ? (byte)128 : ClampByte((values[i] - (c - w / 2)) * 255.0 / w);
                    if (photometric == "MONOCHROME1")
                        g = (byte)(255 - g);
                    bgra[i * 4] = bgra[i * 4 + 1] = bgra[i * 4 + 2] = g;
                }
                return Util.LoadRgb(columns, rows, bgra);
            }

            #endregion

            #region Color

            private void RenderPalette(int[] samples, byte[] bgra)
            {
                var red = ReadPalette(0x00281101, 0x00281201, 0x00281221);
                var green = ReadPalette(0x00281102, 0x00281202, 0x00281222);
                var blue = ReadPalette(0x00281103, 0x00281203, 0x00281223);
                if (red == null || green == null || blue == null)
                {
                    // No usable palette; show the indices as grayscale instead.
                    RenderGrayscale(samples, 1, bgra, 0);
                    return;
                }
                int numPixels = bgra.Length / 4;
                for (int i = 0; i < numPixels; i++)
                {
                    int v = samples[i];
                    bgra[i * 4] = PaletteByte(blue, v);
                    bgra[i * 4 + 1] = PaletteByte(green, v);
                    bgra[i * 4 + 2] = PaletteByte(red, v);
                }
            }

            private static byte PaletteByte(Lut lut, int v)
            {
                int val = lut.Lookup(v);
                return lut.Bits > 8 ? (byte)(val >> (lut.Bits - 8)) : (byte)val;
            }

            private Lut? ReadPalette(uint descriptorTag, uint dataTag, uint segmentedTag)
            {
                var desc = ds.GetNumbers(descriptorTag);
                if (desc.Length < 3)
                    return null;
                int entries = desc[0] == 0 ? 65536 : (int)desc[0];
                int first = (int)desc[1];
                if (signed && first >= 32768)
                    first -= 65536;
                int bits = (int)desc[2];
                if (bits != 8 && bits != 16)
                    bits = 16;

                var lut = new Lut { FirstMapped = first, Bits = bits };
                var dataElement = ds.Get(dataTag);
                if (dataElement != null && dataElement.Length > 0)
                {
                    lut.Data = new int[entries];
                    byte[] b = ds.Buffer;
                    bool eightBit = dataElement.Length == entries;
                    int maxVal = 0;
                    for (int i = 0; i < entries; i++)
                    {
                        int v;
                        if (eightBit)
                            v = b[dataElement.Offset + i];
                        else
                        {
                            int p = dataElement.Offset + i * 2;
                            if (p + 1 >= dataElement.Offset + dataElement.Length)
                                break;
                            v = ds.LittleEndian ? b[p] | (b[p + 1] << 8) : (b[p] << 8) | b[p + 1];
                        }
                        lut.Data[i] = v;
                        maxVal = Math.Max(maxVal, v);
                    }
                    // Some files claim 16-bit entries that actually only go up to 255.
                    if (eightBit || (lut.Bits == 16 && maxVal < 256))
                        lut.Bits = 8;
                    return lut;
                }

                var segmented = ds.GetNumbers(segmentedTag);
                if (segmented.Length > 0)
                {
                    lut.Data = ExpandSegmentedLut(segmented, entries);
                    return lut;
                }
                return null;
            }

            /// <summary>
            /// Expands a segmented palette color lookup table (PS3.3 C.7.9.2).
            /// </summary>
            private static int[] ExpandSegmentedLut(double[] seg, int entries)
            {
                var output = new List<int>();
                int i = 0;
                while (i + 1 < seg.Length && output.Count < entries)
                {
                    int type = (int)seg[i], length = (int)seg[i + 1];
                    i += 2;
                    if (type == 0)
                    {
                        // Discrete: explicit values.
                        for (int k = 0; k < length && i < seg.Length; k++)
                            output.Add((int)seg[i++]);
                    }
                    else if (type == 1)
                    {
                        // Linear: ramp from the last value to the given one.
                        if (i >= seg.Length)
                            break;
                        double y0 = output.Count > 0 ? output[^1] : 0, y1 = seg[i++];
                        for (int k = 1; k <= length; k++)
                            output.Add((int)Math.Round(y0 + (y1 - y0) * k / length));
                    }
                    else if (type == 2)
                    {
                        // Indirect: copy segments from elsewhere in the table. Not seen in
                        // practice; skip the offset.
                        i += 2;
                    }
                    else
                    {
                        break;
                    }
                }
                while (output.Count < entries)
                    output.Add(output.Count > 0 ? output[^1] : 0);
                return output.ToArray();
            }

            private void RenderColor(int[] samples, int components, bool colorConverted, int bits, byte[] bgra)
            {
                int numPixels = bgra.Length / 4;
                int shift = Math.Max(0, bits - 8);
                string pi = colorConverted ? "RGB" : photometric;
                bool ybrFull = pi == "YBR_FULL" || pi == "YBR_FULL_422";
                bool ybrPartial = pi.StartsWith("YBR_PARTIAL");
                bool ybrIct = pi == "YBR_ICT";
                bool ybrRct = pi == "YBR_RCT";
                bool cmyk = pi == "CMYK" && components >= 4;
                // ARGB (retired) stores alpha first.
                int first = pi == "ARGB" && components >= 4 ? 1 : 0;

                for (int i = 0; i < numPixels; i++)
                {
                    int s = i * components + first;
                    int c0 = samples[s] >> shift, c1 = samples[s + 1] >> shift, c2 = samples[s + 2] >> shift;
                    double r, g, b;
                    if (ybrFull || ybrIct)
                    {
                        r = c0 + 1.402 * (c2 - 128);
                        g = c0 - 0.344136 * (c1 - 128) - 0.714136 * (c2 - 128);
                        b = c0 + 1.772 * (c1 - 128);
                    }
                    else if (ybrPartial)
                    {
                        double y = 1.1644 * (c0 - 16);
                        r = y + 1.5960 * (c2 - 128);
                        g = y - 0.3918 * (c1 - 128) - 0.8130 * (c2 - 128);
                        b = y + 2.0172 * (c1 - 128);
                    }
                    else if (ybrRct)
                    {
                        g = c0 - Math.Floor((c1 - 128 + c2 - 128) / 4.0);
                        r = c2 - 128 + g;
                        b = c1 - 128 + g;
                    }
                    else if (cmyk)
                    {
                        int k = samples[s + 3] >> shift;
                        r = (255 - c0) * (255 - k) / 255.0;
                        g = (255 - c1) * (255 - k) / 255.0;
                        b = (255 - c2) * (255 - k) / 255.0;
                    }
                    else
                    {
                        r = c0;
                        g = c1;
                        b = c2;
                    }
                    bgra[i * 4] = ClampByte(b);
                    bgra[i * 4 + 1] = ClampByte(g);
                    bgra[i * 4 + 2] = ClampByte(r);
                }
            }

            #endregion
        }
    }
}
