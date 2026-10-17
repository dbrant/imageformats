#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

/*

The structure of a Photoshop (.PSD, .PSB) file, and the code that reads it: the
header, the few image resources that affect decoding, the layer records, and the
pixel data of the layers and of the merged image. See PsdReader.cs for an overview
of the format.

Only the structure is read up front. Pixel data is read from the stream when it's
needed, one channel at a time, so that large files don't have to be held in memory
in their entirety.

Copyright 2026+ Dmitry Brant
https://dmitrybrant.com

License: MIT
*/

namespace DmitryBrant.ImageFormats
{
    /// <summary>
    /// A Photoshop document, as read from a stream.
    /// </summary>
    internal sealed class PsdDocument
    {
        // Color modes.
        public const int ModeBitmap = 0;
        public const int ModeGrayscale = 1;
        public const int ModeIndexed = 2;
        public const int ModeRgb = 3;
        public const int ModeCmyk = 4;
        public const int ModeMultichannel = 7;
        public const int ModeDuotone = 8;
        public const int ModeLab = 9;

        // Compression methods.
        private const int CompressionRaw = 0;
        private const int CompressionRle = 1;
        private const int CompressionZip = 2;
        private const int CompressionZipPredict = 3;

        // Image resources that affect decoding.
        private const int ResourceTransparencyIndex = 1047;
        private const int ResourceVersionInfo = 1057;

        /// <summary>
        /// Sanity limit on the number of pixels in the image, or in any one of its layers
        /// or masks, which also keeps the size of a plane in bytes inside the range of an int.
        /// </summary>
        public const long MaxPixels = 1L << 28;

        /// <summary>
        /// Sanity limit on how deeply groups may be nested, since each level of nesting may
        /// need a canvas of its own. Photoshop itself allows only a handful of levels.
        /// </summary>
        private const int MaxGroupDepth = 100;

        /// <summary>
        /// Sanity limit on the length of the small fields in the layer records (masks, names
        /// and so on), against corrupt lengths.
        /// </summary>
        private const long MaxFieldLength = 1 << 24;

        // Tagged blocks whose length field is 8 bytes wide in PSB files, rather than 4.
        private static readonly HashSet<string> PsbLongKeys = new()
        {
            "LMsk", "Lr16", "Lr32", "Layr", "Mt16", "Mt32", "Mtrn", "Alph", "FMsk", "lnk2", "FEid", "FXid", "PxSD"
        };

        private readonly Stream stream;
        private readonly long fileEnd;
        private readonly byte[] scratch = new byte[8];

        /// <summary>1 for a PSD file, 2 for a PSB file.</summary>
        public int Version;
        public int Width;
        public int Height;
        public int ChannelCount;
        public int Depth;
        public int ColorMode;

        /// <summary>The palette of an indexed image: 256 reds, then 256 greens, then 256 blues.</summary>
        public byte[] Palette = new byte[768];

        /// <summary>Palette index that is transparent in an indexed image, or -1 if none.</summary>
        public int TransparencyIndex = -1;

        /// <summary>
        /// Whether the merged image is the real picture, according to the version info
        /// resource, or null if the file doesn't have one.
        /// </summary>
        public bool? HasRealMergedData;

        /// <summary>The layer records, bottom-most first.</summary>
        public List<PsdLayer> Layers = new();

        /// <summary>
        /// True if the merged image has a transparency channel following its color
        /// channels, which Photoshop signals with a negative layer count.
        /// </summary>
        public bool MergedTransparency;

        /// <summary>Offset of the merged image data in the stream, or -1 if the file ends before it.</summary>
        public long MergedOffset = -1;

        public bool IsPsb => Version == 2;

        /// <summary>
        /// Number of color channels, which come before any transparency, alpha and spot
        /// channels.
        /// </summary>
        public int ColorChannels
        {
            get
            {
                switch (ColorMode)
                {
                    case ModeRgb:
                    case ModeLab:
                        return 3;
                    case ModeCmyk:
                        return 4;
                    case ModeMultichannel:
                        return ChannelCount;
                    default:
                        return 1;
                }
            }
        }

        /// <summary>
        /// Number of channels the image has once it's converted for display: 3 for RGB, 1
        /// for grayscale.
        /// </summary>
        public int DisplayChannels
        {
            get
            {
                switch (ColorMode)
                {
                    case ModeIndexed:
                    case ModeRgb:
                    case ModeCmyk:
                    case ModeLab:
                        return 3;
                    default:
                        return 1;
                }
            }
        }

        private PsdDocument(Stream stream)
        {
            this.stream = stream;
            fileEnd = stream.Length;
        }

        /// <summary>
        /// Reads the structure of a Photoshop file from the current position of a stream.
        /// The stream must stay open while the document is in use, since pixel data is read
        /// from it on demand.
        /// </summary>
        public static PsdDocument Read(Stream stream)
        {
            var doc = new PsdDocument(stream);
            try
            {
                doc.ReadStructure();
            }
            catch (EndOfStreamException)
            {
                throw new ImageDecodeException("This Photoshop file is truncated.");
            }
            catch (InvalidDataException e)
            {
                throw new ImageDecodeException("This Photoshop file appears to be damaged: " + e.Message);
            }
            return doc;
        }

