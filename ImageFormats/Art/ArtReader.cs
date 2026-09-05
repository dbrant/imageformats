#nullable enable
using System;
using SixLabors.ImageSharp;
using System.IO;

/*

Reader for AOL ART (Johnson-Grace) images.

The ART format was created by the Johnson-Grace Company (acquired by AOL in
1996), and was used throughout AOL's client software to push artwork over
dial-up connections. Files begin with the ASCII characters "JG".

There is no published specification for this format. Everything below was
derived by analyzing the original Johnson-Grace decoders (JGDW400.DLL and its
version-3 predecessor) together with a corpus of sample files.

An ART file consists of an 8-byte file header followed by a stream of tagged
chunks. Each chunk is introduced by a 1-, 2-, or 3-byte header that encodes the
chunk's tag and payload length (see ReadChunkHeader below). The stream is
terminated by a chunk with tag TagEndOfStream.

The byte at offset 2 of the file header is the major version, and it selects the
codec generation rather than a revision of one codec: major version 4 (and the
0, 1 and 2 that the original decoder also accepts) means one of the two image
kinds below, while major version 3 is the earlier, JPEG-like codec that
ArtV3Decoder handles. The two share only the container.

Two kinds of image are carried in a version-4 chunk stream:

  * A "photo" image: header chunk 0x40 followed by data chunks 0x41. This is a
    progressively coded, lossy image: a five-level CDF 9/7 wavelet transform
    (the same one JPEG 2000 calls "9/7 irreversible") over either 3-component
    YCbCr or 1-component grayscale. Level 0 carries the LL band as a DPCM
    residual; each later level adds three quantized subbands whose coefficients
    are run-length coded and then packed with Johnson-Grace's LZ77 + Huffman
    entropy coder.

  * A "graphic" image: header chunk 0x16, auxiliary chunks 0x17 (palette), 0x18,
    0x1B and 0x1C, followed by data chunks 0x19. This is a palettized image
    intended for artwork and clip art. It uses the same entropy coder, but with
    match offsets drawn from a table of two-dimensional pixel neighborhoods, and
    with optional PNG-style per-row prediction filters. Chunk 0x1B may declare a
    transparent color.

A version-3 file carries neither of those. Its own image header is chunk 0x02,
followed by up to three progressive passes of a YCbCr DCT codec; see
ArtV3Decoder.

This class parses the container and all three image headers, which gives us the
exact length of the file and the exact dimensions of the image. The chunk stream
itself is walked by ArtChunkReader, the version-4 entropy codec lives in
ArtJgLossless, and the pixel codecs live in ArtPhotoDecoder, ArtGraphicDecoder
and ArtV3Decoder.

Copyright 2026 Dmitry Brant
http://dmitrybrant.com

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

   http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.

*/

namespace DmitryBrant.ImageFormats
{
    /// <summary>
    /// Handles reading AOL ART (Johnson-Grace) images.
    /// </summary>
    public static class ArtReader
    {
        // The two image headers, and the chunk that terminates the stream. The original
        // decoder recognizes tags 0x02 through 0x62; every other tag is skipped by its
        // length, like any chunk we don't need.
        private const int TagEndOfStream = ArtChunkReader.TagEndOfStream;
        private const int TagGraphicHeader = ArtChunkReader.TagGraphicHeader;
        private const int TagPhotoHeader = ArtChunkReader.TagPhotoHeader;
        private const int TagV3Header = ArtV3Decoder.TagHeader;

        // Purely sanity limits, so that a walk over garbage sectors terminates.
        private const int MaxArtSize = 32 * 1024 * 1024;
        private const int MaxChunks = 0x10000;
        private const int MaxDimension = 32767;

