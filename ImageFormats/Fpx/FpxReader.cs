#nullable enable
using System;
using System.Buffers.Binary;
using System.IO;

/*

Decoder for FlashPix (.FPX) images.

FlashPix was devised by Kodak, together with Hewlett-Packard, Live Picture and
Microsoft, as a format for images that can be viewed quickly at any size. A
FlashPix file is an OLE compound file (see CompoundFile.cs), laid out like this:

  \005Global Info                  (property set) which image object is the one
                                   to show (the "visible output")
  Data Object Store 000001/        (storage) an image object: the source image
    \005Image Contents             (property set) the number of resolutions, and
                                   the size, channels and colors of each; also
                                   any JPEG tables shared by the tiles
    Resolution 0000/               (storage) the smallest resolution
      Subimage 0000 Header         (stream) the size of the image, and a table
                                   giving the location and compression of each tile
      Subimage 0000 Data           (stream) the tiles themselves
    Resolution 0001/ ...           each twice the size of the one before
  Data Object Store 000002/        (storage) optionally, the result of applying a
                                   "viewing transform" (crop, rotation, color
                                   adjustments) to the source image

The header and data streams start with a 28-byte header of their own, which isn't
counted in the offsets within them.

We read the largest resolution of the visible image object, falling back to the
source image if the result of the viewing transform isn't stored. We don't apply
viewing transforms ourselves.

Each resolution is split into tiles (64x64 pixels), stored in rows from the top
left, and padded at the right and bottom edges. A tile may be uncompressed, a
single color, or JPEG. A JPEG tile may omit its quantization and Huffman tables,
and refer instead to a set of tables stored in the image contents property set.

An image has from one to four channels, in one of these color spaces:

  * NIF RGB: red, green and blue, as on a typical monitor (i.e. close to sRGB).
  * PhotoYCC: Kodak's luma and chroma encoding from Photo CD.
  * Monochrome.

Any of these may be followed by an opacity channel, in which case the color
channels have been premultiplied by it. An image may also consist of opacity alone.

JPEG tiles of NIF RGB images are usually converted to YCbCr before compression,
which the tile header tells us to undo. If there is also an opacity channel, the
color channels were also inverted before being converted.

Copyright 2026+ Dmitry Brant
https://dmitrybrant.com

License: MIT
*/

namespace DmitryBrant.ImageFormats
{
    /// <summary>
    /// Handles reading FlashPix (.FPX) images.
    /// </summary>
    public static class FpxReader
    {
        private const string ImageContentsName = "\u0005Image Contents";
        private const string GlobalInfoName = "\u0005Global Info";
        private const string ImageObjectPrefix = "Data Object Store ";
        private const string ResolutionPrefix = "Resolution ";
        private const string SubimageHeaderName = "Subimage 0000 Header";
        private const string SubimageDataName = "Subimage 0000 Data";

        // Size of the header at the start of FlashPix streams.
        private const int StreamHeaderSize = 28;

        private const uint PidVisibleOutputs = 0x00010100;
        private const uint PidSubimageColor = 0x02000002;
        private const uint PidNumericalFormat = 0x02000003;
        private const uint PidJpegTables = 0x03000001;

        private const int VtUi1 = 17;

        private const uint CompressionNone = 0;
        private const uint CompressionSingleColor = 1;
        private const uint CompressionJpeg = 2;
        private const uint CompressionInvalid = 0xFFFFFFFF;

        private const int SpaceColorless = 0;
        private const int SpaceMonochrome = 1;
        private const int SpacePhotoYcc = 2;
        private const int SpaceNifRgb = 3;
        private const int ColorOpacity = 0x7FFE;

        /// <summary>
        /// Reads a FlashPix image from a file.
        /// </summary>
        /// <param name="fileName">Name of the file to read.</param>
        /// <returns>ImageData that contains the image that was read.</returns>
        public static ImageData Load(string fileName)
        {
            using var f = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Load(f);
        }