        private void ReadStructure()
        {
            byte[] signature = ReadBytes(4);
            if (signature[0] != '8' || signature[1] != 'B' || signature[2] != 'P' || signature[3] != 'S')
                throw new ImageDecodeException("This does not appear to be a valid Photoshop file.");

            Version = ReadU16();
            if (Version != 1 && Version != 2)
                throw new ImageDecodeException("Unsupported Photoshop file version " + Version + ".");

            Skip(6); // reserved
            ChannelCount = ReadU16();
            long height = ReadU32();
            long width = ReadU32();
            Depth = ReadU16();
            ColorMode = ReadU16();

            if (width < 1 || height < 1 || width > MaxPixels || height > MaxPixels || width * height > MaxPixels)
                throw new ImageDecodeException("This Photoshop file appears to have invalid dimensions.");
            Width = (int)width;
            Height = (int)height;
            if (Depth != 1 && Depth != 8 && Depth != 16 && Depth != 32)
                throw new ImageDecodeException("Unsupported Photoshop bit depth " + Depth + ".");
            if (ChannelCount < 1 || ChannelCount < ColorChannels)
                throw new ImageDecodeException("This Photoshop file appears to have an invalid channel count.");

            // Color mode data: the palette of an indexed image, and otherwise of no use to us.
            long colorLength = ReadU32();
            long colorStart = Position;
            byte[] colorData = ReadBytes(Math.Min(colorLength, Palette.Length));
            Array.Copy(colorData, Palette, colorData.Length);
            Seek(colorStart + colorLength);

            long resourceLength = ReadU32();
            long resourceStart = Position;
            ReadResources(resourceStart, Math.Min(resourceStart + resourceLength, fileEnd));
            Seek(resourceStart + resourceLength);

            long layerMaskLength = ReadLength();
            long layerMaskStart = Position;
            bool complete = layerMaskLength <= fileEnd - layerMaskStart;
            if (layerMaskLength > 0)
            {
                try
                {
                    ReadLayerAndMaskInfo(layerMaskStart, complete ? layerMaskStart + layerMaskLength : fileEnd);
                }
                catch (Exception e) when (e is EndOfStreamException || e is InvalidDataException)
                {
                    // Layers are optional, as long as there's a merged image.
                    Util.log("PSD: layer information is damaged (" + e.Message + "); layers ignored.");
                    Layers = new List<PsdLayer>();
                }
            }

            if (complete && layerMaskLength + 2 <= fileEnd - layerMaskStart)
                MergedOffset = layerMaskStart + layerMaskLength;
            else
                Util.log("PSD: file is truncated before the merged image data.");
        }

        /// <summary>
        /// Reads the image resources section, which is a series of blocks, each with a
        /// signature, an ID, a name, and data.
        /// </summary>
        private void ReadResources(long pos, long end)
        {
            var seen = new HashSet<int>();
            while (pos + 12 <= end)
            {
                Seek(pos);
                // Normally "8BIM", but PhotoDeluxe also writes some blocks of its own, of
                // unknown purpose, with the signature "PHUT".
                byte[] signature = ReadBytes(4);
                int id = ReadU16();
                int nameLength = ReadU8();
                // The name is a Pascal string, padded to an even length.
                Skip(nameLength + ((nameLength + 1) % 2));
                long size = ReadU32();
                long dataStart = Position;
                if (dataStart > end)
                    break;
                long dataLength = Math.Min(size, end - dataStart);

                // If a resource appears more than once, the first one counts.
                if (signature[0] == '8' && signature[1] == 'B' && signature[2] == 'I' && signature[3] == 'M' && seen.Add(id))
                {
                    if (id == ResourceTransparencyIndex && dataLength >= 2)
                    {
                        TransparencyIndex = ReadU16();
                    }
                    else if (id == ResourceVersionInfo && dataLength >= 5)
                    {
                        // A version number, followed by the flag that tells whether the
                        // merged image is complete, and then the names of the writer and
                        // the reader.
                        Skip(4);
                        HasRealMergedData = ReadU8() != 0;
                    }
                }

                // The data is padded to an even length.
                pos = dataStart + dataLength + (size % 2);
            }
        }

        /// <summary>
        /// Reads the layer and mask information section: the layer info (records and pixel
        /// data), the global layer mask info, and then a series of tagged blocks.
        /// </summary>
        private void ReadLayerAndMaskInfo(long start, long end)
        {
            Seek(start);
            long infoLength = ReadLength();
            long infoStart = Position;
            if (infoLength > fileEnd)
                throw new InvalidDataException("Layer info length is out of range.");
            if (infoLength > 0)
                ReadLayerInfo(infoStart, infoStart + infoLength);

            long pos = infoStart + infoLength;
            if (pos + 4 <= end)
            {
                // The global layer mask info, which doesn't affect rendering.
                Seek(pos);
                pos += 4 + ReadU32();
            }

            foreach (var block in ReadTaggedBlocks(pos, end))
            {
                // 16- and 32-bit documents leave the layer info above empty, and keep it in
                // a tagged block instead, with exactly the same structure.
                if ((block.Key == "Lr16" || block.Key == "Lr32" || block.Key == "Layr")
                    && Layers.Count == 0 && block.Length > 0)
                {
                    ReadLayerInfo(block.Offset, block.Offset + block.Length);
                }
            }
        }

