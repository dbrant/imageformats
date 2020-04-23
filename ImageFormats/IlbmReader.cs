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
    public static class IlbmReader
    {

        /// <summary>
        /// Reads an ILBM image from a file.
        /// </summary>
        /// <param name="fileName">Name of the file to read.</param>
        /// <returns>Bitmap that contains the image that was read.</returns>
        public static Bitmap Load(string fileName)
        {
            Bitmap bmp = null;
            using (var f = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bmp = Load(f);
            }
            return bmp;
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
            int maskType = 0;
            int compressionType = 0;
            int transparentColor = 0;
            bool haveCAMG = false;
            bool haveCTBL = false;
            bool haveSHAM = false;
            bool modeShamLaced = false;
            bool modePbm = false;
            bool modeHalfBrite = false;
            bool modeHAM = false;

            Bitmap theBitmap = null;
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
                    for (int c = 0; c < palette.Length; c++)
                    {
                        palette[c] = tempBytes[c];
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
            }

            if (imgWidth == -1 || imgHeight == -1 || (numPlanes > 12 && numPlanes != 24 && numPlanes != 32))
            {
                throw new ApplicationException("Invalid format of ILBM file.");
            }

            if (maskType == 1)
            {
                throw new ApplicationException("ILBM images with mask plane not yet implemented.");
            }

            if (!haveCAMG && totalColors == 16 && numPlanes == 6)
            {
                // Even though we didn't get a CAMG chunk, this is extremely likely to be
                // a HAM image, so default to it.
                modeHAM = true;
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
                            throw new ApplicationException("ILBM PBM image with unsupported bit width: " + numPlanes);
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
                            bmpData[4 * (y * imgWidth + x)] = (byte)(imageLine[x] & 0xFF);
                            bmpData[4 * (y * imgWidth + x) + 1] = (byte)((imageLine[x] >> 8) & 0xFF);
                            bmpData[4 * (y * imgWidth + x) + 2] = (byte)((imageLine[x] >> 16) & 0xFF);
                            bmpData[4 * (y * imgWidth + x) + 3] = 0xFF;
                        }
                    }
                    else if (modeHAM)
                    {
                        int hamShift = numPlanes - 2;
                        int valMask = (1 << hamShift) - 1;
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
                            index &= valMask;

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
                                    prevR = (index << valShift) | index;
                                }
                                else if (hamVal == 1)
                                {
                                    prevB = (index << valShift) | index;
                                }
                                else if (hamVal == 3)
                                {
                                    prevG = (index << valShift) | index;
                                }
                                bmpData[4 * (y * imgWidth + x)] = (byte)prevB;
                                bmpData[4 * (y * imgWidth + x) + 1] = (byte)prevG;
                                bmpData[4 * (y * imgWidth + x) + 2] = (byte)prevR;
                                bmpData[4 * (y * imgWidth + x) + 3] = 0xFF;

                                if (modeHalfBrite)
                                {
                                    bmpData[4 * (y * imgWidth + x)] >>= 1;
                                    bmpData[4 * (y * imgWidth + x) + 1] >>= 1;
                                    bmpData[4 * (y * imgWidth + x) + 2] >>= 1;
                                }
                            }
                        }
                    }
                    else
                    {
                        byte[] pal = haveCTBL && rowPalette.Count > y ? rowPalette[y] : palette;

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
                                prevR = pal[index * 3];
                                prevG = pal[index * 3 + 1];
                                prevB = pal[index * 3 + 2];

                                bmpData[4 * (y * imgWidth + x)] = (byte)prevB;
                                bmpData[4 * (y * imgWidth + x) + 1] = (byte)prevG;
                                bmpData[4 * (y * imgWidth + x) + 2] = (byte)prevR;
                                bmpData[4 * (y * imgWidth + x) + 3] = 0xFF;

                                if (modeHalfBrite)
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
                //give a partial image in case of unexpected end-of-file

                System.Diagnostics.Debug.WriteLine("Error while processing ILBM file: " + e.Message);
            }

            theBitmap = new Bitmap((int)imgWidth, (int)imgHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            System.Drawing.Imaging.BitmapData bmpBits = theBitmap.LockBits(new Rectangle(0, 0, theBitmap.Width, theBitmap.Height), System.Drawing.Imaging.ImageLockMode.ReadWrite, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            System.Runtime.InteropServices.Marshal.Copy(bmpData, 0, bmpBits.Scan0, imgWidth * 4 * imgHeight);
            theBitmap.UnlockBits(bmpBits);
            return theBitmap;
        }

        private class BitPlaneReader
        {
            private byte[] bytes;
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
            private Stream stream;

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
