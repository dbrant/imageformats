using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

/*

Reader for camera raw files. For now, it reads the preview image that the camera embeds
in the file: an ordinary JPEG image of the picture as the camera itself processed it,
often at full size. Where there is more than one, we read the largest, and rotate it as
the file says the picture should be displayed.

Most raw formats are variants of TIFF, and keep their previews in TIFF structures: as a
JPEG stream that the JPEGInterchangeFormat field points to, or as a single strip (or
tile) of JPEG data, in any directory of the file (including SubIFDs); or as a field of
undefined bytes that holds a JPEG stream (such as Panasonic's JpgFromRaw). We also look
in the maker notes (the camera maker's own directory, inside the EXIF directory) of the
makers that keep their largest previews there:

- Nikon (NEF): a TIFF header of its own, ten bytes in, followed by a directory whose
  PreviewIFD field points to a directory with a JPEGInterchangeFormat field.
- Olympus (ORF): a directory whose CameraSettings field holds a directory with the
  PreviewImageStart and PreviewImageLength fields. The maker notes of newer cameras
  ("OLYMPUS" or "OM SYSTEM") have their own byte order, and their offsets are relative
  to the start of the maker notes; those of older ones ("OLYMP") are relative to the
  TIFF header.
- Leica (DNG): a directory with the preview as a field of undefined bytes.
- Minolta (MRW): a directory with the PreviewImageStart and PreviewImageLength fields.
  The first byte of the preview is often wrong (0x00 or 0x02 instead of 0xFF), so we
  repair it.

Leaf (MOS) files keep their preview in a private field of the first directory, which
holds a series of "packets": each is "PKTS", four bytes of version, a 40-byte name, and
the size of its content (big-endian), followed by the content. The preview is the one
named "JPEG_preview_data".

Other raw formats have structures of their own:

- Canon CRW (CIFF): a "heap" of records, each a tag, size and offset of its content;
  the heap ends with the offset of its table of records. Some records are heaps
  themselves. Records 0x2007 and 0x2008 are JPEG images, and record 0x1810 gives the
  rotation of the picture, in degrees.
- Canon CR3: an ISO base media file (like MP4), a series of nested "boxes", each a
  size and a type (big-endian). Each track ("trak") of the movie ("moov") has one
  sample, which the sample table ("stbl") locates; the first track's is a full-size JPEG
  image. Two of Canon's own "uuid" boxes hold smaller previews ("THMB" and "PRVW", each
  with a 16-byte header), and the "CMT1" box is the first directory of a TIFF file,
  which gives the orientation.
- Fujifilm RAF: a header with the offset and size of a JPEG image (big-endian) at
  offset 84.
- Minolta MRW: a series of blocks, each a 4-byte name and size; the "TTW" block is a
  TIFF file, with offsets relative to its own start.
- Sigma X3F: a directory at the end of the file (pointed to by its last four bytes),
  listing the sections of the file; image sections that hold JPEG data have a 28-byte
  header.

The TIFF-based formats that don't have a signature of their own (CR2, NEF, ARW, PEF,
DNG, and the like) are told apart from other TIFF files by the features of camera raw
files: data straight from the sensor (a color filter array, which TIFF/EP describes
with the CFAPattern field and DNG with a PhotometricInterpretation of its own, or
linear raw data), the
fields of DNG, Leaf and Kodak raw files, or Canon's "CR" signature. Sony's SRF has none
of these, but stores its sensor data as an RGB image of 10 to 14 bits per sample, which
ordinary TIFF files don't use.

Copyright 2026+ Dmitry Brant
https://dmitrybrant.com

License: MIT
*/

namespace DmitryBrant.ImageFormats
{
    /// <summary>
    /// Handles reading camera raw files (.CR2, .CR3, .CRW, .NEF, .NRW, .ARW, .SR2, .SRF, .PEF,
    /// .ORF, .RW2, .RAF, .DNG, .SRW, .KDC, .MRW, .MOS, .X3F, and others), by way of the
    /// preview images embedded in them.
    /// </summary>
    public static class RawReader
    {
        /// <summary>
        /// Reads the largest preview image embedded in a camera raw file.
        /// </summary>
        /// <param name="fileName">Name of the file to read.</param>
        /// <returns>ImageData that contains the image that was read.</returns>
        public static ImageData Load(string fileName)
        {
            using var f = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Load(f);
        }

