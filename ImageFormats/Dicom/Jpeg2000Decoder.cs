using System;
using System.Collections.Generic;

/*

Decoder for JPEG 2000 (ISO/IEC 15444-1, ITU-T T.800) images, as used by the
DICOM transfer syntaxes 1.2.840.10008.1.2.4.90 and .91. Accepts either a raw
codestream (starting with an SOC marker) or a JP2 file, from which the codestream
box is extracted.

A JPEG 2000 image is split into tiles, and each tile component is transformed
with a discrete wavelet transform (the reversible 5/3 integer filter for lossless
images, or the irreversible 9/7 filter for lossy ones) into a set of subbands per
resolution level. Each subband is divided into code-blocks, which are coded
independently, one bit plane at a time from the most significant down, in three
passes per bit plane (significance propagation, magnitude refinement and
cleanup), using a context-adaptive binary arithmetic coder (the MQ coder). The
coded passes of all code-blocks are then gathered into packets, one per layer,
resolution, component and precinct (a spatial grouping of code-blocks), whose
headers say which code-blocks contribute how many passes and bytes. Packets are
ordered according to one of five progression orders, which may be changed
midway through a tile with POC markers.

Decoding runs the whole thing in reverse: parse the packets of each tile to
collect the coded data of each code-block, decode the bit planes of each
code-block, dequantize, run the inverse wavelet transform, undo the optional
multi-component (color) transform, and finally undo the DC level shift.

Copyright 2026+ Dmitry Brant
https://dmitrybrant.com

License: MIT
*/

namespace DmitryBrant.ImageFormats
{
    internal static class Jpeg2000Decoder
    {
        /// <summary>
        /// Decode a JPEG 2000 codestream, or a JP2 file containing one.
        /// </summary>
        public static CodecImage Decode(byte[] data)
        {
            int start = 0, end = data.Length;
            if (data.Length >= 12 && data[0] == 0 && data[1] == 0 && data[2] == 0 && data[3] == 12
                && data[4] == 'j' && data[5] == 'P' && data[6] == ' ' && data[7] == ' ')
            {
                FindCodestreamBox(data, out start, out end);
            }
            try
            {
                return new Codestream(data, start, end).Decode();
            }
            catch (IndexOutOfRangeException)
            {
                throw new ImageDecodeException("Malformed JPEG 2000 codestream.");
            }
            catch (ArgumentException)
            {
                throw new ImageDecodeException("Malformed JPEG 2000 codestream.");
            }
            catch (OverflowException)
            {
                throw new ImageDecodeException("Malformed JPEG 2000 codestream.");
            }
        }

        private static void FindCodestreamBox(byte[] data, out int start, out int end)
        {
            long pos = 0;
            while (pos + 8 <= data.Length)
            {
                long len = ReadU32(data, (int)pos);
                string type = System.Text.Encoding.ASCII.GetString(data, (int)pos + 4, 4);
                int headerLen = 8;
                if (len == 1)
                {
                    if (pos + 16 > data.Length)
                        break;
                    len = ((long)ReadU32(data, (int)pos + 8) << 32) | ReadU32(data, (int)pos + 12);
                    headerLen = 16;
                }
                else if (len == 0)
                {
                    len = data.Length - pos;
                }
                if (type == "jp2c")
                {
                    start = (int)pos + headerLen;
                    end = (int)Math.Min(pos + len, data.Length);
                    return;
                }
                if (len < headerLen)
                    break;
                pos += len;
            }
            throw new ImageDecodeException("No codestream found in JP2 file.");
        }

        private static uint ReadU32(byte[] d, int p)
        {
            return (uint)((d[p] << 24) | (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3]);
        }

        private static int CeilDivPow2(long a, int k)
        {
            return (int)-((-a) >> k);
        }

        private static int CeilDiv(int a, int b)
        {
            return (int)(((long)a + b - 1) / b);
        }

        private static int FloorLog2(int n)
        {
            int r = 0;
            while (n > 1) { n >>= 1; r++; }
            return r;
        }

        // Thrown internally when a tile's data runs out partway through, so that we can
        // still reconstruct whatever was decoded up to that point.
        private sealed class EndOfDataException : Exception
        {
        }

        private sealed class Component
        {
            public int Precision;
            public bool Signed;
            public int Dx, Dy;
        }

        // The parts of a COD marker that apply to the tile as a whole.
        private sealed class GlobalStyle
        {
            public int Scod, Progression, Layers, Mct;
        }

        // The parts of a COD or COC marker that apply to a single component.
        private sealed class CodingStyle
        {
            public int Levels, Xcb, Ycb, BlockStyle, Transform;
            public int[] PPx = Array.Empty<int>(), PPy = Array.Empty<int>();
        }

        private sealed class Quantization
        {
            public int Style, Guard;
            public int[] Exponents = Array.Empty<int>(), Mantissas = Array.Empty<int>();
        }

        private sealed class ProgressionChange
        {
            public int ResStart, CompStart, LayerEnd, ResEnd, CompEnd, Order;
        }

        // Everything we gather about a tile from its tile-part headers, plus its data.
        private sealed class TileInfo
        {
            public GlobalStyle? Cod;
            public CodingStyle? CodComp;
            public CodingStyle?[] Coc;
            public Quantization? Qcd;
            public Quantization?[] Qcc;
            public int[] Roi;
            public List<ProgressionChange>? Poc;
            public readonly List<(int Offset, int Length)> Parts = new();
            public List<byte[]>? Ppt;
            public List<byte>? PpmHeaders;

            public TileInfo(int numComps)
            {
                Coc = new CodingStyle?[numComps];
                Qcc = new Quantization?[numComps];
                Roi = new int[numComps];
                for (int i = 0; i < numComps; i++)
                    Roi[i] = -1;
            }
        }

        // Coded data for a run of coding passes that were terminated together.
        private sealed class Segment
        {
            public int Passes, MaxPasses;
            public byte[] Buf = { 0xFF, 0xFF };
            public int Length;

            public void Append(byte[] src, int offset, int count)
            {
                if (Length + count + 2 > Buf.Length)
                    Array.Resize(ref Buf, Math.Max(Buf.Length * 2, Length + count + 2));
                Buffer.BlockCopy(src, offset, Buf, Length, count);
                Length += count;
                // Pad the data with a marker, which the MQ decoder treats as the end.
                Buf[Length] = 0xFF;
                Buf[Length + 1] = 0xFF;
            }
        }

        private sealed class CodeBlock
        {
            public int X0, Y0, X1, Y1;
            public bool Included;
            public int Lblock = 3;
            public int ZeroBitPlanes;
            public readonly List<Segment> Segments = new();
        }

        // The code-blocks of one subband that fall within one precinct.
        private sealed class PrecinctBand
        {
            public int Cw, Ch;
            public CodeBlock[] Blocks = Array.Empty<CodeBlock>();
            public TagTree? Inclusion, ZeroBitPlanes;
        }

        private sealed class SubBand
        {
            public int Type; // 0 = LL, 1 = HL, 2 = LH, 3 = HH
            public int X0, Y0, X1, Y1;
            public int OffX, OffY;
            public int Mb;
            public double Delta;
            public PrecinctBand[] Precincts = Array.Empty<PrecinctBand>();
        }

        private sealed class Resolution
        {
            public int X0, Y0, X1, Y1;
            public int PPx, PPy, Pw, Ph;
            public SubBand[] Bands = Array.Empty<SubBand>();
            public bool[] Done = Array.Empty<bool>();
        }

        private sealed class TileComp
        {
            public int X0, Y0, X1, Y1;
            public int NumRes;
            public Resolution[] Res = Array.Empty<Resolution>();
            public CodingStyle Style = null!;
            public Quantization Quant = null!;
            public int Roi;
            public bool Reversible;
            public int[]? IData;
            public float[]? FData;
        }

        /// <summary>
        /// A tag tree, which codes a two-dimensional array of non-negative integers by
        /// coding the minimum of each 2x2 group at the level above, recursively.
        /// </summary>
        private sealed class TagTree
        {
            private readonly int[] value, low, offsets, widths;
            private readonly int levels;
            private readonly int[] path;

            public TagTree(int w, int h)
            {
                var offs = new List<int>();
                var ws = new List<int>();
                int total = 0;
                while (true)
                {
                    offs.Add(total);
                    ws.Add(w);
                    total += w * h;
                    if (w <= 1 && h <= 1)
                        break;
                    w = (w + 1) >> 1;
                    h = (h + 1) >> 1;
                }
                levels = offs.Count;
                offsets = offs.ToArray();
                widths = ws.ToArray();
                value = new int[total];
                low = new int[total];
                path = new int[levels];
                Array.Fill(value, int.MaxValue);
            }

            /// <summary>Returns whether the value at the given leaf is less than the threshold.</summary>
            public bool Decode(PacketBitReader br, int x, int y, int threshold)
            {
                for (int lv = 0; lv < levels; lv++)
                {
                    path[lv] = offsets[lv] + y * widths[lv] + x;
                    x >>= 1;
                    y >>= 1;
                }
                int lowv = 0;
                for (int lv = levels - 1; lv >= 0; lv--)
                {
                    int n = path[lv];
                    if (lowv > low[n])
                        low[n] = lowv;
                    else
                        lowv = low[n];
                    while (lowv < threshold && lowv < value[n])
                    {
                        if (br.Bit() != 0)
                            value[n] = lowv;
                        else
                            lowv++;
                    }
                    low[n] = lowv;
                }
                return value[path[0]] < threshold;
            }
        }

