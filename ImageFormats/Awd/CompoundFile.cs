#nullable enable
using System;
using System.Collections.Generic;
using System.Text;

/*

A minimal, read-only reader for Microsoft Compound File Binary (a.k.a. OLE
Structured Storage) files, which is the container format of Microsoft Fax (.AWD)
documents, among many others.

A compound file is a little file system inside a file. It is divided into sectors
(usually 512 bytes), which are chained together by a file allocation table, the
FAT. The FAT itself lives in sectors listed by the "DIFAT", the first 109 entries
of which are in the file header. Streams that are shorter than a cutoff (usually
4096 bytes) are instead stored in 64-byte "mini sectors" inside a separate "mini
stream", with their own mini FAT. The directory is a stream of 128-byte entries,
and the children of each storage are kept as a red-black tree of directory
entries, which we simply walk in order.

This follows Microsoft's [MS-CFB] specification, and only implements as much of
it as is needed to read streams: no writing, and no validation of the tree's
coloring or sort order.

Copyright 2026+ Dmitry Brant
https://dmitrybrant.com

License: MIT
*/

namespace DmitryBrant.ImageFormats
{
    internal class CompoundFileEntry
    {
        public const int TypeStorage = 1;
        public const int TypeStream = 2;
        public const int TypeRoot = 5;

        public int Id;
        public string Name = "";
        public int Type;
        public int Left;
        public int Right;
        public int Child;
        public int StartSector;
        public long Size;

        public bool IsStorage => Type == TypeStorage || Type == TypeRoot;
        public bool IsStream => Type == TypeStream;
    }

    internal class CompoundFile
    {
        private static readonly byte[] Signature = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };

        private const int HeaderSize = 512;
        private const int DirEntrySize = 128;
        private const int EndOfChain = -2;
        private const int HeaderDifatCount = 109;

        private readonly byte[] data;
        private readonly int sectorSize;
        private readonly int miniSectorSize;
        private readonly uint miniStreamCutoff;
        private readonly int[] fat;
        private readonly int[] miniFat;
        private readonly byte[] miniStream;
        private readonly List<CompoundFileEntry> entries = new();

        public CompoundFileEntry Root => entries[0];

        public static bool HasSignature(byte[] header)
        {
            if (header.Length < Signature.Length)
                return false;
            for (int i = 0; i < Signature.Length; i++)
            {
                if (header[i] != Signature[i])
                    return false;
            }
            return true;
        }

        public CompoundFile(byte[] data)
        {
            this.data = data;
            if (data.Length < HeaderSize || !HasSignature(data))
                throw new ImageDecodeException("This is not a valid compound file.");

            int majorVersion = BitConverter.ToUInt16(data, 0x1A);
            int sectorShift = BitConverter.ToUInt16(data, 0x1E);
            int miniSectorShift = BitConverter.ToUInt16(data, 0x20);
            if (sectorShift < 7 || sectorShift > 16 || miniSectorShift < 2 || miniSectorShift >= sectorShift)
                throw new ImageDecodeException("Invalid compound file sector size.");
            sectorSize = 1 << sectorShift;
            miniSectorSize = 1 << miniSectorShift;

            uint numFatSectors = BitConverter.ToUInt32(data, 0x2C);
            int firstDirSector = BitConverter.ToInt32(data, 0x30);
            miniStreamCutoff = BitConverter.ToUInt32(data, 0x38);
            int firstMiniFatSector = BitConverter.ToInt32(data, 0x3C);
            int firstDifatSector = BitConverter.ToInt32(data, 0x44);

            int totalSectors = (int)((data.Length + sectorSize - 1) / sectorSize);
            if (numFatSectors > totalSectors)
                throw new ImageDecodeException("Invalid compound file FAT size.");

            // Gather the list of FAT sectors: the first 109 are in the header, and any
            // more are in a chain of DIFAT sectors, each ending with a pointer to the next.
            var fatSectors = new List<int>();
            for (int i = 0; i < HeaderDifatCount && fatSectors.Count < numFatSectors; i++)
                fatSectors.Add(BitConverter.ToInt32(data, 0x4C + i * 4));
            int difatSector = firstDifatSector;
            int entriesPerSector = sectorSize / 4;
            int guard = 0;
            while (fatSectors.Count < numFatSectors && difatSector >= 0 && guard++ < totalSectors)
            {
                int offset = SectorOffset(difatSector);
                for (int i = 0; i < entriesPerSector - 1 && fatSectors.Count < numFatSectors; i++)
                    fatSectors.Add(BitConverter.ToInt32(data, offset + i * 4));
                difatSector = BitConverter.ToInt32(data, offset + (entriesPerSector - 1) * 4);
            }

            fat = new int[fatSectors.Count * entriesPerSector];
            for (int i = 0; i < fatSectors.Count; i++)
                Buffer.BlockCopy(data, SectorOffset(fatSectors[i]), fat, i * sectorSize, sectorSize);

            byte[] dirData = ReadChain(fat, firstDirSector, -1);
            for (int i = 0; i + DirEntrySize <= dirData.Length; i += DirEntrySize)
            {
                var entry = new CompoundFileEntry { Id = entries.Count };
                int nameLength = BitConverter.ToUInt16(dirData, i + 0x40);
                if (nameLength >= 2 && nameLength <= 64)
                    entry.Name = Encoding.Unicode.GetString(dirData, i, nameLength - 2);
                entry.Type = dirData[i + 0x42];
                entry.Left = BitConverter.ToInt32(dirData, i + 0x44);
                entry.Right = BitConverter.ToInt32(dirData, i + 0x48);
                entry.Child = BitConverter.ToInt32(dirData, i + 0x4C);
                entry.StartSector = BitConverter.ToInt32(dirData, i + 0x74);
                // Version 3 files only use the low 32 bits of the size, and the high bits
                // may contain garbage.
                entry.Size = majorVersion == 3 ? BitConverter.ToUInt32(dirData, i + 0x78) : BitConverter.ToInt64(dirData, i + 0x78);
                entries.Add(entry);
            }
            if (entries.Count == 0 || entries[0].Type != CompoundFileEntry.TypeRoot)
                throw new ImageDecodeException("Compound file has no root entry.");

            byte[] miniFatData = firstMiniFatSector >= 0 ? ReadChain(fat, firstMiniFatSector, -1) : Array.Empty<byte>();
            miniFat = new int[miniFatData.Length / 4];
            Buffer.BlockCopy(miniFatData, 0, miniFat, 0, miniFat.Length * 4);

            // The root entry's stream is the mini stream, which holds all the small streams.
            miniStream = Root.StartSector >= 0 ? ReadChain(fat, Root.StartSector, Root.Size) : Array.Empty<byte>();
        }

