#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

/*

Decoder for SER video files (originally from Lucam Recorder), as used for planetary,
lunar and solar imaging.

A SER file is a 178-byte header, followed by the frames, all of the same size and
uncompressed, and optionally by a trailer of per-frame timestamps, which we ignore.
Each frame is monochrome, a raw Bayer mosaic (RGB or CMY), or RGB/BGR, with 1 to 16
bits per sample. Samples of up to 8 bits are stored in one byte, aligned to the most
significant bit; samples of 9 to 16 bits are stored in two bytes, aligned to the least
significant bit.

The header field that gives the byte order of 16-bit samples is named LittleEndian,
and the specification says 1 means little-endian. Nearly every program that writes
SER files does the opposite, so we follow them: 0 means little-endian.

Bayer frames are demosaiced with bilinear interpolation.

Copyright 2026+ Dmitry Brant
https://dmitrybrant.com

License: MIT
*/

namespace DmitryBrant.ImageFormats
{
    /// <summary>
    /// Handles reading SER (Lucam Recorder) video files.
    /// </summary>
    public static class SerReader
    {
        private const int HeaderSize = 178;
        private const string Signature = "LUCAM-RECORDER";

        private const int ColorMono = 0;
        private const int ColorRgb = 100;
        private const int ColorBgr = 101;

        /// <summary>
        /// Reads the first frame of a SER file.
        /// </summary>
        /// <param name="fileName">Name of the file to read.</param>
        /// <returns>ImageData that contains the image that was read.</returns>
        public static ImageData Load(string fileName)
        {
            return Load(fileName, 0);
        }

        /// <summary>
        /// Reads the first frame of a SER file from a stream.
        /// </summary>
        /// <param name="stream">Stream from which to read the image.</param>
        /// <returns>ImageData that contains the image that was read.</returns>
        public static ImageData Load(Stream stream)
        {
            return Load(stream, 0);
        }

        /// <summary>
        /// Reads the given frame of a SER file.
        /// </summary>
        /// <param name="fileName">Name of the file to read.</param>
        /// <param name="frame">Zero-based index of the frame to read; see <see cref="GetFrameCount(string)"/>.</param>
        /// <returns>ImageData that contains the frame that was read.</returns>
        public static ImageData Load(string fileName, int frame)
        {
            using var f = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Load(f, frame);
        }

        /// <summary>
        /// Reads the given frame of a SER file from a stream. Only the requested frame is
        /// read, unless the stream can't seek.
        /// </summary>
        /// <param name="stream">Stream from which to read the image.</param>
        /// <param name="frame">Zero-based index of the frame to read; see <see cref="GetFrameCount(Stream)"/>.</param>
        /// <returns>ImageData that contains the frame that was read.</returns>
        public static ImageData Load(Stream stream, int frame)
        {
            var ser = new SerFile(stream);
            if (frame < 0 || frame >= ser.FrameCount)
                throw new ArgumentOutOfRangeException(nameof(frame), "Frame " + frame + " is out of range; the file has " + ser.FrameCount + " frame(s).");
            return ser.Decode(frame);
        }

        /// <summary>
        /// Gets the number of frames in a SER file.
        /// </summary>
        /// <param name="fileName">Name of the file to read.</param>
        public static int GetFrameCount(string fileName)
        {
            using var f = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
            return GetFrameCount(f);
        }

        /// <summary>
        /// Gets the number of frames in a SER file.
        /// </summary>
        /// <param name="stream">Stream from which to read the image.</param>
        public static int GetFrameCount(Stream stream)
        {
            return new SerFile(stream).FrameCount;
        }

        /// <summary>
        /// Reads all the frames of a SER file. The file stays open while the sequence is
        /// enumerated, and each frame is only read and decoded when it is reached.
        /// </summary>
        /// <param name="fileName">Name of the file to read.</param>
        /// <returns>The frames of the file, in order.</returns>
        public static IEnumerable<ImageData> LoadFrames(string fileName)
        {
            // Check the header now, so that an invalid file fails here rather than when
            // the sequence is first enumerated.
            using (var f = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read))
                _ = new SerFile(f);
            return EnumerateFrames(fileName);
        }