        /// <summary>
        /// Reads a series of tagged blocks ("additional layer information"), each of which
        /// has a signature, a four-character key, a length, and data.
        /// </summary>
        private List<(string Key, long Offset, long Length)> ReadTaggedBlocks(long pos, long end)
        {
            var blocks = new List<(string, long, long)>();
            var peek = new byte[7];
            while (pos + 12 <= end)
            {
                Seek(pos);
                int peeked = stream.ReadAtLeast(peek, peek.Length, false);
                // Photoshop pads blocks to an even length (or a multiple of 4, outside of
                // layer records), and includes the padding in the block's length, but other
                // writers don't always. So resynchronize on the next signature.
                int skip = -1;
                for (int i = 0; i < 4 && i + 4 <= peeked; i++)
                {
                    if (IsBlockSignature(peek, i))
                    {
                        skip = i;
                        break;
                    }
                }
                if (skip < 0)
                    break;
                if (skip > 0)
                {
                    pos += skip;
                    continue;
                }

                Seek(pos + 4);
                string key = ReadKey();
                long length = IsPsb && PsbLongKeys.Contains(key) ? ReadU64() : ReadU32();
                long offset = Position;
                length = Math.Min(length, Math.Max(end - offset, 0));
                blocks.Add((key, offset, length));
                pos = offset + length;
            }
            return blocks;
        }

        private static bool IsBlockSignature(byte[] b, int i)
        {
            // "8BIM", or "8B64" for some blocks in PSB files.
            return b[i] == '8' && b[i + 1] == 'B'
                && ((b[i + 2] == 'I' && b[i + 3] == 'M') || (b[i + 2] == '6' && b[i + 3] == '4'));
        }

        /// <summary>
        /// Reads the layer info: a count, the layer records, and then the pixel data of every
        /// channel of every layer, in the same order as the records.
        /// </summary>
        private void ReadLayerInfo(long start, long end)
        {
            Seek(start);
            int count = ReadI16();
            // A negative count means that the first alpha channel of the merged image holds
            // its transparency.
            MergedTransparency = count < 0;
            count = Math.Abs(count);

            var layers = new List<PsdLayer>(count);
            for (int i = 0; i < count; i++)
                layers.Add(ReadLayerRecord());

            // The length of each channel includes the compression method that begins it, so
            // the channels can be located without decompressing any of them.
            long pos = Position;
            foreach (var layer in layers)
            {
                foreach (var channel in layer.Channels)
                {
                    channel.Offset = pos;
                    if (pos + 2 <= fileEnd)
                    {
                        Seek(pos);
                        channel.Compression = ReadU16();
                    }
                    pos = Math.Min(pos + channel.Length, long.MaxValue / 2);
                }
            }
            if (pos > end)
                Util.log("PSD: layer pixel data is longer than the layer info section.");
            if (pos > fileEnd)
                Util.log("PSD: file is truncated inside the layer pixel data.");
            Layers = layers;
        }

        private PsdLayer ReadLayerRecord()
        {
            var layer = new PsdLayer
            {
                Top = ReadI32(),
                Left = ReadI32(),
                Bottom = ReadI32(),
                Right = ReadI32()
            };

            int channelCount = ReadU16();
            for (int i = 0; i < channelCount; i++)
            {
                int id = ReadI16();
                layer.Channels.Add(new PsdChannel(id, ReadLength()));
            }

            byte[] signature = ReadBytes(4);
            if (signature[0] != '8' || signature[1] != 'B' || signature[2] != 'I' || signature[3] != 'M')
                Util.log("PSD: unexpected blend mode signature in a layer record.");
            layer.BlendMode = ReadKey();
            layer.Opacity = ReadU8();
            layer.Clipping = ReadU8();
            layer.Flags = ReadU8();
            ReadU8(); // filler

            long extraLength = ReadU32();
            long extraEnd = Position + extraLength;

            long maskLength = ReadU32();
            if (maskLength > extraEnd - Position)
                throw new InvalidDataException("Layer mask data runs past the end of its layer record.");
            ReadMasks(layer, ReadBytes(maskLength));

            // The "Blend If" sliders: a pair of ranges for the composite and for each
            // channel. 0000FFFF 0000FFFF is the full range, i.e. no effect.
            long rangesLength = ReadU32();
            if (rangesLength > extraEnd - Position)
                throw new InvalidDataException("Layer blending ranges run past the end of its layer record.");
            byte[] ranges = ReadBytes(rangesLength);
            for (int i = 0; i + 4 <= ranges.Length; i += 4)
            {
                if (ranges[i] != 0 || ranges[i + 1] != 0 || ranges[i + 2] != 0xFF || ranges[i + 3] != 0xFF)
                    layer.HasBlendRanges = true;
            }

            // A Pascal string, padded to a multiple of 4 bytes. A Unicode version of the name
            // may follow in a tagged block.
            int nameLength = ReadU8();
            layer.Name = Encoding.Latin1.GetString(ReadBytes(nameLength));
            Skip((4 - (1 + nameLength) % 4) % 4);

            foreach (var block in ReadTaggedBlocks(Position, extraEnd))
            {
                layer.BlockKeys.Add(block.Key);
                ReadLayerBlock(layer, block.Key, block.Offset, block.Length);
            }
            Seek(extraEnd);

            if (!IsSaneRect(layer.Top, layer.Left, layer.Bottom, layer.Right))
            {
                Util.log("PSD: layer \"" + layer.Name + "\" is too large, and was ignored.");
                layer.Right = layer.Left;
                layer.Bottom = layer.Top;
            }
            layer.Masks.RemoveAll(mask => !IsSaneRect(mask.Top, mask.Left, mask.Bottom, mask.Right));
            return layer;
        }

