#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

/*

Decoder for Microsoft Fax (.AWD) documents.

AWD ("At Work Document") files were written by the fax software of Microsoft At
Work, and then Microsoft Fax in Windows 95, Windows 98 and Exchange. An AWD file is
an OLE compound file (see CompoundFile.cs), laid out like this:

  Documents/                          (storage)
    <name>.RBA                        (stream) one per document, holding its pages
  Persistent Information/             (storage)
    Global Information/Display Order  (stream) the names of the documents, in order,
                                               each ending with a null, and the
                                               whole list ending with another null
    Document Information/<name>.RBA   (stream) viewer state, e.g. zoom
    Page Information/<name>.RBA/PageN (stream) viewer state, e.g. rotation
  Annotation/                         (storage) annotations drawn by the viewer

Each document stream (".RBA") is a sequence of records, each of which begins with a
16-bit length, followed by that many bytes. Records that begin with four ASCII
letters are markers:

  "AWPI"  image information, found at the start of the stream. Following the tag
          is a 16-bit value that is always 8, then the 32-bit page width, the
          32-bit band height, and the 16-bit horizontal and vertical resolution in
          dots per inch (e.g. 200 x 100 for standard fax resolution, and 200 x 200
          for fine resolution).
  "PAGE"  end of a page.
  "ENDJ"  end of the document.

All other records are bands, i.e. horizontal strips of a page, stored top to
bottom. A band begins with a 12-byte header: a 32-bit value (always 0 as far as
we've seen), two 16-bit values (always 10 and 16), and then the 16-bit height and
width of the band in pixels. The rest of the band is compressed with CCITT Group 4
(T.6), with the bits of each byte in least-significant-first order, and each band
is compressed independently of the others, with its own EOFB.

At standard fax resolution the pixels are twice as tall as they are wide, so we
duplicate rows as needed to give the image its correct proportions.

Since this library returns a single image, Load() stacks all the pages of all the
documents in the file, top to bottom. Use LoadPages() to get them separately.

Copyright 2026+ Dmitry Brant
https://dmitrybrant.com

License: MIT
*/

namespace DmitryBrant.ImageFormats
{
    /// <summary>
    /// Handles reading Microsoft Fax (.AWD) documents.
    /// </summary>
    public static class AwdReader
    {
        private const int BandHeaderSize = 12;
        private const int MaxDimension = 32768;
        private const int MaxRowScale = 4;

        /// <summary>
        /// Reads a Microsoft Fax (.AWD) document from a file, with all of its pages stacked
        /// top to bottom in a single image.
        /// </summary>
        /// <param name="fileName">Name of the file to read.</param>
        /// <returns>ImageData that contains the image that was read.</returns>
        public static ImageData Load(string fileName)
        {
            using var f = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Load(f);
        }

        /// <summary>
        /// Reads a Microsoft Fax (.AWD) document from a stream, with all of its pages
        /// stacked top to bottom in a single image.
        /// </summary>
        /// <param name="stream">Stream from which to read the image.</param>
        /// <returns>ImageData that contains the image that was read.</returns>
        public static ImageData Load(Stream stream)
        {
            return LoadIfAwd(stream) ?? throw new ImageDecodeException("This does not appear to be a valid AWD file.");
        }

        /// <summary>
        /// Reads each page of a Microsoft Fax (.AWD) document from a file, as a separate image.
        /// </summary>
        /// <param name="fileName">Name of the file to read.</param>
        /// <returns>List of the pages in the document, in order.</returns>
        public static List<ImageData> LoadPages(string fileName)
        {
            using var f = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
            return LoadPages(f);
        }

        /// <summary>
        /// Reads each page of a Microsoft Fax (.AWD) document from a stream, as a separate image.
        /// </summary>
        /// <param name="stream">Stream from which to read the image.</param>
        /// <returns>List of the pages in the document, in order.</returns>
        public static List<ImageData> LoadPages(Stream stream)
        {
            var pages = ReadPages(stream);
            if (pages == null)
                throw new ImageDecodeException("This does not appear to be a valid AWD file.");
            if (pages.Count == 0)
                throw new ImageDecodeException("This AWD file contains no pages.");
            return pages;
        }

        /// <summary>
        /// Reads an AWD document with all its pages stacked, or returns null if the stream
        /// is a compound file that isn't an AWD document.
        /// </summary>
        internal static ImageData? LoadIfAwd(Stream stream)
        {
            var pages = ReadPages(stream);
            if (pages == null)
                return null;
            if (pages.Count == 0)
                throw new ImageDecodeException("This AWD file contains no pages.");
            if (pages.Count == 1)
                return pages[0];

            int width = 0;
            long height = 0;
            foreach (var page in pages)
            {
                width = Math.Max(width, page.Width);
                height += page.Height;
            }
            if (height * width * ImageData.BytesPerPixel > int.MaxValue)
                throw new ImageDecodeException("AWD document is too large to fit in one image. Use LoadPages instead.");

            // Pages are nearly always the same width, but if not, pad the narrow ones with white.
            int stride = width * ImageData.BytesPerPixel;
            var data = new byte[stride * height];
            Array.Fill(data, (byte)0xFF);
            int y = 0;
            foreach (var page in pages)
            {
                for (int row = 0; row < page.Height; row++, y++)
                    Buffer.BlockCopy(page.Data, row * page.Stride, data, y * stride, page.Stride);
            }
            return new ImageData(width, (int)height, data);
        }

