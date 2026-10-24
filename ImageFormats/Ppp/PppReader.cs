#nullable enable
using System;
using System.IO;
using System.IO.Compression;

/*

Reader for Serif PagePlus (.PPP) publications.

PagePlus is a desktop publishing program, so a .PPP file is a page layout (text
frames, shapes, styles, fonts and placed pictures) rather than an image, and
rendering it faithfully would take a page layout engine. However, PagePlus saves
a preview image of the first page with every publication, which is what we read.

There are two generations of the format:

  * Up to around PagePlus X4, the file is an OLE compound file (see CompoundFile.cs)
    with a "Document" storage containing the publication. The preview is the
    thumbnail property of the standard "\005SummaryInformation" property set, as a
    DIB (CF_DIB clipboard format).

  * Later versions write a ZIP archive of XML files ("SerifXML"), with the preview
    as a JPEG named "preview.jpg" at the top level of the archive.

Copyright 2026+ Dmitry Brant
https://dmitrybrant.com

License: MIT
*/

namespace DmitryBrant.ImageFormats
{
    /// <summary>
    /// Handles reading the preview image of Serif PagePlus (.PPP) publications.
    /// </summary>
    public static class PppReader
    {
        private const string SummaryInformationName = "\u0005SummaryInformation";
        private const uint PidThumbnail = 0x11;
        private const int ClipboardDib = 8;

        /// <summary>
        /// Reads the preview image of a PagePlus publication from a file.
        /// </summary>
        /// <param name="fileName">Name of the file to read.</param>
        /// <returns>ImageData that contains the image that was read.</returns>
        public static ImageData Load(string fileName)
        {
            using var f = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Load(f);
        }

        /// <summary>
        /// Reads the preview image of a PagePlus publication from a stream.
        /// </summary>
        /// <param name="stream">Stream from which to read the image.</param>
        /// <returns>ImageData that contains the image that was read.</returns>
        public static ImageData Load(Stream stream)
        {
            return LoadIfPpp(stream) ?? throw new ImageDecodeException("This does not appear to be a valid PagePlus file.");
        }

        /// <summary>
        /// Reads the preview image of a PagePlus publication from a stream, or returns null
        /// if the stream is a compound file or ZIP archive that isn't a PagePlus publication.
        /// </summary>
        internal static ImageData? LoadIfPpp(Stream stream)
        {
            byte[] fileData;
            using (var ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                fileData = ms.ToArray();
            }
            if (CompoundFile.HasSignature(fileData))
                return LoadCompoundFile(new CompoundFile(fileData));
            if (fileData.Length >= 4 && fileData[0] == 'P' && fileData[1] == 'K' && fileData[2] == 3 && fileData[3] == 4)
                return LoadZip(fileData);
            return null;
        }

        private static ImageData? LoadCompoundFile(CompoundFile file)
        {
            var document = file.Find(file.Root, "Document");
            if (document == null || !document.IsStorage || file.Find(document, "Pages") == null)
                return null;

            var summary = file.Find(file.Root, SummaryInformationName);
            int format = 0;
            var thumbnail = summary != null && summary.IsStream
                ? new OlePropertySet(file.Read(summary)).GetClipboardData(PidThumbnail, out format)
                : null;
            if (thumbnail == null)
                throw new ImageDecodeException("This PagePlus file has no preview image.");
            if (format != ClipboardDib)
                throw new ImageDecodeException("Unsupported PagePlus preview format: " + format);
            return DibDecoder.Decode(thumbnail);
        }

        private static ImageData? LoadZip(byte[] fileData)
        {
            byte[] preview;
            try
            {
                using var zip = new ZipArchive(new MemoryStream(fileData), ZipArchiveMode.Read);
                var entry = zip.GetEntry("preview.jpg");
                if (entry == null || zip.GetEntry("summary.xml") == null)
                    return null;
                using var ms = new MemoryStream();
                using (var entryStream = entry.Open())
                    entryStream.CopyTo(ms);
                preview = ms.ToArray();
            }
            catch (InvalidDataException)
            {
                return null;
            }

            CodecImage image;
            try
            {
                image = JpegDecoder.Decode(preview);
            }
            catch (ImageDecodeException)
            {
                // A flavor of JPEG we can't decode; let the caller do it.
                return ImageData.FromEncoded(0, 0, preview, "jpeg");
            }
            if (image.Precision != 8 || (image.Components != 1 && image.Components != 3))
                return ImageData.FromEncoded(image.Width, image.Height, preview, "jpeg");

            // Samples are either gray, or red, green and blue.
            var s = image.Samples;
            int n = image.Components;
            var bmp = new byte[image.Width * image.Height * 4];
            for (int i = 0, p = 0; p < bmp.Length; i += n, p += 4)
            {
                bmp[p] = (byte)s[i + n - 1];
                bmp[p + 1] = (byte)s[i + n / 2];
                bmp[p + 2] = (byte)s[i];
                bmp[p + 3] = 0xFF;
            }
            return new ImageData(image.Width, image.Height, bmp);
        }
    }
}
