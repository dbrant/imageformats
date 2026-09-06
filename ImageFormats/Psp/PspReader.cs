#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

/*

Decoder for Paint Shop Pro (.PSP, .PSPIMAGE) images.

The PSP format was created by Jasc Software for Paint Shop Pro 5, and is still
written by Corel PaintShop Pro today. It is a block-oriented format: a 32-byte
signature and a version number are followed by a flat sequence of blocks, each of
which is introduced by the four bytes "~BK\0". A block may contain sub-blocks of
the same shape, so the file is really a shallow tree.

Everything below follows the "Paint Shop Pro (PSP) File Format Specification"
published by Jasc (file format version 3.0, dated November 1998), plus the two
changes that later versions made to it that matter for decoding:

  * From file format version 4 (Paint Shop Pro 6) onward, the block header lost
    its "initial data chunk length" field. Instead every chunk that a block
    begins with now starts with its own length, which is how a reader skips
    fields that were added in a later version. So a version 3 block header is 14
    bytes long and a version 4+ header is 10.

  * Version 3 stores a layer's name as a fixed 256-byte field, and the layer's
    bitmap and channel counts inside the layer information chunk itself. From
    version 4 on the name is a counted string, and the two counts moved out into
    a "layer extension" chunk that follows the layer information chunk. From
    version 9 or thereabouts a raster layer may additionally carry a whole
    sub-block ahead of that extension chunk, which we detect by looking for the
    "~BK\0" signature where the extension's length would otherwise be.

An image is stored one layer at a time, and each layer one channel at a time: a
24-bit layer holds separate red, green and blue channels, and if the layer has
transparency there is a fourth channel holding its transparency mask. Each
channel is an independently compressed stream, either uncompressed, RLE, or
"LZ77", which is plain zlib (the specification describes it as the LZ77 variant
that PNG uses, minus PNG's row filters).

Note that, contrary to what the specification says, scanlines of channels at 8
bits per sample and above are NOT padded to a 4-byte boundary. Only 1- and 4-bit
channels are.

Since this library hands back a single image, the layers are composited here,
bottom to top (the order in which they appear in the file), honoring each layer's
visibility, opacity and blend mode. Adjustment and vector layers are skipped: the
raster data that PSP caches for a vector layer is composited, but the vector
artwork itself is not re-rendered. Layer user masks are skipped as well, since a
mask carries its own bounding rectangle and it isn't clear what the mask value
outside that rectangle is meant to be.

Copyright 2026+ Dmitry Brant
https://dmitrybrant.com

License: MIT
*/

namespace DmitryBrant.ImageFormats
{
    /// <summary>
    /// Handles reading Paint Shop Pro (.PSP) images.
    /// </summary>
    public static class PspReader
    {
        private const string SignatureText = "Paint Shop Pro Image File";
        private const int SignatureLength = 32;

        // Block identifiers (PSPBlockID).
        private const int BlockImage = 0;
        private const int BlockColor = 2;
        private const int BlockLayerStart = 3;
        private const int BlockLayer = 4;
        private const int BlockChannel = 5;
        private const int BlockExtendedData = 10;

        // Bitmap types (PSPDIBType).
        private const int DibImage = 0;
        private const int DibTransMask = 1;
        private const int DibUserMask = 2;

        // Channel types (PSPChannelType).
        private const int ChannelComposite = 0;
        private const int ChannelRed = 1;
        private const int ChannelGreen = 2;
        private const int ChannelBlue = 3;

        // Compression types (PSPCompression).
        private const int CompressionNone = 0;
        private const int CompressionRle = 1;
        private const int CompressionLz77 = 2;

        // Layer types. Version 3 has only two, and both of them are raster layers. From
        // version 4 on the enumeration was renumbered and extended, and only these two of
        // its members hold raster data of their own.
        private const int LayerV3FloatingSelection = 1;
        private const int LayerRaster = 1;
        private const int LayerFloatingRasterSelection = 2;

        // Extended data field identifiers (PSPExtendedDataID).
        private const int XDataTransparencyIndex = 0;

        // The specification permits at most 64 layers, so this is only a sanity limit
        // against a corrupt layer count, not a real one.
        private const int MaxLayers = 4096;

        // Likewise a sanity limit, which also keeps the size of a bitmap in bytes inside
        // the range of an int.
        private const long MaxPixels = 1L << 28;

