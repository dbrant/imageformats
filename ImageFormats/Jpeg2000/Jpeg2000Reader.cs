using System;
using System.Collections.Generic;
using System.IO;

/*

Decoder for JPEG 2000 image files: JP2 (and the JP2-compatible parts of JPX)
files, as well as bare codestreams (.j2k, .j2c, .jpc).

A JP2 file is a sequence of "boxes", each starting with a length and a
four-character type. The signature box ("jP  ") and file type box ("ftyp")
come first, followed by the JP2 header box ("jp2h"), which contains the
image header ("ihdr"), the color space ("colr"), and optionally a palette
("pclr"), a component mapping ("cmap") that says which components go through
the palette, and a channel definition box ("cdef") that says which channel is
which (e.g. which one is the alpha channel). The contiguous codestream box
("jp2c") contains the actual compressed image, which is decoded by
Jpeg2000Decoder.

Copyright 2026+ Dmitry Brant
https://dmitrybrant.com

License: MIT
*/

namespace DmitryBrant.ImageFormats
{
    /// <summary>
    /// Handles reading JPEG 2000 images.
    /// </summary>
    public static class Jpeg2000Reader
    {
        // Enumerated color spaces of the colr box
        private const int ColorSpaceUnknown = -1;
        private const int ColorSpaceCmyk = 12;
        private const int ColorSpaceSrgb = 16;
        private const int ColorSpaceGray = 17;
        private const int ColorSpaceSycc = 18;
        private const int ColorSpaceYpbpr1125 = 22;
        private const int ColorSpaceYpbpr1250 = 23;
        private const int ColorSpaceEsycc = 24;

        // Channel types of the cdef box
        private const int ChannelColor = 0;
        private const int ChannelOpacity = 1;
        private const int ChannelPremultipliedOpacity = 2;

        private sealed class Palette
        {
            public int Entries, Columns;
            public int[] Precisions = Array.Empty<int>();
            public bool[] Signed = Array.Empty<bool>();
            public int[] Values = Array.Empty<int>();   // Entries * Columns
        }

        private sealed class Header
        {
            public int ColorSpace = ColorSpaceUnknown;
            public Palette? Palette;
            public List<(int Component, int Type, int Column)>? ComponentMap;
            public List<(int Channel, int Type, int Association)>? ChannelDefs;
        }

        /// <summary>
        /// A channel of the output image, and how to get its value.
        /// </summary>
        private sealed class Channel
        {
            public int Component;
            public int PaletteColumn = -1;

            // Maps the (offset) value of the channel to 8 bits, for precisions up to 16 bits.
            public byte[]? Lookup;
            public int Precision;
            public bool Signed;
        }

        /// <summary>
        /// Reads a JPEG 2000 image from a file.
        /// </summary>
        /// <param name="fileName">Name of the file to read.</param>
        /// <returns>ImageData that contains the image that was read.</returns>
        public static ImageData? Load(string fileName)
        {
            using var f = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Load(f);
        }

        /// <summary>
        /// Reads a JPEG 2000 image from a stream.
        /// </summary>
        /// <param name="stream">Stream from which to read the image.</param>
        /// <returns>ImageData that contains the image that was read.</returns>
        public static ImageData? Load(Stream stream)
        {
            byte[] data;
            using (var ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                data = ms.ToArray();
            }

            try
            {
                var header = new Header();
                int start = 0, end = data.Length;
                if (HasJp2Signature(data))
                    ReadBoxes(data, header, out start, out end);
                else if (!HasCodestreamSignature(data))
                    throw new ImageDecodeException("Not a valid JPEG 2000 file.");

                var image = Jpeg2000Decoder.Decode(data, start, end);
                return Render(image, header);
            }
            catch (Exception e) when (e is IndexOutOfRangeException || e is ArgumentException || e is OverflowException)
            {
                throw new ImageDecodeException("Invalid JPEG 2000 file: " + e.Message);
            }
        }

