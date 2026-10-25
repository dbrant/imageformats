#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

/*

Decoder for Cine video files (.CIN, .CINE) from Phantom high-speed cameras by Vision
Research.

A Cine file starts with a 44-byte header, which gives the positions of a Windows
BITMAPINFOHEADER that describes the frames, a SETUP structure with the settings of
the camera, and a table of the positions of the frames. Each frame is preceded by a
variable-length annotation.

Frames are uncompressed. They are monochrome, raw color (a mosaic from a sensor with
a color filter array, which we demosaic with bilinear interpolation), or interpolated
color in BGR order, with 8 or 16 bits per sample. Samples stored in 16 bits are not
scaled up; their real bit depth is in the SETUP structure. Raw frames may also be
"packed", with 10 bits per sample and a gamma curve applied, which we convert back to
12-bit linear values. Packed frames are stored top-down, and all others bottom-up.

The specification gives each color filter pattern as its rows from the top of the
image as displayed, separated by slashes. Those of the CFA_VRI and CFA_VRIV6 sensors
of older cameras are four pixels wide: "gbrg/rggb" and "bggr/grbg". (These look like
pairs of 2x2 patterns, but the samples show otherwise.)

The SETUP structure has grown over the years, and only those fields that fit within
its recorded length are present. Older files have no CFA field to tell the color
pattern, so a raw file without one is assumed to be from a color camera of that time,
which used the CFA_VRI pattern.

JPEG-compressed Cine files (.CCI) use a proprietary codec, and aren't supported.

Copyright 2026+ Dmitry Brant
https://dmitrybrant.com

License: MIT
*/

namespace DmitryBrant.ImageFormats
{
    /// <summary>
    /// Handles reading Cine video files from Phantom high-speed cameras.
    /// </summary>
    public static class CineReader
    {
        private const int HeaderSize = 44;
        private const int BitmapHeaderSize = 40;

        private const int CompressionJpeg = 1;
        private const int CompressionRaw = 2;

        private const int BiRgb = 0;
        private const int BiPacked = 256;

        private const int CfaVri = 1;

        // Offsets of the fields that we use in the SETUP structure.
        private const int SetupMark = 0x8C;
        private const int SetupLength = 0x8E;
        private const int SetupCfa = 0x328;
        private const int SetupWbGain = 0x354;
        private const int SetupRealBpp = 0x380;
        private const int SetupMetaWb = 0x165C;
        private const int SetupBlackLevel = 0x1664;
        private const int SetupWhiteLevel = 0x1668;
        private const int SetupEnd = SetupWhiteLevel + 4;

        // Black and white levels of packed samples, after conversion to 12-bit linear.
        private const int PackedBlack = 64;
        private const int PackedWhite = 4064;

        /// <summary>
        /// Reads the first frame of a Cine file.
        /// </summary>
        /// <param name="fileName">Name of the file to read.</param>
        /// <returns>ImageData that contains the image that was read.</returns>
        public static ImageData Load(string fileName)
        {
            return Load(fileName, 0);
        }

        /// <summary>
        /// Reads the first frame of a Cine file from a stream.
        /// </summary>
        /// <param name="stream">Stream from which to read the image.</param>
        /// <returns>ImageData that contains the image that was read.</returns>
        public static ImageData Load(Stream stream)
        {
            return Load(stream, 0);
        }

        /// <summary>
        /// Reads the given frame of a Cine file.
        /// </summary>
        /// <param name="fileName">Name of the file to read.</param>
        /// <param name="frame">Zero-based index of the frame to read, counting from the first
        /// frame saved in the file; see <see cref="GetFrameCount(string)"/>.</param>
        /// <returns>ImageData that contains the frame that was read.</returns>
        public static ImageData Load(string fileName, int frame)
        {
            using var f = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Load(f, frame);
        }

        /// <summary>
        /// Reads the given frame of a Cine file from a stream. Only the requested frame is
        /// read, unless the stream can't seek.
        /// </summary>
        /// <param name="stream">Stream from which to read the image.</param>
        /// <param name="frame">Zero-based index of the frame to read, counting from the first
        /// frame saved in the file; see <see cref="GetFrameCount(Stream)"/>.</param>
        /// <returns>ImageData that contains the frame that was read.</returns>
        public static ImageData Load(Stream stream, int frame)
        {
            var cine = new CineFile(stream);
            if (frame < 0 || frame >= cine.FrameCount)
                throw new ArgumentOutOfRangeException(nameof(frame), "Frame " + frame + " is out of range; the file has " + cine.FrameCount + " frame(s).");
            return cine.Decode(frame);
        }

