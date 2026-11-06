using System;
using System.IO;
using System.IO.Compression;

/*

Decoder for the pixel data of PNG images (the contents of their IDAT chunks), as
embedded in MNG and JNG datastreams. The chunks around the pixel data are handled by
the caller.

The data is a zlib stream of scanlines, each preceded by a byte that names the filter
that was applied to it. Samples of fewer than eight bits are packed into bytes most
significant bits first, and 16-bit samples are big-endian. An interlaced image is
stored as seven passes (Adam7), each a smaller image of its own, which together cover
every pixel once.

MNG adds a filter method (64) in which the red and blue samples of RGB and RGBA images
are also stored as differences from green.

Copyright 2026+ Dmitry Brant
https://dmitrybrant.com

License: MIT
*/

namespace DmitryBrant.ImageFormats
{
    internal static class PngDecoder
    {
        public const int FilterIntrapixel = 64;

        // The starting column and row, and the column and row spacing, of each Adam7 pass.
        private static readonly int[] PassX = { 0, 4, 0, 2, 0, 1, 0 };
        private static readonly int[] PassY = { 0, 0, 4, 0, 2, 0, 1 };
        private static readonly int[] PassDx = { 8, 8, 4, 4, 2, 2, 1 };
        private static readonly int[] PassDy = { 8, 8, 8, 4, 4, 2, 2 };

        /// <summary>
        /// Gets the number of samples per pixel of the given PNG color type.
        /// </summary>
        public static int Channels(int colorType)
        {
            return colorType switch
            {
                0 or 3 => 1,
                2 => 3,
                4 => 2,
                6 => 4,
                _ => throw new ImageDecodeException("Unsupported PNG color type: " + colorType)
            };
        }

        /// <summary>
        /// Checks that the given combination of color type and sample depth is one that PNG allows.
        /// </summary>
        public static void Validate(int colorType, int depth)
        {
            bool valid = colorType switch
            {
                0 => depth is 1 or 2 or 4 or 8 or 16,
                3 => depth is 1 or 2 or 4 or 8,
                2 or 4 or 6 => depth is 8 or 16,
                _ => false
            };
            if (!valid)
                throw new ImageDecodeException("Unsupported PNG color type and sample depth: " + colorType + ", " + depth);
        }

        /// <summary>
        /// Decompresses and unfilters PNG pixel data. Data that is missing at the end of a
        /// truncated stream is treated as zero.
        /// </summary>
        /// <param name="compressed">The concatenated contents of the IDAT chunks.</param>
        /// <returns>The samples of the image, top-down, with all the samples of each pixel together.</returns>
        public static int[] Decode(byte[] compressed, int width, int height, int colorType, int depth, int filterMethod, int interlaceMethod)
        {
            Validate(colorType, depth);
            if (filterMethod != 0 && filterMethod != FilterIntrapixel)
                throw new ImageDecodeException("Unsupported PNG filter method: " + filterMethod);
            int channels = Channels(colorType);
            if (width <= 0 || height <= 0 || (long)width * height * channels > CodecImage.MaxSamples)
                throw new ImageDecodeException("PNG image has invalid dimensions.");

            var samples = new int[width * height * channels];
            using var input = new ZLibStream(new MemoryStream(compressed), CompressionMode.Decompress);
            if (interlaceMethod == 0)
            {
                DecodePass(input, samples, width, height, channels, depth, 0, 0, 1, 1, width, height);
            }
            else
            {
                for (int pass = 0; pass < 7; pass++)
                {
                    int passWidth = (width - PassX[pass] + PassDx[pass] - 1) / PassDx[pass];
                    int passHeight = (height - PassY[pass] + PassDy[pass] - 1) / PassDy[pass];
                    if (passWidth > 0 && passHeight > 0)
                        DecodePass(input, samples, width, height, channels, depth, PassX[pass], PassY[pass], PassDx[pass], PassDy[pass], passWidth, passHeight);
                }
            }

            if (filterMethod == FilterIntrapixel && (colorType == 2 || colorType == 6))
            {
                int mask = (1 << depth) - 1;
                for (int i = 0; i < samples.Length; i += channels)
                {
                    samples[i] = (samples[i] + samples[i + 1]) & mask;
                    samples[i + 2] = (samples[i + 2] + samples[i + 1]) & mask;
                }
            }
            return samples;
        }

        private static void DecodePass(Stream input, int[] samples, int width, int height, int channels, int depth,
            int x0, int y0, int dx, int dy, int passWidth, int passHeight)
        {
            int rowBytes = (int)(((long)passWidth * channels * depth + 7) / 8);
            int bpp = Math.Max(1, channels * depth / 8);
            var prev = new byte[rowBytes];
            var row = new byte[rowBytes];
            var filterByte = new byte[1];
            for (int py = 0; py < passHeight; py++)
            {
                Array.Clear(row);
                int filter = Read(input, filterByte) == 1 ? filterByte[0] : 0;
                Read(input, row);
                Unfilter(filter, row, prev, bpp);

                int y = y0 + py * dy;
                int bitPos = 0;
                for (int px = 0; px < passWidth; px++)
                {
                    int o = (y * width + x0 + px * dx) * channels;
                    for (int c = 0; c < channels; c++)
                    {
                        int v;
                        if (depth == 8)
                            v = row[bitPos >> 3];
                        else if (depth == 16)
                            v = (row[bitPos >> 3] << 8) | row[(bitPos >> 3) + 1];
                        else
                            v = (row[bitPos >> 3] >> (8 - depth - (bitPos & 7))) & ((1 << depth) - 1);
                        samples[o + c] = v;
                        bitPos += depth;
                    }
                }
                (prev, row) = (row, prev);
            }
        }

        private static int Read(Stream input, byte[] buffer)
        {
            try
            {
                return input.ReadAtLeast(buffer, buffer.Length, false);
            }
            catch (InvalidDataException)
            {
                return 0;
            }
        }

        private static void Unfilter(int filter, byte[] row, byte[] prev, int bpp)
        {
            switch (filter)
            {
                case 1:
                    for (int i = bpp; i < row.Length; i++)
                        row[i] += row[i - bpp];
                    break;
                case 2:
                    for (int i = 0; i < row.Length; i++)
                        row[i] += prev[i];
                    break;
                case 3:
                    for (int i = 0; i < row.Length; i++)
                        row[i] += (byte)(((i >= bpp ? row[i - bpp] : 0) + prev[i]) >> 1);
                    break;
                case 4:
                    for (int i = 0; i < row.Length; i++)
                    {
                        int a = i >= bpp ? row[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                        row[i] += (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
                    }
                    break;
            }
        }
    }
}
