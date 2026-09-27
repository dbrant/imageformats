using System;
using System.IO;
using System.Text;

/*

The result type returned by every decoder in this library: a plain buffer of
raw pixel data, with no dependency on any particular imaging library. The
consumer is free to hand the buffer to whichever library they prefer.

Copyright 2013+ Dmitry Brant
https://dmitrybrant.com

License: MIT
*/

namespace DmitryBrant.ImageFormats
{
    /// <summary>
    /// A decoded image, as a raw buffer of pixel data.
    ///
    /// The pixels in <see cref="Data"/> are stored top-down, one row after another,
    /// four bytes per pixel in blue, green, red, alpha order. (Read as a 32-bit
    /// little-endian integer, that is the usual packed ARGB layout.) This is the
    /// same layout as GDI+ Format32bppArgb, WIC/Direct2D B8G8R8A8, Avalonia's
    /// Bgra8888, and ImageSharp's Bgra32, so the buffer can be handed to any of
    /// them directly.
    ///
    /// A few source formats store their pixel data as an embedded image in some
    /// other well-known encoding (for example, DICOM files that wrap a JPEG). For
    /// those, <see cref="Data"/> is null and the encoded bytes are provided
    /// verbatim in <see cref="EncodedData"/> instead, for the consumer to pass to
    /// a decoder of their choosing. Check <see cref="IsEncoded"/> to tell the two
    /// apart.
    /// </summary>
    public class ImageData
    {
        /// <summary>Number of bytes per pixel in <see cref="Data"/>.</summary>
        public const int BytesPerPixel = 4;

        /// <summary>Width of the image, in pixels.</summary>
        public int Width { get; }

        /// <summary>Height of the image, in pixels.</summary>
        public int Height { get; }

        /// <summary>
        /// Raw pixel data, top-down, four bytes per pixel in blue, green, red, alpha
        /// order. Null if <see cref="IsEncoded"/> is true.
        /// </summary>
        public byte[] Data { get; }

        /// <summary>
        /// The image data in its original encoding, if this library does not decode it
        /// to raw pixels itself. Null unless <see cref="IsEncoded"/> is true.
        /// </summary>
        public byte[] EncodedData { get; }

        /// <summary>
        /// Lowercase name of the encoding of <see cref="EncodedData"/>, e.g. "jpeg".
        /// Null unless <see cref="IsEncoded"/> is true.
        /// </summary>
        public string EncodedFormat { get; }

        /// <summary>
        /// True if this image is provided as <see cref="EncodedData"/> in its original
        /// encoding, rather than as raw pixels in <see cref="Data"/>.
        /// </summary>
        public bool IsEncoded => EncodedData != null;

        /// <summary>Number of bytes per row of pixels in <see cref="Data"/>.</summary>
        public int Stride => Width * BytesPerPixel;

        /// <summary>
        /// Create an image from a buffer of raw pixel data.
        /// </summary>
        /// <param name="width">Width of the image, in pixels.</param>
        /// <param name="height">Height of the image, in pixels.</param>
        /// <param name="data">Pixel data, top-down, four bytes per pixel in blue, green,
        /// red, alpha order. Must be at least width * height * 4 bytes long.</param>
        public ImageData(int width, int height, byte[] data)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentException("Invalid image dimensions: " + width + " x " + height);
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            if (data.Length < (long)width * height * BytesPerPixel)
                throw new ArgumentException("Pixel buffer is too small for the given dimensions.");

            Width = width;
            Height = height;
            Data = data;
        }

        private ImageData(int width, int height, byte[] encodedData, string encodedFormat)
        {
            Width = width;
            Height = height;
            EncodedData = encodedData;
            EncodedFormat = encodedFormat;
        }

        /// <summary>
        /// Create an image that is passed through in its original encoding, for the
        /// consumer to decode with a library of their choosing.
        /// </summary>
        /// <param name="width">Width of the image in pixels, or 0 if not known.</param>
        /// <param name="height">Height of the image in pixels, or 0 if not known.</param>
        /// <param name="encodedData">The encoded bytes of the image.</param>
        /// <param name="encodedFormat">Lowercase name of the encoding, e.g. "jpeg".</param>
        public static ImageData FromEncoded(int width, int height, byte[] encodedData, string encodedFormat)
        {
            if (encodedData == null)
                throw new ArgumentNullException(nameof(encodedData));
            return new ImageData(width, height, encodedData, encodedFormat);
        }