        /// <summary>
        /// Reads the bits of packet headers, which are bit-stuffed: after a 0xFF byte,
        /// the most significant bit of the next byte is a stuffed zero.
        /// </summary>
        private sealed class PacketBitReader
        {
            private readonly byte[] data;
            private readonly int end;
            private int buf, ct;
            public int Pos;

            public PacketBitReader(byte[] data, int end)
            {
                this.data = data;
                this.end = end;
            }

            public int Bit()
            {
                if (ct == 0)
                {
                    ct = buf == 0xFF ? 7 : 8;
                    buf = NextByte();
                }
                ct--;
                return (buf >> ct) & 1;
            }

            public int Bits(int n)
            {
                int v = 0;
                while (n-- > 0)
                    v = (v << 1) | Bit();
                return v;
            }

            public void Align()
            {
                if (buf == 0xFF)
                    NextByte();
                buf = 0;
                ct = 0;
            }

            public void Reset()
            {
                buf = 0;
                ct = 0;
            }

            public bool At(int b0, int b1)
            {
                return Pos + 1 < end && data[Pos] == b0 && data[Pos + 1] == b1;
            }

            private int NextByte()
            {
                if (Pos >= end)
                    throw new EndOfDataException();
                return data[Pos++];
            }
        }

        private sealed class Codestream
        {
            private readonly byte[] d;
            private readonly int start, end;

            private int xsiz, ysiz, xosiz, yosiz, xtsiz, ytsiz, xtosiz, ytosiz, numComps;
            private Component[] comps = Array.Empty<Component>();
            private int numXTiles, numYTiles;

            private GlobalStyle? mainCod;
            private CodingStyle? mainCodComp;
            private CodingStyle?[] mainCoc = Array.Empty<CodingStyle?>();
            private Quantization? mainQcd;
            private Quantization?[] mainQcc = Array.Empty<Quantization?>();
            private int[] mainRoi = Array.Empty<int>();
            private List<ProgressionChange>? mainPoc;
            private List<byte>? ppm;
            private TileInfo[] tiles = Array.Empty<TileInfo>();
            private readonly List<int> tilePartOrder = new();

            private int[][] planes = Array.Empty<int[]>();
            private int[] planeW = Array.Empty<int>(), planeH = Array.Empty<int>();
            private bool colorConverted;

            public Codestream(byte[] data, int start, int end)
            {
                d = data;
                this.start = start;
                this.end = end;
            }

            private int U8(int p)
            {
                if (p >= end)
                    throw new ImageDecodeException("Unexpected end of JPEG 2000 codestream.");
                return d[p];
            }

            private int U16(int p)
            {
                if (p + 1 >= end)
                    throw new ImageDecodeException("Unexpected end of JPEG 2000 codestream.");
                return (d[p] << 8) | d[p + 1];
            }

            private long U32(int p)
            {
                if (p + 3 >= end)
                    throw new ImageDecodeException("Unexpected end of JPEG 2000 codestream.");
                return ReadU32(d, p);
            }

            public CodecImage Decode()
            {
                ParseHeaders();

                for (int t = 0; t < tiles.Length; t++)
                {
                    if (tiles[t].Parts.Count > 0)
                        DecodeTile(t);
                }

                // Gather the component planes into interleaved samples at full resolution.
                int w = xsiz - xosiz, h = ysiz - yosiz;
                var samples = new int[(long)w * h * numComps];
                for (int c = 0; c < numComps; c++)
                {
                    var comp = comps[c];
                    var plane = planes[c];
                    int pw = planeW[c], ph = planeH[c];
                    if (pw == 0 || ph == 0)
                        continue;
                    int ox = CeilDiv(xosiz, comp.Dx), oy = CeilDiv(yosiz, comp.Dy);
                    if (comp.Dx == 1 && comp.Dy == 1)
                    {
                        for (int y = 0; y < h; y++)
                        {
                            int src = y * pw, dst = y * w * numComps + c;
                            for (int x = 0; x < w; x++, dst += numComps)
                                samples[dst] = plane[src + x];
                        }
                    }
                    else
                    {
                        // Subsampled component: replicate each sample over the area it covers.
                        for (int y = 0; y < h; y++)
                        {
                            int sy = Math.Clamp((y + yosiz) / comp.Dy - oy, 0, ph - 1);
                            int dst = y * w * numComps + c;
                            for (int x = 0; x < w; x++, dst += numComps)
                            {
                                int sx = Math.Clamp((x + xosiz) / comp.Dx - ox, 0, pw - 1);
                                samples[dst] = plane[sy * pw + sx];
                            }
                        }
                    }
                }

                return new CodecImage(w, h, numComps, comps[0].Precision, comps[0].Signed, samples)
                {
                    ColorConverted = colorConverted
                };
            }

            private void ParseHeaders()
            {
                int pos = start;
                if (U16(pos) != 0xFF4F)
                    throw new ImageDecodeException("Not a JPEG 2000 codestream.");
                pos += 2;

                while (pos + 1 < end)
                {
                    int marker = U16(pos);
                    if (marker == 0xFFD9)
                        break;
                    if ((marker & 0xFF00) != 0xFF00)
                    {
                        // Garbage where a marker should be; if we already have some tiles, just
                        // stop and decode what we have.
                        if (tilePartOrder.Count > 0)
                            break;
                        throw new ImageDecodeException("Invalid marker in JPEG 2000 codestream.");
                    }
                    if (marker >= 0xFF30 && marker <= 0xFF3F)
                    {
                        pos += 2;
                        continue;
                    }
                    if (marker == 0xFF90)
                    {
                        try
                        {
                            pos = ParseTilePart(pos);
                        }
                        catch (ImageDecodeException)
                        {
                            if (tilePartOrder.Count == 0)
                                throw;
                            break;
                        }
                        continue;
                    }
                    int len = U16(pos + 2);
                    ParseMarker(marker, pos + 4, len - 2, null);
                    pos += 2 + len;
                }

                if (comps.Length == 0)
                    throw new ImageDecodeException("JPEG 2000 codestream has no SIZ marker.");
                if (mainCod == null || mainCodComp == null || mainQcd == null)
                    throw new ImageDecodeException("JPEG 2000 codestream is missing a COD or QCD marker.");

                // Packed packet headers in the main header are split up among the tile-parts,
                // in the order in which the tile-parts appear.
                if (ppm != null)
                {
                    int p = 0;
                    foreach (int t in tilePartOrder)
                    {
                        if (p + 4 > ppm.Count)
                            break;
                        int n = (ppm[p] << 24) | (ppm[p + 1] << 16) | (ppm[p + 2] << 8) | ppm[p + 3];
                        p += 4;
                        n = Math.Min(n, ppm.Count - p);
                        tiles[t].PpmHeaders ??= new List<byte>();
                        tiles[t].PpmHeaders!.AddRange(ppm.GetRange(p, n));
                        p += n;
                    }
                }
            }

            private int ParseTilePart(int pos)
            {
                if (tiles.Length == 0)
                    throw new ImageDecodeException("JPEG 2000 tile-part found before SIZ marker.");
                int tpStart = pos;
                int lsot = U16(pos + 2);
                int isot = U16(pos + 4);
                long psot = U32(pos + 6);
                if (isot >= tiles.Length)
                    throw new ImageDecodeException("Invalid tile index in JPEG 2000 codestream.");
                var tile = tiles[isot];
                pos += 2 + lsot;

                while (true)
                {
                    int marker = U16(pos);
                    if (marker == 0xFF93)
                    {
                        pos += 2;
                        break;
                    }
                    if ((marker & 0xFF00) != 0xFF00)
                        throw new ImageDecodeException("Invalid marker in JPEG 2000 tile-part header.");
                    int len = U16(pos + 2);
                    ParseMarker(marker, pos + 4, len - 2, tile);
                    pos += 2 + len;
                }

                long tpEnd;
                if (psot == 0)
                {
                    // The last tile-part, which runs to the end of the codestream.
                    tpEnd = end;
                    if (end - 2 >= pos && d[end - 2] == 0xFF && d[end - 1] == 0xD9)
                        tpEnd = end - 2;
                }
                else
                {
                    tpEnd = Math.Min((long)tpStart + psot, end);
                    if (tpEnd + 1 < end && !(d[tpEnd] == 0xFF && (d[tpEnd + 1] == 0x90 || d[tpEnd + 1] == 0xD9)))
                    {
                        // The tile-part length is wrong (some encoders truncate it to 16 bits),
                        // so find where the tile-part really ends: at the next SOT marker, or
                        // at the end of the codestream. Coded data never contains a 0xFF byte
                        // followed by a byte above 0x8F, so a SOT marker can't be a false match.
                        tpEnd = end;
                        if (end - 2 >= pos && d[end - 2] == 0xFF && d[end - 1] == 0xD9)
                            tpEnd = end - 2;
                        for (int i = pos; i + 3 < end; i++)
                        {
                            if (d[i] == 0xFF && d[i + 1] == 0x90 && d[i + 2] == 0 && d[i + 3] == 10)
                            {
                                tpEnd = i;
                                break;
                            }
                        }
                    }
                }
                if (tpEnd > pos)
                    tile.Parts.Add((pos, (int)(tpEnd - pos)));
                tilePartOrder.Add(isot);
                return (int)Math.Max(tpEnd, pos);
            }

