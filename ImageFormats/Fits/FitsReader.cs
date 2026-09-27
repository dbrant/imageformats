using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

/*

Decoder for FITS (Flexible Image Transport System) images.

A FITS file is a sequence of "HDUs" (header and data units). Each header is a
series of 80-character ASCII "cards" of the form KEYWORD = value / comment,
padded to a multiple of 2880 bytes and terminated by an END card, and describes
the data that follows it (also padded to a multiple of 2880 bytes). The first
(primary) HDU starts with SIMPLE = T, and any further ones ("extensions") with
XTENSION = 'type'. Images can be in the primary HDU or in IMAGE extensions, and
are N-dimensional arrays of big-endian integers or IEEE floats (BITPIX = 8, 16,
32, 64, -32, -64), stored with the first axis varying fastest. The physical
value of each element is BZERO + BSCALE * stored value, and integer elements
equal to BLANK are undefined, as are NaN floating-point elements.

Each 2-D plane of an image (and each image extension) is treated as a frame,
except that a cube of exactly three planes is taken to be an RGB color image.
Since the data is usually scientific rather than meant for display, it's
stretched linearly between the 0.25th and 99.75th percentiles of its values
(as most astronomy software does by default), unless it's an 8-bit RGB image.
Tile-compressed images (.fz files) are also supported; see FitsTileCompression. By
convention, the first row of a FITS image is at the bottom.

Copyright 2019+ Dmitry Brant.
https://dmitrybrant.com

License: MIT
*/

namespace DmitryBrant.ImageFormats
{
    /// <summary>
    /// A header and data unit of a FITS file.
    /// </summary>
    internal sealed class FitsHdu
    {
        public readonly Dictionary<string, string> Cards = new();
        public long DataStart;
        public long DataSize;

        /// <summary>Element type and dimensions of the image (for a compressed image, of the
        /// uncompressed image, rather than the table that contains it).</summary>
        public int Bitpix;
        public long[] Axes = Array.Empty<long>();
        public bool IsImage;

        /// <summary>Whether this is a tile-compressed image, stored in a binary table.</summary>
        public bool IsCompressed;

        public int Width => Axes.Length >= 2 ? (int)Axes[0] : 0;
        public int Height => Axes.Length >= 2 ? (int)Axes[1] : 0;

        /// <summary>Number of 2-D planes in the image.</summary>
        public long Planes
        {
            get
            {
                long p = 1;
                for (int i = 2; i < Axes.Length; i++)
                    p *= Axes[i];
                return p;
            }
        }

        /// <summary>Whether the image looks like an RGB color image.</summary>
        public bool IsRgb => Axes.Length >= 3 && Axes[2] == 3 && Planes == 3;

        public double GetNumber(string key, double defaultValue)
        {
            return Cards.TryGetValue(key, out var v) && double.TryParse(v.Replace('D', 'E').Replace('d', 'e'), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : defaultValue;
        }

        public string GetString(string key, string defaultValue)
        {
            return Cards.TryGetValue(key, out var v) ? v : defaultValue;
        }
    }

    /// <summary>
    /// Handles reading FITS (Flexible Image Transport System) images
    /// </summary>
    public static class FitsReader
    {
        public const int HEADER_BLOCK_LENGTH = 2880;
        public const int HEADER_ITEM_LENGTH = 80;

        // Fraction of values clipped at each end when stretching values for display.
        private const double ClipPercent = 0.25;

        /// <summary>
        /// A frame of the file: one 2-D plane of an image, or three planes for an RGB image.
        /// </summary>
        private readonly record struct Frame(FitsHdu Hdu, long Plane, bool Rgb);

        /// <summary>
        /// Reads a FITS (Flexible Image Transport System) image from a file. If the file
        /// contains more than one image (or image plane), the first one is read.
        /// </summary>
        /// <param name="fileName">Name of the file to read.</param>
        /// <returns>ImageData that contains the image that was read.</returns>
        public static ImageData? Load(string fileName)
        {
            return Load(fileName, 0);
        }

        /// <summary>
        /// Reads a FITS (Flexible Image Transport System) image from a stream. If the file
        /// contains more than one image (or image plane), the first one is read.
        /// </summary>
        /// <param name="stream">Stream from which to read the image.</param>
        /// <returns>ImageData that contains the image that was read.</returns>
        public static ImageData? Load(Stream stream)
        {
            return Load(stream, 0);
        }