        /// <summary>
        /// Reads a Paint Shop Pro (.PSP) image from a file.
        /// </summary>
        /// <param name="fileName">Name of the file to read.</param>
        /// <returns>ImageData that contains the image that was read.</returns>
        public static ImageData Load(string fileName)
        {
            using var f = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Load(f);
        }

        /// <summary>
        /// Reads a Paint Shop Pro (.PSP) image from a stream.
        /// </summary>
        /// <param name="stream">Stream from which to read the image.</param>
        /// <returns>ImageData that contains the image that was read.</returns>
        public static ImageData Load(Stream stream)
        {
            var reader = new BinaryReader(stream);

            byte[] signature = reader.ReadBytes(SignatureLength);
            if (signature.Length < SignatureLength
                || Encoding.ASCII.GetString(signature, 0, SignatureText.Length) != SignatureText)
                throw new ImageDecodeException("This does not appear to be a valid PSP file.");

            var image = new PspImage
            {
                MajorVersion = reader.ReadUInt16(),
                MinorVersion = reader.ReadUInt16()
            };

            // Version 3 is the first published version of the format, and every later
            // version of it is a superset.
            if (image.MajorVersion < 3)
                throw new ImageDecodeException("Unsupported PSP file version "
                    + image.MajorVersion + "." + image.MinorVersion + ".");

            var layers = new List<PspLayer>();
            bool haveAttributes = false;

            try
            {
                while (stream.Position < stream.Length)
                {
                    if (!ReadBlockHeader(reader, image, out int blockId, out long initLength, out long totalLength))
                        break;

                    long blockEnd = Math.Min(stream.Position + totalLength, stream.Length);

                    switch (blockId)
                    {
                        case BlockImage:
                            ReadImageAttributes(reader, image, initLength);
                            haveAttributes = true;
                            break;
                        case BlockColor:
                            image.Palette = ReadPalette(reader, image);
                            break;
                        case BlockExtendedData:
                            ReadExtendedData(reader, image, blockEnd);
                            break;
                        case BlockLayerStart:
                            if (!haveAttributes)
                                throw new ImageDecodeException("PSP file does not begin with a general image attributes block.");
                            ReadLayerBank(reader, image, blockEnd, layers);
                            break;
                        default:
                            // The specification requires readers to skip over any block they
                            // don't recognize, documented or not.
                            break;
                    }

                    stream.Seek(blockEnd, SeekOrigin.Begin);
                }
            }
            catch (Exception e) when (e is EndOfStreamException || e is InvalidDataException)
            {
                // Truncated or corrupt file: composite whatever layers we did get.
                Util.log("Error while processing PSP file: " + e.Message);
            }

            if (!haveAttributes)
                throw new ImageDecodeException("PSP file does not contain a general image attributes block.");

            byte[] bmpData = new byte[image.Width * 4 * image.Height];
            foreach (var layer in layers)
                Composite(bmpData, image.Width, image.Height, layer);

            return Util.LoadRgba(image.Width, image.Height, bmpData);
        }

        /// <summary>
        /// Reads the header that introduces every block and sub-block. Returns false if the
        /// stream is not positioned at one.
        /// </summary>
        private static bool ReadBlockHeader(BinaryReader reader, PspImage image, out int blockId,
                                            out long initLength, out long totalLength)
        {
            blockId = -1;
            initLength = -1;
            totalLength = 0;

            byte[] id = reader.ReadBytes(4);
            if (id.Length < 4)
                return false;
            if (id[0] != '~' || id[1] != 'B' || id[2] != 'K' || id[3] != 0)
                return false;

            blockId = reader.ReadUInt16();
            if (image.MajorVersion < 4)
            {
                initLength = reader.ReadUInt32();
                totalLength = reader.ReadUInt32();
            }
            else
            {
                // Version 4 dropped the initial data chunk length. Each chunk now carries
                // its own length as its first field instead.
                totalLength = reader.ReadUInt32();
            }
            return true;
        }

