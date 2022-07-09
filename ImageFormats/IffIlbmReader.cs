using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Collections.Generic;

/*

Decoder for ILBM (Interleaved Bitmap) images.

Copyright 2020 Dmitry Brant
https://dmitrybrant.com

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

   http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.

*/

namespace DmitryBrant.ImageFormats
{
    /// <summary>
    /// Handles reading ILBM images.
    /// </summary>
    public static class IffIlbmReader
    {

        /// <summary>
        /// Reads an ILBM image from a file.
        /// </summary>
        /// <param name="fileName">Name of the file to read.</param>
        /// <returns>Bitmap that contains the image that was read.</returns>
        public static Bitmap Load(string fileName)
        {
            using (var f = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                return Load(f);
            }
        }

        /// <summary>
        /// Reads an ILBM image from a stream.
        /// </summary>
        /// <param name="stream">Stream from which to read the image.</param>
        /// <returns>Bitmap that contains the image that was read.</returns>
        public static Bitmap Load(Stream stream)
        {
            int imgWidth = -1;
            int imgHeight = -1;

            int numPlanes = -1;
            int totalColors = 0;
            int numColorBits = 0;
            int maskType = 0;
            int compressionType = 0;
            int transparentColor = 0;
            bool haveCAMG = false;
            bool haveCTBL = false;
            bool haveSHAM = false;
            bool modeShamLaced = false;
            bool modePbm = false;
            bool modeHalfBrite = false;
            int halfBriteBit = 0;
            bool modeHAM = false;
            int modeXBMI = -1;

            BinaryReader reader = new BinaryReader(stream);

            byte[] tempBytes = new byte[65536];

            stream.Read(tempBytes, 0, 4);
            if (Encoding.ASCII.GetString(tempBytes, 0, 4) != "FORM") { throw new ApplicationException("This is not a valid ILBM file."); }

            uint chunkSize = Util.BigEndian(reader.ReadUInt32());

            stream.Read(tempBytes, 0, 4);
            string fileType = Encoding.ASCII.GetString(tempBytes, 0, 4);
            if (fileType != "ILBM" && fileType != "PBM ") { throw new ApplicationException("This is not a valid ILBM file."); }
            if (fileType == "PBM ")
            {
                modePbm = true;
            }

            byte[] palette = null;
            var rowPalette = new List<byte[]>();

            while (stream.Position < stream.Length)
            {
                stream.Read(tempBytes, 0, 4);
                string chunkName = Encoding.ASCII.GetString(tempBytes, 0, 4);
                chunkSize = Util.BigEndian(reader.ReadUInt32());
                if (chunkSize % 2 > 0) { chunkSize++; }

                if (chunkName == "BODY")
                {
                    break;
                }

                if (chunkSize <= tempBytes.Length)
                {
                    stream.Read(tempBytes, 0, (int)chunkSize);
                }
                else
                {
                    stream.Seek(chunkSize, SeekOrigin.Current);
                }

                if (chunkName == "BMHD")
                {
                    imgWidth = Util.BigEndian(BitConverter.ToUInt16(tempBytes, 0));
                    imgHeight = Util.BigEndian(BitConverter.ToUInt16(tempBytes, 2));

                    numPlanes = tempBytes[8];
                    maskType = tempBytes[9];
                    compressionType = tempBytes[10];
                    transparentColor = Util.BigEndian(BitConverter.ToUInt16(tempBytes, 12));

                    // initialize palette and randomize it.
                    // TODO: initialize palette colors to known Amiga values?
                    if (numPlanes < 12)
                    {
                        int numColors = 1 << numPlanes;
                        palette = new byte[numColors * 3];
                        (new Random()).NextBytes(palette);
                    }
                }
                else if (chunkName == "CMAP")
                {
                    totalColors = 1 << numPlanes;
                    int numColorsInChunk = (int)chunkSize / 3;
                    if (numColorsInChunk < totalColors)
                    {
                        totalColors = numColorsInChunk;
                    }
                    numColorBits = 0;
                    while (totalColors > (1 << numColorBits)) numColorBits++;
                    palette = new byte[chunkSize];
                    for (int c = 0; c < chunkSize; c++)
                    {
                        palette[c] = tempBytes[c];
                    }

                    // Check if we need to upscale the color values
                    var scaleMask = (1 << numColorBits) - 1;
                    bool scaled = false;
                    for (var i = 0; i < palette.Length; i++)
                    {
                        var value = palette[i];
                        if ((value & scaleMask) != 0)
                        {
                            scaled = true;
                            break;
                        }
                    }
                    if (!scaled)
                    {
                        for (var i = 0; i < palette.Length; i++)
                        {
                            var val = palette[i] >> (8 - numColorBits);
                            palette[i] = (byte)extendTo8Bits(val, numColorBits);
                        }
                    }
                }
                else if (chunkName == "CAMG")
                {
                    uint mode = Util.BigEndian(BitConverter.ToUInt32(tempBytes, 0));
                    if ((mode & 0x80) != 0) { modeHalfBrite = true; }
                    if ((mode & 0x800) != 0) { modeHAM = true; }
                    haveCAMG = true;
                }
                else if (chunkName == "CTBL")
                {
                    haveCTBL = true;
                    int bytesPerRow = 32;
                    int rowsInChunk = (int)chunkSize / bytesPerRow;
                    int colorsPerRow = Math.Min(bytesPerRow / 2, palette.Length / 3);
                    for (int row = 0; row < rowsInChunk; row++)
                    {
                        var rowPal = new byte[palette.Length];
                        rowPalette.Add(rowPal);
                        int ptr = row * bytesPerRow;
                        int r, g, b;
                        for (int c = 0; c < colorsPerRow; c++)
                        {
                            r = (tempBytes[ptr++] & 0xF);
                            g = tempBytes[ptr++];
                            b = g & 0xF;
                            g = (g >> 4) & 0xF;
                            rowPal[c * 3] = (byte)((r << 4) | r);
                            rowPal[c * 3 + 1] = (byte)((g << 4) | g);
                            rowPal[c * 3 + 2] = (byte)((b << 4) | b);
                        }
                    }
                }
                else if (chunkName == "SHAM")
                {
                    haveSHAM = true;
                    int bytesPerRow = 32;
                    int rowsInChunk = (int)(chunkSize - 2) / bytesPerRow;
                    if (rowsInChunk == imgHeight / 2)
                    {
                        modeShamLaced = true;
                    }
                    int colorsPerRow = Math.Min(bytesPerRow / 2, palette.Length / 3);
                    for (int row = 0; row < rowsInChunk; row++)
                    {
                        var rowPal = new byte[palette.Length];
                        rowPalette.Add(rowPal);
                        int ptr = 2 + (row * bytesPerRow);
                        int r, g, b;
                        for (int c = 0; c < colorsPerRow; c++)
                        {
                            r = (tempBytes[ptr++] & 0xF);
                            g = tempBytes[ptr++];
                            b = g & 0xF;
                            g = (g >> 4) & 0xF;
                            rowPal[c * 3] = (byte)((r << 4) | r);
                            rowPal[c * 3 + 1] = (byte)((g << 4) | g);
                            rowPal[c * 3 + 2] = (byte)((b << 4) | b);
                        }
                    }
                }
                else if (chunkName == "XBMI")
                {
                    modeXBMI = Util.BigEndian(BitConverter.ToUInt16(tempBytes, 0));
                    // followed by:
                    // WORD xdpi;
                    // WORD ydpi;
                }
            }

            if (imgWidth == -1 || imgHeight == -1 || (numPlanes > 12 && numPlanes != 24 && numPlanes != 32))
            {
                throw new ApplicationException("Invalid format of ILBM file.");
            }

            if (maskType == 1)
            {
                throw new ApplicationException("ILBM images with mask plane not yet implemented.");
            }

            if (!haveCAMG)
            {
                // Fall back to some defaults if we didn't get a CAMG chunk...
                if (numPlanes == 6 && numColorBits == 4)
                {
                    // If there's exactly two more bitplanes than color bits, then it's very likely to be a HAM image.
                    modeHAM = true;
                }
                else if (numColorBits > 0 && numColorBits < numPlanes)
                {
                    // More generally, if there's more bitplanes than color bits, then it's likely halfBrite.
                    modeHalfBrite = true;
                }
            }

            if (modeHalfBrite)
            {
                halfBriteBit = 1 << (numPlanes - 1);
                if (numColorBits == numPlanes)
                {
                    // Cull the color palette if we have halfBrite mode but too many colors in CMAP.
                    numColorBits--;
                    totalColors >>= 1;
                }
            }

            if (modeHAM && numColorBits > (numPlanes - 2))
            {
                // Cull the color palette if we have HAM mode but too many colors in CMAP.
                var delta = numColorBits - numPlanes + 2;
                numColorBits -= delta;
                totalColors >>= delta;
            }

            ByteRun1Decoder decompressor = new ByteRun1Decoder(stream);
            byte[] bmpData = new byte[(imgWidth + 1) * 4 * imgHeight];

            try
            {
                int bytesPerBitPlane = ((imgWidth + 15) / 16) * 2;
                int bytesPerLine = bytesPerBitPlane * numPlanes;

                // TODO: account for mask data?

                byte[] scanLine = new byte[bytesPerLine];
                uint[] imageLine = new uint[imgWidth];

                for (int y = 0; y < imgHeight; y++)
                {
                    Array.Clear(imageLine, 0, imageLine.Length);

                    if (compressionType == 0)
                    {
                        stream.Read(scanLine, 0, scanLine.Length);
                    }
                    else if (compressionType == 1)
                    {
                        decompressor.ReadNextBytes(scanLine, bytesPerLine);
                    }

                    if (modePbm)
                    {
                        if (numPlanes == 8)
                        {
                            for (int x = 0; x < imgWidth; x++)
                            {
                                imageLine[x] = scanLine[x];
                            }
                        }
                        else
                        {
                            throw new ApplicationException("Unsupported bit width: " + numPlanes);
                        }
                    }
                    else
                    {
                        for (int b = 0; b < numPlanes; b++)
                        {
                            var bp = new BitPlaneReader(scanLine, bytesPerBitPlane * b);
                            for (int x = 0; x < imgWidth; x++)
                            {
                                imageLine[x] |= (uint)bp.NextBit() << b;
                            }
                        }
                    }

                    int prevR = 0, prevG = 0, prevB = 0;
                    int index;

                    // apply mask plane?

                    if (numPlanes == 24)
                    {
                        for (int x = 0; x < imgWidth; x++)
                        {
                            bmpData[4 * (y * imgWidth + x)] = (byte)((imageLine[x] >> 16) & 0xFF);
                            bmpData[4 * (y * imgWidth + x) + 1] = (byte)((imageLine[x] >> 8) & 0xFF);
                            bmpData[4 * (y * imgWidth + x) + 2] = (byte)(imageLine[x] & 0xFF);
                            bmpData[4 * (y * imgWidth + x) + 3] = 0xFF;
                        }
                    }
                    else if (modeHAM)
                    {
                        int hamShift = numColorBits;
                        int valShift = 8 - hamShift;
                        int hamVal;
                        byte[] pal = palette;
                        if (haveSHAM)
                        {
                            if (modeShamLaced && rowPalette.Count > y / 2)
                            {
                                pal = rowPalette[y / 2];
                            }
                            else if (rowPalette.Count > y)
                            {
                                pal = rowPalette[y];
                            }
                        }

                        for (int x = 0; x < imgWidth; x++)
                        {
                            index = (int)imageLine[x];
                            hamVal = (index >> hamShift) & 0x3;
                            index %= totalColors;

                            if (maskType == 2 && index == transparentColor)
                            {
                                bmpData[4 * (y * imgWidth + x)] = 0;
                                bmpData[4 * (y * imgWidth + x) + 1] = 0;
                                bmpData[4 * (y * imgWidth + x) + 2] = 0;
                                bmpData[4 * (y * imgWidth + x) + 3] = 0;
                            }
                            else
                            {
                                if (hamVal == 0)
                                {
                                    prevR = pal[index * 3];
                                    prevG = pal[index * 3 + 1];
                                    prevB = pal[index * 3 + 2];
                                }
                                else if (hamVal == 2)
                                {
                                    prevR = extendTo8Bits(index, numColorBits); // (index << valShift) | index;
                                }
                                else if (hamVal == 1)
                                {
                                    prevB = extendTo8Bits(index, numColorBits); // (index << valShift) | index;
                                }
                                else if (hamVal == 3)
                                {
                                    prevG = extendTo8Bits(index, numColorBits); // (index << valShift) | index;
                                }
                                bmpData[4 * (y * imgWidth + x)] = (byte)prevB;
                                bmpData[4 * (y * imgWidth + x) + 1] = (byte)prevG;
                                bmpData[4 * (y * imgWidth + x) + 2] = (byte)prevR;
                                bmpData[4 * (y * imgWidth + x) + 3] = 0xFF;
                            }
                        }
                    }
                    else
                    {
                        byte[] pal = haveCTBL && rowPalette.Count > y ? rowPalette[y] : palette;
                        bool halfBrite = false;

                        for (int x = 0; x < imgWidth; x++)
                        {
                            index = (int)imageLine[x];


                            if (maskType == 2 && index == transparentColor)
                            {
                                bmpData[4 * (y * imgWidth + x)] = 0;
                                bmpData[4 * (y * imgWidth + x) + 1] = 0;
                                bmpData[4 * (y * imgWidth + x) + 2] = 0;
                                bmpData[4 * (y * imgWidth + x) + 3] = 0;
                            }
                            else
                            {
                                if (modeHalfBrite)
                                {
                                    halfBrite = (index & halfBriteBit) != 0;
                                    index %= totalColors;
                                }

                                if (modeXBMI <= 0)
                                {
                                    // No XBMI mode, or normal palette indexing.
                                    prevR = pal[index * 3];
                                    prevG = pal[index * 3 + 1];
                                    prevB = pal[index * 3 + 2];
                                }
                                else if (modeXBMI == 1)
                                {
                                    // Greyscale
                                    prevR = (0xFF >> numPlanes) * index;
                                    prevG = prevR;
                                    prevB = prevR;
                                }
                                else
                                {
                                    throw new ApplicationException("Unsupported XBMI mode: " + modeXBMI);
                                }

                                bmpData[4 * (y * imgWidth + x)] = (byte)prevB;
                                bmpData[4 * (y * imgWidth + x) + 1] = (byte)prevG;
                                bmpData[4 * (y * imgWidth + x) + 2] = (byte)prevR;
                                bmpData[4 * (y * imgWidth + x) + 3] = 0xFF;

                                if (halfBrite)
                                {
                                    bmpData[4 * (y * imgWidth + x)] >>= 1;
                                    bmpData[4 * (y * imgWidth + x) + 1] >>= 1;
                                    bmpData[4 * (y * imgWidth + x) + 2] >>= 1;
                                }
                            }
                        }
                    }
                }

            }
            catch (Exception e)
            {
                // return a partial image in case of unexpected end-of-file
                Util.log("Error while processing ILBM file: " + e.Message);
            }

            var bmp = new Bitmap(imgWidth, imgHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            System.Drawing.Imaging.BitmapData bmpBits = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), System.Drawing.Imaging.ImageLockMode.ReadWrite, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            System.Runtime.InteropServices.Marshal.Copy(bmpData, 0, bmpBits.Scan0, imgWidth * 4 * imgHeight);
            bmp.UnlockBits(bmpBits);
            return bmp;
        }