        /// <summary>
        /// Reads the given frame of a FITS image from a file. Each plane of an image cube,
        /// and each image extension, is a separate frame.
        /// </summary>
        /// <param name="fileName">Name of the file to read.</param>
        /// <param name="frame">Zero-based index of the frame to read; see <see cref="GetFrameCount(string)"/>.</param>
        /// <returns>ImageData that contains the frame that was read.</returns>
        public static ImageData? Load(string fileName, int frame)
        {
            using var f = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Load(f, frame);
        }

        /// <summary>
        /// Reads the given frame of a FITS image from a stream. Each plane of an image cube,
        /// and each image extension, is a separate frame.
        /// </summary>
        /// <param name="stream">Stream from which to read the image.</param>
        /// <param name="frame">Zero-based index of the frame to read; see <see cref="GetFrameCount(Stream)"/>.</param>
        /// <returns>ImageData that contains the frame that was read.</returns>
        public static ImageData? Load(Stream stream, int frame)
        {
            long start = stream.Position;
            var frames = GetFrames(ReadHdus(stream, start, out _));
            if (frames.Count == 0)
                throw new ImageDecodeException("FITS file does not contain any images.");
            if (frame < 0 || frame >= frames.Count)
                throw new ArgumentOutOfRangeException(nameof(frame), "Frame " + frame + " is out of range; the file has " + frames.Count + " frame(s).");
            return Render(stream, frames[frame]);
        }

        /// <summary>
        /// Gets the number of frames (images, or planes of image cubes) in a FITS file.
        /// </summary>
        /// <param name="fileName">Name of the file to read.</param>
        public static int GetFrameCount(string fileName)
        {
            using var f = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
            return GetFrameCount(f);
        }

        /// <summary>
        /// Gets the number of frames (images, or planes of image cubes) in a FITS file.
        /// </summary>
        /// <param name="stream">Stream from which to read the image.</param>
        public static int GetFrameCount(Stream stream)
        {
            return GetFrames(ReadHdus(stream, stream.Position, out _)).Count;
        }

        /// <summary>
        /// Reads all the frames (images, or planes of image cubes) of a FITS file. Each
        /// frame is only read as the sequence is enumerated, so the stream must remain
        /// open until then.
        /// </summary>
        /// <param name="stream">Stream from which to read the image.</param>
        /// <returns>The frames of the file, in order.</returns>
        public static IEnumerable<ImageData?> LoadFrames(Stream stream)
        {
            long start = stream.Position;
            var frames = GetFrames(ReadHdus(stream, start, out _));
            if (frames.Count == 0)
                throw new ImageDecodeException("FITS file does not contain any images.");
            return EnumerateFrames(stream, frames);
        }

        /// <summary>
        /// Reads all the frames (images, or planes of image cubes) of a FITS file.
        /// </summary>
        /// <param name="fileName">Name of the file to read.</param>
        /// <returns>The frames of the file, in order.</returns>
        public static IEnumerable<ImageData?> LoadFrames(string fileName)
        {
            var f = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                long start = f.Position;
                var frames = GetFrames(ReadHdus(f, start, out _));
                if (frames.Count == 0)
                    throw new ImageDecodeException("FITS file does not contain any images.");
                return EnumerateFramesAndClose(f, frames);
            }
            catch
            {
                f.Dispose();
                throw;
            }
        }

        private static IEnumerable<ImageData?> EnumerateFrames(Stream stream, List<Frame> frames)
        {
            foreach (var frame in frames)
                yield return Render(stream, frame);
        }

        private static IEnumerable<ImageData?> EnumerateFramesAndClose(Stream stream, List<Frame> frames)
        {
            using (stream)
            {
                foreach (var frame in frames)
                    yield return Render(stream, frame);
            }
        }