        private static void ReadImageAttributes(BinaryReader reader, PspImage image, long initLength)
        {
            long chunkStart = reader.BaseStream.Position;
            long chunkLength = image.MajorVersion >= 4 ? reader.ReadUInt32() : initLength;

            image.Width = reader.ReadInt32();
            image.Height = reader.ReadInt32();
            reader.ReadDouble(); // resolution
            reader.ReadByte(); // resolution metric
            image.Compression = reader.ReadUInt16();
            image.BitDepth = reader.ReadUInt16();
            reader.ReadUInt16(); // plane count, always 1
            reader.ReadUInt32(); // color count
            image.Grayscale = reader.ReadByte() != 0;
            reader.ReadUInt32(); // summed size of all layer bitmaps
            reader.ReadInt32(); // active layer
            image.LayerCount = reader.ReadUInt16();

            if (image.Width < 1 || image.Height < 1 || (long)image.Width * image.Height > MaxPixels)
                throw new ImageDecodeException("This PSP file appears to have invalid dimensions.");
            if (image.Compression > CompressionLz77)
                throw new ImageDecodeException("Unsupported PSP compression type " + image.Compression + ".");
            if (image.LayerCount > MaxLayers)
                throw new ImageDecodeException("This PSP file appears to have an invalid layer count.");

            switch (image.BitDepth)
            {
                case 24:
                case 48:
                    image.ColorType = PspColorType.Rgb;
                    break;
                case 16:
                    if (!image.Grayscale)
                        throw new ImageDecodeException("Unsupported PSP bit depth " + image.BitDepth + ".");
                    image.ColorType = PspColorType.Gray;
                    break;
                case 1:
                case 4:
                case 8:
                    image.ColorType = image.Grayscale && image.BitDepth == 8 ? PspColorType.Gray : PspColorType.Indexed;
                    break;
                default:
                    throw new ImageDecodeException("Unsupported PSP bit depth " + image.BitDepth + ".");
            }
            image.BytesPerSample = image.BitDepth == 16 || image.BitDepth == 48 ? 2 : 1;

            if (chunkLength > 0)
                reader.BaseStream.Seek(chunkStart + chunkLength, SeekOrigin.Begin);
        }

        /// <summary>
        /// Reads a color palette sub-block, returning its entries as packed RGB values.
        /// </summary>
        private static uint[] ReadPalette(BinaryReader reader, PspImage image)
        {
            long chunkStart = reader.BaseStream.Position;
            if (image.MajorVersion >= 4)
            {
                long chunkLength = reader.ReadUInt32();
                reader.BaseStream.Seek(chunkStart + chunkLength, SeekOrigin.Begin);
            }

            long entryCount = reader.ReadUInt32();
            if (entryCount > 256)
                throw new ImageDecodeException("This PSP file appears to have an invalid palette size.");

            var palette = new uint[256];
            for (int i = 0; i < entryCount; i++)
            {
                byte r = reader.ReadByte();
                byte g = reader.ReadByte();
                byte b = reader.ReadByte();
                reader.ReadByte(); // reserved, always 0
                palette[i] = ((uint)r << 16) | ((uint)g << 8) | b;
            }
            return palette;
        }

        /// <summary>
        /// Reads the extended data block, which is a series of keyword/value pairs. The only
        /// keyword that concerns us is the index of the transparent color of a paletted image.
        /// </summary>
        private static void ReadExtendedData(BinaryReader reader, PspImage image, long blockEnd)
        {
            var stream = reader.BaseStream;
            while (stream.Position + 10 <= blockEnd)
            {
                byte[] id = reader.ReadBytes(4);
                if (id.Length < 4 || id[0] != '~' || id[1] != 'F' || id[2] != 'L' || id[3] != 0)
                    break;

                int keyword = reader.ReadUInt16();
                long valueLength = reader.ReadUInt32();
                long valueStart = stream.Position;
                if (valueStart + valueLength > blockEnd)
                    break;

                if (keyword == XDataTransparencyIndex && valueLength >= 2)
                    image.TransparentIndex = reader.ReadUInt16();

                stream.Seek(valueStart + valueLength, SeekOrigin.Begin);
            }
        }

        private static void ReadLayerBank(BinaryReader reader, PspImage image, long blockEnd, List<PspLayer> layers)
        {
            var stream = reader.BaseStream;
            while (stream.Position < blockEnd && layers.Count < MaxLayers)
            {
                if (!ReadBlockHeader(reader, image, out int blockId, out long initLength, out long totalLength))
                    break;

                long layerStart = stream.Position;
                long layerEnd = totalLength > 0 ? Math.Min(layerStart + totalLength, blockEnd) : blockEnd;

                if (blockId == BlockLayer)
                {
                    var layer = ReadLayer(reader, image, initLength, layerStart, layerEnd);
                    if (layer != null)
                        layers.Add(layer);
                }
                stream.Seek(layerEnd, SeekOrigin.Begin);
            }
        }