        /// <summary>
        /// Enumerates the entries directly inside the given storage.
        /// </summary>
        public IEnumerable<CompoundFileEntry> Children(CompoundFileEntry storage)
        {
            var result = new List<CompoundFileEntry>();
            var visited = new HashSet<int>();
            var stack = new Stack<int>();
            int id = storage.Child;
            while (IsValidId(id) || stack.Count > 0)
            {
                while (IsValidId(id) && visited.Add(id))
                {
                    stack.Push(id);
                    id = entries[id].Left;
                }
                if (stack.Count == 0)
                    break;
                id = stack.Pop();
                result.Add(entries[id]);
                id = entries[id].Right;
            }
            return result;
        }

        /// <summary>
        /// Finds the entry with the given name directly inside the given storage, or null.
        /// Names in compound files are compared without regard to case.
        /// </summary>
        public CompoundFileEntry? Find(CompoundFileEntry storage, string name)
        {
            foreach (var entry in Children(storage))
            {
                if (string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
                    return entry;
            }
            return null;
        }

        /// <summary>
        /// Reads the whole contents of the given stream entry.
        /// </summary>
        public byte[] Read(CompoundFileEntry entry)
        {
            if (!entry.IsStream || entry.Size <= 0)
                return Array.Empty<byte>();
            if (entry.Size > data.Length)
                throw new ImageDecodeException("Compound file stream is larger than the file.");
            if (entry.Size < miniStreamCutoff)
                return ReadMiniChain(entry.StartSector, entry.Size);
            return ReadChain(fat, entry.StartSector, entry.Size);
        }

        private bool IsValidId(int id) => id >= 0 && id < entries.Count;

        private int SectorOffset(int sector)
        {
            long offset = (long)(sector + 1) * sectorSize;
            if (sector < 0 || offset + sectorSize > data.Length)
                throw new ImageDecodeException("Compound file sector is out of range.");
            return (int)offset;
        }

        /// <summary>
        /// Reads a chain of regular sectors. If the size is negative, the whole chain is read.
        /// </summary>
        private byte[] ReadChain(int[] table, int start, long size)
        {
            var output = new List<byte>();
            int sector = start;
            int guard = 0;
            while (sector >= 0 && (size < 0 || output.Count < size))
            {
                if (guard++ > table.Length)
                    throw new ImageDecodeException("Compound file has a cyclic sector chain.");
                int offset = SectorOffset(sector);
                output.AddRange(new ArraySegment<byte>(data, offset, sectorSize));
                sector = sector < table.Length ? table[sector] : EndOfChain;
            }
            if (size >= 0 && output.Count > size)
                output.RemoveRange((int)size, output.Count - (int)size);
            return output.ToArray();
        }

        private byte[] ReadMiniChain(int start, long size)
        {
            var output = new byte[size];
            int pos = 0;
            int sector = start;
            int guard = 0;
            while (sector >= 0 && pos < size)
            {
                if (guard++ > miniFat.Length)
                    throw new ImageDecodeException("Compound file has a cyclic sector chain.");
                long offset = (long)sector * miniSectorSize;
                if (offset + miniSectorSize > miniStream.Length)
                    throw new ImageDecodeException("Compound file mini sector is out of range.");
                int count = (int)Math.Min(miniSectorSize, size - pos);
                Buffer.BlockCopy(miniStream, (int)offset, output, pos, count);
                pos += count;
                sector = sector < miniFat.Length ? miniFat[sector] : EndOfChain;
            }
            if (pos < size)
                throw new ImageDecodeException("Compound file stream is truncated.");
            return output;
        }
    }
}
