using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

/*

Decoder for Autodesk Animator (.FLI) and Animator Pro (.FLC) animations.

A FLIC file has a 128-byte header (with the signature 0xAF11 for FLI, or 0xAF12
for FLC), followed by a sequence of frames, each of which is a list of chunks.
The image is always 8 bits per pixel, indexed into a 256-color palette. The first
frame is usually a complete picture, and every later frame stores only what has
changed since the previous one (palette entries and/or pixels), so decoding a
given frame means decoding every frame before it. After the last frame there is
usually one more "ring" frame, which turns the last frame back into the first, to
make looping easy; it is not counted as a frame of its own.

The chunk types are:
  4  COLOR_256: palette entries, with components from 0 to 255.
  7  DELTA_FLC: changed pixels, as word-oriented runs (FLC only).
  11 COLOR_64: palette entries, with components from 0 to 63.
  12 DELTA_FLI: changed pixels, as byte-oriented runs.
  13 BLACK: every pixel set to color 0.
  15 BYTE_RUN: the whole picture, run-length encoded.
  16 FLI_COPY: the whole picture, uncompressed.
  18 PSTAMP: a thumbnail of the picture, which we ignore.

FLC files may also start with a "prefix" block (in place of a frame) of settings
specific to Animator Pro, which we skip.

Copyright 2026+ Dmitry Brant
https://dmitrybrant.com

License: MIT
*/

namespace DmitryBrant.ImageFormats
{
    /// <summary>
    /// Handles reading Autodesk FLI and FLC animations.
    /// </summary>
    public static class FliReader
    {
        private const int HeaderSize = 128;
        private const int FrameHeaderSize = 16;
        private const int ChunkHeaderSize = 6;

        private const ushort MagicFli = 0xAF11;
        private const ushort MagicFlc = 0xAF12;
        private const ushort FrameMagic = 0xF1FA;
        private const ushort PrefixMagic = 0xF100;
        private const ushort SegmentTableMagic = 0xF1FB;

        private const int ChunkColor256 = 4;
        private const int ChunkDeltaFlc = 7;
        private const int ChunkColor64 = 11;
        private const int ChunkDeltaFli = 12;
        private const int ChunkBlack = 13;
        private const int ChunkByteRun = 15;
        private const int ChunkCopy = 16;

        /// <summary>
        /// Reads the first frame of a FLI or FLC animation from a file.
        /// </summary>
        /// <param name="fileName">Name of the file to read.</param>
        /// <returns>ImageData that contains the image that was read.</returns>
        public static ImageData Load(string fileName)
        {
            return Load(fileName, 0);
        }

        /// <summary>
        /// Reads the first frame of a FLI or FLC animation from a stream.
        /// </summary>
        /// <param name="stream">Stream from which to read the image.</param>
        /// <returns>ImageData that contains the image that was read.</returns>
        public static ImageData Load(Stream stream)
        {
            return Load(stream, 0);
        }

        /// <summary>
        /// Reads the given frame of a FLI or FLC animation from a file.
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
        /// Reads the given frame of a FLI or FLC animation from a stream. Every frame up to
        /// the requested one has to be decoded along the way, since each frame only stores
        /// what changed since the one before.
        /// </summary>
        /// <param name="stream">Stream from which to read the image.</param>
        /// <param name="frame">Zero-based index of the frame to read; see <see cref="GetFrameCount(Stream)"/>.</param>
        /// <returns>ImageData that contains the frame that was read.</returns>
        public static ImageData Load(Stream stream, int frame)
        {
            var anim = new Animation(stream);
            if (frame < 0 || frame >= anim.FrameCount)
                throw new ArgumentOutOfRangeException(nameof(frame), "Frame " + frame + " is out of range; the animation has " + anim.FrameCount + " frame(s).");
            for (int i = 0; i <= frame; i++)
                anim.DecodeFrame(i);
            return anim.Render();
        }

        /// <summary>
        /// Gets the number of frames in a FLI or FLC animation.
        /// </summary>
        /// <param name="fileName">Name of the file to read.</param>
        public static int GetFrameCount(string fileName)
        {
            using var f = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
            return GetFrameCount(f);
        }

