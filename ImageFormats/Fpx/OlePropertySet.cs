#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;

/*

A minimal reader for OLE property set streams (the "\005..." streams found in
compound files), as specified in Microsoft's [MS-OLEPS].

A property set stream has a 28-byte header (byte order mark, version, OS, class
ID and number of sections), followed by a table of sections, each given by a
format ID and an offset. A section starts with its size and number of properties,
then a table of property IDs and offsets (relative to the start of the section),
and then the values themselves, each starting with a 32-bit type.

Only the first section is read, and only the few types we need.

Copyright 2026+ Dmitry Brant
https://dmitrybrant.com

License: MIT
*/

namespace DmitryBrant.ImageFormats
{
    internal sealed class OlePropertySet
    {
        private const int VtI2 = 2;
        private const int VtI4 = 3;
        private const int VtUi2 = 18;
        private const int VtUi4 = 19;
        private const int VtBlob = 65;
        private const int VtCf = 71;
        private const int VtVector = 0x1000;

        private readonly byte[] data;

        // Offset of the value (just past its type) of each property, by ID.
        private readonly Dictionary<uint, int> values = new();
        private readonly Dictionary<uint, int> types = new();

        public OlePropertySet(byte[] data)
        {
            this.data = data;
            if (data.Length < 48 || BinaryPrimitives.ReadUInt16LittleEndian(data) != 0xFFFE)
                throw new ImageDecodeException("Invalid property set stream.");
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(24)) == 0)
                return;

            long section = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(44));
            if (section > data.Length - 8)
                throw new ImageDecodeException("Invalid property set section.");
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)section + 4));
            for (long i = 0; i < count && section + 8 + i * 8 + 8 <= data.Length; i++)
            {
                int entry = (int)(section + 8 + i * 8);
                uint id = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(entry));
                long offset = section + BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(entry + 4));
                if (offset > data.Length - 4)
                    continue;
                types[id] = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan((int)offset));
                values[id] = (int)offset + 4;
            }
        }

        /// <summary>
        /// Gets the value of an integer property, or null if it is missing or not an integer.
        /// </summary>
        public uint? GetUInt(uint id)
        {
            if (!values.TryGetValue(id, out int offset))
                return null;
            switch (types[id])
            {
                case VtI2:
                case VtUi2:
                    if (offset + 2 <= data.Length)
                        return BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
                    break;
                case VtI4:
                case VtUi4:
                    if (offset + 4 <= data.Length)
                        return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
                    break;
            }
            return null;
        }

        /// <summary>
        /// Gets the value of a vector of 32-bit integers, or null if it is missing or not
        /// such a vector.
        /// </summary>
        public uint[]? GetUIntVector(uint id)
        {
            if (!values.TryGetValue(id, out int offset) || (types[id] != (VtVector | VtUi4) && types[id] != (VtVector | VtI4)))
                return null;
            if (offset + 4 > data.Length)
                return null;
            long count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
            if (count > (data.Length - offset - 4) / 4)
                return null;
            var result = new uint[count];
            for (int i = 0; i < count; i++)
                result[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4 + i * 4));
            return result;
        }

        /// <summary>
        /// Gets the contents of a blob property, or null if it is missing or not a blob.
        /// </summary>
        public byte[]? GetBlob(uint id)
        {
            if (!values.TryGetValue(id, out int offset) || types[id] != VtBlob || offset + 4 > data.Length)
                return null;
            long size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
            if (size > data.Length - offset - 4)
                return null;
            return data.AsSpan(offset + 4, (int)size).ToArray();
        }

        /// <summary>
        /// Gets the contents of a clipboard data property (such as the thumbnail in a
        /// summary information property set) that holds a standard Windows clipboard
        /// format, along with that format (e.g. 8 for CF_DIB). Returns null if the property
        /// is missing or holds anything else.
        /// </summary>
        public byte[]? GetClipboardData(uint id, out int format)
        {
            format = 0;
            if (!values.TryGetValue(id, out int offset) || types[id] != VtCf || offset + 12 > data.Length)
                return null;

            // The size counts a 32-bit tag, which is -1 for a Windows clipboard format, then
            // the format itself, and then the data.
            long size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
            int tag = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 4));
            if (tag != -1 || size < 8 || size > data.Length - offset - 4)
                return null;
            format = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 8));
            return data.AsSpan(offset + 12, (int)size - 8).ToArray();
        }
    }
}