        /// <summary>
        /// Reads all the frames of a SER file from a stream. Each frame is only read and
        /// decoded when it is reached, so the stream must stay open while the sequence is
        /// enumerated.
        /// </summary>
        /// <param name="stream">Stream from which to read the image.</param>
        /// <returns>The frames of the file, in order.</returns>
        public static IEnumerable<ImageData> LoadFrames(Stream stream)
        {
            return EnumerateFrames(new SerFile(stream));
        }

        private static IEnumerable<ImageData> EnumerateFrames(string fileName)
        {
            using var f = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
            foreach (var frame in EnumerateFrames(new SerFile(f)))
                yield return frame;
        }

        private static IEnumerable<ImageData> EnumerateFrames(SerFile ser)
        {
            for (int i = 0; i < ser.FrameCount; i++)
                yield return ser.Decode(i);
        }

        /// <summary>
        /// Checks whether the given header is that of a SER file.
        /// </summary>
        internal static bool HasSignature(byte[] header)
        {
            if (header.Length < Signature.Length)
                return false;
            for (int i = 0; i < Signature.Length; i++)
            {
                if (header[i] != Signature[i])
                    return false;
            }
            return true;
        }

        /// <summary>
        /// The layout of a SER file, read from its header, and the stream that holds its frames.
        /// </summary>
        private sealed class SerFile
        {
            private readonly Stream stream;
            private readonly long basePosition;
            private readonly int colorId;
            private readonly bool littleEndian;
            private readonly int width;
            private readonly int height;
            private readonly int depth;
            private readonly int planes;
            private readonly int bytesPerSample;
            private readonly int frameSize;

            // Where the stream is now, relative to the start of the file, for streams that can't seek.
            private long position;

            // For Bayer frames, the letters of the color filter pattern, starting at the top
            // left: two from the top row, then two from the second row.
            private readonly string? pattern;

            public int FrameCount { get; }