        private static PspLayer? ReadLayer(BinaryReader reader, PspImage image, long initLength,
                                           long layerStart, long layerEnd)
        {
            var stream = reader.BaseStream;
            var imageRect = new int[4];
            var savedRect = new int[4];
            string name;
            int layerType, channelCount;
            long channelStart;
            byte opacity;
            int blendMode;
            bool visible;

            if (image.MajorVersion >= 4)
            {
                long chunkLength = reader.ReadUInt32();
                name = Encoding.Latin1.GetString(reader.ReadBytes(reader.ReadUInt16()));
                layerType = reader.ReadByte();
                ReadRect(reader, imageRect);
                ReadRect(reader, savedRect);
                opacity = reader.ReadByte();
                blendMode = reader.ReadByte();
                // Version 6 turned the layer visibility flag into a bit field, whose lowest
                // bit still means "visible".
                visible = (reader.ReadByte() & 1) != 0;

                if (layerType != LayerRaster && layerType != LayerFloatingRasterSelection)
                {
                    Util.log("PSP: layer \"" + name + "\" is of unsupported type " + layerType + ", and was skipped.");
                    return null;
                }

                // The layer extension chunk that carries the bitmap and channel counts sits
                // just past the end of the layer information chunk.
                long extensionStart = layerStart + chunkLength;
                if (extensionStart + 8 > layerEnd)
                    return null;
                stream.Seek(extensionStart, SeekOrigin.Begin);

                // Newer versions may place an additional sub-block ahead of the extension
                // chunk. A chunk length can never begin with the block signature, so looking
                // for the signature here is unambiguous.
                byte[] peek = reader.ReadBytes(4);
                if (peek.Length < 4)
                    return null;
                if (peek[0] == '~' && peek[1] == 'B' && peek[2] == 'K' && peek[3] == 0)
                {
                    reader.ReadUInt16(); // block identifier
                    long subLength = reader.ReadUInt32();
                    extensionStart = stream.Position + subLength;
                    if (extensionStart + 8 > layerEnd)
                        return null;
                }

                stream.Seek(extensionStart, SeekOrigin.Begin);
                long extensionLength = reader.ReadUInt32();
                reader.ReadUInt16(); // bitmap count
                channelCount = reader.ReadUInt16();
                channelStart = extensionStart + extensionLength;
            }
            else
            {
                byte[] nameBytes = reader.ReadBytes(256);
                int nameEnd = Array.IndexOf(nameBytes, (byte)0);
                name = Encoding.Latin1.GetString(nameBytes, 0, nameEnd < 0 ? nameBytes.Length : nameEnd);
                layerType = reader.ReadByte();
                ReadRect(reader, imageRect);
                ReadRect(reader, savedRect);
                opacity = reader.ReadByte();
                blendMode = reader.ReadByte();
                visible = reader.ReadByte() != 0;
                reader.ReadByte(); // transparency protected flag
                reader.ReadByte(); // link group identifier
                reader.ReadBytes(32); // mask rectangle and saved mask rectangle
                reader.ReadByte(); // mask linked flag
                reader.ReadByte(); // mask disabled flag
                reader.ReadByte(); // invert mask on blend flag
                reader.ReadUInt16(); // blend range count
                reader.ReadBytes(40); // five source/destination blend range pairs
                reader.ReadUInt16(); // bitmap count
                channelCount = reader.ReadUInt16();

                if (layerType == LayerV3FloatingSelection)
                    Util.log("PSP: floating selection layer \"" + name + "\" treated as a normal layer.");
                channelStart = layerStart + initLength;
            }

            // Only the "saved" rectangle of a layer is written to the file. It gives the
            // extent of the layer's significant data, relative to the layer's own origin.
            int width = savedRect[2] - savedRect[0];
            int height = savedRect[3] - savedRect[1];
            if (width <= 0 || height <= 0 || channelCount <= 0)
                return null;
            if ((long)width * height > MaxPixels)
                throw new ImageDecodeException("This PSP file appears to have invalid layer dimensions.");

            var layer = new PspLayer
            {
                X = imageRect[0] + savedRect[0],
                Y = imageRect[1] + savedRect[1],
                Width = width,
                Height = height,
                Opacity = opacity,
                BlendMode = blendMode,
                Visible = visible
            };

            // Every channel of the layer's bitmap is compressed separately, and arrives in
            // its own sub-block.
            var planes = new byte[]?[4];
            byte[]? alphaPlane = null;

            stream.Seek(channelStart, SeekOrigin.Begin);
            for (int i = 0; i < channelCount && stream.Position < layerEnd; i++)
            {
                if (!ReadBlockHeader(reader, image, out int blockId, out long initLen, out long totalLen))
                    break;

                long chunkStart = stream.Position;
                long chanEnd = totalLen > 0 ? Math.Min(chunkStart + totalLen, layerEnd) : layerEnd;

                if (blockId == BlockChannel)
                {
                    long chunkLength = image.MajorVersion >= 4 ? reader.ReadUInt32() : initLen;
                    long compressedLength = reader.ReadUInt32();
                    reader.ReadUInt32(); // uncompressed length, which files in the wild get wrong
                    int bitmapType = reader.ReadUInt16();
                    int channelType = reader.ReadUInt16();

                    if (chunkLength > 0)
                        stream.Seek(chunkStart + chunkLength, SeekOrigin.Begin);

                    if (bitmapType == DibTransMask)
                    {
                        alphaPlane = ReadChannel(reader, image, width, height, compressedLength, true);
                    }
                    else if (bitmapType == DibImage && channelType >= ChannelComposite && channelType <= ChannelBlue)
                    {
                        planes[channelType] = ReadChannel(reader, image, width, height, compressedLength, false);
                    }
                    else if (bitmapType == DibUserMask)
                    {
                        Util.log("PSP: user mask of layer \"" + name + "\" is not supported, and was ignored.");
                    }
                }
                stream.Seek(chanEnd, SeekOrigin.Begin);
            }

            layer.Pixels = BuildLayerPixels(image, layer, planes, alphaPlane);
            return layer.Pixels != null ? layer : null;
        }