            private int ReadCompIndex(ref int p)
            {
                int c;
                if (numComps < 257)
                {
                    c = U8(p);
                    p++;
                }
                else
                {
                    c = U16(p);
                    p += 2;
                }
                if (c >= numComps)
                    throw new ImageDecodeException("Invalid component index in JPEG 2000 codestream.");
                return c;
            }

            private void ParseMarker(int marker, int p, int len, TileInfo? tile)
            {
                int segEnd = p + len;
                if (len < 0 || segEnd > end)
                    throw new ImageDecodeException("Invalid marker segment length in JPEG 2000 codestream.");

                switch (marker)
                {
                    case 0xFF51: // SIZ
                        {
                            xsiz = (int)U32(p + 2);
                            ysiz = (int)U32(p + 6);
                            xosiz = (int)U32(p + 10);
                            yosiz = (int)U32(p + 14);
                            xtsiz = (int)U32(p + 18);
                            ytsiz = (int)U32(p + 22);
                            xtosiz = (int)U32(p + 26);
                            ytosiz = (int)U32(p + 30);
                            numComps = U16(p + 34);
                            if (xsiz <= xosiz || ysiz <= yosiz || xtsiz <= 0 || ytsiz <= 0 || numComps == 0
                                || xtosiz > xosiz || ytosiz > yosiz || xtosiz + xtsiz <= xosiz || ytosiz + ytsiz <= yosiz)
                                throw new ImageDecodeException("Invalid JPEG 2000 image dimensions.");
                            if ((long)(xsiz - xosiz) * (ysiz - yosiz) * numComps > DicomReader.MaxSamples)
                                throw new ImageDecodeException("JPEG 2000 image is too large.");
                            comps = new Component[numComps];
                            for (int c = 0; c < numComps; c++)
                            {
                                int ssiz = U8(p + 36 + c * 3);
                                comps[c] = new Component
                                {
                                    Precision = (ssiz & 0x7F) + 1,
                                    Signed = (ssiz & 0x80) != 0,
                                    Dx = U8(p + 37 + c * 3),
                                    Dy = U8(p + 38 + c * 3)
                                };
                                if (comps[c].Dx == 0 || comps[c].Dy == 0 || comps[c].Precision > 31)
                                    throw new ImageDecodeException("Invalid JPEG 2000 component parameters.");
                            }
                            numXTiles = CeilDiv(xsiz - xtosiz, xtsiz);
                            numYTiles = CeilDiv(ysiz - ytosiz, ytsiz);
                            if ((long)numXTiles * numYTiles > 65535)
                                throw new ImageDecodeException("Too many tiles in JPEG 2000 image.");
                            tiles = new TileInfo[numXTiles * numYTiles];
                            for (int t = 0; t < tiles.Length; t++)
                                tiles[t] = new TileInfo(numComps);
                            mainCoc = new CodingStyle?[numComps];
                            mainQcc = new Quantization?[numComps];
                            mainRoi = new int[numComps];

                            planes = new int[numComps][];
                            planeW = new int[numComps];
                            planeH = new int[numComps];
                            for (int c = 0; c < numComps; c++)
                            {
                                planeW[c] = CeilDiv(xsiz, comps[c].Dx) - CeilDiv(xosiz, comps[c].Dx);
                                planeH[c] = CeilDiv(ysiz, comps[c].Dy) - CeilDiv(yosiz, comps[c].Dy);
                                planes[c] = new int[(long)planeW[c] * planeH[c]];
                            }
                            break;
                        }
                    case 0xFF52: // COD
                        {
                            int scod = U8(p);
                            var g = new GlobalStyle { Scod = scod, Progression = U8(p + 1), Layers = U16(p + 2), Mct = U8(p + 4) };
                            var cs = ReadCodingStyle(p + 5, (scod & 1) != 0);
                            if (tile != null)
                            {
                                tile.Cod = g;
                                tile.CodComp = cs;
                            }
                            else
                            {
                                mainCod = g;
                                mainCodComp = cs;
                            }
                            break;
                        }
                    case 0xFF53: // COC
                        {
                            int q = p;
                            int c = ReadCompIndex(ref q);
                            int scoc = U8(q);
                            var cs = ReadCodingStyle(q + 1, (scoc & 1) != 0);
                            if (tile != null)
                                tile.Coc[c] = cs;
                            else
                                mainCoc[c] = cs;
                            break;
                        }
                    case 0xFF5C: // QCD
                        {
                            var qz = ReadQuantization(p, segEnd);
                            if (tile != null)
                                tile.Qcd = qz;
                            else
                                mainQcd = qz;
                            break;
                        }
                    case 0xFF5D: // QCC
                        {
                            int q = p;
                            int c = ReadCompIndex(ref q);
                            var qz = ReadQuantization(q, segEnd);
                            if (tile != null)
                                tile.Qcc[c] = qz;
                            else
                                mainQcc[c] = qz;
                            break;
                        }
                    case 0xFF5E: // RGN
                        {
                            int q = p;
                            int c = ReadCompIndex(ref q);
                            if (U8(q) == 0)
                            {
                                if (tile != null)
                                    tile.Roi[c] = U8(q + 1);
                                else
                                    mainRoi[c] = U8(q + 1);
                            }
                            break;
                        }
                    case 0xFF5F: // POC
                        {
                            var list = new List<ProgressionChange>();
                            int q = p;
                            int compBytes = numComps < 257 ? 1 : 2;
                            while (q + 5 + 2 * compBytes <= segEnd)
                            {
                                var pc = new ProgressionChange();
                                pc.ResStart = U8(q++);
                                pc.CompStart = compBytes == 1 ? U8(q) : U16(q);
                                q += compBytes;
                                pc.LayerEnd = U16(q);
                                q += 2;
                                pc.ResEnd = U8(q++);
                                pc.CompEnd = compBytes == 1 ? U8(q) : U16(q);
                                if (pc.CompEnd == 0)
                                    pc.CompEnd = compBytes == 1 ? 256 : 16384;
                                q += compBytes;
                                pc.Order = U8(q++);
                                list.Add(pc);
                            }
                            if (tile != null)
                                (tile.Poc ??= new List<ProgressionChange>()).AddRange(list);
                            else
                                (mainPoc ??= new List<ProgressionChange>()).AddRange(list);
                            break;
                        }
                    case 0xFF60: // PPM
                        {
                            ppm ??= new List<byte>();
                            for (int i = p + 1; i < segEnd; i++)
                                ppm.Add(d[i]);
                            break;
                        }
                    case 0xFF61: // PPT
                        {
                            if (tile != null)
                            {
                                var bytes = new byte[Math.Max(0, len - 1)];
                                Buffer.BlockCopy(d, p + 1, bytes, 0, bytes.Length);
                                (tile.Ppt ??= new List<byte[]>()).Add(bytes);
                            }
                            break;
                        }
                    default:
                        // TLM, PLM, PLT, CRG, COM, CAP and so on don't affect decoding.
                        break;
                }
            }

            private CodingStyle ReadCodingStyle(int p, bool customPrecincts)
            {
                var cs = new CodingStyle
                {
                    Levels = U8(p),
                    Xcb = (U8(p + 1) & 0xF) + 2,
                    Ycb = (U8(p + 2) & 0xF) + 2,
                    BlockStyle = U8(p + 3),
                    Transform = U8(p + 4)
                };
                if (cs.Levels > 32)
                    throw new ImageDecodeException("Invalid number of JPEG 2000 decomposition levels.");
                if (cs.Xcb + cs.Ycb > 12)
                    throw new ImageDecodeException("Invalid JPEG 2000 code-block size.");
                if ((cs.BlockStyle & 0x40) != 0)
                    throw new ImageDecodeException("High-throughput JPEG 2000 is not supported.");
                cs.PPx = new int[cs.Levels + 1];
                cs.PPy = new int[cs.Levels + 1];
                for (int r = 0; r <= cs.Levels; r++)
                {
                    if (customPrecincts)
                    {
                        int b = U8(p + 5 + r);
                        cs.PPx[r] = b & 0xF;
                        cs.PPy[r] = b >> 4;
                    }
                    else
                    {
                        cs.PPx[r] = 15;
                        cs.PPy[r] = 15;
                    }
                }
                return cs;
            }

            private Quantization ReadQuantization(int p, int segEnd)
            {
                int sq = U8(p);
                var qz = new Quantization { Style = sq & 0x1F, Guard = sq >> 5 };
                var exps = new List<int>();
                var mants = new List<int>();
                if (qz.Style == 0)
                {
                    for (int q = p + 1; q < segEnd; q++)
                    {
                        exps.Add(d[q] >> 3);
                        mants.Add(0);
                    }
                }
                else
                {
                    for (int q = p + 1; q + 1 < segEnd; q += 2)
                    {
                        int v = (d[q] << 8) | d[q + 1];
                        exps.Add(v >> 11);
                        mants.Add(v & 0x7FF);
                    }
                }
                if (exps.Count == 0)
                    throw new ImageDecodeException("Invalid JPEG 2000 quantization marker.");
                qz.Exponents = exps.ToArray();
                qz.Mantissas = mants.ToArray();
                return qz;
            }

