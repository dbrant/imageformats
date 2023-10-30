using System;
using SixLabors.ImageSharp;
using System.IO;
using System.Text;

/*

Decoder for IFF DEEP (TV Paint) images.

Copyright 2022- Dmitry Brant
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
        /// Reads a DEEP image from a file.
        /// </summary>
        /// <param name="fileName">Name of the file to read.</param>
        /// <returns>Bitmap that contains the image that was read.</returns>
        public static Image Load(string fileName)
        {
            using var f = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Load(f);
        }

        /// <summary>
        /// Reads a DEEP image from a stream.
        /// </summary>
        /// <param name="stream">Stream from which to read the image.</param>
        /// <returns>Bitmap that contains the image that was read.</returns>
        public static Image Load(Stream stream, bool wantOpacity = false)
        {
            int imgWidth = -1;
            int imgHeight = -1;

            int numElements = 0;
            int[] elementTypes = new int[16];
            int[] elementSizes = new int[16];

            int compressionType = 0;
            int[] tvdcTable = new int[16];
            byte[] bodyChunk = null;

            var reader = new BinaryReader(stream);

            byte[] tempBytes = new byte[65536];

            stream.Read(tempBytes, 0, 4);
            if (Encoding.ASCII.GetString(tempBytes, 0, 4) != "FORM") { throw new ImageDecodeException("This is not a valid DEEP file."); }

            uint chunkSize = Util.BigEndian(reader.ReadUInt32());

            stream.Read(tempBytes, 0, 4);
            string fileType = Encoding.ASCII.GetString(tempBytes, 0, 4);
            if (fileType != "DEEP" && fileType != "TVPP") { throw new ImageDecodeException("This is not a valid DEEP file."); }

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
                throw new ImageDecodeException("Invalid format of DEEP file.");
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
                                bmpData[4 * (y * imgWidth + x) + 3] = wantOpacity ? bodyChunk[ptr] : (byte)0xFF; ptr++;
                            }
                        }
                    }
                }
                else if (compressionType == 1)
                {
                    var uncompressed = new byte[imgWidth * imgHeight * numElements];
                    var curElement = new byte[numElements];
                    int uptr = 0;
                    int bptr = 0;
                    
                    int curCount = 0;
                    bool curLiteral = false;

                    /*
                     * This is a modified version of PackBits compression where the "code" byte controls
                     * the state of the next [numElements] worth of bytes, instead of just a single byte.
                     * Since these are RGB images, with each byte containing R, G, B, R, G, B, etc., it
                     * would not make sense to do run-length encoding on a per-byte basis, since there wouldn't
                     * be many useful runs where R/G/B values are perfectly uniform. Therefore the run-length
                     * encoding is done by triplets of bytes (in the case of RGB), or quadruplets (RGBA), etc.
                     */

                    while (uptr < uncompressed.Length && bptr < bodyChunk.Length)
                    {
                        if (curCount > 0)
                        {
                            curCount--;
                            if (curLiteral)
                            {
                                for (int e = 0; e < numElements; e++)
                                {
                                    uncompressed[uptr++] = bodyChunk[bptr++];
                                }
                            }
                            else
                            {
                                for (int e = 0; e < numElements; e++)
                                {
                                    uncompressed[uptr++] = curElement[e];
                                }
                            }
                            continue;
                        }

                        int c;
                        do
                        {
                            c = bodyChunk[bptr++];
                        } while (c == 128 && stream.Position < stream.Length);

                        if (c < 128)
                        {
                            curLiteral = true;
                            curCount = c + 1;
                        }
                        else
                        {
                            curLiteral = false;
                            curCount = 257 - c;
                            for (int e = 0; e < numElements; e++)
                            {
                                curElement[e] = bodyChunk[bptr++];
                            }
                        }
                    }

                    uptr = 0;

                    for (int y = 0; y < imgHeight; y++)
                    {
                        for (int x = 0; x < imgWidth; x++)
                        {
                            // TODO: handle non-8-bit element lengths?
                            if (numElements == 3)
                            {
                                bmpData[4 * (y * imgWidth + x) + 2] = uncompressed[uptr++];
                                bmpData[4 * (y * imgWidth + x) + 1] = uncompressed[uptr++];
                                bmpData[4 * (y * imgWidth + x)] = uncompressed[uptr++];
                                bmpData[4 * (y * imgWidth + x) + 3] = 0xFF;
                            }
                            else if (numElements == 4)
                            {
                                bmpData[4 * (y * imgWidth + x) + 2] = uncompressed[uptr++];
                                bmpData[4 * (y * imgWidth + x) + 1] = uncompressed[uptr++];
                                bmpData[4 * (y * imgWidth + x)] = uncompressed[uptr++];
                                bmpData[4 * (y * imgWidth + x) + 3] = wantOpacity ? uncompressed[uptr] : (byte)0xFF; uptr++;
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
                                    bmpData[4 * (y * imgWidth + x) + e] = wantOpacity ? uncompressed[x] : (byte)0xFF;
                                }
                            }
                        }
                    }
                }
                else
                {
                    throw new ImageDecodeException("Invalid compression type.");
                }
            }
            catch (Exception e)
            {
                // return a partial image in case of unexpected end-of-file
                Util.log("Error while processing DEEP file: " + e.Message);
            }

            return Util.LoadRgba(imgWidth, imgHeight, bmpData);
        }
    }
}