        private static bool IsSaneRect(int top, int left, int bottom, int right)
        {
            long width = Math.Max((long)right - left, 0), height = Math.Max((long)bottom - top, 0);
            return width <= MaxPixels && height <= MaxPixels && width * height <= MaxPixels;
        }

        /// <summary>
        /// Picks out the information we need from one of the tagged blocks in a layer record.
        /// </summary>
        private void ReadLayerBlock(PsdLayer layer, string key, long offset, long length)
        {
            Seek(offset);
            switch (key)
            {
                case "luni":
                    // The Unicode name of the layer: a count of UTF-16 code units, and the units.
                    if (length >= 4)
                    {
                        long count = ReadU32();
                        if (count * 2 <= length - 4)
                        {
                            string name = Encoding.BigEndianUnicode.GetString(ReadBytes(count * 2)).TrimEnd('\0');
                            if (name.Length > 0)
                                layer.Name = name;
                        }
                    }
                    break;
                case "lsct":
                case "lsdk":
                    // Section divider: this record opens or closes a group. It may carry the
                    // blend mode of the group, which takes precedence over the record's own.
                    if (length >= 4)
                    {
                        layer.Section = (int)Math.Min(ReadU32(), int.MaxValue);
                        if (length >= 12)
                        {
                            byte[] signature = ReadBytes(4);
                            if (signature[0] == '8' && signature[1] == 'B' && signature[2] == 'I' && signature[3] == 'M')
                                layer.BlendMode = ReadKey();
                        }
                    }
                    break;
                case "iOpa":
                    // Fill opacity, which applies to the layer's pixels, but not to its effects.
                    if (length >= 1)
                        layer.FillOpacity = ReadU8();
                    break;
            }
        }

        /// <summary>
        /// Reads the layer mask data of a layer record, which may describe up to two masks:
        /// the layer mask (channel -2), and, if the layer also has a vector mask, a "real"
        /// user mask (channel -3).
        /// </summary>
        private static void ReadMasks(PsdLayer layer, byte[] data)
        {
            if (data.Length < 18)
                return;
            layer.Masks.Add(new PsdMask(-2, ReadI32(data, 0), ReadI32(data, 4), ReadI32(data, 8), ReadI32(data, 12),
                                        data[16], data[17]));

            // If the layer has a channel -3, a second mask follows: its flags, its default
            // color, and its rectangle. The specification puts the optional mask parameters
            // (density and feather, present if bit 4 of the flags is set) ahead of this second
            // mask, but Photoshop writes them after it, which is where psd-tools, having been
            // tested against Photoshop's files, reads them from. We have no use for the
            // parameters themselves. Note that the parameters alone can make the record 36 bytes
            // or longer, so its length doesn't tell us whether the second mask is there.
            if (data.Length >= 36 && layer.FindChannel(-3) != null)
            {
                layer.Masks.Add(new PsdMask(-3, ReadI32(data, 20), ReadI32(data, 24), ReadI32(data, 28), ReadI32(data, 32),
                                            data[19], data[18]));
            }
        }

        /// <summary>
        /// Nests the flat list of layers into a tree of groups, bottom-most first.
        /// </summary>
        public List<PsdLayer> BuildLayerTree()
        {
            // Groups are flat in the file. Reading the records bottom up, an end-of-group
            // marker opens a group, the records that follow are its contents, and a group
            // record closes it, carrying the group's name, visibility, blend mode and so on.
            var root = new List<PsdLayer>();
            var stack = new List<List<PsdLayer>> { root };
            // Groups nested deeper than we're willing to go are flattened into their parent.
            int flattened = 0;
            foreach (var layer in Layers)
            {
                layer.Children = new List<PsdLayer>();
                if (layer.Section == PsdLayer.SectionGroupEnd)
                {
                    if (stack.Count <= MaxGroupDepth)
                        stack.Add(new List<PsdLayer>());
                    else
                        flattened++;
                }
                else if (layer.IsGroup)
                {
                    if (flattened > 0)
                        flattened--;
                    else if (stack.Count > 1)
                    {
                        layer.Children = stack[^1];
                        stack.RemoveAt(stack.Count - 1);
                    }
                    stack[^1].Add(layer);
                }
                else
                {
                    stack[^1].Add(layer);
                }
            }

            // A group that is never closed: keep its contents anyway.
            while (stack.Count > 1)
            {
                var orphans = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                stack[^1].AddRange(orphans);
            }
            return root;
        }