            private int tx0, ty0, tx1, ty1;
            private TileComp[] tcomps = Array.Empty<TileComp>();
            private int numLayers;
            private int blockStyleForPacket;

            private void DecodeTile(int t)
            {
                var info = tiles[t];
                int p = t % numXTiles, q = t / numXTiles;
                tx0 = Math.Max(xtosiz + p * xtsiz, xosiz);
                ty0 = Math.Max(ytosiz + q * ytsiz, yosiz);
                tx1 = (int)Math.Min((long)xtosiz + (long)(p + 1) * xtsiz, xsiz);
                ty1 = (int)Math.Min((long)ytosiz + (long)(q + 1) * ytsiz, ysiz);

                var g = info.Cod ?? mainCod!;
                numLayers = Math.Max(g.Layers, 1);

                tcomps = new TileComp[numComps];
                for (int c = 0; c < numComps; c++)
                {
                    var cs = info.Coc[c] ?? info.CodComp ?? mainCoc[c] ?? mainCodComp!;
                    var qz = info.Qcc[c] ?? info.Qcd ?? mainQcc[c] ?? mainQcd!;
                    int roi = info.Roi[c] >= 0 ? info.Roi[c] : mainRoi[c];
                    tcomps[c] = BuildTileComp(c, cs, qz, roi);
                }

                // Gather the tile's data, and its packet headers if they're stored separately.
                int total = 0;
                foreach (var part in info.Parts)
                    total += part.Length;
                var tileData = new byte[total];
                int o = 0;
                foreach (var part in info.Parts)
                {
                    Buffer.BlockCopy(d, part.Offset, tileData, o, part.Length);
                    o += part.Length;
                }
                byte[]? headers = null;
                if (info.PpmHeaders != null)
                {
                    headers = info.PpmHeaders.ToArray();
                }
                else if (info.Ppt != null)
                {
                    var list = new List<byte>();
                    foreach (var b in info.Ppt)
                        list.AddRange(b);
                    headers = list.ToArray();
                }

                var ctx = new PacketContext(tileData, headers);
                try
                {
                    var pocs = info.Poc ?? mainPoc;
                    if (pocs != null && pocs.Count > 0)
                    {
                        foreach (var pc in pocs)
                            RunProgression(ctx, pc.Order, pc.ResStart, pc.ResEnd, pc.CompStart, Math.Min(pc.CompEnd, numComps), Math.Min(pc.LayerEnd, numLayers));
                    }
                    // Any packets not covered by progression order changes (which some encoders
                    // produce) follow in the default order. Packets that were already decoded
                    // are skipped, so without POC markers this is simply the whole tile.
                    RunProgression(ctx, g.Progression, 0, 33, 0, numComps, numLayers);
                }
                catch (EndOfDataException)
                {
                    // Truncated tile; reconstruct whatever we got.
                }

                var t1 = new Tier1();
                foreach (var tc in tcomps)
                {
                    DecodeCodeBlocks(tc, t1);
                    InverseTransform(tc);
                }

                // Inverse multi-component transform.
                bool mct = g.Mct == 1 && numComps >= 3
                    && tcomps[0].X1 - tcomps[0].X0 == tcomps[1].X1 - tcomps[1].X0 && tcomps[0].X1 - tcomps[0].X0 == tcomps[2].X1 - tcomps[2].X0
                    && tcomps[0].Y1 - tcomps[0].Y0 == tcomps[1].Y1 - tcomps[1].Y0 && tcomps[0].Y1 - tcomps[0].Y0 == tcomps[2].Y1 - tcomps[2].Y0;
                if (mct)
                {
                    if (tcomps[0].IData != null && tcomps[1].IData != null && tcomps[2].IData != null)
                    {
                        var y0 = tcomps[0].IData!;
                        var y1 = tcomps[1].IData!;
                        var y2 = tcomps[2].IData!;
                        for (int i = 0; i < y0.Length; i++)
                        {
                            int gg = y0[i] - ((y2[i] + y1[i]) >> 2);
                            int r = y2[i] + gg;
                            int b = y1[i] + gg;
                            y0[i] = r;
                            y1[i] = gg;
                            y2[i] = b;
                        }
                        colorConverted = true;
                    }
                    else if (tcomps[0].FData != null && tcomps[1].FData != null && tcomps[2].FData != null)
                    {
                        var y0 = tcomps[0].FData!;
                        var y1 = tcomps[1].FData!;
                        var y2 = tcomps[2].FData!;
                        for (int i = 0; i < y0.Length; i++)
                        {
                            float yy = y0[i], cb = y1[i], cr = y2[i];
                            y0[i] = yy + 1.402f * cr;
                            y1[i] = yy - 0.34413f * cb - 0.71414f * cr;
                            y2[i] = yy + 1.772f * cb;
                        }
                        colorConverted = true;
                    }
                }

                // DC level shift, clamp, and store into the component planes.
                for (int c = 0; c < numComps; c++)
                {
                    var tc = tcomps[c];
                    int w = tc.X1 - tc.X0, h = tc.Y1 - tc.Y0;
                    if (w <= 0 || h <= 0)
                        continue;
                    var comp = comps[c];
                    int shift = comp.Signed ? 0 : 1 << (comp.Precision - 1);
                    int min = comp.Signed ? -(1 << (comp.Precision - 1)) : 0;
                    int max = comp.Signed ? (1 << (comp.Precision - 1)) - 1 : (int)((1L << comp.Precision) - 1);
                    int px0 = tc.X0 - CeilDiv(xosiz, comp.Dx), py0 = tc.Y0 - CeilDiv(yosiz, comp.Dy);
                    var plane = planes[c];
                    int pw = planeW[c];
                    for (int y = 0; y < h; y++)
                    {
                        int dst = (py0 + y) * pw + px0, src = y * w;
                        if (tc.IData != null)
                        {
                            var data = tc.IData;
                            for (int x = 0; x < w; x++)
                                plane[dst + x] = Math.Clamp(data[src + x] + shift, min, max);
                        }
                        else
                        {
                            var data = tc.FData!;
                            for (int x = 0; x < w; x++)
                            {
                                float v = data[src + x] + shift;
                                int iv = v >= max ? max : v <= min ? min : (int)MathF.Floor(v + 0.5f);
                                plane[dst + x] = Math.Clamp(iv, min, max);
                            }
                        }
                    }
                    tc.IData = null;
                    tc.FData = null;
                }
            }

            private TileComp BuildTileComp(int c, CodingStyle cs, Quantization qz, int roi)
            {
                var comp = comps[c];
                var tc = new TileComp
                {
                    X0 = CeilDiv(tx0, comp.Dx),
                    Y0 = CeilDiv(ty0, comp.Dy),
                    X1 = CeilDiv(tx1, comp.Dx),
                    Y1 = CeilDiv(ty1, comp.Dy),
                    Style = cs,
                    Quant = qz,
                    Roi = Math.Max(roi, 0),
                    NumRes = cs.Levels + 1,
                    Reversible = cs.Transform == 1
                };
                int nl = cs.Levels;
                int w = Math.Max(tc.X1 - tc.X0, 0), h = Math.Max(tc.Y1 - tc.Y0, 0);
                if (tc.Reversible)
                    tc.IData = new int[(long)w * h];
                else
                    tc.FData = new float[(long)w * h];

                tc.Res = new Resolution[tc.NumRes];
                for (int r = 0; r <= nl; r++)
                {
                    int lev = nl - r;
                    var res = new Resolution
                    {
                        X0 = CeilDivPow2(tc.X0, lev),
                        Y0 = CeilDivPow2(tc.Y0, lev),
                        X1 = CeilDivPow2(tc.X1, lev),
                        Y1 = CeilDivPow2(tc.Y1, lev),
                        PPx = cs.PPx[r],
                        PPy = cs.PPy[r]
                    };
                    if (res.X1 > res.X0 && res.Y1 > res.Y0)
                    {
                        res.Pw = CeilDivPow2(res.X1, res.PPx) - (res.X0 >> res.PPx);
                        res.Ph = CeilDivPow2(res.Y1, res.PPy) - (res.Y0 >> res.PPy);
                    }
                    res.Done = new bool[(long)res.Pw * res.Ph * numLayers];
                    tc.Res[r] = res;

                    int numBands = r == 0 ? 1 : 3;
                    res.Bands = new SubBand[numBands];
                    for (int bi = 0; bi < numBands; bi++)
                    {
                        int type = r == 0 ? 0 : bi + 1;
                        var band = new SubBand { Type = type };
                        if (r == 0)
                        {
                            band.X0 = res.X0;
                            band.Y0 = res.Y0;
                            band.X1 = res.X1;
                            band.Y1 = res.Y1;
                        }
                        else
                        {
                            int nb = nl - r + 1;
                            long xob = type & 1, yob = type >> 1;
                            band.X0 = CeilDivPow2(tc.X0 - (xob << (nb - 1)), nb);
                            band.Y0 = CeilDivPow2(tc.Y0 - (yob << (nb - 1)), nb);
                            band.X1 = CeilDivPow2(tc.X1 - (xob << (nb - 1)), nb);
                            band.Y1 = CeilDivPow2(tc.Y1 - (yob << (nb - 1)), nb);
                            var prev = tc.Res[r - 1];
                            if ((type & 1) != 0)
                                band.OffX = prev.X1 - prev.X0;
                            if ((type & 2) != 0)
                                band.OffY = prev.Y1 - prev.Y0;
                        }

                        // Quantization parameters for this band.
                        int bandIndex = r == 0 ? 0 : 3 * (r - 1) + type;
                        int eps, mu;
                        if (qz.Style == 1)
                        {
                            eps = qz.Exponents[0] - (r == 0 ? 0 : r - 1);
                            mu = qz.Mantissas[0];
                        }
                        else
                        {
                            int bix = Math.Min(bandIndex, qz.Exponents.Length - 1);
                            eps = qz.Exponents[bix];
                            mu = qz.Mantissas[bix];
                        }
                        int gain = type == 0 ? 0 : type == 3 ? 2 : 1;
                        band.Mb = qz.Guard + eps - 1;
                        band.Delta = qz.Style == 0 ? 1.0 : Math.Pow(2.0, comp.Precision + gain - eps) * (1.0 + mu / 2048.0);

                        BuildPrecincts(res, band, r, cs);
                        res.Bands[bi] = band;
                    }
                }
                return tc;
            }