        /// <summary>
        /// Returns the pages of an AWD document, or null if the stream isn't one.
        /// </summary>
        private static List<ImageData>? ReadPages(Stream stream)
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
            var documents = file.Find(file.Root, "Documents");
            if (documents == null || !documents.IsStorage)
                return null;

            var pages = new List<ImageData>();
            foreach (var doc in GetDocumentsInOrder(file, documents))
                ReadDocument(file.Read(doc), pages);
            return pages;
        }

        /// <summary>
        /// Returns the document streams in the order that the "Display Order" stream lists
        /// them, followed by any that it doesn't list.
        /// </summary>
        private static List<CompoundFileEntry> GetDocumentsInOrder(CompoundFile file, CompoundFileEntry documents)
        {
            var remaining = new List<CompoundFileEntry>();
            foreach (var entry in file.Children(documents))
            {
                if (entry.IsStream)
                    remaining.Add(entry);
            }

            var ordered = new List<CompoundFileEntry>();
            var info = file.Find(file.Root, "Persistent Information");
            var global = info != null ? file.Find(info, "Global Information") : null;
            var displayOrder = global != null ? file.Find(global, "Display Order") : null;
            if (displayOrder != null)
            {
                string list = Encoding.ASCII.GetString(file.Read(displayOrder));
                foreach (var name in list.Split('\0', StringSplitOptions.RemoveEmptyEntries))
                {
                    int index = remaining.FindIndex(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (index >= 0)
                    {
                        ordered.Add(remaining[index]);
                        remaining.RemoveAt(index);
                    }
                }
            }
            ordered.AddRange(remaining);
            return ordered;
        }

        private class Band
        {
            public int Width;
            public int Height;
            public int Offset;
            public int Length;
        }

        /// <summary>
        /// Decodes the pages of one document stream, adding them to the given list.
        /// </summary>
        private static void ReadDocument(byte[] data, List<ImageData> pages)
        {
            int xRes = 0, yRes = 0;
            var bands = new List<Band>();
            int pos = 0;

            while (pos + 2 <= data.Length)
            {
                int length = BitConverter.ToUInt16(data, pos);
                int start = pos + 2;
                pos = start + length;
                if (pos > data.Length)
                {
                    Util.log("AWD record runs past the end of the stream.");
                    break;
                }

                string tag = length >= 4 ? Encoding.ASCII.GetString(data, start, 4) : "";
                if (tag == "AWPI")
                {
                    if (length >= 18)
                    {
                        xRes = BitConverter.ToUInt16(data, start + 14);
                        yRes = BitConverter.ToUInt16(data, start + 16);
                    }
                }
                else if (tag == "PAGE")
                {
                    AddPage(data, bands, xRes, yRes, pages);
                    bands.Clear();
                }
                else if (tag == "ENDJ")
                {
                    break;
                }
                else if (length >= BandHeaderSize && !IsTag(tag))
                {
                    bands.Add(new Band
                    {
                        Height = BitConverter.ToUInt16(data, start + 8),
                        Width = BitConverter.ToUInt16(data, start + 10),
                        Offset = start + BandHeaderSize,
                        Length = length - BandHeaderSize
                    });
                }
            }

            // In case the stream is truncated before the end of the last page.
            AddPage(data, bands, xRes, yRes, pages);
        }

        private static bool IsTag(string tag)
        {
            foreach (char c in tag)
            {
                if (c < 'A' || c > 'Z')
                    return false;
            }
            return tag.Length == 4;
        }

        private static void AddPage(byte[] data, List<Band> bands, int xRes, int yRes, List<ImageData> pages)
        {
            int width = 0, height = 0;
            foreach (var band in bands)
            {
                width = Math.Max(width, band.Width);
                height += band.Height;
            }
            if (width == 0 || height == 0)
                return;

            int rowScale = 1;
            if (xRes > 0 && yRes > 0 && xRes > yRes)
                rowScale = Math.Min((xRes + yRes / 2) / yRes, MaxRowScale);
            if (width > MaxDimension || (long)height * rowScale > MaxDimension)
                throw new ImageDecodeException("AWD page dimensions are too large.");

            int stride = width * ImageData.BytesPerPixel;
            var output = new byte[stride * height * rowScale];
            Array.Fill(output, (byte)0xFF);

            int y = 0;
            foreach (var band in bands)
            {
                if (band.Width == 0 || band.Height == 0)
                    continue;
                byte[] pixels = CcittG4Decoder.Decode(data, band.Offset, band.Length, band.Width, band.Height, true);
                for (int row = 0; row < band.Height; row++, y++)
                {
                    int outRow = y * rowScale * stride;
                    int src = row * band.Width;
                    for (int x = 0; x < band.Width; x++)
                    {
                        if (pixels[src + x] != 0)
                        {
                            int p = outRow + x * ImageData.BytesPerPixel;
                            output[p] = output[p + 1] = output[p + 2] = 0;
                        }
                    }
                    for (int i = 1; i < rowScale; i++)
                        Buffer.BlockCopy(output, outRow, output, outRow + i * stride, stride);
                }
            }

            pages.Add(new ImageData(width, height * rowScale, output));
        }
    }
}