        /// <summary>
        /// Reads the largest preview image embedded in a camera raw file.
        /// </summary>
        /// <param name="stream">Stream from which to read the image.</param>
        /// <returns>ImageData that contains the image that was read.</returns>
        public static ImageData Load(Stream stream)
        {
            var search = Guard(() => PreviewSearch.Run(ReadAll(stream)))
                ?? throw new ImageDecodeException("This does not appear to be a camera raw file.");
            return Guard(search.Decode)
                ?? throw new ImageDecodeException("This raw file has no preview image.");
        }

        /// <summary>
        /// Checks whether the given header is that of a camera raw format with a signature of
        /// its own (as opposed to a plain TIFF signature).
        /// </summary>
        internal static bool HasSignature(byte[] header)
        {
            if (header.Length < 16)
                return false;
            var h = header.AsSpan();
            return h.StartsWith("FUJIFILMCCD-RAW"u8) || h.StartsWith("\0MRM"u8) || h.StartsWith("FOVb"u8)
                || h.StartsWith("IIRO"u8) || h.StartsWith("IIRS"u8) || h.StartsWith("MMOR"u8) || h.StartsWith("IIU\0"u8)
                || IsCiff(h) || IsCr3(h);
        }

        /// <summary>
        /// Reads the largest preview image of a camera raw file that has a TIFF signature, or
        /// returns null if it is some other kind of TIFF file, or has no preview.
        /// </summary>
        internal static ImageData? LoadIfRaw(byte[] data)
        {
            PreviewSearch? search;
            try
            {
                search = Guard(() => PreviewSearch.Run(data));
            }
            catch (ImageDecodeException)
            {
                return null;
            }
            return search == null ? null : Guard(search.Decode);
        }

        private static bool IsCiff(ReadOnlySpan<byte> h)
        {
            return h.Length >= 14 && h[0] == h[1] && (h[0] == 'I' || h[0] == 'M') && h.Slice(6, 8).SequenceEqual("HEAPCCDR"u8);
        }

        private static bool IsCr3(ReadOnlySpan<byte> h)
        {
            return h.Length >= 12 && h.Slice(4, 8).SequenceEqual("ftypcrx "u8);
        }

        private static byte[] ReadAll(Stream stream)
        {
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
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
                throw new ImageDecodeException("Invalid raw file: " + e.Message);
            }
        }

        /// <summary>
        /// Finds the preview images in a raw file.
        /// </summary>
        private sealed class PreviewSearch
        {
            private const int TagSubIfds = 330;
            private const int TagStripOffsets = 273;
            private const int TagStripByteCounts = 279;
            private const int TagTileOffsets = 324;
            private const int TagTileByteCounts = 325;
            private const int TagJpegInterchangeFormat = 513;
            private const int TagJpegInterchangeFormatLength = 514;
            private const int TagPhotometric = 262;
            private const int TagBitsPerSample = 258;
            private const int TagMake = 271;
            private const int TagOrientation = 274;
            private const int TagExifIfd = 34665;
            private const int TagMakerNote = 37500;
            private const int TagDngVersion = 50706;
            private const int TagDngPrivateData = 50740;
            private const int TagLeafData = 34310;
            private const int TagKodakIfd = 65024;
            private const int TagCfaPattern = 33422;

            private const int TagNikonPreviewIfd = 0x11;
            private const int TagOlympusCameraSettings = 0x2020;
            private const int TagOlympusPreviewStart = 0x101;
            private const int TagOlympusPreviewLength = 0x102;
            private const int TagMinoltaPreviewStart = 0x88;
            private const int TagMinoltaPreviewLength = 0x89;

            private const int PhotometricCfa = 32803;
            private const int PhotometricLinearRaw = 34892;

            private const int MaxDepth = 4;

            private readonly struct Candidate
            {
                public readonly long Offset;
                public readonly long Length;

                /// <summary>Whether the first byte of the JPEG stream may be wrong.</summary>
                public readonly bool Repair;

                public Candidate(long offset, long length, bool repair)
                {
                    Offset = offset;
                    Length = length;
                    Repair = repair;
                }
            }

            private readonly byte[] data;
            private readonly List<Candidate> candidates = new();

            /// <summary>
            /// The Orientation (as in TIFF) of the picture, or 0 if the raw file doesn't say,
            /// in which case we go by the EXIF data of the preview itself.
            /// </summary>
            private int orientation;