        /// <summary>
        /// Reads a FITS (Flexible Image Transport System) image from a stream, and/or works
        /// out the total size of the FITS file and the dimensions of its first image.
        /// </summary>
        /// <param name="stream">Stream from which to read the image.</param>
        /// <param name="wantImage">Whether to decode the image, or only get its information.</param>
        /// <param name="fileSize">Reference to a long that will receive the size of the file.</param>
        /// <param name="imgWidth">Reference to an int that will receive the width of the first image.</param>
        /// <param name="imgHeight">Reference to an int that will receive the height of the first image.</param>
        /// <returns>ImageData that contains the image that was read, or null if not wanted.</returns>
        public static ImageData? Load(Stream stream, bool wantImage, ref long fileSize, ref int imgWidth, ref int imgHeight)
        {
            fileSize = 0;
            List<Frame> frames;
            try
            {
                frames = GetFrames(ReadHdus(stream, stream.Position, out fileSize));
            }
            catch (ImageDecodeException)
            {
                // Not a FITS file.
                return null;
            }
            if (frames.Count == 0)
                return null;
            imgWidth = frames[0].Hdu.Width;
            imgHeight = frames[0].Hdu.Height;
            return wantImage ? Render(stream, frames[0]) : null;
        }

        #region Parsing

        /// <summary>
        /// Reads the headers of all the HDUs in the file, and works out the total size of
        /// the file. Stops at the end of the stream, or at the first thing that doesn't
        /// look like a valid header (the stream may contain more than just the FITS file).
        /// </summary>
        private static List<FitsHdu> ReadHdus(Stream stream, long start, out long fileSize)
        {
            var hdus = new List<FitsHdu>();
            fileSize = 0;
            var card = new byte[HEADER_ITEM_LENGTH];
            long pos = start;
            while (true)
            {
                stream.Seek(pos, SeekOrigin.Begin);
                var hdu = new FitsHdu();
                bool ended = false;
                long cardCount = 0;
                while (true)
                {
                    if (stream.Read(card, 0, HEADER_ITEM_LENGTH) < HEADER_ITEM_LENGTH)
                        break;
                    string text = Encoding.Latin1.GetString(card);
                    string keyword = text[..8].TrimEnd();
                    if (cardCount == 0)
                    {
                        bool valid = hdus.Count == 0 ? keyword == "SIMPLE" : keyword == "XTENSION";
                        if (!valid)
                            break;
                    }
                    cardCount++;
                    if (keyword == "END")
                    {
                        ended = true;
                        break;
                    }
                    if (text.Length > 10 && text[8] == '=' && keyword.Length > 0)
                        hdu.Cards.TryAdd(keyword, ParseValue(text[10..]));
                    if (cardCount > 100000)
                        break;
                }
                if (!ended)
                {
                    if (hdus.Count == 0)
                        throw new ImageDecodeException("Not a valid FITS file.");
                    break;
                }

                // The data starts at the next block boundary after the header.
                long headerBytes = (cardCount * HEADER_ITEM_LENGTH + HEADER_BLOCK_LENGTH - 1) / HEADER_BLOCK_LENGTH * HEADER_BLOCK_LENGTH;
                hdu.DataStart = pos + headerBytes;
                hdu.Bitpix = (int)hdu.GetNumber("BITPIX", 0);
                int naxis = (int)hdu.GetNumber("NAXIS", 0);
                if (naxis < 0 || naxis > 999 || !(hdu.Bitpix is 8 or 16 or 32 or 64 or -32 or -64))
                {
                    if (hdus.Count == 0)
                        throw new ImageDecodeException("Invalid FITS header.");
                    break;
                }
                hdu.Axes = new long[naxis];
                for (int i = 0; i < naxis; i++)
                    hdu.Axes[i] = Math.Max(0, (long)hdu.GetNumber("NAXIS" + (i + 1), 0));

                // Size of the data: |BITPIX| * GCOUNT * (PCOUNT + NAXIS1 * NAXIS2 * ... * NAXISn)
                // bits. In random groups data, NAXIS1 is 0 and doesn't count.
                bool groups = hdus.Count == 0 && hdu.Cards.TryGetValue("GROUPS", out var g) && g == "T" && naxis > 0 && hdu.Axes[0] == 0;
                long elements = naxis == 0 ? 0 : 1;
                for (int i = groups ? 1 : 0; i < naxis; i++)
                    elements *= hdu.Axes[i];
                long pcount = (long)hdu.GetNumber("PCOUNT", 0), gcount = (long)hdu.GetNumber("GCOUNT", 1);
                if (hdus.Count > 0 || groups)
                    elements = gcount * (pcount + elements);
                hdu.DataSize = elements * Math.Abs(hdu.Bitpix) / 8;

                string xtension = hdus.Count == 0 ? "" : hdu.GetString("XTENSION", "");
                hdu.IsImage = !groups && (hdus.Count == 0 || xtension == "IMAGE" || xtension == "IUEIMAGE");
                if (xtension == "BINTABLE" && hdu.GetString("ZIMAGE", "") == "T")
                {
                    // A tile-compressed image: the table describes the image it contains.
                    int znaxis = (int)hdu.GetNumber("ZNAXIS", 0);
                    int zbitpix = (int)hdu.GetNumber("ZBITPIX", 0);
                    if (znaxis > 0 && znaxis <= 999 && zbitpix is 8 or 16 or 32 or 64 or -32 or -64)
                    {
                        hdu.Axes = new long[znaxis];
                        for (int i = 0; i < znaxis; i++)
                            hdu.Axes[i] = Math.Max(0, (long)hdu.GetNumber("ZNAXIS" + (i + 1), 0));
                        hdu.Bitpix = zbitpix;
                        hdu.IsImage = true;
                        hdu.IsCompressed = true;
                    }
                }

                hdus.Add(hdu);
                long dataBlocks = (hdu.DataSize + HEADER_BLOCK_LENGTH - 1) / HEADER_BLOCK_LENGTH * HEADER_BLOCK_LENGTH;
                pos = hdu.DataStart + dataBlocks;
                fileSize = pos - start;
                if (!stream.CanSeek || pos >= stream.Length)
                    break;
            }
            return hdus;
        }