        /// <summary>
        /// Everything that can be determined about an ART image without decoding its
        /// compressed pixel data.
        /// </summary>
        public class ArtInfo
        {
            /// <summary>Width of the image in pixels, or -1 if no image header was found.</summary>
            public int Width = -1;
            /// <summary>Height of the image in pixels, or -1 if no image header was found.</summary>
            public int Height = -1;
            /// <summary>Number of color components: 3 for color, 1 for grayscale.</summary>
            public int Components;
            /// <summary>Bits per pixel of the image that the original decoder would produce.</summary>
            public int BitsPerPixel;
            /// <summary>True if this is a wavelet-coded "photo" image (chunk 0x40).</summary>
            public bool IsPhoto;
            /// <summary>True if this is a palettized "graphic" image (chunk 0x16).</summary>
            public bool IsGraphic;
            /// <summary>True if this is a major-version-3 image, i.e. the older DCT codec.</summary>
            public bool IsV3;
            /// <summary>Total length of the file, in bytes.</summary>
            public long FileSize;
            /// <summary>True if the terminating chunk was reached, i.e. the file is complete.</summary>
            public bool Complete;

            public bool HasImage => Width > 0 && Height > 0;
        }

        /// <summary>
        /// Reads an ART image from a file.
        /// </summary>
        /// <param name="fileName">Name of the file to read.</param>
        /// <returns>Bitmap that contains the image that was read.</returns>
        public static Image Load(string fileName)
        {
            using var f = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Load(f);
        }

        /// <summary>
        /// Reads an ART image from a stream.
        /// </summary>
        /// <param name="stream">Stream from which to read the image.</param>
        /// <returns>Bitmap that contains the image that was read.</returns>
        public static Image Load(Stream stream)
        {
            long start = stream.CanSeek ? stream.Position : 0;
            var info = ReadInfo(stream);
            if (!info.HasImage)
                throw new ImageDecodeException("This ART file does not contain an image.");

            // ReadInfo has told us exactly how long the file is, so we can now read the
            // whole thing, which is what both pixel codecs want.
            if (stream.CanSeek)
                stream.Seek(start, SeekOrigin.Begin);
            var data = new byte[info.FileSize];
            int total = 0;
            while (total < data.Length)
            {
                int read = stream.Read(data, total, data.Length - total);
                if (read <= 0)
                    break;
                total += read;
            }
            if (total < data.Length)
                Array.Resize(ref data, total);

            var image = info.IsV3 ? ArtV3Decoder.Decode(data)
                : info.IsPhoto ? ArtPhotoDecoder.Decode(data) : ArtGraphicDecoder.Decode(data);
            return image.HasAlpha ? Util.LoadRgba(image.Width, image.Height, image.Bgra)
                                  : Util.LoadRgb(image.Width, image.Height, image.Bgra);
        }

        /// <summary>
        /// Reads the header of an ART image, and walks the chunk stream to determine the
        /// exact length of the file.
        /// </summary>
        /// <param name="stream">Stream from which to read, positioned at the start of the file.</param>
        /// <returns>Information about the image.</returns>
        public static ArtInfo ReadInfo(Stream stream)
        {
            var reader = new BinaryReader(stream);

            var fileHeader = reader.ReadBytes(8);
            if (fileHeader.Length < 8 || fileHeader[0] != 'J' || fileHeader[1] != 'G')
                throw new ImageDecodeException("This is not a valid ART file.");
            if (!IsSupportedVersion(fileHeader[2], fileHeader[3]))
                throw new ImageDecodeException("Unsupported ART version " + fileHeader[2] + "." + fileHeader[3] + ".");

            var info = new ArtInfo();
            // Major version 3 is a different codec generation that reuses the container, so
            // the tag of its image header only means what it means in a version-3 file.
            int imageHeaderTag = fileHeader[2] == 3 ? TagV3Header : -1;
            long pos = 8;

            try
            {
                for (int i = 0; i < MaxChunks; i++)
                {
                    pos += ReadChunkHeader(reader, out int tag, out int payloadLength);

                    if (tag == TagEndOfStream)
                    {
                        info.Complete = true;
                        break;
                    }
                    // Tag 0 is not a valid chunk. Real files never contain one, but a run of
                    // zeros in unallocated space would otherwise walk forever.
                    if (tag == 0)
                        break;

                    if (payloadLength > 0)
                    {
                        // Only the image header needs to be looked at; every other chunk is
                        // skipped by its length.
                        if (IsImageHeaderTag(tag, imageHeaderTag) && !info.HasImage)
                        {
                            var payload = reader.ReadBytes(payloadLength);
                            if (payload.Length < payloadLength)
                                break;
                            ParseImageHeader(tag, payload, info);
                        }
                        else
                        {
                            Skip(reader, payloadLength);
                        }
                        pos += payloadLength;
                    }

                    if (pos > MaxArtSize)
                        break;
                }
            }
            catch (EndOfStreamException)
            {
                // Truncated file: return what we have, with Complete left false.
            }

            info.FileSize = pos;
            return info;
        }