        /// <summary>
        /// Load a file into an ImageData object. Will automatically
        /// detect the format of the image.
        /// </summary>
        /// <param name="fileName">Name of the file to load.</param>
        /// <returns>ImageData that contains the decoded image, or null if it could
        /// not be decoded by any of the formats known to this library.</returns>
        public static ImageData Load(string fileName)
        {
            ImageData bmp = null;
            using (var f = new FileStream(fileName, FileMode.Open, FileAccess.Read))
            {
                bmp = Load(f);
            }

            if (bmp == null)
            {
                if (Path.GetExtension(fileName).ToLower().Contains("tga"))
                    bmp = TgaReader.Load(fileName);
            }

            if (bmp == null)
            {
                if (Path.GetExtension(fileName).ToLower().Contains("cut"))
                    bmp = CutReader.Load(fileName);
            }

            if (bmp == null)
            {
                if (Path.GetExtension(fileName).ToLower().Contains("sgi") || Path.GetExtension(fileName).ToLower().Contains("rgb") || Path.GetExtension(fileName).ToLower().Contains("bw"))
                    bmp = SgiReader.Load(fileName);
            }

            if (bmp == null)
            {
                if (Path.GetExtension(fileName).ToLower().Contains("xpm"))
                    bmp = XpmReader.Load(fileName);
            }

            return bmp;
        }

        /// <summary>
        /// Create an ImageData object from a Stream. Will automatically
        /// detect the format of the image.
        /// </summary>
        /// <param name="stream">Stream from which the image will be read.</param>
        /// <returns>ImageData that contains the decoded image, or null if it could
        /// not be decoded by any of the formats known to this library.</returns>
        public static ImageData Load(Stream stream)
        {
            ImageData bmp = null;

            //read the first few bytes of the file to determine what format it is...
            byte[] header = new byte[256];
            stream.ReadExactly(header);
            stream.Seek(0, SeekOrigin.Begin);

            if ((header[0] == 0xA) && (header[1] <= 0x5) && (header[2] == 0x1) && ((header[3] == 0x1) || (header[3] == 0x2) || (header[3] == 0x4) || (header[3] == 0x8)))
            {
                bmp = PcxReader.Load(stream);
            }
            else if ((header[0] == 'P') && ((header[1] >= '1') && (header[1] <= '6')) && ((header[2] == 0xA) || (header[2] == 0xD) || (header[2] == 0x20)))
            {
                bmp = PnmReader.Load(stream);
            }
            else if ((header[0] == 0x59) && (header[1] == 0xa6) && (header[2] == 0x6a) && (header[3] == 0x95))
            {
                bmp = RasReader.Load(stream);
            }
            else if ((header[0x80] == 'D') && (header[0x81] == 'I') && (header[0x82] == 'C') && (header[0x83] == 'M'))
            {
                bmp = DicomReader.Load(stream);
            }
            else if ((header[0x41] == 'P') && (header[0x42] == 'N') && (header[0x43] == 'T') && (header[0x44] == 'G'))
            {
                bmp = MacPaintReader.Load(stream);
            }
            else if ((header[0] == 'F') && (header[1] == 'O') && (header[2] == 'R') && (header[3] == 'M'))
            {
                string iffType = Encoding.ASCII.GetString(header, 8, 4);
                if (iffType == "ILBM" || iffType.StartsWith("PBM") || iffType == "ACBM")
                {
                    bmp = IffIlbmReader.Load(stream);
                }
                else if (iffType == "DEEP" || iffType == "TVPP")
                {
                    bmp = IffDeepReader.Load(stream);
                }
                else if (iffType == "RGBN" || iffType == "RGB8")
                {
                    bmp = IffRgbnReader.Load(stream);
                }
            }
            else if ((header[0] == 'S') && (header[1] >= 'I') && (header[2] >= 'M') && (header[3] >= 'P'))
            {
                bmp = FitsReader.Load(stream);
            }
            else if ((header[0x0] == 1) && (header[0x1] == 0xDA))
            {
                bmp = SgiReader.Load(stream);
            }
            else if ((header[0] == 'J') && (header[1] == 'G') && (header[2] <= 4) && (header[3] >= 0xC))
            {
                bmp = ArtReader.Load(stream);
            }
            else if (Encoding.ASCII.GetString(header, 0, 25) == "Paint Shop Pro Image File")
            {
                bmp = PspReader.Load(stream);
            }
            else if (CompoundFile.HasSignature(header))
            {
                // An OLE compound file, which could be one of many things, and only some of
                // which are images.
                bmp = AwdReader.LoadIfAwd(stream);
            }
            return bmp;
        }
    }
}