        /// <summary>
        /// Gets the number of frames in a FLI or FLC animation.
        /// </summary>
        /// <param name="stream">Stream from which to read the image.</param>
        public static int GetFrameCount(Stream stream)
        {
            return new Animation(stream).FrameCount;
        }

        /// <summary>
        /// Reads all the frames of a FLI or FLC animation from a file. The file is read up
        /// front, but each frame is only decoded as the sequence is enumerated.
        /// </summary>
        /// <param name="fileName">Name of the file to read.</param>
        /// <returns>The frames of the animation, in order.</returns>
        public static IEnumerable<ImageData> LoadFrames(string fileName)
        {
            using var f = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
            return LoadFrames(f);
        }

        /// <summary>
        /// Reads all the frames of a FLI or FLC animation from a stream. The stream is read
        /// up front, but each frame is only decoded as the sequence is enumerated.
        /// </summary>
        /// <param name="stream">Stream from which to read the image.</param>
        /// <returns>The frames of the animation, in order.</returns>
        public static IEnumerable<ImageData> LoadFrames(Stream stream)
        {
            return EnumerateFrames(new Animation(stream));
        }

        private static IEnumerable<ImageData> EnumerateFrames(Animation anim)
        {
            for (int i = 0; i < anim.FrameCount; i++)
            {
                anim.DecodeFrame(i);
                yield return anim.Render();
            }
        }

