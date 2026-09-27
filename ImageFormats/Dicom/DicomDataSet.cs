using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

/*

Parser for the data set at the heart of a DICOM file: a flat list of "data
elements", each identified by a (group, element) tag, sorted by tag. Depending on
the transfer syntax, each element either states its value representation (VR,
i.e. its data type) explicitly, or leaves it implicit, to be looked up in the
data dictionary. Elements of type SQ (sequence) contain a list of items, each of
which is itself a nested data set. Both sequences and items can be of undefined
length, in which case they are terminated by delimiter tags instead.

Pixel data may also be of undefined length, in which case it's "encapsulated":
split into fragments, each wrapped in an item tag, with the first fragment
holding an optional table of offsets to the start of each frame.

Since real-world files are frequently not quite right (wrong transfer syntax,
explicit VR elements in implicit VR files, truncation), the parser tries to be
lenient, and holds on to whatever it managed to read if it runs into trouble.

Copyright 2026+ Dmitry Brant
https://dmitrybrant.com

License: MIT
*/

namespace DmitryBrant.ImageFormats
{
    internal sealed class DicomElement
    {
        public uint Tag;

        /// <summary>Value representation, or empty if not known (implicit VR).</summary>
        public string Vr = "";

        /// <summary>Position and length of the value in the data set's buffer.</summary>
        public int Offset, Length;

        /// <summary>Items of a sequence, or null if this element isn't one.</summary>
        public List<DicomDataSet>? Items;

        /// <summary>Fragments of encapsulated pixel data (including the offset table), or null.</summary>
        public List<(int Offset, int Length)>? Fragments;
    }

    internal sealed class DicomDataSet
    {
        public const uint UndefinedLength = 0xFFFFFFFF;

        public const uint TagItem = 0xFFFEE000;
        public const uint TagItemDelimiter = 0xFFFEE00D;
        public const uint TagSequenceDelimiter = 0xFFFEE0DD;
        public const uint TagPixelData = 0x7FE00010;
        public const uint TagFloatPixelData = 0x7FE00008;
        public const uint TagDoublePixelData = 0x7FE00009;

        private const int MaxDepth = 64;

        // Value representations whose length is stored in 4 bytes rather than 2 (in explicit VR).
        private static readonly HashSet<string> LongVrs = new() { "OB", "OD", "OF", "OL", "OV", "OW", "SQ", "SV", "UC", "UN", "UR", "UT", "UV" };

        private static readonly HashSet<string> AllVrs = new()
        {
            "AE", "AS", "AT", "CS", "DA", "DS", "DT", "FD", "FL", "IS", "LO", "LT", "OB", "OD", "OF", "OL", "OV", "OW",
            "PN", "SH", "SL", "SQ", "SS", "ST", "SV", "TM", "UC", "UI", "UL", "UN", "UR", "US", "UT", "UV"
        };

        // VRs of the elements we actually read, for data sets that don't state them.
        private static readonly Dictionary<uint, string> ImplicitVrs = new()
        {
            { 0x00020010, "UI" }, { 0x00080060, "CS" },
            { 0x00280002, "US" }, { 0x00280004, "CS" }, { 0x00280006, "US" }, { 0x00280008, "IS" },
            { 0x00280010, "US" }, { 0x00280011, "US" }, { 0x00280100, "US" }, { 0x00280101, "US" },
            { 0x00280102, "US" }, { 0x00280103, "US" }, { 0x00280120, "US" }, { 0x00281050, "DS" },
            { 0x00281051, "DS" }, { 0x00281052, "DS" }, { 0x00281053, "DS" }, { 0x00281056, "CS" },
            { 0x00281101, "US" }, { 0x00281102, "US" }, { 0x00281103, "US" }, { 0x00283002, "US" },
            { 0x00283006, "OW" }, { 0x7FE00001, "OV" }
        };

        public byte[] Buffer { get; }
        public bool LittleEndian { get; }
        public Dictionary<uint, DicomElement> Elements { get; } = new();

        private DicomDataSet(byte[] buffer, bool littleEndian)
        {
            Buffer = buffer;
            LittleEndian = littleEndian;
        }

        public static bool IsVr(byte a, byte b)
        {
            return a >= 'A' && a <= 'Z' && b >= 'A' && b <= 'Z' && AllVrs.Contains(new string(new[] { (char)a, (char)b }));
        }

        /// <summary>
        /// Parses the data set that starts at the given position and runs to the end
        /// of the buffer.
        /// </summary>
        public static DicomDataSet Parse(byte[] buffer, int start, bool explicitVr, bool littleEndian)
        {
            int pos = start;
            return ParseDataSet(buffer, ref pos, buffer.Length, explicitVr, littleEndian, false, 0);
        }

        private static ushort ReadUInt16(byte[] b, int pos, bool le)
        {
            return le ? (ushort)(b[pos] | (b[pos + 1] << 8)) : (ushort)((b[pos] << 8) | b[pos + 1]);
        }

