#nullable enable
using System;
using System.Collections.Generic;

/*

Chunk stream walker for AOL ART (Johnson-Grace) images, shared by the
container parser (ArtReader) and the three pixel decoders (ArtPhotoDecoder,
ArtGraphicDecoder and ArtV3Decoder). Major versions 3 and 4 disagree about
everything below the chunk stream, but they frame it identically.

Copyright 2026+ Dmitry Brant
https://dmitrybrant.com

License: MIT
*/

namespace DmitryBrant.ImageFormats
{
    /// <summary>
    /// One chunk of an ART file's chunk stream.
    /// </summary>
    internal class ArtChunk
    {
        public int Tag;
        public byte[] Payload = Array.Empty<byte>();
    }

    /// <summary>
    /// A decoded image, as a top-down BGRA buffer that can be handed straight to
    /// Util.LoadRgba or Util.LoadRgb.
    /// </summary>
    internal class ArtImage
    {
        public int Width;
        public int Height;
        public byte[] Bgra = Array.Empty<byte>();
        /// <summary>True if the image declared a transparent color, i.e. the alpha channel matters.</summary>
        public bool HasAlpha;
    }

    internal static class ArtChunkReader
    {
        // Version-4 chunk tags that anyone downstream cares about. The original decoder
        // recognizes tags 0x02 through 0x62; every other tag is skipped by its length. Only
        // TagEndOfStream means the same thing in a version-3 file; that vocabulary lives in
        // ArtV3Decoder.
        public const int TagEndOfStream = 0x0B;
        public const int TagGraphicHeader = 0x16;
        public const int TagPalette = 0x17;
        public const int TagGraphicTables = 0x18;
        public const int TagGraphicData = 0x19;
        public const int TagTransparency = 0x1B;
        public const int TagPhotoHeader = 0x40;
        public const int TagPhotoData = 0x41;

        // Aliases used by a second image plane.
        public const int TagGraphicHeader2 = 0x5D;
        public const int TagPalette2 = 0x5E;
        public const int TagGraphicTables2 = 0x5F;
        public const int TagGraphicData2 = 0x60;
        public const int TagTransparency2 = 0x61;

        /// <summary>
        /// Reads the header of the chunk at the given position, and returns its length in
        /// bytes, or 0 if the header doesn't fit inside the buffer.
        /// </summary>
        /// <remarks>
        /// Chunk headers are variable length, and the tag is not always in the same place:
        ///
        ///   1 byte,  b0 = 11tttttt                : tag = b0 &amp; 0x3F, no payload.
        ///   2 bytes, b0 = 10llllll, b1 = LLLttttt : tag = b1 &amp; 0x1F,
        ///                                           length = (b0 &amp; 0x3F) | ((b1 >> 5) &lt;&lt; 6).
        ///   3 bytes, b0 = 0lllllll, b1, b2        : length = ((b1 &lt;&lt; 7) | b0) - 1, tag = b2.
        ///                                           A combined value of zero means an empty
        ///                                           2-byte chunk with tag 0.
        /// </remarks>
        public static int PeekChunkHeader(byte[] bytes, int pos, int length, out int tag, out int payloadLength)
        {
            tag = 0;
            payloadLength = 0;
            if (pos < 0 || pos >= length)
                return 0;

            int b0 = bytes[pos];
            if ((b0 & 0x80) != 0)
            {
                if ((b0 & 0x40) != 0)
                {
                    tag = b0 & 0x3F;
                    return 1;
                }
                if (pos + 1 >= length)
                    return 0;
                int b1 = bytes[pos + 1];
                tag = b1 & 0x1F;
                payloadLength = (b0 & 0x3F) | ((b1 >> 5) << 6);
                return 2;
            }

            if (pos + 1 >= length)
                return 0;
            int combined = (bytes[pos + 1] << 7) + b0;
            if (combined == 0)
                return 2;
            if (pos + 2 >= length)
                return 0;
            tag = bytes[pos + 2];
            payloadLength = combined - 1;
            return 3;
        }

        /// <summary>
        /// Walks the chunk stream of a complete ART file that has been read into memory.
        /// A truncated file simply yields a short final payload and then stops.
        /// </summary>
        /// <param name="data">Contents of the file, starting with the 8-byte file header.</param>
        public static IEnumerable<ArtChunk> Walk(byte[] data)
        {
            int pos = 8;
            while (true)
            {
                int headerLength = PeekChunkHeader(data, pos, data.Length, out int tag, out int payloadLength);
                if (headerLength == 0)
                    yield break;
                pos += headerLength;

                int available = Math.Min(payloadLength, data.Length - pos);
                var payload = new byte[available > 0 ? available : 0];
                if (available > 0)
                    Array.Copy(data, pos, payload, 0, available);

                yield return new ArtChunk { Tag = tag, Payload = payload };

                if (tag == TagEndOfStream)
                    yield break;
                pos += payloadLength;
            }
        }

        /// <summary>
        /// Reads a Johnson-Grace varint: one byte, unless its high bit is set, in which case
        /// a second byte supplies bits 7..14.
        /// </summary>
        public static int ReadVarint(byte[] buf, ref int pos)
        {
            if (pos >= buf.Length)
                return 0;
            int b = buf[pos];
            if ((b & 0x80) != 0)
            {
                int hi = pos + 1 < buf.Length ? buf[pos + 1] : 0;
                pos += 2;
                return (b & 0x7F) | (hi << 7);
            }
            pos++;
            return b;
        }
    }
}