        /// <summary>
        /// Reads a FlashPix image from a stream.
        /// </summary>
        /// <param name="stream">Stream from which to read the image.</param>
        /// <returns>ImageData that contains the image that was read.</returns>
        public static ImageData Load(Stream stream)
        {
            return LoadIfFpx(stream) ?? throw new ImageDecodeException("This does not appear to be a valid FlashPix file.");
        }

        /// <summary>
        /// Reads a FlashPix image from a stream, or returns null if the stream is a compound
        /// file that isn't a FlashPix image.
        /// </summary>
        internal static ImageData? LoadIfFpx(Stream stream)
        {
            byte[] fileData;
            using (var ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                fileData = ms.ToArray();
            }
            if (!CompoundFile.HasSignature(fileData))
                return null;

            var file = new CompoundFile(fileData);
            var imageObject = FindImageObject(file);
            if (imageObject == null)
                return null;
            try
            {
                return ReadImageObject(file, imageObject);
            }
            catch (Exception e) when (e is IndexOutOfRangeException || e is ArgumentException || e is OverflowException)
            {
                throw new ImageDecodeException("Invalid FlashPix file: " + e.Message);
            }
        }

        /// <summary>
        /// Finds the image object (storage) that should be shown.
        /// </summary>
        private static CompoundFileEntry? FindImageObject(CompoundFile file)
        {
            // Not a FlashPix file as such, but a bare image object.
            if (file.Find(file.Root, ImageContentsName) != null)
                return file.Root;

            var globalInfo = file.Find(file.Root, GlobalInfoName);
            if (globalInfo != null && globalInfo.IsStream)
            {
                var outputs = new OlePropertySet(file.Read(globalInfo)).GetUIntVector(PidVisibleOutputs);
                if (outputs != null && outputs.Length > 0)
                {
                    var visible = file.Find(file.Root, ImageObjectPrefix + outputs[0].ToString("D6"));
                    if (visible != null && IsImageObject(file, visible))
                        return visible;
                }
            }

            // Otherwise the image object with the lowest number, which is normally the
            // source image.
            CompoundFileEntry? result = null;
            foreach (var entry in file.Children(file.Root))
            {
                if (entry.Name.StartsWith(ImageObjectPrefix, StringComparison.OrdinalIgnoreCase) && IsImageObject(file, entry)
                    && (result == null || string.CompareOrdinal(entry.Name, result.Name) < 0))
                    result = entry;
            }
            return result;
        }

        private static bool IsImageObject(CompoundFile file, CompoundFileEntry entry)
        {
            return entry.IsStorage && file.Find(entry, ImageContentsName) != null;
        }