        /// <summary>
        /// Combines a layer's decoded channels into a single BGRA bitmap.
        /// </summary>
        private static byte[]? BuildLayerPixels(PspImage image, PspLayer layer, byte[]?[] planes, byte[]? alphaPlane)
        {
            int width = layer.Width, height = layer.Height, pixelCount = width * height;
            // Samples wider than 8 bits are truncated to their most significant byte.
            int sampleSize = image.BytesPerSample;
            int high = sampleSize - 1;
            var pixels = new byte[pixelCount * 4];

            if (image.ColorType == PspColorType.Rgb)
            {
                byte[]? r = planes[ChannelRed], g = planes[ChannelGreen], b = planes[ChannelBlue];
                if (r == null || g == null || b == null)
                    return null;
                for (int i = 0, p = 0, s = high; i < pixelCount; i++, p += 4, s += sampleSize)
                {
                    pixels[p] = b[s];
                    pixels[p + 1] = g[s];
                    pixels[p + 2] = r[s];
                    pixels[p + 3] = 0xFF;
                }
            }
            else if (image.ColorType == PspColorType.Gray)
            {
                byte[]? c = planes[ChannelComposite];
                if (c == null)
                    return null;
                for (int i = 0, p = 0, s = high; i < pixelCount; i++, p += 4, s += sampleSize)
                {
                    pixels[p] = pixels[p + 1] = pixels[p + 2] = c[s];
                    pixels[p + 3] = 0xFF;
                }
            }
            else
            {
                byte[]? c = planes[ChannelComposite];
                if (c == null)
                    return null;
                uint[] palette = image.Palette ?? GrayscalePalette();
                // Scanlines below 8 bits per pixel are padded to a 4-byte boundary.
                int stride = image.BitDepth < 8 ? ((width * image.BitDepth + 7) / 8 + 3) / 4 * 4 : width;

                for (int y = 0; y < height; y++)
                {
                    int p = y * width * 4;
                    int row = y * stride;
                    for (int x = 0; x < width; x++, p += 4)
                    {
                        int index;
                        switch (image.BitDepth)
                        {
                            case 1:
                                index = (c[row + (x >> 3)] >> (7 - (x & 7))) & 1;
                                break;
                            case 4:
                                index = (x & 1) == 0 ? c[row + (x >> 1)] >> 4 : c[row + (x >> 1)] & 0xF;
                                break;
                            default:
                                index = c[row + x];
                                break;
                        }
                        uint color = palette[index];
                        pixels[p] = (byte)color;
                        pixels[p + 1] = (byte)(color >> 8);
                        pixels[p + 2] = (byte)(color >> 16);
                        pixels[p + 3] = index == image.TransparentIndex ? (byte)0 : (byte)0xFF;
                    }
                }
            }

            if (alphaPlane != null)
            {
                for (int i = 0, p = 3, s = high; i < pixelCount; i++, p += 4, s += sampleSize)
                    pixels[p] = Math.Min(pixels[p], alphaPlane[s]);
            }
            return pixels;
        }