        /// <summary>
        /// Gets the number of frames in a Cine file.
        /// </summary>
        /// <param name="fileName">Name of the file to read.</param>
        public static int GetFrameCount(string fileName)
        {
            using var f = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
            return GetFrameCount(f);
        }

        /// <summary>
        /// Gets the number of frames in a Cine file.
        /// </summary>
        /// <param name="stream">Stream from which to read the image.</param>
        public static int GetFrameCount(Stream stream)
        {
            return new CineFile(stream).FrameCount;
        }

        /// <summary>
        /// Reads all the frames of a Cine file. The file stays open while the sequence is
        /// enumerated, and each frame is only read and decoded when it is reached.
        /// </summary>
        /// <param name="fileName">Name of the file to read.</param>
        /// <returns>The frames of the file, in order.</returns>
        public static IEnumerable<ImageData> LoadFrames(string fileName)
        {
            // Check the header now, so that an invalid file fails here rather than when
            // the sequence is first enumerated.
            using (var f = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read))
                _ = new CineFile(f);
            return EnumerateFrames(fileName);
        }

        /// <summary>
        /// Reads all the frames of a Cine file from a stream. Each frame is only read and
        /// decoded when it is reached, so the stream must stay open while the sequence is
        /// enumerated.
        /// </summary>
        /// <param name="stream">Stream from which to read the image.</param>
        /// <returns>The frames of the file, in order.</returns>
        public static IEnumerable<ImageData> LoadFrames(Stream stream)
        {
            return EnumerateFrames(new CineFile(stream));
        }

        private static IEnumerable<ImageData> EnumerateFrames(string fileName)
        {
            using var f = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
            foreach (var frame in EnumerateFrames(new CineFile(f)))
                yield return frame;
        }

        private static IEnumerable<ImageData> EnumerateFrames(CineFile cine)
        {
            for (int i = 0; i < cine.FrameCount; i++)
                yield return cine.Decode(i);
        }

        /// <summary>
        /// Checks whether the given header is that of a Cine file.
        /// </summary>
        internal static bool HasSignature(byte[] header)
        {
            if (header.Length < HeaderSize || header[0] != 'C' || header[1] != 'I')
                return false;
            var h = header.AsSpan();
            return BinaryPrimitives.ReadUInt16LittleEndian(h[2..]) == HeaderSize
                && BinaryPrimitives.ReadUInt16LittleEndian(h[4..]) <= CompressionRaw
                && BinaryPrimitives.ReadUInt16LittleEndian(h[6..]) <= 1
                && BinaryPrimitives.ReadUInt32LittleEndian(h[24..]) >= HeaderSize;
        }

        /// <summary>
        /// The layout of a Cine file, read from its headers, and the stream that holds its frames.
        /// </summary>
        private sealed class CineFile
        {
            private readonly Stream stream;
            private readonly long basePosition;
            private readonly long[] frameOffsets;

            private readonly int width;
            private readonly int height;
            private readonly bool bottomUp;
            private readonly bool packed;
            private readonly int planes;
            private readonly int bytesPerSample;
            private readonly int stride;

            // Samples at or below black are shown as black, and at or above white as white.
            private readonly int black;
            private readonly int white;

            // For raw color frames, the channel (0 = red, 1 = green, 2 = blue) of each pixel
            // of the filter pattern, row by row from the top left of the image as displayed.
            private readonly int[]? cfa;
            private readonly int cfaWidth;

            // The high bits of the CFA field, which mark the quadrants of the image that come
            // from monochrome sensors, on cameras with more than one.
            private readonly uint grayQuadrants;

            // White balance gains for red and blue, for each quadrant of the image, or null if
            // the samples have them applied already.
            private readonly float[]? wbGains;

            // Where the stream is now, relative to the start of the file, for streams that can't seek.
            private long position;

            public int FrameCount => frameOffsets.Length;