        /// <summary>
        /// Checks whether the given bytes look like the beginning of an ART file. In addition
        /// to the "JG" signature this walks the chunk stream far enough to find an image header
        /// with sensible dimensions, which makes it safe to use against raw sector data.
        /// </summary>
        /// <param name="bytes">Buffer that begins with the candidate file.</param>
        /// <returns>True if this looks like a valid ART image.</returns>
        public static bool IsValidHeader(byte[] bytes)
        {
            // Stay well inside the smallest buffer that callers hand us (one sector).
            int length = Math.Min(bytes.Length, 512);
            if (length < 16 || bytes[0] != 'J' || bytes[1] != 'G')
                return false;
            if (!IsSupportedVersion(bytes[2], bytes[3]))
                return false;

            var info = new ArtInfo();
            int imageHeaderTag = bytes[2] == 3 ? TagV3Header : -1;
            int pos = 8;

            while (pos < length)
            {
                int headerLength = ArtChunkReader.PeekChunkHeader(bytes, pos, length, out int tag, out int payloadLength);
                if (headerLength == 0)
                    return false;
                pos += headerLength;

                if (tag == TagEndOfStream || tag == 0)
                    return false;

                if (IsImageHeaderTag(tag, imageHeaderTag))
                {
                    if (pos + payloadLength > length)
                        return false;
                    var payload = new byte[payloadLength];
                    Array.Copy(bytes, pos, payload, 0, payloadLength);
                    ParseImageHeader(tag, payload, info);
                    return info.HasImage;
                }

                pos += payloadLength;
            }
            return false;
        }

        /// <summary>
        /// The original version-4 decoder accepts major versions 0, 1, 2 and 4, and rejects 3
        /// outright, because major version 3 is a different, earlier codec rather than an
        /// earlier revision of the same one. We handle that one too, in ArtV3Decoder, so it
        /// is accepted here as well. Either way a minor version of at least 12 is required.
        /// Every file seen in the wild is 3.14 or 4.14.
        /// </summary>
        private static bool IsSupportedVersion(byte major, byte minor)
        {
            return major <= 4 && minor >= 0x0C;
        }

        /// <summary>
        /// Checks whether the given tag introduces the image header of the file being read.
        /// Tag 0x02 is an image header in a version-3 file and an ignorable chunk in a
        /// version-4 one, so which tag counts depends on the file major version.
        /// </summary>
        /// <param name="imageHeaderTag">The version-3 header tag, or -1 for a version-4 file.</param>
        private static bool IsImageHeaderTag(int tag, int imageHeaderTag)
        {
            return imageHeaderTag >= 0 ? tag == imageHeaderTag
                                       : tag == TagPhotoHeader || tag == TagGraphicHeader;
        }

        /// <summary>
        /// Parses whichever of the three image headers the given tag introduces.
        /// </summary>
        private static void ParseImageHeader(int tag, byte[] payload, ArtInfo info)
        {
            if (tag == TagV3Header)
                ParseV3Header(payload, info);
            else if (tag == TagPhotoHeader)
                ParsePhotoHeader(payload, info);
            else
                ParseGraphicHeader(payload, info);
        }

