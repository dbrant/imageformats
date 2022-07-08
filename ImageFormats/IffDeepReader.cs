using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Collections.Generic;

/*

Decoder for IFF DEEP (TV Paint) images.

Copyright 2022 Dmitry Brant
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
    /// Handles reading TVPaint DEEP images.
    /// </summary>
    public static class IffDeepReader
    {

        /// <summary>
        /// Reads an DEEP image from a file.
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
        /// Reads an DEEP image from a stream.
        /// </summary>
        /// <param name="stream">Stream from which to read the image.</param>
        /// <returns>Bitmap that contains the image that was read.</returns>
        public static Bitmap Load(Stream stream)
        {
            int imgWidth = -1;
            int imgHeight = -1;

            int numElements = 0;
            int[] elementTypes = new int[16];
            int[] elementSizes = new int[16];

            int compressionType = 0;
            int[] tvdcTable = new int[16];
            byte[] bodyChunk = null;

            BinaryReader reader = new BinaryReader(stream);

            byte[] tempBytes = new byte[65536];

            stream.Read(tempBytes, 0, 4);
            if (Encoding.ASCII.GetString(tempBytes, 0, 4) != "FORM") { throw new ApplicationException("This is not a valid DEEP file."); }

            uint chunkSize = Util.BigEndian(reader.ReadUInt32());

            stream.Read(tempBytes, 0, 4);
            string fileType = Encoding.ASCII.GetString(tempBytes, 0, 4);
            if (fileType != "DEEP" && fileType != "TVPP") { throw new ApplicationException("This is not a valid DEEP file."); }

            while (stream.Position < stream.Length)
            {
                stream.Read(tempBytes, 0, 4);
                string chunkName = Encoding.ASCII.GetString(tempBytes, 0, 4);
                chunkSize = Util.BigEndian(reader.ReadUInt32());

                // if (chunkSize % 2 > 0) { chunkSize++; }

                if (chunkName == "DBOD")
                {
                    bodyChunk = new byte[chunkSize];
                    stream.Read(bodyChunk, 0, (int)chunkSize);
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

                if (chunkName == "DGBL")
                {
                    if (imgWidth <= 0) imgWidth = Util.BigEndian(BitConverter.ToUInt16(tempBytes, 0));
                    if (imgHeight <= 0) imgHeight = Util.BigEndian(BitConverter.ToUInt16(tempBytes, 2));
                    compressionType = Util.BigEndian(BitConverter.ToUInt16(tempBytes, 4));
                    // last 2 bytes are xAspect, yAspect.
                }
                else if (chunkName == "DLOC")
                {
                    imgWidth = Util.BigEndian(BitConverter.ToUInt16(tempBytes, 0));
                    imgHeight = Util.BigEndian(BitConverter.ToUInt16(tempBytes, 2));
                    // last 4 bytes are xLocation, yLocation.
                }
                else if (chunkName == "DPEL")
                {
                    numElements = (int)Util.BigEndian(BitConverter.ToUInt32(tempBytes, 0));
                    int ptr = 4;
                    for (int i = 0; i < numElements; i++)
                    {
                        elementTypes[i] = Util.BigEndian(BitConverter.ToUInt16(tempBytes, ptr)); ptr += 2;
                        elementSizes[i] = Util.BigEndian(BitConverter.ToUInt16(tempBytes, ptr)); ptr += 2;
                    }
                }
                else if (chunkName == "TVDC" && chunkSize >= 32)
                {
                    for (int i = 0; i < 16; i++)
                    {
                        tvdcTable[i] = (short)Util.BigEndian(BitConverter.ToUInt16(tempBytes, i * 2));
                    }
                }
            }

            if (imgWidth == -1 || imgHeight == -1)
            {
                throw new ApplicationException("Invalid format of DEEP file.");
            }

            byte[] bmpData = new byte[(imgWidth + 1) * 4 * imgHeight];

            try
            {
                if (compressionType == 0)
                {
                    int ptr = 0;
                    for (int y = 0; y < imgHeight; y++)
                    {
                        for (int x = 0; x < imgWidth; x++)
                        {
                            // TODO: handle non-8-bit element lengths?
                            if (numElements == 3)
                            {
                                bmpData[4 * (y * imgWidth + x) + 2] = bodyChunk[ptr++];
                                bmpData[4 * (y * imgWidth + x) + 1] = bodyChunk[ptr++];
                                bmpData[4 * (y * imgWidth + x)] = bodyChunk[ptr++];
                                bmpData[4 * (y * imgWidth + x) + 3] = 0xFF;
                            }
                            else if (numElements == 4)
                            {
                                bmpData[4 * (y * imgWidth + x) + 2] = bodyChunk[ptr++];
                                bmpData[4 * (y * imgWidth + x) + 1] = bodyChunk[ptr++];
                                bmpData[4 * (y * imgWidth + x)] = bodyChunk[ptr++];
                                bmpData[4 * (y * imgWidth + x) + 3] = bodyChunk[ptr++];
                            }
                        }
                    }
                }
                else if (compressionType == 5)
                {
                    int scanLineSize = imgWidth;
                    byte[] uncompressed = new byte[scanLineSize * 2];
                    int pos = 0;
                    
                    for (int y = 0; y < imgHeight; y++)
                    {
                        for (int e = 0; e < numElements; e++)
                        {
                            int d = 0;
                            int v = 0;

                            for (int i = 0; i < scanLineSize; i++)
                            {
                                d = bodyChunk[pos >> 1];
                                if ((pos++ & 1) != 0) d &= 0xF;
                                else d >>= 4;
                                v += tvdcTable[d];
                                uncompressed[i] = (byte)v;
                                if (tvdcTable[d] == 0)
                                {
                                    d = bodyChunk[pos >> 1];
                                    if ((pos++ & 1) != 0) d &= 0xF;
                                    else d >>= 4;
                                    while (d-- != 0) uncompressed[++i] = (byte)v;
                                }
                            }

                            if (pos % 2 != 0) pos++;

                            // TODO: handle non-8-bit element lengths?
                            for (int x = 0; x < imgWidth; x++)
                            {
                                if (e < 3)
                                {
                                    bmpData[4 * (y * imgWidth + x) + (2 - e)] = uncompressed[x];
                                }
                                else
                                {
                                    bmpData[4 * (y * imgWidth + x) + e] = uncompressed[x];
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                // return a partial image in case of unexpected end-of-file
                Util.log("Error while processing DEEP file: " + e.Message);
            }

            var bmp = new Bitmap(imgWidth, imgHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            System.Drawing.Imaging.BitmapData bmpBits = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), System.Drawing.Imaging.ImageLockMode.ReadWrite, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            System.Runtime.InteropServices.Marshal.Copy(bmpData, 0, bmpBits.Scan0, imgWidth * 4 * imgHeight);
            bmp.UnlockBits(bmpBits);
            return bmp;
        }
    }
}