        private static int extendTo8Bits(int value, int bits)
        {
            var result = 0;
            for (var s = 8 - bits; s >= 0; s -= bits)
            {
                result |= value << s;
            }
            return result;
        }

        private class BitPlaneReader
        {
            private readonly byte[] bytes;
            private int currentByte;
            private int bytePtr;
            private int currentBit;

            public BitPlaneReader(byte[] bytes, int offset)
            {
                this.bytes = bytes;
                bytePtr = offset;
            }

            public int NextBit()
            {
                if (currentBit == 0)
                {
                    currentBit = 0x80;
                    currentByte = bytes[bytePtr++];
                }
                int ret = (currentByte & currentBit) != 0 ? 1 : 0;
                currentBit >>= 1;
                return ret;
            }
        }

        /// <summary>
        /// Helper class for reading a run-length encoded stream in an ILBM file.
        /// </summary>
        private class ByteRun1Decoder
        {
            private readonly Stream stream;

            public ByteRun1Decoder(Stream stream)
            {
                this.stream = stream;
            }

            public void ReadNextBytes(byte[] bytes, int bytesNeeded)
            {
                int bytesRead = 0;
                int runLength;
                sbyte op;
                int curByte;
                while (bytesRead < bytesNeeded)
                {
                    op = (sbyte)stream.ReadByte();
                    if (op == -128) { }
                    if (op < 0)
                    {
                        runLength = -(int)op + 1;
                        curByte = stream.ReadByte();
                        for (int i = 0; i < runLength; i++)
                        {
                            if (bytesRead >= bytesNeeded) { break; }
                            bytes[bytesRead++] = (byte)curByte;
                        }
                    }
                    else if (op >= 0)
                    {
                        runLength = 1 + (int)op;
                        for (int i = 0; i < runLength; i++)
                        {
                            if (bytesRead >= bytesNeeded) { break; }
                            bytes[bytesRead++] = (byte)stream.ReadByte();
                        }
                    }
                    else
                    {
                        break;
                    }
                }
            }
        }
    }
}