        private static ImageData ReadImageObject(CompoundFile file, CompoundFileEntry imageObject)
        {
            var contents = new OlePropertySet(file.Read(file.Find(imageObject, ImageContentsName)!));

            // The largest resolution has the highest number. (A non-hierarchical image
            // stores only that one.)
            CompoundFileEntry? resStorage = null;
            int resolution = -1;
            foreach (var entry in file.Children(imageObject))
            {
                if (entry.IsStorage && entry.Name.StartsWith(ResolutionPrefix, StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(entry.Name.AsSpan(ResolutionPrefix.Length), out int n) && n > resolution && n < 256)
                {
                    resStorage = entry;
                    resolution = n;
                }
            }
            if (resStorage == null)
                throw new ImageDecodeException("FlashPix image has no resolutions.");

            uint resPid = (uint)resolution << 16;
            var colorBlob = contents.GetBlob(PidSubimageColor | resPid)
                ?? throw new ImageDecodeException("FlashPix image has no subimage color property.");
            var format = contents.GetUIntVector(PidNumericalFormat | resPid);
            if (format != null && Array.Exists(format, f => f != VtUi1))
                throw new ImageDecodeException("Only 8-bit FlashPix images are supported.");

            var headerEntry = file.Find(resStorage, SubimageHeaderName);
            var dataEntry = file.Find(resStorage, SubimageDataName);
            if (headerEntry == null || dataEntry == null)
                throw new ImageDecodeException("FlashPix resolution is missing its subimage streams.");
            var header = file.Read(headerEntry);
            var data = file.Read(dataEntry);
            if (header.Length < StreamHeaderSize + 36)
                throw new ImageDecodeException("FlashPix subimage header is too short.");

            var h = header.AsSpan(StreamHeaderSize);
            int width = (int)BinaryPrimitives.ReadUInt32LittleEndian(h[4..]);
            int height = (int)BinaryPrimitives.ReadUInt32LittleEndian(h[8..]);
            long tileCount = BinaryPrimitives.ReadUInt32LittleEndian(h[12..]);
            int tileWidth = (int)BinaryPrimitives.ReadUInt32LittleEndian(h[16..]);
            int tileHeight = (int)BinaryPrimitives.ReadUInt32LittleEndian(h[20..]);
            int channels = (int)BinaryPrimitives.ReadUInt32LittleEndian(h[24..]);
            long tableOffset = BinaryPrimitives.ReadUInt32LittleEndian(h[28..]);
            long entrySize = BinaryPrimitives.ReadUInt32LittleEndian(h[32..]);

            if (width <= 0 || height <= 0 || (long)width * height * 4 > int.MaxValue)
                throw new ImageDecodeException("FlashPix image has invalid dimensions.");
            if (tileWidth <= 0 || tileHeight <= 0 || tileWidth > 4096 || tileHeight > 4096)
                throw new ImageDecodeException("FlashPix image has an invalid tile size.");
            if (channels < 1 || channels > 4)
                throw new ImageDecodeException("Unsupported number of FlashPix channels: " + channels);
            if (entrySize < 16)
                throw new ImageDecodeException("FlashPix tile header table is invalid.");

            var colors = new ColorLayout(colorBlob, channels);

            int tilesAcross = (width + tileWidth - 1) / tileWidth;
            int tilesDown = (height + tileHeight - 1) / tileHeight;
            if (tileCount < (long)tilesAcross * tilesDown
                || StreamHeaderSize + tableOffset + (long)tilesAcross * tilesDown * entrySize > header.Length)
                throw new ImageDecodeException("FlashPix image is missing some of its tiles.");

            var samples = new byte[width * height * channels];
            for (int ty = 0; ty < tilesDown; ty++)
            {
                for (int tx = 0; tx < tilesAcross; tx++)
                {
                    var entry = header.AsSpan((int)(StreamHeaderSize + tableOffset + (ty * tilesAcross + tx) * entrySize));
                    var tile = new Tile
                    {
                        X = tx * tileWidth,
                        Y = ty * tileHeight,
                        Width = Math.Min(tileWidth, width - tx * tileWidth),
                        Height = Math.Min(tileHeight, height - ty * tileHeight),
                        Offset = BinaryPrimitives.ReadUInt32LittleEndian(entry),
                        Size = BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]),
                        Compression = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]),
                        Subtype = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]),
                    };
                    DecodeTile(tile, data, contents, colors, samples, width, channels, tileWidth, tileHeight);
                }
            }

            return Util.LoadRgba(width, height, ToBgra(samples, width * height, colors));
        }

        private sealed class Tile
        {
            public int X, Y, Width, Height;
            public long Offset, Size;
            public uint Compression, Subtype;
        }

        private static void DecodeTile(Tile tile, byte[] data, OlePropertySet contents, ColorLayout colors,
            byte[] samples, int width, int channels, int tileWidth, int tileHeight)
        {
            switch (tile.Compression)
            {
                case CompressionNone:
                {
                    var src = TileData(tile, data);
                    if (src.Length < (long)tileWidth * tileHeight * channels)
                        throw new ImageDecodeException("FlashPix tile is truncated.");
                    for (int y = 0; y < tile.Height; y++)
                        src.Slice(y * tileWidth * channels, tile.Width * channels).CopyTo(samples.AsSpan(((tile.Y + y) * width + tile.X) * channels));
                    break;
                }

                case CompressionSingleColor:
                {
                    // The color is in the subtype, one byte per channel, starting from the
                    // least significant.
                    for (int y = 0; y < tile.Height; y++)
                    {
                        int p = ((tile.Y + y) * width + tile.X) * channels;
                        for (int x = 0; x < tile.Width; x++)
                            for (int c = 0; c < channels; c++)
                                samples[p++] = (byte)(tile.Subtype >> (c * 8));
                    }
                    break;
                }

                case CompressionJpeg:
                    DecodeJpegTile(tile, data, contents, colors, samples, width, channels);
                    break;

                case CompressionInvalid:
                    // A tile with no valid data, which shouldn't be in a saved file. Leave it blank.
                    break;

                default:
                    throw new ImageDecodeException("Unsupported FlashPix compression type: " + tile.Compression);
            }
        }

        private static void DecodeJpegTile(Tile tile, byte[] data, OlePropertySet contents, ColorLayout colors,
            byte[] samples, int width, int channels)
        {
            // The subtype holds the interleaving, the chroma subsampling, whether the color
            // was converted to YCbCr, and which set of JPEG tables to use (0 if the tile has
            // its own), one byte each, starting from the least significant.
            bool colorConverted = ((tile.Subtype >> 16) & 0xFF) == 1;
            int tableIndex = (int)(tile.Subtype >> 24);

            byte[]? tables = null;
            if (tableIndex != 0)
            {
                tables = contents.GetBlob(PidJpegTables | ((uint)tableIndex << 16))
                    ?? throw new ImageDecodeException("FlashPix tile refers to missing JPEG tables.");
            }

            var image = JpegDecoder.Decode(JpegDecoder.WithTables(tables, TileData(tile, data)), false);
            if (image.Components != channels)
                throw new ImageDecodeException("FlashPix JPEG tile has the wrong number of channels.");
            if (image.Precision != 8)
                throw new ImageDecodeException("Only 8-bit FlashPix JPEG tiles are supported.");

            var s = image.Samples;
            if (colorConverted && colors.Space == SpaceNifRgb && channels >= 3)
            {
                JpegDecoder.YccToRgb(s, 255, channels);
                if (colors.Opacity >= 0)
                {
                    for (int i = 0; i + 2 < s.Length; i += channels)
                    {
                        s[i] = 255 - s[i];
                        s[i + 1] = 255 - s[i + 1];
                        s[i + 2] = 255 - s[i + 2];
                    }
                }
            }

            int copyWidth = Math.Min(tile.Width, image.Width);
            int copyHeight = Math.Min(tile.Height, image.Height);
            for (int y = 0; y < copyHeight; y++)
            {
                int src = y * image.Width * channels;
                int dst = ((tile.Y + y) * width + tile.X) * channels;
                for (int i = 0; i < copyWidth * channels; i++)
                    samples[dst + i] = (byte)s[src + i];
            }
        }

        private static ReadOnlySpan<byte> TileData(Tile tile, byte[] data)
        {
            long start = StreamHeaderSize + tile.Offset;
            if (start > data.Length || tile.Size > data.Length - start)
                throw new ImageDecodeException("FlashPix tile is out of bounds.");
            return data.AsSpan((int)start, (int)tile.Size);
        }

        /// <summary>
        /// The meaning of each channel of an image, as given by its subimage color property.
        /// </summary>
        private sealed class ColorLayout
        {
            public int Space;
            public bool Premultiplied;

            // Index of the channel of each color (red, green and blue; luma and two
            // chromas; or monochrome), and of opacity, or -1 if there is none.
            public int[] Color = { -1, -1, -1 };
            public int Opacity = -1;

            public ColorLayout(byte[] blob, int channels)
            {
                if (blob.Length < 8 + channels * 4 || BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(4)) != channels)
                    throw new ImageDecodeException("FlashPix subimage color property doesn't match the image.");

                Space = -1;
                for (int i = 0; i < channels; i++)
                {
                    uint code = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(8 + i * 4));
                    // The top bit of the space means "uncalibrated", which makes no
                    // difference to us.
                    int space = (int)(code >> 16) & 0x7FFF;
                    int color = (int)code & 0x7FFF;
                    if (color == ColorOpacity)
                    {
                        Opacity = i;
                        if (Space < 0)
                            Space = space;
                        continue;
                    }
                    if (color > 2)
                        throw new ImageDecodeException("Unsupported FlashPix channel color: " + color);
                    Space = space;
                    Color[color] = i;
                    Premultiplied |= (code & 0x8000) != 0;
                }

                bool valid = Space switch
                {
                    SpaceNifRgb or SpacePhotoYcc => Color[0] >= 0 && Color[1] >= 0 && Color[2] >= 0,
                    SpaceMonochrome => Color[0] >= 0,
                    SpaceColorless => Opacity >= 0 && Color[0] < 0,
                    _ => false
                };
                if (!valid)
                    throw new ImageDecodeException("Unsupported FlashPix color space: " + Space);
            }
        }

        private static byte[] ToBgra(byte[] samples, int pixelCount, ColorLayout colors)
        {
            int channels = samples.Length / pixelCount;
            var bmp = new byte[pixelCount * 4];
            var c = new int[3];
            for (int i = 0, s = 0, d = 0; i < pixelCount; i++, s += channels, d += 4)
            {
                int alpha = colors.Opacity >= 0 ? samples[s + colors.Opacity] : 255;
                for (int k = 0; k < 3; k++)
                {
                    int v = colors.Color[k] >= 0 ? samples[s + colors.Color[k]] : 0;
                    if (colors.Premultiplied && colors.Opacity >= 0)
                        v = alpha == 0 ? 0 : Math.Min(255, (v * 255 + alpha / 2) / alpha);
                    c[k] = v;
                }

                int r, g, b;
                switch (colors.Space)
                {
                    case SpaceNifRgb:
                        r = c[0];
                        g = c[1];
                        b = c[2];
                        break;
                    case SpacePhotoYcc:
                        PhotoYccToRgb(c[0], c[1], c[2], out r, out g, out b);
                        break;
                    case SpaceMonochrome:
                        r = g = b = c[0];
                        break;
                    default:
                        // Opacity alone, which we show as a grayscale image.
                        r = g = b = alpha;
                        alpha = 255;
                        break;
                }
                bmp[d] = (byte)b;
                bmp[d + 1] = (byte)g;
                bmp[d + 2] = (byte)r;
                bmp[d + 3] = (byte)alpha;
            }
            return bmp;
        }

        /// <summary>
        /// Converts 8-bit PhotoYCC values to RGB, by inverting the encoding given in the
        /// FlashPix specification (section 5.3.1). Values beyond the white point are clipped.
        /// </summary>
        private static void PhotoYccToRgb(int y, int c1, int c2, out int r, out int g, out int b)
        {
            double luma = y * 1.402;
            double chroma1 = (c1 - 156) * 255.0 / 111.40;
            double chroma2 = (c2 - 137) * 255.0 / 135.64;
            double rr = luma + chroma2;
            double bb = luma + chroma1;
            double gg = (luma - 0.299 * rr - 0.114 * bb) / 0.587;
            r = Clamp(rr);
            g = Clamp(gg);
            b = Clamp(bb);
        }

        private static int Clamp(double v)
        {
            return v <= 0 ? 0 : v >= 255 ? 255 : (int)(v + 0.5);
        }
    }
}