        /// <summary>
        /// Checks whether the given header looks like the start of a FLI or FLC file.
        /// </summary>
        internal static bool HasSignature(byte[] header)
        {
            if (header.Length < HeaderSize + FrameHeaderSize)
                return false;
            int magic = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4));
            int firstBlock = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(HeaderSize + 4));
            return (magic == MagicFli || magic == MagicFlc)
                && (firstBlock == FrameMagic || firstBlock == PrefixMagic || firstBlock == SegmentTableMagic);
        }

        /// <summary>
        /// The contents of a FLIC file, along with the current state of playback: the
        /// pixels and palette as of the last frame that was decoded.
        /// </summary>
        private sealed class Animation
        {
            private readonly byte[] buffer;
            private readonly List<int> frameOffsets = new();
            private readonly int width;
            private readonly int height;
            private readonly byte[] pixels;
            private readonly byte[] palette = new byte[256 * 3];

            public int FrameCount { get; }

            public Animation(Stream stream)
            {
                using (var ms = new MemoryStream())
                {
                    stream.CopyTo(ms);
                    buffer = ms.ToArray();
                }
                if (buffer.Length < HeaderSize)
                    throw new ImageDecodeException("This is not a valid FLI or FLC file.");

                var header = buffer.AsSpan();
                int magic = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
                if (magic != MagicFli && magic != MagicFlc)
                    throw new ImageDecodeException("This is not a valid FLI or FLC file.");

                int declaredFrames = BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
                width = BinaryPrimitives.ReadUInt16LittleEndian(header[8..]);
                height = BinaryPrimitives.ReadUInt16LittleEndian(header[10..]);
                int depth = BinaryPrimitives.ReadUInt16LittleEndian(header[12..]);

                // Some early FLI files leave the depth as zero, and the format originally
                // assumed a 320x200 screen.
                if (depth != 0 && depth != 8)
                    throw new ImageDecodeException("Only 8-bit FLI and FLC animations are supported, not " + depth + "-bit.");
                if (width == 0 || height == 0)
                {
                    width = 320;
                    height = 200;
                }
                if ((long)width * height > CodecImage.MaxSamples)
                    throw new ImageDecodeException("FLIC image dimensions are too large.");
                pixels = new byte[width * height];

                // An FLC file gives the offset of its first frame, which may be preceded by
                // a prefix block. Older FLI files leave this field as zero.
                int pos = HeaderSize;
                long firstFrame = BinaryPrimitives.ReadUInt32LittleEndian(header[80..]);
                if (magic == MagicFlc && firstFrame > HeaderSize && firstFrame <= buffer.Length - FrameHeaderSize
                    && BinaryPrimitives.ReadUInt16LittleEndian(header[((int)firstFrame + 4)..]) == FrameMagic)
                    pos = (int)firstFrame;

                while (pos <= buffer.Length - FrameHeaderSize)
                {
                    long size = BinaryPrimitives.ReadUInt32LittleEndian(header[pos..]);
                    int type = BinaryPrimitives.ReadUInt16LittleEndian(header[(pos + 4)..]);
                    if (size < FrameHeaderSize)
                        break;
                    if (type == FrameMagic)
                        frameOffsets.Add(pos);
                    pos = (int)Math.Min(pos + size, buffer.Length);
                }

                FrameCount = declaredFrames > 0 && declaredFrames <= frameOffsets.Count ? declaredFrames : frameOffsets.Count;
                if (FrameCount == 0)
                    throw new ImageDecodeException("FLIC file does not contain any frames.");
            }

            /// <summary>
            /// Applies the given frame on top of the current state. Frames must be decoded
            /// in order, starting from the first.
            /// </summary>
            public void DecodeFrame(int index)
            {
                int pos = frameOffsets[index];
                int frameEnd = (int)Math.Min(pos + (long)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(pos)), buffer.Length);
                int chunkCount = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(pos + 6));
                pos += FrameHeaderSize;

                for (int i = 0; i < chunkCount && pos <= frameEnd - ChunkHeaderSize; i++)
                {
                    long size = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(pos));
                    int type = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(pos + 4));
                    int chunkEnd = size < ChunkHeaderSize || size > frameEnd - pos ? frameEnd : pos + (int)size;
                    var data = new ChunkReader(buffer.AsSpan(pos + ChunkHeaderSize, chunkEnd - pos - ChunkHeaderSize));

                    switch (type)
                    {
                        case ChunkColor256:
                            DecodeColors(ref data, false);
                            break;
                        case ChunkColor64:
                            DecodeColors(ref data, true);
                            break;
                        case ChunkDeltaFlc:
                            DecodeDeltaFlc(ref data);
                            break;
                        case ChunkDeltaFli:
                            DecodeDeltaFli(ref data);
                            break;
                        case ChunkBlack:
                            Array.Clear(pixels);
                            break;
                        case ChunkByteRun:
                            DecodeByteRun(ref data);
                            break;
                        case ChunkCopy:
                            data.CopyTo(pixels);
                            break;
                    }
                    pos = chunkEnd;
                }
            }

            public ImageData Render()
            {
                var bmpData = new byte[pixels.Length * 4];
                for (int i = 0, p = 0; i < pixels.Length; i++, p += 4)
                {
                    int c = pixels[i] * 3;
                    bmpData[p] = palette[c + 2];
                    bmpData[p + 1] = palette[c + 1];
                    bmpData[p + 2] = palette[c];
                    bmpData[p + 3] = 0xFF;
                }
                return new ImageData(width, height, bmpData);
            }

            private void DecodeColors(ref ChunkReader data, bool sixBit)
            {
                int packets = data.Word();
                int index = 0;
                for (int p = 0; p < packets && !data.AtEnd; p++)
                {
                    index += data.Byte();
                    int count = data.Byte();
                    if (count == 0)
                        count = 256;
                    for (int i = 0; i < count; i++, index++)
                    {
                        for (int c = 0; c < 3; c++)
                        {
                            int v = data.Byte();
                            if (sixBit)
                                v = ((v & 0x3F) << 2) | ((v & 0x3F) >> 4);
                            if (index < 256)
                                palette[index * 3 + c] = (byte)v;
                        }
                    }
                }
            }

            /// <summary>
            /// Decodes the whole picture, compressed line by line with byte runs.
            /// </summary>
            private void DecodeByteRun(ref ChunkReader data)
            {
                for (int y = 0; y < height && !data.AtEnd; y++)
                {
                    // Each line starts with a count of packets, which overflows for wide
                    // images, so we go by the width instead.
                    data.Byte();
                    int row = y * width;
                    int x = 0;
                    while (x < width && !data.AtEnd)
                    {
                        int count = data.SByte();
                        if (count > 0)
                        {
                            byte v = data.Byte();
                            for (int i = 0; i < count; i++, x++)
                                Put(row, x, v);
                        }
                        else
                        {
                            for (int i = 0; i < -count; i++, x++)
                                Put(row, x, data.Byte());
                        }
                    }
                }
            }

            /// <summary>
            /// Decodes the changes from the previous frame, as a range of lines, each with
            /// packets of skipped pixels followed by byte runs.
            /// </summary>
            private void DecodeDeltaFli(ref ChunkReader data)
            {
                int y = data.Word();
                int lineCount = data.Word();
                for (int line = 0; line < lineCount && y < height && !data.AtEnd; line++, y++)
                {
                    int packets = data.Byte();
                    int row = y * width;
                    int x = 0;
                    for (int p = 0; p < packets; p++)
                    {
                        x += data.Byte();
                        int count = data.SByte();
                        if (count > 0)
                        {
                            for (int i = 0; i < count; i++, x++)
                                Put(row, x, data.Byte());
                        }
                        else
                        {
                            byte v = data.Byte();
                            for (int i = 0; i < -count; i++, x++)
                                Put(row, x, v);
                        }
                    }
                }
            }

            /// <summary>
            /// Decodes the changes from the previous frame, as a list of lines, each with
            /// packets of skipped pixels followed by runs of two-pixel words.
            /// </summary>
            private void DecodeDeltaFlc(ref ChunkReader data)
            {
                int lineCount = data.Word();
                int y = 0;
                while (lineCount > 0 && y < height && !data.AtEnd)
                {
                    // Each line starts with any number of opcodes, the last of which is a
                    // count of packets. The top two bits of an opcode give its kind.
                    int op = (short)data.Word();
                    int row = y * width;
                    switch (op & 0xC000)
                    {
                        case 0xC000:
                            // Number of lines to skip, negated.
                            y -= op;
                            continue;
                        case 0x8000:
                            // Value of the last pixel of the line, for odd widths.
                            Put(row, width - 1, (byte)op);
                            continue;
                        case 0x4000:
                            return;
                    }

                    int x = 0;
                    for (int p = 0; p < op; p++)
                    {
                        x += data.Byte();
                        int count = data.SByte();
                        if (count > 0)
                        {
                            for (int i = 0; i < count * 2; i++, x++)
                                Put(row, x, data.Byte());
                        }
                        else
                        {
                            byte v1 = data.Byte();
                            byte v2 = data.Byte();
                            for (int i = 0; i < -count; i++, x += 2)
                            {
                                Put(row, x, v1);
                                Put(row, x + 1, v2);
                            }
                        }
                    }
                    y++;
                    lineCount--;
                }
            }

            private void Put(int row, int x, byte value)
            {
                if ((uint)x < (uint)width)
                    pixels[row + x] = value;
            }
        }

        /// <summary>
        /// Reads the data of a chunk. Reading past the end of the chunk yields zeros, so
        /// that a truncated or damaged chunk only spoils its own part of the picture.
        /// </summary>
        private ref struct ChunkReader
        {
            private readonly ReadOnlySpan<byte> data;
            private int pos;

            public ChunkReader(ReadOnlySpan<byte> data)
            {
                this.data = data;
                pos = 0;
            }

            public readonly bool AtEnd => pos >= data.Length;

            public byte Byte()
            {
                return pos < data.Length ? data[pos++] : (byte)0;
            }

            public int SByte()
            {
                return (sbyte)Byte();
            }

            public int Word()
            {
                int lo = Byte();
                return lo | (Byte() << 8);
            }

            public void CopyTo(byte[] dest)
            {
                var src = data[pos..];
                if (src.Length > dest.Length)
                    src = src[..dest.Length];
                src.CopyTo(dest);
                pos += src.Length;
            }
        }
    }
}