        /// <summary>
        /// Reads one channel of a layer, as 8-bit samples. The plane has the size of the
        /// layer's rectangle, or of the mask's rectangle in the case of a mask channel. A
        /// channel that is missing or can't be decoded comes back as all zeros.
        /// </summary>
        public byte[] ReadLayerChannel(PsdLayer layer, int channelId)
        {
            var channel = layer.FindChannel(channelId);
            var mask = layer.FindMask(channelId);
            int width = mask != null ? mask.Width : layer.Width;
            int height = mask != null ? mask.Height : layer.Height;
            if (channel == null || channel.Length < 2 || width == 0 || height == 0)
            {
                if (channel == null)
                    Util.log("PSD: layer \"" + layer.Name + "\" has no channel " + channelId + ".");
                return new byte[width * height];
            }

            int rowBytes = (int)(((long)width * Depth + 7) / 8);
            try
            {
                var planes = ReadPlanes(channel.Offset + 2, channel.Offset + channel.Length, channel.Compression,
                                        1, height, rowBytes, null);
                return To8Bit(planes[0]!, width, height, channelId < 0);
            }
            catch (ImageDecodeException e)
            {
                Util.log("PSD: layer \"" + layer.Name + "\" channel " + channelId + ": " + e.Message);
                return new byte[width * height];
            }
        }

        /// <summary>
        /// Reads the selected channels of the merged image, as 8-bit samples.
        /// </summary>
        /// <param name="wanted">Which channels to read, indexed by channel number.</param>
        /// <returns>The planes of the channels that were asked for, indexed by channel number,
        /// with null in place of the others.</returns>
        public byte[]?[] ReadMergedChannels(bool[] wanted)
        {
            if (MergedOffset < 0)
                throw new ImageDecodeException("This Photoshop file is truncated before its merged image data.");

            Seek(MergedOffset);
            int compression = ReadU16();
            int rowBytes = (int)(((long)Width * Depth + 7) / 8);
            var planes = ReadPlanes(MergedOffset + 2, fileEnd, compression, ChannelCount, Height, rowBytes, wanted);
            for (int i = 0; i < planes.Length; i++)
            {
                if (planes[i] != null)
                    planes[i] = To8Bit(planes[i]!, Width, Height, i >= ColorChannels);
            }
            return planes;
        }

        /// <summary>
        /// Decodes planes of raw samples that are stored one after another, all compressed
        /// with the same method. Data that is missing is filled with zeros.
        /// </summary>
        /// <param name="pos">Offset of the data, just past the compression method.</param>
        /// <param name="end">End of the data, for the methods that need to know it.</param>
        /// <param name="wanted">Which planes to decode, or null for all of them.</param>
        private byte[]?[] ReadPlanes(long pos, long end, int compression, int count, int rows, int rowBytes, bool[]? wanted)
        {
            var planes = new byte[]?[count];
            int size = rows * rowBytes;
            int last = count - 1;
            if (wanted != null)
                last = Array.LastIndexOf(wanted, true);

            switch (compression)
            {
                case CompressionRaw:
                    for (int i = 0; i <= last; i++)
                    {
                        if (wanted != null && !wanted[i])
                            continue;
                        planes[i] = new byte[size];
                        if (ReadAvailable(pos + (long)i * size, planes[i]!, 0, size) < size)
                            Util.log("PSD: image data is truncated.");
                    }
                    break;

                case CompressionRle:
                    ReadRlePlanes(planes, pos, rows, rowBytes, last, wanted);
                    break;

                case CompressionZip:
                case CompressionZipPredict:
                    ReadZipPlanes(planes, pos, end, compression == CompressionZipPredict, rows, rowBytes, last, wanted);
                    break;

                default:
                    throw new ImageDecodeException("Unsupported Photoshop compression method " + compression + ".");
            }
            return planes;
        }

        /// <summary>
        /// Decodes PackBits-compressed planes. The data begins with a table of the compressed
        /// length of every row of every plane, followed by the rows themselves.
        /// </summary>
        private void ReadRlePlanes(byte[]?[] planes, long pos, int rows, int rowBytes, int last, bool[]? wanted)
        {
            int entrySize = IsPsb ? 4 : 2;
            int tableSize = rows * entrySize;
            var table = new byte[tableSize];
            var rowLengths = new int[rows];
            long dataPos = pos + (long)planes.Length * tableSize;
            bool shortRows = false;

            for (int i = 0; i <= last; i++)
            {
                // The row lengths of this plane, and their sum, which tells us where the
                // next plane begins.
                Array.Clear(table);
                ReadAvailable(pos + (long)i * tableSize, table, 0, tableSize);
                long planeLength = 0;
                for (int y = 0; y < rows; y++)
                {
                    long length = IsPsb ? BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(y * 4))
                                        : BinaryPrimitives.ReadUInt16BigEndian(table.AsSpan(y * 2));
                    rowLengths[y] = (int)Math.Min(length, int.MaxValue);
                    planeLength += length;
                }

                if (wanted == null || wanted[i])
                {
                    var compressed = new byte[Math.Clamp(Math.Min(planeLength, fileEnd - dataPos), 0, int.MaxValue - 64)];
                    int available = ReadAvailable(dataPos, compressed, 0, compressed.Length);

                    var plane = new byte[rows * rowBytes];
                    int src = 0;
                    for (int y = 0; y < rows; y++)
                    {
                        int rowEnd = (int)Math.Min((long)src + rowLengths[y], available);
                        if (UnpackBits(compressed, src, rowEnd, plane, y * rowBytes, rowBytes) < rowBytes)
                            shortRows = true;
                        src = rowEnd;
                    }
                    planes[i] = plane;
                }
                dataPos += planeLength;
            }
            if (shortRows)
                Util.log("PSD: short or missing rows of RLE data padded with zeros.");
        }