            private PreviewSearch(byte[] data)
            {
                this.data = data;
            }

            /// <summary>
            /// Finds the previews in the given file, or returns null if it isn't a raw file.
            /// </summary>
            public static PreviewSearch? Run(byte[] data)
            {
                var search = new PreviewSearch(data);
                var h = data.AsSpan();
                if (h.StartsWith("FUJIFILMCCD-RAW"u8))
                    search.SearchRaf();
                else if (h.StartsWith("\0MRM"u8))
                    search.SearchMrw();
                else if (h.StartsWith("FOVb"u8))
                    search.SearchX3f();
                else if (IsCiff(h))
                    search.SearchCiff();
                else if (IsCr3(h))
                    search.SearchBoxes(0, data.Length, 0);
                else if (h.Length >= 8 && h[0] == h[1] && (h[0] == 'I' || h[0] == 'M'))
                {
                    var file = new TiffFile(data, 0, true);
                    int magic = file.ReadU16(2);
                    if (magic == 43 || (magic == 42 && !IsRawTiff(file)))
                        return null;
                    search.SearchTiff(file);
                }
                else
                {
                    return null;
                }
                return search;
            }

            /// <summary>
            /// Decodes the largest preview, or returns null if there is none.
            /// </summary>
            public ImageData? Decode()
            {
                Candidate? best = null;
                long bestArea = 0;
                int bestExifOrientation = 0;
                foreach (var c in candidates)
                {
                    if (!ReadJpegHeader(c, out int width, out int height, out int exifOrientation))
                        continue;
                    if ((long)width * height > bestArea)
                    {
                        best = c;
                        bestArea = (long)width * height;
                        bestExifOrientation = exifOrientation;
                    }
                }
                if (best is not Candidate preview)
                    return null;

                var jpeg = data.AsSpan((int)preview.Offset, (int)Math.Min(preview.Length, data.Length - preview.Offset)).ToArray();
                if (preview.Repair)
                    jpeg[0] = 0xFF;
                var image = JpegDecoder.DecodeToImage(jpeg);
                if (image.IsEncoded)
                    return image;
                return TiffPage.Reorient(image, orientation > 0 ? orientation : bestExifOrientation);
            }

            private void Add(long offset, long length, bool repair = false)
            {
                if (offset <= 0 || offset > data.Length - 4)
                    return;
                if (length <= 0 || length > data.Length - offset)
                    length = data.Length - offset;
                if ((data[offset] != 0xFF && !repair) || data[offset + 1] != 0xD8 || data[offset + 2] != 0xFF)
                    return;
                foreach (var c in candidates)
                {
                    if (c.Offset == offset)
                        return;
                }
                candidates.Add(new Candidate(offset, length, repair));
            }

