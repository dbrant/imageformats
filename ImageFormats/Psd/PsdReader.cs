using System;
using System.IO;

/*

Decoder for Adobe Photoshop (.PSD, .PSB) images, and for Adobe PhotoDeluxe (.PDD)
images, which are Photoshop files by another name.

A Photoshop file has five consecutive sections, with all numbers big-endian:

  * A 26-byte header: the signature "8BPS", the version (1 for PSD, 2 for the
    "large document" PSB variant), the number of channels, the height and width,
    the number of bits per channel (1, 8, 16 or 32), and the color mode (bitmap,
    grayscale, indexed, RGB, CMYK, multichannel, duotone or Lab).

  * Color mode data: the palette of an indexed image, or the (undocumented) ink
    specification of a duotone one.

  * Image resources: blocks of metadata, of which two concern us. Resource 1047
    names a transparent palette index, and resource 1057 says, among other things,
    whether the merged image (see below) is the real picture.

  * Layer and mask information: the layer records, then the pixel data of every
    channel of every layer, and then any number of tagged blocks. 16- and 32-bit
    documents keep their layers in one of those tagged blocks ("Lr16", "Lr32")
    rather than in the usual place.

  * The merged image: the whole picture, flattened, as Photoshop rendered it. Its
    color channels may be followed by a transparency channel, and then by alpha
    channels (saved selections) and spot color channels, which we ignore.

PSB files are the same, except that some of their length fields are 64 bits wide
instead of 32, and the row lengths of RLE-compressed data are 32 bits wide instead
of 16.

Every channel is stored as a separate plane, either uncompressed, as PackBits RLE
(with a table of the compressed length of every row up front), or as a zlib
stream, optionally with each row delta-encoded ("ZIP with prediction").

The merged image is the best source of the picture when it's genuine, since it
includes layer effects, adjustment layers, text and vector shapes, none of which can
be reproduced from the pixels of the layers alone. But it isn't always genuine.
PhotoDeluxe writes a "Photoshop 3.0 is required to open this file" banner in its
place, and Photoshop leaves it blank white when "Maximize Compatibility" is turned
off. So we use the merged image unless resource 1057 says it isn't real, or (in
files too old to have that resource) it consists only of pure black and white, with
all color channels identical, which is what both placeholders look like. Otherwise,
the layers are composited (see PsdCompositor.cs).

When a document with transparency is flattened into the merged image, Photoshop
composites it against white first, so we reverse that to recover the real colors.

Everything is reduced to 8 bits per channel, and to RGB or grayscale. 16-bit samples
keep their high byte. 32-bit samples are floating-point values in linear light, and
are converted to sRGB. CMYK and Lab are converted with fixed formulas, without
regard to any ICC profile. Duotone and multichannel images are shown in grayscale
(the first channel, in the case of multichannel).

Everything here follows Adobe's published Photoshop file format specification,
except where files in the wild contradict it.

Copyright 2026+ Dmitry Brant
https://dmitrybrant.com

License: MIT
*/

namespace DmitryBrant.ImageFormats
{
    /// <summary>
    /// Which image data of a Photoshop file to take the picture from.
    /// </summary>
    public enum PsdImageSource
    {
        /// <summary>
        /// The merged image, unless the file has layers and the merged image is missing or
        /// only a placeholder, in which case the layers are composited.
        /// </summary>
        Auto,

        /// <summary>
        /// The merged image that Photoshop stores at the end of the file: its own rendering
        /// of the picture, including layer effects, adjustment layers and so on.
        /// </summary>
        Merged,

        /// <summary>
        /// The layers, composited. Layer effects, adjustment and fill layers, vector masks
        /// and "Blend If" ranges are not rendered.
        /// </summary>
        Layers
    }

    /// <summary>
    /// Handles reading Adobe Photoshop (.PSD, .PSB) and Adobe PhotoDeluxe (.PDD) images.
    /// </summary>
    public static class PsdReader
    {
        /// <summary>
        /// Reads a Photoshop (.PSD, .PSB) or PhotoDeluxe (.PDD) image from a file.
        /// </summary>
        /// <param name="fileName">Name of the file to read.</param>
        /// <param name="source">Which image data in the file to take the picture from.</param>
        /// <returns>ImageData that contains the image that was read.</returns>
        public static ImageData Load(string fileName, PsdImageSource source = PsdImageSource.Auto)
        {
            using var f = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Load(f, source);
        }

        /// <summary>
        /// Reads a Photoshop (.PSD, .PSB) or PhotoDeluxe (.PDD) image from a stream.
        /// </summary>
        /// <param name="stream">Stream from which to read the image. It must be seekable.</param>
        /// <param name="source">Which image data in the file to take the picture from.</param>
        /// <returns>ImageData that contains the image that was read.</returns>
        public static ImageData Load(Stream stream, PsdImageSource source = PsdImageSource.Auto)
        {
            var doc = PsdDocument.Read(stream);
            switch (source)
            {
                case PsdImageSource.Merged:
                    return RenderMerged(doc, ReadMergedPlanes(doc));
                case PsdImageSource.Layers:
                    if (doc.Layers.Count == 0)
                        throw new ImageDecodeException("This Photoshop file has no layers.");
                    return RenderLayers(doc);
                default:
                    return RenderAuto(doc);
            }
        }

