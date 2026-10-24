#nullable enable
using System;
using System.Buffers.Binary;

/*

Decoder for Windows device-independent bitmaps (DIBs): the pixel format of .BMP
files, minus the 14-byte file header, which is also how Windows puts bitmaps on the
clipboard (CF_DIB) and how many applications store thumbnails.

A DIB starts with a header (40 bytes for BITMAPINFOHEADER, more for its V4 and V5
extensions, or 12 for the old OS/2 BITMAPCOREHEADER), followed by the color masks
(for BI_BITFIELDS), then the palette (for 8 bits per pixel or fewer), and then the
pixel rows, each padded to a multiple of four bytes. Rows are stored bottom-up,
unless the height is negative.

Uncompressed and bit-field images of 1, 2, 4, 8, 16, 24 and 32 bits per pixel are
supported; RLE-compressed ones are not. The alpha channel of 32-bit images is ignored,
since it is rarely meaningful in a DIB.

Copyright 2026+ Dmitry Brant
https://dmitrybrant.com

License: MIT
*/

namespace DmitryBrant.ImageFormats
{
    internal static class DibDecoder
    {
        private const int BiRgb = 0;
        private const int BiBitfields = 3;
        private const int BiAlphaBitfields = 6;

        public static ImageData Decode(ReadOnlySpan<byte> dib)
        {
            if (dib.Length < 12)
                throw new ImageDecodeException("DIB is too short.");

            int headerSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(dib);
            int width, height, bitCount, compression = BiRgb, colorsUsed = 0;
            int paletteEntrySize = 4;
            if (headerSize == 12)
            {
                width = BinaryPrimitives.ReadUInt16LittleEndian(dib[4..]);
                height = BinaryPrimitives.ReadInt16LittleEndian(dib[6..]);
                bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib[10..]);
                paletteEntrySize = 3;
            }
            else if (headerSize >= 40 && headerSize <= dib.Length)
            {
                width = BinaryPrimitives.ReadInt32LittleEndian(dib[4..]);
                height = BinaryPrimitives.ReadInt32LittleEndian(dib[8..]);
                bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]);
                compression = BinaryPrimitives.ReadInt32LittleEndian(dib[16..]);
                colorsUsed = BinaryPrimitives.ReadInt32LittleEndian(dib[32..]);
            }
            else
            {
                throw new ImageDecodeException("Unsupported DIB header size: " + headerSize);
            }

            bool topDown = height < 0;
            height = Math.Abs(height);
            if (width <= 0 || height <= 0 || (long)width * height * 4 > int.MaxValue)
                throw new ImageDecodeException("DIB has invalid dimensions.");
            if (compression != BiRgb && compression != BiBitfields && compression != BiAlphaBitfields)
                throw new ImageDecodeException("Unsupported DIB compression: " + compression);
            if (bitCount != 1 && bitCount != 2 && bitCount != 4 && bitCount != 8 && bitCount != 16 && bitCount != 24 && bitCount != 32)
                throw new ImageDecodeException("Unsupported DIB bit depth: " + bitCount);

            int pos = headerSize;

            // Masks of the red, green and blue bits of 16- and 32-bit pixels. With
            // BI_BITFIELDS, they follow a 40-byte header, or are part of a longer one.
            uint[] masks = bitCount == 16 ? new uint[] { 0x7C00, 0x03E0, 0x001F } : new uint[] { 0xFF0000, 0xFF00, 0xFF };
            if (compression != BiRgb && (bitCount == 16 || bitCount == 32))
            {
                int maskPos = headerSize == 40 ? pos : 40;
                if (maskPos + 12 > dib.Length)
                    throw new ImageDecodeException("DIB is missing its color masks.");
                for (int i = 0; i < 3; i++)
                    masks[i] = BinaryPrimitives.ReadUInt32LittleEndian(dib[(maskPos + i * 4)..]);
                if (headerSize == 40)
                    pos += compression == BiAlphaBitfields ? 16 : 12;
            }

            var palette = new byte[256 * 3];
            if (bitCount <= 8)
            {
                int count = colorsUsed > 0 && colorsUsed <= (1 << bitCount) ? colorsUsed : 1 << bitCount;
                if (pos + count * paletteEntrySize > dib.Length)
                    throw new ImageDecodeException("DIB palette is truncated.");
                for (int i = 0; i < count; i++)
                {
                    // Stored as blue, green, red (and a reserved byte).
                    palette[i * 3] = dib[pos + i * paletteEntrySize + 2];
                    palette[i * 3 + 1] = dib[pos + i * paletteEntrySize + 1];
                    palette[i * 3 + 2] = dib[pos + i * paletteEntrySize];
                }
                pos += count * paletteEntrySize;
            }

            int stride = (int)(((long)width * bitCount + 31) / 32 * 4);
            if (pos + (long)stride * height > dib.Length)
                throw new ImageDecodeException("DIB pixel data is truncated.");

            var shifts = new int[3];
            var scales = new int[3];
            for (int i = 0; i < 3; i++)
            {
                shifts[i] = masks[i] == 0 ? 0 : System.Numerics.BitOperations.TrailingZeroCount(masks[i]);
                scales[i] = (int)(masks[i] >> shifts[i]);
            }

            var bmp = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                var row = dib.Slice(pos + (topDown ? y : height - 1 - y) * stride, stride);
                int d = y * width * 4;
                for (int x = 0; x < width; x++, d += 4)
                {
                    switch (bitCount)
                    {
                        case 24:
                            bmp[d] = row[x * 3];
                            bmp[d + 1] = row[x * 3 + 1];
                            bmp[d + 2] = row[x * 3 + 2];
                            break;
                        case 16:
                        case 32:
                        {
                            uint v = bitCount == 16 ? BinaryPrimitives.ReadUInt16LittleEndian(row[(x * 2)..]) : BinaryPrimitives.ReadUInt32LittleEndian(row[(x * 4)..]);
                            for (int c = 0; c < 3; c++)
                                bmp[d + 2 - c] = scales[c] == 0 ? (byte)0 : (byte)(((v & masks[c]) >> shifts[c]) * 255 / scales[c]);
                            break;
                        }
                        default:
                        {
                            int bit = x * bitCount;
                            int index = (row[bit >> 3] >> (8 - bitCount - (bit & 7))) & ((1 << bitCount) - 1);
                            bmp[d] = palette[index * 3 + 2];
                            bmp[d + 1] = palette[index * 3 + 1];
                            bmp[d + 2] = palette[index * 3];
                            break;
                        }
                    }
                    bmp[d + 3] = 0xFF;
                }
            }
            return new ImageData(width, height, bmp);
        }
    }
}