            private static void BuildPrecincts(Resolution res, SubBand band, int r, CodingStyle cs)
            {
                int numPrec = res.Pw * res.Ph;
                band.Precincts = new PrecinctBand[numPrec];
                int bppx = Math.Max(r == 0 ? res.PPx : res.PPx - 1, 0);
                int bppy = Math.Max(r == 0 ? res.PPy : res.PPy - 1, 0);
                int xcb = Math.Min(cs.Xcb, bppx), ycb = Math.Min(cs.Ycb, bppy);
                int baseX = res.X0 >> res.PPx, baseY = res.Y0 >> res.PPy;
                for (int py = 0; py < res.Ph; py++)
                {
                    for (int px = 0; px < res.Pw; px++)
                    {
                        var pb = new PrecinctBand();
                        band.Precincts[py * res.Pw + px] = pb;
                        long prx0 = (long)(baseX + px) << bppx, pry0 = (long)(baseY + py) << bppy;
                        int x0 = (int)Math.Max(prx0, band.X0), x1 = (int)Math.Min(prx0 + (1L << bppx), band.X1);
                        int y0 = (int)Math.Max(pry0, band.Y0), y1 = (int)Math.Min(pry0 + (1L << bppy), band.Y1);
                        if (x0 >= x1 || y0 >= y1)
                            continue;
                        int cbx0 = x0 >> xcb, cby0 = y0 >> ycb;
                        pb.Cw = CeilDivPow2(x1, xcb) - cbx0;
                        pb.Ch = CeilDivPow2(y1, ycb) - cby0;
                        pb.Blocks = new CodeBlock[pb.Cw * pb.Ch];
                        for (int j = 0; j < pb.Ch; j++)
                        {
                            for (int i = 0; i < pb.Cw; i++)
                            {
                                pb.Blocks[j * pb.Cw + i] = new CodeBlock
                                {
                                    X0 = Math.Max((cbx0 + i) << xcb, x0),
                                    Y0 = Math.Max((cby0 + j) << ycb, y0),
                                    X1 = Math.Min((cbx0 + i + 1) << xcb, x1),
                                    Y1 = Math.Min((cby0 + j + 1) << ycb, y1)
                                };
                            }
                        }
                        pb.Inclusion = new TagTree(pb.Cw, pb.Ch);
                        pb.ZeroBitPlanes = new TagTree(pb.Cw, pb.Ch);
                    }
                }
            }

            // Packet progression

            private sealed class PacketContext
            {
                public readonly byte[] Body;
                public int BodyPos;
                public readonly PacketBitReader Header;
                public readonly bool SeparateHeaders;
                public readonly List<(CodeBlock Block, Segment Seg, int Length)> Pending = new();

                public PacketContext(byte[] body, byte[]? headers)
                {
                    Body = body;
                    SeparateHeaders = headers != null;
                    Header = headers != null ? new PacketBitReader(headers, headers.Length) : new PacketBitReader(body, body.Length);
                }
            }

            private void RunProgression(PacketContext ctx, int order, int rs, int re, int cs, int ce, int le)
            {
                int maxRes = 0;
                foreach (var tc in tcomps)
                    maxRes = Math.Max(maxRes, tc.NumRes);
                re = Math.Min(re, maxRes);

                switch (order)
                {
                    case 0: // LRCP
                        for (int l = 0; l < le; l++)
                            for (int r = rs; r < re; r++)
                                for (int c = cs; c < ce; c++)
                                    AllPrecincts(ctx, l, r, c);
                        break;
                    case 1: // RLCP
                        for (int r = rs; r < re; r++)
                            for (int l = 0; l < le; l++)
                                for (int c = cs; c < ce; c++)
                                    AllPrecincts(ctx, l, r, c);
                        break;
                    case 2: // RPCL
                        {
                            GetSteps(out long xstep, out long ystep);
                            for (int r = rs; r < re; r++)
                                for (long y = ty0; y < ty1; y += ystep - (y % ystep))
                                    for (long x = tx0; x < tx1; x += xstep - (x % xstep))
                                        for (int c = cs; c < ce; c++)
                                            PositionPackets(ctx, c, r, x, y, le);
                            break;
                        }
                    case 3: // PCRL
                        {
                            GetSteps(out long xstep, out long ystep);
                            for (long y = ty0; y < ty1; y += ystep - (y % ystep))
                                for (long x = tx0; x < tx1; x += xstep - (x % xstep))
                                    for (int c = cs; c < ce; c++)
                                        for (int r = rs; r < re; r++)
                                            PositionPackets(ctx, c, r, x, y, le);
                            break;
                        }
                    case 4: // CPRL
                        {
                            GetSteps(out long xstep, out long ystep);
                            for (int c = cs; c < ce; c++)
                                for (long y = ty0; y < ty1; y += ystep - (y % ystep))
                                    for (long x = tx0; x < tx1; x += xstep - (x % xstep))
                                        for (int r = rs; r < re; r++)
                                            PositionPackets(ctx, c, r, x, y, le);
                            break;
                        }
                    default:
                        throw new ImageDecodeException("Invalid JPEG 2000 progression order.");
                }
            }

            private void AllPrecincts(PacketContext ctx, int l, int r, int c)
            {
                var tc = tcomps[c];
                if (r >= tc.NumRes)
                    return;
                var res = tc.Res[r];
                int n = res.Pw * res.Ph;
                for (int p = 0; p < n; p++)
                    Packet(ctx, tc, res, l, r, p);
            }

            private void GetSteps(out long xstep, out long ystep)
            {
                xstep = long.MaxValue;
                ystep = long.MaxValue;
                for (int c = 0; c < numComps; c++)
                {
                    var tc = tcomps[c];
                    for (int r = 0; r < tc.NumRes; r++)
                    {
                        int lev = tc.NumRes - 1 - r;
                        xstep = Math.Min(xstep, (long)comps[c].Dx << Math.Min(tc.Res[r].PPx + lev, 40));
                        ystep = Math.Min(ystep, (long)comps[c].Dy << Math.Min(tc.Res[r].PPy + lev, 40));
                    }
                }
            }

            private void PositionPackets(PacketContext ctx, int c, int r, long x, long y, int le)
            {
                var tc = tcomps[c];
                if (r >= tc.NumRes)
                    return;
                var res = tc.Res[r];
                if (res.Pw == 0 || res.Ph == 0)
                    return;
                int lev = tc.NumRes - 1 - r;
                long dx = comps[c].Dx, dy = comps[c].Dy;
                int rpx = Math.Min(res.PPx + lev, 40), rpy = Math.Min(res.PPy + lev, 40);
                if (!((y % (dy << rpy) == 0) || (y == ty0 && (((long)res.Y0 << lev) % (1L << rpy)) != 0)))
                    return;
                if (!((x % (dx << rpx) == 0) || (x == tx0 && (((long)res.X0 << lev) % (1L << rpx)) != 0)))
                    return;
                long prci = (-((-x) / (dx << lev)) >> res.PPx) - (res.X0 >> res.PPx);
                long prcj = (-((-y) / (dy << lev)) >> res.PPy) - (res.Y0 >> res.PPy);
                if (prci < 0 || prcj < 0 || prci >= res.Pw || prcj >= res.Ph)
                    return;
                int p = (int)(prci + prcj * res.Pw);
                for (int l = 0; l < le; l++)
                    Packet(ctx, tc, res, l, r, p);
            }

