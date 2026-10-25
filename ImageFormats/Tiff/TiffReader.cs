using System;
using System.Collections.Generic;
using System.IO;

/*

Decoder for TIFF images.

Supports classic TIFF and BigTIFF, in either byte order; strips and tiles; chunky and
planar sample layouts; and multiple pages (one per image file directory).

Compression: none, CCITT Modified Huffman, Group 3 (1D and 2D) and Group 4, LZW,
PackBits, Deflate, JPEG (including the old TIFF 6.0 style, when it points to a whole
JPEG stream), and JPEG 2000. The horizontal and floating point predictors.

Color: bilevel, grayscale and palette images of any bit depth up to 16; RGB, CMYK
and YCbCr (with or without subsampling); CIE and ICC L*a*b*; with or without an
alpha channel, associated or not. Samples may be unsigned or signed integers of up
to 32 bits, or floating point numbers of 16, 24, 32 or 64 bits. Floating point data
outside the range 0 to 1 is scaled to fit. The Orientation field is applied.

See TiffFile.cs for the structure of the file, and TiffPage.cs for how each image
is decoded.

Copyright 2026+ Dmitry Brant
https://dmitrybrant.com

License: MIT
*/

namespace DmitryBrant.ImageFormats
{
    /// <summary>
    /// Handles reading TIFF images.
    /// </summary>
    public static class TiffReader
    {
        /// <summary>
        /// Reads a TIFF image from a file. If the file contains more than one page, the first
        /// one is read.
        /// </summary>
        /// <param name="fileName">Name of the file to read.</param>
        /// <returns>ImageData that contains the image that was read.</returns>
        public static ImageData Load(string fileName)
        {
            return Load(fileName, 0);
        }

        /// <summary>
        /// Reads a TIFF image from a stream. If the file contains more than one page, the first
        /// one is read.
        /// </summary>
        /// <param name="stream">Stream from which to read the image.</param>
        /// <returns>ImageData that contains the image that was read.</returns>
        public static ImageData Load(Stream stream)
        {
            return Load(stream, 0);
        }

        /// <summary>
        /// Reads the given page of a (possibly multi-page) TIFF image from a file.
        /// </summary>
        /// <param name="fileName">Name of the file to read.</param>
        /// <param name="page">Zero-based index of the page to read; see <see cref="GetPageCount(string)"/>.</param>
        /// <returns>ImageData that contains the page that was read.</returns>
        public static ImageData Load(string fileName, int page)
        {
            using var f = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Load(f, page);
        }

        /// <summary>
        /// Reads the given page of a (possibly multi-page) TIFF image from a stream.
        /// </summary>
        /// <param name="stream">Stream from which to read the image.</param>
        /// <param name="page">Zero-based index of the page to read; see <see cref="GetPageCount(Stream)"/>.</param>
        /// <returns>ImageData that contains the page that was read.</returns>
        public static ImageData Load(Stream stream, int page)
        {
            var file = Open(stream);
            if (page < 0 || page >= file.Directories.Count)
                throw new ArgumentOutOfRangeException(nameof(page), "Page " + page + " is out of range; the image has " + file.Directories.Count + " page(s).");
            return Guard(() => new TiffPage(file, file.Directories[page]).Decode());
        }

        /// <summary>
        /// Gets the number of pages in a TIFF file.
        /// </summary>
        /// <param name="fileName">Name of the file to read.</param>
        public static int GetPageCount(string fileName)
        {
            using var f = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
            return GetPageCount(f);
        }

        /// <summary>
        /// Gets the number of pages in a TIFF file.
        /// </summary>
        /// <param name="stream">Stream from which to read the image.</param>
        public static int GetPageCount(Stream stream)
        {
            return Open(stream).Directories.Count;
        }

        /// <summary>
        /// Reads all the pages of a TIFF image from a file. The file is read and parsed up
        /// front, but each page is only decoded as the sequence is enumerated.
        /// </summary>
        /// <param name="fileName">Name of the file to read.</param>
        /// <returns>The pages of the image, in order.</returns>
        public static IEnumerable<ImageData> LoadPages(string fileName)
        {
            using var f = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
            return LoadPages(f);
        }

        /// <summary>
        /// Reads all the pages of a TIFF image from a stream. The stream is read and parsed up
        /// front, but each page is only decoded as the sequence is enumerated.
        /// </summary>
        /// <param name="stream">Stream from which to read the image.</param>
        /// <returns>The pages of the image, in order.</returns>
        public static IEnumerable<ImageData> LoadPages(Stream stream)
        {
            return EnumeratePages(Open(stream));
        }

        private static IEnumerable<ImageData> EnumeratePages(TiffFile file)
        {
            foreach (var dir in file.Directories)
                yield return Guard(() => new TiffPage(file, dir).Decode());
        }

        /// <summary>
        /// Checks whether the given header is that of a TIFF or BigTIFF file.
        /// </summary>
        internal static bool HasSignature(byte[] header)
        {
            return header.Length >= 4
                && ((header[0] == 'I' && header[1] == 'I' && (header[2] == 42 || header[2] == 43) && header[3] == 0)
                    || (header[0] == 'M' && header[1] == 'M' && header[2] == 0 && (header[3] == 42 || header[3] == 43)));
        }

        private static TiffFile Open(Stream stream)
        {
            byte[] data;
            using (var ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                data = ms.ToArray();
            }
            return Guard(() => new TiffFile(data));
        }

        /// <summary>
        /// Runs the given function, turning low-level exceptions caused by malformed data
        /// that slipped past our checks into ImageDecodeExceptions.
        /// </summary>
        private static T Guard<T>(Func<T> func)
        {
            try
            {
                return func();
            }
            catch (Exception e) when (e is IndexOutOfRangeException || e is ArgumentException || e is OverflowException || e is InvalidDataException)
            {
                throw new ImageDecodeException("Invalid TIFF file: " + e.Message);
            }
        }
    }
}
