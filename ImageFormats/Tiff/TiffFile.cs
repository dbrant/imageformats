#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;

/*

The structure of a TIFF file: a header, followed (anywhere in the file) by a chain of
image file directories (IFDs), each of which describes one image (or "page") as a
list of tagged fields.

A classic TIFF file starts with "II" (little-endian) or "MM" (big-endian), then the
number 42, then the 32-bit offset of the first IFD. An IFD is a 16-bit count of
entries, then the entries, then the 32-bit offset of the next IFD (or 0). Each entry
is 12 bytes: the tag, the type of its values, the number of values, and either the
values themselves (if they fit in 4 bytes) or their offset.

BigTIFF is the same, except that the number is 43, offsets and counts are 64 bits,
entries are 20 bytes, and values of up to 8 bytes are stored inline.

Copyright 2026+ Dmitry Brant
https://dmitrybrant.com

License: MIT
*/

namespace DmitryBrant.ImageFormats
{
    internal sealed class TiffFile
    {
        private const int MaxDirectories = 65536;

        public readonly byte[] Data;
        public readonly bool BigEndian;
        public readonly bool BigTiff;
        public readonly List<TiffDirectory> Directories = new();

        public TiffFile(byte[] data)
        {
            Data = data;
            if (data.Length < 8 || !TiffReader.HasSignature(data))
                throw new ImageDecodeException("This is not a valid TIFF file.");
            BigEndian = data[0] == 'M';
            BigTiff = ReadU16(2) == 43;

            long offset;
            if (BigTiff)
            {
                if (data.Length < 16 || ReadU16(4) != 8)
                    throw new ImageDecodeException("Unsupported BigTIFF offset size.");
                offset = (long)ReadU64(8);
            }
            else
            {
                offset = ReadU32(4);
            }

            var visited = new HashSet<long>();
            while (offset > 0 && visited.Add(offset) && Directories.Count < MaxDirectories)
            {
                var dir = TiffDirectory.Read(this, offset, out offset);
                if (dir == null)
                    break;
                Directories.Add(dir);
            }
            if (Directories.Count == 0)
                throw new ImageDecodeException("TIFF file contains no images.");
        }

