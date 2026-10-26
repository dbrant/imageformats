#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

/*

Decoder for DirectDraw Surface (DDS) textures.

A DDS file is a 128-byte header (the magic number "DDS " and a DDS_HEADER), optionally
followed by a DDS_HEADER_DXT10 extension that gives the format as a DXGI_FORMAT value,
and then the pixel data. A file can hold one texture, a cube map (six faces), an array
of textures or cube maps, or a volume texture (a stack of slices), each with a chain of
mipmaps. The surfaces are stored one after another, each followed by its mipmaps,
except that in a volume texture all the slices of each mipmap level are together.

We read the full-size image of each surface, or of each slice of a volume texture, as
a frame. Formats:

- Block-compressed: BC1 to BC7 (DXT1 to DXT5, ATI1, ATI2, BC6H and BC7), including the
  premultiplied-alpha DXT2 and DXT4, and the signed variants of BC4, BC5 and BC6H.
- Uncompressed, described by bit masks in the legacy header: RGB with or without alpha,
  luminance with or without alpha, alpha only, and the signed "bump map" formats.
- Uncompressed DXGI formats (and their legacy Direct3D 9 equivalents): 8, 16 and 32 bits
  per channel, unsigned or signed normalized, or floating point; packed formats such as
  B5G6R5, B4G4R4A4, R10G10B10A2, R11G11B10_FLOAT and R9G9B9E5_SHAREDEXP; and the 4:2:2
  formats YUY2, UYVY, R8G8_B8G8 and G8R8_G8B8.

Formats with a single channel are shown in gray. Signed values are mapped from the range
-1 to 1 to the range 0 to 1, as is usual for showing normal maps. Floating point (high
dynamic range) values are taken to be in linear light and are encoded as sRGB, with
values above 1 clipped. Premultiplied alpha is undone.

Copyright 2026+ Dmitry Brant
https://dmitrybrant.com

License: MIT
*/

namespace DmitryBrant.ImageFormats
{
    /// <summary>
    /// Handles reading DirectDraw Surface (DDS) textures.
    /// </summary>
    public static class DdsReader
    {
        /// <summary>
        /// Reads a DDS texture. If the file contains more than one image (for example, the
        /// faces of a cube map), the first one is read.
        /// </summary>
        /// <param name="fileName">Name of the file to read.</param>
        /// <returns>ImageData that contains the image that was read.</returns>
        public static ImageData Load(string fileName)
        {
            return Load(fileName, 0);
        }

        /// <summary>
        /// Reads a DDS texture from a stream. If the file contains more than one image (for
        /// example, the faces of a cube map), the first one is read.
        /// </summary>
        /// <param name="stream">Stream from which to read the image.</param>
        /// <returns>ImageData that contains the image that was read.</returns>
        public static ImageData Load(Stream stream)
        {
            return Load(stream, 0);
        }

        /// <summary>
        /// Reads the given image of a DDS texture: a face of a cube map, an element of a
        /// texture array, or a slice of a volume texture.
        /// </summary>
        /// <param name="fileName">Name of the file to read.</param>
        /// <param name="frame">Zero-based index of the image to read; see <see cref="GetFrameCount(string)"/>.</param>
        /// <returns>ImageData that contains the image that was read.</returns>
        public static ImageData Load(string fileName, int frame)
        {
            using var f = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Load(f, frame);
        }

        /// <summary>
        /// Reads the given image of a DDS texture from a stream: a face of a cube map, an
        /// element of a texture array, or a slice of a volume texture. Only the requested
        /// image is read, unless the stream can't seek.
        /// </summary>
        /// <param name="stream">Stream from which to read the image.</param>
        /// <param name="frame">Zero-based index of the image to read; see <see cref="GetFrameCount(Stream)"/>.</param>
        /// <returns>ImageData that contains the image that was read.</returns>
        public static ImageData Load(Stream stream, int frame)
        {
            var dds = new DdsFile(stream);
            if (frame < 0 || frame >= dds.FrameCount)
                throw new ArgumentOutOfRangeException(nameof(frame), "Frame " + frame + " is out of range; the file has " + dds.FrameCount + " frame(s).");
            return dds.Decode(frame);
        }