            public SerFile(Stream stream)
            {
                this.stream = stream;
                basePosition = stream.CanSeek ? stream.Position : 0;

                var header = new byte[HeaderSize];
                if (stream.ReadAtLeast(header, HeaderSize, false) < HeaderSize || !HasSignature(header))
                    throw new ImageDecodeException("This is not a valid SER file.");
                position = HeaderSize;

                colorId = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(18));
                littleEndian = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(22)) == 0;
                width = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(26));
                height = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(30));
                depth = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(34));
                int headerFrameCount = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(38));

                pattern = colorId switch
                {
                    8 => "RGGB",
                    9 => "GRBG",
                    10 => "GBRG",
                    11 => "BGGR",
                    16 => "CYYM",
                    17 => "YCMY",
                    18 => "YMCY",
                    19 => "MYYC",
                    _ => null
                };
                if (pattern != null || colorId == ColorMono)
                    planes = 1;
                else if (colorId == ColorRgb || colorId == ColorBgr)
                    planes = 3;
                else
                    throw new ImageDecodeException("Unsupported SER color format: " + colorId);

                if (depth < 1 || depth > 16)
                    throw new ImageDecodeException("Unsupported SER bit depth: " + depth);
                bytesPerSample = depth > 8 ? 2 : 1;

                if (width <= 0 || height <= 0 || (long)width * height * Math.Max(4, planes * bytesPerSample) > int.MaxValue)
                    throw new ImageDecodeException("SER image has invalid dimensions.");
                frameSize = width * height * planes * bytesPerSample;

                // Trust the frames that are actually there over the header, which may be
                // wrong if the recording was cut short.
                long count = headerFrameCount;
                if (stream.CanSeek)
                {
                    long available = Math.Max(0, stream.Length - basePosition - HeaderSize) / frameSize;
                    if (count <= 0 || count > available)
                        count = available;
                }
                if (count <= 0)
                    throw new ImageDecodeException("SER file contains no frames.");
                FrameCount = (int)Math.Min(count, int.MaxValue);
            }

            public ImageData Decode(int index)
            {
                var raw = new byte[frameSize];
                long offset = HeaderSize + (long)index * frameSize;
                if (stream.CanSeek)
                {
                    stream.Position = basePosition + offset;
                }
                else
                {
                    if (offset < position)
                        throw new NotSupportedException("Frames must be read in order from a stream that can't seek.");
                    while (position < offset)
                    {
                        int skipped = stream.Read(raw, 0, (int)Math.Min(raw.Length, offset - position));
                        if (skipped == 0)
                            throw new ImageDecodeException("SER file is truncated.");
                        position += skipped;
                    }
                }
                if (stream.ReadAtLeast(raw, frameSize, false) < frameSize)
                    throw new ImageDecodeException("SER file is truncated.");
                position = offset + frameSize;

                int count = width * height * planes;
                var samples = new int[count];
                int maxValue;
                if (bytesPerSample == 1)
                {
                    for (int i = 0; i < count; i++)
                        samples[i] = raw[i];
                    maxValue = 255;
                }
                else
                {
                    int highest = 0;
                    for (int i = 0; i < count; i++)
                    {
                        int v = littleEndian ? raw[i * 2] | (raw[i * 2 + 1] << 8) : (raw[i * 2] << 8) | raw[i * 2 + 1];
                        samples[i] = v;
                        highest = Math.Max(highest, v);
                    }
                    // Some programs align samples to the most significant bit anyway.
                    maxValue = (1 << depth) - 1;
                    if (highest > maxValue)
                        maxValue = 0xFFFF;
                }

                int pixels = width * height;
                var data = new byte[pixels * 4];
                if (pattern != null)
                {
                    bool cmy = pattern.Contains('C');
                    var rgb = Demosaic(samples, cmy ? "CYM" : "RGB");
                    for (int i = 0; i < pixels; i++)
                    {
                        int c0 = rgb[i * 3], c1 = rgb[i * 3 + 1], c2 = rgb[i * 3 + 2];
                        if (cmy)
                        {
                            // Each complementary filter passes two primaries: C = G + B,
                            // Y = R + G, M = R + B.
                            int c = c0, y = c1, m = c2;
                            c0 = (y + m - c) / 2;
                            c1 = (y + c - m) / 2;
                            c2 = (c + m - y) / 2;
                        }
                        data[i * 4] = Scale(c2, maxValue);
                        data[i * 4 + 1] = Scale(c1, maxValue);
                        data[i * 4 + 2] = Scale(c0, maxValue);
                        data[i * 4 + 3] = 0xFF;
                    }
                }
                else if (planes == 3)
                {
                    int r = colorId == ColorRgb ? 0 : 2, b = 2 - r;
                    for (int i = 0; i < pixels; i++)
                    {
                        data[i * 4] = Scale(samples[i * 3 + b], maxValue);
                        data[i * 4 + 1] = Scale(samples[i * 3 + 1], maxValue);
                        data[i * 4 + 2] = Scale(samples[i * 3 + r], maxValue);
                        data[i * 4 + 3] = 0xFF;
                    }
                }
                else
                {
                    for (int i = 0; i < pixels; i++)
                    {
                        byte v = Scale(samples[i], maxValue);
                        data[i * 4] = v;
                        data[i * 4 + 1] = v;
                        data[i * 4 + 2] = v;
                        data[i * 4 + 3] = 0xFF;
                    }
                }
                return new ImageData(width, height, data);
            }

            /// <summary>
            /// Fills in the two missing channels at each pixel of a Bayer mosaic.
            /// </summary>
            /// <param name="mosaic">One sample per pixel.</param>
            /// <param name="channels">The letters of the pattern, in the order of the output channels.</param>
            /// <returns>Three samples per pixel, in the order of the given channels.</returns>
            private int[] Demosaic(int[] mosaic, string channels)
            {
                Span<int> cfa = stackalloc int[4];
                for (int i = 0; i < 4; i++)
                    cfa[i] = channels.IndexOf(pattern![i]);
                return ImageFormats.Demosaic.Bilinear(mosaic, width, height, cfa);
            }

            private static byte Scale(int value, int maxValue)
            {
                return value <= 0 ? (byte)0 : value >= maxValue ? (byte)255 : (byte)((value * 255 + maxValue / 2) / maxValue);
            }
        }
    }
}