        internal static bool HasJp2Signature(byte[] header)
        {
            return header.Length >= 12 && header[0] == 0 && header[1] == 0 && header[2] == 0 && header[3] == 12
                && header[4] == 'j' && header[5] == 'P' && header[6] == ' ' && header[7] == ' '
                && header[8] == 0x0D && header[9] == 0x0A && header[10] == 0x87 && header[11] == 0x0A;
        }

        internal static bool HasCodestreamSignature(byte[] header)
        {
            // SOC marker, immediately followed by SIZ.
            return header.Length >= 4 && header[0] == 0xFF && header[1] == 0x4F && header[2] == 0xFF && header[3] == 0x51;
        }

        private static uint ReadU32(byte[] d, long p)
        {
            return (uint)((d[p] << 24) | (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3]);
        }

        private static int ReadU16(byte[] d, long p)
        {
            return (d[p] << 8) | d[p + 1];
        }

        /// <summary>
        /// Walks the boxes in the given range, collecting header information, and finds
        /// the codestream.
        /// </summary>
        private static void ReadBoxes(byte[] data, Header header, out int codestreamStart, out int codestreamEnd)
        {
            codestreamStart = codestreamEnd = -1;
            WalkBoxes(data, 0, data.Length, header, ref codestreamStart, ref codestreamEnd);
            if (codestreamStart < 0)
                throw new ImageDecodeException("No codestream found in JPEG 2000 file.");
        }

        private static void WalkBoxes(byte[] data, long pos, long end, Header header, ref int codestreamStart, ref int codestreamEnd)
        {
            while (pos + 8 <= end)
            {
                long len = ReadU32(data, pos);
                string type = System.Text.Encoding.ASCII.GetString(data, (int)pos + 4, 4);
                int headerLen = 8;
                if (len == 1)
                {
                    if (pos + 16 > end)
                        break;
                    len = ((long)ReadU32(data, pos + 8) << 32) | ReadU32(data, pos + 12);
                    headerLen = 16;
                }
                else if (len == 0)
                {
                    // The last box in the file.
                    len = end - pos;
                }
                if (len < headerLen)
                    break;
                long boxEnd = Math.Min(pos + len, end);
                long body = pos + headerLen;

                switch (type)
                {
                    case "jp2h":
                    case "jpch": // JPX codestream header
                        WalkBoxes(data, body, boxEnd, header, ref codestreamStart, ref codestreamEnd);
                        break;
                    case "colr":
                        ReadColorSpace(data, body, boxEnd, header);
                        break;
                    case "pclr":
                        header.Palette = ReadPalette(data, body, boxEnd);
                        break;
                    case "cmap":
                        header.ComponentMap = new List<(int, int, int)>();
                        for (long p = body; p + 4 <= boxEnd; p += 4)
                            header.ComponentMap.Add((ReadU16(data, p), data[p + 2], data[p + 3]));
                        break;
                    case "cdef":
                        if (body + 2 <= boxEnd)
                        {
                            int count = ReadU16(data, body);
                            header.ChannelDefs = new List<(int, int, int)>();
                            for (int i = 0; i < count && body + 2 + i * 6 + 6 <= boxEnd; i++)
                            {
                                long p = body + 2 + i * 6;
                                header.ChannelDefs.Add((ReadU16(data, p), ReadU16(data, p + 2), ReadU16(data, p + 4)));
                            }
                        }
                        break;
                    case "jp2c":
                        if (codestreamStart < 0)
                        {
                            codestreamStart = (int)body;
                            codestreamEnd = (int)boxEnd;
                        }
                        break;
                }
                pos = boxEnd;
            }
        }

        private static void ReadColorSpace(byte[] data, long body, long end, Header header)
        {
            // There can be several colr boxes; the first one we understand wins.
            if (header.ColorSpace != ColorSpaceUnknown || body + 3 > end)
                return;
            int method = data[body];
            if (method == 1 && body + 7 <= end)
                header.ColorSpace = (int)ReadU32(data, body + 3);
            // Otherwise it's an ICC profile, which we don't interpret; the number of
            // channels will have to do.
        }