        /// <summary>
        /// Extracts the value of a header card (everything after "= "): either a quoted
        /// string, or whatever comes before the comment.
        /// </summary>
        private static string ParseValue(string text)
        {
            text = text.TrimStart();
            if (text.StartsWith('\''))
            {
                // A string, in which a doubled quote stands for a single one.
                var sb = new StringBuilder();
                for (int i = 1; i < text.Length; i++)
                {
                    if (text[i] == '\'')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '\'')
                        {
                            sb.Append('\'');
                            i++;
                            continue;
                        }
                        break;
                    }
                    sb.Append(text[i]);
                }
                return sb.ToString().TrimEnd();
            }
            int slash = text.IndexOf('/');
            return (slash >= 0 ? text[..slash] : text).Trim();
        }

        private static List<Frame> GetFrames(List<FitsHdu> hdus)
        {
            var frames = new List<Frame>();
            foreach (var hdu in hdus)
            {
                if (!hdu.IsImage || hdu.Axes.Length < 2 || hdu.Width <= 0 || hdu.Height <= 0)
                    continue;
                if ((long)hdu.Width * hdu.Height > CodecImage.MaxSamples)
                    continue;
                if (hdu.IsRgb)
                {
                    frames.Add(new Frame(hdu, 0, true));
                    continue;
                }
                for (long p = 0; p < hdu.Planes && frames.Count < 100000; p++)
                    frames.Add(new Frame(hdu, p, false));
            }
            return frames;
        }

        #endregion

        #region Rendering

        /// <summary>
        /// Reads one plane of an image, as physical values (with undefined values as NaN).
        /// </summary>
        private static float[] ReadPlane(Stream stream, FitsHdu hdu, long plane)
        {
            if (hdu.IsCompressed)
                return FitsTileCompression.ReadPlane(stream, hdu, plane);

            int width = hdu.Width, height = hdu.Height;
            int bytesPerElement = Math.Abs(hdu.Bitpix) / 8;
            long planeBytes = (long)width * height * bytesPerElement;
            var bytes = new byte[planeBytes];
            stream.Seek(hdu.DataStart + plane * planeBytes, SeekOrigin.Begin);
            int total = 0;
            while (total < bytes.Length)
            {
                int n = stream.Read(bytes, total, bytes.Length - total);
                if (n <= 0)
                    break; // truncated file: the rest of the plane is left as zeros
                total += n;
            }

            double bzero = hdu.GetNumber("BZERO", 0), bscale = hdu.GetNumber("BSCALE", 1);
            if (bscale == 0 || double.IsNaN(bscale))
                bscale = 1;
            bool hasBlank = hdu.Cards.ContainsKey("BLANK") && hdu.Bitpix > 0;
            long blank = hasBlank ? (long)hdu.GetNumber("BLANK", 0) : 0;

            var values = new float[(long)width * height];
            var span = bytes.AsSpan();
            for (int i = 0; i < values.Length; i++)
            {
                int p = i * bytesPerElement;
                double v;
                long raw = 0;
                switch (hdu.Bitpix)
                {
                    case 8: raw = bytes[p]; v = raw; break;
                    case 16: raw = BinaryPrimitives.ReadInt16BigEndian(span[p..]); v = raw; break;
                    case 32: raw = BinaryPrimitives.ReadInt32BigEndian(span[p..]); v = raw; break;
                    case 64: raw = BinaryPrimitives.ReadInt64BigEndian(span[p..]); v = raw; break;
                    case -32: v = BinaryPrimitives.ReadSingleBigEndian(span[p..]); break;
                    default: v = BinaryPrimitives.ReadDoubleBigEndian(span[p..]); break;
                }
                if ((hasBlank && raw == blank) || double.IsInfinity(v))
                    v = double.NaN;
                values[i] = (float)(bzero + bscale * v);
            }
            return values;
        }

        private static ImageData Render(Stream stream, Frame frame)
        {
            var hdu = frame.Hdu;
            int width = hdu.Width, height = hdu.Height;
            var planes = new List<float[]>();
            for (int c = 0; c < (frame.Rgb ? 3 : 1); c++)
                planes.Add(ReadPlane(stream, hdu, frame.Plane + c));

            // An 8-bit RGB image is presumably meant to be displayed as it is; anything else
            // is stretched between percentiles (shared by all channels of a color image,
            // to keep its color balance).
            double lo, hi;
            bool plain = frame.Rgb && hdu.Bitpix == 8 && hdu.GetNumber("BZERO", 0) == 0 && hdu.GetNumber("BSCALE", 1) == 1;
            if (plain)
            {
                lo = 0;
                hi = 255;
            }
            else
            {
                GetStretch(planes, out lo, out hi);
            }
            double scale = hi > lo ? 255.0 / (hi - lo) : 0;

            var bgra = new byte[(long)width * height * 4];
            for (int c = 0; c < planes.Count; c++)
            {
                var values = planes[c];
                // For a color image, the planes are red, green and blue; a grayscale image
                // goes to all three.
                int first = planes.Count == 3 ? 2 - c : 0, last = planes.Count == 3 ? 2 - c : 2;
                for (int y = 0; y < height; y++)
                {
                    // The first row of the image is at the bottom.
                    int dst = (height - 1 - y) * width * 4;
                    int src = y * width;
                    for (int x = 0; x < width; x++, dst += 4)
                    {
                        double v = values[src + x];
                        byte b = double.IsNaN(v) ? (byte)0 : scale == 0 ? (byte)128 : (byte)Math.Clamp((v - lo) * scale + 0.5, 0, 255);
                        for (int k = first; k <= last; k++)
                            bgra[dst + k] = b;
                    }
                }
            }
            return Util.LoadRgb(width, height, bgra);
        }

        // Beyond this many values, the percentiles are estimated from an evenly spaced sample.
        private const int MaxStretchSamples = 1 << 22;

        /// <summary>
        /// Finds the range of values to display: from the ClipPercent percentile of the
        /// defined values to the (100 - ClipPercent) percentile.
        /// </summary>
        private static void GetStretch(List<float[]> planes, out double lo, out double hi)
        {
            long total = 0;
            foreach (var p in planes)
                total += p.Length;
            long step = Math.Max(1, (total + MaxStretchSamples - 1) / MaxStretchSamples);
            var samples = new List<float>((int)Math.Min(total, MaxStretchSamples));
            long index = 0;
            foreach (var p in planes)
            {
                for (long i = index % step == 0 ? 0 : step - index % step; i < p.Length; i += step)
                {
                    if (!float.IsNaN(p[i]))
                        samples.Add(p[i]);
                }
                index += p.Length;
            }
            if (samples.Count == 0)
            {
                lo = hi = 0;
                return;
            }
            var sorted = samples.ToArray();
            Array.Sort(sorted);
            lo = Percentile(sorted, ClipPercent);
            hi = Percentile(sorted, 100 - ClipPercent);
            if (hi <= lo)
            {
                // Mostly constant; fall back to the full range.
                lo = sorted[0];
                hi = sorted[^1];
            }
        }

        private static double Percentile(float[] sorted, double percent)
        {
            // Linear interpolation between the closest ranks, as numpy does by default.
            double rank = percent / 100 * (sorted.Length - 1);
            int i = (int)Math.Floor(rank);
            if (i >= sorted.Length - 1)
                return sorted[^1];
            return sorted[i] + ((double)sorted[i + 1] - sorted[i]) * (rank - i);
        }

        #endregion
    }
}