        private static uint ReadUInt32(byte[] b, int pos, bool le)
        {
            return le
                ? (uint)(b[pos] | (b[pos + 1] << 8) | (b[pos + 2] << 16) | (b[pos + 3] << 24))
                : (uint)((b[pos] << 24) | (b[pos + 1] << 16) | (b[pos + 2] << 8) | b[pos + 3]);
        }

        private static uint ReadTag(byte[] b, int pos, bool le)
        {
            return ((uint)ReadUInt16(b, pos, le) << 16) | ReadUInt16(b, pos + 2, le);
        }

        private static DicomDataSet ParseDataSet(byte[] b, ref int pos, int end, bool explicitVr, bool le, bool inItem, int depth)
        {
            var ds = new DicomDataSet(b, le);
            try
            {
                while (pos + 8 <= end)
                {
                    uint tag = ReadTag(b, pos, le);

                    if ((tag >> 16) == 0xFFFE)
                    {
                        uint delimLen = ReadUInt32(b, pos + 4, le);
                        pos += 8;
                        if (tag == TagItemDelimiter || tag == TagSequenceDelimiter)
                        {
                            if (inItem)
                                break;
                            continue; // stray delimiter
                        }
                        // A stray item outside of a sequence: skip it.
                        if (delimLen != UndefinedLength)
                            pos += (int)Math.Min(delimLen, (uint)(end - pos));
                        continue;
                    }

                    var element = new DicomElement { Tag = tag };
                    uint length;
                    if (explicitVr && IsVr(b[pos + 4], b[pos + 5]))
                    {
                        element.Vr = Encoding.ASCII.GetString(b, pos + 4, 2);
                        if (LongVrs.Contains(element.Vr))
                        {
                            if (pos + 12 > end)
                                break;
                            length = ReadUInt32(b, pos + 8, le);
                            pos += 12;
                        }
                        else
                        {
                            length = ReadUInt16(b, pos + 6, le);
                            pos += 8;
                        }
                    }
                    else
                    {
                        // Implicit VR (or an explicit VR data set with an element that
                        // isn't; we try to cope with that too).
                        length = ReadUInt32(b, pos + 4, le);
                        pos += 8;
                    }

                    if ((tag == TagPixelData || tag == TagFloatPixelData || tag == TagDoublePixelData) && length == UndefinedLength)
                    {
                        element.Fragments = ParseFragments(b, ref pos, end, le);
                        element.Offset = pos;
                    }
                    else if (element.Vr == "SQ" || length == UndefinedLength || (element.Vr is "" or "UN" && LooksLikeItem(b, pos, end, length, le)))
                    {
                        // A sequence. The contents of a sequence of unknown VR are always
                        // implicit VR little endian.
                        bool seqExplicit = element.Vr == "UN" ? false : explicitVr;
                        bool seqLe = element.Vr == "UN" || le;
                        if (depth >= MaxDepth)
                            throw new ImageDecodeException("DICOM sequences are nested too deeply.");
                        element.Offset = pos;
                        int seqEnd = length == UndefinedLength ? end : (int)Math.Min((long)pos + length, end);
                        element.Items = ParseSequence(b, ref pos, seqEnd, length == UndefinedLength, seqExplicit, seqLe, depth + 1);
                        if (length != UndefinedLength)
                            pos = seqEnd;
                    }
                    else
                    {
                        element.Offset = pos;
                        element.Length = (int)Math.Min(length, (uint)(end - pos));
                        pos += element.Length;
                    }
                    element.Length = Math.Max(element.Length, 0);
                    ds.Elements.TryAdd(tag, element);
                }
            }
            catch (Exception e)
            {
                // Malformed data; keep what we have.
                Util.log("Error while parsing DICOM data set: " + e.Message);
                pos = end;
            }
            return ds;
        }

        private static bool LooksLikeItem(byte[] b, int pos, int end, uint length, bool le)
        {
            return length >= 8 && pos + 8 <= end && (ReadTag(b, pos, le) == TagItem || ReadTag(b, pos, true) == TagItem);
        }

        private static List<DicomDataSet> ParseSequence(byte[] b, ref int pos, int end, bool undefinedLength, bool explicitVr, bool le, int depth)
        {
            var items = new List<DicomDataSet>();
            while (pos + 8 <= end)
            {
                uint tag = ReadTag(b, pos, le);
                uint length = ReadUInt32(b, pos + 4, le);
                if (tag == TagSequenceDelimiter)
                {
                    pos += 8;
                    break;
                }
                if (tag != TagItem)
                {
                    if (undefinedLength)
                        throw new ImageDecodeException("Invalid item in DICOM sequence.");
                    break;
                }
                pos += 8;
                if (length == UndefinedLength)
                {
                    items.Add(ParseDataSet(b, ref pos, end, explicitVr, le, true, depth));
                }
                else
                {
                    int itemEnd = (int)Math.Min((long)pos + length, end);
                    items.Add(ParseDataSet(b, ref pos, itemEnd, explicitVr, le, true, depth));
                    pos = itemEnd;
                }
            }
            return items;
        }