        public int ReadU16(long pos) => BigEndian ? BinaryPrimitives.ReadUInt16BigEndian(Data.AsSpan((int)pos)) : BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan((int)pos));
        public uint ReadU32(long pos) => BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(Data.AsSpan((int)pos)) : BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan((int)pos));
        public ulong ReadU64(long pos) => BigEndian ? BinaryPrimitives.ReadUInt64BigEndian(Data.AsSpan((int)pos)) : BinaryPrimitives.ReadUInt64LittleEndian(Data.AsSpan((int)pos));
    }

    internal sealed class TiffDirectory
    {
        public const int TypeByte = 1;
        public const int TypeAscii = 2;
        public const int TypeShort = 3;
        public const int TypeLong = 4;
        public const int TypeRational = 5;
        public const int TypeSByte = 6;
        public const int TypeUndefined = 7;
        public const int TypeSShort = 8;
        public const int TypeSLong = 9;
        public const int TypeSRational = 10;
        public const int TypeFloat = 11;
        public const int TypeDouble = 12;
        public const int TypeIfd = 13;
        public const int TypeLong8 = 16;
        public const int TypeSLong8 = 17;
        public const int TypeIfd8 = 18;

        private readonly struct Entry
        {
            public readonly int Type;
            public readonly long Count;
            public readonly long Offset;   // where the values are

            public Entry(int type, long count, long offset)
            {
                Type = type;
                Count = count;
                Offset = offset;
            }
        }

        private readonly TiffFile file;
        private readonly Dictionary<int, Entry> entries = new();

        private TiffDirectory(TiffFile file)
        {
            this.file = file;
        }

        private static int TypeSize(int type) => type switch
        {
            TypeByte or TypeAscii or TypeSByte or TypeUndefined => 1,
            TypeShort or TypeSShort => 2,
            TypeLong or TypeSLong or TypeFloat or TypeIfd => 4,
            TypeRational or TypeSRational or TypeDouble or TypeLong8 or TypeSLong8 or TypeIfd8 => 8,
            _ => 0
        };

        /// <summary>
        /// Reads the IFD at the given offset, or returns null if it is out of bounds.
        /// </summary>
        public static TiffDirectory? Read(TiffFile file, long offset, out long nextOffset)
        {
            nextOffset = 0;
            int countSize = file.BigTiff ? 8 : 2;
            int entrySize = file.BigTiff ? 20 : 12;
            int inlineSize = file.BigTiff ? 8 : 4;
            long length = file.Data.Length;
            if (offset < 0 || offset > length - countSize)
                return null;

            long count = file.BigTiff ? (long)Math.Min(file.ReadU64(offset), long.MaxValue) : file.ReadU16(offset);
            long pos = offset + countSize;
            count = Math.Min(count, (length - pos) / entrySize);

            var dir = new TiffDirectory(file);
            for (long i = 0; i < count; i++, pos += entrySize)
            {
                int tag = file.ReadU16(pos);
                int type = file.ReadU16(pos + 2);
                long n = file.BigTiff ? (long)Math.Min(file.ReadU64(pos + 4), long.MaxValue) : file.ReadU32(pos + 4);
                long valuePos = pos + (file.BigTiff ? 12 : 8);
                int size = TypeSize(type);
                if (size == 0)
                    continue;
                long valueOffset = n * size <= inlineSize ? valuePos
                    : file.BigTiff ? (long)Math.Min(file.ReadU64(valuePos), long.MaxValue) : file.ReadU32(valuePos);
                if (n > (length - Math.Min(valueOffset, length)) / size)
                    continue;
                dir.entries.TryAdd(tag, new Entry(type, n, valueOffset));
            }

            if (pos <= length - inlineSize)
                nextOffset = file.BigTiff ? (long)Math.Min(file.ReadU64(pos), long.MaxValue) : file.ReadU32(pos);
            return dir;
        }

        public bool Has(int tag) => entries.ContainsKey(tag);

        /// <summary>
        /// Gets the first value of an integer field, or the given default if it is missing.
        /// </summary>
        public long GetLong(int tag, long defaultValue)
        {
            var values = GetLongs(tag);
            return values != null && values.Length > 0 ? values[0] : defaultValue;
        }

        /// <summary>
        /// Gets the values of an integer field, or null if it is missing or not integers.
        /// </summary>
        public long[]? GetLongs(int tag)
        {
            if (!entries.TryGetValue(tag, out var e))
                return null;
            var result = new long[e.Count];
            for (int i = 0; i < e.Count; i++)
            {
                long p = e.Offset + (long)i * TypeSize(e.Type);
                result[i] = e.Type switch
                {
                    TypeByte or TypeUndefined => file.Data[p],
                    TypeSByte => (sbyte)file.Data[p],
                    TypeShort => file.ReadU16(p),
                    TypeSShort => (short)file.ReadU16(p),
                    TypeLong or TypeIfd => file.ReadU32(p),
                    TypeSLong => (int)file.ReadU32(p),
                    TypeLong8 or TypeSLong8 or TypeIfd8 => (long)file.ReadU64(p),
                    _ => throw new ImageDecodeException("TIFF field " + tag + " has an unexpected type.")
                };
            }
            return result;
        }

        /// <summary>
        /// Gets the values of a numeric field (including rationals and floats), or null if
        /// it is missing.
        /// </summary>
        public double[]? GetDoubles(int tag)
        {
            if (!entries.TryGetValue(tag, out var e))
                return null;
            var result = new double[e.Count];
            for (int i = 0; i < e.Count; i++)
            {
                long p = e.Offset + (long)i * TypeSize(e.Type);
                switch (e.Type)
                {
                    case TypeRational:
                    case TypeSRational:
                    {
                        double num = e.Type == TypeRational ? file.ReadU32(p) : (int)file.ReadU32(p);
                        double den = e.Type == TypeRational ? file.ReadU32(p + 4) : (int)file.ReadU32(p + 4);
                        result[i] = den == 0 ? 0 : num / den;
                        break;
                    }
                    case TypeFloat:
                        result[i] = BitConverter.Int32BitsToSingle((int)file.ReadU32(p));
                        break;
                    case TypeDouble:
                        result[i] = BitConverter.Int64BitsToDouble((long)file.ReadU64(p));
                        break;
                    default:
                        return Array.ConvertAll(GetLongs(tag)!, v => (double)v);
                }
            }
            return result;
        }

        /// <summary>
        /// Gets the raw bytes of a field's values, or null if it is missing.
        /// </summary>
        public byte[]? GetBytes(int tag)
        {
            if (!entries.TryGetValue(tag, out var e))
                return null;
            return file.Data.AsSpan((int)e.Offset, (int)(e.Count * TypeSize(e.Type))).ToArray();
        }
    }
}