        /// <summary>
        /// Gets the number of images in a DDS texture: 6 for a cube map, the number of
        /// elements of a texture array (times 6 for an array of cube maps), the number of
        /// slices of a volume texture, and otherwise 1. Mipmaps are not counted.
        /// </summary>
        /// <param name="fileName">Name of the file to read.</param>
        public static int GetFrameCount(string fileName)
        {
            using var f = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
            return GetFrameCount(f);
        }

        /// <summary>
        /// Gets the number of images in a DDS texture: 6 for a cube map, the number of
        /// elements of a texture array (times 6 for an array of cube maps), the number of
        /// slices of a volume texture, and otherwise 1. Mipmaps are not counted.
        /// </summary>
        /// <param name="stream">Stream from which to read the image.</param>
        public static int GetFrameCount(Stream stream)
        {
            return new DdsFile(stream).FrameCount;
        }

        /// <summary>
        /// Reads all the images of a DDS texture. The file stays open while the sequence is
        /// enumerated, and each image is only read and decoded when it is reached.
        /// </summary>
        /// <param name="fileName">Name of the file to read.</param>
        /// <returns>The images of the file, in order.</returns>
        public static IEnumerable<ImageData> LoadFrames(string fileName)
        {
            // Check the header now, so that an invalid file fails here rather than when
            // the sequence is first enumerated.
            using (var f = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read))
                _ = new DdsFile(f);
            return EnumerateFrames(fileName);
        }

        /// <summary>
        /// Reads all the images of a DDS texture from a stream. Each image is only read and
        /// decoded when it is reached, so the stream must stay open while the sequence is
        /// enumerated.
        /// </summary>
        /// <param name="stream">Stream from which to read the image.</param>
        /// <returns>The images of the file, in order.</returns>
        public static IEnumerable<ImageData> LoadFrames(Stream stream)
        {
            return EnumerateFrames(new DdsFile(stream));
        }

        private static IEnumerable<ImageData> EnumerateFrames(string fileName)
        {
            using var f = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
            foreach (var frame in EnumerateFrames(new DdsFile(f)))
                yield return frame;
        }

        private static IEnumerable<ImageData> EnumerateFrames(DdsFile dds)
        {
            for (int i = 0; i < dds.FrameCount; i++)
                yield return dds.Decode(i);
        }