        private static Palette? ReadPalette(byte[] data, long body, long end)
        {
            if (body + 3 > end)
                return null;
            var pal = new Palette { Entries = ReadU16(data, body), Columns = data[body + 2] };
            if (pal.Entries == 0 || pal.Columns == 0 || body + 3 + pal.Columns > end)
                return null;
            pal.Precisions = new int[pal.Columns];
            pal.Signed = new bool[pal.Columns];
            var sizes = new int[pal.Columns];
            for (int i = 0; i < pal.Columns; i++)
            {
                int b = data[body + 3 + i];
                pal.Precisions[i] = Math.Min((b & 0x7F) + 1, 32);
                pal.Signed[i] = (b & 0x80) != 0;
                sizes[i] = (pal.Precisions[i] + 7) / 8;
            }
            pal.Values = new int[pal.Entries * pal.Columns];
            long p = body + 3 + pal.Columns;
            for (int e = 0; e < pal.Entries; e++)
            {
                for (int c = 0; c < pal.Columns; c++)
                {
                    if (p + sizes[c] > end)
                        return pal;
                    long v = 0;
                    for (int k = 0; k < sizes[c]; k++)
                        v = (v << 8) | data[p + k];
                    p += sizes[c];
                    if (pal.Signed[c] && pal.Precisions[c] < 32 && (v & (1L << (pal.Precisions[c] - 1))) != 0)
                        v -= 1L << pal.Precisions[c];
                    pal.Values[e * pal.Columns + c] = (int)v;
                }
            }
            return pal;
        }

        /// <summary>
        /// Works out the channels of the image: components of the codestream, possibly
        /// mapped through a palette.
        /// </summary>
        private static List<Channel> BuildChannels(CodecImage image, Header header)
        {
            var channels = new List<Channel>();
            var pal = header.Palette;
            if (pal != null && header.ComponentMap != null && header.ComponentMap.Count > 0)
            {
                foreach (var (component, type, column) in header.ComponentMap)
                {
                    if (component >= image.Components)
                        throw new ImageDecodeException("JPEG 2000 component mapping refers to a missing component.");
                    if (type == 1 && column < pal.Columns)
                        channels.Add(new Channel { Component = component, PaletteColumn = column, Precision = pal.Precisions[column], Signed = pal.Signed[column] });
                    else
                        channels.Add(new Channel { Component = component, Precision = image.ComponentPrecisions[component], Signed = image.ComponentSigned[component] });
                }
            }
            else
            {
                for (int c = 0; c < image.Components; c++)
                    channels.Add(new Channel { Component = c, Precision = image.ComponentPrecisions[c], Signed = image.ComponentSigned[c] });
            }

            foreach (var ch in channels)
            {
                if (ch.Precision <= 16)
                {
                    int count = 1 << ch.Precision;
                    int max = count - 1;
                    ch.Lookup = new byte[count];
                    for (int i = 0; i < count; i++)
                        ch.Lookup[i] = (byte)((i * 255 + max / 2) / Math.Max(max, 1));
                }
            }
            return channels;
        }

        /// <summary>
        /// Gets the 8-bit value of a channel at the given pixel.
        /// </summary>
        private static int ChannelValue(Channel ch, CodecImage image, Palette? pal, int pixel)
        {
            long v = image.Samples[pixel * image.Components + ch.Component];
            if (ch.PaletteColumn >= 0)
            {
                // The palette index is the unsigned value of the component.
                if (image.ComponentSigned[ch.Component])
                    v += 1L << (image.ComponentPrecisions[ch.Component] - 1);
                int index = (int)Math.Clamp(v, 0, pal!.Entries - 1);
                v = pal.Values[index * pal.Columns + ch.PaletteColumn];
            }
            if (ch.Signed)
                v += 1L << (ch.Precision - 1);
            long max = (1L << ch.Precision) - 1;
            v = Math.Clamp(v, 0, max);
            return ch.Lookup != null ? ch.Lookup[v] : (int)(v * 255 / max);
        }