            private void Packet(PacketContext ctx, TileComp tc, Resolution res, int l, int r, int p)
            {
                int doneIndex = p * numLayers + l;
                if (res.Done[doneIndex])
                    return;
                res.Done[doneIndex] = true;
                blockStyleForPacket = tc.Style.BlockStyle;

                var hr = ctx.Header;
                var body = ctx.Body;

                // An optional SOP marker precedes the packet in the body.
                if (ctx.BodyPos + 5 < body.Length && body[ctx.BodyPos] == 0xFF && body[ctx.BodyPos + 1] == 0x91)
                    ctx.BodyPos += 6;
                if (!ctx.SeparateHeaders)
                    hr.Pos = ctx.BodyPos;

                hr.Reset();
                ctx.Pending.Clear();
                if (hr.Bit() != 0)
                {
                    foreach (var band in res.Bands)
                    {
                        var pb = band.Precincts[p];
                        for (int i = 0; i < pb.Blocks.Length; i++)
                        {
                            var cb = pb.Blocks[i];
                            int cx = i % pb.Cw, cy = i / pb.Cw;
                            bool included = cb.Included ? hr.Bit() != 0 : pb.Inclusion!.Decode(hr, cx, cy, l + 1);
                            if (!included)
                                continue;
                            if (!cb.Included)
                            {
                                int th = 1;
                                while (!pb.ZeroBitPlanes!.Decode(hr, cx, cy, th))
                                {
                                    th++;
                                    if (th > 64)
                                        throw new ImageDecodeException("Invalid JPEG 2000 packet header.");
                                }
                                cb.ZeroBitPlanes = th - 1;
                                cb.Included = true;
                            }
                            int passes = DecodeNumPasses(hr);
                            while (hr.Bit() != 0)
                                cb.Lblock++;

                            while (passes > 0)
                            {
                                Segment seg;
                                if (cb.Segments.Count == 0 || cb.Segments[^1].Passes >= cb.Segments[^1].MaxPasses)
                                {
                                    seg = NewSegment(cb);
                                    cb.Segments.Add(seg);
                                }
                                else
                                {
                                    seg = cb.Segments[^1];
                                }
                                int n = Math.Min(passes, seg.MaxPasses - seg.Passes);
                                int len = hr.Bits(cb.Lblock + FloorLog2(n));
                                seg.Passes += n;
                                passes -= n;
                                ctx.Pending.Add((cb, seg, len));
                            }
                        }
                    }
                }
                hr.Align();

                // An optional EPH marker follows the packet header.
                if (hr.At(0xFF, 0x92))
                    hr.Pos += 2;
                if (!ctx.SeparateHeaders)
                    ctx.BodyPos = hr.Pos;

                foreach (var (_, seg, len) in ctx.Pending)
                {
                    int avail = Math.Min(len, body.Length - ctx.BodyPos);
                    if (avail > 0)
                        seg.Append(body, ctx.BodyPos, avail);
                    ctx.BodyPos += avail;
                }
                if (!ctx.SeparateHeaders)
                    hr.Pos = ctx.BodyPos;
            }

            private Segment NewSegment(CodeBlock cb)
            {
                int style = blockStyleForPacket;
                int max;
                if ((style & 4) != 0)
                {
                    max = 1;
                }
                else if ((style & 1) != 0)
                {
                    // In bypass mode, the first ten passes are arithmetic coded together, and
                    // after that, raw significance + refinement passes alternate with arithmetic
                    // coded cleanup passes.
                    if (cb.Segments.Count == 0)
                        max = 10;
                    else
                        max = cb.Segments[^1].MaxPasses == 1 || cb.Segments[^1].MaxPasses == 10 ? 2 : 1;
                }
                else
                {
                    max = 1000;
                }
                return new Segment { MaxPasses = max };
            }

            private static int DecodeNumPasses(PacketBitReader hr)
            {
                if (hr.Bit() == 0)
                    return 1;
                if (hr.Bit() == 0)
                    return 2;
                int v = hr.Bits(2);
                if (v != 3)
                    return 3 + v;
                v = hr.Bits(5);
                if (v != 31)
                    return 6 + v;
                return 37 + hr.Bits(7);
            }

            // Code-block decoding and dequantization