        /// <summary>
        /// Reads and decompresses a single channel of a layer.
        /// </summary>
        private static byte[] ReadChannel(BinaryReader reader, PspImage image, int width, int height,
                                          long compressedLength, bool isMask)
        {
            // Only 1- and 4-bit color scanlines are padded to a 4-byte boundary. Despite
            // what the specification says, deeper ones are not, and a mask is one sample
            // per pixel however deep the image's own color channels are.
            int stride = !isMask && image.BitDepth < 8
                ? ((width * image.BitDepth + 7) / 8 + 3) / 4 * 4
                : width * image.BytesPerSample;
            var plane = new byte[stride * height];

            switch (image.Compression)
            {
                case CompressionNone:
                    ReadFully(reader.BaseStream, plane, 0, plane.Length);
                    break;
                case CompressionRle:
                    DecompressRle(reader.BaseStream, plane, compressedLength);
                    break;
                case CompressionLz77:
                    DecompressLz77(reader, plane, compressedLength);
                    break;
            }
            return plane;
        }

        /// <summary>
        /// A run count over 128 repeats the byte that follows it that many times minus 128,
        /// and any other run count introduces that many literal bytes.
        /// </summary>
        private static void DecompressRle(Stream stream, byte[] plane, long compressedLength)
        {
            long end = stream.Position + compressedLength;
            int outPos = 0;

            while (outPos < plane.Length && stream.Position < end)
            {
                int runCount = stream.ReadByte();
                if (runCount < 0)
                    break;

                if (runCount > 128)
                {
                    runCount = Math.Min(runCount - 128, plane.Length - outPos);
                    int value = stream.ReadByte();
                    if (value < 0)
                        break;
                    for (int i = 0; i < runCount; i++)
                        plane[outPos++] = (byte)value;
                }
                else
                {
                    runCount = Math.Min(runCount, plane.Length - outPos);
                    outPos += ReadFully(stream, plane, outPos, runCount);
                }
            }
        }

        /// <summary>
        /// The format's "LZ77" compression is plain zlib: the LZ77 variant used by PNG, but
        /// without PNG's row filtering.
        /// </summary>
        private static void DecompressLz77(BinaryReader reader, byte[] plane, long compressedLength)
        {
            byte[] compressed = reader.ReadBytes((int)compressedLength);
            using var inflater = new ZLibStream(new MemoryStream(compressed), CompressionMode.Decompress);
            ReadFully(inflater, plane, 0, plane.Length);
        }

        /// <summary>
        /// Composites one layer onto the canvas, using the standard separable blend functions.
        /// </summary>
        private static void Composite(byte[] canvas, int canvasWidth, int canvasHeight, PspLayer layer)
        {
            if (!layer.Visible || layer.Pixels == null || layer.Opacity == 0)
                return;

            byte[] src = layer.Pixels;
            for (int y = 0; y < layer.Height; y++)
            {
                int destY = layer.Y + y;
                if (destY < 0 || destY >= canvasHeight)
                    continue;

                for (int x = 0; x < layer.Width; x++)
                {
                    int destX = layer.X + x;
                    if (destX < 0 || destX >= canvasWidth)
                        continue;

                    int s = (y * layer.Width + x) * 4;
                    int srcAlpha = src[s + 3] * layer.Opacity / 255;
                    if (srcAlpha == 0)
                        continue;

                    int d = (destY * canvasWidth + destX) * 4;
                    int destAlpha = canvas[d + 3];
                    int outAlpha = srcAlpha + destAlpha * (255 - srcAlpha) / 255;
                    if (outAlpha == 0)
                        continue;

                    for (int channel = 0; channel < 3; channel++)
                    {
                        int sc = src[s + channel];
                        int dc = canvas[d + channel];
                        // Where the backdrop is transparent there is nothing to blend with,
                        // so the blend function fades in with the backdrop's alpha.
                        int blended = destAlpha == 0 ? sc
                            : ((255 - destAlpha) * sc + destAlpha * Blend(layer.BlendMode, dc, sc) + 127) / 255;
                        int value = (blended * srcAlpha * 255 + dc * destAlpha * (255 - srcAlpha)
                            + outAlpha * 255 / 2) / (outAlpha * 255);
                        canvas[d + channel] = (byte)Math.Min(255, value);
                    }
                    canvas[d + 3] = (byte)outAlpha;
                }
            }
        }