        private static List<(int, int)> ParseFragments(byte[] b, ref int pos, int end, bool le)
        {
            var fragments = new List<(int, int)>();
            while (pos + 8 <= end)
            {
                uint tag = ReadTag(b, pos, le);
                uint length = ReadUInt32(b, pos + 4, le);
                pos += 8;
                if (tag != TagItem)
                    break; // normally the sequence delimiter
                int len = (int)Math.Min(length, (uint)(end - pos));
                fragments.Add((pos, len));
                pos += len;
            }
            return fragments;
        }

        #region Value access

        public bool Contains(uint tag)
        {
            return Elements.ContainsKey(tag);
        }

        public DicomElement? Get(uint tag)
        {
            return Elements.TryGetValue(tag, out var e) ? e : null;
        }

        private string VrOf(DicomElement e)
        {
            if (e.Vr.Length > 0 && e.Vr != "UN")
                return e.Vr;
            return ImplicitVrs.TryGetValue(e.Tag, out var vr) ? vr : "";
        }

        public string? GetString(uint tag)
        {
            var e = Get(tag);
            if (e == null || e.Items != null)
                return null;
            return Encoding.Latin1.GetString(Buffer, e.Offset, e.Length).Trim(' ', '\0');
        }

        public List<DicomDataSet>? GetItems(uint tag)
        {
            return Get(tag)?.Items;
        }

        public byte[]? GetBytes(uint tag)
        {
            var e = Get(tag);
            if (e == null || e.Items != null || e.Fragments != null)
                return null;
            var bytes = new byte[e.Length];
            Array.Copy(Buffer, e.Offset, bytes, 0, e.Length);
            return bytes;
        }

        /// <summary>
        /// Gets all the numeric values of an element, whether it's stored as binary
        /// (US, SS, UL, SL, FL, FD) or as text (IS, DS).
        /// </summary>
        public double[] GetNumbers(uint tag, bool signed = false)
        {
            var e = Get(tag);
            if (e == null || e.Items != null || e.Length == 0)
                return Array.Empty<double>();
            string vr = VrOf(e);
            if (vr == "US" && signed)
                vr = "SS";
            switch (vr)
            {
                case "US":
                case "SS":
                case "OW":
                    {
                        var vals = new double[e.Length / 2];
                        for (int i = 0; i < vals.Length; i++)
                        {
                            ushort v = ReadUInt16(Buffer, e.Offset + i * 2, LittleEndian);
                            vals[i] = vr == "SS" ? (short)v : v;
                        }
                        return vals;
                    }
                case "UL":
                case "SL":
                case "OL":
                case "AT":
                    {
                        var vals = new double[e.Length / 4];
                        for (int i = 0; i < vals.Length; i++)
                        {
                            uint v = ReadUInt32(Buffer, e.Offset + i * 4, LittleEndian);
                            vals[i] = vr == "SL" ? (int)v : v;
                        }
                        return vals;
                    }
                case "FL":
                case "OF":
                    {
                        var vals = new double[e.Length / 4];
                        for (int i = 0; i < vals.Length; i++)
                            vals[i] = BitConverter.Int32BitsToSingle((int)ReadUInt32(Buffer, e.Offset + i * 4, LittleEndian));
                        return vals;
                    }
                case "FD":
                case "OD":
                    {
                        var vals = new double[e.Length / 8];
                        for (int i = 0; i < vals.Length; i++)
                        {
                            ulong hi = ReadUInt32(Buffer, e.Offset + i * 8 + (LittleEndian ? 4 : 0), LittleEndian);
                            ulong lo = ReadUInt32(Buffer, e.Offset + i * 8 + (LittleEndian ? 0 : 4), LittleEndian);
                            vals[i] = BitConverter.Int64BitsToDouble((long)((hi << 32) | lo));
                        }
                        return vals;
                    }
                case "IS":
                case "DS":
                    return ParseNumberList(Encoding.ASCII.GetString(Buffer, e.Offset, e.Length));
                default:
                    // No idea what it is; guess from the contents.
                    if (e.Length == 2)
                        return new double[] { signed ? (short)ReadUInt16(Buffer, e.Offset, LittleEndian) : ReadUInt16(Buffer, e.Offset, LittleEndian) };
                    if (e.Length == 4 && Buffer[e.Offset] < 0x20)
                        return new double[] { signed ? (int)ReadUInt32(Buffer, e.Offset, LittleEndian) : ReadUInt32(Buffer, e.Offset, LittleEndian) };
                    return ParseNumberList(Encoding.ASCII.GetString(Buffer, e.Offset, e.Length));
            }
        }

        private static double[] ParseNumberList(string str)
        {
            var parts = str.Split('\\');
            var vals = new List<double>();
            foreach (var p in parts)
            {
                if (double.TryParse(p.Trim(' ', '\0'), NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                    vals.Add(d);
                else
                    break;
            }
            return vals.ToArray();
        }

        public double GetNumber(uint tag, double defaultValue, bool signed = false)
        {
            var vals = GetNumbers(tag, signed);
            return vals.Length > 0 && !double.IsNaN(vals[0]) ? vals[0] : defaultValue;
        }

        public int GetInt(uint tag, int defaultValue)
        {
            double d = GetNumber(tag, defaultValue);
            return d >= int.MinValue && d <= int.MaxValue ? (int)d : defaultValue;
        }

        #endregion
    }
}