            private static void DecodeCodeBlocks(TileComp tc, Tier1 t1)
            {
                int w = tc.X1 - tc.X0;
                if (w <= 0 || tc.Y1 <= tc.Y0)
                    return;
                bool quantized = tc.Quant.Style != 0;
                foreach (var res in tc.Res)
                {
                    foreach (var band in res.Bands)
                    {
                        foreach (var pb in band.Precincts)
                        {
                            foreach (var cb in pb.Blocks)
                            {
                                if (cb.Segments.Count == 0)
                                    continue;
                                int mbEff = band.Mb + tc.Roi;
                                int planes = mbEff - cb.ZeroBitPlanes;
                                if (planes <= 0 || planes > 31)
                                    continue;
                                int bw = cb.X1 - cb.X0, bh = cb.Y1 - cb.Y0;
                                t1.Decode(cb, bw, bh, band.Type, tc.Style.BlockStyle, planes);

                                int ox = cb.X0 - band.X0 + band.OffX, oy = cb.Y0 - band.Y0 + band.OffY;
                                var mag = t1.Mag;
                                var nbits = t1.NBits;
                                var flags = t1.Flags;
                                int fs = bw + 2;
                                for (int y = 0; y < bh; y++)
                                {
                                    int dst = (oy + y) * w + ox;
                                    for (int x = 0; x < bw; x++)
                                    {
                                        int m = mag[y * bw + x];
                                        if (m == 0)
                                            continue;
                                        bool neg = (flags[(y + 1) * fs + x + 1] & Tier1.Neg) != 0;
                                        // Bit planes that weren't decoded (because the code-block
                                        // was truncated, or the transform is lossy) are
                                        // reconstructed at the middle of the remaining interval.
                                        int shift = planes - nbits[y * bw + x];
                                        if (tc.Reversible && !quantized && tc.Roi == 0)
                                        {
                                            int v = shift > 0 ? (m << shift) | (1 << (shift - 1)) : m >> -shift;
                                            tc.IData![dst + x] = neg ? -v : v;
                                        }
                                        else
                                        {
                                            double half = tc.Reversible && (shift <= 0 || tc.Roi > 0) ? 0.0 : 0.5;
                                            double v = (m + half) * Math.Pow(2.0, shift);
                                            if (tc.Roi > 0 && v >= Math.Pow(2.0, tc.Roi))
                                                v /= Math.Pow(2.0, tc.Roi);
                                            v *= band.Delta;
                                            if (neg)
                                                v = -v;
                                            if (tc.Reversible)
                                                tc.IData![dst + x] = (int)Math.Round(v);
                                            else
                                                tc.FData![dst + x] = (float)v;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // Inverse discrete wavelet transform

            private const int Pad = 4;

            private static void InverseTransform(TileComp tc)
            {
                int w = tc.X1 - tc.X0, h = tc.Y1 - tc.Y0;
                if (w <= 0 || h <= 0)
                    return;
                int maxDim = Math.Max(w, h);
                int[]? ibuf = tc.IData != null ? new int[maxDim + 2 * Pad] : null;
                float[]? fbuf = tc.FData != null ? new float[maxDim + 2 * Pad] : null;

                for (int r = 1; r < tc.NumRes; r++)
                {
                    var res = tc.Res[r];
                    var prev = tc.Res[r - 1];
                    int rw = res.X1 - res.X0, rh = res.Y1 - res.Y0;
                    int snx = prev.X1 - prev.X0, sny = prev.Y1 - prev.Y0;
                    if (rw <= 0 || rh <= 0)
                        continue;
                    int px = res.X0 & 1, py = res.Y0 & 1;

                    if (ibuf != null)
                    {
                        var data = tc.IData!;
                        for (int y = 0; y < rh; y++)
                        {
                            int row = y * w;
                            for (int j = 0; j < rw; j++)
                                ibuf[Pad + j] = ((px + j) & 1) == 0 ? data[row + (j >> 1)] : data[row + snx + (j >> 1)];
                            Lift53(ibuf, rw, px);
                            Buffer.BlockCopy(ibuf, Pad * 4, data, row * 4, rw * 4);
                        }
                        for (int x = 0; x < rw; x++)
                        {
                            for (int j = 0; j < rh; j++)
                                ibuf[Pad + j] = ((py + j) & 1) == 0 ? data[(j >> 1) * w + x] : data[(sny + (j >> 1)) * w + x];
                            Lift53(ibuf, rh, py);
                            for (int j = 0; j < rh; j++)
                                data[j * w + x] = ibuf[Pad + j];
                        }
                    }
                    else
                    {
                        var data = tc.FData!;
                        for (int y = 0; y < rh; y++)
                        {
                            int row = y * w;
                            for (int j = 0; j < rw; j++)
                                fbuf![Pad + j] = ((px + j) & 1) == 0 ? data[row + (j >> 1)] : data[row + snx + (j >> 1)];
                            Lift97(fbuf!, rw, px);
                            Buffer.BlockCopy(fbuf!, Pad * 4, data, row * 4, rw * 4);
                        }
                        for (int x = 0; x < rw; x++)
                        {
                            for (int j = 0; j < rh; j++)
                                fbuf![Pad + j] = ((py + j) & 1) == 0 ? data[(j >> 1) * w + x] : data[(sny + (j >> 1)) * w + x];
                            Lift97(fbuf!, rh, py);
                            for (int j = 0; j < rh; j++)
                                data[j * w + x] = fbuf![Pad + j];
                        }
                    }
                }
            }

            // Symmetric extension of a signal of length n, for index i outside [0, n).
            private static int Mirror(int i, int n)
            {
                int period = 2 * (n - 1);
                i %= period;
                if (i < 0)
                    i += period;
                return i < n ? i : period - i;
            }

            /// <summary>
            /// Inverse reversible 5/3 lifting, in place, on the interleaved signal at
            /// x[Pad..Pad+n). p0 is the parity of the first sample's absolute coordinate:
            /// samples at even coordinates are low-pass, odd ones are high-pass.
            /// </summary>
            private static void Lift53(int[] x, int n, int p0)
            {
                if (n == 1)
                {
                    if (p0 == 1)
                        x[Pad] /= 2;
                    return;
                }
                for (int i = 1; i <= Pad; i++)
                {
                    x[Pad - i] = x[Pad + Mirror(-i, n)];
                    x[Pad + n - 1 + i] = x[Pad + Mirror(n - 1 + i, n)];
                }
                for (int j = p0 == 0 ? 0 : -1; j <= n; j += 2)
                    x[Pad + j] -= (x[Pad + j - 1] + x[Pad + j + 1] + 2) >> 2;
                for (int j = p0 == 0 ? 1 : 0; j < n; j += 2)
                    x[Pad + j] += (x[Pad + j - 1] + x[Pad + j + 1]) >> 1;
            }

            private const float Alpha = -1.586134342059924f, Beta = -0.052980118572961f;
            private const float Gamma = 0.882911075530934f, DeltaCoef = 0.443506852043971f;
            private const float K = 1.230174104914001f, InvK = 1f / 1.230174104914001f;

            /// <summary>
            /// Inverse irreversible 9/7 lifting, in place, on the interleaved signal at
            /// x[Pad..Pad+n), with the same conventions as Lift53.
            /// </summary>
            private static void Lift97(float[] x, int n, int p0)
            {
                if (n == 1)
                {
                    if (p0 == 1)
                        x[Pad] *= 0.5f;
                    return;
                }
                for (int i = 1; i <= Pad; i++)
                {
                    x[Pad - i] = x[Pad + Mirror(-i, n)];
                    x[Pad + n - 1 + i] = x[Pad + Mirror(n - 1 + i, n)];
                }
                // First even (low-pass) and odd (high-pass) indices at or after -Pad.
                int e = ((p0 - Pad) & 1) == 0 ? -Pad : -Pad + 1;
                int o = e == -Pad ? -Pad + 1 : -Pad;
                for (int j = e; j < n + Pad; j += 2)
                    x[Pad + j] *= K;
                for (int j = o; j < n + Pad; j += 2)
                    x[Pad + j] *= InvK;
                for (int j = FirstAtLeast(e, -3); j < n + 3; j += 2)
                    x[Pad + j] -= DeltaCoef * (x[Pad + j - 1] + x[Pad + j + 1]);
                for (int j = FirstAtLeast(o, -2); j < n + 2; j += 2)
                    x[Pad + j] -= Gamma * (x[Pad + j - 1] + x[Pad + j + 1]);
                for (int j = FirstAtLeast(e, -1); j < n + 1; j += 2)
                    x[Pad + j] -= Beta * (x[Pad + j - 1] + x[Pad + j + 1]);
                for (int j = FirstAtLeast(o, 0); j < n; j += 2)
                    x[Pad + j] -= Alpha * (x[Pad + j - 1] + x[Pad + j + 1]);
            }

            private static int FirstAtLeast(int start, int min)
            {
                while (start < min)
                    start += 2;
                return start;
            }
        }

        /// <summary>
        /// Decodes the coding passes of a single code-block (EBCOT tier 1).
        /// </summary>
        private sealed class Tier1
        {
            // Per-sample flags. The low eight bits say which of the eight neighbors are
            // significant, which is all that's needed to pick a zero coding context.
            private const int SigN = 1, SigS = 2, SigW = 4, SigE = 8, SigNW = 16, SigNE = 32, SigSW = 64, SigSE = 128;
            private const int NegN = 256, NegS = 512, NegW = 1024, NegE = 2048;
            public const int Sig = 4096, Visit = 8192, Refined = 16384, Neg = 32768;
            // With vertically causal context formation, the stripe below is off limits.
            private const int CausalMask = ~(SigS | SigSW | SigSE | NegS);

            private const int CtxRun = 17, CtxUniform = 18;

            public int[] Mag = new int[64 * 64];
            public byte[] NBits = new byte[64 * 64];
            public int[] Flags = new int[66 * 66];
            private int w, h, fs;

            private static readonly byte[][] ZeroContexts = { BuildZeroContexts(0), BuildZeroContexts(1), BuildZeroContexts(2) };

            // MQ decoder state.
            private byte[] data = Array.Empty<byte>();
            private int bp, dataEnd, a, chigh, clow, ct;
            private readonly int[] cx = new int[19];
            private int rawC, rawCt;

            private static readonly int[] Qe =
            {
                0x5601, 0x3401, 0x1801, 0x0AC1, 0x0521, 0x0221, 0x5601, 0x5401, 0x4801, 0x3801, 0x3001, 0x2401,
                0x1C01, 0x1601, 0x5601, 0x5401, 0x5101, 0x4801, 0x3801, 0x3401, 0x3001, 0x2801, 0x2401, 0x2201,
                0x1C01, 0x1801, 0x1601, 0x1401, 0x1201, 0x1101, 0x0AC1, 0x09C1, 0x08A1, 0x0521, 0x0441, 0x02A1,
                0x0221, 0x0141, 0x0111, 0x0085, 0x0049, 0x0025, 0x0015, 0x0009, 0x0005, 0x0001, 0x5601
            };
            private static readonly byte[] Nmps =
            {
                1, 2, 3, 4, 5, 38, 7, 8, 9, 10, 11, 12, 13, 29, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24,
                25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 45, 46
            };
            private static readonly byte[] Nlps =
            {
                1, 6, 9, 12, 29, 33, 6, 14, 14, 14, 17, 18, 20, 21, 14, 14, 15, 16, 17, 18, 19, 19, 20, 21,
                22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 46
            };
            private static readonly byte[] Switch =
            {
                1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
            };

            // orient: 0 for LL and LH, 1 for HL (horizontal and vertical swapped), 2 for HH.
            private static byte[] BuildZeroContexts(int orient)
            {
                var t = new byte[256];
                for (int b = 0; b < 256; b++)
                {
                    int v = (b & 1) + ((b >> 1) & 1);
                    int hh = ((b >> 2) & 1) + ((b >> 3) & 1);
                    int dd = ((b >> 4) & 1) + ((b >> 5) & 1) + ((b >> 6) & 1) + ((b >> 7) & 1);
                    if (orient == 1)
                        (hh, v) = (v, hh);
                    int ctx;
                    if (orient != 2)
                    {
                        if (hh == 2)
                            ctx = 8;
                        else if (hh == 1)
                            ctx = v >= 1 ? 7 : dd >= 1 ? 6 : 5;
                        else
                            ctx = v == 2 ? 4 : v == 1 ? 3 : dd >= 2 ? 2 : dd == 1 ? 1 : 0;
                    }
                    else
                    {
                        int hv = hh + v;
                        if (dd >= 3)
                            ctx = 8;
                        else if (dd == 2)
                            ctx = hv >= 1 ? 7 : 6;
                        else if (dd == 1)
                            ctx = hv >= 2 ? 5 : hv == 1 ? 4 : 3;
                        else
                            ctx = hv >= 2 ? 2 : hv == 1 ? 1 : 0;
                    }
                    t[b] = (byte)ctx;
                }
                return t;
            }

            private void ResetContexts()
            {
                Array.Clear(cx);
                cx[0] = 4 << 1;
                cx[CtxRun] = 3 << 1;
                cx[CtxUniform] = 46 << 1;
            }

            public void Decode(CodeBlock cb, int bw, int bh, int bandType, int style, int planes)
            {
                w = bw;
                h = bh;
                fs = w + 2;
                int n = w * h, fn = fs * (h + 2);
                if (Mag.Length < n)
                {
                    Mag = new int[n];
                    NBits = new byte[n];
                }
                if (Flags.Length < fn)
                    Flags = new int[fn];
                Array.Clear(Mag, 0, n);
                Array.Clear(NBits, 0, n);
                Array.Clear(Flags, 0, fn);
                ResetContexts();

                var zc = ZeroContexts[bandType == 1 ? 1 : bandType == 3 ? 2 : 0];
                bool bypass = (style & 1) != 0, reset = (style & 2) != 0;
                bool causal = (style & 8) != 0, segSymbols = (style & 0x20) != 0;
                int maxPasses = 3 * planes - 2;
                int k = 0;
                foreach (var seg in cb.Segments)
                {
                    if (k >= maxPasses)
                        break;
                    bool raw = bypass && k >= 10 && (k - 10) % 3 != 2;
                    if (raw)
                        RawInit(seg.Buf, seg.Length);
                    else
                        MqInit(seg.Buf, seg.Length);
                    for (int i = 0; i < seg.Passes && k < maxPasses; i++, k++)
                    {
                        int type = k == 0 ? 2 : (k - 1) % 3;
                        if (type == 0)
                            SignificancePass(zc, raw, causal);
                        else if (type == 1)
                            RefinementPass(raw, causal);
                        else
                        {
                            CleanupPass(zc, causal);
                            if (segSymbols)
                            {
                                for (int s = 0; s < 4; s++)
                                    MqDecode(CtxUniform);
                            }
                        }
                        if (reset)
                            ResetContexts();
                    }
                }
            }

            private void SetSignificant(int idx, bool neg)
            {
                var f = Flags;
                f[idx] |= Sig | (neg ? Neg : 0);
                f[idx - fs] |= SigS | (neg ? NegS : 0);
                f[idx + fs] |= SigN | (neg ? NegN : 0);
                f[idx - 1] |= SigE | (neg ? NegE : 0);
                f[idx + 1] |= SigW | (neg ? NegW : 0);
                f[idx - fs - 1] |= SigSE;
                f[idx - fs + 1] |= SigSW;
                f[idx + fs - 1] |= SigNE;
                f[idx + fs + 1] |= SigNW;
            }

            private int DecodeSign(int f)
            {
                int hc = 0, vc = 0;
                if ((f & SigW) != 0) hc += (f & NegW) != 0 ? -1 : 1;
                if ((f & SigE) != 0) hc += (f & NegE) != 0 ? -1 : 1;
                if ((f & SigN) != 0) vc += (f & NegN) != 0 ? -1 : 1;
                if ((f & SigS) != 0) vc += (f & NegS) != 0 ? -1 : 1;
                hc = Math.Clamp(hc, -1, 1);
                vc = Math.Clamp(vc, -1, 1);
                int ctx, xorBit;
                if (hc == 0)
                {
                    ctx = vc == 0 ? 9 : 10;
                    xorBit = vc < 0 ? 1 : 0;
                }
                else
                {
                    ctx = 12 + hc * vc;
                    xorBit = hc < 0 ? 1 : 0;
                }
                return MqDecode(ctx) ^ xorBit;
            }

            private void SignificancePass(byte[] zc, bool raw, bool causal)
            {
                for (int y0 = 0; y0 < h; y0 += 4)
                {
                    int yEnd = Math.Min(y0 + 4, h);
                    for (int x = 0; x < w; x++)
                    {
                        for (int y = y0; y < yEnd; y++)
                        {
                            int idx = (y + 1) * fs + x + 1;
                            int f = Flags[idx];
                            if ((f & Sig) != 0)
                                continue;
                            if (causal && (y & 3) == 3)
                                f &= CausalMask;
                            if ((f & 0xFF) == 0)
                                continue;
                            int i = y * w + x;
                            int bit = raw ? RawBit() : MqDecode(zc[f & 0xFF]);
                            if (bit != 0)
                            {
                                int neg = raw ? RawBit() : DecodeSign(f);
                                SetSignificant(idx, neg != 0);
                                Mag[i] = 1;
                            }
                            Flags[idx] |= Visit;
                            NBits[i]++;
                        }
                    }
                }
            }

            private void RefinementPass(bool raw, bool causal)
            {
                for (int y0 = 0; y0 < h; y0 += 4)
                {
                    int yEnd = Math.Min(y0 + 4, h);
                    for (int x = 0; x < w; x++)
                    {
                        for (int y = y0; y < yEnd; y++)
                        {
                            int idx = (y + 1) * fs + x + 1;
                            int f = Flags[idx];
                            if ((f & (Sig | Visit)) != Sig)
                                continue;
                            int bit;
                            if (raw)
                            {
                                bit = RawBit();
                            }
                            else
                            {
                                if (causal && (y & 3) == 3)
                                    f &= CausalMask;
                                bit = MqDecode((f & Refined) != 0 ? 16 : (f & 0xFF) != 0 ? 15 : 14);
                            }
                            int i = y * w + x;
                            Mag[i] = (Mag[i] << 1) | bit;
                            NBits[i]++;
                            Flags[idx] |= Refined;
                        }
                    }
                }
            }

            private void CleanupPass(byte[] zc, bool causal)
            {
                for (int y0 = 0; y0 < h; y0 += 4)
                {
                    int yEnd = Math.Min(y0 + 4, h);
                    for (int x = 0; x < w; x++)
                    {
                        int y = y0;
                        if (y0 + 3 < h)
                        {
                            // Run mode: if all four samples in this column of the stripe are
                            // insignificant with insignificant neighbors, a single symbol says
                            // whether any of them becomes significant.
                            int idx0 = (y0 + 1) * fs + x + 1;
                            int f3 = Flags[idx0 + 3 * fs];
                            if (causal)
                                f3 &= CausalMask;
                            if (((Flags[idx0] | Flags[idx0 + fs] | Flags[idx0 + 2 * fs] | f3) & (Sig | Visit | 0xFF)) == 0)
                            {
                                if (MqDecode(CtxRun) == 0)
                                {
                                    for (int k = 0; k < 4; k++)
                                        NBits[(y0 + k) * w + x]++;
                                    continue;
                                }
                                int run = MqDecode(CtxUniform) << 1;
                                run |= MqDecode(CtxUniform);
                                for (int k = 0; k < run; k++)
                                    NBits[(y0 + k) * w + x]++;
                                y = y0 + run;
                                int idx = (y + 1) * fs + x + 1;
                                int f = Flags[idx];
                                if (causal && (y & 3) == 3)
                                    f &= CausalMask;
                                SetSignificant(idx, DecodeSign(f) != 0);
                                Mag[y * w + x] = 1;
                                NBits[y * w + x]++;
                                y++;
                            }
                        }
                        for (; y < yEnd; y++)
                        {
                            int idx = (y + 1) * fs + x + 1;
                            int f = Flags[idx];
                            if ((f & (Sig | Visit)) == 0)
                            {
                                if (causal && (y & 3) == 3)
                                    f &= CausalMask;
                                int i = y * w + x;
                                if (MqDecode(zc[f & 0xFF]) != 0)
                                {
                                    SetSignificant(idx, DecodeSign(f) != 0);
                                    Mag[i] = 1;
                                }
                                NBits[i]++;
                            }
                            Flags[idx] &= ~Visit;
                        }
                    }
                }
            }

            // MQ arithmetic decoder (T.800 Annex C). The data is padded with 0xFFFF, which
            // the decoder sees as a marker, and past which it feeds in 1 bits.

            private void MqInit(byte[] buf, int len)
            {
                data = buf;
                dataEnd = len;
                bp = 0;
                chigh = data[0];
                clow = 0;
                ByteIn();
                chigh = ((chigh << 7) & 0xFFFF) | ((clow >> 9) & 0x7F);
                clow = (clow << 7) & 0xFFFF;
                ct -= 7;
                a = 0x8000;
            }

            private void ByteIn()
            {
                if (data[bp] == 0xFF)
                {
                    if (data[bp + 1] > 0x8F)
                    {
                        clow += 0xFF00;
                        ct = 8;
                    }
                    else
                    {
                        bp++;
                        clow += data[bp] << 9;
                        ct = 7;
                    }
                }
                else
                {
                    bp++;
                    clow += bp < dataEnd ? data[bp] << 8 : 0xFF00;
                    ct = 8;
                }
                if (clow > 0xFFFF)
                {
                    chigh += clow >> 16;
                    clow &= 0xFFFF;
                }
            }

            private int MqDecode(int ctxIndex)
            {
                int state = cx[ctxIndex];
                int idx = state >> 1, mps = state & 1;
                int qe = Qe[idx];
                int d;
                int av = a - qe;
                if (chigh < qe)
                {
                    if (av < qe)
                    {
                        av = qe;
                        d = mps;
                        idx = Nmps[idx];
                    }
                    else
                    {
                        av = qe;
                        d = 1 ^ mps;
                        if (Switch[idx] == 1)
                            mps = d;
                        idx = Nlps[idx];
                    }
                }
                else
                {
                    chigh -= qe;
                    if ((av & 0x8000) != 0)
                    {
                        a = av;
                        return mps;
                    }
                    if (av < qe)
                    {
                        d = 1 ^ mps;
                        if (Switch[idx] == 1)
                            mps = d;
                        idx = Nlps[idx];
                    }
                    else
                    {
                        d = mps;
                        idx = Nmps[idx];
                    }
                }
                do
                {
                    if (ct == 0)
                        ByteIn();
                    av <<= 1;
                    chigh = ((chigh << 1) & 0xFFFF) | ((clow >> 15) & 1);
                    clow = (clow << 1) & 0xFFFF;
                    ct--;
                } while ((av & 0x8000) == 0);
                a = av;
                cx[ctxIndex] = (idx << 1) | mps;
                return d;
            }

            // Raw (bypass) decoding: plain bits, with a stuffed zero bit after each 0xFF.

            private void RawInit(byte[] buf, int len)
            {
                data = buf;
                dataEnd = len;
                bp = 0;
                rawC = 0;
                rawCt = 0;
            }

            private int RawBit()
            {
                if (rawCt == 0)
                {
                    if (rawC == 0xFF)
                    {
                        if (data[bp] > 0x8F)
                        {
                            rawC = 0xFF;
                            rawCt = 8;
                        }
                        else
                        {
                            rawC = data[bp++];
                            rawCt = 7;
                        }
                    }
                    else
                    {
                        rawC = data[bp];
                        if (bp <= dataEnd)
                            bp++;
                        rawCt = 8;
                    }
                }
                rawCt--;
                return (rawC >> rawCt) & 1;
            }
        }
    }
}