        internal static bool HasSignature(byte[] header)
        {
            return header.Length >= 6 && header[0] == '8' && header[1] == 'B' && header[2] == 'P' && header[3] == 'S'
                && header[4] == 0 && (header[5] == 1 || header[5] == 2);
        }

        private static ImageData RenderAuto(PsdDocument doc)
        {
            if (!doc.Layers.Exists(layer => layer.Width > 0 && layer.Height > 0))
                return RenderMerged(doc, ReadMergedPlanes(doc));
            if (doc.MergedOffset < 0)
                return RenderLayers(doc);

            // Photoshop 6 and later tell us outright whether the merged image is real.
            if (doc.HasRealMergedData.HasValue)
                return doc.HasRealMergedData.Value ? RenderMerged(doc, ReadMergedPlanes(doc)) : RenderLayers(doc);

            byte[]?[] planes;
            try
            {
                planes = ReadMergedPlanes(doc);
            }
            catch (ImageDecodeException e)
            {
                Util.log("PSD: " + e.Message + " Compositing the layers instead.");
                return RenderLayers(doc);
            }
            if (LooksLikePlaceholder(doc, planes))
            {
                Util.log("PSD: the merged image is only a placeholder. Compositing the layers instead.");
                return RenderLayers(doc);
            }
            return RenderMerged(doc, planes);
        }

        /// <summary>
        /// True if the merged image consists only of pure black and white, identical in all
        /// color channels, like the "Photoshop 3.0 is required" banner that PhotoDeluxe stores
        /// in place of the picture, or the blank page that Photoshop stores when "Maximize
        /// Compatibility" is turned off.
        /// </summary>
        private static bool LooksLikePlaceholder(PsdDocument doc, byte[]?[] planes)
        {
            if ((doc.ColorMode != PsdDocument.ModeRgb && doc.ColorMode != PsdDocument.ModeGrayscale) || doc.Depth != 8)
                return false;
            var first = planes[0]!;
            for (int c = 1; c < doc.ColorChannels; c++)
            {
                if (!first.AsSpan().SequenceEqual(planes[c]))
                    return false;
            }
            return !first.AsSpan().ContainsAnyExcept((byte)0, (byte)0xFF);
        }

        /// <summary>
        /// Reads the channels of the merged image that we need: the color channels that are
        /// shown, and the transparency channel, if there is one.
        /// </summary>
        private static byte[]?[] ReadMergedPlanes(PsdDocument doc)
        {
            var wanted = new bool[doc.ChannelCount];
            for (int c = 0; c < ShownChannels(doc); c++)
                wanted[c] = true;
            if (HasMergedTransparency(doc))
                wanted[doc.ColorChannels] = true;
            return doc.ReadMergedChannels(wanted);
        }

        private static int ShownChannels(PsdDocument doc)
        {
            return doc.ColorMode == PsdDocument.ModeMultichannel ? 1 : doc.ColorChannels;
        }

        private static bool HasMergedTransparency(PsdDocument doc)
        {
            return doc.MergedTransparency && doc.ChannelCount > doc.ColorChannels;
        }

        private static ImageData RenderMerged(PsdDocument doc, byte[]?[] planes)
        {
            var colorPlanes = new byte[ShownChannels(doc)][];
            for (int c = 0; c < colorPlanes.Length; c++)
                colorPlanes[c] = planes[c]!;
            var color = doc.ToDisplayColor(colorPlanes);

            byte[]? alpha = null;
            if (HasMergedTransparency(doc))
            {
                alpha = planes[doc.ColorChannels]!;
                // Photoshop composites the merged image of a transparent document against
                // white, so undo that to recover the real colors:
                // color = (stored - 255 * (1 - alpha)) / alpha.
                foreach (var plane in color)
                {
                    for (int i = 0; i < plane.Length; i++)
                    {
                        int a = alpha[i];
                        plane[i] = a == 0 ? (byte)0 : (byte)Math.Clamp(((plane[i] - 255 + a) * 255 + a / 2) / a, 0, 255);
                    }
                }
            }
            else if (doc.ColorMode == PsdDocument.ModeIndexed && doc.TransparencyIndex >= 0)
            {
                alpha = new byte[colorPlanes[0].Length];
                for (int i = 0; i < alpha.Length; i++)
                    alpha[i] = colorPlanes[0][i] == doc.TransparencyIndex ? (byte)0 : (byte)0xFF;
            }
            return ToImageData(doc, color, alpha);
        }

        private static ImageData RenderLayers(PsdDocument doc)
        {
            var canvas = new PsdCompositor(doc).Render();
            return ToImageData(doc, canvas.Color, canvas.Alpha);
        }

        /// <summary>
        /// Interleaves RGB or grayscale planes, plus an optional alpha plane, into BGRA pixels.
        /// </summary>
        private static ImageData ToImageData(PsdDocument doc, byte[][] color, byte[]? alpha)
        {
            int count = doc.Width * doc.Height;
            var bmpData = new byte[count * 4];
            byte[] r = color[0], g = color.Length == 3 ? color[1] : color[0], b = color.Length == 3 ? color[2] : color[0];
            for (int i = 0, p = 0; i < count; i++, p += 4)
            {
                bmpData[p] = b[i];
                bmpData[p + 1] = g[i];
                bmpData[p + 2] = r[i];
                bmpData[p + 3] = alpha != null ? alpha[i] : (byte)0xFF;
            }
            return Util.LoadRgba(doc.Width, doc.Height, bmpData);
        }
    }
}