        /// <summary>
        /// Applies one of the separable blend functions to a single 8-bit component.
        /// </summary>
        private static int Blend(int mode, int backdrop, int source)
        {
            switch (mode)
            {
                case 1: // darken
                    return Math.Min(backdrop, source);
                case 2: // lighten
                    return Math.Max(backdrop, source);
                case 7: // multiply
                    return (backdrop * source + 127) / 255;
                case 8: // screen
                    return 255 - ((255 - backdrop) * (255 - source) + 127) / 255;
                case 10: // overlay, which is hard light with the two operands exchanged
                    return HardLight(source, backdrop);
                case 11: // hard light
                    return HardLight(backdrop, source);
                case 12: // soft light
                    return SoftLight(backdrop, source);
                case 13: // difference
                    return Math.Abs(backdrop - source);
                case 14: // dodge
                    if (backdrop == 0)
                        return 0;
                    if (source >= 255)
                        return 255;
                    return Math.Min(255, backdrop * 255 / (255 - source));
                case 15: // burn
                    if (backdrop >= 255)
                        return 255;
                    if (source == 0)
                        return 0;
                    return 255 - Math.Min(255, (255 - backdrop) * 255 / source);
                case 16: // exclusion
                    return backdrop + source - 2 * backdrop * source / 255;
                default:
                    // Normal and dissolve, as well as the blend modes that operate on the
                    // color as a whole rather than component by component (hue, saturation,
                    // color, luminosity), all fall back to painting the source over.
                    return source;
            }
        }

        private static int HardLight(int backdrop, int source)
        {
            return source <= 127
                ? backdrop * 2 * source / 255
                : 255 - (255 - backdrop) * 2 * (255 - source) / 255;
        }

        private static int SoftLight(int backdrop, int source)
        {
            double b = backdrop / 255.0, s = source / 255.0;
            double d = b <= 0.25 ? ((16 * b - 12) * b + 4) * b : Math.Sqrt(b);
            double result = s <= 0.5 ? b - (1 - 2 * s) * b * (1 - b) : b + (2 * s - 1) * (d - b);
            return (int)Math.Round(Math.Max(0.0, Math.Min(1.0, result)) * 255);
        }

        private static void ReadRect(BinaryReader reader, int[] rect)
        {
            for (int i = 0; i < 4; i++)
                rect[i] = reader.ReadInt32();
        }

        private static int ReadFully(Stream stream, byte[] buffer, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                int bytesRead = stream.Read(buffer, offset + total, count - total);
                if (bytesRead <= 0)
                    break;
                total += bytesRead;
            }
            return total;
        }

        /// <summary>
        /// Fallback for a paletted image whose color palette block is missing.
        /// </summary>
        private static uint[] GrayscalePalette()
        {
            var palette = new uint[256];
            for (int i = 0; i < palette.Length; i++)
                palette[i] = ((uint)i << 16) | ((uint)i << 8) | (uint)i;
            return palette;
        }

        private enum PspColorType
        {
            Rgb,
            Gray,
            Indexed
        }

        private class PspImage
        {
            public int MajorVersion;
            public int MinorVersion;
            public int Width;
            public int Height;
            public int Compression;
            public int BitDepth;
            public bool Grayscale;
            public int LayerCount;
            public int BytesPerSample = 1;
            public PspColorType ColorType;
            public uint[]? Palette;
            public int TransparentIndex = -1;
        }

        private class PspLayer
        {
            public int X;
            public int Y;
            public int Width;
            public int Height;
            public byte Opacity;
            public int BlendMode;
            public bool Visible;
            public byte[]? Pixels;
        }
    }
}