        /// <summary>
        /// Reads the header of the next chunk, and returns its length in bytes. Same as
        /// ArtChunkReader.PeekChunkHeader, but reads out of a stream instead of a buffer,
        /// so that the length of a file can be measured without reading all of it.
        /// </summary>
        /// <remarks>
        /// Chunk headers are variable length, and the tag is not always in the same place;
        /// see ArtChunkReader.PeekChunkHeader for the layout.
        /// </remarks>
        private static int ReadChunkHeader(BinaryReader reader, out int tag, out int payloadLength)
        {
            int b0 = reader.ReadByte();
            if ((b0 & 0x80) != 0)
            {
                if ((b0 & 0x40) != 0)
                {
                    tag = b0 & 0x3F;
                    payloadLength = 0;
                    return 1;
                }
                int b1 = reader.ReadByte();
                tag = b1 & 0x1F;
                payloadLength = (b0 & 0x3F) | ((b1 >> 5) << 6);
                return 2;
            }

            int lengthHigh = reader.ReadByte();
            int combined = (lengthHigh << 7) + b0;
            if (combined == 0)
            {
                tag = 0;
                payloadLength = 0;
                return 2;
            }
            tag = reader.ReadByte();
            payloadLength = combined - 1;
            return 3;
        }

        /// <summary>
        /// Parses the header of a wavelet-coded "photo" image (chunk 0x40).
        /// </summary>
        /// <remarks>
        /// payload[0]   : codec identifier (0x15 in every file seen so far).
        /// payload[1]   : bit 0 set means 3 color components, clear means 1 (grayscale).
        ///                bit 1 selects between two component-sampling configurations.
        /// payload[2..3]: width, little-endian.
        /// payload[4..5]: height, little-endian.
        ///
        /// The wavelet pyramid always has five levels, so the smallest band is the
        /// dimensions halved (rounding up) four times. Components are never subsampled;
        /// chroma simply stops refining at a lower level than luma.
        /// </remarks>
        private static void ParsePhotoHeader(byte[] payload, ArtInfo info)
        {
            if (!ArtPhotoDecoder.ParseHeader(payload, out int components, out int width, out int height))
                return;
            if (width > MaxDimension || height > MaxDimension)
                return;

            info.IsPhoto = true;
            info.Width = width;
            info.Height = height;
            info.Components = components;
            info.BitsPerPixel = 24;
        }

        /// <summary>
        /// Parses the header of a palettized "graphic" image (chunk 0x16).
        /// </summary>
        /// <remarks>
        /// Note that the dimensions are stored the other way around from a photo header.
        ///
        /// payload[0..1] : flags; bits 8..11 hold a "kind" that selects how the fields
        ///                 at payload[6..7] and payload[10..11] are interpreted.
        /// payload[2..3] : height, little-endian.
        /// payload[4..5] : width, little-endian.
        /// payload[6..]  : entropy-coder parameters (minimum match length, window size,
        ///                 position-slot budget) and the pixel format. See ArtGraphicDecoder.
        /// </remarks>
        private static void ParseGraphicHeader(byte[] payload, ArtInfo info)
        {
            if (payload.Length < 12)
                return;

            int height = payload[2] | (payload[3] << 8);
            int width = payload[4] | (payload[5] << 8);
            if (width < 1 || height < 1 || width > MaxDimension || height > MaxDimension)
                return;

            info.IsGraphic = true;
            info.Width = width;
            info.Height = height;
            info.Components = 1;
            info.BitsPerPixel = 8;
        }

        /// <summary>
        /// Parses the header of a major-version-3 image (chunk 0x02). That image is YCbCr
        /// with a DCT, so the original decoder produces the same 24-bit output for it as it
        /// does for a photo image. See ArtV3Decoder for the layout of the header itself.
        /// </summary>
        private static void ParseV3Header(byte[] payload, ArtInfo info)
        {
            if (!ArtV3Decoder.ParseHeader(payload, out int width, out int height))
                return;

            info.IsV3 = true;
            info.Width = width;
            info.Height = height;
            info.Components = 3;
            info.BitsPerPixel = 24;
        }

        private static void Skip(BinaryReader reader, int count)
        {
            var stream = reader.BaseStream;
            if (stream.CanSeek)
            {
                // A stream over raw disk contents clamps to its end instead of failing, so
                // check that the seek actually went as far as we asked.
                long before = stream.Position;
                stream.Seek(count, SeekOrigin.Current);
                if (stream.Position - before < count)
                    throw new EndOfStreamException();
            }
            else if (reader.ReadBytes(count).Length < count)
            {
                throw new EndOfStreamException();
            }
        }
    }
}