            /// <summary>
            /// Reads the size of a JPEG image, and the Orientation from its EXIF data (or 0 if
            /// none). Returns false if it isn't an ordinary JPEG image of 8 bits per sample.
            /// </summary>
            private bool ReadJpegHeader(Candidate c, out int width, out int height, out int exifOrientation)
            {
                width = height = exifOrientation = 0;
                long pos = c.Offset + 2, end = c.Offset + c.Length;
                while (pos + 4 <= end)
                {
                    if (data[pos] != 0xFF)
                        return false;
                    int marker = data[pos + 1];
                    if (marker == 0xFF)
                    {
                        pos++;
                        continue;
                    }
                    if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
                    {
                        pos += 2;
                        continue;
                    }
                    if (marker == 0xD9 || marker == 0xDA)
                        return false;
                    int length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan((int)pos + 2));
                    if (length < 2 || pos + 2 + length > end)
                        return false;
                    if (marker == 0xE1 && length >= 16 && data.AsSpan((int)pos + 4, 6).SequenceEqual("Exif\0\0"u8))
                        exifOrientation = ReadExifOrientation(pos + 10);
                    else if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                    {
                        // Only the Huffman-coded DCT processes (baseline, extended and progressive)
                        // are what a preview would use.
                        if (marker > 0xC2 || length < 8)
                            return false;
                        int precision = data[pos + 4];
                        height = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan((int)pos + 5));
                        width = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan((int)pos + 7));
                        int components = data[pos + 9];
                        return precision == 8 && (components == 1 || components == 3) && width > 0 && height > 0;
                    }
                    pos += 2 + length;
                }
                return false;
            }

            private int ReadExifOrientation(long tiffHeader)
            {
                try
                {
                    var exif = new TiffFile(data, tiffHeader, false);
                    return (int)GetInt(exif.Directories[0], TagOrientation, 0);
                }
                catch (ImageDecodeException)
                {
                    return 0;
                }
            }

            #region TIFF

            /// <summary>
            /// Checks for the features of a camera raw file in a TIFF file with the usual
            /// signature.
            /// </summary>
            private static bool IsRawTiff(TiffFile file)
            {
                var ifd0 = file.Directories[0];
                if (ifd0.Has(TagDngVersion) || ifd0.Has(TagDngPrivateData) || ifd0.Has(TagLeafData) || ifd0.Has(TagKodakIfd))
                    return true;
                if (file.Data.Length > 10 && file.Data[8] == 'C' && file.Data[9] == 'R')
                    return true;
                if (ifd0.Has(TagMake) && GetInts(ifd0, TagBitsPerSample) is long[] bits && bits.Length > 0
                    && Array.TrueForAll(bits, b => b >= 10 && b <= 14))
                    return true;
                foreach (var dir in file.Directories)
                {
                    if (HasSensorData(file, dir, 0))
                        return true;
                }
                return false;
            }

            /// <summary>
            /// Checks whether a directory holds data from the sensor, as opposed to an image.
            /// </summary>
            private static bool IsSensorData(TiffDirectory dir)
            {
                long photometric = GetInt(dir, TagPhotometric, 0);
                return photometric == PhotometricCfa || photometric == PhotometricLinearRaw || dir.Has(TagCfaPattern);
            }

            private static bool HasSensorData(TiffFile file, TiffDirectory dir, int depth)
            {
                if (IsSensorData(dir))
                    return true;
                if (depth < MaxDepth && GetInts(dir, TagSubIfds) is long[] subIfds)
                {
                    foreach (long offset in subIfds)
                    {
                        if (TiffDirectory.Read(file, offset, out _) is TiffDirectory sub && HasSensorData(file, sub, depth + 1))
                            return true;
                    }
                }
                return false;
            }

            /// <summary>
            /// Looks for previews in all the directories of a TIFF file, including the maker
            /// notes. Its first directory says how the picture is oriented.
            /// </summary>
            private void SearchTiff(TiffFile file)
            {
                var ifd0 = file.Directories[0];
                orientation = (int)GetInt(ifd0, TagOrientation, 0);
                foreach (var dir in file.Directories)
                    SearchDirectory(file, dir, 0);

                if (ifd0.TryGetData(TagLeafData, out long leafOffset, out long leafLength))
                    SearchLeafPackets(leafOffset, leafLength);

                var make = ifd0.GetBytes(TagMake) is byte[] makeBytes ? Encoding.ASCII.GetString(makeBytes).TrimEnd('\0', ' ') : "";
                if (SubDirectory(file, ifd0, TagExifIfd) is TiffDirectory exif)
                    SearchMakerNote(file, exif, make);
            }

            /// <summary>
            /// Looks for previews in a directory and its SubIFDs.
            /// </summary>
            private void SearchDirectory(TiffFile file, TiffDirectory dir, int depth)
            {
                AddImages(file, dir);
                AddBlobs(dir, false);
                if (depth < MaxDepth && GetInts(dir, TagSubIfds) is long[] subIfds)
                {
                    foreach (long offset in subIfds)
                    {
                        if (TiffDirectory.Read(file, offset, out _) is TiffDirectory sub)
                            SearchDirectory(file, sub, depth + 1);
                    }
                }
            }

            /// <summary>
            /// Adds the image of a directory, if it's stored as JPEG data in one piece, and
            /// isn't data from the sensor.
            /// </summary>
            private void AddImages(TiffFile file, TiffDirectory dir)
            {
                long jpegOffset = GetInt(dir, TagJpegInterchangeFormat, 0);
                if (jpegOffset > 0)
                    Add(file.Origin + jpegOffset, GetInt(dir, TagJpegInterchangeFormatLength, 0));

                if (IsSensorData(dir))
                    return;
                var offsets = GetInts(dir, TagStripOffsets) ?? GetInts(dir, TagTileOffsets);
                var counts = GetInts(dir, TagStripByteCounts) ?? GetInts(dir, TagTileByteCounts);
                if (offsets != null && offsets.Length == 1 && counts != null && counts.Length == 1)
                    Add(file.Origin + offsets[0], counts[0]);
            }

            /// <summary>
            /// Adds the fields of undefined bytes in a directory that hold JPEG data.
            /// </summary>
            private void AddBlobs(TiffDirectory dir, bool repair)
            {
                foreach (int tag in dir.Tags)
                {
                    if (dir.GetFieldType(tag) == TiffDirectory.TypeUndefined && dir.TryGetData(tag, out long offset, out long length) && length > 4)
                        Add(offset, length, repair);
                }
            }

            private void SearchMakerNote(TiffFile file, TiffDirectory exif, string make)
            {
                if (!exif.TryGetData(TagMakerNote, out long note, out long noteLength) || noteLength < 16)
                    return;
                var header = data.AsSpan((int)note, 16);

                if (header.StartsWith("Nikon\0"u8))
                {
                    TiffFile nikon;
                    try
                    {
                        nikon = new TiffFile(data, note + 10, false);
                    }
                    catch (ImageDecodeException)
                    {
                        return;
                    }
                    if (SubDirectory(nikon, nikon.Directories[0], TagNikonPreviewIfd) is TiffDirectory preview)
                        AddImages(nikon, preview);
                }
                else if (header.StartsWith("OLYMPUS\0"u8) || header.StartsWith("OM SYSTEM\0\0\0"u8))
                {
                    int byteOrder = header[0] == 'O' && header[1] == 'L' ? 8 : 12;
                    var view = TiffFile.View(data, note, header[byteOrder] == 'M');
                    if (TiffDirectory.Read(view, byteOrder + 4, out _) is TiffDirectory ifd)
                        SearchOlympus(view, ifd);
                }
                else if (header.StartsWith("OLYMP\0"u8))
                {
                    if (TiffDirectory.Read(file, note + 8 - file.Origin, out _) is TiffDirectory ifd)
                        SearchOlympus(file, ifd);
                }
                else if (header.StartsWith("LEICA\0"u8))
                {
                    if (TiffDirectory.Read(file, note + 8 - file.Origin, out _) is TiffDirectory ifd)
                        AddBlobs(ifd, false);
                }
                else if (make.Contains("MINOLTA", StringComparison.OrdinalIgnoreCase))
                {
                    if (TiffDirectory.Read(file, note - file.Origin, out _) is TiffDirectory ifd)
                    {
                        AddBlobs(ifd, true);
                        long start = GetInt(ifd, TagMinoltaPreviewStart, 0);
                        if (start > 0)
                            Add(file.Origin + start, GetInt(ifd, TagMinoltaPreviewLength, 0), true);
                    }
                }
            }

            private void SearchOlympus(TiffFile view, TiffDirectory ifd)
            {
                AddBlobs(ifd, false);
                if (SubDirectory(view, ifd, TagOlympusCameraSettings) is TiffDirectory settings)
                {
                    long start = GetInt(settings, TagOlympusPreviewStart, 0);
                    if (start > 0)
                        Add(view.Origin + start, GetInt(settings, TagOlympusPreviewLength, 0));
                }
            }

            private void SearchLeafPackets(long offset, long length)
            {
                long pos = offset, end = offset + length;
                while (pos + 52 <= end && data.AsSpan((int)pos, 4).SequenceEqual("PKTS"u8))
                {
                    var name = data.AsSpan((int)pos + 8, 40);
                    long size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan((int)pos + 48));
                    if (name.StartsWith("JPEG_preview_data\0"u8))
                        Add(pos + 52, size);
                    pos += 52 + size;
                }
            }

            /// <summary>
            /// Reads the directory that a field points to, or that is stored as the field's
            /// value (as some maker notes do).
            /// </summary>
            private static TiffDirectory? SubDirectory(TiffFile file, TiffDirectory dir, int tag)
            {
                if (dir.GetFieldType(tag) == TiffDirectory.TypeUndefined)
                    return dir.TryGetData(tag, out long pos, out _) ? TiffDirectory.Read(file, pos - file.Origin, out _) : null;
                long offset = GetInt(dir, tag, 0);
                return offset > 0 ? TiffDirectory.Read(file, offset, out _) : null;
            }

            private static long GetInt(TiffDirectory dir, int tag, long defaultValue)
            {
                return GetInts(dir, tag) is long[] values && values.Length > 0 ? values[0] : defaultValue;
            }

            /// <summary>
            /// Gets the values of a field of unsigned integers, or null if it is missing or
            /// has some other type (as can happen in maker notes, which reuse tag numbers).
            /// </summary>
            private static long[]? GetInts(TiffDirectory dir, int tag)
            {
                return dir.GetFieldType(tag) switch
                {
                    TiffDirectory.TypeShort or TiffDirectory.TypeLong or TiffDirectory.TypeIfd
                        or TiffDirectory.TypeLong8 or TiffDirectory.TypeIfd8 => dir.GetLongs(tag),
                    _ => null
                };
            }

            #endregion

            #region Other formats

            private void SearchRaf()
            {
                if (data.Length < 92)
                    return;
                Add(BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(84)), BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(88)));
            }

            private void SearchMrw()
            {
                long end = Math.Min(8 + (long)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4)), data.Length);
                for (long pos = 8; pos + 8 <= end;)
                {
                    long size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan((int)pos + 4));
                    if (data.AsSpan((int)pos, 4).SequenceEqual("\0TTW"u8))
                    {
                        SearchTiff(new TiffFile(data, pos + 8, false));
                        return;
                    }
                    pos += 8 + size;
                }
            }

            private void SearchX3f()
            {
                if (data.Length < 16)
                    return;
                long dir = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(data.Length - 4));
                if (dir > data.Length - 12 || !data.AsSpan((int)dir, 4).SequenceEqual("SECd"u8))
                    return;
                long count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)dir + 8));
                count = Math.Min(count, (data.Length - dir - 12) / 12);
                for (long i = 0; i < count; i++)
                {
                    var entry = data.AsSpan((int)(dir + 12 + i * 12), 12);
                    long offset = BinaryPrimitives.ReadUInt32LittleEndian(entry);
                    long length = BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
                    if ((entry[8..].SequenceEqual("IMAG"u8) || entry[8..].SequenceEqual("IMA2"u8)) && length > 28 && offset <= data.Length - 28)
                        Add(offset + 28, length - 28);
                }
            }

            private void SearchCiff()
            {
                bool bigEndian = data[0] == 'M';
                long headerLength = ReadU32(data.AsSpan(2), bigEndian);
                if (headerLength < 14 || headerLength >= data.Length)
                    return;
                SearchHeap(headerLength, data.Length - headerLength, bigEndian, 0);
            }

            /// <summary>
            /// Looks for previews in a heap of a CIFF file, and the heaps within it.
            /// </summary>
            private void SearchHeap(long start, long length, bool bigEndian, int depth)
            {
                if (length < 6)
                    return;
                long end = start + length;
                long table = start + ReadU32(data.AsSpan((int)end - 4), bigEndian);
                if (table < start || table > end - 2)
                    return;
                int count = (int)Math.Min(ReadU16(data.AsSpan((int)table), bigEndian), (end - table - 2) / 10);
                for (int i = 0; i < count; i++)
                {
                    var record = data.AsSpan((int)table + 2 + i * 10, 10);
                    int tag = ReadU16(record, bigEndian);
                    long size = ReadU32(record[2..], bigEndian);
                    long pos = start + ReadU32(record[6..], bigEndian);
                    // The top two bits say where the data is; anything but 0 means it's in the record itself.
                    if ((tag & 0xC000) != 0 || pos > end || size > end - pos)
                        continue;
                    if ((tag >> 8) == 0x28 || (tag >> 8) == 0x30)
                    {
                        if (depth < MaxDepth)
                            SearchHeap(pos, size, bigEndian, depth + 1);
                    }
                    else if (tag == 0x2007 || tag == 0x2008)
                    {
                        Add(pos, size);
                    }
                    else if (tag == 0x1810 && size >= 16)
                    {
                        // Width, height, pixel aspect ratio, and rotation in degrees.
                        int rotation = (int)ReadU32(data.AsSpan((int)pos + 12), bigEndian);
                        orientation = ((rotation % 360) + 360) % 360 switch
                        {
                            90 => 6,
                            180 => 3,
                            270 => 8,
                            _ => 1
                        };
                    }
                }
            }

            private static readonly byte[] CanonUuid = Convert.FromHexString("85C0B687820F11E08111F4CE462B6A48");
            private static readonly byte[] PreviewUuid = Convert.FromHexString("EAF42B5E1C984B88B9FBB7DC406E4D16");

            /// <summary>
            /// Looks for previews in a series of boxes of a CR3 file, and the boxes within them.
            /// </summary>
            private void SearchBoxes(long start, long end, int depth)
            {
                for (long pos = start; pos + 8 <= end;)
                {
                    long size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan((int)pos));
                    var type = data.AsSpan((int)pos + 4, 4);
                    long header = 8;
                    if (size == 1)
                    {
                        if (pos + 16 > end)
                            return;
                        size = (long)Math.Min(BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan((int)pos + 8)), (ulong)long.MaxValue);
                        header = 16;
                    }
                    else if (size == 0)
                    {
                        size = end - pos;
                    }
                    if (size < header || size > end - pos)
                        return;
                    long content = pos + header, boxEnd = pos + size;

                    if (type.SequenceEqual("moov"u8) || type.SequenceEqual("trak"u8) || type.SequenceEqual("mdia"u8) || type.SequenceEqual("minf"u8))
                    {
                        if (depth < MaxDepth)
                            SearchBoxes(content, boxEnd, depth + 1);
                    }
                    else if (type.SequenceEqual("stbl"u8))
                    {
                        AddTrackSample(content, boxEnd);
                    }
                    else if (type.SequenceEqual("uuid"u8) && boxEnd - content >= 16 && depth < MaxDepth)
                    {
                        var uuid = data.AsSpan((int)content, 16);
                        if (uuid.SequenceEqual(CanonUuid))
                            SearchBoxes(content + 16, boxEnd, depth + 1);
                        else if (uuid.SequenceEqual(PreviewUuid))
                            SearchBoxes(content + 24, boxEnd, depth + 1);
                    }
                    else if (type.SequenceEqual("THMB"u8) || type.SequenceEqual("PRVW"u8))
                    {
                        // A 16-byte header, which includes the size of the image.
                        Add(content + 16, boxEnd - content - 16);
                    }
                    else if (type.SequenceEqual("CMT1"u8))
                    {
                        // The first directory of a TIFF file.
                        try
                        {
                            orientation = (int)GetInt(new TiffFile(data, content, false).Directories[0], TagOrientation, 0);
                        }
                        catch (ImageDecodeException)
                        {
                        }
                    }
                    pos = boxEnd;
                }
            }

            /// <summary>
            /// Adds the first sample of a track, given the boxes of its sample table: the
            /// sample sizes (stsz), and the chunk offsets (stco, or co64 for 64-bit offsets).
            /// </summary>
            private void AddTrackSample(long start, long end)
            {
                long sampleSize = -1, offset = -1;
                for (long pos = start; pos + 8 <= end;)
                {
                    long size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan((int)pos));
                    if (size < 8 || size > end - pos)
                        return;
                    var type = data.AsSpan((int)pos + 4, 4);
                    var content = data.AsSpan((int)pos + 8, (int)size - 8);
                    // Each starts with a version and flags, then a count (or for stsz, a size
                    // that applies to every sample, or 0 if they have sizes of their own).
                    if (type.SequenceEqual("stsz"u8) && content.Length >= 12)
                    {
                        sampleSize = BinaryPrimitives.ReadUInt32BigEndian(content[4..]);
                        if (sampleSize == 0 && content.Length >= 16)
                            sampleSize = BinaryPrimitives.ReadUInt32BigEndian(content[12..]);
                    }
                    else if (type.SequenceEqual("stco"u8) && content.Length >= 12)
                    {
                        offset = BinaryPrimitives.ReadUInt32BigEndian(content[8..]);
                    }
                    else if (type.SequenceEqual("co64"u8) && content.Length >= 16)
                    {
                        offset = (long)Math.Min(BinaryPrimitives.ReadUInt64BigEndian(content[8..]), (ulong)long.MaxValue);
                    }
                    pos += size;
                }
                if (offset > 0 && sampleSize > 0)
                    Add(offset, sampleSize);
            }

            private static int ReadU16(ReadOnlySpan<byte> s, bool bigEndian)
            {
                return bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s);
            }

            private static long ReadU32(ReadOnlySpan<byte> s, bool bigEndian)
            {
                return bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s);
            }

            #endregion
        }
    }
}