        /// <summary>
        /// Decodes one row of PackBits (Macintosh RLE) data, returning the number of bytes
        /// written. A header byte n below 128 introduces n + 1 literal bytes, one above 128
        /// repeats the next byte 257 - n times, and 128 does nothing.
        /// </summary>
        private static int UnpackBits(byte[] src, int pos, int end, byte[] dst, int dstPos, int size)
        {
            int written = 0;
            while (pos < end && written < size)
            {
                int n = src[pos++];
                if (n < 128)
                {
                    int count = Math.Min(Math.Min(n + 1, end - pos), size - written);
                    Array.Copy(src, pos, dst, dstPos + written, count);
                    written += count;
                    pos += n + 1;
                }
                else if (n > 128)
                {
                    if (pos >= end)
                        break;
                    int count = Math.Min(257 - n, size - written);
                    dst.AsSpan(dstPos + written, count).Fill(src[pos++]);
                    written += count;
                }
            }
            return written;
        }

        /// <summary>
        /// Decodes planes compressed as a single zlib stream, optionally with each row
        /// delta-encoded.
        /// </summary>
        private void ReadZipPlanes(byte[]?[] planes, long pos, long end, bool predicted, int rows, int rowBytes, int last, bool[]? wanted)
        {
            var compressed = new byte[Math.Clamp(Math.Min(end, fileEnd) - pos, 0, int.MaxValue - 64)];
            int available = ReadAvailable(pos, compressed, 0, compressed.Length);
            int size = rows * rowBytes;

            using var inflater = new ZLibStream(new MemoryStream(compressed, 0, available), CompressionMode.Decompress);
            bool damaged = false;
            for (int i = 0; i <= last; i++)
            {
                var plane = new byte[size];
                if (!damaged)
                {
                    try
                    {
                        if (inflater.ReadAtLeast(plane, size, false) < size)
                            Util.log("PSD: ZIP-compressed image data is truncated.");
                    }
                    catch (InvalidDataException e)
                    {
                        Util.log("PSD: damaged ZIP-compressed image data (" + e.Message + ").");
                        damaged = true;
                    }
                }
                if (wanted != null && !wanted[i])
                    continue;
                if (predicted)
                    UndoPrediction(plane, rows, rowBytes);
                planes[i] = plane;
            }
        }

        /// <summary>
        /// Reverses the delta encoding of "ZIP with prediction", in which each sample of a row
        /// is stored as its difference from the previous one.
        /// </summary>
        private void UndoPrediction(byte[] plane, int rows, int rowBytes)
        {
            byte[]? row = Depth == 32 ? new byte[rowBytes] : null;
            for (int y = 0; y < rows; y++)
            {
                int start = y * rowBytes;
                if (Depth == 16)
                {
                    // Differences between big-endian 16-bit values.
                    int prev = 0;
                    for (int i = start; i + 1 < start + rowBytes; i += 2)
                    {
                        prev = (prev + ((plane[i] << 8) | plane[i + 1])) & 0xFFFF;
                        plane[i] = (byte)(prev >> 8);
                        plane[i + 1] = (byte)prev;
                    }
                    continue;
                }

                for (int i = start + 1; i < start + rowBytes; i++)
                    plane[i] += plane[i - 1];

                if (row != null)
                {
                    // 32-bit rows are split into four planes of bytes before they are
                    // delta-encoded: the first byte of every sample, then the second byte of
                    // every sample, and so on. Put the samples back together.
                    int n = rowBytes / 4;
                    for (int x = 0; x < n; x++)
                    {
                        for (int k = 0; k < 4; k++)
                            row[x * 4 + k] = plane[start + k * n + x];
                    }
                    Array.Copy(row, 0, plane, start, n * 4);
                }
            }
        }