        private static ImageData Render(CodecImage image, Header header)
        {
            var channels = BuildChannels(image, header);
            int numChannels = channels.Count;

            // How many color channels the color space calls for.
            int colorSpace = header.ColorSpace;
            int numColors = colorSpace switch
            {
                ColorSpaceGray => 1,
                ColorSpaceCmyk => 4,
                ColorSpaceSrgb or ColorSpaceSycc or ColorSpaceEsycc or ColorSpaceYpbpr1125 or ColorSpaceYpbpr1250 => 3,
                _ => numChannels >= 3 ? 3 : 1
            };
            if (numChannels < numColors)
            {
                // Not enough channels for the stated color space; make the best of it.
                numColors = numChannels >= 3 ? 3 : 1;
                colorSpace = ColorSpaceUnknown;
            }

            // Assign channels to colors and alpha, according to the channel definitions if
            // there are any, or else in order, with an extra channel taken to be alpha.
            var colorChannels = new int[numColors];
            for (int i = 0; i < numColors; i++)
                colorChannels[i] = i;
            int alphaChannel = numChannels > numColors ? numColors : -1;
            bool premultiplied = false;
            if (header.ChannelDefs != null && header.ChannelDefs.Count > 0)
            {
                var assigned = new int[numColors];
                Array.Fill(assigned, -1);
                int alpha = -1;
                foreach (var (channel, type, association) in header.ChannelDefs)
                {
                    if (channel >= numChannels)
                        continue;
                    if (type == ChannelColor && association >= 1 && association <= numColors)
                        assigned[association - 1] = channel;
                    else if ((type == ChannelOpacity || type == ChannelPremultipliedOpacity) && association == 0 && alpha < 0)
                    {
                        alpha = channel;
                        premultiplied = type == ChannelPremultipliedOpacity;
                    }
                }
                if (Array.IndexOf(assigned, -1) < 0)
                {
                    colorChannels = assigned;
                    alphaChannel = alpha;
                }
            }

            int width = image.Width, height = image.Height;
            var pal = header.Palette;
            var bgra = new byte[(long)width * height * 4];
            bool ycc = colorSpace is ColorSpaceSycc or ColorSpaceEsycc or ColorSpaceYpbpr1125 or ColorSpaceYpbpr1250;
            var ch0 = channels[colorChannels[0]];
            var ch1 = numColors > 1 ? channels[colorChannels[1]] : ch0;
            var ch2 = numColors > 2 ? channels[colorChannels[2]] : ch0;
            var ch3 = numColors > 3 ? channels[colorChannels[3]] : ch0;
            var chAlpha = alphaChannel >= 0 ? channels[alphaChannel] : null;

            for (int i = 0, o = 0; i < width * height; i++, o += 4)
            {
                int r, g, b;
                int c0 = ChannelValue(ch0, image, pal, i);
                if (numColors == 1)
                {
                    r = g = b = c0;
                }
                else
                {
                    int c1 = ChannelValue(ch1, image, pal, i), c2 = ChannelValue(ch2, image, pal, i);
                    if (ycc)
                    {
                        r = c0 + (int)Math.Round(1.402 * (c2 - 128));
                        g = c0 - (int)Math.Round(0.344136 * (c1 - 128) + 0.714136 * (c2 - 128));
                        b = c0 + (int)Math.Round(1.772 * (c1 - 128));
                    }
                    else if (numColors == 4)
                    {
                        int k = 255 - ChannelValue(ch3, image, pal, i);
                        r = (255 - c0) * k / 255;
                        g = (255 - c1) * k / 255;
                        b = (255 - c2) * k / 255;
                    }
                    else
                    {
                        r = c0;
                        g = c1;
                        b = c2;
                    }
                }

                int a = chAlpha != null ? ChannelValue(chAlpha, image, pal, i) : 255;
                if (premultiplied && a > 0 && a < 255)
                {
                    r = r * 255 / a;
                    g = g * 255 / a;
                    b = b * 255 / a;
                }
                bgra[o] = (byte)Math.Clamp(b, 0, 255);
                bgra[o + 1] = (byte)Math.Clamp(g, 0, 255);
                bgra[o + 2] = (byte)Math.Clamp(r, 0, 255);
                bgra[o + 3] = (byte)a;
            }
            return Util.LoadRgba(width, height, bgra);
        }
    }
}