        /// <summary>
        /// Checks whether the given header is that of a DDS file.
        /// </summary>
        internal static bool HasSignature(byte[] header)
        {
            return header.Length >= 8 && header[0] == 'D' && header[1] == 'D' && header[2] == 'S' && header[3] == ' '
                && BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) == 124;
        }
    }

    /// <summary>
    /// The layout of a DDS file, read from its header, and the stream that holds its images.
    /// </summary>
    internal sealed class DdsFile
    {
        private const int HeaderSize = 128;
        private const int Dx10HeaderSize = 20;

        // Header flags.
        private const uint DdsdDepth = 0x800000;

        // Pixel format flags.
        private const uint DdpfAlphaPixels = 0x1;
        private const uint DdpfAlpha = 0x2;
        private const uint DdpfFourCC = 0x4;
        private const uint DdpfRgb = 0x40;
        private const uint DdpfLuminance = 0x20000;
        private const uint DdpfBumpDuDv = 0x80000;

        // Second capability flags.
        private const uint Caps2Cubemap = 0x200;
        private const uint Caps2CubemapFaces = 0xFC00;
        private const uint Caps2Volume = 0x200000;

        // DX10 resource dimensions and flags.
        private const int Texture3D = 4;
        private const uint MiscTextureCube = 0x4;
        private const int AlphaModePremultiplied = 2;
        private const int AlphaModeOpaque = 3;

        // Formats are DXGI_FORMAT values, plus these for the legacy formats that have none.
        private const int FormatMasked = 1000;
        private const int FormatUyvy = 1001;

        private readonly Stream stream;
        private readonly long basePosition;
        private readonly long dataOffset;

        private readonly int width;
        private readonly int height;
        private readonly int format;
        private readonly bool premultiplied;
        private readonly bool opaque;

        // The distance between consecutive frames in the file.
        private readonly long frameStride;

        // For FormatMasked, the bits per pixel, the pixel format flags and the masks of the
        // red, green, blue and alpha channels.
        private readonly int maskedBits;
        private readonly uint maskedFlags;
        private readonly uint[] masks = new uint[4];

        // Where the stream is now, relative to the start of the file, for streams that can't seek.
        private long position;

        public int FrameCount { get; }

        public DdsFile(Stream stream)
        {
            this.stream = stream;
            basePosition = stream.CanSeek ? stream.Position : 0;

            var header = new byte[HeaderSize];
            ReadAt(0, header, "This is not a valid DDS file.");
            if (!DdsReader.HasSignature(header))
                throw new ImageDecodeException("This is not a valid DDS file.");
            uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(offset));

            uint flags = U32(8);
            height = (int)U32(12);
            width = (int)U32(16);
            int depth = (int)U32(24);
            int mipCount = (int)Math.Max(1, U32(28));
            uint pfFlags = U32(80);
            uint fourCC = U32(84);
            uint caps2 = U32(112);
            if (width <= 0 || height <= 0 || (long)width * height * 4 > int.MaxValue)
                throw new ImageDecodeException("DDS image has invalid dimensions.");

            int surfaces = 1;
            bool volume = false;
            dataOffset = HeaderSize;
            if ((pfFlags & DdpfFourCC) != 0 && fourCC == FourCC("DX10"))
            {
                var dx10 = new byte[Dx10HeaderSize];
                ReadAt(HeaderSize, dx10);
                dataOffset += Dx10HeaderSize;
                format = (int)BinaryPrimitives.ReadUInt32LittleEndian(dx10);
                int dimension = (int)BinaryPrimitives.ReadUInt32LittleEndian(dx10.AsSpan(4));
                uint miscFlag = BinaryPrimitives.ReadUInt32LittleEndian(dx10.AsSpan(8));
                int arraySize = (int)Math.Max(1, BinaryPrimitives.ReadUInt32LittleEndian(dx10.AsSpan(12)));
                int alphaMode = (int)(BinaryPrimitives.ReadUInt32LittleEndian(dx10.AsSpan(16)) & 7);
                premultiplied = alphaMode == AlphaModePremultiplied;
                opaque = alphaMode == AlphaModeOpaque;
                volume = dimension == Texture3D;
                surfaces = (miscFlag & MiscTextureCube) != 0 ? arraySize * 6 : arraySize;
                format = TypedFormat(format);
            }
            else
            {
                if ((caps2 & Caps2Cubemap) != 0)
                    surfaces = Math.Max(1, BitOperations.PopCount(caps2 & Caps2CubemapFaces));
                volume = (caps2 & Caps2Volume) != 0 && (flags & DdsdDepth) != 0;
                if ((pfFlags & DdpfFourCC) != 0)
                {
                    format = LegacyFormat(fourCC, out premultiplied);
                }
                else if ((pfFlags & (DdpfRgb | DdpfLuminance | DdpfAlpha | DdpfBumpDuDv)) != 0)
                {
                    format = FormatMasked;
                    maskedFlags = pfFlags;
                    maskedBits = (int)U32(88);
                    for (int i = 0; i < 4; i++)
                        masks[i] = U32(92 + i * 4);
                    if (maskedBits != 8 && maskedBits != 16 && maskedBits != 24 && maskedBits != 32)
                        throw new ImageDecodeException("Unsupported DDS bit count: " + maskedBits);
                    // Many writers swap the red and blue masks of 10:10:10:2 formats, whose
                    // red is in fact in the low bits.
                    if (maskedBits == 32 && masks[0] == 0x3FF00000 && masks[2] == 0x3FF)
                        (masks[0], masks[2]) = (masks[2], masks[0]);
                }
                else
                {
                    throw new ImageDecodeException("Unsupported DDS pixel format.");
                }
            }
            if (BytesPerBlock(format) == 0 && BitsPerPixel() == 0)
                throw new ImageDecodeException("Unsupported DDS format: " + FormatName());

            // Mipmaps beyond the 1x1 level are meaningless, so a larger count is ignored.
            mipCount = Math.Min(mipCount, BitOperations.Log2((uint)Math.Max(width, height)) + 1);
            if (volume)
            {
                FrameCount = Math.Max(1, depth);
                frameStride = LevelSize(width, height);
            }
            else
            {
                FrameCount = surfaces;
                for (int i = 0; i < mipCount; i++)
                    frameStride += LevelSize(Math.Max(1, width >> i), Math.Max(1, height >> i));
            }
        }

        private static uint FourCC(string code)
        {
            return (uint)(code[0] | (code[1] << 8) | (code[2] << 16) | (code[3] << 24));
        }

        /// <summary>
        /// Gets the equivalent DXGI format of a legacy FourCC code.
        /// </summary>
        private static int LegacyFormat(uint fourCC, out bool premultiplied)
        {
            premultiplied = fourCC == FourCC("DXT2") || fourCC == FourCC("DXT4");
            if (fourCC == FourCC("DXT1")) return 71;
            if (fourCC == FourCC("DXT2") || fourCC == FourCC("DXT3")) return 74;
            if (fourCC == FourCC("DXT4") || fourCC == FourCC("DXT5")) return 77;
            if (fourCC == FourCC("ATI1") || fourCC == FourCC("BC4U")) return 80;
            if (fourCC == FourCC("BC4S")) return 81;
            if (fourCC == FourCC("ATI2") || fourCC == FourCC("BC5U")) return 83;
            if (fourCC == FourCC("BC5S")) return 84;
            if (fourCC == FourCC("RGBG")) return 68;
            if (fourCC == FourCC("GRGB")) return 69;
            if (fourCC == FourCC("YUY2")) return 107;
            if (fourCC == FourCC("UYVY")) return FormatUyvy;
            // Direct3D 9 formats, given as numbers rather than codes.
            return fourCC switch
            {
                36 => 11,   // D3DFMT_A16B16G16R16
                110 => 13,  // D3DFMT_Q16W16V16U16
                111 => 54,  // D3DFMT_R16F
                112 => 34,  // D3DFMT_G16R16F
                113 => 10,  // D3DFMT_A16B16G16R16F
                114 => 41,  // D3DFMT_R32F
                115 => 16,  // D3DFMT_G32R32F
                116 => 2,   // D3DFMT_A32B32G32R32F
                _ => throw new ImageDecodeException("Unsupported DDS FourCC: " + FourCCName(fourCC))
            };
        }

        private static string FourCCName(uint fourCC)
        {
            Span<char> chars = stackalloc char[4];
            for (int i = 0; i < 4; i++)
            {
                char c = (char)((fourCC >> (i * 8)) & 0xFF);
                if (c < 0x20 || c > 0x7E)
                    return fourCC.ToString();
                chars[i] = c;
            }
            return new string(chars);
        }

        private string FormatName()
        {
            return format == FormatMasked ? "bit masks" : format == FormatUyvy ? "UYVY" : "DXGI " + format;
        }

        /// <summary>
        /// Gets the format to decode a "typeless" DXGI format as.
        /// </summary>
        private static int TypedFormat(int format)
        {
            return format switch
            {
                1 => 2,     // R32G32B32A32
                5 => 6,     // R32G32B32
                9 => 11,    // R16G16B16A16
                15 => 16,   // R32G32
                23 => 24,   // R10G10B10A2
                27 => 28,   // R8G8B8A8
                33 => 35,   // R16G16
                39 => 41,   // R32
                48 => 49,   // R8G8
                53 => 56,   // R16
                60 => 61,   // R8
                70 => 71,   // BC1
                73 => 74,   // BC2
                76 => 77,   // BC3
                79 => 80,   // BC4
                82 => 83,   // BC5
                90 => 87,   // B8G8R8A8
                92 => 88,   // B8G8R8X8
                94 => 95,   // BC6H
                97 => 98,   // BC7
                _ => format
            };
        }

        private static int BytesPerBlock(int format)
        {
            return format switch
            {
                71 or 72 or 80 or 81 => 8,
                74 or 75 or 77 or 78 or 83 or 84 or 95 or 96 or 98 or 99 => 16,
                _ => 0
            };
        }

        private int BitsPerPixel()
        {
            return format switch
            {
                FormatMasked => maskedBits,
                2 => 128,
                6 => 96,
                10 or 11 or 13 or 16 => 64,
                24 or 26 or 28 or 29 or 31 or 34 or 35 or 37 or 41 or 67 or 87 or 88 or 91 or 93 => 32,
                49 or 51 or 54 or 56 or 58 or 85 or 86 or 115 => 16,
                61 or 63 or 65 => 8,
                // 4:2:2 formats, with two pixels in four bytes.
                68 or 69 or 107 or FormatUyvy => 16,
                _ => 0
            };
        }

        private bool IsPacked422 => format is 68 or 69 or 107 or FormatUyvy;

        private long RowSize(int w)
        {
            int blockBytes = BytesPerBlock(format);
            if (blockBytes > 0)
                return (long)((w + 3) / 4) * blockBytes;
            if (IsPacked422)
                return (long)((w + 1) / 2) * 4;
            return ((long)w * BitsPerPixel() + 7) / 8;
        }

        private long LevelSize(int w, int h)
        {
            int rows = BytesPerBlock(format) > 0 ? (h + 3) / 4 : h;
            return RowSize(w) * rows;
        }

        public ImageData Decode(int frame)
        {
            long size = LevelSize(width, height);
            if (size > int.MaxValue)
                throw new ImageDecodeException("DDS image is too large.");
            var raw = new byte[size];
            ReadAt(dataOffset + frame * frameStride, raw);

            var data = new byte[width * height * 4];
            if (BytesPerBlock(format) > 0)
                DecodeBlocks(raw, data);
            else if (IsPacked422)
                DecodePacked422(raw, data);
            else
                DecodeUncompressed(raw, data);

            if (opaque)
            {
                for (int i = 3; i < data.Length; i += 4)
                    data[i] = 0xFF;
            }
            else if (premultiplied)
            {
                for (int i = 0; i < data.Length; i += 4)
                {
                    int a = data[i + 3];
                    if (a == 255)
                        continue;
                    for (int c = 0; c < 3; c++)
                        data[i + c] = a == 0 ? (byte)0 : (byte)Math.Min(255, data[i + c] * 255 / a);
                }
            }
            return new ImageData(width, height, data);
        }

        private void DecodeBlocks(byte[] raw, byte[] data)
        {
            int blockBytes = BytesPerBlock(format);
            int blocksX = (width + 3) / 4, blocksY = (height + 3) / 4;
            Span<byte> pixels = stackalloc byte[64];
            Span<float> channel = stackalloc float[16];
            Span<float> channel2 = stackalloc float[16];
            Span<ushort> halves = stackalloc ushort[48];
            for (int by = 0; by < blocksY; by++)
            {
                for (int bx = 0; bx < blocksX; bx++)
                {
                    var block = raw.AsSpan((by * blocksX + bx) * blockBytes, blockBytes);
                    switch (format)
                    {
                        case 71: case 72:
                            BcDecoder.DecodeColor(block, pixels, false);
                            break;
                        case 74: case 75:
                            BcDecoder.DecodeColor(block[8..], pixels, true);
                            BcDecoder.DecodeExplicitAlpha(block, pixels);
                            break;
                        case 77: case 78:
                            BcDecoder.DecodeColor(block[8..], pixels, true);
                            BcDecoder.DecodeChannel(block, channel, false);
                            for (int i = 0; i < 16; i++)
                                pixels[i * 4 + 3] = ToByte(channel[i]);
                            break;
                        case 80: case 81:
                            BcDecoder.DecodeChannel(block, channel, format == 81);
                            for (int i = 0; i < 16; i++)
                            {
                                byte v = ToByte(format == 81 ? FromSigned(channel[i]) : channel[i]);
                                pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = v;
                                pixels[i * 4 + 3] = 0xFF;
                            }
                            break;
                        case 83: case 84:
                            BcDecoder.DecodeChannel(block, channel, format == 84);
                            BcDecoder.DecodeChannel(block[8..], channel2, format == 84);
                            for (int i = 0; i < 16; i++)
                            {
                                pixels[i * 4] = 0;
                                pixels[i * 4 + 1] = ToByte(format == 84 ? FromSigned(channel2[i]) : channel2[i]);
                                pixels[i * 4 + 2] = ToByte(format == 84 ? FromSigned(channel[i]) : channel[i]);
                                pixels[i * 4 + 3] = 0xFF;
                            }
                            break;
                        case 95: case 96:
                            BptcDecoder.DecodeBc6h(block, halves, format == 96);
                            for (int i = 0; i < 16; i++)
                            {
                                pixels[i * 4] = ColorConversion.LinearToSrgbByte((float)BitConverter.UInt16BitsToHalf(halves[i * 3 + 2]));
                                pixels[i * 4 + 1] = ColorConversion.LinearToSrgbByte((float)BitConverter.UInt16BitsToHalf(halves[i * 3 + 1]));
                                pixels[i * 4 + 2] = ColorConversion.LinearToSrgbByte((float)BitConverter.UInt16BitsToHalf(halves[i * 3]));
                                pixels[i * 4 + 3] = 0xFF;
                            }
                            break;
                        default:
                            BptcDecoder.DecodeBc7(block, pixels);
                            break;
                    }

                    int x0 = bx * 4, y0 = by * 4;
                    int w = Math.Min(4, width - x0), h = Math.Min(4, height - y0);
                    for (int y = 0; y < h; y++)
                        pixels.Slice(y * 16, w * 4).CopyTo(data.AsSpan(((y0 + y) * width + x0) * 4));
                }
            }
        }

        private void DecodePacked422(byte[] raw, byte[] data)
        {
            int rowSize = (int)RowSize(width);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int p = y * rowSize + (x / 2) * 4;
                    int r, g, b;
                    switch (format)
                    {
                        case 68: // R8G8_B8G8
                            r = raw[p];
                            g = raw[p + 1 + (x & 1) * 2];
                            b = raw[p + 2];
                            break;
                        case 69: // G8R8_G8B8
                            r = raw[p + 1];
                            g = raw[p + (x & 1) * 2];
                            b = raw[p + 3];
                            break;
                        default:
                        {
                            int luma, u, v;
                            if (format == FormatUyvy)
                            {
                                luma = raw[p + 1 + (x & 1) * 2];
                                u = raw[p];
                                v = raw[p + 2];
                            }
                            else
                            {
                                luma = raw[p + (x & 1) * 2];
                                u = raw[p + 1];
                                v = raw[p + 3];
                            }
                            // ITU-R BT.601, with luma from 16 to 235.
                            int c = 298 * (luma - 16), d = u - 128, e = v - 128;
                            r = Math.Clamp((c + 409 * e + 128) >> 8, 0, 255);
                            g = Math.Clamp((c - 100 * d - 208 * e + 128) >> 8, 0, 255);
                            b = Math.Clamp((c + 516 * d + 128) >> 8, 0, 255);
                            break;
                        }
                    }
                    int i = (y * width + x) * 4;
                    data[i] = (byte)b;
                    data[i + 1] = (byte)g;
                    data[i + 2] = (byte)r;
                    data[i + 3] = 0xFF;
                }
            }
        }

        private void DecodeUncompressed(byte[] raw, byte[] data)
        {
            int rowSize = (int)RowSize(width);
            int bytesPerPixel = BitsPerPixel() / 8;
            bool linear = format is 2 or 6 or 10 or 16 or 26 or 34 or 41 or 54 or 67;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    var p = raw.AsSpan(y * rowSize + x * bytesPerPixel, bytesPerPixel);
                    int i = (y * width + x) * 4;
                    switch (format)
                    {
                        // The common 8-bit formats, which need no conversion.
                        case 28: case 29:
                            data[i] = p[2]; data[i + 1] = p[1]; data[i + 2] = p[0]; data[i + 3] = p[3];
                            continue;
                        case 87: case 91:
                            data[i] = p[0]; data[i + 1] = p[1]; data[i + 2] = p[2]; data[i + 3] = p[3];
                            continue;
                        case 88: case 93:
                            data[i] = p[0]; data[i + 1] = p[1]; data[i + 2] = p[2]; data[i + 3] = 0xFF;
                            continue;
                    }

                    ReadPixel(p, out float r, out float g, out float b, out float a);
                    if (linear)
                    {
                        data[i] = ColorConversion.LinearToSrgbByte(b);
                        data[i + 1] = ColorConversion.LinearToSrgbByte(g);
                        data[i + 2] = ColorConversion.LinearToSrgbByte(r);
                    }
                    else
                    {
                        data[i] = ToByte(b);
                        data[i + 1] = ToByte(g);
                        data[i + 2] = ToByte(r);
                    }
                    data[i + 3] = ToByte(a);
                }
            }
        }

        /// <summary>
        /// Reads one pixel of an uncompressed format, as values from 0 to 1 (or beyond, for
        /// floating point formats).
        /// </summary>
        private void ReadPixel(ReadOnlySpan<byte> p, out float r, out float g, out float b, out float a)
        {
            static float F32(ReadOnlySpan<byte> p, int i) => BinaryPrimitives.ReadSingleLittleEndian(p[(i * 4)..]);
            static float F16(ReadOnlySpan<byte> p, int i) => (float)BinaryPrimitives.ReadHalfLittleEndian(p[(i * 2)..]);
            static float U16(ReadOnlySpan<byte> p, int i) => BinaryPrimitives.ReadUInt16LittleEndian(p[(i * 2)..]) / 65535f;
            static float S16(ReadOnlySpan<byte> p, int i) => FromSigned(Math.Max(BinaryPrimitives.ReadInt16LittleEndian(p[(i * 2)..]), (short)-32767) / 32767f);
            static float U8(ReadOnlySpan<byte> p, int i) => p[i] / 255f;
            static float S8(ReadOnlySpan<byte> p, int i) => FromSigned(Math.Max((sbyte)p[i], (sbyte)-127) / 127f);

            r = g = b = 0;
            a = 1;
            switch (format)
            {
                case 2: r = F32(p, 0); g = F32(p, 1); b = F32(p, 2); a = F32(p, 3); break;
                case 6: r = F32(p, 0); g = F32(p, 1); b = F32(p, 2); break;
                case 10: r = F16(p, 0); g = F16(p, 1); b = F16(p, 2); a = F16(p, 3); break;
                case 11: r = U16(p, 0); g = U16(p, 1); b = U16(p, 2); a = U16(p, 3); break;
                case 13: r = S16(p, 0); g = S16(p, 1); b = S16(p, 2); a = S16(p, 3); break;
                case 16: r = F32(p, 0); g = F32(p, 1); break;
                case 24:
                {
                    uint v = BinaryPrimitives.ReadUInt32LittleEndian(p);
                    r = (v & 0x3FF) / 1023f;
                    g = ((v >> 10) & 0x3FF) / 1023f;
                    b = ((v >> 20) & 0x3FF) / 1023f;
                    a = (v >> 30) / 3f;
                    break;
                }
                case 26:
                {
                    uint v = BinaryPrimitives.ReadUInt32LittleEndian(p);
                    r = SmallFloat(v & 0x7FF, 6);
                    g = SmallFloat((v >> 11) & 0x7FF, 6);
                    b = SmallFloat(v >> 22, 5);
                    break;
                }
                case 31: r = S8(p, 0); g = S8(p, 1); b = S8(p, 2); a = S8(p, 3); break;
                case 34: r = F16(p, 0); g = F16(p, 1); break;
                case 35: r = U16(p, 0); g = U16(p, 1); break;
                case 37: r = S16(p, 0); g = S16(p, 1); break;
                case 41: r = g = b = F32(p, 0); break;
                case 49: r = U8(p, 0); g = U8(p, 1); break;
                case 51: r = S8(p, 0); g = S8(p, 1); break;
                case 54: r = g = b = F16(p, 0); break;
                case 56: r = g = b = U16(p, 0); break;
                case 58: r = g = b = S16(p, 0); break;
                case 61: r = g = b = U8(p, 0); break;
                case 63: r = g = b = S8(p, 0); break;
                case 65: a = U8(p, 0); break;
                case 67:
                {
                    uint v = BinaryPrimitives.ReadUInt32LittleEndian(p);
                    float scale = MathF.Pow(2, (int)(v >> 27) - 15 - 9);
                    r = (v & 0x1FF) * scale;
                    g = ((v >> 9) & 0x1FF) * scale;
                    b = ((v >> 18) & 0x1FF) * scale;
                    break;
                }
                case 85:
                {
                    int v = BinaryPrimitives.ReadUInt16LittleEndian(p);
                    b = (v & 0x1F) / 31f;
                    g = ((v >> 5) & 0x3F) / 63f;
                    r = (v >> 11) / 31f;
                    break;
                }
                case 86:
                {
                    int v = BinaryPrimitives.ReadUInt16LittleEndian(p);
                    b = (v & 0x1F) / 31f;
                    g = ((v >> 5) & 0x1F) / 31f;
                    r = ((v >> 10) & 0x1F) / 31f;
                    a = v >> 15;
                    break;
                }
                case 115:
                {
                    int v = BinaryPrimitives.ReadUInt16LittleEndian(p);
                    b = (v & 0xF) / 15f;
                    g = ((v >> 4) & 0xF) / 15f;
                    r = ((v >> 8) & 0xF) / 15f;
                    a = (v >> 12) / 15f;
                    break;
                }
                case FormatMasked:
                {
                    uint v = 0;
                    for (int i = 0; i < p.Length; i++)
                        v |= (uint)p[i] << (i * 8);
                    bool signed = (maskedFlags & DdpfBumpDuDv) != 0;
                    if ((maskedFlags & DdpfLuminance) != 0)
                    {
                        r = g = b = Channel(v, masks[0], false);
                    }
                    else if ((maskedFlags & (DdpfRgb | DdpfBumpDuDv)) != 0)
                    {
                        r = Channel(v, masks[0], signed);
                        g = Channel(v, masks[1], signed);
                        b = Channel(v, masks[2], signed);
                    }
                    if (((maskedFlags & (DdpfAlphaPixels | DdpfAlpha)) != 0 || signed) && masks[3] != 0)
                        a = Channel(v, masks[3], signed);
                    break;
                }
            }
        }

        /// <summary>
        /// Extracts the channel with the given bit mask from a pixel.
        /// </summary>
        private static float Channel(uint pixel, uint mask, bool signed)
        {
            if (mask == 0)
                return 0;
            int shift = BitOperations.TrailingZeroCount(mask);
            int bits = BitOperations.PopCount(mask);
            uint value = (pixel & mask) >> shift;
            if (!signed)
                return value / (float)(mask >> shift);
            int max = (1 << (bits - 1)) - 1;
            int s = (int)(value << (32 - bits)) >> (32 - bits);
            return FromSigned(Math.Max(s, -max) / (float)max);
        }

        /// <summary>
        /// Decodes the unsigned 11-bit and 10-bit floating point values of R11G11B10_FLOAT,
        /// which have a 5-bit exponent and a 6-bit or 5-bit mantissa.
        /// </summary>
        private static float SmallFloat(uint value, int mantissaBits)
        {
            int exponent = (int)(value >> mantissaBits);
            float mantissa = (value & ((1u << mantissaBits) - 1)) / (float)(1 << mantissaBits);
            if (exponent == 0)
                return mantissa * MathF.Pow(2, -14);
            if (exponent == 31)
                return mantissa == 0 ? float.PositiveInfinity : float.NaN;
            return (1 + mantissa) * MathF.Pow(2, exponent - 15);
        }

        private static float FromSigned(float value)
        {
            return (value + 1) / 2;
        }

        private static byte ToByte(float value)
        {
            return !(value > 0) ? (byte)0 : value >= 1 ? (byte)255 : (byte)(value * 255 + 0.5f);
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
                        throw new ImageDecodeException(error ?? "DDS file is truncated.");
                    position += skipped;
                }
            }
            if (stream.ReadAtLeast(buffer, buffer.Length, false) < buffer.Length)
                throw new ImageDecodeException(error ?? "DDS file is truncated.");
            position = offset + buffer.Length;
        }
    }
}