        /// <summary>
        /// Converts a plane of samples at the document's bit depth to 8 bits per sample.
        /// </summary>
        /// <param name="linear">True if the plane holds transparency or a mask rather than a
        /// color, which matters only to 32-bit samples.</param>
        private byte[] To8Bit(byte[] raw, int width, int height, bool linear)
        {
            int count = width * height;
            switch (Depth)
            {
                case 8:
                    return raw;

                case 16:
                {
                    // Big-endian; keep the high byte.
                    var plane = new byte[count];
                    for (int i = 0; i < count; i++)
                        plane[i] = raw[i * 2];
                    return plane;
                }

                case 32:
                {
                    // Big-endian floats, in linear light, where 1.0 is white. Colors are
                    // encoded as sRGB, while transparency and masks are simply scaled.
                    var plane = new byte[count];
                    for (int i = 0; i < count; i++)
                    {
                        double v = BinaryPrimitives.ReadSingleBigEndian(raw.AsSpan(i * 4));
                        plane[i] = linear ? LinearToByte(v) : LinearToSrgbByte(v);
                    }
                    return plane;
                }

                default:
                {
                    // 1 bit per pixel, most significant bit first, rows padded to a byte. A set
                    // bit is black.
                    var plane = new byte[count];
                    int rowBytes = (width + 7) / 8;
                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            bool set = (raw[y * rowBytes + (x >> 3)] & (0x80 >> (x & 7))) != 0;
                            plane[y * width + x] = set ? (byte)0 : (byte)0xFF;
                        }
                    }
                    return plane;
                }
            }
        }

        private static byte LinearToByte(double v)
        {
            if (!(v > 0))
                return 0;
            return v >= 1 ? (byte)255 : (byte)(int)(v * 255 + 0.5);
        }

        /// <summary>
        /// Encodes a value in linear light, nominally between 0 and 1, as an 8-bit sRGB value.
        /// </summary>
        public static byte LinearToSrgbByte(double v)
        {
            if (!(v > 0)) // also catches NaN
                return 0;
            if (v >= 1)
                return 255;
            v = v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;
            return (byte)(int)(v * 255 + 0.5);
        }

        /// <summary>
        /// Converts the color channels of the document's color mode, as 8-bit planes, to RGB
        /// or grayscale planes (see DisplayChannels).
        /// </summary>
        public byte[][] ToDisplayColor(byte[][] planes)
        {
            switch (ColorMode)
            {
                case ModeRgb:
                    return new[] { planes[0], planes[1], planes[2] };
                case ModeCmyk:
                    return CmykToRgb(planes[0], planes[1], planes[2], planes[3]);
                case ModeLab:
                    return LabToRgb(planes[0], planes[1], planes[2]);
                case ModeIndexed:
                {
                    var rgb = new byte[3][];
                    for (int c = 0; c < 3; c++)
                    {
                        rgb[c] = new byte[planes[0].Length];
                        for (int i = 0; i < rgb[c].Length; i++)
                            rgb[c][i] = Palette[c * 256 + planes[0][i]];
                    }
                    return rgb;
                }
                case ModeDuotone:
                    // Adobe recommends showing a duotone image as grayscale, since the ink
                    // specification is undocumented.
                    Util.log("PSD: duotone image shown as grayscale.");
                    return new[] { planes[0] };
                case ModeMultichannel:
                    Util.log("PSD: multichannel image; only the first channel is shown, as grayscale.");
                    return new[] { planes[0] };
                default:
                    return new[] { planes[0] };
            }
        }

        private static byte[][] CmykToRgb(byte[] c, byte[] m, byte[] y, byte[] k)
        {
            // Photoshop stores CMYK inverted (255 means no ink), so this is the plain "white
            // minus ink" conversion, without regard to any ICC profile.
            var rgb = new byte[][] { new byte[c.Length], new byte[c.Length], new byte[c.Length] };
            for (int i = 0; i < c.Length; i++)
            {
                rgb[0][i] = (byte)((c[i] * k[i] + 127) / 255);
                rgb[1][i] = (byte)((m[i] * k[i] + 127) / 255);
                rgb[2][i] = (byte)((y[i] * k[i] + 127) / 255);
            }
            return rgb;
        }

        /// <summary>
        /// Converts CIE L*a*b* (D50) to sRGB.
        /// </summary>
        private static byte[][] LabToRgb(byte[] lp, byte[] ap, byte[] bp)
        {
            static double FInv(double t) => t > 6.0 / 29 ? Math.Pow(t, 3) : 3 * Math.Pow(6.0 / 29, 2) * (t - 4.0 / 29);

            var rgb = new byte[][] { new byte[lp.Length], new byte[lp.Length], new byte[lp.Length] };
            var cache = new Dictionary<int, int>();
            for (int i = 0; i < lp.Length; i++)
            {
                int key = (lp[i] << 16) | (ap[i] << 8) | bp[i];
                if (!cache.TryGetValue(key, out int packed))
                {
                    // L* is scaled to 0..255, and a* and b* are offset by 128.
                    double fy = (lp[i] * 100 / 255.0 + 16) / 116;
                    double x = 0.96422 * FInv(fy + (ap[i] - 128) / 500.0);
                    double y = FInv(fy);
                    double z = 0.82521 * FInv(fy - (bp[i] - 128) / 200.0);
                    // XYZ (D50) to linear sRGB, with Bradford adaptation to D65.
                    packed = (LinearToSrgbByte(3.1338561 * x - 1.6168667 * y - 0.4906146 * z) << 16)
                        | (LinearToSrgbByte(-0.9787684 * x + 1.9161415 * y + 0.0334540 * z) << 8)
                        | LinearToSrgbByte(0.0719453 * x - 0.2289914 * y + 1.4052427 * z);
                    cache[key] = packed;
                }
                rgb[0][i] = (byte)(packed >> 16);
                rgb[1][i] = (byte)(packed >> 8);
                rgb[2][i] = (byte)packed;
            }
            return rgb;
        }

        private static int ReadI32(byte[] data, int pos) => BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos));

        private long Position => stream.Position;

        /// <summary>
        /// Moves to the given position, or to the end of the stream if the position is beyond
        /// it (where a corrupt length would put it), so that the next read fails cleanly.
        /// </summary>
        private void Seek(long position) => stream.Seek(Math.Min(position, fileEnd), SeekOrigin.Begin);

        private void Skip(long count) => Seek(Position + count);

        private int ReadU8()
        {
            int b = stream.ReadByte();
            return b >= 0 ? b : throw new EndOfStreamException();
        }

        private int ReadU16()
        {
            stream.ReadExactly(scratch, 0, 2);
            return BinaryPrimitives.ReadUInt16BigEndian(scratch);
        }

        private int ReadI16()
        {
            stream.ReadExactly(scratch, 0, 2);
            return BinaryPrimitives.ReadInt16BigEndian(scratch);
        }

        private long ReadU32()
        {
            stream.ReadExactly(scratch, 0, 4);
            return BinaryPrimitives.ReadUInt32BigEndian(scratch);
        }

        private int ReadI32()
        {
            stream.ReadExactly(scratch, 0, 4);
            return BinaryPrimitives.ReadInt32BigEndian(scratch);
        }

        private long ReadU64()
        {
            stream.ReadExactly(scratch, 0, 8);
            ulong value = BinaryPrimitives.ReadUInt64BigEndian(scratch);
            // No length in a real file comes anywhere near this, and keeping well away from
            // the limit of a long means that offsets can be added without overflowing.
            if (value > long.MaxValue / 4)
                throw new InvalidDataException("Length is out of range.");
            return (long)value;
        }

        /// <summary>
        /// Reads a length field that is 4 bytes wide in a PSD file and 8 bytes in a PSB file.
        /// </summary>
        private long ReadLength() => IsPsb ? ReadU64() : ReadU32();

        private string ReadKey()
        {
            stream.ReadExactly(scratch, 0, 4);
            return Encoding.Latin1.GetString(scratch, 0, 4);
        }

        private byte[] ReadBytes(long count)
        {
            if (count > MaxFieldLength)
                throw new InvalidDataException("Field length is out of range.");
            if (count > fileEnd - Position)
                throw new EndOfStreamException();
            var bytes = new byte[count];
            stream.ReadExactly(bytes);
            return bytes;
        }

        /// <summary>
        /// Reads up to count bytes at the given position, as many as the stream has, and
        /// returns how many were read.
        /// </summary>
        private int ReadAvailable(long position, byte[] buffer, int offset, int count)
        {
            if (position >= fileEnd)
                return 0;
            Seek(position);
            return stream.ReadAtLeast(buffer.AsSpan(offset, count), count, false);
        }
    }

    /// <summary>
    /// A layer record. Groups are layer records too, as are the markers that end them.
    /// </summary>
    internal sealed class PsdLayer
    {
        // Values of the section divider setting.
        public const int SectionGroupOpen = 1;
        public const int SectionGroupClosed = 2;
        public const int SectionGroupEnd = 3;

        public string Name = "";
        public int Top;
        public int Left;
        public int Bottom;
        public int Right;
        public List<PsdChannel> Channels = new();
        public List<PsdMask> Masks = new();
        public string BlendMode = "norm";
        public int Opacity = 255;
        public int FillOpacity = 255;
        /// <summary>0 for a base layer, 1 for a layer clipped to the layer below it.</summary>
        public int Clipping;
        public int Flags;
        public int Section;
        /// <summary>True if the layer has "Blend If" ranges other than the full range.</summary>
        public bool HasBlendRanges;
        /// <summary>The keys of the tagged blocks in the layer record.</summary>
        public HashSet<string> BlockKeys = new();
        /// <summary>The contents of a group, bottom-most first, once the layer tree is built.</summary>
        public List<PsdLayer> Children = new();

        public int Width => (int)Math.Max((long)Right - Left, 0);
        public int Height => (int)Math.Max((long)Bottom - Top, 0);
        public bool Hidden => (Flags & 0x02) != 0;
        public bool IsGroup => Section == SectionGroupOpen || Section == SectionGroupClosed;

        public PsdChannel? FindChannel(int id) => Channels.Find(c => c.Id == id);
        public PsdMask? FindMask(int id) => Masks.Find(m => m.ChannelId == id);
    }

    /// <summary>
    /// One channel of a layer. Channels 0 and up are color channels, -1 is transparency, and
    /// -2 and -3 are masks.
    /// </summary>
    internal sealed class PsdChannel
    {
        public readonly int Id;
        /// <summary>Length of the channel's data, including its compression method.</summary>
        public readonly long Length;
        /// <summary>Offset of the channel's data in the stream, starting with its compression method.</summary>
        public long Offset;
        public int Compression;

        public PsdChannel(int id, long length)
        {
            Id = id;
            Length = length;
        }
    }

    /// <summary>
    /// A layer mask, whose rectangle is in canvas coordinates. Outside its rectangle, the mask
    /// has its default color.
    /// </summary>
    internal sealed class PsdMask
    {
        public readonly int ChannelId;
        public readonly int Top;
        public readonly int Left;
        public readonly int Bottom;
        public readonly int Right;
        public readonly int DefaultColor;
        public readonly int Flags;

        public PsdMask(int channelId, int top, int left, int bottom, int right, int defaultColor, int flags)
        {
            ChannelId = channelId;
            Top = top;
            Left = left;
            Bottom = bottom;
            Right = right;
            DefaultColor = defaultColor;
            Flags = flags;
        }

        public bool Disabled => (Flags & 0x02) != 0;
        public int Width => (int)Math.Max((long)Right - Left, 0);
        public int Height => (int)Math.Max((long)Bottom - Top, 0);
    }
}