            public CineFile(Stream stream)
            {
                this.stream = stream;
                basePosition = stream.CanSeek ? stream.Position : 0;

                var header = new byte[HeaderSize];
                ReadAt(0, header, "This is not a valid Cine file.");
                if (!HasSignature(header))
                    throw new ImageDecodeException("This is not a valid Cine file.");
                int compression = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4));
                int version = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6));
                uint imageCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20));
                long offImageHeader = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24));
                long offSetup = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(28));
                long offImageOffsets = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(32));
                if (compression == CompressionJpeg)
                    throw new ImageDecodeException("JPEG-compressed Cine files are not supported.");

                var bih = new byte[BitmapHeaderSize];
                ReadAt(offImageHeader, bih);
                width = BinaryPrimitives.ReadInt32LittleEndian(bih.AsSpan(4));
                height = BinaryPrimitives.ReadInt32LittleEndian(bih.AsSpan(8));
                int bitCount = BinaryPrimitives.ReadUInt16LittleEndian(bih.AsSpan(14));
                uint biCompression = BinaryPrimitives.ReadUInt32LittleEndian(bih.AsSpan(16));
                uint clrImportant = BinaryPrimitives.ReadUInt32LittleEndian(bih.AsSpan(36));

                // Phantom software doesn't write negative heights, but a top-down DIB would have one.
                bottomUp = height > 0;
                height = Math.Abs(height);

                if (biCompression == BiPacked)
                {
                    packed = true;
                    bottomUp = false;
                    planes = 1;
                }
                else if (biCompression == BiRgb && (bitCount == 8 || bitCount == 16 || bitCount == 24 || bitCount == 48))
                {
                    planes = bitCount == 24 || bitCount == 48 ? 3 : 1;
                    bytesPerSample = bitCount / planes / 8;
                }
                else
                {
                    throw new ImageDecodeException("Unsupported Cine pixel format: " + bitCount + " bits, compression " + biCompression);
                }

                if (width <= 0 || height <= 0 || (long)width * height * Math.Max(4, planes * bytesPerSample) > int.MaxValue)
                    throw new ImageDecodeException("Cine image has invalid dimensions.");
                stride = width * planes * bytesPerSample;

                // Read as much of the SETUP structure as we use, and as the file has.
                var setup = new byte[SetupEnd];
                ReadAt(offSetup, setup.AsSpan(0, SetupLength + 2));
                int setupLength = 0;
                if (setup[SetupMark] == 'S' && setup[SetupMark + 1] == 'T')
                {
                    setupLength = Math.Min((int)BinaryPrimitives.ReadUInt16LittleEndian(setup.AsSpan(SetupLength)), SetupEnd);
                    if (setupLength > SetupLength + 2)
                        ReadAt(offSetup + SetupLength + 2, setup.AsSpan(SetupLength + 2, setupLength - SetupLength - 2));
                }
                int? SetupInt(int offset) => offset + 4 <= setupLength ? BinaryPrimitives.ReadInt32LittleEndian(setup.AsSpan(offset)) : null;

                if (compression == CompressionRaw && planes == 1)
                {
                    uint cfaCode = (uint)(SetupInt(SetupCfa) ?? CfaVri);
                    cfa = (cfaCode & 0xFFFFFF) switch
                    {
                        1 => new[] { 1, 2, 0, 1, 0, 1, 1, 2 }, // CFA_VRI
                        2 => new[] { 2, 1, 1, 0, 1, 0, 2, 1 }, // CFA_VRIV6
                        3 => new[] { 1, 2, 0, 1 }, // CFA_BAYER
                        4 => new[] { 0, 1, 1, 2 }, // CFA_BAYERFLIP
                        _ => null
                    };
                    cfaWidth = cfa?.Length == 8 ? 4 : 2;
                    grayQuadrants = cfaCode >> 28;

                    if (cfa != null && SetupInt(SetupMetaWb) is int metaWb && metaWb != 0)
                    {
                        wbGains = new float[8];
                        for (int i = 0; i < 8; i++)
                        {
                            float gain = BinaryPrimitives.ReadSingleLittleEndian(setup.AsSpan(SetupWbGain + i * 4));
                            wbGains[i] = gain > 0 && gain < 100 ? gain : 1;
                        }
                    }
                }

                if (packed)
                {
                    black = PackedBlack;
                    white = PackedWhite;
                }
                else
                {
                    int depth = 8;
                    if (bytesPerSample == 2)
                    {
                        int realBpp = SetupInt(SetupRealBpp) ?? 0;
                        if (realBpp > 8 && realBpp <= 16)
                            depth = realBpp;
                        else if (clrImportant > 256 && clrImportant <= 65536 && (clrImportant & (clrImportant - 1)) == 0)
                            depth = BitOperations.Log2(clrImportant);
                        else
                            depth = 16;
                    }
                    int maxValue = (1 << depth) - 1;
                    black = 0;
                    white = maxValue;

                    // The levels are of raw samples, which interpolated color frames are not.
                    if (planes == 1 && SetupInt(SetupBlackLevel) is int b && SetupInt(SetupWhiteLevel) is int w
                        && b >= 0 && b < w && w <= maxValue)
                    {
                        black = b;
                        white = w;
                    }
                }

                // Version 0 files have 32-bit frame offsets, and later ones 64-bit.
                int offsetSize = version == 0 ? 4 : 8;
                if (imageCount == 0)
                    throw new ImageDecodeException("Cine file contains no frames.");
                if (imageCount > int.MaxValue / offsetSize
                    || (stream.CanSeek && offImageOffsets + (long)imageCount * offsetSize > stream.Length - basePosition))
                    throw new ImageDecodeException("Cine file is truncated.");
                var table = new byte[imageCount * offsetSize];
                ReadAt(offImageOffsets, table);
                frameOffsets = new long[imageCount];
                for (int i = 0; i < frameOffsets.Length; i++)
                {
                    frameOffsets[i] = offsetSize == 4
                        ? BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(i * 4))
                        : BinaryPrimitives.ReadInt64LittleEndian(table.AsSpan(i * 8));
                }
            }

            public ImageData Decode(int index)
            {
                long offset = frameOffsets[index];
                if (offset < 0)
                    throw new ImageDecodeException("Cine frame " + index + " has an invalid offset.");
                var sizeBytes = new byte[4];
                ReadAt(offset, sizeBytes);
                long annotationSize = BinaryPrimitives.ReadUInt32LittleEndian(sizeBytes);
                if (annotationSize < 8 || annotationSize > 0x1000000)
                    throw new ImageDecodeException("Cine frame " + index + " has an invalid annotation.");
                var annotation = new byte[annotationSize - 4];
                ReadAt(offset + 4, annotation);

                // The annotation ends with the size of the pixel data, which tells us whether
                // the rows are padded to four bytes, as in a Windows bitmap.
                long imageSize = BinaryPrimitives.ReadUInt32LittleEndian(annotation.AsSpan(annotation.Length - 4));
                int rowSize = stride;
                if (!packed && (stride & 3) != 0 && imageSize == (long)((stride + 3) & ~3) * height)
                    rowSize = (stride + 3) & ~3;

                int pixels = width * height;
                long rawSize = packed ? ((long)pixels * 10 + 7) / 8 : (long)rowSize * height;
                // One byte more than needed, so that packed samples can always be read in pairs of bytes.
                var raw = new byte[rawSize + 1];
                ReadAt(offset + annotationSize, raw.AsSpan(0, (int)rawSize));

                var samples = new int[pixels * planes];
                if (packed)
                {
                    for (int i = 0; i < pixels; i++)
                    {
                        long bit = (long)i * 10;
                        int pos = (int)(bit >> 3);
                        int v = ((raw[pos] << 8) | raw[pos + 1]) >> (6 - (int)(bit & 7));
                        samples[i] = PackedToLinear[v & 0x3FF];
                    }
                }
                else
                {
                    int rowSamples = width * planes;
                    for (int y = 0; y < height; y++)
                    {
                        int src = (bottomUp ? height - 1 - y : y) * rowSize;
                        int dst = y * rowSamples;
                        if (bytesPerSample == 1)
                        {
                            for (int x = 0; x < rowSamples; x++)
                                samples[dst + x] = raw[src + x];
                        }
                        else
                        {
                            for (int x = 0; x < rowSamples; x++)
                                samples[dst + x] = raw[src + x * 2] | (raw[src + x * 2 + 1] << 8);
                        }
                    }
                }

                var data = new byte[pixels * 4];
                if (cfa != null)
                {
                    var rgb = Demosaic.Bilinear(samples, width, height, cfa, cfaWidth);
                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            int i = y * width + x;
                            int quadrant = (y >= height / 2 ? 2 : 0) | (x >= width / 2 ? 1 : 0);
                            if ((grayQuadrants & (8u >> quadrant)) != 0)
                            {
                                byte v = Scale(samples[i], 1);
                                data[i * 4] = v;
                                data[i * 4 + 1] = v;
                                data[i * 4 + 2] = v;
                            }
                            else
                            {
                                float gainR = wbGains != null ? wbGains[quadrant * 2] : 1;
                                float gainB = wbGains != null ? wbGains[quadrant * 2 + 1] : 1;
                                data[i * 4] = Scale(rgb[i * 3 + 2], gainB);
                                data[i * 4 + 1] = Scale(rgb[i * 3 + 1], 1);
                                data[i * 4 + 2] = Scale(rgb[i * 3], gainR);
                            }
                            data[i * 4 + 3] = 0xFF;
                        }
                    }
                }
                else if (planes == 3)
                {
                    for (int i = 0; i < pixels; i++)
                    {
                        data[i * 4] = Scale(samples[i * 3], 1);
                        data[i * 4 + 1] = Scale(samples[i * 3 + 1], 1);
                        data[i * 4 + 2] = Scale(samples[i * 3 + 2], 1);
                        data[i * 4 + 3] = 0xFF;
                    }
                }
                else
                {
                    for (int i = 0; i < pixels; i++)
                    {
                        byte v = Scale(samples[i], 1);
                        data[i * 4] = v;
                        data[i * 4 + 1] = v;
                        data[i * 4 + 2] = v;
                        data[i * 4 + 3] = 0xFF;
                    }
                }
                return new ImageData(width, height, data);
            }

            private byte Scale(int value, float gain)
            {
                if (value <= black)
                    return 0;
                float v = (value - black) * gain * 255f / (white - black);
                return v >= 255 ? (byte)255 : (byte)(v + 0.5f);
            }

            /// <summary>
            /// Reads the given number of bytes from the given offset in the file. A stream that
            /// can't seek must be read in order.
            /// </summary>
            private void ReadAt(long offset, Span<byte> buffer, string? error = null)
            {
                if (stream.CanSeek)
                {
                    stream.Position = basePosition + offset;
                }
                else
                {
                    if (offset < position)
                        throw new NotSupportedException("Frames must be read in order from a stream that can't seek.");
                    var skip = new byte[(int)Math.Min(0x10000, offset - position)];
                    while (position < offset)
                    {
                        int skipped = stream.Read(skip, 0, (int)Math.Min(skip.Length, offset - position));
                        if (skipped == 0)
                            throw new ImageDecodeException(error ?? "Cine file is truncated.");
                        position += skipped;
                    }
                }
                if (stream.ReadAtLeast(buffer, buffer.Length, false) < buffer.Length)
                    throw new ImageDecodeException(error ?? "Cine file is truncated.");
                position = offset + buffer.Length;
            }
        }

        /// <summary>
        /// Converts packed 10-bit samples, which have a gamma curve applied, to 12-bit linear
        /// values. From the Cine file format specification.
        /// </summary>
        private static readonly int[] PackedToLinear =
        {
               2,    5,    6,    7,    8,    9,   10,   11,   12,   13,   14,   15,   16,   17,   17,   18,
              19,   20,   21,   22,   23,   24,   25,   26,   27,   28,   29,   30,   31,   32,   33,   33,
              34,   35,   36,   37,   38,   39,   40,   41,   42,   43,   44,   45,   46,   47,   48,   48,
              49,   50,   51,   52,   53,   54,   55,   56,   57,   58,   59,   60,   61,   62,   63,   63,
              64,   65,   66,   67,   68,   69,   70,   71,   72,   73,   74,   75,   76,   77,   78,   79,
              79,   80,   81,   82,   83,   84,   85,   86,   87,   88,   89,   90,   91,   92,   93,   94,
              94,   95,   96,   97,   98,   99,  100,  101,  102,  103,  104,  105,  106,  107,  108,  109,
             110,  110,  111,  112,  113,  114,  115,  116,  117,  118,  119,  120,  121,  122,  123,  124,
             125,  125,  126,  127,  128,  129,  130,  131,  132,  133,  134,  135,  136,  137,  137,  138,
             139,  140,  141,  142,  143,  144,  145,  146,  147,  148,  149,  150,  151,  152,  153,  154,
             156,  157,  158,  159,  160,  161,  162,  163,  164,  165,  167,  168,  169,  170,  171,  172,
             173,  175,  176,  177,  178,  179,  181,  182,  183,  184,  186,  187,  188,  189,  191,  192,
             193,  194,  196,  197,  198,  200,  201,  202,  204,  205,  206,  208,  209,  210,  212,  213,
             215,  216,  217,  219,  220,  222,  223,  225,  226,  227,  229,  230,  232,  233,  235,  236,
             238,  239,  241,  242,  244,  245,  247,  249,  250,  252,  253,  255,  257,  258,  260,  261,
             263,  265,  266,  268,  270,  271,  273,  275,  276,  278,  280,  281,  283,  285,  287,  288,
             290,  292,  294,  295,  297,  299,  301,  302,  304,  306,  308,  310,  312,  313,  315,  317,
             319,  321,  323,  325,  327,  328,  330,  332,  334,  336,  338,  340,  342,  344,  346,  348,
             350,  352,  354,  356,  358,  360,  362,  364,  366,  368,  370,  372,  374,  377,  379,  381,
             383,  385,  387,  389,  391,  394,  396,  398,  400,  402,  404,  407,  409,  411,  413,  416,
             418,  420,  422,  425,  427,  429,  431,  434,  436,  438,  441,  443,  445,  448,  450,  452,
             455,  457,  459,  462,  464,  467,  469,  472,  474,  476,  479,  481,  484,  486,  489,  491,
             494,  496,  499,  501,  504,  506,  509,  511,  514,  517,  519,  522,  524,  527,  529,  532,
             535,  537,  540,  543,  545,  548,  551,  553,  556,  559,  561,  564,  567,  570,  572,  575,
             578,  581,  583,  586,  589,  592,  594,  597,  600,  603,  606,  609,  611,  614,  617,  620,
             623,  626,  629,  632,  635,  637,  640,  643,  646,  649,  652,  655,  658,  661,  664,  667,
             670,  673,  676,  679,  682,  685,  688,  691,  694,  698,  701,  704,  707,  710,  713,  716,
             719,  722,  726,  729,  732,  735,  738,  742,  745,  748,  751,  754,  758,  761,  764,  767,
             771,  774,  777,  781,  784,  787,  790,  794,  797,  800,  804,  807,  811,  814,  817,  821,
             824,  828,  831,  834,  838,  841,  845,  848,  852,  855,  859,  862,  866,  869,  873,  876,
             880,  883,  887,  890,  894,  898,  901,  905,  908,  912,  916,  919,  923,  927,  930,  934,
             938,  941,  945,  949,  952,  956,  960,  964,  967,  971,  975,  979,  982,  986,  990,  994,
             998, 1001, 1005, 1009, 1013, 1017, 1021, 1025, 1028, 1032, 1036, 1040, 1044, 1048, 1052, 1056,
            1060, 1064, 1068, 1072, 1076, 1080, 1084, 1088, 1092, 1096, 1100, 1104, 1108, 1112, 1116, 1120,
            1124, 1128, 1132, 1137, 1141, 1145, 1149, 1153, 1157, 1162, 1166, 1170, 1174, 1178, 1183, 1187,
            1191, 1195, 1200, 1204, 1208, 1212, 1217, 1221, 1225, 1230, 1234, 1238, 1243, 1247, 1251, 1256,
            1260, 1264, 1269, 1273, 1278, 1282, 1287, 1291, 1295, 1300, 1304, 1309, 1313, 1318, 1322, 1327,
            1331, 1336, 1340, 1345, 1350, 1354, 1359, 1363, 1368, 1372, 1377, 1382, 1386, 1391, 1396, 1400,
            1405, 1410, 1414, 1419, 1424, 1428, 1433, 1438, 1443, 1447, 1452, 1457, 1462, 1466, 1471, 1476,
            1481, 1486, 1490, 1495, 1500, 1505, 1510, 1515, 1520, 1524, 1529, 1534, 1539, 1544, 1549, 1554,
            1559, 1564, 1569, 1574, 1579, 1584, 1589, 1594, 1599, 1604, 1609, 1614, 1619, 1624, 1629, 1634,
            1639, 1644, 1649, 1655, 1660, 1665, 1670, 1675, 1680, 1685, 1691, 1696, 1701, 1706, 1711, 1717,
            1722, 1727, 1732, 1738, 1743, 1748, 1753, 1759, 1764, 1769, 1775, 1780, 1785, 1791, 1796, 1801,
            1807, 1812, 1818, 1823, 1828, 1834, 1839, 1845, 1850, 1856, 1861, 1867, 1872, 1878, 1883, 1889,
            1894, 1900, 1905, 1911, 1916, 1922, 1927, 1933, 1939, 1944, 1950, 1956, 1961, 1967, 1972, 1978,
            1984, 1989, 1995, 2001, 2007, 2012, 2018, 2024, 2030, 2035, 2041, 2047, 2053, 2058, 2064, 2070,
            2076, 2082, 2087, 2093, 2099, 2105, 2111, 2117, 2123, 2129, 2135, 2140, 2146, 2152, 2158, 2164,
            2170, 2176, 2182, 2188, 2194, 2200, 2206, 2212, 2218, 2224, 2231, 2237, 2243, 2249, 2255, 2261,
            2267, 2273, 2279, 2286, 2292, 2298, 2304, 2310, 2317, 2323, 2329, 2335, 2341, 2348, 2354, 2360,
            2366, 2373, 2379, 2385, 2392, 2398, 2404, 2411, 2417, 2423, 2430, 2436, 2443, 2449, 2455, 2462,
            2468, 2475, 2481, 2488, 2494, 2501, 2507, 2514, 2520, 2527, 2533, 2540, 2546, 2553, 2559, 2566,
            2572, 2579, 2586, 2592, 2599, 2605, 2612, 2619, 2625, 2632, 2639, 2645, 2652, 2659, 2666, 2672,
            2679, 2686, 2693, 2699, 2706, 2713, 2720, 2726, 2733, 2740, 2747, 2754, 2761, 2767, 2774, 2781,
            2788, 2795, 2802, 2809, 2816, 2823, 2830, 2837, 2844, 2850, 2857, 2864, 2871, 2878, 2885, 2893,
            2900, 2907, 2914, 2921, 2928, 2935, 2942, 2949, 2956, 2963, 2970, 2978, 2985, 2992, 2999, 3006,
            3013, 3021, 3028, 3035, 3042, 3049, 3057, 3064, 3071, 3078, 3086, 3093, 3100, 3108, 3115, 3122,
            3130, 3137, 3144, 3152, 3159, 3166, 3174, 3181, 3189, 3196, 3204, 3211, 3218, 3226, 3233, 3241,
            3248, 3256, 3263, 3271, 3278, 3286, 3294, 3301, 3309, 3316, 3324, 3331, 3339, 3347, 3354, 3362,
            3370, 3377, 3385, 3393, 3400, 3408, 3416, 3423, 3431, 3439, 3447, 3454, 3462, 3470, 3478, 3486,
            3493, 3501, 3509, 3517, 3525, 3533, 3540, 3548, 3556, 3564, 3572, 3580, 3588, 3596, 3604, 3612,
            3620, 3628, 3636, 3644, 3652, 3660, 3668, 3676, 3684, 3692, 3700, 3708, 3716, 3724, 3732, 3740,
            3749, 3757, 3765, 3773, 3781, 3789, 3798, 3806, 3814, 3822, 3830, 3839, 3847, 3855, 3863, 3872,
            3880, 3888, 3897, 3905, 3913, 3922, 3930, 3938, 3947, 3955, 3963, 3972, 3980, 3989, 3997, 4006,
            4014, 4022, 4031, 4039, 4048, 4056, 4064, 4095, 4095, 4095, 4095, 4095, 4095, 4095, 4095, 4095
        };
    }
}
